using Microsoft.Extensions.DependencyInjection;
using RevitCli.Commands;
using RevitCli.Commands.Auth;
using RevitCli.Infrastructure;
using RevitCli.Services;
using Spectre.Console.Cli;

var services = new ServiceCollection();
services.AddApsHttpClient();
services.AddSingleton<TokenStore>();
services.AddSingleton<AuthService>();
services.AddSingleton<DesignAutomationService>();
services.AddSingleton<DataManagementService>();
services.AddSingleton<OssService>();
services.AddSingleton<RevitEngineResolver>();
services.AddSingleton<AppBundlePackager>();
services.AddSingleton<CliConfigStore>();
services.AddSingleton<YamlConfigService>();
services.AddSingleton<JobRunner>();

var registrar = new TypeRegistrar(services);
var app = new CommandApp(registrar);

app.Configure(config =>
{
    config.SetApplicationName("revit");
    config.SetApplicationVersion(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    config.AddCommand<RunCommand>("run")
        .WithDescription("Execute a Revit automation job from a YAML configuration file");

    config.AddCommand<UpdateCommand>("update")
        .WithDescription("Update the AppBundle and Activity to a new version without running a job");

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
