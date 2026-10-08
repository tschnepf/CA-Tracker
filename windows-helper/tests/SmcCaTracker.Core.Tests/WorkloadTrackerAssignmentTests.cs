using System.Text.Json.Nodes;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.Tests;

/// <summary>Slice 2: binding local projects to tracker projects (D3), teams, and item assignments (D4).</summary>
public sealed class WorkloadTrackerAssignmentTests : IDisposable
{
    private const string Address = "https://tracker.example";
    private readonly FakeTracker tracker = new();
    private readonly TrackerStore store = new(new InMemoryPersistence());
    private DateTimeOffset now = new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);
    private readonly WorkloadTrackerSync sync;

    public WorkloadTrackerAssignmentTests()
    {
        sync = new WorkloadTrackerSync(store, new WorkloadTrackerApi(new HttpClient(tracker)), new PlainProtector(), () => now);
    }

    public void Dispose() => store.Dispose();

    private static ProviderSnapshot Snapshot(string projectId, int items)
    {
        var scanned = new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);
        return new ProviderSnapshot
        {
            SchemaVersion = 1,
            Provider = Providers.Helix,
            ProjectId = projectId,
            ProjectName = $"Project {projectId}",
            ItemTypes = [ItemTypes.Rfi],
            Complete = true,
            ScannedAt = scanned,
            User = new SnapshotUser { FullName = "Casey Lead", Company = "SMC" },
            Coverage = { [ItemTypes.Rfi] = new TypeCoverage { ScannedAt = scanned, Complete = true } },
            Items = Enumerable.Range(1, items).Select(n => new SnapshotItem
            {
                ItemType = ItemTypes.Rfi,
                ItemId = $"{n}",
                Number = $"RFI-{n:D4}",
                Title = $"Item {n}",
                Status = "Open"
            }).ToList()
        };
    }

    private TrackerProject Track(string projectId, PhaseScope? scope = null, string? name = null)
    {
        var project = new TrackerProject
        {
            Name = name ?? projectId,
            Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = projectId },
            Scope = scope ?? new PhaseScope()
        };
        var settings = store.SettingsCopy();
        settings.Projects.Add(project);
        store.SaveSettings(settings);
        return project;
    }

    private async Task PairAsync() => Assert.Null(await sync.ConnectAsync(Address, "token-1", 99, now.AddDays(180)));

    private static TrackerProjectChoice Choice(int id) => new(id, $"CA-{id}", $"Project {id}", "active", "Client");

    private WorkloadBinding Binding(TrackerProject project) => store.Settings.WorkloadTracker.Bindings[project.Id];

    private async Task<TrackerProject> BoundAsync(int trackerProjectId = 12)
    {
        var project = Track("H-1");
        store.SaveSnapshot(Snapshot("H-1", 3));
        await PairAsync();
        sync.SetBinding(project.Id, Choice(trackerProjectId));
        await sync.SendAsync();
        return project;
    }

    private static WorkloadItemRef Item(int trackerProjectId = 12, string number = "RFI-0001") =>
        new(trackerProjectId, CaProviderEnum.Helix, "project.helixteam.com", "H-1", CaItemTypeEnum.Rfi, number);

    private static TeamMember Person(int id) => new(id, $"Person {id}", "Electrical", "Lead");

    // ---- Bindings -------------------------------------------------------------------------------------

    [Fact]
    public async Task ABoundProjectSendsItsBindingInFullAndKeepsTheTrackersAnswer()
    {
        var project = Track("H-1");
        store.SaveSnapshot(Snapshot("H-1", 3));
        await PairAsync();
        await sync.SendAsync();
        tracker.Requests.Clear();
        Assert.Empty(tracker.Observations); // nothing waiting

        sync.SetBinding(project.Id, Choice(12));
        Assert.True(Assert.Single(store.Outbox()).FullSet); // binding sends everything again

        await sync.SendAsync();
        var observation = Assert.Single(tracker.Observations);
        var binding = Assert.Single(observation["bindings"]!.AsArray())!;
        Assert.Equal((12, "whole", true), (binding["trackerProjectId"]!.GetValue<int>(), binding["scope"]!.GetValue<string>(), binding["includeUnlabeled"]!.GetValue<bool>()));
        Assert.Equal(3, observation["items"]!.AsArray().Count);
        Assert.Empty(PinnedSchema.Problems("CaObservationPartRequest", observation.ToJsonString()));
        Assert.Equal(WorkloadBinding.Bound, Binding(project).Outcome);
        Assert.Equal("CA-12 · Project 12", Binding(project).Label);
    }

    [Fact]
    public async Task AConflictShowsTheTrackersChoiceAndUseTheirsTakesIt()
    {
        var project = await BoundAsync();
        tracker.BindingAnswer = _ => ("conflict", 15, "CA-15", "Tower", "Lee Lead");
        store.QueueFullSets();
        await sync.SendAsync();

        var binding = Binding(project);
        Assert.Equal(WorkloadBinding.Conflict, binding.Outcome);
        Assert.Equal((15, "CA-15 · Tower", "Lee Lead"), (binding.TheirProjectId, binding.TheirLabel, binding.TheirAssertedBy));
        Assert.Equal(12, binding.ProjectId); // still this PC's choice until it takes theirs

        tracker.BindingAnswer = null;
        tracker.Requests.Clear();
        Assert.True(sync.UseTheirBinding(project.Id));
        Assert.Equal((15, (string?)null), (Binding(project).ProjectId, Binding(project).Outcome));
        await sync.SendAsync();

        var sent = Assert.Single(Assert.Single(tracker.Observations)["bindings"]!.AsArray())!;
        Assert.Equal(15, sent["trackerProjectId"]!.GetValue<int>());
        Assert.Equal(WorkloadBinding.Bound, Binding(project).Outcome);
        Assert.Null(Binding(project).TheirProjectId);
    }

    [Fact]
    public void BaseBuildAndFitOutEntriesBindTheirPartOfASharedProject()
    {
        var settings = new TrackerSettings();
        var baseBuild = new TrackerProject
        {
            Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = "H-1" },
            Scope = new PhaseScope { Phase = ProjectPhase.BaseBuild, FilterItems = true, IncludeUnlabeled = true }
        };
        var fitOut = new TrackerProject
        {
            Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = "H-1" },
            Scope = new PhaseScope { Phase = ProjectPhase.TenantFitOut, FilterItems = true }
        };
        settings.Projects.AddRange([baseBuild, fitOut]);
        settings.WorkloadTracker.Bindings[baseBuild.Id] = new WorkloadBinding { ProjectId = 12, ProjectName = "BB" };
        settings.WorkloadTracker.Bindings[fitOut.Id] = new WorkloadBinding { ProjectId = 13, ProjectName = "TFO" };

        var bindings = WorkloadTrackerSync.BindingsFor(settings, "helix", "H-1");
        Assert.Equal([(12, "base_build", true), (13, "tenant_fit_out", false)],
            bindings.Select(binding => (binding.TrackerProjectId, binding.Scope, binding.IncludeUnlabeled)));

        // A Base Build entry whose Helix project holds only Base Build items binds all of it.
        fitOut.Scope.FilterItems = false;
        baseBuild.Scope.FilterItems = false;
        Assert.Equal(["whole"], WorkloadTrackerSync.BindingsFor(settings, "helix", "H-1").Select(binding => binding.Scope));
        Assert.Empty(WorkloadTrackerSync.BindingsFor(settings, "helix", "H-2"));
    }

    [Fact]
    public async Task BindingsGoWithTheirProjectAndAreForgottenForAnotherTracker()
    {
        var project = await BoundAsync();
        var other = Track("H-2");
        sync.SetBinding(other.Id, Choice(20));
        Assert.Equal(2, store.Settings.WorkloadTracker.Bindings.Count);

        var settings = store.SettingsCopy();
        settings.Projects.RemoveAll(entry => entry.Id == other.Id);
        store.SaveSettings(settings);
        Assert.Equal([project.Id], store.Settings.WorkloadTracker.Bindings.Keys);

        await sync.DisconnectAsync();
        Assert.Single(store.Settings.WorkloadTracker.Bindings); // this PC's choice survives disconnecting

        Assert.Null(await sync.ConnectAsync("https://other-tracker.example", "token-2", 5, null));
        Assert.Empty(store.Settings.WorkloadTracker.Bindings);
    }

    // ---- Search and team ------------------------------------------------------------------------------

    [Fact]
    public async Task ProjectSearchAsksForRunningProjectsAndReadsOnlyWhatItUses()
    {
        await PairAsync();
        tracker.SearchBody = """
            {"count": 3, "next": "https://tracker.example/api/projects/search/?page=2", "previous": null, "rolesByDepartment": {},
             "results": [
               {"id": 12, "projectNumber": "25.078", "name": "CMH01", "status": "active", "client": "ADC", "notesJson": {"x": 1}},
               {"id": 13, "projectNumber": null, "name": "Tower", "status": "active_ca", "client": null},
               {"id": "bad", "name": "Skipped"}
             ]}
            """;

        var (projects, more, problem) = await sync.SearchProjectsAsync("cmh");

        Assert.Null(problem);
        Assert.True(more);
        Assert.Equal(["25.078 · CMH01", "Tower"], projects.Select(project => project.Label));
        var request = tracker.Requests.Last();
        Assert.Equal((HttpMethod.Post, "/api/projects/search/"), (request.Method, request.Path));
        Assert.Equal("active,active_ca", request.Body!["status_in"]!.GetValue<string>());
        Assert.Equal("cmh", request.Body["search_tokens"]![0]!["term"]!.GetValue<string>());
        Assert.Equal(25, request.Body["page_size"]!.GetValue<int>());
        Assert.Empty(PinnedSchema.Problems("ProjectSearchRequestRequest", request.Body.ToJsonString()));
    }

    [Fact]
    public async Task TheTeamIsReadToTheLastPageOncePerPerson()
    {
        await PairAsync();
        tracker.TeamPages.Add("""
            {"count": 4, "next": "https://tracker.example/api/assignments/?page=2", "previous": null, "results": [
              {"id": 1, "person": 39, "personName": "Julia Jackson", "personDepartmentName": "Electrical", "roleName": "Electrical Lead"},
              {"id": 2, "person": null, "personName": "", "personDepartmentName": null, "roleName": "Placeholder"},
              {"id": 3, "person": 99, "personName": "Braydon Lilley", "personDepartmentName": "Mechanical", "roleName": "Mechanical Support"}
            ]}
            """);
        tracker.TeamPages.Add("""
            {"count": 4, "next": null, "previous": "https://tracker.example/api/assignments/?page=1", "results": [
              {"id": 4, "person": 39, "personName": "Julia Jackson", "personDepartmentName": "Electrical", "roleName": "CA Reviewer"}
            ]}
            """);

        var (team, problem) = await sync.TeamAsync(12);

        Assert.Null(problem);
        Assert.Equal(
            [new TeamMember(99, "Braydon Lilley", "Mechanical", "Mechanical Support"), new TeamMember(39, "Julia Jackson", "Electrical", "Electrical Lead, CA Reviewer")],
            team);
        var reads = tracker.Requests.Where(request => request.Path == "/api/assignments/").ToList();
        Assert.Equal(2, reads.Count);

        await sync.TeamAsync(12); // kept for a while
        Assert.Equal(2, tracker.Requests.Count(request => request.Path == "/api/assignments/"));
        now += WorkloadTrackerSync.TeamCacheAge;
        await sync.TeamAsync(12);
        Assert.Equal(4, tracker.Requests.Count(request => request.Path == "/api/assignments/"));
    }

    [Fact]
    public async Task SearchAndTeamNeedAConnection()
    {
        Assert.NotNull((await sync.SearchProjectsAsync("x")).Problem);
        Assert.NotNull((await sync.TeamAsync(12)).Problem);
        Assert.Empty(tracker.Requests);
    }

    // ---- Assignments ----------------------------------------------------------------------------------

    [Fact]
    public async Task AnAssignmentShowsAtOnceIsSentAndIsReadBack()
    {
        await BoundAsync();
        sync.ChangeAssignments([Item()], [Person(39), Person(99)], []);

        var shown = store.AssignmentIndex()[Item().Key];
        Assert.Equal(["Person 39", "Person 99"], shown.Select(row => row.PersonName));
        Assert.All(shown, row => Assert.True(row.Pending));
        Assert.Equal(2, sync.PendingAssignmentCount);

        await sync.SendAsync();

        var post = Assert.Single(tracker.Requests, request => request.Path == "/api/ca/assignments/" && request.Method == HttpMethod.Post);
        Assert.Empty(PinnedSchema.Problems("CaAssignmentBatchRequest", post.Body!.ToJsonString()));
        Assert.Equal("2026-10-07T15:00:00+00:00", post.Body["assignments"]![0]!["assignedAt"]!.GetValue<string>());
        Assert.Equal(0, sync.PendingAssignmentCount);
        Assert.All(store.AssignmentIndex()[Item().Key], row => Assert.Equal("Casey Lead", row.AssignedByName));
        var read = Assert.Single(tracker.Requests, request => request.Path == "/api/ca/assignments/" && request.Method == HttpMethod.Get);
        Assert.False(read.Headers.ContainsKey("If-None-Match"));

        // Read back again only when due, and then with the ETag.
        await sync.SendAsync();
        Assert.Single(tracker.Requests, request => request.Path == "/api/ca/assignments/" && request.Method == HttpMethod.Get);
        await sync.SendAsync(readAssignments: true);
        Assert.True(tracker.Requests.Last().Headers.ContainsKey("If-None-Match"));
        Assert.Equal(2, store.AssignmentIndex()[Item().Key].Count);
    }

    [Fact]
    public async Task UnassigningRemovesThePersonAndSomeoneElsesChangeIsReadBack()
    {
        await BoundAsync();
        sync.ChangeAssignments([Item(), Item(number: "RFI-0002")], [Person(39)], []);
        await sync.SendAsync();

        now = now.AddMinutes(1);
        sync.ChangeAssignments([Item()], [], [39]);
        Assert.False(store.AssignmentIndex().ContainsKey(Item().Key));
        await sync.SendAsync();
        Assert.False(tracker.Assigned.Values.Single(row => row["number"]!.GetValue<string>() == "RFI-0001")["active"]!.GetValue<bool>());

        // Unassigning someone who isn't assigned changes nothing.
        sync.ChangeAssignments([Item(number: "RFI-0003")], [], [39]);
        Assert.Equal(0, sync.PendingAssignmentCount);

        // Someone else assigns on another PC: the next read-back shows it.
        tracker.Assigned["x"] = new JsonObject
        {
            ["id"] = 99, ["trackerProjectId"] = 12,
            ["providerProject"] = new JsonObject { ["provider"] = "helix", ["host"] = "project.helixteam.com", ["projectId"] = "H-1" },
            ["itemType"] = "rfi", ["number"] = "RFI-0003", ["personId"] = 7, ["personName"] = "Lee Lead", ["personActive"] = true,
            ["assignedByName"] = "Lee Lead", ["assignedAt"] = "2026-10-07T15:30:00Z", ["active"] = true, ["completedAt"] = null
        };
        now += WorkloadTrackerSync.AssignmentReadInterval;
        await sync.SendAsync();
        Assert.Equal("Lee Lead", Assert.Single(store.AssignmentIndex()[Item(number: "RFI-0003").Key]).PersonName);
    }

    [Fact]
    public async Task ARefusedAssignmentIsDroppedAndExplained()
    {
        await BoundAsync();
        tracker.PeopleWhoLeft.Add(77);
        sync.ChangeAssignments([Item()], [Person(77)], []);

        await sync.SendAsync();

        Assert.False(store.AssignmentIndex().ContainsKey(Item().Key));
        Assert.Contains("has left", sync.LastProblem);
        Assert.Equal(0, sync.PendingAssignmentCount);
    }

    [Fact]
    public async Task DisconnectingForgetsAssignments()
    {
        await BoundAsync();
        sync.ChangeAssignments([Item()], [Person(39)], []);
        await sync.DisconnectAsync();
        Assert.Empty(store.Assignments());

        sync.ChangeAssignments([Item()], [Person(39)], []); // unpaired: nothing is kept
        Assert.Empty(store.Assignments());
    }

    [Fact]
    public async Task AChangeMadeWhileSendingWinsOverTheAnswer()
    {
        await PairAsync();
        var sent = Row(39, active: true, minute: 0);
        store.ChangeAssignments([sent]);
        var sending = store.PendingAssignments().Single();

        store.ChangeAssignments([Row(39, active: false, minute: 1)]); // unassigned while the first change was on its way
        store.CompleteAssignments([(sending, Tracked(Row(39, active: true, minute: 0)))]);

        var kept = Assert.Single(store.Assignments());
        Assert.True(kept.Pending);
        Assert.False(kept.Active);
    }

    [Fact]
    public async Task ReadBackKeepsNewerChangesMadeHere()
    {
        await PairAsync();
        store.ChangeAssignments([Row(39, active: true, minute: 5), Row(40, active: true, minute: 5)]);

        store.ReplaceAssignments(12, [Tracked(Row(39, active: false, minute: 1)), Tracked(Row(40, active: false, minute: 9)), Tracked(Row(41, active: true, minute: 2))]);

        var rows = store.Assignments(12).OrderBy(row => row.PersonId).ToList();
        Assert.Equal([(39, true, true), (40, false, false), (41, true, false)], rows.Select(row => (row.PersonId, row.Active, row.Pending)));
    }

    // ---- Marked complete in Workload Tracker ------------------------------------------------------------

    private static JsonObject Mark(int personId, string itemId = "1", int trackerProjectId = 12) => new()
    {
        ["trackerProjectId"] = trackerProjectId,
        ["providerProject"] = new JsonObject { ["provider"] = "helix", ["host"] = "project.helixteam.com", ["projectId"] = "H-1" },
        ["itemType"] = "rfi", ["providerItemId"] = itemId, ["number"] = $"RFI-{int.Parse(itemId):D4}", ["revision"] = null,
        ["personId"] = personId, ["personName"] = $"Person {personId}", ["markedByName"] = "Casey Lead", ["markedAt"] = "2026-10-07T16:00:00Z"
    };

    private IEnumerable<(HttpMethod Method, string Path, Dictionary<string, string> Headers, JsonNode? Body)> Reads =>
        tracker.Requests.Where(request => request.Path == "/api/ca/assignments/" && request.Method == HttpMethod.Get);

    [Fact]
    public async Task MarksAreReadBackWithTheAssignmentsAndGoWhenTheTrackerStopsListingThem()
    {
        await BoundAsync();
        tracker.Completions.Add(Mark(39));

        await sync.SendAsync(readAssignments: true);

        var mark = Assert.Single(store.Completions(12));
        Assert.Equal(("helix", "H-1", "rfi", "1", "RFI-0001", (string?)null), (mark.Provider, mark.ProviderProjectId, mark.ItemType, mark.ProviderItemId, mark.Number, mark.Revision));
        Assert.Equal((39, "Person 39", "Casey Lead"), (mark.PersonId, mark.PersonName, mark.MarkedByName));
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 16, 0, 0, TimeSpan.Zero), mark.MarkedAt);
        Assert.Single(store.CompletionIndex()["helix|H-1|rfi|1"]);

        // Unchanged: the tracker answers 304 and the mark is kept.
        await sync.SendAsync(readAssignments: true);
        Assert.True(Reads.Last().Headers.ContainsKey("If-None-Match"));
        Assert.Single(store.Completions(12));

        // Returned in Helix (or the mark taken back): the tracker stops listing it.
        tracker.Completions.Clear();
        await sync.SendAsync(readAssignments: true);
        Assert.Empty(store.Completions(12));
    }

    [Fact]
    public async Task MarksGoWhenTheProjectCantBeSeenOrOnDisconnecting()
    {
        await BoundAsync();
        tracker.Completions.Add(Mark(39));
        tracker.Completions.Add(Mark(40, itemId: "2"));
        await sync.SendAsync(readAssignments: true);
        Assert.Equal(2, store.Completions().Count);

        tracker.HiddenProjects.Add(12);
        await sync.SendAsync(readAssignments: true);
        Assert.Empty(store.Completions());

        tracker.HiddenProjects.Clear();
        await sync.SendAsync(readAssignments: true);
        Assert.Equal(2, store.Completions().Count);

        await sync.DisconnectAsync();
        Assert.Empty(store.Completions());
    }

    private WorkloadAssignment Row(int personId, bool active, int minute) => new()
    {
        TrackerProjectId = 12,
        Provider = "helix",
        Host = "project.helixteam.com",
        ProviderProjectId = "H-1",
        ItemType = "rfi",
        Number = "RFI-0001",
        PersonId = personId,
        PersonName = $"Person {personId}",
        Active = active,
        AssignedAt = now.AddMinutes(minute)
    };

    private static WorkloadAssignment Tracked(WorkloadAssignment row)
    {
        row.Pending = false;
        return row;
    }
}
