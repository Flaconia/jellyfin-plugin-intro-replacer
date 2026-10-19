using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IntroReplacer.Configuration;
using Jellyfin.Plugin.IntroReplacer.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroReplacer.Api;

/// <summary>
/// REST endpoints backing the dashboard configuration page: asset upload/list/delete,
/// library listing for rule conditions, and a rule evaluator for the "Test" button.
/// Configuration itself is stored through Jellyfin's standard plugin config route.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("[controller]")]
[Produces(MediaTypeNames.Application.Json)]
public class IntroReplacerController : ControllerBase
{
    private readonly ILogger<IntroReplacerController> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly Random _random = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroReplacerController"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public IntroReplacerController(
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = loggerFactory.CreateLogger<IntroReplacerController>();
    }

    private static IntroReplacerPlugin Plugin =>
        IntroReplacerPlugin.Instance ?? throw new InvalidOperationException("Plugin not initialized");

    /// <summary>
    /// Lists registered intro assets and whether their file exists on disk. Files
    /// dropped into the intro folder are registered automatically on this call.
    /// </summary>
    /// <returns>The assets.</returns>
    [HttpGet("Assets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<IntroAssetDto>>> GetAssets()
    {
        var config = Plugin.Configuration;
        if (Plugin.AssetStore.ScanForNewFiles(config))
        {
            Plugin.SaveConfiguration();
            await Plugin.IntroLibrary.EnsureAsync().ConfigureAwait(false);
            _libraryManager.QueueLibraryScan();
        }

        var result = config.Assets.Select(a => new IntroAssetDto(
            a.Id,
            a.Name,
            AssetExists(a),
            a.LibraryItemId == Guid.Empty ? "Uploaded" : "Library"));

        return Ok(result);
    }

    private bool AssetExists(IntroAsset asset)
        => asset.LibraryItemId != Guid.Empty
            ? _libraryManager.GetItemById(asset.LibraryItemId) is Video
            : Plugin.AssetStore.ResolvePath(asset) is not null;

    /// <summary>
    /// Returns the intro folder that is currently in use.
    /// </summary>
    /// <returns>The configured and effective folder.</returns>
    [HttpGet("IntroFolder")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IntroFolderDto> GetIntroFolder()
    {
        return Ok(new IntroFolderDto(
            Plugin.Configuration.IntroFolder ?? string.Empty,
            Plugin.AssetStore.Directory,
            IntroAssetStore.PathsEqual(Plugin.AssetStore.Directory, Plugin.AssetStore.DefaultDirectory)));
    }

    /// <summary>
    /// Moves the intro files to a new folder, re-points the hidden library and saves
    /// the configuration. An empty path restores the plugin's own folder.
    /// </summary>
    /// <param name="request">The requested folder.</param>
    /// <returns>The effective folder.</returns>
    [HttpPost("IntroFolder")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IntroFolderDto>> SetIntroFolder([FromBody] IntroFolderRequest? request)
    {
        var requested = request?.Path?.Trim();
        var target = string.IsNullOrEmpty(requested)
            ? Plugin.AssetStore.DefaultDirectory
            : requested;

        if (!Path.IsPathFullyQualified(target))
        {
            return BadRequest("Use an absolute folder path (for example /mnt/media/intros).");
        }

        try
        {
            target = Path.GetFullPath(target);
            Directory.CreateDirectory(target);

            // Fail early when the folder is not writable.
            var probe = Path.Combine(target, ".introreplacer-write-test");
            await System.IO.File.WriteAllTextAsync(probe, string.Empty).ConfigureAwait(false);
            System.IO.File.Delete(probe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intro Replacer: intro folder {Path} is not usable", target);
            return BadRequest("That folder cannot be written to. Create it or pick another one.");
        }

        var config = Plugin.Configuration;
        int moved;
        try
        {
            moved = Plugin.AssetStore.MoveAll(target, config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intro Replacer: failed to move intro files to {Path}", target);
            return BadRequest("Could not move the existing intro files to that folder.");
        }

        try
        {
            Plugin.AssetStore.SetDirectory(target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Intro Replacer: failed to use intro folder {Path}", target);
            return BadRequest("That folder cannot be used.");
        }

        config.IntroFolder = IntroAssetStore.PathsEqual(target, Plugin.AssetStore.DefaultDirectory)
            ? string.Empty
            : target;
        // Files dropped into the new folder become assets too.
        Plugin.AssetStore.ScanForNewFiles(config);
        Plugin.SaveConfiguration();

        await Plugin.IntroLibrary.RecreateAsync().ConfigureAwait(false);
        _libraryManager.QueueLibraryScan();

        _logger.LogInformation(
            "Intro Replacer: intro folder set to {Path} ({Moved} file(s) moved)",
            Plugin.AssetStore.Directory,
            moved);

        return Ok(new IntroFolderDto(
            config.IntroFolder,
            Plugin.AssetStore.Directory,
            IntroAssetStore.PathsEqual(Plugin.AssetStore.Directory, Plugin.AssetStore.DefaultDirectory)));
    }

    /// <summary>
    /// Uploads a new intro file and registers it as an asset.
    /// </summary>
    /// <param name="file">The uploaded video file.</param>
    /// <returns>The created asset.</returns>
    [HttpPost("Assets")]
    [RequestSizeLimit(2_000_000_000)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IntroAssetDto>> UploadAsset([FromForm] IFormFile file)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest("No file provided.");
        }

        if (!IntroAssetStore.IsSupported(file.FileName))
        {
            return BadRequest("Unsupported file type.");
        }

        await using var stream = file.OpenReadStream();
        var storedName = Plugin.AssetStore.Save(file.FileName, stream);

        var asset = new IntroAsset
        {
            Id = Guid.NewGuid(),
            Name = Path.GetFileNameWithoutExtension(file.FileName),
            FileName = storedName
        };

        Plugin.Configuration.Assets.Add(asset);
        Plugin.SaveConfiguration();

        // Register the intro folder as a (hidden) library so the new file becomes
        // a playable item, then trigger a scan so it is resolvable right away.
        await Plugin.IntroLibrary.EnsureAsync().ConfigureAwait(false);
        _libraryManager.QueueLibraryScan();

        _logger.LogInformation("Uploaded intro asset {Name} ({File})", asset.Name, storedName);
        return Ok(new IntroAssetDto(asset.Id, asset.Name, true, "Uploaded"));
    }

    /// <summary>
    /// Registers an existing library video as an intro asset. The media file is never
    /// copied or modified; the plugin only remembers the item id.
    /// </summary>
    /// <param name="itemId">The id of the library video to use as an intro.</param>
    /// <returns>The created (or already registered) asset.</returns>
    [HttpPost("Assets/FromLibrary/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IntroAssetDto> AddLibraryAsset([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (item is not Video video || string.IsNullOrEmpty(video.Path))
        {
            return BadRequest("The selected item is not a playable video file.");
        }

        var existing = Plugin.Configuration.Assets.FirstOrDefault(a => a.LibraryItemId == itemId);
        if (existing is not null)
        {
            return Ok(new IntroAssetDto(existing.Id, existing.Name, true, "Library"));
        }

        var asset = new IntroAsset
        {
            Id = Guid.NewGuid(),
            Name = Describe(item),
            LibraryItemId = itemId
        };

        Plugin.Configuration.Assets.Add(asset);
        Plugin.SaveConfiguration();

        _logger.LogInformation("Registered library video {Name} ({ItemId}) as an intro", asset.Name, itemId);
        return Ok(new IntroAssetDto(asset.Id, asset.Name, true, "Library"));
    }

    /// <summary>
    /// Lists every video file in every library, so an existing video can be picked as
    /// an intro. The plugin's own hidden intro library is excluded.
    /// </summary>
    /// <returns>The candidates, ordered by library then label.</returns>
    [HttpGet("Videos")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<VideoCandidateDto>> GetVideos()
    {
        var introLibraryId = Plugin.IntroLibrary.GetLibraryItemId();
        var includeTypes = new[]
        {
            BaseItemKind.Movie,
            BaseItemKind.Episode,
            BaseItemKind.Video,
            BaseItemKind.MusicVideo,
            BaseItemKind.Trailer
        };

        var candidates = new List<VideoCandidateDto>();

        // One query per library: the library name comes for free, without walking the
        // ancestor chain of every single item.
        foreach (var library in _libraryManager.GetVirtualFolders())
        {
            if (!Guid.TryParse(library.ItemId, out var libraryId)
                || libraryId == Guid.Empty
                || libraryId == introLibraryId)
            {
                continue;
            }

            var items = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = includeTypes,
                Recursive = true,
                IsVirtualItem = false,
                ParentId = libraryId
            }).Items;

            candidates.AddRange(items
                .Where(i => i is Video && !string.IsNullOrEmpty(i.Path))
                .Select(i => new VideoCandidateDto(i.Id, Describe(i), i.GetBaseItemKind().ToString(), library.Name)));
        }

        return Ok(candidates
            .OrderBy(c => c.Library, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static string Describe(BaseItem item)
    {
        switch (item)
        {
            case Episode episode:
                var series = episode.SeriesName;
                var season = episode.ParentIndexNumber;
                var number = episode.IndexNumber;
                var code = season.HasValue || number.HasValue
                    ? $"S{(season ?? 0):00}E{(number ?? 0):00}"
                    : string.Empty;
                return string.Join(
                    " ",
                    new[] { series, code, episode.Name }.Where(s => !string.IsNullOrWhiteSpace(s)));
            case Movie movie:
                return movie.ProductionYear is int year ? $"{movie.Name} ({year})" : movie.Name;
            default:
                return item.Name;
        }
    }

    /// <summary>
    /// Deletes an asset. Only files uploaded into the plugin's folder are removed;
    /// assets that point at an existing library video are just unregistered.
    /// </summary>
    /// <param name="id">The asset id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Assets/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteAsset([FromRoute] Guid id)
    {
        var asset = Plugin.Configuration.Assets.FirstOrDefault(a => a.Id == id);
        if (asset is null)
        {
            return NotFound();
        }

        Plugin.AssetStore.Delete(asset);
        Plugin.Configuration.Assets.Remove(asset);

        foreach (var rule in Plugin.Configuration.Rules)
        {
            rule.AssetIds.Remove(id);
        }

        Plugin.SaveConfiguration();
        return NoContent();
    }

    /// <summary>
    /// Lists users so the config page can offer them as rule conditions.
    /// </summary>
    /// <returns>The users.</returns>
    [HttpGet("Users")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<UserDto>> GetUsers()
    {
        var users = _userManager.Users
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => new UserDto(u.Id, u.Username));

        return Ok(users);
    }

    /// <summary>
    /// Returns the target tree used by the config page: every targetable library,
    /// each with its series (season/episode children) and movies.
    /// </summary>
    /// <returns>The target tree, libraries first, ordered by name.</returns>
    [HttpGet("Targets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<TargetNodeDto>> GetTargets()
    {
        var user = _userManager.Users.FirstOrDefault();

        // Never offer the plugin's own hidden intro library (its intro clips show up
        // as movies) as something to target.
        var introLibraryId = Plugin.IntroLibrary.GetLibraryItemId();

        var result = new List<TargetNodeDto>();

        // One query per library: the library name comes for free, without walking the
        // ancestor chain of every single item.
        foreach (var library in _libraryManager.GetVirtualFolders()
            .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(library.ItemId, out var libraryId)
                || libraryId == Guid.Empty
                || libraryId == introLibraryId)
            {
                continue;
            }

            var items = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false,
                ParentId = libraryId
            }).Items;

            if (items.Count == 0)
            {
                // Nothing targetable (for example a music library); skip the noise.
                continue;
            }

            var children = new List<TargetNodeDto>();
            foreach (var item in items.OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase))
            {
                if (item is Series series)
                {
                    var seasons = new List<TargetNodeDto>();
                    if (user is not null)
                    {
                        foreach (var season in series.GetChildren(user, true)
                                     .OfType<Season>()
                                     .OrderBy(s => s.IndexNumber ?? 0))
                        {
                            var episodes = season.GetChildren(user, true)
                                .OfType<Episode>()
                                .OrderBy(e => e.IndexNumber ?? 0)
                                .Select(e => Node(e, "Episode", e.IndexNumber, Array.Empty<TargetNodeDto>()))
                                .ToList();

                            seasons.Add(Node(season, "Season", season.IndexNumber, episodes));
                        }
                    }

                    children.Add(Node(series, "Series", null, seasons));
                }
                else if (item is Movie movie)
                {
                    children.Add(Node(movie, "Movie", null, Array.Empty<TargetNodeDto>()));
                }
            }

            var libraryItem = _libraryManager.GetItemById(libraryId);
            result.Add(libraryItem is not null
                ? Node(libraryItem, "Library", null, children)
                : new TargetNodeDto(
                    libraryId,
                    library.Name,
                    "Library",
                    null,
                    children,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()));
        }

        return Ok(result);

        // The config page previews tag-based rules locally, so every node carries
        // the metadata it would match against (own tags/genres/studios merged with
        // the ancestors, exactly like IntroMatcher does for playback).
        static TargetNodeDto Node(BaseItem item, string type, int? index, IReadOnlyList<TargetNodeDto> children)
        {
            var (tags, genres, studios) = IntroMatcher.GetMergedMetadata(item);
            return new TargetNodeDto(item.Id, item.Name, type, index, children, tags, genres, studios);
        }
    }

    /// <summary>
    /// Evaluates the configured rules for an item, without playing anything.
    /// Useful for the config page's "Test" button.
    /// </summary>
    /// <param name="itemId">The item (episode/movie) to evaluate.</param>
    /// <param name="userId">Optional user id used for user-scoped rules.</param>
    /// <returns>The matched rule and intros, if any.</returns>
    [HttpPost("Test/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<RuleTestResult> TestRule([FromRoute] Guid itemId, [FromQuery] Guid? userId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var context = IntroMatcher.BuildContext(item, userId ?? Guid.Empty, _libraryManager);
        var selection = RuleEngine.Select(Plugin.Configuration, context, _random);
        if (selection is null)
        {
            return Ok(new RuleTestResult(null, Array.Empty<string>()));
        }

        var names = selection.AssetIds
            .Select(id => Plugin.Configuration.Assets.FirstOrDefault(a => a.Id == id)?.Name)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToList();

        return Ok(new RuleTestResult(selection.RuleName, names));
    }
}

/// <summary>Asset representation returned to the config page.</summary>
/// <param name="Id">Asset id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Exists">Whether the source file/item still exists.</param>
/// <param name="Source">Where the intro comes from: <c>Uploaded</c> or <c>Library</c>.</param>
public record IntroAssetDto(Guid Id, string Name, bool Exists, string Source);

/// <summary>A library video that can be used as an intro.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Label">Human readable label (series/episode code, movie year, …).</param>
/// <param name="Type">Item type name.</param>
/// <param name="Library">Name of the library the video belongs to.</param>
public record VideoCandidateDto(Guid Id, string Label, string Type, string Library);

/// <summary>User representation.</summary>
/// <param name="Id">User id.</param>
/// <param name="Name">Username.</param>
public record UserDto(Guid Id, string Name);

/// <summary>A node in the series/season/episode/movie target tree.</summary>
/// <param name="Id">Item id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Type">Series, Season, Episode or Movie.</param>
/// <param name="Index">Season/episode number, when applicable.</param>
/// <param name="Children">Nested nodes.</param>
/// <param name="Tags">Tags merged with the ancestors (used by the tag-rule preview).</param>
/// <param name="Genres">Genres merged with the ancestors.</param>
/// <param name="Studios">Studios merged with the ancestors.</param>
public record TargetNodeDto(
    Guid Id,
    string Name,
    string Type,
    int? Index,
    IReadOnlyList<TargetNodeDto> Children,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Studios);

/// <summary>Result of a rule evaluation test.</summary>
/// <param name="RuleName">Matched rule name, or null.</param>
/// <param name="IntroNames">Resolved intro names.</param>
public record RuleTestResult(string? RuleName, IReadOnlyList<string> IntroNames);

/// <summary>Request to change the intro folder.</summary>
/// <param name="Path">The new absolute folder; empty restores the default.</param>
public record IntroFolderRequest(string? Path);

/// <summary>The intro folder in use.</summary>
/// <param name="Configured">The saved value (empty when using the default folder).</param>
/// <param name="Effective">The absolute folder that holds the intro files.</param>
/// <param name="IsDefault">Whether <paramref name="Effective"/> is the plugin's own folder.</param>
public record IntroFolderDto(string Configured, string Effective, bool IsDefault);
