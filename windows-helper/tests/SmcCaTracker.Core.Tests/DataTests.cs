using Microsoft.Data.Sqlite;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Data;

namespace SmcCaTracker.Core.Tests;

public class DatabaseTests
{
    private static TrackerSettings SampleSettings()
    {
        var project = new TrackerProject
        {
            Id = "project-1",
            Name = "CMH04 BB",
            CaFolder = @"Z:\Projects\CMH04\CA",
            Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = "helix-1", ProjectName = "CMH04" },
            CompanyId = "company-1",
            CompanyName = "SMC",
            FoldersOverride = new FolderLayout { RfiFolder = "01 RFIs", SubmittalFolder = "02 Submittals", ReturnedFolder = "~Returned" },
            Scope = new PhaseScope { Phase = ProjectPhase.BaseBuild, FilterFolders = true }
        };
        project.Disciplines[Disciplines.Key(ItemTypes.Submittal, "P000210 - Doors")] = "Electrical";
        project.Disciplines[Disciplines.ItemKey(ItemTypes.Rfi, "abc123")] = "Mechanical";
        return new TrackerSettings
        {
            Theme = ThemePreference.Dark,
            Folders = new FolderLayout { RfiFolder = "RFIs", SubmittalFolder = "Submittals", ReturnedFolder = "~Returned" },
            Sync = new SyncSettings { AutoSync = false, IntervalMinutes = 30 },
            Projects = { project }
        };
    }

    private static ProviderSnapshot SampleSnapshot(params SnapshotItem[] items) => new()
    {
        SchemaVersion = 1,
        Provider = Providers.Helix,
        ProjectId = "helix-1",
        ProjectName = "CMH04",
        ItemTypes = { ItemTypes.Rfi, ItemTypes.Submittal },
        Complete = true,
        ScannedAt = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.FromHours(-7)),
        User = new SnapshotUser { FullName = "Current User", CompanyId = "company-1", Company = "SMC" },
        Coverage =
        {
            [ItemTypes.Rfi] = new TypeCoverage { ScannedAt = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), Complete = true },
            [ItemTypes.Submittal] = new TypeCoverage { ScannedAt = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero), Complete = true }
        },
        Items = items.ToList()
    };

    private static SnapshotItem Item(string id, string number, string holder = "Current User") => new()
    {
        ItemType = ItemTypes.Rfi,
        ItemId = id,
        Number = number,
        Title = $"Title {number}",
        Status = "Review",
        StatusRank = 800,
        Responsible = holder,
        DueAt = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
        Holders =
        {
            new SnapshotHolder { Name = holder, Company = "SMC", State = "current", Since = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero) }
        }
    };

    private static long ChangeCount(string databaseFile, string entity, string operation)
    {
        using var connection = new SqliteConnection($"Data Source={databaseFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM change_log WHERE entity = $entity AND operation = $operation;";
        command.Parameters.AddWithValue("$entity", entity);
        command.Parameters.AddWithValue("$operation", operation);
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void EverythingSavedComesBackAfterReopening()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Root, "tracker.db");
        var scan = new TypeFolderScan
        {
            ItemType = ItemTypes.Submittal,
            Signature = "sig",
            TypeFolderPath = @"Z:\CA\Submittals",
            TypeFolderExists = true,
            ReturnedFolderPath = @"Z:\CA\Submittals\~Returned",
            ReturnedFolderExists = true,
            Folders = new[]
            {
                new FolderEntry("P000210 - Doors", @"Z:\CA\Submittals\Elec\BB\P000210 - Doors", FolderLocation.Active,
                    new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc))
                {
                    Groups = new[] { "Elec", "BB" },
                    RelativeParent = @"Elec\BB"
                }
            },
            Containers = new[] { new FolderStamp(@"Z:\CA\Submittals", new DateTime(638_000_000_000_000_123, DateTimeKind.Utc), true) }
        };

        using (var database = new SqlitePersistence(file))
        {
            database.SaveSettings(SampleSettings());
            database.SaveSnapshot(SampleSnapshot(Item("a", "RFI-001"), Item("b", "RFI-002", "Other Person")));
            database.SaveFolderScan("project-1", scan);
            database.SaveCatalog(new[] { new CatalogProject { Provider = Providers.Helix, ProjectId = "helix-2", Name = "Other job", SeenAt = DateTimeOffset.Now } });
            database.SaveAccounts(new[] { new ProviderAccount { Provider = Providers.Helix, UserName = "Current User", SignedInAt = DateTimeOffset.Now } });
            database.SaveViewState(new ViewState { Filter = "all", People = { "Current User" } });
            database.RecordSyncRun(new SyncRun { Provider = Providers.Helix, ProjectId = "helix-1", Mode = "changes", Status = "complete", StartedAt = DateTimeOffset.Now, ItemCount = 2 });
        }

        using (var database = new SqlitePersistence(file))
        {
            var state = database.Load();
            Assert.Equal(ThemePreference.Dark, state.Settings.Theme);
            Assert.False(state.Settings.Sync.AutoSync);
            Assert.Equal(30, state.Settings.Sync.IntervalMinutes);
            var project = Assert.Single(state.Settings.Projects);
            Assert.Equal("CMH04 BB", project.Name);
            Assert.Equal("helix-1", project.Connection?.ProjectId);
            Assert.Equal("01 RFIs", project.FoldersOverride?.RfiFolder);
            Assert.Equal(ProjectPhase.BaseBuild, project.Scope.Phase);
            Assert.True(project.Scope.FilterFolders);
            Assert.Equal("Electrical", project.Disciplines[Disciplines.Key(ItemTypes.Submittal, "P000210 - Doors")]);
            Assert.Equal("Mechanical", project.Disciplines[Disciplines.ItemKey(ItemTypes.Rfi, "abc123")]);

            var snapshot = Assert.Single(state.Snapshots);
            Assert.Equal(2, snapshot.Items.Count);
            Assert.True(snapshot.Covers(ItemTypes.Submittal));
            Assert.True(snapshot.CoverageFor(ItemTypes.Rfi)!.Complete);
            Assert.Equal("Current User", snapshot.User.FullName);
            var a = snapshot.Items.Single(item => item.ItemId == "a");
            Assert.Equal(800, a.StatusRank);
            Assert.Equal(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), a.DueAt);
            Assert.Equal("Current User", Assert.Single(a.Holders).Name);
            Assert.Equal(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), a.Holders[0].Since);

            var saved = Assert.Single(state.FolderScans);
            Assert.Equal("project-1", saved.ProjectId);
            Assert.Equal("sig", saved.Scan.Signature);
            var folder = Assert.Single(saved.Scan.Folders);
            Assert.Equal(new[] { "Elec", "BB" }, folder.Groups);
            Assert.Equal(@"Elec\BB", folder.RelativeParent);
            Assert.Equal(638_000_000_000_000_123, Assert.Single(saved.Scan.Containers).LastWriteTimeUtc.Ticks);

            Assert.Equal("Other job", Assert.Single(state.Catalog).Name);
            Assert.Equal("Current User", Assert.Single(state.Accounts).UserName);
            Assert.Equal("all", state.View.Filter);
            Assert.Equal("changes", Assert.Single(state.RecentSyncRuns).Mode);
        }

        Assert.Equal(1, ChangeCount(file, "project", "insert"));
        Assert.Equal(2, ChangeCount(file, "item", "insert"));
    }

    [Fact]
    public void ItemChangesAndRemovalsAreLoggedButUnchangedItemsAreNot()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Root, "tracker.db");
        using (var database = new SqlitePersistence(file))
        {
            database.SaveSnapshot(SampleSnapshot(Item("a", "RFI-001"), Item("b", "RFI-002")));
            // "a" goes to someone else, "b" is unchanged, "c" is new; nothing else.
            database.SaveSnapshot(SampleSnapshot(Item("a", "RFI-001", "Other Person"), Item("b", "RFI-002"), Item("c", "RFI-003")));
            // "b" was deleted in Helix.
            database.SaveSnapshot(SampleSnapshot(Item("a", "RFI-001", "Other Person"), Item("c", "RFI-003")));
            var items = Assert.Single(database.Load().Snapshots).Items;
            Assert.Equal(new[] { "a", "c" }, items.Select(item => item.ItemId).OrderBy(id => id));
            Assert.Equal("Other Person", items.Single(item => item.ItemId == "a").Holders[0].Name);
        }

        Assert.Equal(3, ChangeCount(file, "item", "insert"));
        Assert.Equal(1, ChangeCount(file, "item", "update"));
        Assert.Equal(1, ChangeCount(file, "item", "delete"));
    }

    [Fact]
    public void RemovedProjectsAreMarkedDeletedAndLogged()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Root, "tracker.db");
        using (var database = new SqlitePersistence(file))
        {
            database.SaveSettings(SampleSettings());
            database.SaveSettings(SampleSettings()); // unchanged: no new log entry
            var empty = SampleSettings();
            empty.Projects.Clear();
            database.SaveSettings(empty);
            Assert.Empty(database.Load().Settings.Projects);
        }

        Assert.Equal(1, ChangeCount(file, "project", "insert"));
        Assert.Equal(0, ChangeCount(file, "project", "update"));
        Assert.Equal(1, ChangeCount(file, "project", "delete"));
    }

    [Theory]
    [InlineData("rfi|p000210 - doors", "rfi", "folder", "p000210 - doors")]
    [InlineData("item|submittal|abc123", "submittal", "item", "abc123")]
    [InlineData("nonsense", "", "", "")]
    public void DisciplineKeysSplitIntoColumns(string key, string itemType, string kind, string target)
    {
        Assert.Equal((itemType, kind, target), SqlitePersistence.SplitDisciplineKey(key));
    }
}

public class TrackerStoreTests
{
    [Fact]
    public void ChangesAreKeptInMemoryAtOnceAndSavedInTheBackground()
    {
        var persistence = new InMemoryPersistence();
        var changes = new List<StoreChangeKind>();
        using (var store = new TrackerStore(persistence))
        {
            store.Changed += (_, change) => changes.Add(change.Kind);
            var merged = store.SaveSnapshot(new ProviderSnapshot
            {
                Provider = Providers.Helix,
                ProjectId = "p1",
                ProjectName = "Project",
                ItemTypes = { ItemTypes.Rfi },
                Complete = true,
                ScannedAt = DateTimeOffset.Now,
                Items = { new SnapshotItem { ItemType = ItemTypes.Rfi, ItemId = "a", Number = "1" } }
            });
            Assert.Same(merged, store.Snapshot(Providers.Helix, "p1"));
            store.RecordAccount("HELIX", "Someone", null, DateTimeOffset.Now);
            Assert.Equal("Someone", store.Account(Providers.Helix)?.UserName);
            store.MergeCatalog(new CatalogMessage
            {
                Provider = Providers.Helix,
                Projects = { new CatalogProject { ProjectId = "p2", Name = "Second" } }
            }, DateTimeOffset.Now);
            Assert.Contains(store.CatalogWithSnapshots(), project => project.ProjectId == "p1");
            Assert.Contains(store.CatalogWithSnapshots(), project => project.ProjectId == "p2");
            Assert.True(store.Flush(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(new[] { StoreChangeKind.Snapshot, StoreChangeKind.Accounts, StoreChangeKind.Catalog }, changes);
        using var reopened = new TrackerStore(persistence);
        Assert.Equal("1", reopened.Snapshot(Providers.Helix, "p1")?.Items.Single().Number);
        Assert.Equal("Someone", reopened.Account(Providers.Helix)?.UserName);
    }

    [Fact]
    public void AFullProcoreListingReplacesThatSitesProjectsOnly()
    {
        using var store = new TrackerStore(new InMemoryPersistence());
        var now = DateTimeOffset.Now;
        store.MergeCatalog(new CatalogMessage
        {
            Provider = Providers.Procore,
            Projects =
            {
                new CatalogProject { ProjectId = "1", AccountId = "10", Name = "Kept" },
                new CatalogProject { ProjectId = "2", AccountId = "10", Name = "No longer visible" }
            }
        }, now);
        store.MergeCatalog(new CatalogMessage
        {
            Provider = Providers.Helix,
            Projects = { new CatalogProject { ProjectId = "h1", Name = "Helix job" } }
        }, now);

        // Adding (the default) keeps what was there.
        store.MergeCatalog(new CatalogMessage
        {
            Provider = Providers.Procore,
            Projects = { new CatalogProject { ProjectId = "3", AccountId = "20", Name = "New" } }
        }, now);
        Assert.Equal(3, store.Catalog().Count(project => project.Provider == Providers.Procore));

        // A complete listing drops Procore projects that aren't in it, and leaves Helix alone.
        store.MergeCatalog(new CatalogMessage
        {
            Provider = Providers.Procore,
            Projects =
            {
                new CatalogProject { ProjectId = "1", AccountId = "10", Name = "Kept" },
                new CatalogProject { ProjectId = "3", AccountId = "20", Name = "New" }
            }
        }, now, replace: true);
        var ids = store.Catalog().Select(project => project.ProjectId).OrderBy(id => id).ToList();
        Assert.Equal(new[] { "1", "3", "h1" }, ids);
    }

    [Fact]
    public void FolderListsAreReusedUntilAFolderChanges()
    {
        using var temp = new TempFolder();
        temp.Make("CA", "RFIs", "RFI-001 - First");
        temp.Make("CA", "Submittals");
        using var store = new TrackerStore(new InMemoryPersistence());
        var index = new FolderIndex(store);
        var project = new TrackerProject { Id = "p", Name = "P", CaFolder = Path.Combine(temp.Root, "CA") };
        var layout = new FolderLayout();

        Assert.Null(index.Cached(project, ItemTypes.Rfi, layout));
        var first = index.GetOrScan(project, ItemTypes.Rfi, layout);
        Assert.Single(first.Folders);
        Assert.Same(first, index.Cached(project, ItemTypes.Rfi, layout));
        Assert.True(FolderScanner.IsCurrent(first));

        // Nothing changed: nothing is listed again.
        Assert.True(index.Refresh(project, layout)); // the Submittals folder had no list yet
        Assert.False(index.Refresh(project, layout));
        Assert.Same(first, index.Cached(project, ItemTypes.Rfi, layout));

        // A new item folder changes its parent's date, so the next check lists it.
        temp.Make("CA", "RFIs", "RFI-002 - Second");
        Directory.SetLastWriteTimeUtc(Path.Combine(temp.Root, "CA", "RFIs"), DateTime.UtcNow.AddMinutes(1));
        Assert.False(FolderScanner.IsCurrent(first));
        Assert.True(index.Refresh(project, layout));
        Assert.Equal(2, index.Cached(project, ItemTypes.Rfi, layout)!.Folders.Count);

        // A different CA folder doesn't reuse the old list.
        var moved = new TrackerProject { Id = "p", Name = "P", CaFolder = Path.Combine(temp.Root, "Elsewhere") };
        Assert.Null(index.Cached(moved, ItemTypes.Rfi, layout));
    }
}

public class LegacyImportTests
{
    [Fact]
    public void JsonFilesOfEarlierVersionsAreImportedOnceAndMovedAside()
    {
        using var temp = new TempFolder();
        var paths = new AppPaths(temp.Root);
        new SettingsStore(paths).Save(new TrackerSettings
        {
            Projects = { new TrackerProject { Id = "p1", Name = "Imported", CaFolder = @"Z:\CA" } }
        });
        new SnapshotStore(paths).Save(new ProviderSnapshot
        {
            Provider = Providers.Helix,
            ProjectId = "h1",
            ProjectName = "Helix job",
            ItemTypes = { ItemTypes.Rfi },
            Complete = true,
            ScannedAt = DateTimeOffset.Now,
            Items = { new SnapshotItem { ItemType = ItemTypes.Rfi, ItemId = "a", Number = "1" } }
        });

        using (var database = new SqlitePersistence(paths.DatabaseFile))
        {
            var result = LegacyImport.RunIfNeeded(paths, database);
            Assert.True(result.Imported);
            Assert.Equal(1, result.Projects);
            Assert.Equal(1, result.ItemLists);
            Assert.False(LegacyImport.RunIfNeeded(paths, database).Imported);

            var state = database.Load();
            Assert.Equal("Imported", Assert.Single(state.Settings.Projects).Name);
            Assert.Equal("a", Assert.Single(Assert.Single(state.Snapshots).Items).ItemId);
        }

        Assert.False(File.Exists(paths.SettingsFile));
        Assert.False(Directory.Exists(paths.SnapshotsDirectory));
        Assert.True(Directory.Exists(paths.LegacyBackupDirectory));
    }
}

public class StoreBehaviourTests
{
    [Fact]
    public void ARereadWithNoChangesOnlyMovesTheReadTime()
    {
        using var store = new TrackerStore(new InMemoryPersistence());
        var original = store.SaveSnapshot(new ProviderSnapshot
        {
            Provider = Providers.Helix,
            ProjectId = "p1",
            ProjectName = "Project",
            ItemTypes = { ItemTypes.Rfi },
            Complete = true,
            ScannedAt = DateTimeOffset.Now.AddHours(-1),
            Items = { new SnapshotItem { ItemType = ItemTypes.Rfi, ItemId = "a", Number = "1" } }
        });
        StoreChange? seen = null;
        store.Changed += (_, change) => seen = change;
        var now = DateTimeOffset.Now;

        store.TouchSnapshot(Providers.Helix, "p1", now);

        var touched = store.Snapshot(Providers.Helix, "p1")!;
        Assert.Equal(now, touched.ScannedAt);
        Assert.Equal(now, touched.CoverageFor(ItemTypes.Rfi)!.ScannedAt);
        Assert.Same(original.Items, touched.Items);
        Assert.False(seen!.ItemsChanged);
    }

    [Fact]
    public void AFailedBatchLeavesTheDatabaseUnchanged()
    {
        using var temp = new TempFolder();
        using var database = new SqlitePersistence(Path.Combine(temp.Root, "tracker.db"));
        Assert.Throws<InvalidOperationException>(() => database.RunBatch(() =>
        {
            database.SaveSettings(new TrackerSettings { Projects = { new TrackerProject { Id = "p1", Name = "Half imported" } } });
            database.SetMeta("legacy_json_imported", "yes");
            throw new InvalidOperationException("boom");
        }));

        Assert.Empty(database.Load().Settings.Projects);
        Assert.Null(database.GetMeta("legacy_json_imported"));
    }

    [Fact]
    public void ExpiredSignInsAreRememberedUntilTheNextSignIn()
    {
        using var store = new TrackerStore(new InMemoryPersistence());
        store.RecordAccount(Providers.Helix, "Someone", "SMC", DateTimeOffset.Now);
        store.MarkSignInNeeded(Providers.Helix);
        Assert.True(store.Account(Providers.Helix)!.NeedsSignIn);
        Assert.Equal("Someone", store.Account(Providers.Helix)!.UserName);
        store.RecordAccount(Providers.Helix, null, null, DateTimeOffset.Now);
        Assert.False(store.Account(Providers.Helix)!.NeedsSignIn);
        Assert.Equal("Someone", store.Account(Providers.Helix)!.UserName);
    }
}
