using System;
using System.Collections.Generic;
using Jellyfin.Plugin.IntroReplacer.Configuration;
using Jellyfin.Plugin.IntroReplacer.Services;
using Xunit;

namespace Jellyfin.Plugin.IntroReplacer.Tests;

public class RuleEngineTests
{
    private static readonly Guid AssetA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AssetB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Library1 = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Series1 = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid Season1 = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid Episode1 = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid User1 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static PluginConfiguration Config(params IntroRule[] rules)
    {
        return new PluginConfiguration
        {
            Assets = new List<IntroAsset>
            {
                new() { Id = AssetA, Name = "A", FileName = "a.mp4", Enabled = true },
                new() { Id = AssetB, Name = "B", FileName = "b.mp4", Enabled = true }
            },
            Rules = new List<IntroRule>(rules)
        };
    }

    private static RuleContext Context() => new()
    {
        CollectionFolderIds = new HashSet<Guid> { Library1 },
        ItemAndAncestorIds = new HashSet<Guid> { Series1 },
        Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "anime" },
        Genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action" },
        Studios = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pixar" },
        UserId = User1,
        Now = new DateTime(2026, 1, 1)
    };

    private static RuleContext EpisodeContext() => new()
    {
        CollectionFolderIds = new HashSet<Guid> { Library1 },
        ItemAndAncestorIds = new HashSet<Guid> { Episode1, Season1, Series1 },
        Tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "anime" },
        Genres = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Action" },
        Studios = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Pixar" },
        UserId = User1,
        Now = new DateTime(2026, 1, 1)
    };

    [Fact]
    public void EmptyRuleMatchesEverything()
    {
        Assert.True(RuleEngine.Matches(new IntroRule(), Context()));
    }

    [Fact]
    public void LibraryConditionMustMatch()
    {
        var rule = new IntroRule { LibraryIds = { Guid.NewGuid() } };
        Assert.False(RuleEngine.Matches(rule, Context()));

        rule.LibraryIds = new List<Guid> { Library1 };
        Assert.True(RuleEngine.Matches(rule, Context()));
    }

    [Fact]
    public void TargetItemConditionMatchesAncestors()
    {
        var rule = new IntroRule { TargetItemIds = { Series1 } };
        Assert.True(RuleEngine.Matches(rule, EpisodeContext()));
    }

    [Fact]
    public void ExcludedSeasonBlocksItsEpisodes()
    {
        var rule = new IntroRule
        {
            TargetItemIds = { Series1 },
            ExcludedItemIds = { Season1 }
        };

        Assert.False(RuleEngine.Matches(rule, EpisodeContext()));
    }

    [Fact]
    public void ExcludedEpisodeBlocksOnlyThatEpisode()
    {
        var rule = new IntroRule
        {
            TargetItemIds = { Series1 },
            ExcludedItemIds = { Episode1 }
        };

        Assert.False(RuleEngine.Matches(rule, EpisodeContext()));

        var otherEpisode = new RuleContext
        {
            ItemAndAncestorIds = new HashSet<Guid> { Guid.NewGuid(), Season1, Series1 }
        };

        Assert.True(RuleEngine.Matches(rule, otherEpisode));
    }

    [Fact]
    public void IncludeTagsRequireAnyMatch()
    {
        Assert.True(RuleEngine.Matches(new IntroRule { IncludeTags = { "ANIME" } }, Context()));
        Assert.False(RuleEngine.Matches(new IntroRule { IncludeTags = { "horror" } }, Context()));
    }

    [Fact]
    public void ExcludeTagsBlockMatch()
    {
        Assert.False(RuleEngine.Matches(new IntroRule { ExcludeTags = { "anime" } }, Context()));
    }

    [Fact]
    public void UserConditionMustMatch()
    {
        Assert.True(RuleEngine.Matches(new IntroRule { UserIds = { User1 } }, Context()));
        Assert.False(RuleEngine.Matches(new IntroRule { UserIds = { Guid.NewGuid() } }, Context()));
    }

    [Fact]
    public void FirstMatchingRuleWins()
    {
        var first = new IntroRule { Name = "first", AssetIds = { AssetA }, IncludeTags = { "anime" } };
        var second = new IntroRule { Name = "second", AssetIds = { AssetB } };

        var selection = RuleEngine.Select(Config(first, second), Context(), new Random(1));

        Assert.NotNull(selection);
        Assert.Equal("first", selection!.RuleName);
        Assert.Equal(new[] { AssetA }, selection.AssetIds);
    }

    [Fact]
    public void DisabledRuleIsSkipped()
    {
        var disabled = new IntroRule { Name = "disabled", Enabled = false, AssetIds = { AssetA } };
        var fallback = new IntroRule { Name = "fallback", AssetIds = { AssetB } };

        var selection = RuleEngine.Select(Config(disabled, fallback), Context(), new Random(1));

        Assert.Equal("fallback", selection!.RuleName);
    }

    [Fact]
    public void SequentialAllReturnsEveryEnabledAssetInOrder()
    {
        var rule = new IntroRule { AssetIds = { AssetB, AssetA }, PlaybackMode = IntroPlaybackMode.SequentialAll };

        var selection = RuleEngine.Select(Config(rule), Context(), new Random(1));

        Assert.Equal(new[] { AssetB, AssetA }, selection!.AssetIds);
    }

    [Fact]
    public void PlacementAndFrequencyAreCarriedThrough()
    {
        var rule = new IntroRule
        {
            AssetIds = { AssetA },
            Placement = IntroPlacement.Prepend,
            Frequency = IntroFrequency.OncePerSession
        };

        var selection = RuleEngine.Select(Config(rule), Context(), new Random(1));

        Assert.Equal(IntroPlacement.Prepend, selection!.Placement);
        Assert.Equal(IntroFrequency.OncePerSession, selection.Frequency);
    }

    [Fact]
    public void DisabledAssetsAreNotSelected()
    {
        var config = Config(new IntroRule { AssetIds = { AssetA }, PlaybackMode = IntroPlaybackMode.SequentialAll });
        config.Assets[0].Enabled = false;

        var selection = RuleEngine.Select(config, Context(), new Random(1));

        Assert.Null(selection);
    }

    [Fact]
    public void NoRuleReturnsNull()
    {
        Assert.Null(RuleEngine.Select(Config(), Context(), new Random(1)));
    }

    [Fact]
    public void YearlyDateRangeWorks()
    {
        var range = new DateRange
        {
            Start = new DateTime(2000, 12, 1),
            End = new DateTime(2000, 12, 31),
            Repeat = RepeatMode.Yearly
        };

        Assert.True(range.Contains(new DateTime(2026, 12, 15)));
        Assert.False(range.Contains(new DateTime(2026, 6, 15)));
    }
}
