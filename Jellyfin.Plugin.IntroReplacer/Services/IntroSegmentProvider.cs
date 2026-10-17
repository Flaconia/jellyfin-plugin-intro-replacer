using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// Derives the item's own "Intro" media segment from its chapters.
/// <para>
/// Two reasons this exists:
/// 1. Jellyfin filters the segment list returned to clients by the segment
///    providers registered for the item's library. Segments we create by hand are
///    invisible unless a provider with the matching id is registered.
/// 2. It lets the scheduled "Media Segment Scan" task populate intros too.
/// </para>
/// It is purely additive: if another provider (e.g. Intro Skipper) already stores an
/// Intro segment, the on-demand path in <see cref="IntroSegmentHelper"/> leaves it alone.
/// </summary>
public sealed class IntroSegmentProvider : IMediaSegmentProvider
{
    /// <summary>The provider name; its hashed form is the stored segment provider id.</summary>
    public const string ProviderName = "Intro Replacer";

    /// <summary>The segment provider id Jellyfin computes for <see cref="ProviderName"/>.</summary>
    public static readonly string ProviderId =
        ProviderName.ToLowerInvariant().GetMD5().ToString("N", CultureInfo.InvariantCulture);

    private readonly ILibraryManager _libraryManager;
    private readonly IItemRepository _itemRepository;
    private readonly ILogger<IntroSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSegmentProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="itemRepository">Item repository (chapter access).</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public IntroSegmentProvider(
        ILibraryManager libraryManager,
        IItemRepository itemRepository,
        ILoggerFactory loggerFactory)
    {
        _libraryManager = libraryManager;
        _itemRepository = itemRepository;
        _logger = loggerFactory.CreateLogger<IntroSegmentProvider>();
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) => new(item is Video);

    /// <inheritdoc />
    public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MediaSegmentDto> empty = Array.Empty<MediaSegmentDto>();

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null)
        {
            return Task.FromResult(empty);
        }

        var segment = BuildSegment(item, _itemRepository);
        if (segment is null)
        {
            return Task.FromResult(empty);
        }

        _logger.LogDebug("Intro Replacer: chapter-based intro segment found for {Item}", item.Name);
        return Task.FromResult<IReadOnlyList<MediaSegmentDto>>(new[] { segment });
    }

    /// <summary>
    /// Builds an Intro segment for an item from its chapters, or <c>null</c>.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="itemRepository">Item repository (chapter access).</param>
    /// <returns>The segment, or <c>null</c>.</returns>
    public static MediaSegmentDto? BuildSegment(BaseItem item, IItemRepository itemRepository)
    {
        var range = IntroChapterDetector.Detect(itemRepository.GetChapters(item));
        if (range is null)
        {
            return null;
        }

        var start = range.Value.StartTicks;
        var end = range.Value.EndTicks >= 0 ? range.Value.EndTicks : item.RunTimeTicks ?? 0;
        if (end <= start)
        {
            return null;
        }

        return new MediaSegmentDto
        {
            ItemId = item.Id,
            Type = MediaSegmentType.Intro,
            StartTicks = start,
            EndTicks = end
        };
    }
}
