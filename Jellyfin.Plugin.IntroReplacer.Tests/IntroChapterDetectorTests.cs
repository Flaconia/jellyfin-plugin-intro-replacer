using System.Collections.Generic;
using Jellyfin.Plugin.IntroReplacer.Services;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.IntroReplacer.Tests;

public class IntroChapterDetectorTests
{
    private const long Second = 10_000_000;

    private static ChapterInfo Chapter(string? name, double startSeconds)
        => new() { Name = name, StartPositionTicks = (long)(startSeconds * Second) };

    [Fact]
    public void NullOrEmptyReturnsNull()
    {
        Assert.Null(IntroChapterDetector.Detect(null));
        Assert.Null(IntroChapterDetector.Detect(new List<ChapterInfo>()));
    }

    [Fact]
    public void NamedIntroChapterIsDetected()
    {
        // Mirrors the real Pokemon rip: "Sigla d'apertura" (Italian for opening theme).
        var chapters = new List<ChapterInfo>
        {
            Chapter("Sigla d'apertura", 0),
            Chapter("Prologo", 89.25),
            Chapter("Titolo", 211.95),
            Chapter("Parte A", 219.25)
        };

        var range = IntroChapterDetector.Detect(chapters);

        Assert.NotNull(range);
        Assert.Equal(0, range!.Value.StartTicks);
        Assert.Equal((long)(89.25 * Second), range.Value.EndTicks);
    }

    [Fact]
    public void ClosingThemeIsNotTreatedAsIntro()
    {
        var chapters = new List<ChapterInfo>
        {
            Chapter("Parte A", 0),
            Chapter("Sigla di chiusura", 1261.3)
        };

        // "Parte A" is not an intro and the position fallback does not apply
        // (second chapter starts too late).
        Assert.Null(IntroChapterDetector.Detect(chapters));
    }

    [Fact]
    public void EnglishOpeningIsDetected()
    {
        var chapters = new List<ChapterInfo>
        {
            Chapter("Cold open", 0),
            Chapter("Opening", 30),
            Chapter("Episode", 120)
        };

        var range = IntroChapterDetector.Detect(chapters);

        Assert.NotNull(range);
        Assert.Equal(30 * Second, range!.Value.StartTicks);
        Assert.Equal(120 * Second, range.Value.EndTicks);
    }

    [Fact]
    public void OpTokenIsDetected()
    {
        var chapters = new List<ChapterInfo>
        {
            Chapter("Part 1", 0),
            Chapter("OP", 45),
            Chapter("Part 2", 135)
        };

        var range = IntroChapterDetector.Detect(chapters);

        Assert.NotNull(range);
        Assert.Equal(45 * Second, range!.Value.StartTicks);
    }

    [Fact]
    public void PositionFallbackForUnnamedChapters()
    {
        var chapters = new List<ChapterInfo>
        {
            Chapter(null, 0),
            Chapter(null, 60),
            Chapter(null, 1200)
        };

        var range = IntroChapterDetector.Detect(chapters);

        Assert.NotNull(range);
        Assert.Equal(0, range!.Value.StartTicks);
        Assert.Equal(60 * Second, range.Value.EndTicks);
    }

    [Fact]
    public void SingleChapterHasNoIntro()
    {
        Assert.Null(IntroChapterDetector.Detect(new List<ChapterInfo> { Chapter("Chapter 1", 0) }));
    }

    [Fact]
    public void IntroAsLastChapterHasUnknownEnd()
    {
        var chapters = new List<ChapterInfo>
        {
            Chapter("Intro", 300)
        };

        var range = IntroChapterDetector.Detect(chapters);

        Assert.NotNull(range);
        Assert.Equal(300 * Second, range!.Value.StartTicks);
        Assert.Equal(-1, range.Value.EndTicks);
    }
}
