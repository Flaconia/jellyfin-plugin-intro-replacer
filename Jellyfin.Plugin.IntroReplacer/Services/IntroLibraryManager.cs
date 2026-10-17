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
    private readonly string _rootPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroLibraryManager"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="rootPath">The folder that holds the intro files.</param>
    public IntroLibraryManager(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        string rootPath)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = loggerFactory.CreateLogger<IntroLibraryManager>();
        _rootPath = rootPath;
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
    /// Creates the hidden library when missing and re-applies the user hiding.
    /// </summary>
    /// <returns>A task.</returns>
    public async Task EnsureAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (FindVirtualFolder() is not null)
            {
                await HideFromUsersAsync().ConfigureAwait(false);
                return;
            }

            if (!Directory.Exists(_rootPath))
            {
                // Nothing uploaded yet; the folder is created by the asset store on first upload.
                return;
            }

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
                PathInfos = new[] { new MediaPathInfo(_rootPath) },
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

            _logger.LogInformation("Intro Replacer: creating hidden intro library at {Path}", _rootPath);
            await _libraryManager
                .AddVirtualFolder(LibraryName, CollectionTypeOptions.movies, options, refreshLibrary: true)
                .ConfigureAwait(false);

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
                .FirstOrDefault(c => PathsEqual(c.Path, fullPath));
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

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        static string Normalize(string p) => p.Replace('\\', '/').TrimEnd('/');

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }
}
