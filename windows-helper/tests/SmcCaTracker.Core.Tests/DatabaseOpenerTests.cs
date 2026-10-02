using Microsoft.Data.Sqlite;
using SmcCaTracker.Data;

namespace SmcCaTracker.Core.Tests;

public class DatabaseOpenerTests
{
    [Fact]
    public void AHealthyDatabaseOpens()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Root, "tracker.db");
        var opened = DatabaseOpener.Open(file, attempts: 1);
        Assert.Equal(DatabaseOpenOutcome.Opened, opened.Outcome);
        Assert.NotNull(opened.Database);
        opened.Database!.Dispose();

        var again = DatabaseOpener.Open(file, attempts: 1);
        Assert.Equal(DatabaseOpenOutcome.Opened, again.Outcome);
        again.Database!.Dispose();
    }

    [Fact]
    public void AFileOfRandomBytesIsCorruptNotInUse()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Root, "tracker.db");
        var junk = new byte[8192];
        Random.Shared.NextBytes(junk);
        File.WriteAllBytes(file, junk);

        var opened = DatabaseOpener.Open(file, attempts: 1);
        Assert.Equal(DatabaseOpenOutcome.Corrupt, opened.Outcome);
        Assert.Null(opened.Database);
        Assert.True(DatabaseOpener.IsCorruption(opened.Error!));
        Assert.True(File.Exists(file), "deciding it's corrupt must not touch the file");

        var copy = DatabaseOpener.SetAside(file, new DateTime(2026, 10, 1, 8, 30, 0));
        Assert.Equal(file + ".20261001-083000.bad", copy);
        Assert.Equal(junk, File.ReadAllBytes(copy));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void ADatabaseHeldOpenByAnotherProgramIsReportedInUseAndLeftAlone()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Root, "tracker.db");
        DatabaseOpener.Open(file, attempts: 1).Database!.Dispose();
        var before = File.ReadAllBytes(file);

        using (var holder = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var opened = DatabaseOpener.Open(file, attempts: 2, delay: TimeSpan.FromMilliseconds(10));
            Assert.Equal(DatabaseOpenOutcome.InUse, opened.Outcome);
            Assert.Null(opened.Database);
            Assert.False(DatabaseOpener.IsCorruption(opened.Error!));
        }

        Assert.True(File.Exists(file));
        Assert.Empty(Directory.GetFiles(temp.Root, "*.bad*"));
        Assert.Equal(before, File.ReadAllBytes(file));

        var afterwards = DatabaseOpener.Open(file, attempts: 1);
        Assert.Equal(DatabaseOpenOutcome.Opened, afterwards.Outcome);
        afterwards.Database!.Dispose();
    }

    [Fact]
    public void ABusyOrLockedSqliteErrorCountsAsInUseNotCorruption()
    {
        Assert.True(DatabaseOpener.IsInUse(new SqliteException("database is locked", 5)));
        Assert.True(DatabaseOpener.IsInUse(new SqliteException("database table is locked", 6)));
        Assert.False(DatabaseOpener.IsCorruption(new SqliteException("database is locked", 5)));
        Assert.True(DatabaseOpener.IsCorruption(new SqliteException("database disk image is malformed", 11)));
        Assert.True(DatabaseOpener.IsCorruption(new SqliteException("file is not a database", 26)));
        Assert.False(DatabaseOpener.IsInUse(new UnauthorizedAccessException()));
    }
}
