using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.IntroReplacer.Services;

/// <summary>
/// Tracks custom intro playback so a rule can repeat it only after a wait time,
/// or only once per client session.
/// <para>
/// The wait-time mode needs no client knowledge: it simply remembers when an
/// intro last played for a user. The session mode keys on the Jellyfin session id
/// resolved from the current request; the plugin forwards client disconnects to
/// <see cref="NotifySessionEnded(string)"/>, which is what makes the intro play
/// again after the client reconnects.
/// </para>
/// </summary>
public sealed class IntroSessionTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, DateTime> _lastIntroAt = new();

    // Session ids that already saw the intro since they connected.
    private readonly HashSet<string> _playedSessions = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns whether the intro may play now under the wait-time rule, and records
    /// the time when it may.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="wait">How long to wait before the intro may play again.</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c> when the intro may play.</returns>
    public bool ShouldPlayAfterWait(Guid userId, TimeSpan wait, DateTime now)
    {
        lock (_lock)
        {
            if (_lastIntroAt.TryGetValue(userId, out var last) && now - last < wait)
            {
                return false;
            }

            _lastIntroAt[userId] = now;
            return true;
        }
    }

    /// <summary>
    /// Returns whether the intro may play now for a client session, and records the
    /// session when it may.
    /// </summary>
    /// <param name="sessionId">The Jellyfin session id of the requesting client.</param>
    /// <returns><c>true</c> when the intro may play.</returns>
    public bool ShouldPlayOncePerSession(string sessionId)
    {
        lock (_lock)
        {
            return _playedSessions.Add(sessionId);
        }
    }

    /// <summary>
    /// Forgets that the given session already saw the intro, so it plays again the
    /// next time the client connects. Call this when a client disconnects.
    /// </summary>
    /// <param name="sessionId">The Jellyfin session id that ended.</param>
    public void NotifySessionEnded(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        lock (_lock)
        {
            _playedSessions.Remove(sessionId);
        }
    }
}
