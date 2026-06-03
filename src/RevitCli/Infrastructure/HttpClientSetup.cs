using Microsoft.Extensions.DependencyInjection;

namespace RevitCli.Infrastructure;

public static class HttpClientSetup
{
    public static IServiceCollection AddApsHttpClient(this IServiceCollection services)
    {
        services.AddHttpClient("aps", client =>
        {
            client.BaseAddress = new Uri("https://developer.api.autodesk.com");
        });

        return services;
    }
}
