using System.ComponentModel;
using RevitCli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RevitCli.Commands;

public sealed class MaxModelsCommand : AsyncCommand<MaxModelsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[count]")]
        [Description("The maximum number of models per automation run")]
        public int? Count { get; init; }
    }

    private readonly CliConfigStore _configStore;

    public MaxModelsCommand(CliConfigStore configStore)
    {
        _configStore = configStore;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        if (settings.Count is null)
        {
            var current = await _configStore.GetMaxModelsAsync();
            var suffix = _configStore.ConfigFileExists() ? "" : " (default)";
            AnsiConsole.MarkupLine($"Max models: {current}{suffix}");
            return 0;
        }

        if (settings.Count <= 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Count must be a positive integer.");
            return 1;
        }

        await _configStore.SetMaxModelsAsync(settings.Count.Value);
        AnsiConsole.MarkupLine($"Max models set to: {settings.Count.Value}");
        return 0;
    }
}
