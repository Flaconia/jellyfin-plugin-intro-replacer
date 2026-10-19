using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// Builds a <see cref="RuleContext"/> from a Jellyfin item, merging the item's
/// own metadata with that of its ancestors (season, series, folders).
/// </summary>
public static class IntroMatcher
{
    /// <summary>
    /// Builds the rule context for an item.
    /// </summary>
    /// <param name="item">The item being played.</param>
    /// <param name="userId">The id of the playing user.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="now">The current time (injectable for tests).</param>
    /// <returns>The populated context.</returns>
    public static RuleContext BuildContext(
        BaseItem item,
        Guid userId,
        ILibraryManager libraryManager,
        DateTime? now = null)
    {
        var ancestors = item.GetParents().ToList();
        var (tags, genres, studios) = GetMergedMetadata(item);

        var itemAndAncestors = new HashSet<Guid> { item.Id };
        foreach (var id in item.GetAncestorIds())
        {
            itemAndAncestors.Add(id);
        }

        var collectionFolders = libraryManager
            .GetCollectionFolders(item)
            .Select(folder => folder.Id)
            .ToHashSet();

        var releaseDate = item.PremiereDate
            ?? ancestors.Select(a => a.PremiereDate).FirstOrDefault(d => d.HasValue);

        return new RuleContext
        {
            CollectionFolderIds = collectionFolders,
            ItemAndAncestorIds = itemAndAncestors,
            Tags = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase),
            Genres = new HashSet<string>(genres, StringComparer.OrdinalIgnoreCase),
            Studios = new HashSet<string>(studios, StringComparer.OrdinalIgnoreCase),
            UserId = userId,
            Now = now ?? DateTime.Now,
            ReleaseDate = releaseDate
        };
    }

    /// <summary>
    /// Returns the item's tags/genres/studios merged with those of its ancestors
    /// (season, series, library, ...). Used by the rule matcher and by the config
    /// page to preview how many items a tag-based rule affects.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The merged, sorted values.</returns>
    public static (IReadOnlyList<string> Tags, IReadOnlyList<string> Genres, IReadOnlyList<string> Studios) GetMergedMetadata(BaseItem item)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var studios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in item.GetParents().Prepend(item))
        {
            AddAll(tags, source.Tags);
            AddAll(genres, source.Genres);
            AddAll(studios, source.Studios);
        }

        static IReadOnlyList<string> Sorted(HashSet<string> values)
            => values.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray();

        return (Sorted(tags), Sorted(genres), Sorted(studios));
    }

    private static void AddAll(ISet<string> target, string[]? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var value in source)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Add(value);
            }
        }
    }
}
