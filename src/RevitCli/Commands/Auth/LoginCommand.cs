using Spectre.Console.Cli;

namespace RevitCli.Commands.Auth;

public sealed class LoginCommand : AsyncCommand
{
    protected override Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellation)
    {
        // Will be implemented in TASK 4
        return Task.FromResult(0);
    }
}
