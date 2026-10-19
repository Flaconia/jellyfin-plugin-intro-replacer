using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.IntroReplacer.Configuration;

/// <summary>
/// Plugin configuration. Serialized to XML by Jellyfin, so keep it to simple,
/// settable types (no dictionaries, no interfaces).
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the registered intro videos: files uploaded into the plugin's
    /// folder, or existing library items picked by the administrator.
    /// </summary>
    public List<IntroAsset> Assets { get; set; } = new();

    /// <summary>
    /// Gets or sets the ordered rules. First enabled matching rule wins.
    /// </summary>
    public List<IntroRule> Rules { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether intros are limited to movies.
    /// </summary>
    public bool MoviesOnly { get; set; }

    /// <summary>
    /// Gets or sets the folder that holds uploaded intro files. Empty means the
    /// plugin's own folder (under the Jellyfin configuration directory); set it to a
    /// folder on a larger drive to keep the server drive free.
    /// </summary>
    public string IntroFolder { get; set; } = string.Empty;
}

/// <summary>
/// A single intro video registered with the plugin.
/// </summary>
public class IntroAsset
{
    /// <summary>Gets or sets the asset id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file name relative to the plugin's intros folder. Empty when
    /// the asset points at an existing library video (see <see cref="LibraryItemId"/>).
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of an existing library video used as the intro, or
    /// <see cref="Guid.Empty"/> for a file uploaded into the plugin's folder.
    /// </summary>
    public Guid LibraryItemId { get; set; }
}

/// <summary>
/// A rule that decides which intro(s) to play for an item. Conditions are ANDed;
/// an empty condition list matches everything. First enabled matching rule wins.
/// </summary>
public class IntroRule
{
    /// <summary>Gets or sets the rule id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the rule name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this rule is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the library ids this rule applies to. Kept for configurations
    /// written by older versions; the config page now drives scoping through
    /// <see cref="ExcludedLibraryIds"/>. An empty list means "all libraries".
    /// </summary>
    public List<Guid> LibraryIds { get; set; } = new();

    /// <summary>
    /// Gets or sets library ids excluded from this rule. All libraries are active
    /// unless listed here, so deactivating one hides its contents.
    /// </summary>
    public List<Guid> ExcludedLibraryIds { get; set; } = new();

    /// <summary>Gets or sets the user ids this rule applies to (empty = all users).</summary>
    public List<Guid> UserIds { get; set; } = new();

    /// <summary>
    /// Gets or sets the item ids that activate this rule's item scope (series or
    /// movie ids; empty = all items matching the other conditions). Seasons and
    /// episodes inherit activation from their series unless excluded.
    /// </summary>
    public List<Guid> TargetItemIds { get; set; } = new();

    /// <summary>
    /// Gets or sets item ids deactivated inside an active target (seasons or
    /// episodes). Only meaningful when <see cref="TargetItemIds"/> is not empty.
    /// </summary>
    public List<Guid> ExcludedItemIds { get; set; } = new();

    /// <summary>Gets or sets tags that must be present (any of).</summary>
    public List<string> IncludeTags { get; set; } = new();

    /// <summary>Gets or sets tags that must not be present.</summary>
    public List<string> ExcludeTags { get; set; } = new();

    /// <summary>Gets or sets genres that must be present (any of).</summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>Gets or sets studios that must be present (any of).</summary>
    public List<string> Studios { get; set; } = new();

    /// <summary>Gets or sets an optional current-date window.</summary>
    public DateRange? DateRange { get; set; }

    /// <summary>Gets or sets an optional media release-date window.</summary>
    public DateRange? ReleaseDateRange { get; set; }

    /// <summary>Gets or sets the intro assets to use.</summary>
    public List<Guid> AssetIds { get; set; } = new();

    /// <summary>Gets or sets how the assets are played.</summary>
    public IntroPlaybackMode PlaybackMode { get; set; } = IntroPlaybackMode.RandomOne;

    /// <summary>
    /// Gets or sets what happens to the item's own intro. Replace keeps the item's
    /// Intro media segments so clients skip the original; Prepend removes them.
    /// </summary>
    public IntroPlacement Placement { get; set; } = IntroPlacement.Replace;

    /// <summary>Gets or sets how often the custom intro plays.</summary>
    public IntroFrequency Frequency { get; set; } = IntroFrequency.EveryTime;

    /// <summary>
    /// Gets or sets how long (in minutes) the intro waits before it may play again.
    /// Only used by <see cref="IntroFrequency.OncePerInterval"/>.
    /// </summary>
    public int RepeatAfterMinutes { get; set; } = 180;
}

/// <summary>How intros are selected from a rule's asset list.</summary>
public enum IntroPlaybackMode
{
    /// <summary>Play one random asset.</summary>
    RandomOne,

    /// <summary>Play every asset in order.</summary>
    SequentialAll
}

/// <summary>What happens to the item's own intro.</summary>
public enum IntroPlacement
{
    /// <summary>Skip the item's own intro (keep its Intro media segments).</summary>
    Replace,

    /// <summary>Play the item from the start (remove its Intro media segments).</summary>
    Prepend
}

/// <summary>How often the custom intro plays.</summary>
public enum IntroFrequency
{
    /// <summary>
    /// Play before every title the user starts. When several titles are started
    /// together (a queue), Jellyfin plays the intro before the first one only.
    /// </summary>
    EveryTime,

    /// <summary>
    /// Play again only once the rule's configured wait time has passed
    /// (<see cref="IntroRule.RepeatAfterMinutes"/>).
    /// </summary>
    OncePerInterval,

    /// <summary>
    /// Play once per client session; it plays again after the client disconnects
    /// (closes the app, or signs out) and connects again.
    /// </summary>
    OncePerSession
}

/// <summary>A date window with optional repetition.</summary>
public class DateRange
{
    /// <summary>Gets or sets the window start.</summary>
    public DateTime Start { get; set; }

    /// <summary>Gets or sets the window end.</summary>
    public DateTime End { get; set; }

    /// <summary>Gets or sets the repeat mode.</summary>
    public RepeatMode Repeat { get; set; } = RepeatMode.None;

    /// <summary>
    /// Returns whether <paramref name="date"/> falls inside this window.
    /// </summary>
    /// <param name="date">The date to test.</param>
    /// <returns><c>true</c> when the date is inside the window.</returns>
    public bool Contains(DateTime date)
    {
        return Repeat switch
        {
            RepeatMode.None => date >= Start && date <= End,
            RepeatMode.Weekly => date.DayOfWeek >= Start.DayOfWeek && date.DayOfWeek <= End.DayOfWeek,
            RepeatMode.Monthly => date.Day >= Start.Day && date.Day <= End.Day,
            RepeatMode.Yearly => ContainsYearly(date),
            _ => false
        };
    }

    private bool ContainsYearly(DateTime date)
    {
        var year = date.Year;
        var current = new DateTime(year, date.Month, date.Day);
        var start = new DateTime(year, Start.Month, Start.Day);
        var end = new DateTime(year, End.Month, End.Day);
        return current >= start && current <= end;
    }
}

/// <summary>How a <see cref="DateRange"/> repeats.</summary>
public enum RepeatMode
{
    /// <summary>No repeat.</summary>
    None,

    /// <summary>Repeat weekly.</summary>
    Weekly,

    /// <summary>Repeat monthly.</summary>
    Monthly,

    /// <summary>Repeat yearly.</summary>
    Yearly
}
