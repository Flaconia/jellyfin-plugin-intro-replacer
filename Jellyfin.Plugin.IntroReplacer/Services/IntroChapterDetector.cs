using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// A detected intro time range. <see cref="EndTicks"/> is <c>-1</c> when the end is
/// unknown (no following chapter), in which case the item runtime should be used.
/// </summary>
/// <param name="StartTicks">Start of the intro.</param>
/// <param name="EndTicks">End of the intro, or -1 when it must fall back to the item runtime.</param>
public readonly record struct IntroRange(long StartTicks, long EndTicks);

/// <summary>
/// Derives the item's own intro range from its embedded chapters. This is a
/// best-effort, offline heuristic: it never touches the media file. It is used to
/// create an "Intro" media segment so clients offer to skip the original intro,
/// when no dedicated segment provider (e.g. Intro Skipper) already did.
/// </summary>
public static class IntroChapterDetector
{
    // Phrases that, when found in a chapter name, mark it as the intro. Kept
    // multilingual because Jellyfin chapter names come from the release.
    private static readonly string[] IntroPhrases =
    {
        "intro", "opening", "opening theme", "sigla d'apertura", "sigla di apertura",
        "apertura", "vorspann", "abertura", "cabecera", "tema de apertura",
        "générique début", "generique debut", "générique de début"
    };

    // Tokens that only count when the whole (trimmed) name matches.
    private static readonly string[] IntroTokens = { "op", "op.", "opening", "int" };

    // Phrases that mark the intro's counterpart (closing/credits), so we never
    // mistake "Sigla di chiusura" for the opening.
    private static readonly string[] OutroPhrases =
    {
        "outro", "ending", "credits", "closing", "sigla di chiusura", "sigla di chiusa",
        "chiusura", "abspann", "générique de fin", "generique de fin", "cierre"
    };

    /// <summary>
    /// Finds the intro range in a chapter list, or <c>null</c> when none is found.
    /// </summary>
    /// <param name="chapters">The item's chapters, ordered by start.</param>
    /// <returns>The detected range, or <c>null</c>.</returns>
    public static IntroRange? Detect(IReadOnlyList<ChapterInfo>? chapters)
    {
        if (chapters is null || chapters.Count == 0)
        {
            return null;
        }

        var ordered = chapters.OrderBy(c => c.StartPositionTicks).ToList();

        var index = FindIntroIndex(ordered);
        if (index < 0)
        {
            return null;
        }

        var start = ordered[index].StartPositionTicks;
        var end = index + 1 < ordered.Count ? ordered[index + 1].StartPositionTicks : -1;
        return end <= start ? new IntroRange(start, -1) : new IntroRange(start, end);
    }

    private static int FindIntroIndex(IReadOnlyList<ChapterInfo> chapters)
    {
        // 1. Name-based match (highest confidence).
        for (var i = 0; i < chapters.Count; i++)
        {
            var name = (chapters[i].Name ?? string.Empty).Trim();
            if (Matches(OutroPhrases, name))
            {
                continue;
            }

            if (Matches(IntroPhrases, name) || IntroTokens.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        // 2. Position-based fallback: a first chapter at 0:00 followed by another
        //    chapter starting within a few minutes is almost always an opening.
        if (chapters.Count >= 2)
        {
            var first = chapters[0];
            var second = chapters[1];
            var maxIntroTicks = TimeSpan.FromMinutes(4).Ticks;
            if (first.StartPositionTicks == 0
                && second.StartPositionTicks > 0
                && second.StartPositionTicks <= maxIntroTicks)
            {
                return 0;
            }
        }

        return -1;
    }

    private static bool Matches(IEnumerable<string> phrases, string name)
    {
        return name.Length > 0
            && phrases.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
