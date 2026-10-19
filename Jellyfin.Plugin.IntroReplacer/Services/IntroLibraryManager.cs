using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// Owns a hidden Jellyfin library that points at the plugin's intro folder.
/// <para>
/// This is required: when an <c>IIntroProvider</c> returns an <c>IntroInfo</c> with
/// only a <c>Path</c>, Jellyfin resolves it to a <c>Video</c> and then looks the item
/// up in the database by id. A file that is not a scanned library item resolves to
/// nothing and the intro is silently dropped. Registering the intro folder as a
/// library gives each intro file a real item id, which is what makes it playable.
/// </para>
/// <para>
/// The library is hidden from every user (removed from My Media / Latest / ordered
/// views) while remaining playable (kept out of the blocked-folders list and added
/// to the enabled-folders allow-list when the user restricts folders).
/// </para>
/// </summary>
public sealed class IntroLibraryManager
{
    /// <summary>The hidden library's name.</summary>
    public const string LibraryName = "Intro Replacer";

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<IntroLibraryManager> _logger;
    private readonly IntroAssetStore _assets;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroLibraryManager"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="assets">The intro file store (its folder can change at runtime).</param>
    public IntroLibraryManager(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        IntroAssetStore assets)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = loggerFactory.CreateLogger<IntroLibraryManager>();
        _assets = assets;
    }

    /// <summary>Gets a value indicating whether the hidden library currently exists.</summary>
    public bool Exists()
    {
        try
        {
            return FindVirtualFolder() is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intro Replacer: could not enumerate virtual folders");
            return false;
        }
    }

    /// <summary>
    /// Gets the hidden library's own item id, when it exists. Used to keep the
    /// library and its intro files out of the configuration page's lists.
    /// </summary>
    /// <returns>The library item id, or <c>null</c>.</returns>
    public Guid? GetLibraryItemId()
    {
        try
        {
            var folder = FindVirtualFolder();
            return folder is not null && Guid.TryParse(folder.ItemId, out var id) ? id : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Intro Replacer: could not read the hidden intro library id");
            return null;
        }
    }

    /// <summary>
    /// Creates the hidden library when missing, moves it when the intro folder changed,
    /// and re-applies the user hiding.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task EnsureAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var folder = FindVirtualFolder();
            if (folder is not null)
            {
                var location = folder.Locations?.FirstOrDefault();
                if (location is null || IntroAssetStore.PathsEqual(location, _assets.Directory))
                {
                    await HideFromUsersAsync().ConfigureAwait(false);
                    return;
                }

                // The configured folder changed while the server was down.
                _logger.LogInformation(
                    "Intro Replacer: hidden intro library points at {OldPath}, moving it to {NewPath}",
                    location,
                    _assets.Directory);
                await _libraryManager.RemoveVirtualFolder(LibraryName, refreshLibrary: false).ConfigureAwait(false);
            }

            if (!Directory.Exists(_assets.Directory))
            {
                // Nothing uploaded yet; the folder is created by the asset store on first upload.
                return;
            }

            await CreateLibraryAsync().ConfigureAwait(false);
            await HideFromUsersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intro Replacer: failed to create the hidden intro library");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rebuilds the hidden library over the store's current folder. Used after the
    /// administrator changes the intro folder.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task RecreateAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (FindVirtualFolder() is not null)
            {
                await _libraryManager.RemoveVirtualFolder(LibraryName, refreshLibrary: false).ConfigureAwait(false);
            }

            if (!Directory.Exists(_assets.Directory))
            {
                return;
            }

            _logger.LogInformation("Intro Replacer: rebuilding the hidden intro library at {Path}", _assets.Directory);
            await CreateLibraryAsync().ConfigureAwait(false);
            await HideFromUsersAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intro Replacer: failed to rebuild the hidden intro library");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CreateLibraryAsync()
    {
        var options = new LibraryOptions
        {
            Enabled = true,
            EnableRealtimeMonitor = false,
            EnableChapterImageExtraction = false,
            ExtractChapterImagesDuringLibraryScan = false,
            EnableTrickplayImageExtraction = false,
            ExtractTrickplayImagesDuringLibraryScan = false,
            EnableLUFSScan = false,
            EnableAutomaticSeriesGrouping = false,
            EnableEmbeddedTitles = false,
            EnableEmbeddedExtrasTitles = false,
            EnableEmbeddedEpisodeInfos = false,
            EnablePhotos = false,
            SaveLocalMetadata = false,
            AutomaticRefreshIntervalDays = 0,
            PathInfos = new[] { new MediaPathInfo(_assets.Directory) },
            // Do not try to match intro clips against online metadata.
            TypeOptions = new[] { "Movie", "Video" }
                .Select(t => new TypeOptions
                {
                    Type = t,
                    MetadataFetchers = Array.Empty<string>(),
                    MetadataFetcherOrder = Array.Empty<string>(),
                    ImageFetchers = Array.Empty<string>(),
                    ImageFetcherOrder = Array.Empty<string>()
                })
                .ToArray()
        };

        _logger.LogInformation("Intro Replacer: creating hidden intro library at {Path}", _assets.Directory);
        await _libraryManager
            .AddVirtualFolder(LibraryName, CollectionTypeOptions.movies, options, refreshLibrary: true)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the hidden library item that matches an absolute file path.
    /// </summary>
    /// <param name="fullPath">The absolute path of the intro file.</param>
    /// <returns>The library item, or <c>null</c> when not scanned yet.</returns>
    public BaseItem? FindItem(string fullPath)
    {
        var root = GetRootFolder();
        if (root is null)
        {
            return null;
        }

        try
        {
            return root.GetRecursiveChildren(c => c.Path is not null)
                .FirstOrDefault(c => IntroAssetStore.PathsEqual(c.Path, fullPath));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Intro Replacer: error walking the hidden intro library");
            return null;
        }
    }

    private VirtualFolderInfo? FindVirtualFolder()
    {
        return _libraryManager.GetVirtualFolders()
            .FirstOrDefault(v => string.Equals(v.Name, LibraryName, StringComparison.OrdinalIgnoreCase));
    }

    private Folder? GetRootFolder()
    {
        var folder = FindVirtualFolder();
        if (folder is null || !Guid.TryParse(folder.ItemId, out var id))
        {
            return null;
        }

        return _libraryManager.GetItemById(id) as Folder;
    }

    private async Task HideFromUsersAsync()
    {
        var folder = FindVirtualFolder();
        if (folder is null || !Guid.TryParse(folder.ItemId, out var folderId))
        {
            return;
        }

        foreach (var user in _userManager.Users)
        {
            try
            {
                // Keep it playable: never blocked, and present in the allow-list
                // when the user restricts folders at all.
                var blocked = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders);
                if (blocked.Contains(folderId))
                {
                    user.SetPreference(PreferenceKind.BlockedMediaFolders, blocked.Where(x => x != folderId).ToArray());
                }

                var enabled = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);
                if (enabled.Length > 0 && !enabled.Contains(folderId))
                {
                    user.SetPreference(PreferenceKind.EnabledFolders, enabled.Append(folderId).ToArray());
                }

                // Keep it out of the home screen and search.
                var myMedia = user.GetPreferenceValues<Guid>(PreferenceKind.MyMediaExcludes);
                if (!myMedia.Contains(folderId))
                {
                    user.SetPreference(PreferenceKind.MyMediaExcludes, myMedia.Append(folderId).ToArray());
                }

                var latest = user.GetPreferenceValues<Guid>(PreferenceKind.LatestItemExcludes);
                if (!latest.Contains(folderId))
                {
                    user.SetPreference(PreferenceKind.LatestItemExcludes, latest.Append(folderId).ToArray());
                }

                var ordered = user.GetPreferenceValues<Guid>(PreferenceKind.OrderedViews);
                if (ordered.Contains(folderId))
                {
                    user.SetPreference(PreferenceKind.OrderedViews, ordered.Where(x => x != folderId).ToArray());
                }

                var grouped = user.GetPreferenceValues<Guid>(PreferenceKind.GroupedFolders);
                if (grouped.Contains(folderId))
                {
                    user.SetPreference(PreferenceKind.GroupedFolders, grouped.Where(x => x != folderId).ToArray());
                }

                await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intro Replacer: could not hide the intro library from user {User}", user.Username);
            }
        }
    }
}
