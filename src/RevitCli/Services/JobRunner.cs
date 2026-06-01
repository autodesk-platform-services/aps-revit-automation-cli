using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevitCli.Models;
using Spectre.Console;

namespace RevitCli.Services;

public class JobRunner
{
    private readonly RevitEngineResolver _engineResolver;
    private readonly AuthService _authService;
    private readonly DesignAutomationService _designAutomationService;
    private readonly DataManagementService _dataManagementService;
    private readonly AppBundlePackager _appBundlePackager;
    private readonly OssService _ossService;

    public JobRunner(
        RevitEngineResolver engineResolver,
        AuthService authService,
        DesignAutomationService designAutomationService,
        DataManagementService dataManagementService,
        AppBundlePackager appBundlePackager,
        OssService ossService)
    {
        _engineResolver = engineResolver;
        _authService = authService;
        _designAutomationService = designAutomationService;
        _dataManagementService = dataManagementService;
        _appBundlePackager = appBundlePackager;
        _ossService = ossService;
    }

    public async Task<int> RunAsync(JobConfig config, IAnsiConsole console)
    {
        var stopwatch = Stopwatch.StartNew();
        string? zipPath = null;
        string? bucketKey = null;
        string? twoLeggedToken = null;

        try
        {
            var engineId = _engineResolver.Resolve(config.Revit.Version!);
            console.MarkupLine($"[blue]Revit engine:[/] {engineId}");

            if (_engineResolver.IsDeprecationWarning(config.Revit.Version!))
            {
                console.Write(new Panel("[yellow]Revit 2022 is deprecated and may be removed in a future update.[/]")
                    .Header("[yellow]Deprecation Warning[/]")
                    .BorderColor(Color.Yellow));
            }

            twoLeggedToken = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Authenticating (2-legged)...", async _ =>
                    await _authService.GetTwoLeggedTokenAsync(
                        config.Authentication.ClientId!, config.Authentication.ClientSecret!));
            console.MarkupLine("[green]✓[/] 2-legged token acquired");

            var threeLeggedToken = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Authenticating (3-legged)...", async _ =>
                    await _authService.EnsureThreeLeggedTokenAsync(
                        config.Authentication.ClientId!, config.Authentication.ClientSecret!));
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

            (zipPath, var zipHash) = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Packaging app...", async _ =>
                    await _appBundlePackager.PackageAsync(config.App.Path!));
            console.MarkupLine($"[green]✓[/] App packaged (SHA-256: {zipHash[..12]}...)");

            var (bundleVersion, wasSkipped) = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Uploading AppBundle...", async _ =>
                    await _designAutomationService.EnsureAppBundleAsync(
                        config.App.Name!, engineId, zipPath, zipHash, nickname, twoLeggedToken));

            if (wasSkipped)
                console.MarkupLine($"[green]✓[/] AppBundle up-to-date (v{bundleVersion}), skipping upload");
            else
                console.MarkupLine($"[green]✓[/] AppBundle uploaded (v{bundleVersion})");

            var activityId = await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Configuring Activity...", async _ =>
                    await _designAutomationService.EnsureActivityAsync(
                        config.App.Name!, engineId, nickname, twoLeggedToken));
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
                ModelGuid = cloudModelIds.ModelGuid
            });

            var toolInputsJson = JsonSerializer.Serialize(
                config.Inputs.Params ?? new Dictionary<string, object>());

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
                PrintSuccessSummary(console, config, workItemResult, stopwatch.Elapsed);
                return 0;
            }
            else
            {
                stopwatch.Stop();
                PrintFailurePanel(console, workItemResult, bucketKey);
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

    private async Task CleanupBucketAsync(string bucketKey, string twoLeggedToken)
    {
        try
        {
            await _ossService.DeleteBucketAsync(bucketKey, twoLeggedToken);
        }
        catch { }
    }

    private static void PrintSuccessSummary(
        IAnsiConsole console, JobConfig config, Models.Api.WorkItemResponse workItem, TimeSpan elapsed)
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

        if (workItem.ReportUrl is not null)
            table.AddRow("Report URL", workItem.ReportUrl);

        console.Write(table);
    }

    private static void PrintFailurePanel(
        IAnsiConsole console, Models.Api.WorkItemResponse workItem, string? bucketKey)
    {
        console.WriteLine();
        var panel = new Panel(
            $"[red]Status:[/] {workItem.Status}\n" +
            $"[red]WorkItem ID:[/] {workItem.Id}\n" +
            (workItem.ReportUrl is not null ? $"[yellow]Report URL:[/] {workItem.ReportUrl}\n" : "") +
            (bucketKey is not null ? $"[yellow]Bucket Key:[/] {bucketKey}" : ""))
            .Header("[red]Job Failed[/]")
            .BorderColor(Color.Red);

        console.Write(panel);
    }

    private static string SanitizeBucketKey(string raw)
    {
        var sanitized = Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9-]", "-");
        if (sanitized.Length > 128)
            sanitized = sanitized[..128];
        return sanitized;
    }
}
