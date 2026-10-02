using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmcCaTracker.Core.Central;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Data;

namespace SmcCaTracker.Core.Tests;

public class CentralSyncTests
{
    private const string Server = "https://ca.example.com";
    private const string Token = "token-1";

    /// <summary>A small central server following central/PROTOCOL.md (without the server's de-duplication).</summary>
    private sealed class FakeCentral : HttpMessageHandler
    {
        public List<JsonObject> Changes { get; } = new();
        public List<string> Requests { get; } = new();

        public void AddFromSomeoneElse(string entity, string key, object? payload, DateTimeOffset changedAt, string operation = "update")
        {
            Changes.Add(new JsonObject
            {
                ["seq"] = Changes.Count + 1,
                ["client"] = "someone-else",
                ["user"] = "Other Person",
                ["entity"] = entity,
                ["key"] = key,
                ["operation"] = operation,
                ["changedAt"] = changedAt.ToUniversalTime().ToString("O"),
                ["payload"] = payload is null ? null : JsonSerializer.SerializeToNode(payload, TrackerJson.Options)
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {request.RequestUri.PathAndQuery}");
            if (request.Headers.Authorization?.Scheme != "Bearer" || request.Headers.Authorization.Parameter != Token)
            {
                return Answer(HttpStatusCode.Unauthorized, """{ "error": "unknown token" }""");
            }

            if (path == "/api/ca-tracker/v1/hello")
            {
                return Answer(HttpStatusCode.OK, """{ "name": "CA Tracker server", "protocol": 1, "user": "Pat Engineer" }""");
            }

            if (path == "/api/ca-tracker/v1/changes" && request.Method == HttpMethod.Post)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                foreach (var change in body["changes"]!.AsArray())
                {
                    var stored = change!.DeepClone().AsObject();
                    stored["seq"] = Changes.Count + 1;
                    stored["client"] = body["client"]!.GetValue<string>();
                    stored["user"] = body["user"]!.GetValue<string>();
                    Changes.Add(stored);
                }

                return Answer(HttpStatusCode.OK, $$"""{ "accepted": {{body["changes"]!.AsArray().Count}}, "last": {{Changes.Count}} }""");
            }

            if (path == "/api/ca-tracker/v1/changes" && request.Method == HttpMethod.Get)
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
                var after = int.Parse(query["after"]!);
                var limit = int.Parse(query["limit"]!);
                if (after > Changes.Count)
                {
                    return Answer(HttpStatusCode.Conflict, $$"""{ "error": "reset", "last": {{Changes.Count}} }""");
                }

                var page = new JsonArray(Changes.Skip(after).Take(limit).Select(change => (JsonNode)change.DeepClone()).ToArray());
                var last = page.Count > 0 ? after + page.Count : after;
                return Answer(HttpStatusCode.OK, new JsonObject { ["changes"] = page, ["last"] = last }.ToJsonString());
            }

            return Answer(HttpStatusCode.NotFound, """{ "error": "not found" }""");
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static TrackerProject Project(string id = "p1", string helix = "h1") => new()
    {
        Id = id,
        Name = "CMH04 BB",
        CaFolder = @"Z:\Shared\CMH04\CA",
        Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = helix, ProjectName = "CMH04" }
    };

    private static SnapshotItem Item(string id, string status = "Review") => new()
    {
        ItemType = ItemTypes.Rfi,
        ItemId = id,
        Number = $"RFI-{id}",
        Title = $"Title {id}",
        Status = status,
        Holders = { new SnapshotHolder { Name = "Pat Engineer", State = "current" } }
    };

    private static (TrackerStore Store, FakeCentral Fake, CentralSync Sync) Open(TempFolder temp)
    {
        var store = new TrackerStore(new SqlitePersistence(Path.Combine(temp.Root, "tracker.db")));
        store.SaveSettings(new TrackerSettings { Projects = { Project() } });
        store.SaveSnapshot(new ProviderSnapshot
        {
            Provider = Providers.Helix,
            ProjectId = "h1",
            ProjectName = "CMH04",
            ItemTypes = { ItemTypes.Rfi },
            Complete = true,
            ScannedAt = DateTimeOffset.Now.AddHours(-2),
            Items = { Item("a"), Item("b") }
        });
        var fake = new FakeCentral();
        var sync = new CentralSync(store, new HttpClient(fake), new PlainProtector());
        sync.Configure(Server, Token);
        return (store, fake, sync);
    }

    private static Task<IReadOnlyList<ChangeRecord>> Pending(TrackerStore store) =>
        store.OnDatabaseAsync(database => database.PendingChanges(10_000));

    [Fact]
    public async Task TheFirstShareSendsEverythingOnceAndNothingAfterwards()
    {
        using var temp = new TempFolder();
        var (store, fake, sync) = Open(temp);
        using (store)
        {
            Assert.True(sync.IsConfigured);
            var first = await sync.SyncAsync();
            Assert.Null(first.Problem);
            Assert.Equal(3, first.Sent);
            Assert.Equal(new[] { "helix|h1|rfi|a", "helix|h1|rfi|b", "p1" },
                fake.Changes.Select(change => change["key"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray());
            Assert.Empty(await Pending(store));
            Assert.Equal("CMH04 BB", fake.Changes.Single(change => change["entity"]!.GetValue<string>() == "project")["payload"]!["name"]!.GetValue<string>());

            var second = await sync.SyncAsync();
            Assert.Equal((0, 0, (string?)null), (second.Sent, second.Received, second.Problem));
            Assert.Equal(3, fake.Changes.Count);

            // A change made here goes out with the next share.
            var settings = store.SettingsCopy();
            settings.Projects[0].Disciplines[Disciplines.ItemKey(ItemTypes.Rfi, "a")] = "Mechanical";
            store.SaveSettings(settings);
            Assert.Equal(1, (await sync.SyncAsync()).Sent);
            Assert.Equal("Mechanical", fake.Changes[^1]["payload"]!["disciplines"]![Disciplines.ItemKey(ItemTypes.Rfi, "a")]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task OtherPeoplesItemsArriveForProjectsTrackedHereAndAreNotSentBack()
    {
        using var temp = new TempFolder();
        var (store, fake, sync) = Open(temp);
        using (store)
        {
            await sync.SyncAsync();
            var now = DateTimeOffset.Now;
            fake.AddFromSomeoneElse("item", "helix|h1|rfi|c", Item("c"), now);
            fake.AddFromSomeoneElse("item", "helix|h1|rfi|a", Item("a", "Answered"), now);
            fake.AddFromSomeoneElse("item", "helix|h1|rfi|b", null, now, "delete");
            fake.AddFromSomeoneElse("item", "helix|h9|rfi|z", Item("z"), now); // not tracked here

            var result = await sync.SyncAsync();
            Assert.Null(result.Problem);
            Assert.Equal(3, result.Received);
            var items = store.Snapshot(Providers.Helix, "h1")!.Items.ToDictionary(item => item.ItemId);
            Assert.Equal(new[] { "a", "c" }, items.Keys.Order(StringComparer.Ordinal));
            Assert.Equal("Answered", items["a"].Status);
            Assert.Null(store.Snapshot(Providers.Helix, "h9"));
            Assert.Empty(await Pending(store));

            // Kept after reopening, without being queued to send.
            Assert.True(store.Flush(TimeSpan.FromSeconds(5)));
        }

        using var reopened = new TrackerStore(new SqlitePersistence(Path.Combine(temp.Root, "tracker.db")));
        Assert.Equal("Answered", reopened.Snapshot(Providers.Helix, "h1")!.Items.Single(item => item.ItemId == "a").Status);
        Assert.Empty(await Pending(reopened));
    }

    [Fact]
    public async Task TheFirstShareTakesInEveryoneElsesChoicesBeforeSendingEverything()
    {
        using var temp = new TempFolder();
        var (store, fake, sync) = Open(temp);
        using (store)
        {
            var theirs = Project("their-id");
            theirs.Disciplines[Disciplines.Key(ItemTypes.Rfi, "RFI-001 - Doors")] = "Fire";
            fake.AddFromSomeoneElse("project", "their-id", theirs, DateTimeOffset.Now);

            var result = await sync.SyncAsync();
            Assert.Equal((1, 3), (result.Received, result.Sent));
            var sent = fake.Changes.Last(change => change["entity"]!.GetValue<string>() == "project");
            Assert.Equal("p1", sent["key"]!.GetValue<string>());
            Assert.Equal("Fire", sent["payload"]!["disciplines"]![Disciplines.Key(ItemTypes.Rfi, "RFI-001 - Doors")]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task AServerThatWasResetGetsEverythingAgain()
    {
        using var temp = new TempFolder();
        var (store, fake, sync) = Open(temp);
        using (store)
        {
            await sync.SyncAsync();
            fake.AddFromSomeoneElse("item", "helix|h1|rfi|c", Item("c"), DateTimeOffset.Now);
            await sync.SyncAsync();
            fake.Changes.Clear(); // restored from an empty backup

            var result = await sync.SyncAsync();
            Assert.Null(result.Problem);
            Assert.Equal(4, result.Sent); // the project and items a, b, c
            Assert.Equal(4, fake.Changes.Count);
        }
    }

    [Fact]
    public async Task AWrongTokenIsReportedWithoutLosingAnything()
    {
        using var temp = new TempFolder();
        var (store, _, sync) = Open(temp);
        using (store)
        {
            sync.Configure(Server, "wrong");
            Assert.Contains("access token", await sync.TestAsync());
            var result = await sync.SyncAsync();
            Assert.Contains("access token", result.Problem);
            Assert.Equal(result.Problem, sync.LastProblem);
            Assert.Equal(3, (await Pending(store)).Count);

            sync.Configure(Server, Token);
            Assert.Null(await sync.TestAsync());
            Assert.Equal("Pat Engineer", sync.ConnectedAs);
            Assert.Equal(3, (await sync.SyncAsync()).Sent);

            sync.Disconnect();
            Assert.False(sync.IsConfigured);
            Assert.NotNull((await sync.SyncAsync()).Problem);
        }
    }

    [Fact]
    public void ACompanyServerTakesPrecedenceAndAddressesAreChecked()
    {
        using var store = new TrackerStore(new InMemoryPersistence());
        var sync = new CentralSync(store, new HttpClient(new FakeCentral()), new PlainProtector());
        sync.Configure("https://mine.example.com/", Token);
        Assert.Equal("https://mine.example.com", sync.Server);
        sync.CompanyServer = "https://company.example.com";
        sync.Configure("https://other.example.com", null);
        Assert.Equal("https://company.example.com", sync.Server);
        Assert.Equal("https://mine.example.com", store.Settings.Central.Server);
        Assert.Equal("", CentralSync.NormalizeServer("ca.example.com"));
        Assert.Equal("", CentralSync.NormalizeServer("ftp://ca.example.com"));
        Assert.Equal("http://localhost:8080", CentralSync.NormalizeServer(" http://localhost:8080/ "));
    }

    // ---- What is taken from other people ----

    private static RemoteChange Remote(long seq, string entity, string key, object? payload, DateTimeOffset changedAt, string client = "someone-else") =>
        new(seq, client, "Other Person", entity, key, payload is null ? "delete" : "update", changedAt,
            payload is null ? null : JsonSerializer.SerializeToElement(payload, TrackerJson.Options));

    private static TrackerStore StoreWithProject()
    {
        var store = new TrackerStore(new InMemoryPersistence());
        var project = Project();
        project.Disciplines[Disciplines.Key(ItemTypes.Submittal, "P000210 - Doors")] = "Electrical";
        store.SaveSettings(new TrackerSettings { Projects = { project } });
        return store;
    }

    [Fact]
    public void ChoicesForTheSameHelixProjectAreSharedButNotItsFolderOrName()
    {
        using var store = StoreWithProject();
        var theirs = Project("their-id");
        theirs.Name = "Their name";
        theirs.CaFolder = @"C:\Users\other\CA";
        theirs.Disciplines[Disciplines.Key(ItemTypes.Rfi, "RFI-001 - Doors")] = "Fire";
        // Same phase as here (whole project), so their scope details and folder names are taken too.
        theirs.Scope = new PhaseScope { Phase = ProjectPhase.Whole, FilterItems = true };
        theirs.FoldersOverride = new FolderLayout { RfiFolder = "01 RFIs", SubmittalFolder = "02 Submittals", ReturnedFolder = "~Returned" };

        var applied = CentralSync.Apply(store, new[] { Remote(1, "project", "their-id", theirs, DateTimeOffset.Now) }, "me", new HashSet<string>());

        Assert.Equal(1, applied);
        var mine = store.Settings.Projects.Single();
        Assert.Equal(("p1", "CMH04 BB", @"Z:\Shared\CMH04\CA"), (mine.Id, mine.Name, mine.CaFolder));
        // Choices are combined: theirs are added to the ones made here.
        Assert.Equal(new Dictionary<string, string>
        {
            [Disciplines.Key(ItemTypes.Submittal, "P000210 - Doors")] = "Electrical",
            [Disciplines.Key(ItemTypes.Rfi, "RFI-001 - Doors")] = "Fire"
        }, mine.Disciplines);
        Assert.Equal(ProjectPhase.Whole, mine.Scope.Phase);
        Assert.True(mine.Scope.FilterItems);
        Assert.Equal("01 RFIs", mine.FoldersOverride?.RfiFolder);

        // The same again changes nothing.
        Assert.Equal(0, CentralSync.Apply(store, new[] { Remote(2, "project", "their-id", theirs, DateTimeOffset.Now) }, "me", new HashSet<string>()));
    }

    [Fact]
    public void ATwinOfAnotherPhaseSharesChoicesButKeepsItsOwnScopeAndFolders()
    {
        // Here: a Base Build and a Tenant Fit-Out entry for the same Helix project.
        using var store = new TrackerStore(new InMemoryPersistence());
        var baseBuild = Project("bb");
        baseBuild.Name = "CMH04 BB";
        baseBuild.Scope = new PhaseScope { Phase = ProjectPhase.BaseBuild, FilterFolders = true };
        baseBuild.FoldersOverride = new FolderLayout { RfiFolder = "RFIs", SubmittalFolder = "Submittals", ReturnedFolder = "~Returned (Base Build)" };
        var fitOut = Project("tfo");
        fitOut.Name = "CMH04 TFO";
        fitOut.CaFolder = @"Z:\Shared\CMH04\CA TFO";
        fitOut.Scope = new PhaseScope { Phase = ProjectPhase.TenantFitOut, FilterFolders = true };
        fitOut.FoldersOverride = new FolderLayout { RfiFolder = "RFIs", SubmittalFolder = "Submittals", ReturnedFolder = "~Returned (TFO)" };
        store.SaveSettings(new TrackerSettings { Projects = { baseBuild, fitOut } });

        // Someone else changes their Base Build entry: a new choice, item filtering on, a new returned folder.
        var theirs = Project("their-bb");
        theirs.Disciplines[DoorsKey] = "Electrical";
        theirs.Scope = new PhaseScope { Phase = ProjectPhase.BaseBuild, FilterFolders = true, FilterItems = true };
        theirs.FoldersOverride = new FolderLayout { RfiFolder = "RFIs", SubmittalFolder = "Submittals", ReturnedFolder = "~Returned BB" };

        var applied = CentralSync.Apply(store, new[] { Remote(1, "project", "their-bb", theirs, DateTimeOffset.Now) }, "me", new HashSet<string>());

        Assert.Equal(2, applied);
        var mineBb = store.Settings.Projects.Single(project => project.Id == "bb");
        var mineTfo = store.Settings.Projects.Single(project => project.Id == "tfo");
        // The choice reaches both twins.
        Assert.Equal("Electrical", mineBb.Disciplines[DoorsKey]);
        Assert.Equal("Electrical", mineTfo.Disciplines[DoorsKey]);
        // The Base Build twin follows their Base Build scope and folders...
        Assert.Equal(ProjectPhase.BaseBuild, mineBb.Scope.Phase);
        Assert.True(mineBb.Scope.FilterItems);
        Assert.Equal("~Returned BB", mineBb.FoldersOverride?.ReturnedFolder);
        // ...the Tenant Fit-Out twin stays Tenant Fit-Out with its own folders.
        Assert.Equal(ProjectPhase.TenantFitOut, mineTfo.Scope.Phase);
        Assert.False(mineTfo.Scope.FilterItems);
        Assert.Equal("~Returned (TFO)", mineTfo.FoldersOverride?.ReturnedFolder);
    }

    [Theory]
    [InlineData(@"Z:\Elsewhere\RFIs", "Submittals", "~Returned")]
    [InlineData("RFIs", @"\\server\share\Submittals", "~Returned")]
    [InlineData(@"..\RFIs", "Submittals", "~Returned")]
    [InlineData("RFIs", "Submittals", @"..\..\Returned")]
    [InlineData("Same", "Same", "~Returned")]
    public void FolderNamesFromAnotherPcAreIgnoredUnlessTheyArePlainNames(string rfi, string submittal, string returned)
    {
        using var store = StoreWithProject();
        var theirs = Project("their-id");
        theirs.Disciplines[LightsKey] = "Electrical";
        theirs.FoldersOverride = new FolderLayout { RfiFolder = rfi, SubmittalFolder = submittal, ReturnedFolder = returned };

        var applied = CentralSync.Apply(store, new[] { Remote(1, "project", "their-id", theirs, DateTimeOffset.Now) }, "me", new HashSet<string>());

        Assert.Equal(1, applied);
        var mine = store.Settings.Projects.Single();
        Assert.Equal("Electrical", mine.Disciplines[LightsKey]);
        Assert.Null(mine.FoldersOverride);
        Assert.False(CentralSync.IsSafeOverride(theirs.FoldersOverride));
        Assert.True(CentralSync.IsSafeOverride(new FolderLayout { RfiFolder = "RFIs", SubmittalFolder = "Submittals", ReturnedFolder = @"\\server\share\Returned" }));
    }

    private static readonly string DoorsKey = Disciplines.Key(ItemTypes.Submittal, "P000210 - Doors");
    private static readonly string LightsKey = Disciplines.Key(ItemTypes.Submittal, "P000220 - Lights");

    [Fact]
    public void UnsentChangesMadeHereWin()
    {
        using var store = StoreWithProject();
        var theirs = Project("their-id");
        theirs.Disciplines[DoorsKey] = "Fire";
        theirs.Disciplines[LightsKey] = "Electrical";
        theirs.Scope = new PhaseScope { Phase = ProjectPhase.BaseBuild };
        var pending = new HashSet<string> { "project|p1", "item|helix|h1|rfi|a" };

        var applied = CentralSync.Apply(store, new[]
        {
            Remote(1, "project", "their-id", theirs, DateTimeOffset.Now),
            Remote(2, "item", "helix|h1|rfi|a", Item("a", "Theirs"), DateTimeOffset.Now)
        }, "me", pending);

        // Their new choice is added; the one made here (waiting to be sent) and the scope stay.
        Assert.Equal(1, applied);
        var mine = store.Settings.Projects.Single();
        Assert.Equal("Electrical", mine.Disciplines[DoorsKey]);
        Assert.Equal("Electrical", mine.Disciplines[LightsKey]);
        Assert.Equal(ProjectPhase.Whole, mine.Scope.Phase);
        Assert.Null(store.Snapshot(Providers.Helix, "h1"));
    }

    [Fact]
    public void WhatThisPcSentAfterSomeoneElsesChangeWins()
    {
        using var store = StoreWithProject();
        var theirs = Project("their-id");
        theirs.Disciplines[DoorsKey] = "Fire";
        var mineAsSent = Project();
        mineAsSent.Disciplines[DoorsKey] = "Electrical";

        CentralSync.Apply(store, new[]
        {
            Remote(1, "project", "their-id", theirs, DateTimeOffset.Now),
            Remote(2, "project", "p1", mineAsSent, DateTimeOffset.Now, client: "me")
        }, "me", new HashSet<string>());
        Assert.Equal("Electrical", store.Settings.Projects.Single().Disciplines[DoorsKey]);

        // A change of theirs after it does replace it, including going back to detecting.
        theirs.Disciplines[DoorsKey] = "";
        Assert.Equal(1, CentralSync.Apply(store, new[] { Remote(3, "project", "their-id", theirs, DateTimeOffset.Now) }, "me", new HashSet<string>()));
        Assert.Equal("", store.Settings.Projects.Single().Disciplines[DoorsKey]);
    }

    [Fact]
    public void RemovalsForAProjectNeverReadHereChangeNothing()
    {
        using var store = StoreWithProject();
        Assert.Equal(1, CentralSync.Apply(store, new[] { Remote(1, "item", "helix|h1|rfi|a", null, DateTimeOffset.Now) }, "me", new HashSet<string>()));
        Assert.Null(store.Snapshot(Providers.Helix, "h1"));
    }

    [Fact]
    public void OnlyTheNewestChangeOfEachItemCountsAndOwnChangesAreSkipped()
    {
        using var store = StoreWithProject();
        var now = DateTimeOffset.Now;
        var applied = CentralSync.Apply(store, new[]
        {
            Remote(1, "item", "helix|h1|rfi|a", Item("a", "First"), now),
            Remote(2, "item", "helix|h1|rfi|a", Item("a", "Second"), now),
            Remote(3, "item", "helix|h1|rfi|b", Item("b", "Theirs"), now),
            Remote(4, "item", "helix|h1|rfi|b", Item("b", "Mine"), now, client: "me")
        }, "me", new HashSet<string>());

        Assert.Equal(1, applied);
        var item = store.Snapshot(Providers.Helix, "h1")!.Items.Single();
        Assert.Equal(("a", "Second"), (item.ItemId, item.Status));
    }

    [Fact]
    public void TheMostRecentlyReadVersionWinsEvenWhenItArrivedFirst()
    {
        using var store = StoreWithProject();
        var morning = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
        var noon = morning.AddHours(4);
        var state = new CentralSync.ApplyState();
        string Status() => store.Snapshot(Providers.Helix, "h1")!.Items.Single().Status;

        // A PC sending everything it has re-sends its morning read after someone's noon read.
        CentralSync.Apply(store, new[]
        {
            Remote(1, "item", "helix|h1|rfi|a", Item("a", "Noon"), noon),
            Remote(2, "item", "helix|h1|rfi|a", Item("a", "Morning"), morning)
        }, "me", new HashSet<string>(), state);
        Assert.Equal("Noon", Status());

        // Nor can a later page of the same read undo it.
        Assert.Equal(0, CentralSync.Apply(store, new[] { Remote(3, "item", "helix|h1|rfi|a", Item("a", "Morning"), morning) }, "me", new HashSet<string>(), state));
        Assert.Equal("Noon", Status());
    }

    [Fact]
    public void ReadingTheProjectHereSinceTheyDidKeepsWhatIsHere()
    {
        using var store = StoreWithProject();
        var readHere = DateTimeOffset.Now;
        store.RecordSyncRun(new SyncRun { Provider = Providers.Helix, ProjectId = "h1", StartedAt = readHere, Status = "complete" });

        Assert.Equal(0, CentralSync.Apply(store, new[] { Remote(1, "item", "helix|h1|rfi|a", Item("a"), readHere.AddMinutes(-5)) }, "me", new HashSet<string>()));
        Assert.Null(store.Snapshot(Providers.Helix, "h1"));

        // A failed read here doesn't count; a newer read of theirs does.
        store.RecordSyncRun(new SyncRun { Provider = Providers.Helix, ProjectId = "h1", StartedAt = readHere.AddMinutes(1), Status = "failed" });
        Assert.Equal(1, CentralSync.Apply(store, new[] { Remote(2, "item", "helix|h1|rfi|a", Item("a"), readHere.AddMinutes(5)) }, "me", new HashSet<string>()));
        var snapshot = store.Snapshot(Providers.Helix, "h1")!;
        Assert.Single(snapshot.Items);
        // Never read here: dated when the other person read it.
        Assert.Equal(readHere.AddMinutes(5), snapshot.CoverageFor(ItemTypes.Rfi)!.ScannedAt);
    }

    [Fact]
    public void SomeoneRemovingAProjectFromTheirListLeavesItHere()
    {
        using var store = StoreWithProject();
        Assert.Equal(0, CentralSync.Apply(store, new[] { Remote(1, "project", "p1", null, DateTimeOffset.Now) }, "me", new HashSet<string>()));
        Assert.Single(store.Settings.Projects);
    }

    // ---- The wire format ----

    [Fact]
    public void TheChangesBodyCarriesPayloadsAsJson()
    {
        var changedAt = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.FromHours(-7));
        var body = CentralSync.ChangesBody("pc-1", "Pat", new[]
        {
            new ChangeRecord(7, "item", "helix|h1|rfi|a", "update", changedAt, """{"itemId":"a","title":"Ünïcode \"quoted\""}"""),
            new ChangeRecord(8, "item", "helix|h1|rfi|b", "delete", changedAt, null)
        });

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("pc-1", root.GetProperty("client").GetString());
        Assert.Equal("Pat", root.GetProperty("user").GetString());
        var changes = root.GetProperty("changes");
        Assert.Equal(7, changes[0].GetProperty("seq").GetInt64());
        Assert.Equal("2026-09-30T17:00:00.0000000+00:00", changes[0].GetProperty("changedAt").GetString());
        Assert.Equal("Ünïcode \"quoted\"", changes[0].GetProperty("payload").GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, changes[1].GetProperty("payload").ValueKind);
    }

    [Fact]
    public void PagesAreReadWhateverTheServerLeavesOut()
    {
        var (changes, last) = CentralSync.ParsePage("""
            { "changes": [
                { "seq": 5, "client": "pc-2", "entity": "item", "key": "helix|h1|rfi|a", "operation": "update",
                  "changedAt": "2026-09-30T17:00:00.123456Z", "payload": { "itemId": "a" } },
                { "seq": 6, "entity": "item", "key": "helix|h1|rfi|b", "operation": "delete", "payload": null } ] }
            """, 4);
        Assert.Equal(6, last);
        Assert.Equal(2, changes.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 17, 0, 0, TimeSpan.Zero).AddTicks(1_234_560), changes[0].ChangedAt);
        Assert.True(changes[1].IsDelete);
        Assert.Null(changes[1].Payload);
        Assert.Null(changes[1].Client);

        Assert.Equal(9, CentralSync.ParsePage("""{ "changes": [], "last": 9 }""", 9).Last);
        Assert.Equal(9, CentralSync.ParsePage("""{ "changes": [] }""", 9).Last);
    }
}

public class ChangeLogTests
{
    [Fact]
    public void ChangesFromTheServerAreSavedButNotLogged()
    {
        using var temp = new TempFolder();
        using var database = new SqlitePersistence(Path.Combine(temp.Root, "tracker.db"));
        database.SaveSettings(new TrackerSettings { Projects = { new TrackerProject { Id = "p1", Name = "One" } } });
        Assert.Single(database.PendingChanges(100));

        database.RunFromServer(() => database.SaveSettings(new TrackerSettings
        {
            Projects = { new TrackerProject { Id = "p1", Name = "One" }, new TrackerProject { Id = "p2", Name = "Two" } }
        }));
        Assert.Single(database.PendingChanges(100));
        Assert.Equal(2, database.Load().Settings.Projects.Count);

        // Afterwards changes are logged again.
        database.SaveSettings(new TrackerSettings { Projects = { new TrackerProject { Id = "p1", Name = "Renamed" } } });
        Assert.Equal(new[] { "project|p1|insert", "project|p1|update", "project|p2|delete" },
            database.PendingChanges(100).Select(change => $"{change.Entity}|{change.Key}|{change.Operation}").ToArray());
    }

    [Fact]
    public void SentChangesAreNotSentAgainAndNumbersKeepGoingUp()
    {
        using var temp = new TempFolder();
        using var database = new SqlitePersistence(Path.Combine(temp.Root, "tracker.db"));
        database.SaveSettings(new TrackerSettings { Projects = { new TrackerProject { Id = "p1", Name = "One" } } });
        var first = database.PendingChanges(100).Single();
        Assert.Equal(new[] { "project|p1" }, database.PendingKeys().ToArray());
        database.MarkChangesSent(first.Seq);
        Assert.Empty(database.PendingChanges(100));
        Assert.Empty(database.PendingKeys());

        database.SaveSettings(new TrackerSettings { Projects = { new TrackerProject { Id = "p1", Name = "Two" } } });
        Assert.True(database.PendingChanges(100).Single().Seq > first.Seq);
    }

    [Fact]
    public void QueueEverythingReplacesWhatWasWaiting()
    {
        using var temp = new TempFolder();
        using var database = new SqlitePersistence(Path.Combine(temp.Root, "tracker.db"));
        database.SaveSettings(new TrackerSettings { Projects = { new TrackerProject { Id = "p1", Name = "One" } } });
        database.SaveSettings(new TrackerSettings { Projects = { new TrackerProject { Id = "p1", Name = "Two" } } });
        var readAt = new DateTimeOffset(2026, 9, 28, 9, 30, 0, TimeSpan.Zero);
        database.SaveSnapshot(new ProviderSnapshot
        {
            Provider = Providers.Helix,
            ProjectId = "h1",
            ItemTypes = { ItemTypes.Rfi },
            ScannedAt = readAt,
            Items = { new SnapshotItem { ItemType = ItemTypes.Rfi, ItemId = "a", Number = "1" } }
        });
        Assert.Equal(3, database.PendingChanges(100).Count);

        database.QueueEverything();
        var pending = database.PendingChanges(100);
        Assert.Equal(new[] { "project|p1", "item|helix|h1|rfi|a" }, pending.Select(change => $"{change.Entity}|{change.Key}").ToArray());
        Assert.Contains("\"Two\"", pending[0].Payload);
        // Items are dated when they were read, so a newer read elsewhere wins over them.
        Assert.Equal(readAt, pending[1].ChangedAt);
    }
}
