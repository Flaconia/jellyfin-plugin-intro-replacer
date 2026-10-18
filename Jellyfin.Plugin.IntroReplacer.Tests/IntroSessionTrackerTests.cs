using System;
using Jellyfin.Plugin.IntroReplacer.Services;
using Xunit;

namespace Jellyfin.Plugin.IntroReplacer.Tests;

public class IntroSessionTrackerTests
{
    private static readonly Guid User1 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid User2 = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(30);

    [Fact]
    public void Wait_PlaysFirstTime_ThenSkipsWithinTheWait()
    {
        var tracker = new IntroSessionTracker();

        Assert.True(tracker.ShouldPlayAfterWait(User1, Wait, Now));
        Assert.False(tracker.ShouldPlayAfterWait(User1, Wait, Now.AddMinutes(5)));
        Assert.False(tracker.ShouldPlayAfterWait(User1, Wait, Now.AddMinutes(29)));
    }

    [Fact]
    public void Wait_PlaysAgainOnceTheWaitHasPassed()
    {
        var tracker = new IntroSessionTracker();

        Assert.True(tracker.ShouldPlayAfterWait(User1, Wait, Now));
        Assert.True(tracker.ShouldPlayAfterWait(User1, Wait, Now.AddMinutes(30)));
        Assert.False(tracker.ShouldPlayAfterWait(User1, Wait, Now.AddMinutes(59)));
        Assert.True(tracker.ShouldPlayAfterWait(User1, Wait, Now.AddMinutes(60)));
    }

    [Fact]
    public void Wait_IsTrackedPerUser()
    {
        var tracker = new IntroSessionTracker();

        Assert.True(tracker.ShouldPlayAfterWait(User1, Wait, Now));
        Assert.True(tracker.ShouldPlayAfterWait(User2, Wait, Now));
    }

    [Fact]
    public void Session_PlaysOncePerSession()
    {
        var tracker = new IntroSessionTracker();

        Assert.True(tracker.ShouldPlayOncePerSession("session-a"));
        Assert.False(tracker.ShouldPlayOncePerSession("session-a"));
        Assert.False(tracker.ShouldPlayOncePerSession("session-a"));
    }

    [Fact]
    public void Session_PlaysAgainAfterTheSessionEnds()
    {
        var tracker = new IntroSessionTracker();

        Assert.True(tracker.ShouldPlayOncePerSession("session-a"));
        Assert.False(tracker.ShouldPlayOncePerSession("session-a"));

        tracker.NotifySessionEnded("session-a");

        Assert.True(tracker.ShouldPlayOncePerSession("session-a"));
    }

    [Fact]
    public void Session_IsTrackedPerSession()
    {
        var tracker = new IntroSessionTracker();

        Assert.True(tracker.ShouldPlayOncePerSession("session-a"));
        Assert.True(tracker.ShouldPlayOncePerSession("session-b"));
        Assert.False(tracker.ShouldPlayOncePerSession("session-a"));
    }

    [Fact]
    public void Session_EndingAnotherSessionDoesNotResetThisOne()
    {
        var tracker = new IntroSessionTracker();

        Assert.True(tracker.ShouldPlayOncePerSession("session-a"));
        tracker.NotifySessionEnded("session-b");
        Assert.False(tracker.ShouldPlayOncePerSession("session-a"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Session_IgnoresEmptySessionEnded(string? sessionId)
    {
        var tracker = new IntroSessionTracker();

        tracker.NotifySessionEnded(sessionId);

        Assert.True(tracker.ShouldPlayOncePerSession("session-a"));
    }
}
