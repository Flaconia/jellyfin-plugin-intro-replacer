using System;
using System.Collections.Generic;
using Jellyfin.Plugin.IntroReplacer.Configuration;
using Jellyfin.Plugin.IntroReplacer.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroReplacer;

/// <summary>
/// The Intro Replacer plugin. Plays a per-item custom intro before an episode
/// or movie, then lets the client skip the original intro.
/// </summary>
public class IntroReplacerPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The stable plugin GUID (also used in build.yaml/manifest.json).</summary>
    public const string PluginGuid = "07e08043-224d-4228-964f-02242635344f";

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroReplacerPlugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public IntroReplacerPlugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ISessionManager sessionManager,
        ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        LibraryManager = libraryManager;
        AssetStore = new IntroAssetStore(applicationPaths.PluginConfigurationsPath);
        SessionTracker = new IntroSessionTracker();
        sessionManager.SessionEnded += (_, e) => SessionTracker.NotifySessionEnded(e.SessionInfo?.Id);
        IntroLibrary = new IntroLibraryManager(libraryManager, userManager, loggerFactory, AssetStore.Directory);
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static IntroReplacerPlugin? Instance { get; private set; }

    /// <summary>Gets the library manager.</summary>
    public static ILibraryManager? LibraryManager { get; private set; }

    /// <summary>Gets the intro file store.</summary>
    public IntroAssetStore AssetStore { get; }

    /// <summary>Gets the per-user session tracker for "once per session" rules.</summary>
    public IntroSessionTracker SessionTracker { get; }

    /// <summary>Gets the hidden library that makes intro files playable.</summary>
    public IntroLibraryManager IntroLibrary { get; }

    /// <inheritdoc />
    public override string Name => "Intro Replacer";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
            }
        };
    }
}
