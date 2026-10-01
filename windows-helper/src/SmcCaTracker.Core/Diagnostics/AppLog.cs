using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace SmcCaTracker.Core.Diagnostics;

/// <summary>
/// The app's log: how long the steps that can be slow took (loading, folder checks, comparing, list
/// updates, syncs) and any errors. Lines are written in the background to
/// %LOCALAPPDATA%\SMC-CA-Tracker\logs, one file per day, kept for two weeks. A step slower than its
/// budget is marked SLOW, so a sluggish step shows up in the log instead of being guessed at.
/// </summary>
public static class AppLog
{
    private static readonly BlockingCollection<string> Pending = new(new ConcurrentQueue<string>(), 20_000);
    private static readonly object Gate = new();
    private static string? directory;
    private static Thread? writer;

    /// <summary>Starts writing to <paramref name="logDirectory"/>. Before this, lines only go to the debugger.</summary>
    public static void Start(string logDirectory)
    {
        lock (Gate)
        {
            if (writer is not null)
            {
                return;
            }

            directory = logDirectory;
            try
            {
                Directory.CreateDirectory(logDirectory);
                DeleteOld(logDirectory, TimeSpan.FromDays(14));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                directory = null;
                return;
            }

            writer = new Thread(Run) { IsBackground = true, Name = "CA Tracker log" };
            writer.Start();
        }
    }

    /// <summary>Where the log files are, once started.</summary>
    public static string? LogDirectory => directory;

    public static void Info(string area, string message) => Write("INFO", area, message);

    public static void Warn(string area, string message) => Write("WARN", area, message);

    public static void Error(string area, string message, Exception? error = null) =>
        Write("ERROR", area, error is null ? message : $"{message}: {error.GetType().Name}: {error.Message}");

    /// <summary>
    /// Times a step and logs it when disposed: <c>using (AppLog.Time("compare", "3 projects")) { … }</c>.
    /// Steps over <paramref name="budgetMs"/> are marked SLOW.
    /// </summary>
    public static Timing Time(string step, string? detail = null, int budgetMs = 100) => new(step, detail, budgetMs);

    /// <summary>Waits briefly for queued lines to be written (used when the app closes).</summary>
    public static void Flush(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }
    }

    internal static void Write(string level, string area, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} [{area}] {message}";
        if (writer is null)
        {
            Debug.WriteLine(line);
            return;
        }

        Pending.TryAdd(line);
    }

    private static void Run()
    {
        foreach (var line in Pending.GetConsumingEnumerable())
        {
            var batch = new StringBuilder(line).AppendLine();
            while (batch.Length < 64_000 && Pending.TryTake(out var more))
            {
                batch.AppendLine(more);
            }

            try
            {
                var path = Path.Combine(directory!, $"ca-tracker-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, batch.ToString(), Encoding.UTF8);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop the app.
            }
        }
    }

    private static void DeleteOld(string logDirectory, TimeSpan age)
    {
        foreach (var file in new DirectoryInfo(logDirectory).EnumerateFiles("ca-tracker-*.log"))
        {
            if (DateTime.UtcNow - file.LastWriteTimeUtc > age)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

/// <summary>A running timing from <see cref="AppLog.Time"/>; logs its duration when disposed.</summary>
public readonly struct Timing : IDisposable
{
    private readonly string step;
    private readonly string? detail;
    private readonly int budgetMs;
    private readonly long started;

    public Timing(string step, string? detail, int budgetMs)
    {
        this.step = step;
        this.detail = detail;
        this.budgetMs = budgetMs;
        started = Stopwatch.GetTimestamp();
    }

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(started);

    public void Dispose()
    {
        if (step is null)
        {
            return;
        }

        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        AppLog.Write(milliseconds > budgetMs ? "SLOW" : "TIME", step,
            detail is null ? $"{milliseconds:0} ms" : $"{milliseconds:0} ms · {detail}");
    }
}
