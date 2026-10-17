using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// Tracks the last time each user saw a custom intro so a rule can play it only
/// once per watching session. Jellyfin does not expose a client session id to
/// intro providers, so a session is approximated by an idle timeout.
/// </summary>
public sealed class IntroSessionTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, DateTime> _lastIntroAt = new();

    /// <summary>
    /// Returns whether the intro should play now, and records the time when it should.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="idleTimeout">How long without an intro starts a new session.</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c> when the intro should play.</returns>
    public bool ShouldPlay(Guid userId, TimeSpan idleTimeout, DateTime now)
    {
        lock (_lock)
        {
            if (_lastIntroAt.TryGetValue(userId, out var last) && now - last < idleTimeout)
            {
                return false;
            }

            _lastIntroAt[userId] = now;
            return true;
        }
    }
}
