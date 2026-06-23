using System.ComponentModel;
using RevitCli.Models;
using RevitCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RevitCli.Commands;

public sealed class ValidateCommand : AsyncCommand<ValidateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<yaml-file>")]
        [Description("Path to the job YAML configuration file")]
        public string YamlFile { get; init; } = string.Empty;
    }

    private readonly YamlConfigService _yamlConfigService;

    public ValidateCommand(YamlConfigService yamlConfigService)
    {
        _yamlConfigService = yamlConfigService;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
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

        if (config.App?.Path != null && !config.App.Path.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine("  [red]•[/] app.path must end with .bundle");
            return 1;
        }

        if (!Directory.Exists(config.App?.Path))
        {
            AnsiConsole.MarkupLine($"  [red]•[/] app.path directory does not exist: {Markup.Escape(config.App?.Path ?? "(not specified)")}");
            return 1;
        }

        var outputPath = config.Outputs?.Result?.Path;
        if (!string.IsNullOrEmpty(outputPath))
        {
            if (outputPath.Contains("{modelName}", StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine("[blue]i[/] outputs.result.path uses {modelName} placeholder; directories will be created per model at runtime.");
            }
            else
            {
                var parentDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    AnsiConsole.MarkupLine($"  [yellow]⚠[/] outputs.result.path parent directory does not exist yet: {Markup.Escape(parentDir)}");
            }
        }

        AnsiConsole.Write(new Panel("[green]YAML is valid[/]").BorderColor(Color.Green));
        return 0;
    }
}
