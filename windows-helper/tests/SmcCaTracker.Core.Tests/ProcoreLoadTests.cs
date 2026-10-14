using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Data;

namespace SmcCaTracker.Core.Tests;

public class ProcoreThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 0, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void NothingIsAskedUntilTheTimeProcoreGave()
    {
        var throttle = new ProcoreThrottle();
        Assert.Null(throttle.WaitingUntil(Now));

        var until = throttle.SlowDown(Now, 600);

        Assert.Equal(Now.AddMinutes(10), until);
        Assert.Equal(until, throttle.WaitingUntil(Now.AddMinutes(5)));
        Assert.Null(throttle.WaitingUntil(Now.AddMinutes(10)));
    }

    [Fact]
    public void WithoutATimeFromProcoreItWaitsHalfAnHour()
    {
        Assert.Equal(Now + ProcoreThrottle.DefaultWait, new ProcoreThrottle().SlowDown(Now, null));
        Assert.Equal(Now + ProcoreThrottle.DefaultWait, new ProcoreThrottle().SlowDown(Now, double.NaN));
        Assert.Equal(Now + ProcoreThrottle.DefaultWait, new ProcoreThrottle().SlowDown(Now, -5));
    }

    [Fact]
    public void TheWaitIsKeptBetweenAMinuteAndTwelveHours()
    {
        Assert.Equal(Now.AddMinutes(1), new ProcoreThrottle().SlowDown(Now, 0));
        Assert.Equal(Now.AddHours(12), new ProcoreThrottle().SlowDown(Now, 1e12));
    }

    [Fact]
    public void ALongerWaitAlreadyGivenIsKept()
    {
        var throttle = new ProcoreThrottle();
        throttle.SlowDown(Now, 3600);

        Assert.Equal(Now.AddHours(1), throttle.SlowDown(Now, 60));
        Assert.Equal(Now.AddHours(2), throttle.SlowDown(Now, 7200));
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(500, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void OnlyTooManyRequestsAndComeBackLaterMeanSlowDown(int status, bool slowDown) =>
        Assert.Equal(slowDown, ProcoreThrottle.IsSlowDown(status));

    [Theory]
    [InlineData(100_000, 4_999, true)]
    [InlineData(100_000, 5_000, false)]
    [InlineData(3_600, 49, true)]
    [InlineData(3_600, 200, false)]
    [InlineData(0, 0, false)]
    public void TheRateLimitIsNearlyUsedUpBelowFivePercent(long limit, long remaining, bool nearlyUsedUp) =>
        Assert.Equal(nearlyUsedUp, ProcoreThrottle.NearlyUsedUp(limit, remaining));
}

public class ProcoreReadPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 0, 0, TimeSpan.FromHours(-5));
    private static readonly TimeSpan Every12Hours = TimeSpan.FromHours(12);

    [Fact]
    public void AProjectIsReadInFullTheFirstTime()
    {
        Assert.Equal(ProcoreReadKind.Full, ProcoreReadPlan.For(false, Now.AddHours(-1), null, Now, Every12Hours));
        Assert.Equal(ProcoreReadKind.Full, ProcoreReadPlan.For(true, null, null, Now, Every12Hours));
    }

    [Fact]
    public void AfterAFullReadOnlyChangesAreRead()
    {
        // A restart doesn't matter: the time comes from the sync history.
        Assert.Equal(ProcoreReadKind.Changes, ProcoreReadPlan.For(true, Now.AddHours(-2), null, Now, Every12Hours));
    }

    [Fact]
    public void CountsAreComparedEveryTwelveHours()
    {
        Assert.Equal(ProcoreReadKind.ChangesAndCount, ProcoreReadPlan.For(true, Now.AddHours(-13), null, Now, Every12Hours));
        Assert.Equal(ProcoreReadKind.Changes, ProcoreReadPlan.For(true, Now.AddHours(-13), Now.AddHours(-1), Now, Every12Hours));
        // A count check from before the last full read doesn't count.
        Assert.Equal(ProcoreReadKind.ChangesAndCount, ProcoreReadPlan.For(true, Now.AddHours(-13), Now.AddHours(-20), Now, Every12Hours));
    }

    [Fact]
    public void AProjectIsReadInFullOnceAWeekWhateverItsCounts()
    {
        Assert.Equal(ProcoreReadKind.Full, ProcoreReadPlan.For(true, Now.AddDays(-7), Now.AddHours(-1), Now, Every12Hours));
        Assert.Equal(ProcoreReadKind.Changes, ProcoreReadPlan.For(true, Now.AddDays(-6), Now.AddHours(-1), Now, Every12Hours));
    }

    [Fact]
    public void AFullReadDatedInTheFutureIsNotTrusted()
    {
        Assert.Equal(ProcoreReadKind.Full, ProcoreReadPlan.For(true, Now.AddHours(3), null, Now, Every12Hours));
    }

    [Fact]
    public void CountsThatDifferFromTheSavedListAreNamed()
    {
        var snapshot = new ProviderSnapshot
        {
            Provider = Providers.Procore,
            ProjectId = "9",
            Items =
            {
                new SnapshotItem { ItemType = ItemTypes.Rfi, ItemId = "1" },
                new SnapshotItem { ItemType = ItemTypes.Rfi, ItemId = "2" },
                new SnapshotItem { ItemType = ItemTypes.Rfi, ItemId = "3" },
                new SnapshotItem { ItemType = ItemTypes.Submittal, ItemId = "4" },
                new SnapshotItem { ItemType = ItemTypes.Submittal, ItemId = "5" }
            }
        };

        Assert.Empty(ProcoreReadPlan.Mismatched(snapshot, new Dictionary<string, int?> { [ItemTypes.Rfi] = 3, [ItemTypes.Submittal] = 2 }));
        // One submittal removed in Procore.
        Assert.Equal(new[] { ItemTypes.Submittal },
            ProcoreReadPlan.Mismatched(snapshot, new Dictionary<string, int?> { [ItemTypes.Rfi] = 3, [ItemTypes.Submittal] = 1 }));
        // One RFI that appeared without changing (e.g. access given to it).
        Assert.Equal(new[] { ItemTypes.Rfi }, ProcoreReadPlan.Mismatched(snapshot, new Dictionary<string, int?> { [ItemTypes.Rfi] = 4 }));
        // No count from Procore: nothing to compare.
        Assert.Empty(ProcoreReadPlan.Mismatched(snapshot, new Dictionary<string, int?> { [ItemTypes.Rfi] = null, [ItemTypes.Submittal] = null }));
    }
}

public class SyncScheduleTests
{
    [Theory]
    [InlineData(6, 59, 180)]
    [InlineData(7, 0, 60)]
    [InlineData(12, 30, 60)]
    [InlineData(16, 59, 60)]
    [InlineData(17, 0, 180)]
    [InlineData(23, 0, 180)]
    [InlineData(0, 0, 180)]
    public void HourlyFrom7To5AndEveryThreeHoursOtherwise(int hour, int minute, int minutes)
    {
        var sync = new SyncSettings();
        Assert.Equal(TimeSpan.FromMinutes(minutes), sync.IntervalAt(new DateTime(2026, 10, 8, hour, minute, 0)));
    }

    [Theory]
    [InlineData(2026, 10, 10, false)] // Saturday
    [InlineData(2026, 10, 11, false)] // Sunday
    [InlineData(2026, 10, 12, true)]  // Monday
    [InlineData(2026, 10, 9, true)]   // Friday
    public void NothingSyncsByItselfOnWeekends(int year, int month, int day, bool syncs)
    {
        foreach (var hour in new[] { 0, 9, 18 })
        {
            Assert.Equal(syncs, SyncSettings.SyncsAt(new DateTime(year, month, day, hour, 0, 0)));
        }
    }

    [Fact]
    public void IntervalsOutOfRangeAreBroughtIntoIt()
    {
        var settings = new TrackerSettings { Sync = new SyncSettings { WorkHoursIntervalMinutes = 5, OffHoursIntervalMinutes = 10_000 } };

        SettingsStore.Normalize(settings);

        Assert.Equal(SyncSettings.WorkHoursMinimum, settings.Sync.WorkHoursIntervalMinutes);
        Assert.Equal(SyncSettings.OffHoursMaximum, settings.Sync.OffHoursIntervalMinutes);
    }

    [Fact]
    public void TheCompanyFileSetsBothIntervalsWhereTheyAreStillTheDefault()
    {
        var company = new CompanySettings { SyncWorkHoursMinutes = 30, SyncOffHoursMinutes = 240 };
        var untouched = new TrackerSettings();
        var chosen = new TrackerSettings { Sync = new SyncSettings { WorkHoursIntervalMinutes = 45 } };

        Assert.True(CompanySettingsLoader.Apply(company, untouched));
        Assert.True(CompanySettingsLoader.Apply(company, chosen));

        Assert.Equal(30, untouched.Sync.WorkHoursIntervalMinutes);
        Assert.Equal(240, untouched.Sync.OffHoursIntervalMinutes);
        // Someone's own choice stays unless the company locks "sync".
        Assert.Equal(45, chosen.Sync.WorkHoursIntervalMinutes);
        company.Locked.Add("sync");
        Assert.True(CompanySettingsLoader.Apply(company, chosen));
        Assert.Equal(30, chosen.Sync.WorkHoursIntervalMinutes);
    }

    [Fact]
    public void AnOldSingleIntervalIsLeftForTheNewDefaults()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<SyncSettings>(
            """{ "autoSync": true, "intervalMinutes": 15, "fullReadHours": 12 }""", TrackerJson.Options)!;

        Assert.Equal(60, settings.WorkHoursIntervalMinutes);
        Assert.Equal(180, settings.OffHoursIntervalMinutes);
    }
}

public class SyncHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    private static SyncRun Run(string projectId, string mode, DateTimeOffset at, string status = "complete") => new()
    {
        Provider = Providers.Procore,
        ProjectId = projectId,
        Mode = mode,
        StartedAt = at,
        FinishedAt = at.AddSeconds(5),
        Status = status
    };

    [Fact]
    public void AProjectsLastFullReadIsKnownHoweverManyRunsCameAfter()
    {
        using var store = new TrackerStore(new InMemoryPersistence());
        store.RecordSyncRun(Run("p1", "full", Start));
        store.RecordSyncRun(Run("p1", "checked", Start.AddHours(13)));
        for (var index = 0; index < 600; index++)
        {
            store.RecordSyncRun(Run("p2", "changes", Start.AddMinutes(index + 1)));
        }

        // A later full read that failed doesn't count.
        store.RecordSyncRun(Run("p1", "full", Start.AddDays(2), "failed"));

        Assert.Equal(Start, store.LastCompleteSyncRun(Providers.Procore, "p1", "full")?.StartedAt);
        Assert.Equal(Start.AddHours(13), store.LastCompleteSyncRun("PROCORE", "p1", "checked")?.StartedAt);
        Assert.Null(store.LastCompleteSyncRun(Providers.Procore, "p2", "full"));
    }

    [Fact]
    public void TheDatabaseKeepsEachProjectsLastFullReadAcrossRestarts()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Root, "tracker.db");
        using (var persistence = new SqlitePersistence(file))
        {
            persistence.RecordSyncRun(Run("p1", "full", Start));
            persistence.RecordSyncRun(Run("p1", "checked", Start.AddHours(13)));
            persistence.RunBatch(() =>
            {
                // More runs of another project than the database keeps (2,000).
                for (var index = 0; index < 2_100; index++)
                {
                    persistence.RecordSyncRun(Run("p2", "changes", Start.AddDays(1).AddMinutes(index)));
                }
            });
        }

        using (var reopened = new SqlitePersistence(file))
        {
            var runs = reopened.Load().RecentSyncRuns;
            Assert.Contains(runs, run => run.ProjectId == "p1" && run.Mode == "full" && run.StartedAt == Start);
            Assert.Contains(runs, run => run.ProjectId == "p1" && run.Mode == "checked");
            // The latest 200 of all, plus each project's latest complete run of each mode.
            Assert.Equal(202, runs.Count);
            Assert.Equal(runs.OrderBy(run => run.StartedAt).Select(run => run.Id), runs.Select(run => run.Id));
        }

        // The store disposes the database it is given.
        using (var store = new TrackerStore(new SqlitePersistence(file)))
        {
            Assert.Equal(Start, store.LastCompleteSyncRun(Providers.Procore, "p1", "full")?.StartedAt);
        }

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sync_runs;";
        // 2,000 kept by age, plus p1's full and checked runs.
        Assert.Equal(2_002L, (long)count.ExecuteScalar()!);
    }
}
