using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.IntroReplacer.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// Helpers for manipulating an item's Intro media segments according to a rule's
/// <see cref="IntroPlacement"/>.
/// </summary>
public static class IntroSegmentHelper
{
    /// <summary>
    /// Applies the rule's placement to the item's own intro.
    /// <list type="bullet">
    /// <item><see cref="IntroPlacement.Replace"/>: ensure an Intro segment exists so
    /// clients offer to skip the item's original intro. Existing segments (e.g. from
    /// Intro Skipper) are left untouched; if none exist, one is derived from chapters.</item>
    /// <item><see cref="IntroPlacement.Prepend"/>: remove existing Intro segments so the
    /// item plays from the very beginning after the custom intro.</item>
    /// </list>
    /// </summary>
    /// <param name="segmentManager">The media segment manager.</param>
    /// <param name="itemRepository">Item repository (chapter access).</param>
    /// <param name="item">The item.</param>
    /// <param name="placement">The rule's placement mode.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>A task.</returns>
    public static async Task ApplyPlacementAsync(
        IMediaSegmentManager segmentManager,
        IItemRepository itemRepository,
        BaseItem item,
        IntroPlacement placement,
        ILogger logger)
    {
        if (placement == IntroPlacement.Prepend)
        {
            await RemoveIntroSegmentsAsync(segmentManager, item, logger).ConfigureAwait(false);
            return;
        }

        var existing = await segmentManager
            .GetSegmentsAsync(item, new[] { MediaSegmentType.Intro }, filterByProvider: false)
            .ConfigureAwait(false);

        if (existing.Any())
        {
            return;
        }

        var segment = IntroSegmentProvider.BuildSegment(item, itemRepository);
        if (segment is null)
        {
            logger.LogInformation(
                "IntroReplacer: no chapter-based intro found for '{Item}'. Automatic audio detection is not implemented; install Intro Skipper if you need it for items without chapters.",
                item.Name);
            return;
        }

        await segmentManager
            .CreateSegmentAsync(segment, IntroSegmentProvider.ProviderId)
            .ConfigureAwait(false);

        logger.LogInformation(
            "IntroReplacer: created a chapter-based Intro segment ({Start}-{End} ticks) for '{Item}'",
            segment.StartTicks,
            segment.EndTicks,
            item.Name);
    }

    /// <summary>
    /// Removes all Intro media segments from an item so the item is played from
    /// the very beginning (used when a rule opts out of replacing the original intro).
    /// </summary>
    /// <param name="segmentManager">The media segment manager.</param>
    /// <param name="item">The item.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>A task.</returns>
    public static async Task RemoveIntroSegmentsAsync(
        IMediaSegmentManager segmentManager,
        BaseItem item,
        ILogger logger)
    {
        var segments = await segmentManager
            .GetSegmentsAsync(item, new[] { MediaSegmentType.Intro }, filterByProvider: false)
            .ConfigureAwait(false);

        foreach (var segment in segments)
        {
            logger.LogDebug("Removing Intro segment {SegmentId} from {ItemName}", segment.Id, item.Name);
            await segmentManager.DeleteSegmentAsync(segment.Id).ConfigureAwait(false);
        }
    }
}
