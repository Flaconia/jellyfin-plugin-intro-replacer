using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// A plain, Jellyfin-free snapshot of everything a rule can match on.
/// Keeping this primitive makes the rule engine unit-testable.
/// </summary>
public sealed class RuleContext
{
    /// <summary>Gets the library (collection folder) ids the item belongs to.</summary>
    public IReadOnlySet<Guid> CollectionFolderIds { get; init; } = new HashSet<Guid>();

    /// <summary>Gets the item id plus all ancestor ids (season, series, folders).</summary>
    public IReadOnlySet<Guid> ItemAndAncestorIds { get; init; } = new HashSet<Guid>();

    /// <summary>Gets the merged tags from the item and its ancestors.</summary>
    public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the merged genres from the item and its ancestors.</summary>
    public IReadOnlySet<string> Genres { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the merged studios from the item and its ancestors.</summary>
    public IReadOnlySet<string> Studios { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the id of the user playing the item.</summary>
    public Guid UserId { get; init; }

    /// <summary>Gets the current time.</summary>
    public DateTime Now { get; init; } = DateTime.Now;

    /// <summary>Gets the release (premiere) date of the item or its nearest ancestor with one.</summary>
    public DateTime? ReleaseDate { get; init; }
}
