namespace SmcCaTracker.Core.Procore;

/// <summary>
/// Procore asked CA Tracker to slow down (429, too many requests) or to come back later (503), or its
/// rate limit is nearly used up: nothing more is asked of Procore until the time it gave. One is shared
/// by background syncs, finding projects and downloads, so none of them keeps asking while another waits.
/// </summary>
public sealed class ProcoreThrottle
{
    /// <summary>How long to wait when Procore doesn't say.</summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan ShortestWait = TimeSpan.FromMinutes(1);

    /// <summary>A longer wait than this is taken as a mistake in what Procore sent.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromHours(12);

    private readonly object gate = new();
    private DateTimeOffset until = DateTimeOffset.MinValue;

    /// <summary>The app's one throttle.</summary>
    public static ProcoreThrottle Shared { get; } = new();

    /// <summary>Procore's answers that mean "slow down" or "come back later".</summary>
    public static bool IsSlowDown(int status) => status is 429 or 503;

    /// <summary>
    /// True when less than 5% (and at least 50 requests) of Procore's stated rate limit is left, so the
    /// rest of a sync would likely be refused.
    /// </summary>
    public static bool NearlyUsedUp(long limit, long remaining) => limit > 0 && remaining < Math.Max(50, limit / 20);

    /// <summary>Until when nothing is asked of Procore, or null when it may be asked now.</summary>
    public DateTimeOffset? WaitingUntil(DateTimeOffset now)
    {
        lock (gate)
        {
            return until > now ? until : null;
        }
    }

    /// <summary>
    /// Waits as long as Procore asked (<paramref name="retryAfterSeconds"/>), kept between
    /// <see cref="ShortestWait"/> and <see cref="LongestWait"/>, or <see cref="DefaultWait"/> when it
    /// didn't say. A wait already longer is kept. Returns until when.
    /// </summary>
    public DateTimeOffset SlowDown(DateTimeOffset now, double? retryAfterSeconds)
    {
        var wait = retryAfterSeconds is { } seconds && seconds >= 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds)
            ? TimeSpan.FromSeconds(Math.Min(seconds, LongestWait.TotalSeconds))
            : DefaultWait;
        wait = wait < ShortestWait ? ShortestWait : wait > LongestWait ? LongestWait : wait;
        lock (gate)
        {
            if (now + wait > until)
            {
                until = now + wait;
            }

            return until;
        }
    }

    /// <summary>Lets Procore be asked again at once (tests).</summary>
    public void Clear()
    {
        lock (gate)
        {
            until = DateTimeOffset.MinValue;
        }
    }

    /// <summary>What the app says while it waits.</summary>
    public static string WaitText(DateTimeOffset until) =>
        $"Procore asked CA Tracker to slow down, so it won't ask again until {until.ToLocalTime():h:mm tt}.";
}
