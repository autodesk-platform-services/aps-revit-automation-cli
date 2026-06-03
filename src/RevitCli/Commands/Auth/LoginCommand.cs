using RevitCli.Services;
using Spectre.Console;
using Spectre.Console.Cli;

namespace RevitCli.Commands.Auth;

public sealed class LoginCommand : AsyncCommand
{
    private readonly AuthService _authService;

    public LoginCommand(AuthService authService)
    {
        _authService = authService;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellation)
    {
        AnsiConsole.MarkupLine("[yellow]To log in, you need your APS application credentials.[/]");
        var clientId = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter your [green]Client ID[/]:"));
        var clientSecret = AnsiConsole.Prompt(
            new TextPrompt<string>("Enter your [green]Client Secret[/]:").Secret());

        try
        {
            AnsiConsole.MarkupLine("[blue]Opening browser for Autodesk login...[/]");
            await _authService.EnsureThreeLeggedTokenAsync(clientId, clientSecret);
            AnsiConsole.MarkupLine("[green]Login successful! Token stored.[/]");
            return 0;
        }
        catch (TimeoutException)
        {
            AnsiConsole.MarkupLine("[red]Login timed out. Please try again.[/]");
            return 1;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Login failed: {ex.Message}[/]");
            return 1;
        }
    }
}
