using System.ComponentModel;
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

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellation)
    {
        // Will be implemented in TASK 10
        return Task.FromResult(0);
    }
}
