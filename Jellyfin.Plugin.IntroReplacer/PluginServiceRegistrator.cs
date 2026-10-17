using Jellyfin.Plugin.IntroReplacer.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.IntroReplacer;

/// <summary>
/// Registers the plugin's services with Jellyfin's dependency injection container.
/// <para>
/// Unlike <see cref="MediaBrowser.Controller.Library.IIntroProvider"/>, segment
/// providers are not discovered automatically from plugin assemblies — a plugin
/// must register them itself. This is required so the chapter-based Intro segments
/// pass the provider filter on <c>GET /MediaSegments/{itemId}</c> and reach clients.
/// </para>
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IMediaSegmentProvider, IntroSegmentProvider>();
    }
}
