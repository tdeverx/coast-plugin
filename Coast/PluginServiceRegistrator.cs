using Jellyfin.Plugin.Coast.Updates;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Coast;

/// <summary>Registers the bounded server change journal and its event subscriptions.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ChangeJournal>();
        serviceCollection.AddHostedService<ChangeMonitor>();
    }
}
