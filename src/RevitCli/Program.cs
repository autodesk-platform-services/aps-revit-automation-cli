using Microsoft.Extensions.DependencyInjection;
using RevitCli.Commands;
using RevitCli.Commands.Auth;
using RevitCli.Infrastructure;
using Spectre.Console.Cli;

var services = new ServiceCollection();
services.AddApsHttpClient();
services.AddSingleton<TokenStore>();
services.AddSingleton<AppStateStore>();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("revit");

    config.AddCommand<RunCommand>("run")
        .WithDescription("Execute a Revit automation job from a YAML configuration file");

    config.AddCommand<ValidateCommand>("validate")
        .WithDescription("Validate a job YAML configuration file");

    config.AddBranch("auth", auth =>
    {
        auth.SetDescription("Authentication commands");

        auth.AddCommand<LoginCommand>("login")
            .WithDescription("Log in via browser-based Autodesk authentication");

        auth.AddCommand<StatusCommand>("status")
            .WithDescription("Show current authentication token status");
    });
});

return await app.RunAsync(args);
