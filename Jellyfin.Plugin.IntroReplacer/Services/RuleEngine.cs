using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.IntroReplacer.Configuration;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// The result of evaluating the rule set for an item.
/// </summary>
/// <param name="AssetIds">The ordered intro asset ids to play.</param>
/// <param name="Placement">Whether the item's own intro is replaced or the custom intro is prepended.</param>
/// <param name="Frequency">How often the intro repeats (every time, after a wait, or once per session).</param>
/// <param name="RepeatAfterMinutes">The rule's wait time in minutes, used by <see cref="IntroFrequency.OncePerInterval"/>.</param>
/// <param name="RuleName">The name of the matched rule (for logging).</param>
public sealed record IntroSelection(
    IReadOnlyList<Guid> AssetIds,
    IntroPlacement Placement,
    IntroFrequency Frequency,
    int RepeatAfterMinutes,
    string RuleName);

/// <summary>
/// Evaluates the ordered rule set. First enabled rule whose conditions match wins.
/// An empty condition list matches all.
/// </summary>
public static class RuleEngine
{
    /// <summary>
    /// Selects the intro(s) to play for the given context.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <param name="context">The item snapshot to match against.</param>
    /// <param name="random">Random source (injectable for tests).</param>
    /// <returns>The selection, or <c>null</c> when no rule matches.</returns>
    public static IntroSelection? Select(PluginConfiguration configuration, RuleContext context, Random random)
    {
        var knownAssets = configuration.Assets.Select(a => a.Id).ToHashSet();

        foreach (var rule in configuration.Rules)
        {
            if (!rule.Enabled || !Matches(rule, context))
            {
                continue;
            }

            var candidates = rule.AssetIds.Where(knownAssets.Contains).ToList();
            var assetIds = rule.PlaybackMode == IntroPlaybackMode.SequentialAll
                ? candidates
                : PickOne(candidates, random);

            if (assetIds.Count == 0)
            {
                continue;
            }

            return new IntroSelection(assetIds, rule.Placement, rule.Frequency, rule.RepeatAfterMinutes, rule.Name);
        }

        return null;
    }

    /// <summary>
    /// Returns whether every non-empty condition of the rule matches the context.
    /// </summary>
    /// <param name="rule">The rule to test.</param>
    /// <param name="context">The item snapshot.</param>
    /// <returns><c>true</c> when the rule matches.</returns>
    public static bool Matches(IntroRule rule, RuleContext context)
    {
        if (ResolveTargetMode(rule) == IntroTargetMode.Targets)
        {
            if (rule.LibraryIds.Count > 0 && !rule.LibraryIds.Any(context.CollectionFolderIds.Contains))
            {
                return false;
            }

            // Libraries are active by default; an excluded library excludes its subtree.
            if (rule.ExcludedLibraryIds.Any(context.CollectionFolderIds.Contains))
            {
                return false;
            }

            // Item scope: empty target list means "all items matching the other conditions".
            // Otherwise the item (or one of its ancestors) must be an active target.
            if (rule.TargetItemIds.Count > 0)
            {
                if (!rule.TargetItemIds.Any(context.ItemAndAncestorIds.Contains))
                {
                    return false;
                }

                // Deactivated seasons/episodes exclude their whole subtree.
                if (rule.ExcludedItemIds.Any(context.ItemAndAncestorIds.Contains))
                {
                    return false;
                }
            }
        }
        else
        {
            if (rule.IncludeTags.Count > 0 &&
                !rule.IncludeTags.Any(t => context.Tags.Contains(t)))
            {
                return false;
            }

            if (rule.ExcludeTags.Count > 0 &&
                rule.ExcludeTags.Any(t => context.Tags.Contains(t)))
            {
                return false;
            }

            if (rule.Genres.Count > 0 && !rule.Genres.Any(g => context.Genres.Contains(g)))
            {
                return false;
            }

            if (rule.Studios.Count > 0 && !rule.Studios.Any(s => context.Studios.Contains(s)))
            {
                return false;
            }
        }

        if (rule.UserIds.Count > 0 && !rule.UserIds.Contains(context.UserId))
        {
            return false;
        }

        if (rule.DateRange is not null && !rule.DateRange.Contains(context.Now))
        {
            return false;
        }

        if (rule.ReleaseDateRange is not null &&
            (context.ReleaseDate is null || !rule.ReleaseDateRange.Contains(context.ReleaseDate.Value)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves the rule's targeting method. Rules saved before <c>TargetMode</c>
    /// existed have no explicit value: they are treated as tag-based when any tag,
    /// genre or studio condition is set, and target-tree-based otherwise.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>The targeting method.</returns>
    public static IntroTargetMode ResolveTargetMode(IntroRule rule)
    {
        if (rule.TargetMode.HasValue)
        {
            return rule.TargetMode.Value;
        }

        return rule.IncludeTags.Count > 0
            || rule.ExcludeTags.Count > 0
            || rule.Genres.Count > 0
            || rule.Studios.Count > 0
            ? IntroTargetMode.Tags
            : IntroTargetMode.Targets;
    }

    private static List<Guid> PickOne(List<Guid> candidates, Random random)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        return new List<Guid> { candidates[random.Next(candidates.Count)] };
    }
}
