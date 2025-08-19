using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AudioPruner;

public class ServiceRegistration : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<Services.RemuxService>();
    }
}
