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
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
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
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ISessionManager _sessionManager;
    private readonly Random _random = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroProvider"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="mediaSegmentManager">Media segment manager.</param>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="itemRepository">Item repository (chapter access).</param>
    /// <param name="httpContextAccessor">Accessor for the current request.</param>
    /// <param name="sessionManager">Session manager, used by the session frequency.</param>
    public IntroProvider(
        ILoggerFactory loggerFactory,
        IMediaSegmentManager mediaSegmentManager,
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        IHttpContextAccessor httpContextAccessor,
        ISessionManager sessionManager)
    {
        _logger = loggerFactory.CreateLogger<IntroProvider>();
        _segmentManager = mediaSegmentManager;
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
        _httpContextAccessor = httpContextAccessor;
        _sessionManager = sessionManager;
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
            // scanned library item. Make sure the hidden library exists before
            // resolving uploaded files (library-picked intros are already items).
            var intros = new List<IntroInfo>();
            var libraryEnsured = false;
            foreach (var assetId in selection.AssetIds)
            {
                var asset = configuration.Assets.FirstOrDefault(a => a.Id == assetId);
                if (asset is null)
                {
                    continue;
                }

                if (asset.LibraryItemId != Guid.Empty)
                {
                    // An existing library video used as the intro: the item is already
                    // scanned, so its path + id resolve directly.
                    if (_libraryManager.GetItemById(asset.LibraryItemId) is Video video &&
                        !string.IsNullOrEmpty(video.Path))
                    {
                        intros.Add(new IntroInfo { Path = video.Path, ItemId = video.Id });
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Intro asset {AssetName} points at a library item ({ItemId}) that no longer exists",
                            asset.Name,
                            asset.LibraryItemId);
                    }

                    continue;
                }

                var path = plugin.AssetStore.ResolvePath(asset);
                if (path is null)
                {
                    _logger.LogWarning("Intro asset {AssetName} is missing on disk", asset.Name);
                    continue;
                }

                if (!libraryEnsured)
                {
                    libraryEnsured = true;
                    if (!plugin.IntroLibrary.Exists())
                    {
                        await plugin.IntroLibrary.EnsureAsync().ConfigureAwait(false);
                    }
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

            if (!ShouldPlay(plugin, selection.Frequency, user, item))
            {
                return Enumerable.Empty<IntroInfo>();
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

    private bool ShouldPlay(IntroReplacerPlugin plugin, IntroFrequency frequency, User user, BaseItem item)
    {
        if (frequency == IntroFrequency.EveryTime)
        {
            return true;
        }

        var wait = TimeSpan.FromMinutes(Math.Max(1, plugin.Configuration.RepeatAfterMinutes));

        if (frequency == IntroFrequency.OncePerInterval)
        {
            return Allowed(
                plugin.SessionTracker.ShouldPlayAfterWait(user.Id, wait, DateTime.UtcNow),
                "the wait time has not passed");
        }

        var sessionId = ResolveSessionId(user);
        if (string.IsNullOrEmpty(sessionId))
        {
            // No session to key on (for example an API-key request); fall back to the
            // wait time so the intro is not played on every call.
            return Allowed(
                plugin.SessionTracker.ShouldPlayAfterWait(user.Id, wait, DateTime.UtcNow),
                "the client session could not be identified");
        }

        return Allowed(
            plugin.SessionTracker.ShouldPlayOncePerSession(sessionId),
            "it already played in this client session");

        bool Allowed(bool allowed, string reason)
        {
            if (!allowed)
            {
                _logger.LogDebug(
                    "IntroReplacer: skipping intro for '{Item}' ({Reason})",
                    item.Name,
                    reason);
            }

            return allowed;
        }
    }

    /// <summary>
    /// Finds the Jellyfin session id of the client that made the current request.
    /// </summary>
    private string? ResolveSessionId(User user)
    {
        try
        {
            var httpContext = _httpContextAccessor.HttpContext;
            var sessions = _sessionManager.Sessions
                .Where(s => s.UserId == user.Id)
                .ToList();

            // Claim type constants live in Jellyfin.Api.Constants.InternalClaimTypes,
            // which plugins do not reference; the literal values are stable.
            var deviceId = httpContext?.User?.FindFirst("Jellyfin-DeviceId")?.Value;
            if (!string.IsNullOrEmpty(deviceId))
            {
                var match = sessions.FirstOrDefault(s =>
                    string.Equals(s.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match.Id;
                }
            }

            // Fall back to the most recently active session for this user.
            return sessions.OrderByDescending(s => s.LastActivityDate).FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IntroReplacer: could not resolve the client session");
            return null;
        }
    }
}
