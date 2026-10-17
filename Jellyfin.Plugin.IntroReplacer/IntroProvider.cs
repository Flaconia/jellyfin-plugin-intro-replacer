using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IntroReplacer.Configuration;
using Jellyfin.Plugin.IntroReplacer.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroReplacer;

/// <summary>
/// Tells Jellyfin which intro(s) to play before an item. This is the same
/// server-side mechanism used by the official Local Intros plugin: the client
/// calls <c>GET /Items/{itemId}/Intros</c> and plays the returned files first.
/// </summary>
public class IntroProvider : IIntroProvider
{
    private readonly ILogger<IntroProvider> _logger;
    private readonly IMediaSegmentManager _segmentManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepository;
    private readonly Random _random = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroProvider"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="mediaSegmentManager">Media segment manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="itemRepository">Item repository (chapter access).</param>
    public IntroProvider(
        ILoggerFactory loggerFactory,
        IMediaSegmentManager mediaSegmentManager,
        ILibraryManager libraryManager,
        IItemRepository itemRepository)
    {
        _logger = loggerFactory.CreateLogger<IntroProvider>();
        _segmentManager = mediaSegmentManager;
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
    }

    /// <inheritdoc />
    public string Name => "Intro Replacer";

    /// <inheritdoc />
    public async Task<IEnumerable<IntroInfo>> GetIntros(BaseItem item, User user)
    {
        try
        {
            var plugin = IntroReplacerPlugin.Instance;
            if (plugin is null)
            {
                return Enumerable.Empty<IntroInfo>();
            }

            var configuration = plugin.Configuration;
            if (configuration.MoviesOnly && item.GetBaseItemKind() != BaseItemKind.Movie)
            {
                return Enumerable.Empty<IntroInfo>();
            }

            var context = IntroMatcher.BuildContext(item, user.Id, _libraryManager);
            var selection = RuleEngine.Select(configuration, context, _random);
            if (selection is null)
            {
                return Enumerable.Empty<IntroInfo>();
            }

            // A path-only intro is silently dropped by Jellyfin unless the file is a
            // scanned library item. Make sure the hidden library exists before resolving.
            if (!plugin.IntroLibrary.Exists())
            {
                await plugin.IntroLibrary.EnsureAsync().ConfigureAwait(false);
            }

            var intros = new List<IntroInfo>();
            foreach (var assetId in selection.AssetIds)
            {
                var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
                if (asset is null)
                {
                    continue;
                }

                var path = plugin.AssetStore.ResolvePath(asset);
                if (path is null)
                {
                    _logger.LogWarning("Intro asset {AssetName} is missing on disk", asset.Name);
                    continue;
                }

                var libraryItem = plugin.IntroLibrary.FindItem(path);
                if (libraryItem is not null)
                {
                    intros.Add(new IntroInfo { Path = libraryItem.Path, ItemId = libraryItem.Id });
                }
                else
                {
                    _logger.LogWarning(
                        "Intro asset {AssetName} is not a Jellyfin library item yet; waiting for the hidden intro library scan",
                        asset.Name);
                    intros.Add(new IntroInfo { Path = path });
                }
            }

            if (intros.Count == 0)
            {
                return Enumerable.Empty<IntroInfo>();
            }

            if (selection.Frequency == IntroFrequency.OncePerSession)
            {
                var idleTimeout = TimeSpan.FromMinutes(Math.Max(1, configuration.SessionIdleMinutes));
                if (!plugin.SessionTracker.ShouldPlay(user.Id, idleTimeout, DateTime.UtcNow))
                {
                    _logger.LogDebug(
                        "IntroReplacer: skipping intro for '{Item}' (already played this session)",
                        item.Name);
                    return Enumerable.Empty<IntroInfo>();
                }
            }

            _logger.LogInformation(
                "IntroReplacer rule '{Rule}' matched for '{Item}' ({Count} intro file(s), {Placement}, {Frequency})",
                selection.RuleName,
                item.Name,
                intros.Count,
                selection.Placement,
                selection.Frequency);

            await IntroSegmentHelper
                .ApplyPlacementAsync(_segmentManager, _itemRepository, item, selection.Placement, _logger)
                .ConfigureAwait(false);

            return intros;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IntroReplacer failed to resolve intros for {ItemName}", item.Name);
            return Enumerable.Empty<IntroInfo>();
        }
    }
}
