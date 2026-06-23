using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RevitCli.Infrastructure;
using RevitCli.Models;
using Spectre.Console;

namespace RevitCli.Services;

public class JobRunner
{
    private static readonly JsonSerializerOptions CloudModelJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RevitEngineResolver _engineResolver;
    private readonly AuthService _authService;
    private readonly TokenStore _tokenStore;
    private readonly DesignAutomationService _designAutomationService;
    private readonly DataManagementService _dataManagementService;
    private readonly AppBundlePackager _appBundlePackager;
    private readonly OssService _ossService;

    public JobRunner(
        RevitEngineResolver engineResolver,
        AuthService authService,
        TokenStore tokenStore,
        DesignAutomationService designAutomationService,
        DataManagementService dataManagementService,
        AppBundlePackager appBundlePackager,
        OssService ossService)
    {
        _engineResolver = engineResolver;
        _authService = authService;
        _tokenStore = tokenStore;
        _designAutomationService = designAutomationService;
        _dataManagementService = dataManagementService;
        _appBundlePackager = appBundlePackager;
        _ossService = ossService;
    }

    private record MultiModelResult(
        string DisplayName, string WorkItemId, string Status,
        string? OutputPath, string? LogPath, TimeSpan? ExecutionTime);

    public async Task<int> RunAsync(JobConfig config, IAnsiConsole console)
    {
        var isSingleModel = !string.IsNullOrWhiteSpace(config.Inputs.Model.ModelName);
        var isMultiNamed = config.Inputs.Model.ModelNames is { Count: > 0 };
        var isMultiModel = !isSingleModel;

        if (isMultiModel)
            return await RunMultiModelAsync(config, console, isMultiNamed);

        return await RunSingleModelAsync(config, console);
    }

    private async Task<int> RunSingleModelAsync(JobConfig config, IAnsiConsole console)
    {
        var stopwatch = Stopwatch.StartNew();
        string? zipPath = null;
        string? bucketKey = null;
        string? twoLeggedToken = null;

        try
        {
            var engineId = _engineResolver.Resolve(config.Revit.Version!);
            console.MarkupLine($"[blue]Revit engine:[/] {engineId}");

            var aliasName = string.IsNullOrWhiteSpace(config.Environment) ? "prod" : config.Environment;
            console.MarkupLine($"[blue]Environment:[/] {aliasName}");

            if (_engineResolver.IsDeprecationWarning(config.Revit.Version!))
            {
                console.Write(new Panel("[yellow]Revit 2022 is deprecated and may be removed in a future update.[/]")
                    .Header("[yellow]Deprecation Warning[/]")
                    .BorderColor(Color.Yellow));
            }

            var (clientId, clientSecret) = await _tokenStore.GetCredentialsAsync();

            twoLeggedToken = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Authenticating (2-legged)...", async _ =>
                    await _authService.GetTwoLeggedTokenAsync(clientId, clientSecret));
            console.MarkupLine("[green]✓[/] 2-legged token acquired");

            var threeLeggedToken = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Authenticating (3-legged)...", async _ =>
                    await _authService.EnsureThreeLeggedTokenAsync(clientId, clientSecret));
            console.MarkupLine("[green]✓[/] 3-legged token acquired");

            var nickname = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Retrieving nickname...", async _ =>
                    await _designAutomationService.GetNicknameAsync(twoLeggedToken));
            console.MarkupLine($"[blue]Nickname:[/] {nickname}");

            var cloudModelIds = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Resolving cloud model...", async _ =>
                    await _dataManagementService.ResolveAsync(
                        config.Inputs.Model.FolderUrl!, config.Inputs.Model.ModelName!, threeLeggedToken));
            console.MarkupLine($"[green]✓[/] Model resolved: Region={cloudModelIds.Region}, ProjectGuid={cloudModelIds.ProjectGuid}");

            var appBundleAliasExists = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Checking AppBundle...", async _ =>
                    await _designAutomationService.CheckAppBundleAliasAsync(
                        config.App.Name!, nickname, aliasName, twoLeggedToken));

            if (appBundleAliasExists)
            {
                console.MarkupLine("[green]✓[/] AppBundle already exists (alias found, skipping upload)");
            }
            else
            {
                zipPath = await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Packaging app...", async _ =>
                        await _appBundlePackager.PackageAsync(config.App.Path!));
                console.MarkupLine("[green]✓[/] App packaged");

                var (bundleVersion, _) = await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Uploading AppBundle...", async _ =>
                        await _designAutomationService.CreateAppBundleIfMissingAsync(
                            config.App.Name!, engineId, zipPath, nickname, aliasName, twoLeggedToken));
                console.MarkupLine($"[green]✓[/] AppBundle uploaded (v{bundleVersion})");
            }

            var activityId = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Configuring Activity...", async _ =>
                    await _designAutomationService.CreateActivityIfMissingAsync(
                        config.App.Name!, engineId, nickname, aliasName, twoLeggedToken));
            console.MarkupLine($"[green]✓[/] Activity configured: {activityId}");

            var hasOutputs = !string.IsNullOrWhiteSpace(config.Outputs.Result.Path);
            var outputObjectName = "result";
            string? outputUrn = null;

            if (hasOutputs)
            {
                bucketKey = SanitizeBucketKey($"{config.App.Name}-{DateTime.UtcNow:yyyyMMddHHmmss}");
                await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Creating output bucket...", async _ =>
                        await _ossService.CreateBucketAsync(bucketKey, twoLeggedToken));
                console.MarkupLine($"[green]✓[/] Output bucket created: {bucketKey}");

                outputUrn = _ossService.GetObjectUrn(bucketKey, outputObjectName);
            }
            else
            {
                console.MarkupLine("[blue]No outputs configured; skipping bucket creation.[/]");
            }

            var cloudModelJson = JsonSerializer.Serialize(new
            {
                Region = cloudModelIds.Region,
                ProjectGuid = cloudModelIds.ProjectGuid,
                ModelGuid = cloudModelIds.ModelGuid,
                ToolName = config.Inputs.Tool?.Name,
                Save = config.Inputs.Model.Save ?? true,
                OpenOption = config.Inputs.Model.OpenOption ?? "OpenAllWorksets"
            }, CloudModelJsonOptions);

            var toolInputsJson = "{}";
            if (!string.IsNullOrWhiteSpace(config.Inputs.Tool?.Inputs))
            {
                var rawToolInputsJson = await File.ReadAllTextAsync(config.Inputs.Tool.Inputs);
                try
                {
                    using var parsedToolInputs = JsonDocument.Parse(rawToolInputsJson);
                    toolInputsJson = JsonSerializer.Serialize(parsedToolInputs.RootElement);
                }
                catch (JsonException ex)
                {
                    throw new ConfigValidationException([$"'inputs.tool.inputs' must contain valid JSON. {ex.Message}"]);
                }
            }

            var workItemId = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Submitting job...", async _ =>
                    await _designAutomationService.SubmitWorkItemAsync(
                        activityId, cloudModelJson, toolInputsJson, outputUrn, twoLeggedToken, threeLeggedToken));
            console.MarkupLine($"[green]✓[/] WorkItem submitted: {workItemId}");

            var workItemResult = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Executing job...", async ctx =>
                {
                    var pollStart = Stopwatch.StartNew();
                    var result = await _designAutomationService.PollWorkItemAsync(
                        workItemId, twoLeggedToken, CancellationToken.None);
                    return result;
                });

            var logPath = await DownloadWorkItemReportAsync(workItemResult, console);

            if (workItemResult.Status == "success")
            {
                if (hasOutputs && bucketKey is not null)
                {
                    await console.Status()
                        .Spinner(Spinner.Known.Dots)
                        .StartAsync("Downloading output...", async _ =>
                            await _ossService.DownloadObjectAsync(
                                bucketKey, outputObjectName, config.Outputs.Result.Path!, twoLeggedToken));
                    console.MarkupLine($"[green]✓[/] Output downloaded to: {config.Outputs.Result.Path}");

                    await CleanupBucketAsync(bucketKey, twoLeggedToken);
                    bucketKey = null;
                }

                stopwatch.Stop();
                PrintSuccessSummary(console, config, workItemResult, stopwatch.Elapsed, logPath);
                return 0;
            }
            else
            {
                stopwatch.Stop();
                PrintFailureSummary(console, workItemResult, bucketKey, logPath);
                if (logPath is not null)
                    TryOpenFile(logPath, console);
                return 1;
            }
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            if (zipPath is not null && File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch { }
            }

            if (bucketKey is not null && twoLeggedToken is not null)
            {
                await CleanupBucketAsync(bucketKey, twoLeggedToken);
            }
        }
    }

    private async Task<int> RunMultiModelAsync(JobConfig config, IAnsiConsole console, bool isMultiNamed)
    {
        var stopwatch = Stopwatch.StartNew();
        string? zipPath = null;
        string? bucketKey = null;
        string? twoLeggedToken = null;

        try
        {
            var engineId = _engineResolver.Resolve(config.Revit.Version!);
            console.MarkupLine($"[blue]Revit engine:[/] {engineId}");

            var aliasName = string.IsNullOrWhiteSpace(config.Environment) ? "prod" : config.Environment;
            console.MarkupLine($"[blue]Environment:[/] {aliasName}");

            if (_engineResolver.IsDeprecationWarning(config.Revit.Version!))
            {
                console.Write(new Panel("[yellow]Revit 2022 is deprecated and may be removed in a future update.[/]")
                    .Header("[yellow]Deprecation Warning[/]")
                    .BorderColor(Color.Yellow));
            }

            var (clientId, clientSecret) = await _tokenStore.GetCredentialsAsync();

            twoLeggedToken = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Authenticating (2-legged)...", async _ =>
                    await _authService.GetTwoLeggedTokenAsync(clientId, clientSecret));
            console.MarkupLine("[green]✓[/] 2-legged token acquired");

            var threeLeggedToken = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Authenticating (3-legged)...", async _ =>
                    await _authService.EnsureThreeLeggedTokenAsync(clientId, clientSecret));
            console.MarkupLine("[green]✓[/] 3-legged token acquired");

            var nickname = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Retrieving nickname...", async _ =>
                    await _designAutomationService.GetNicknameAsync(twoLeggedToken));
            console.MarkupLine($"[blue]Nickname:[/] {nickname}");

            List<(string DisplayName, CloudModelIds Ids)> resolvedModels;
            if (isMultiNamed)
            {
                resolvedModels = await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Resolving named models...", async _ =>
                        await _dataManagementService.ResolveMultipleByNameAsync(
                            config.Inputs.Model.FolderUrl!, config.Inputs.Model.ModelNames!, threeLeggedToken));
            }
            else
            {
                resolvedModels = await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Resolving all models in folder...", async _ =>
                        await _dataManagementService.ResolveAllAsync(
                            config.Inputs.Model.FolderUrl!, threeLeggedToken));
            }

            if (resolvedModels.Count == 0)
            {
                console.MarkupLine("[yellow]No RVT models found in folder. Nothing to do.[/]");
                return 0;
            }

            console.MarkupLine($"[green]✓[/] Resolved {resolvedModels.Count} model(s):");
            foreach (var (displayName, ids) in resolvedModels)
                console.MarkupLine($"    [blue]•[/] {Markup.Escape(displayName)} (Region={ids.Region})");

            var appBundleAliasExists = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Checking AppBundle...", async _ =>
                    await _designAutomationService.CheckAppBundleAliasAsync(
                        config.App.Name!, nickname, aliasName, twoLeggedToken));

            if (appBundleAliasExists)
            {
                console.MarkupLine("[green]✓[/] AppBundle already exists (alias found, skipping upload)");
            }
            else
            {
                zipPath = await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Packaging app...", async _ =>
                        await _appBundlePackager.PackageAsync(config.App.Path!));
                console.MarkupLine("[green]✓[/] App packaged");

                var (bundleVersion, _) = await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Uploading AppBundle...", async _ =>
                        await _designAutomationService.CreateAppBundleIfMissingAsync(
                            config.App.Name!, engineId, zipPath, nickname, aliasName, twoLeggedToken));
                console.MarkupLine($"[green]✓[/] AppBundle uploaded (v{bundleVersion})");
            }

            var activityId = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Configuring Activity...", async _ =>
                    await _designAutomationService.CreateActivityIfMissingAsync(
                        config.App.Name!, engineId, nickname, aliasName, twoLeggedToken));
            console.MarkupLine($"[green]✓[/] Activity configured: {activityId}");

            var hasOutputs = !string.IsNullOrWhiteSpace(config.Outputs.Result.Path);

            if (hasOutputs)
            {
                bucketKey = SanitizeBucketKey($"{config.App.Name}-{DateTime.UtcNow:yyyyMMddHHmmss}");
                await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Creating output bucket...", async _ =>
                        await _ossService.CreateBucketAsync(bucketKey, twoLeggedToken));
                console.MarkupLine($"[green]✓[/] Output bucket created: {bucketKey}");
            }
            else
            {
                console.MarkupLine("[blue]No outputs configured; skipping bucket creation.[/]");
            }

            var toolInputsJson = "{}";
            if (!string.IsNullOrWhiteSpace(config.Inputs.Tool?.Inputs))
            {
                var rawToolInputsJson = await File.ReadAllTextAsync(config.Inputs.Tool.Inputs);
                try
                {
                    using var parsedToolInputs = JsonDocument.Parse(rawToolInputsJson);
                    toolInputsJson = JsonSerializer.Serialize(parsedToolInputs.RootElement);
                }
                catch (JsonException ex)
                {
                    throw new ConfigValidationException([$"'inputs.tool.inputs' must contain valid JSON. {ex.Message}"]);
                }
            }

            var submitted = new List<(string DisplayName, string WorkItemId, string ObjectName, string? LocalPath)>();

            foreach (var (displayName, ids) in resolvedModels)
            {
                var cloudModelJson = JsonSerializer.Serialize(new
                {
                    Region = ids.Region,
                    ProjectGuid = ids.ProjectGuid,
                    ModelGuid = ids.ModelGuid,
                    ToolName = config.Inputs.Tool?.Name,
                    Save = config.Inputs.Model.Save ?? true,
                    OpenOption = config.Inputs.Model.OpenOption ?? "OpenAllWorksets"
                }, CloudModelJsonOptions);

                var objectName = SanitizeObjectName($"result-{displayName}");
                string? outputUrn = null;
                if (hasOutputs && bucketKey is not null)
                    outputUrn = _ossService.GetObjectUrn(bucketKey, objectName);

                var localPath = hasOutputs
                    ? ResolveOutputPath(config.Outputs.Result.Path!, displayName, resolvedModels.Count > 1)
                    : null;

                var workItemId = await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Submitting job for {Markup.Escape(displayName)}...", async _ =>
                        await _designAutomationService.SubmitWorkItemAsync(
                            activityId, cloudModelJson, toolInputsJson, outputUrn, twoLeggedToken, threeLeggedToken));
                console.MarkupLine($"[green]✓[/] WorkItem submitted for {Markup.Escape(displayName)}: {workItemId}");

                submitted.Add((displayName, workItemId, objectName, localPath));
            }

            console.MarkupLine($"\n[blue]Polling {submitted.Count} workitem(s) concurrently...[/]");

            var pollTasks = submitted.Select(m =>
                _designAutomationService.PollWorkItemAsync(m.WorkItemId, twoLeggedToken, CancellationToken.None))
                .ToArray();
            var pollResults = await Task.WhenAll(pollTasks);

            var results = new List<MultiModelResult>();

            for (var i = 0; i < submitted.Count; i++)
            {
                var (displayName, workItemId, objectName, localPath) = submitted[i];
                var workItemResult = pollResults[i];

                var logPath = await DownloadWorkItemReportAsync(workItemResult, console);

                TimeSpan? executionTime = null;
                if (workItemResult.Stats?.TimeInstructionsStarted is not null &&
                    workItemResult.Stats?.TimeInstructionsEnded is not null &&
                    DateTime.TryParse(workItemResult.Stats.TimeInstructionsStarted, out var started) &&
                    DateTime.TryParse(workItemResult.Stats.TimeInstructionsEnded, out var ended))
                {
                    executionTime = ended - started;
                }

                if (workItemResult.Status == "success" && hasOutputs && bucketKey is not null && localPath is not null)
                {
                    try
                    {
                        var parentDir = Path.GetDirectoryName(localPath);
                        if (!string.IsNullOrEmpty(parentDir))
                            Directory.CreateDirectory(parentDir);

                        await _ossService.DownloadObjectAsync(bucketKey, objectName, localPath, twoLeggedToken);
                        console.MarkupLine($"[green]✓[/] Output for {Markup.Escape(displayName)} downloaded to: {localPath}");
                    }
                    catch (Exception ex)
                    {
                        console.MarkupLine($"[yellow]Warning:[/] failed to download output for {Markup.Escape(displayName)}: {Markup.Escape(ex.Message)}");
                        localPath = null;
                    }
                }

                results.Add(new MultiModelResult(
                    displayName, workItemId, workItemResult.Status ?? "unknown",
                    workItemResult.Status == "success" ? localPath : null,
                    logPath, executionTime));
            }

            stopwatch.Stop();
            PrintMultiModelSummary(console, results, stopwatch.Elapsed);

            var allSucceeded = results.All(r => r.Status == "success");
            return allSucceeded ? 0 : 1;
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            if (zipPath is not null && File.Exists(zipPath))
            {
                try { File.Delete(zipPath); } catch { }
            }

            if (bucketKey is not null && twoLeggedToken is not null)
            {
                await CleanupBucketAsync(bucketKey, twoLeggedToken);
            }
        }
    }

    internal static string? ResolveOutputPath(string configPath, string displayName, bool isMultiModel)
    {
        var normalized = NormalizeModelName(displayName);

        if (configPath.Contains("{modelName}", StringComparison.OrdinalIgnoreCase))
            return configPath.Replace("{modelName}", normalized, StringComparison.OrdinalIgnoreCase);

        if (isMultiModel)
        {
            var dir = Path.GetDirectoryName(configPath) ?? ".";
            var fileName = Path.GetFileName(configPath);
            return Path.Combine(dir, normalized, fileName);
        }

        return configPath;
    }

    internal static string SanitizeObjectName(string name)
    {
        var sanitized = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9-]", "-");
        if (sanitized.Length > 64)
            sanitized = sanitized[..64];
        return sanitized;
    }

    private static string NormalizeModelName(string name)
    {
        return name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }

    private static void PrintMultiModelSummary(
        IAnsiConsole console, IReadOnlyList<MultiModelResult> results, TimeSpan totalElapsed)
    {
        console.WriteLine();
        var allSucceeded = results.All(r => r.Status == "success");
        var title = allSucceeded
            ? "[green]All Jobs Completed Successfully[/]"
            : "[yellow]Jobs Completed (Partial Failure)[/]";

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title(title);

        table.AddColumn("Model");
        table.AddColumn("Status");
        table.AddColumn("WorkItem ID");
        table.AddColumn("Output");
        table.AddColumn("Execution Time");

        foreach (var r in results)
        {
            var statusMarkup = r.Status == "success"
                ? "[green]success[/]"
                : $"[red]{Markup.Escape(r.Status)}[/]";

            var execTime = r.ExecutionTime.HasValue
                ? $"{r.ExecutionTime.Value.TotalSeconds:F1}s"
                : "N/A";

            table.AddRow(
                Markup.Escape(r.DisplayName),
                statusMarkup,
                Markup.Escape(r.WorkItemId),
                Markup.Escape(r.OutputPath ?? "N/A"),
                execTime);
        }

        table.AddEmptyRow();
        table.AddRow("", "", "", "[blue]Total Time[/]", $"{totalElapsed.TotalSeconds:F1}s");

        console.Write(table);

        var succeeded = results.Count(r => r.Status == "success");
        var failed = results.Count - succeeded;
        console.MarkupLine($"\n[blue]Summary:[/] {succeeded}/{results.Count} succeeded, {failed} failed");
    }

    private static async Task<string?> DownloadWorkItemReportAsync(
        Models.Api.WorkItemResponse workItem, IAnsiConsole console)
    {
        if (string.IsNullOrWhiteSpace(workItem.ReportUrl))
            return null;

        var logPath = Path.Combine(Directory.GetCurrentDirectory(), $"{workItem.Id}.log");

        try
        {
            using var client = new HttpClient();
            using var stream = await client.GetStreamAsync(workItem.ReportUrl);
            using var file = new FileStream(logPath, FileMode.Create, FileAccess.Write);
            await stream.CopyToAsync(file);

            console.MarkupLine($"[green]✓[/] Log saved to: {logPath}");
            return logPath;
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[yellow]Warning:[/] failed to download log: {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    private static void TryOpenFile(string path, IAnsiConsole console)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[yellow]Warning:[/] could not auto-open log: {Markup.Escape(ex.Message)}");
        }
    }

    private async Task CleanupBucketAsync(string bucketKey, string twoLeggedToken)
    {
        try
        {
            await _ossService.DeleteBucketAsync(bucketKey, twoLeggedToken);
        }
        catch { }
    }

    private static void PrintSuccessSummary(
        IAnsiConsole console, JobConfig config, Models.Api.WorkItemResponse workItem, TimeSpan elapsed, string? logPath)
    {
        console.WriteLine();
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[green]Job Completed Successfully[/]");

        table.AddColumn("Property");
        table.AddColumn("Value");

        table.AddRow("Status", "[green]success[/]");
        table.AddRow("WorkItem ID", workItem.Id);
        table.AddRow("Output", config.Outputs.Result.Path ?? "N/A");
        table.AddRow("Total Time", $"{elapsed.TotalSeconds:F1}s");

        if (workItem.Stats is not null)
        {
            if (workItem.Stats.TimeQueued is not null && workItem.Stats.TimeInstructionsStarted is not null)
            {
                if (DateTime.TryParse(workItem.Stats.TimeQueued, out var queued) &&
                    DateTime.TryParse(workItem.Stats.TimeInstructionsStarted, out var started))
                {
                    table.AddRow("Queue Time", $"{(started - queued).TotalSeconds:F1}s");
                }
            }

            if (workItem.Stats.TimeInstructionsStarted is not null && workItem.Stats.TimeInstructionsEnded is not null)
            {
                if (DateTime.TryParse(workItem.Stats.TimeInstructionsStarted, out var started) &&
                    DateTime.TryParse(workItem.Stats.TimeInstructionsEnded, out var ended))
                {
                    table.AddRow("Execution Time", $"{(ended - started).TotalSeconds:F1}s");
                }
            }
        }

        if (logPath is not null)
            table.AddRow("Log", logPath);

        console.Write(table);
    }

    private static void PrintFailureSummary(
        IAnsiConsole console, Models.Api.WorkItemResponse workItem, string? bucketKey, string? logPath)
    {
        console.WriteLine();
        console.MarkupLine("[red]Job Failed[/]");
        console.MarkupLine($"[red]Status:[/] {Markup.Escape(workItem.Status ?? "")}");
        console.MarkupLine($"[red]WorkItem ID:[/] {Markup.Escape(workItem.Id ?? "")}");
        if (logPath is not null)
            console.WriteLine($"Log: {logPath}");
        if (bucketKey is not null)
            console.WriteLine($"Bucket Key: {bucketKey}");
    }

    private static string SanitizeBucketKey(string raw)
    {
        var sanitized = Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9-]", "-");
        if (sanitized.Length > 128)
            sanitized = sanitized[..128];
        return sanitized;
    }
}
