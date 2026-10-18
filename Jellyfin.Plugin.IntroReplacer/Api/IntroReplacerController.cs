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
    /// Lists registered intro assets and whether their file exists on disk.
    /// </summary>
    /// <returns>The assets.</returns>
    [HttpGet("Assets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<IntroAssetDto>> GetAssets()
    {
        var result = Plugin.Configuration.Assets.Select(a => new IntroAssetDto(
            a.Id,
            a.Name,
            a.Enabled,
            AssetExists(a),
            a.LibraryItemId == Guid.Empty ? "Uploaded" : "Library"));

        return Ok(result);
    }

    private bool AssetExists(IntroAsset asset)
        => asset.LibraryItemId != Guid.Empty
            ? _libraryManager.GetItemById(asset.LibraryItemId) is Video
            : Plugin.AssetStore.ResolvePath(asset) is not null;

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
            FileName = storedName,
            Enabled = true
        };

        Plugin.Configuration.Assets.Add(asset);
        Plugin.SaveConfiguration();

        // Register the intro folder as a (hidden) library so the new file becomes
        // a playable item, then trigger a scan so it is resolvable right away.
        await Plugin.IntroLibrary.EnsureAsync().ConfigureAwait(false);

        _logger.LogInformation("Uploaded intro asset {Name} ({File})", asset.Name, storedName);
        return Ok(new IntroAssetDto(asset.Id, asset.Name, asset.Enabled, true, "Uploaded"));
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
            return Ok(new IntroAssetDto(existing.Id, existing.Name, existing.Enabled, true, "Library"));
        }

        var asset = new IntroAsset
        {
            Id = Guid.NewGuid(),
            Name = Describe(item),
            LibraryItemId = itemId,
            Enabled = true
        };

        Plugin.Configuration.Assets.Add(asset);
        Plugin.SaveConfiguration();

        _logger.LogInformation("Registered library video {Name} ({ItemId}) as an intro", asset.Name, itemId);
        return Ok(new IntroAssetDto(asset.Id, asset.Name, asset.Enabled, true, "Library"));
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
                                .Select(e => new TargetNodeDto(e.Id, e.Name, "Episode", e.IndexNumber, Array.Empty<TargetNodeDto>()))
                                .ToList();

                            seasons.Add(new TargetNodeDto(
                                season.Id,
                                season.Name,
                                "Season",
                                season.IndexNumber,
                                episodes));
                        }
                    }

                    children.Add(new TargetNodeDto(series.Id, series.Name, "Series", null, seasons));
                }
                else if (item is Movie movie)
                {
                    children.Add(new TargetNodeDto(movie.Id, movie.Name, "Movie", null, Array.Empty<TargetNodeDto>()));
                }
            }

            result.Add(new TargetNodeDto(libraryId, library.Name, "Library", null, children));
        }

        return Ok(result);
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
/// <param name="Enabled">Whether enabled.</param>
/// <param name="Exists">Whether the source file/item still exists.</param>
/// <param name="Source">Where the intro comes from: <c>Uploaded</c> or <c>Library</c>.</param>
public record IntroAssetDto(Guid Id, string Name, bool Enabled, bool Exists, string Source);

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
public record TargetNodeDto(
    Guid Id,
    string Name,
    string Type,
    int? Index,
    IReadOnlyList<TargetNodeDto> Children);

/// <summary>Result of a rule evaluation test.</summary>
/// <param name="RuleName">Matched rule name, or null.</param>
/// <param name="IntroNames">Resolved intro names.</param>
public record RuleTestResult(string? RuleName, IReadOnlyList<string> IntroNames);
