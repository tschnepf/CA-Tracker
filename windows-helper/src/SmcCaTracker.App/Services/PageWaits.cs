namespace SmcCaTracker.App.Services;

/// <summary>
/// Time limits for the hidden Helix and Procore pages, so a browser that gets stuck ends the sync with
/// a reason instead of hanging it.
/// </summary>
public static class PageWaits
{
    /// <summary>How long the browser may take to start, and to open its page.</summary>
    public static readonly TimeSpan Start = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long running a script may take. Running one only starts it (answers come back as messages), so
    /// a page that takes longer has stopped responding.
    /// </summary>
    public static readonly TimeSpan Script = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Waits for <paramref name="task"/>. Past <paramref name="timeout"/> a <see cref="TimeoutException"/>
    /// names the <paramref name="step"/> that got stuck; stopping (<paramref name="stop"/>) ends the wait at once.
    /// </summary>
    public static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, string step, CancellationToken stop = default)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeout, stop));
        stop.ThrowIfCancellationRequested();
        if (finished != task)
        {
            throw new TimeoutException($"The built-in browser got stuck {step}.");
        }

        return await task;
    }
}

/// <summary>A hidden page's browser stopped, or stopped responding; the next sync starts it afresh.</summary>
public sealed class PageCrashedException : Exception
{
    public PageCrashedException(string message)
        : base(message)
    {
    }
}
