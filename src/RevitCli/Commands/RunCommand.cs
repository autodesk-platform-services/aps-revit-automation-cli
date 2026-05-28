using System.ComponentModel;
using RevitCli.Models;
using RevitCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RevitCli.Commands;

public sealed class RunCommand : AsyncCommand<RunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<yaml-file>")]
        [Description("Path to the job YAML configuration file")]
        public string YamlFile { get; init; } = string.Empty;
    }

    private readonly YamlConfigService _yamlConfigService;
    private readonly JobRunner _jobRunner;

    public RunCommand(YamlConfigService yamlConfigService, JobRunner jobRunner)
    {
        _yamlConfigService = yamlConfigService;
        _jobRunner = jobRunner;
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

        return await _jobRunner.RunAsync(config, AnsiConsole.Console);
    }
}
