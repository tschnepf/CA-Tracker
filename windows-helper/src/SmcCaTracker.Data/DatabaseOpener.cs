using Microsoft.Data.Sqlite;

namespace SmcCaTracker.Data;

/// <summary>Why the database could, or could not, be opened.</summary>
public enum DatabaseOpenOutcome
{
    /// <summary>The database is open and passed a quick integrity check.</summary>
    Opened,

    /// <summary>The file is not a usable SQLite database (corrupt or not a database): the only case where it may be rebuilt.</summary>
    Corrupt,

    /// <summary>Another program holds the file; nothing is wrong with the data.</summary>
    InUse,

    /// <summary>Something else (permissions, a missing folder, …) stopped it opening.</summary>
    Failed
}

public sealed record DatabaseOpenResult(SqlitePersistence? Database, DatabaseOpenOutcome Outcome, Exception? Error);

/// <summary>
/// Opens tracker.db carefully: a file that is merely locked by another program is retried and then
/// reported, and only a file SQLite itself says is corrupt is treated as one to set aside. Everything
/// else is a plain failure — never a reason to throw the data away.
/// </summary>
public static class DatabaseOpener
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteCorrupt = 11;
    private const int SqliteCantOpen = 14;
    private const int SqliteNotADb = 26;

    // Windows: ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION wrapped as an IOException.
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);

    public static DatabaseOpenResult Open(string path, int attempts = 5, TimeSpan? delay = null)
    {
        var wait = delay ?? TimeSpan.FromMilliseconds(500);
        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, attempts); attempt++)
        {
            SqlitePersistence? database = null;
            try
            {
                database = new SqlitePersistence(path);
                var check = QuickCheck(database);
                if (check is not null)
                {
                    database.Dispose();
                    return new DatabaseOpenResult(null, DatabaseOpenOutcome.Corrupt,
                        new SqliteException($"PRAGMA quick_check: {check}", SqliteCorrupt));
                }

                return new DatabaseOpenResult(database, DatabaseOpenOutcome.Opened, null);
            }
            catch (Exception error)
            {
                database?.Dispose();
                last = error;
                if (IsCorruption(error))
                {
                    return new DatabaseOpenResult(null, DatabaseOpenOutcome.Corrupt, error);
                }

                if (!IsInUse(error))
                {
                    return new DatabaseOpenResult(null, DatabaseOpenOutcome.Failed, error);
                }

                if (attempt < attempts)
                {
                    Thread.Sleep(wait);
                }
            }
        }

        return new DatabaseOpenResult(null, DatabaseOpenOutcome.InUse, last);
    }

    /// <summary>True only when SQLite says the file isn't a usable database.</summary>
    public static bool IsCorruption(Exception error) =>
        error is SqliteException { SqliteErrorCode: SqliteCorrupt or SqliteNotADb };

    /// <summary>
    /// True when another program holds the file (a lock or a sharing violation), which clears when it lets
    /// go. SQLITE_CANTOPEN counts too: SQLite reports a sharing violation on Windows that way, and the folder
    /// was created before opening, so "can't open" an existing file means held or denied, never corrupt.
    /// </summary>
    public static bool IsInUse(Exception error) => error switch
    {
        SqliteException { SqliteErrorCode: SqliteBusy or SqliteLocked or SqliteCantOpen } => true,
        IOException io => IsSharingViolation(io),
        _ => false
    };

    private static bool IsSharingViolation(Exception? error) =>
        error is IOException { HResult: SharingViolation or LockViolation };

    /// <summary>Null when the database passes; otherwise what SQLite reported.</summary>
    private static string? QuickCheck(SqlitePersistence database)
    {
        using var command = database.Command("PRAGMA quick_check;", null);
        using var reader = command.ExecuteReader();
        var problems = new List<string>();
        while (reader.Read())
        {
            var line = reader.GetString(0);
            if (!string.Equals(line, "ok", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(line);
            }
        }

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    /// <summary>
    /// Keeps a copy of a corrupt database (and its -wal/-shm) as <c>tracker.db.&lt;stamp&gt;.bad</c>, then clears
    /// the way for a fresh one. Returns the path of the copy.
    /// </summary>
    public static string SetAside(string path, DateTime now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var copy = $"{path}.{stamp}.bad";
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = path + suffix;
            if (!File.Exists(file))
            {
                continue;
            }

            File.Copy(file, copy + suffix, overwrite: true);
            File.Delete(file);
        }

        return copy;
    }
}
