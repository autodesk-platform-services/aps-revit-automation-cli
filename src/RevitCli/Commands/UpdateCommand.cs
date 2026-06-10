using System.ComponentModel;
using RevitCli.Infrastructure;
using RevitCli.Models;
using RevitCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RevitCli.Commands;

public sealed class UpdateCommand : AsyncCommand<UpdateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<yaml-file>")]
        [Description("Path to the job YAML configuration file")]
        public string YamlFile { get; init; } = string.Empty;
    }

    private readonly YamlConfigService _yamlConfigService;
    private readonly AuthService _authService;
    private readonly TokenStore _tokenStore;
    private readonly RevitEngineResolver _engineResolver;
    private readonly DesignAutomationService _designAutomationService;
    private readonly AppBundlePackager _appBundlePackager;

    public UpdateCommand(
        YamlConfigService yamlConfigService,
        AuthService authService,
        TokenStore tokenStore,
        RevitEngineResolver engineResolver,
        DesignAutomationService designAutomationService,
        AppBundlePackager appBundlePackager)
    {
        _yamlConfigService = yamlConfigService;
        _authService = authService;
        _tokenStore = tokenStore;
        _engineResolver = engineResolver;
        _designAutomationService = designAutomationService;
        _appBundlePackager = appBundlePackager;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        string? zipPath = null;

        try
        {
            JobConfig config;
            try
            {
                config = await _yamlConfigService.LoadAsync(settings.YamlFile);
            }
            catch (ConfigValidationException ex)
            {
                AnsiConsole.MarkupLine("[red]Configuration validation failed:[/]");
                foreach (var error in ex.Errors)
                    AnsiConsole.MarkupLine($"  [red]•[/] {Markup.Escape(error)}");
                return 1;
            }

            var engineId = _engineResolver.Resolve(config.Revit.Version!);
            AnsiConsole.MarkupLine($"[blue]Revit engine:[/] {engineId}");

            var aliasName = string.IsNullOrWhiteSpace(config.Environment) ? "prod" : config.Environment;
            AnsiConsole.MarkupLine($"[blue]Environment:[/] {aliasName}");

            var (clientId, clientSecret) = await _tokenStore.GetCredentialsAsync();

            var twoLeggedToken = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Authenticating (2-legged)...", async _ =>
                    await _authService.GetTwoLeggedTokenAsync(clientId, clientSecret));
            AnsiConsole.MarkupLine("[green]✓[/] 2-legged token acquired");

            var nickname = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Retrieving nickname...", async _ =>
                    await _designAutomationService.GetNicknameAsync(twoLeggedToken));
            AnsiConsole.MarkupLine($"[blue]Nickname:[/] {nickname}");

            zipPath = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Packaging app...", async _ =>
                    await _appBundlePackager.PackageAsync(config.App.Path!));
            AnsiConsole.MarkupLine("[green]✓[/] App packaged");

            var (bundleVersion, _) = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Updating AppBundle...", async _ =>
                    await _designAutomationService.ForceUpdateAppBundleAsync(
                        config.App.Name!, engineId, zipPath, nickname, aliasName, twoLeggedToken));
            AnsiConsole.MarkupLine($"[green]✓[/] AppBundle updated (v{bundleVersion})");

            var activityId = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Updating Activity...", async _ =>
                    await _designAutomationService.EnsureActivityAsync(
                        config.App.Name!, engineId, nickname, aliasName, twoLeggedToken));
            AnsiConsole.MarkupLine($"[green]✓[/] Activity updated: {activityId}");

            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel($"[green]Update complete.[/] Run [bold]revit run {Markup.Escape(settings.YamlFile)}[/] to execute the updated bundle.")
                .BorderColor(Color.Green));

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        finally
        {
            if (zipPath != null && File.Exists(zipPath))
                File.Delete(zipPath);
        }
    }
}
