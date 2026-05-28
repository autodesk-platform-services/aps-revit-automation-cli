using RevitCli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RevitCli.Commands.Auth;

public sealed class StatusCommand : AsyncCommand
{
    private readonly TokenStore _tokenStore;

    public StatusCommand(TokenStore tokenStore)
    {
        _tokenStore = tokenStore;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellation)
    {
        var stored = await _tokenStore.LoadAsync();

        if (stored is null)
        {
            AnsiConsole.MarkupLine("[yellow]No token stored. Run [bold]revit auth login[/] to authenticate.[/]");
            return 0;
        }

        var table = new Table();
        table.AddColumn("Property");
        table.AddColumn("Value");

        var now = DateTime.UtcNow;
        var isValid = stored.ExpiresAtUtc > now;
        var statusText = isValid
            ? "[green]Valid[/]"
            : "[red]Expired[/]";

        table.AddRow("Status", statusText);
        table.AddRow("Expires At (UTC)", stored.ExpiresAtUtc.ToString("yyyy-MM-dd HH:mm:ss"));

        if (isValid)
        {
            var remaining = stored.ExpiresAtUtc - now;
            table.AddRow("Time Remaining", $"{remaining.Hours}h {remaining.Minutes}m {remaining.Seconds}s");
        }

        table.AddRow("Has Refresh Token", stored.RefreshToken is not null ? "[green]Yes[/]" : "[yellow]No[/]");

        AnsiConsole.Write(table);
        return 0;
    }
}
