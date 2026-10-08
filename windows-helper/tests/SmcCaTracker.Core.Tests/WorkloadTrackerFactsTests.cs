using System.Text.Json;
using System.Text.Json.Nodes;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.Tests;

/// <summary>CA Tracker's own facts about an item (discipline, folder status) as Workload Tracker receives them.</summary>
public class ItemFactsTests
{
    private const string CaFolder = @"Z:\Jobs\Tower\CA";

    private static SnapshotItem Item(string id, string number, string title = "Item", string? revision = null) => new()
    {
        ItemType = ItemTypes.Rfi,
        ItemId = id,
        Number = number,
        Title = title,
        Status = "Open",
        Revision = revision,
        Holders = [new SnapshotHolder { Name = "Casey Lead", Company = "SMC", State = "current" }]
    };

    private static ComparisonRow Row(RowStatus status, FolderEntry? folder = null, Discipline? discipline = null,
        DisciplineSource source = DisciplineSource.None, bool item = true) => new()
    {
        ItemType = ItemTypes.Rfi,
        Item = item ? Item("1", "RFI-0001") : null,
        Folder = folder,
        Status = status,
        Discipline = discipline,
        DisciplineSource = source
    };

    private static FolderEntry Folder(string relative, FolderLocation location = FolderLocation.Active) =>
        new(Path.GetFileName(relative), Path.Combine(CaFolder, relative), location, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

    [Theory]
    [InlineData(RowStatus.Open, "open")]
    [InlineData(RowStatus.ReadyToReturn, "ready_to_return")]
    [InlineData(RowStatus.Reopened, "reopened")]
    [InlineData(RowStatus.Duplicate, "duplicate")]
    [InlineData(RowStatus.Filed, "filed")]
    [InlineData(RowStatus.NoFolder, "no_folder")]
    [InlineData(RowStatus.Ambiguous, null)]
    [InlineData(RowStatus.NotInProvider, null)]
    [InlineData(RowStatus.NotInView, null)]
    [InlineData(RowStatus.NoData, null)]
    public void FolderStatusesMapToTheContract(RowStatus status, string? expected)
    {
        Assert.Equal(expected, ItemFacts.FolderStatusOf(status));
        Assert.True(expected is null || FolderStatusEnum.All.Contains(expected));
    }

    [Fact]
    public void TheDisciplineGoesAsTheListShowsItWithHowItWasFound()
    {
        var electrical = Disciplines.Find("Electrical")!;

        // The folder goes as its full path, as people paste it into Explorer.
        var detected = ItemFacts.Of(Row(RowStatus.Open, Folder(@"RFIs\Elec\RFI-0001 - Panel"), electrical, DisciplineSource.Detected))!;
        Assert.Equal(new ItemFacts("Electrical", "detected", "open", $@"{CaFolder}\RFIs\Elec\RFI-0001 - Panel"), detected);

        var set = ItemFacts.Of(Row(RowStatus.Filed, Folder(@"RFIs\~Returned\RFI-0001 - Panel", FolderLocation.Returned), electrical, DisciplineSource.Manual))!;
        Assert.Equal(new ItemFacts("Electrical", "set", "filed", $@"{CaFolder}\RFIs\~Returned\RFI-0001 - Panel"), set);

        // Set to none by hand, and nothing found: both say there is none, but only one was chosen.
        Assert.Equal(new ItemFacts(null, "set", "no_folder", null), ItemFacts.Of(Row(RowStatus.NoFolder, source: DisciplineSource.Manual)));
        Assert.Equal(new ItemFacts(null, null, "no_folder", null), ItemFacts.Of(Row(RowStatus.NoFolder)));

        // A folder with no item isn't sent.
        Assert.Null(ItemFacts.Of(Row(RowStatus.NotInProvider, Folder(@"RFIs\RFI-0009 - Gone"), item: false)));

        // A path longer than the contract allows is cut to 1024 characters.
        var deep = ItemFacts.Of(Row(RowStatus.Open, Folder(string.Join('\\', Enumerable.Repeat(new string('d', 99), 12)))))!;
        Assert.Equal(1024, deep.FolderPath!.Length);
        Assert.StartsWith(CaFolder, deep.FolderPath);
    }

    [Fact]
    public void ABaseBuildAndFitOutPairEachGiveTheItemsOfTheirPart()
    {
        var baseBuild = new TrackerProject
        {
            Name = "Tower BB",
            CaFolder = @"Z:\Jobs\Tower BB\CA",
            Connection = new ProviderConnection { Provider = Providers.Procore, ProjectId = "42" },
            Scope = new PhaseScope { Phase = ProjectPhase.BaseBuild, FilterItems = true }
        };
        var fitOut = new TrackerProject
        {
            Name = "Tower TFO",
            CaFolder = @"Z:\Jobs\Tower TFO\CA",
            Connection = new ProviderConnection { Provider = Providers.Procore, ProjectId = "42" },
            Scope = new PhaseScope { Phase = ProjectPhase.TenantFitOut, FilterItems = true }
        };
        var settings = new TrackerSettings { Projects = { baseBuild, fitOut } };
        var snapshot = new ProviderSnapshot
        {
            Provider = Providers.Procore,
            ProjectId = "42",
            ItemTypes = [ItemTypes.Rfi],
            Complete = true,
            ScannedAt = DateTimeOffset.Now,
            User = new SnapshotUser { FullName = "Casey Lead", Company = "SMC" },
            Items =
            [
                Item("10", "BB-001", "Slab"),
                Item("11", "TFO-002", "Ceiling"),
                Item("12", "BB-003", "Doors", revision: "0"),
                Item("13", "BB-003", "Doors", revision: "1")
            ]
        };

        TypeFolderScan? Folders(TrackerProject project, string itemType, FolderLayout layout) => new()
        {
            ItemType = itemType,
            Signature = FolderScanner.SignatureFor(project.CaFolder, itemType, layout),
            TypeFolderExists = true,
            Folders = itemType != ItemTypes.Rfi ? []
                : project == baseBuild
                    ? [new FolderEntry("BB-001 - Slab", $@"{project.CaFolder}\RFIs\BB-001 - Slab", FolderLocation.Active, DateTime.UtcNow)]
                    : [new FolderEntry("TFO-002 - Ceiling", $@"{project.CaFolder}\RFIs\TFO-002 - Ceiling", FolderLocation.Active, DateTime.UtcNow)]
        };

        var facts = ItemFacts.ForProject(snapshot, settings, Folders);

        Assert.Equal(@"Z:\Jobs\Tower BB\CA\RFIs\BB-001 - Slab", facts["rfi|10"].FolderPath);
        Assert.Equal(@"Z:\Jobs\Tower TFO\CA\RFIs\TFO-002 - Ceiling", facts["rfi|11"].FolderPath);
        // Of a Procore item's revisions only the newest is compared; the older one keeps what the tracker has.
        Assert.Equal("no_folder", facts["rfi|13"].FolderStatus);
        Assert.False(facts.ContainsKey("rfi|12"));
        Assert.Equal(3, facts.Count);
    }

    [Fact]
    public void FoldersNotListedYetOrUnreadableGiveNoFacts()
    {
        var project = new TrackerProject { CaFolder = CaFolder, Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = "H-1" } };
        var settings = new TrackerSettings { Projects = { project } };
        var snapshot = new ProviderSnapshot
        {
            Provider = Providers.Helix,
            ProjectId = "H-1",
            ItemTypes = [ItemTypes.Rfi],
            Complete = true,
            Items = [Item("1", "RFI-0001")]
        };

        Assert.Empty(ItemFacts.ForProject(snapshot, settings, (_, _, _) => null));
        Assert.Empty(ItemFacts.ForProject(snapshot, settings, (_, itemType, _) => new TypeFolderScan { ItemType = itemType, Error = "The CA folder cannot be found." }));

        // A project with no CA folder has no folders: its items are listed as having none.
        project.CaFolder = "";
        var facts = ItemFacts.ForProject(snapshot, settings, (_, itemType, layout) => FolderScanner.Scan("", itemType, layout));
        Assert.Equal("no_folder", Assert.Single(facts).Value.FolderStatus);
    }

    [Fact]
    public void AnItemCarriesItsFactsAsASetOrLeavesThemOut()
    {
        var item = Item("1", "RFI-0001");

        var without = JsonNode.Parse(JsonSerializer.Serialize(ObservationBuilder.Item(item), ContractJson.Options))!.AsObject();
        Assert.All(new[] { "discipline", "disciplineSource", "folderStatus", "folderPath" }, key => Assert.False(without.ContainsKey(key)));

        var cleared = JsonNode.Parse(JsonSerializer.Serialize(ObservationBuilder.Item(item, new ItemFacts(null, null, "no_folder", null)), ContractJson.Options))!.AsObject();
        Assert.True(cleared.ContainsKey("discipline") && cleared["discipline"] is null);
        Assert.True(cleared.ContainsKey("folderPath") && cleared["folderPath"] is null);
        Assert.Equal("no_folder", cleared["folderStatus"]!.GetValue<string>());

        Assert.Empty(PinnedSchema.Problems("CaItemRequest", without.ToJsonString()));
        Assert.Empty(PinnedSchema.Problems("CaItemRequest", cleared.ToJsonString()));
        var path = $@"{CaFolder}\RFIs\RFI-0001 - Panel";
        var full = ObservationBuilder.Item(item, new ItemFacts("Electrical", "set", "ready_to_return", path));
        Assert.Empty(PinnedSchema.Problems("CaItemRequest", JsonSerializer.Serialize(full, ContractJson.Options)));
        Assert.Equal(ItemFacts.FingerprintOf(full), new ItemFacts("Electrical", "set", "ready_to_return", path).Fingerprint);

        // The pinned schema takes a full path of up to 1024 characters.
        var longest = ObservationBuilder.Item(item, new ItemFacts(null, null, "open", new string('p', 1024)));
        Assert.Empty(PinnedSchema.Problems("CaItemRequest", JsonSerializer.Serialize(longest, ContractJson.Options)));
        var tooLong = JsonNode.Parse(JsonSerializer.Serialize(longest, ContractJson.Options))!.AsObject();
        tooLong["folderPath"] = new string('p', 1025);
        Assert.NotEmpty(PinnedSchema.Problems("CaItemRequest", tooLong.ToJsonString()));
        Assert.Null(ItemFacts.FingerprintOf(ObservationBuilder.Item(item)));
    }
}

/// <summary>Facts change on their own (a discipline set by hand, a folder moved); each change sends its items once.</summary>
public sealed class WorkloadTrackerFactsSyncTests : IDisposable
{
    private const string Address = "https://tracker.example";
    private const string CaFolder = @"Z:\Jobs\H-1\CA";
    private readonly FakeTracker tracker = new();
    private readonly TrackerStore store = new(new InMemoryPersistence());
    private DateTimeOffset now = new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);
    private readonly WorkloadTrackerSync sync;

    public WorkloadTrackerFactsSyncTests()
    {
        sync = new WorkloadTrackerSync(store, new WorkloadTrackerApi(new HttpClient(tracker)), new PlainProtector(), () => now);
    }

    public void Dispose() => store.Dispose();

    private TrackerProject Track(string caFolder = CaFolder)
    {
        var project = new TrackerProject { Name = "H-1", CaFolder = caFolder, Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = "H-1" } };
        var settings = store.SettingsCopy();
        settings.Projects.Add(project);
        store.SaveSettings(settings);
        return project;
    }

    private static ProviderSnapshot Snapshot()
    {
        var scanned = new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);
        return new ProviderSnapshot
        {
            SchemaVersion = 1,
            Provider = Providers.Helix,
            ProjectId = "H-1",
            ProjectName = "Project H-1",
            ItemTypes = [ItemTypes.Rfi],
            Complete = true,
            ScannedAt = scanned,
            User = new SnapshotUser { FullName = "Casey Lead", Company = "SMC" },
            Coverage = { [ItemTypes.Rfi] = new TypeCoverage { ScannedAt = scanned, Complete = true } },
            Items = Enumerable.Range(1, 3).Select(n => new SnapshotItem
            {
                ItemType = ItemTypes.Rfi,
                ItemId = $"{n}",
                Number = $"RFI-{n:D4}",
                Title = $"Item {n}",
                Status = "Open",
                // With this PC's company, so open items aren't returned.
                Holders = [new SnapshotHolder { Name = "Casey Lead", Company = "SMC", State = "current" }]
            }).ToList()
        };
    }

    /// <summary>Saves the project's folder lists as the folder index would (the drive isn't read).</summary>
    private void SaveFolders(TrackerProject project, params FolderEntry[] rfiFolders)
    {
        var layout = project.EffectiveFolders(store.Settings.Folders);
        foreach (var itemType in ItemTypes.All)
        {
            store.SaveFolderScan(project.Id, new TypeFolderScan
            {
                ItemType = itemType,
                Signature = FolderScanner.SignatureFor(project.CaFolder, itemType, layout),
                TypeFolderPath = Path.Combine(project.CaFolder, layout.TypeFolder(itemType)),
                TypeFolderExists = true,
                Folders = itemType == ItemTypes.Rfi ? rfiFolders : []
            });
        }
    }

    private static FolderEntry Folder(string name, bool returned = false) => returned
        ? new FolderEntry(name, $@"{CaFolder}\RFIs\~Returned\{name}", FolderLocation.Returned, DateTime.UtcNow) { ReturnedFolder = $@"{CaFolder}\RFIs\~Returned" }
        : new FolderEntry(name, $@"{CaFolder}\RFIs\{name}", FolderLocation.Active, DateTime.UtcNow);

    private static JsonObject Sent(JsonNode observation, string itemId) =>
        observation["items"]!.AsArray().Single(item => item!["providerItemId"]!.GetValue<string>() == itemId)!.AsObject();

    private async Task<TrackerProject> SentOnceAsync()
    {
        var project = Track();
        store.SaveSnapshot(Snapshot());
        SaveFolders(project, Folder("RFI-0001 - Elec - Item 1"));
        Assert.Null(await sync.ConnectAsync(Address, "token-1", 99, now.AddDays(180)));
        await sync.SendAsync();
        return project;
    }

    [Fact]
    public async Task ItemsCarryTheirDisciplineAndFolderStatus()
    {
        await SentOnceAsync();

        var observation = Assert.Single(tracker.Observations);
        Assert.Empty(PinnedSchema.Problems("CaObservationPartRequest", observation.ToJsonString()));
        var first = Sent(observation, "1");
        Assert.Equal(("Electrical", "detected", "open", $@"{CaFolder}\RFIs\RFI-0001 - Elec - Item 1"),
            (first["discipline"]!.GetValue<string>(), first["disciplineSource"]!.GetValue<string>(),
                first["folderStatus"]!.GetValue<string>(), first["folderPath"]!.GetValue<string>()));
        var second = Sent(observation, "2");
        Assert.Equal("no_folder", second["folderStatus"]!.GetValue<string>());
        Assert.True(second.ContainsKey("discipline") && second["discipline"] is null && second["disciplineSource"] is null && second["folderPath"] is null);

        Assert.Equal(3, store.SentFacts("helix", "H-1").Count);
        Assert.Empty(store.Outbox());
    }

    [Fact]
    public async Task ADisciplineSetByHandSendsThatItemOnceWithItsNewFacts()
    {
        var project = await SentOnceAsync();
        tracker.Requests.Clear();

        // Nothing changed: worked out again later, nothing is sent.
        now += WorkloadTrackerSync.FactsInterval + TimeSpan.FromMinutes(1);
        await sync.SendAsync();
        Assert.Empty(tracker.Observations);

        var settings = store.SettingsCopy();
        settings.FindProject(project.Id)!.Disciplines[Disciplines.ItemKey(ItemTypes.Rfi, "2")] = "Mechanical";
        store.SaveSettings(settings);
        await sync.SendAsync();

        var observation = Assert.Single(tracker.Observations);
        Assert.Empty(PinnedSchema.Problems("CaObservationPartRequest", observation.ToJsonString()));
        var item = Assert.Single(observation["items"]!.AsArray())!;
        Assert.Equal(("2", "Mechanical", "set", "no_folder"),
            (item["providerItemId"]!.GetValue<string>(), item["discipline"]!.GetValue<string>(),
                item["disciplineSource"]!.GetValue<string>(), item["folderStatus"]!.GetValue<string>()));
        Assert.False(observation["coverage"]![0]!["fullSet"]!.GetValue<bool>());
        Assert.Empty(store.Outbox());

        // Sent once: later rounds have nothing more to say about it.
        tracker.Requests.Clear();
        now += WorkloadTrackerSync.FactsInterval + TimeSpan.FromMinutes(1);
        await sync.SendAsync();
        await sync.SendAsync();
        Assert.Empty(tracker.Observations);
    }

    [Fact]
    public async Task AFolderFiledInReturnedIsSentAtTheNextRound()
    {
        var project = await SentOnceAsync();
        tracker.Requests.Clear();

        // The folder index found the folder in ~Returned (the item is still with this PC's company).
        SaveFolders(project, Folder("RFI-0001 - Elec - Item 1", returned: true));
        await sync.SendAsync();

        var item = Assert.Single(Assert.Single(tracker.Observations)["items"]!.AsArray())!;
        Assert.Equal(("1", "reopened", $@"{CaFolder}\RFIs\~Returned\RFI-0001 - Elec - Item 1"),
            (item["providerItemId"]!.GetValue<string>(), item["folderStatus"]!.GetValue<string>(), item["folderPath"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ItemsOfAProjectWhoseFoldersArentListedYetLeaveTheirFactsOut()
    {
        Track();
        store.SaveSnapshot(Snapshot());
        Assert.Null(await sync.ConnectAsync(Address, "token-1", 99, now.AddDays(180)));

        await sync.SendAsync();

        var observation = Assert.Single(tracker.Observations);
        Assert.Equal(3, observation["items"]!.AsArray().Count);
        Assert.All(observation["items"]!.AsArray(), item => Assert.False(item!.AsObject().ContainsKey("folderStatus")));
        Assert.Empty(store.SentFacts("helix", "H-1"));
    }

    [Fact]
    public async Task DisconnectingForgetsWhatWasSentSoReconnectingSendsItAll()
    {
        await SentOnceAsync();
        Assert.NotEmpty(store.SentFacts("helix", "H-1"));

        await sync.DisconnectAsync();

        Assert.Empty(store.SentFacts("helix", "H-1"));
    }

    [Fact]
    public void SentFactsAreKeptAcrossARestartAndThoseOfItemsThatWentAwayAreForgotten()
    {
        var persistence = new InMemoryPersistence();
        using (var first = new TrackerStore(persistence))
        {
            var settings = first.SettingsCopy();
            settings.WorkloadTracker.Address = Address;
            settings.WorkloadTracker.TokenProtected = "token";
            settings.WorkloadTracker.Account = "tracker.example#7";
            first.SaveSettings(settings);
            first.SaveSnapshot(Snapshot());
            first.RecordSentFacts("helix", "H-1", [("rfi|1", "a"), ("rfi|gone", "b")]);
            Assert.True(first.Flush(TimeSpan.FromSeconds(5)));
        }

        using var reopened = new TrackerStore(persistence);
        Assert.Equal(new Dictionary<string, string> { ["rfi|1"] = "a" }, reopened.SentFacts("helix", "H-1"));
    }
}

/// <summary>Whether a "marked complete" mark read back from Workload Tracker is about an item as this PC has it.</summary>
public class WorkloadCompletionTests
{
    private static readonly ProviderConnection Procore = new() { Provider = Providers.Procore, ProjectId = "42" };

    private static WorkloadCompletion Mark(int personId, string itemId, string? revision) => new()
    {
        TrackerProjectId = 12,
        Provider = "procore",
        Host = "app.procore.com",
        ProviderProjectId = "42",
        ItemType = "submittal",
        ProviderItemId = itemId,
        Number = "03 30 00-1",
        Revision = revision,
        PersonId = personId,
        PersonName = $"Person {personId}",
        MarkedAt = new DateTimeOffset(2026, 10, 7, 16, 0, 0, TimeSpan.Zero)
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<WorkloadCompletion>> Index(params WorkloadCompletion[] marks) =>
        marks.GroupBy(mark => mark.ItemKey).ToDictionary(group => group.Key, group => (IReadOnlyList<WorkloadCompletion>)group.ToList());

    private static SnapshotItem Submittal(string id, string? revision) =>
        new() { ItemType = "Submittal", ItemId = id, Number = "03 30 00-1", Revision = revision, Status = "Open" };

    [Fact]
    public void AMarkCoversTheItemRevisionItWasMadeOn()
    {
        var index = Index(Mark(39, "55", "2"), Mark(40, "55", "2"), Mark(39, "55", "2"), Mark(41, "56", null));

        Assert.Equal([39, 40], WorkloadCompletion.On(index, Procore, Submittal("55", "2")).Select(mark => mark.PersonId));
        // The same provider item at a newer revision is new work.
        Assert.Empty(WorkloadCompletion.On(index, Procore, Submittal("55", "3")));
        // No revision on either side matches; a blank revision is none.
        Assert.Single(WorkloadCompletion.On(index, Procore, Submittal("56", " ")));
        Assert.Empty(WorkloadCompletion.On(index, Procore, Submittal("57", null)));
        // Another project, or the other provider, is another item.
        Assert.Empty(WorkloadCompletion.On(index, new ProviderConnection { Provider = Providers.Procore, ProjectId = "43" }, Submittal("55", "2")));
        Assert.Empty(WorkloadCompletion.On(index, new ProviderConnection { Provider = Providers.Helix, ProjectId = "42" }, Submittal("55", "2")));
    }

    [Fact]
    public void AMarkedItemIsFlaggedUntilItIsReturned()
    {
        var marks = new[] { Mark(39, "55", "2") };
        var item = Submittal("55", "2");
        var active = new FolderEntry("03 30 00-1 - Rebar", @"Z:\CA\Submittals\03 30 00-1 - Rebar", FolderLocation.Active, DateTime.UtcNow);
        var filed = active with { FullPath = @"Z:\CA\Submittals\~Returned\03 30 00-1 - Rebar", Location = FolderLocation.Returned };

        Assert.True(WorkloadCompletion.AwaitsReturn(new ComparisonRow { Item = item, Folder = active, Status = RowStatus.Open }, marks));
        Assert.True(WorkloadCompletion.AwaitsReturn(new ComparisonRow { Item = item, Status = RowStatus.NoFolder }, marks));
        Assert.False(WorkloadCompletion.AwaitsReturn(new ComparisonRow { Item = item, Folder = active, Status = RowStatus.Open }, []));
        Assert.False(WorkloadCompletion.AwaitsReturn(new ComparisonRow { Item = item, Folder = active, IsReturned = true, Status = RowStatus.ReadyToReturn }, marks));
        Assert.False(WorkloadCompletion.AwaitsReturn(new ComparisonRow { Item = item, Folder = filed, Status = RowStatus.Reopened }, marks));
        var closed = Submittal("55", "2");
        closed.Closed = true;
        Assert.False(WorkloadCompletion.AwaitsReturn(new ComparisonRow { Item = closed, Folder = active, Status = RowStatus.ReadyToReturn }, marks));
    }
}
