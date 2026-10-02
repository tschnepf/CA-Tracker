using System.Text.Json;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Downloads;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Operations;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Tests;

public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Root = Path.Combine(Path.GetTempPath(), "ca-tracker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Make(params string[] parts)
    {
        var path = Path.Combine(new[] { Root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Build
{
    public static SnapshotItem Item(string id, string number, string type = ItemTypes.Rfi, bool closed = false, params SnapshotHolder[] holders) => new()
    {
        ItemId = id,
        ItemType = type,
        Number = number,
        Title = $"Item {number}",
        Status = closed ? "Complete" : "Review",
        Closed = closed,
        Holders = holders.ToList()
    };

    public static SnapshotHolder Holder(string company, string state) => new()
    {
        Name = $"{company} person",
        CompanyId = company,
        Company = company,
        State = state
    };

    public static ProviderSnapshot Snapshot(params SnapshotItem[] items) => new()
    {
        SchemaVersion = 1,
        Provider = Providers.Helix,
        ProjectId = "p1",
        ProjectName = "Aligned CMH04",
        ItemTypes = new List<string> { ItemTypes.Rfi, ItemTypes.Submittal },
        Complete = true,
        ScannedAt = DateTimeOffset.Parse("2026-09-28T20:00:00Z"),
        User = new SnapshotUser { FullName = "Pat", CompanyId = "smc", Company = "SMC" },
        Items = items.ToList()
    };
}

public class ItemNumberTests
{
    [Theory]
    [InlineData("RFI-012", "RFI012")]
    [InlineData("03 30 00-1", "0330001")]
    [InlineData("p000210", "P000210")]
    public void ExactKeysKeepOnlyLettersAndDigits(string value, string expected) =>
        Assert.Equal(expected, ItemNumber.ExactKey(value));

    [Theory]
    [InlineData("RFI 012", "12")]
    [InlineData("12", "12")]
    [InlineData("SUB-0007", "7")]
    [InlineData("RFI", "RFI")]
    public void LooseKeysDropTypePrefixesAndLeadingZeros(string value, string expected) =>
        Assert.Equal(expected, ItemNumber.LooseKey(value));

    [Fact]
    public void FolderCandidatesPreferTheTextBeforeTheTitleSeparator()
    {
        Assert.Equal(new[] { "P000210" }, ItemNumber.FolderNumberCandidates("P000210 - Overhead Coiling Doors"));
        Assert.Equal(new[] { "03 30 00-1" }, ItemNumber.FolderNumberCandidates("03 30 00-1 - Concrete"));
        Assert.Equal(new[] { "RFI", "RFI 012" }, ItemNumber.FolderNumberCandidates("RFI 012 Slab edge"));
    }

    [Fact]
    public void NaturalOrderReadsNumbersAsNumbers()
    {
        Assert.True(ItemNumber.CompareNatural("RFI-2", "RFI-10") < 0);
        Assert.True(ItemNumber.CompareNatural("P000210", "P000209") > 0);
    }
}

public class MatcherTests
{
    private static FolderEntry Folder(string name, FolderLocation location = FolderLocation.Active) =>
        new(name, Path.Combine("C:", "CA", name), location, DateTime.UtcNow);

    [Fact]
    public void MatchesExactlyThenLoosely()
    {
        var items = new[] { Build.Item("a", "RFI-012"), Build.Item("b", "7") };
        var matches = FolderMatcher.Match(new[] { Folder("RFI-012 - Slab"), Folder("RFI 007 Door") }, items);
        Assert.Equal("a", matches[0].Item!.ItemId);
        Assert.Equal(MatchKind.Exact, matches[0].Kind);
        Assert.Equal("b", matches[1].Item!.ItemId);
        Assert.Equal(MatchKind.Loose, matches[1].Kind);
    }

    [Fact]
    public void ARevisedItemMatchesTheFolderNamedByItsNumber()
    {
        var revised = Build.Item("b", "P1");
        revised.Revision = "2";
        var match = Assert.Single(FolderMatcher.Match(new[] { Folder("P1 - Title") }, new[] { revised }));
        Assert.Equal("b", match.Item!.ItemId);
        Assert.Equal(MatchKind.Exact, match.Kind);
    }

    [Fact]
    public void TheNewestRevisionDecidesAboutTheFolder()
    {
        // Procore keeps each revision as its own submittal with the same number. Rev 0 is closed (it would
        // say "ready to return"); rev 2 is back with SMC, so the folder is open — and rev 0 isn't listed.
        var old = Build.Item("a", "P1", ItemTypes.Submittal, closed: true);
        old.Revision = "0";
        var current = Build.Item("b", "P1", ItemTypes.Submittal, holders: Build.Holder("smc", "current"));
        current.Revision = "2";
        var scan = new TypeFolderScan { ItemType = ItemTypes.Submittal, TypeFolderExists = true, Folders = new[] { Folder("P1 - Title") } };

        var result = ProjectComparer.CompareType(ItemTypes.Submittal, scan, Build.Snapshot(old, current),
            new CompanyIdentity("smc", "SMC"), holdersReported: true, new ReturnedRule());

        var row = Assert.Single(result.Rows);
        Assert.Equal("b", row.Item!.ItemId);
        Assert.Equal(RowStatus.Open, row.Status);
    }

    [Fact]
    public void ReportsAmbiguousAndUnmatchedFolders()
    {
        var items = new[] { Build.Item("a", "12"), Build.Item("b", "012") };
        var matches = FolderMatcher.Match(new[] { Folder("RFI 12 - Thing"), Folder("Meeting notes") }, items);
        Assert.Equal(MatchKind.Ambiguous, matches[0].Kind);
        Assert.Null(matches[0].Item);
        Assert.Equal(MatchKind.None, matches[1].Kind);
    }
}

public class CourtTests
{
    private static readonly CompanyIdentity Smc = new("smc", "SMC");

    [Fact]
    public void ReturnedMeansNoOneFromTheCompanyHoldsIt()
    {
        var held = Build.Item("1", "1", holders: new[] { Build.Holder("smc", "current") });
        var elsewhere = Build.Item("2", "2", holders: new[] { Build.Holder("arch", "current") });
        var later = Build.Item("3", "3", holders: new[] { Build.Holder("arch", "current"), Build.Holder("smc", "upcoming") });
        var closed = Build.Item("4", "4", closed: true);
        var rule = new ReturnedRule();

        Assert.Equal(CourtState.InCourt, CourtEvaluator.Evaluate(held, Smc, true));
        Assert.Equal(CourtState.NotHeld, CourtEvaluator.Evaluate(elsewhere, Smc, true));
        Assert.Equal(CourtState.ComingUp, CourtEvaluator.Evaluate(later, Smc, true));
        Assert.Equal(CourtState.Closed, CourtEvaluator.Evaluate(closed, Smc, true));

        Assert.False(CourtEvaluator.IsReturned(held, CourtState.InCourt, rule));
        Assert.True(CourtEvaluator.IsReturned(elsewhere, CourtState.NotHeld, rule));
        Assert.False(CourtEvaluator.IsReturned(later, CourtState.ComingUp, rule));
        Assert.True(CourtEvaluator.IsReturned(closed, CourtState.Closed, rule));
    }

    [Fact]
    public void StatusListsOverrideTheCourtRule()
    {
        var item = Build.Item("1", "1", holders: new[] { Build.Holder("smc", "current") });
        item.Status = "Revise and Resubmit";
        var rule = new ReturnedRule { AlwaysReturnedStatuses = { "revise and resubmit" } };
        Assert.True(CourtEvaluator.IsReturned(item, CourtState.InCourt, rule));
        rule.NeverReturnedStatuses.Add("Revise and Resubmit");
        Assert.False(CourtEvaluator.IsReturned(item, CourtState.InCourt, rule));
    }

    [Fact]
    public void UnreportedHoldersAreUnknownRatherThanReturned()
    {
        var item = Build.Item("1", "1");
        Assert.Equal(CourtState.Unknown, CourtEvaluator.Evaluate(item, Smc, holdersReported: false));
        Assert.False(CourtEvaluator.IsReturned(item, CourtState.Unknown, new ReturnedRule()));
    }
}

public class SnapshotStoreTests
{
    [Fact]
    public void CompleteScansReplaceOnlyTheTypesTheyCover()
    {
        var existing = Build.Snapshot(Build.Item("r1", "1"), Build.Item("s1", "S1", ItemTypes.Submittal));
        var incoming = Build.Snapshot(Build.Item("r2", "2"));
        incoming.ItemTypes = new List<string> { ItemTypes.Rfi };
        var merged = SnapshotStore.Merge(existing, incoming);
        Assert.Equal(new[] { "s1", "r2" }, merged.Items.Select(item => item.ItemId));
        Assert.Equal(2, merged.ItemTypes.Count);
    }

    [Fact]
    public void PartialScansOnlyAddOrUpdateRows()
    {
        var existing = Build.Snapshot(Build.Item("r1", "1"));
        existing.Provider = Providers.Procore;
        var incoming = Build.Snapshot(Build.Item("r2", "2"));
        incoming.Provider = Providers.Procore;
        incoming.Complete = false;
        incoming.ItemTypes = new List<string> { ItemTypes.Rfi };
        var merged = SnapshotStore.Merge(existing, incoming);
        Assert.Equal(new[] { "r1", "r2" }, merged.Items.Select(item => item.ItemId).OrderBy(id => id));
        Assert.False(merged.CoverageFor(ItemTypes.Rfi)!.Complete);
    }

    [Fact]
    public void SnapshotsReadFromThePageAreStoredAndReloaded()
    {
        using var temp = new TempFolder();
        var store = new SnapshotStore(new AppPaths(temp.Root));
        // The shape snapshot.js posts back from the built-in browser.
        var json = """
        {"schemaVersion":1,"provider":"helix","accountId":null,"projectId":"p1",
         "projectName":"Aligned CMH04","projectUrl":null,"itemTypes":["rfi"],"complete":true,
         "scannedAt":"2026-09-28T20:00:00.000Z","user":{"fullName":"Pat","companyId":"smc","company":"SMC"},
         "items":[{"itemType":"rfi","itemId":"i1","number":"RFI-012","title":"Slab","status":"Review","statusRank":800,
         "closed":false,"responsible":"","holders":[{"name":"Alex","companyId":"arch","company":"Arch","state":"current"}],
         "attachmentCount":2,"createdAt":null,"modifiedAt":"2026-09-01T00:00:00.000Z","dueAt":null,"sourceUrl":"https://example.test"}]}
        """;
        var snapshot = JsonSerializer.Deserialize<ProviderSnapshot>(json, TrackerJson.Options)!;
        store.Save(snapshot);
        var stored = store.Load("helix", "p1");
        Assert.NotNull(stored);
        Assert.Equal("RFI-012", stored!.Items[0].Number);
        Assert.Equal("arch", stored.Items[0].Holders[0].CompanyId);
    }
}

public class ComparisonAndOperationTests
{
    private static (TempFolder Temp, TrackerSettings Settings, TrackerProject Mapping) Setup()
    {
        var temp = new TempFolder();
        var ca = temp.Make("CMH04", "CA");
        temp.Make("CMH04", "CA", "RFIs", "RFI-001 - Held by us");
        temp.Make("CMH04", "CA", "RFIs", "RFI-002 - With architect");
        temp.Make("CMH04", "CA", "RFIs", "~Returned", "RFI-003 - Back with us");
        temp.Make("CMH04", "CA", "RFIs", "~Returned", "RFI-004 - Closed");
        temp.Make("CMH04", "CA", "RFIs", "Scratch");
        var settings = new TrackerSettings();
        var mapping = new TrackerProject
        {
            Name = "CMH04",
            CaFolder = ca,
            Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = "p1", ProjectName = "Aligned CMH04" }
        };
        settings.Projects.Add(mapping);
        return (temp, settings, mapping);
    }

    [Fact]
    public void ProcoreBallInCourtWithCompaniesDecidesLikeHelix()
    {
        using var temp = new TempFolder();
        var ca = temp.Make("Job", "CA");
        temp.Make("Job", "CA", "RFIs", "~Returned", "12 - Slab edge");
        var settings = new TrackerSettings();
        var project = new TrackerProject
        {
            Name = "Job",
            CaFolder = ca,
            Connection = new ProviderConnection { Provider = Providers.Procore, ProjectId = "34", AccountId = "12" }
        };
        settings.Projects.Add(project);

        // Back with SMC (Procore names the person and their company): the folder in ~Returned is back in court.
        var snapshot = Build.Snapshot(Build.Item("901", "12", holders: Build.Holder("smc", "current")));
        snapshot.Provider = Providers.Procore;
        var row = Assert.Single(ProjectComparer.Compare(snapshot, project, settings).For(ItemTypes.Rfi).Rows);
        Assert.Equal(RowStatus.Reopened, row.Status);
        Assert.Equal(CourtState.InCourt, row.Court);

        // Only names (Procore's list page): whose company can't be told, so it stays unknown as before.
        var namesOnly = Build.Snapshot(Build.Item("901", "12", holders: new SnapshotHolder { Name = "Sam", State = "current" }));
        namesOnly.Provider = Providers.Procore;
        Assert.Equal(CourtState.Unknown, Assert.Single(ProjectComparer.Compare(namesOnly, project, settings).For(ItemTypes.Rfi).Rows).Court);

        // A list-page read that updated one item of a background read: that item isn't taken as out of court.
        temp.Make("Job", "CA", "RFIs", "13 - Door hardware");
        var mixed = Build.Snapshot(
            Build.Item("901", "12", holders: Build.Holder("smc", "current")),
            Build.Item("902", "13", holders: new SnapshotHolder { Name = "Sam", State = "current" }));
        mixed.Provider = Providers.Procore;
        var rows = ProjectComparer.Compare(mixed, project, settings).For(ItemTypes.Rfi).Rows;
        var listed = Assert.Single(rows, candidate => candidate.Item?.Number == "13");
        Assert.Equal(CourtState.Unknown, listed.Court);
        Assert.Equal(RowStatus.Open, listed.Status);
        Assert.Equal(CourtState.InCourt, Assert.Single(rows, candidate => candidate.Item?.Number == "12").Court);
    }

    private static ProviderSnapshot Snapshot() => Build.Snapshot(
        Build.Item("1", "RFI-001", holders: new[] { Build.Holder("smc", "current") }),
        Build.Item("2", "RFI-002", holders: new[] { Build.Holder("arch", "current") }),
        Build.Item("3", "RFI-003", holders: new[] { Build.Holder("smc", "current") }),
        Build.Item("4", "RFI-004", closed: true),
        Build.Item("5", "RFI-005", holders: new[] { Build.Holder("smc", "upcoming") }));

    [Fact]
    public void ComparisonClassifiesEveryFolderAndItem()
    {
        var (temp, settings, mapping) = Setup();
        using (temp)
        {
            var result = ProjectComparer.Compare(Snapshot(), mapping, settings).For(ItemTypes.Rfi);
            var byNumber = result.Rows.ToDictionary(row => row.Item?.Number ?? row.Folder!.Name, row => row.Status);
            Assert.Equal(RowStatus.Open, byNumber["RFI-001"]);
            Assert.Equal(RowStatus.ReadyToReturn, byNumber["RFI-002"]);
            Assert.Equal(RowStatus.Reopened, byNumber["RFI-003"]);
            Assert.Equal(RowStatus.Filed, byNumber["RFI-004"]);
            Assert.Equal(RowStatus.NoFolder, byNumber["RFI-005"]);
            Assert.Equal(RowStatus.NotInProvider, byNumber["Scratch"]);
            Assert.Equal(RowStatus.ReadyToReturn, result.Rows[0].Status);
        }
    }

    [Fact]
    public void PlanningChangesNothingAndExecutionMovesOnlyConfirmedFolders()
    {
        var (temp, settings, mapping) = Setup();
        using (temp)
        {
            var comparison = ProjectComparer.Compare(Snapshot(), mapping, settings);
            var ready = comparison.For(ItemTypes.Rfi).Rows.Where(row => row.Status == RowStatus.ReadyToReturn).ToList();
            var plan = OperationPlanner.PlanMarkReturned(ready, comparison);

            var source = Path.Combine(mapping.CaFolder, "RFIs", "RFI-002 - With architect");
            var destination = Path.Combine(mapping.CaFolder, "RFIs", "~Returned", "RFI-002 - With architect");
            Assert.Single(plan.Operations);
            Assert.True(Directory.Exists(source));
            Assert.False(Directory.Exists(destination));

            var results = new OperationExecutor(new AppPaths(temp.Make("appdata"))).Execute(plan);
            Assert.All(results, result => Assert.Equal(OperationOutcome.Done, result.Outcome));
            Assert.False(Directory.Exists(source));
            Assert.True(Directory.Exists(destination));
        }
    }

    [Fact]
    public void PlansBlockCollisionsAndCreateMissingReturnedFolders()
    {
        var (temp, settings, mapping) = Setup();
        using (temp)
        {
            settings.Folders.ReturnedFolder = "_Returned";
            temp.Make("CMH04", "CA", "RFIs", "RFI-009 - Dup");
            var comparison = ProjectComparer.Compare(Snapshot(), mapping, settings);
            var rows = comparison.For(ItemTypes.Rfi).Rows.Where(row => row.CanMarkReturned).ToList();
            var plan = OperationPlanner.PlanMarkReturned(rows, comparison);
            Assert.Equal(OperationKind.CreateFolder, plan.Operations[0].Kind);
            Assert.EndsWith("_Returned", plan.Operations[0].DestinationPath);
            Assert.False(Directory.Exists(plan.Operations[0].DestinationPath));

            Directory.CreateDirectory(Path.Combine(mapping.CaFolder, "RFIs", "_Returned", "RFI-001 - Held by us"));
            var blocked = OperationPlanner.PlanMarkReturned(rows, ProjectComparer.Compare(Snapshot(), mapping, settings));
            Assert.Contains(blocked.Operations, operation => operation.IsBlocked && operation.Title.Contains("RFI-001"));
            Assert.Contains(blocked.Operations, operation => operation.Warning is not null && operation.Title.Contains("RFI-001"));
        }
    }

    [Fact]
    public void MoveBackReturnsFoldersToTheActiveFolder()
    {
        var (temp, settings, mapping) = Setup();
        using (temp)
        {
            var comparison = ProjectComparer.Compare(Snapshot(), mapping, settings);
            var reopened = comparison.For(ItemTypes.Rfi).Rows.Where(row => row.Status == RowStatus.Reopened).ToList();
            var plan = OperationPlanner.PlanMoveBack(reopened, comparison);
            Assert.Single(plan.Operations);
            new OperationExecutor(new AppPaths(temp.Make("appdata"))).Execute(plan);
            Assert.True(Directory.Exists(Path.Combine(mapping.CaFolder, "RFIs", "RFI-003 - Back with us")));
        }
    }

    [Fact]
    public void FolderNamesAreValidated()
    {
        Assert.True(FolderNames.IsValid("~Returned"));
        Assert.False(FolderNames.IsValid("Bad:Name"));
        Assert.False(FolderNames.IsValid(" padded"));
        Assert.False(FolderNames.IsValid("CON"));
        Assert.NotEmpty(new FolderLayout { RfiFolder = "Same", SubmittalFolder = "same" }.Validate());
    }
}

public class RefreshTests
{
    private static TrackerProject HelixProject() => new()
    {
        Name = "CMH04",
        CaFolder = @"C:\CA",
        Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = "p1", ProjectName = "Aligned CMH04" }
    };

    [Theory]
    [InlineData("https://project.helixteam.com/Projects/abc123/Overview", "helix", "abc123", null)]
    [InlineData("https://project.helixteam.com/Projects/abc123/RFI#helix-activity-open=x", "helix", "abc123", null)]
    [InlineData("https://us02.procore.com/webclients/host/companies/12/projects/34/tools/rfis?page=2", "procore", "34", "12")]
    public void ParsesProjectLinks(string link, string provider, string projectId, string? accountId)
    {
        Assert.True(ProviderLinks.TryParse(link, out var connection, out _));
        Assert.Equal(provider, connection!.Provider);
        Assert.Equal(projectId, connection.ProjectId);
        Assert.Equal(accountId, connection.AccountId);
    }

    [Theory]
    [InlineData("https://app.procore.com/34/project/rfi/show/9")]
    [InlineData("https://project.helixteam.com/Home")]
    [InlineData("http://project.helixteam.com/Projects/abc/Overview")]
    [InlineData("https://example.com/Projects/abc/Overview")]
    [InlineData("not a link")]
    public void RejectsLinksWithoutAProject(string link)
    {
        Assert.False(ProviderLinks.TryParse(link, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void BuildsRefreshPagesForEachProvider()
    {
        var helix = ProviderLinks.RefreshSteps(HelixProject().Connection!);
        Assert.Single(helix);
        Assert.Equal("https://project.helixteam.com/Projects/p1/Overview", helix[0].Url);

        var procore = ProviderLinks.RefreshSteps(new ProviderConnection
        {
            Provider = Providers.Procore, ProjectId = "34", AccountId = "12", Host = "us02.procore.com"
        });
        Assert.Equal(new[]
        {
            "https://us02.procore.com/webclients/host/companies/12/projects/34/tools/rfis",
            "https://us02.procore.com/webclients/host/companies/12/projects/34/tools/submittals"
        }, procore.Select(step => step.Url));
    }

    [Fact]
    public void OnlyExistingRequestsCanBeClaimedAndCompleted()
    {
        using var temp = new TempFolder();
        var paths = new AppPaths(temp.Root);
        var store = new RefreshRequestStore(paths);
        var now = DateTimeOffset.Parse("2026-09-28T20:00:00Z");
        var request = store.Create(HelixProject(), new BrowserSettings(), now);

        Assert.Matches("^[a-f0-9]{32}$", request.Id);
        Assert.Equal("https://project.helixteam.com/Projects/p1/Overview", request.UrlForStep(0));
        Assert.Null(store.Claim(new string('a', 32), Providers.Helix, now).Request);
        Assert.Null(store.Claim(request.Id, Providers.Procore, now).Request);

        var claim = store.Claim(request.Id, Providers.Helix, now);
        Assert.Equal(RefreshStatus.Running, claim.Request!.Status);

        var snapshot = Build.Snapshot(Build.Item("1", "RFI-001"));
        var result = store.CompleteStep(request.Id, 0, snapshot, now.AddSeconds(20));
        Assert.True(result!.Done);
        Assert.Equal(RefreshStatus.Complete, store.Load(request.Id)!.Status);
        Assert.Null(store.Claim(request.Id, Providers.Helix, now).Request);
    }

    [Fact]
    public void RequestsExpireAndRejectTheWrongProject()
    {
        using var temp = new TempFolder();
        var store = new RefreshRequestStore(new AppPaths(temp.Root));
        var now = DateTimeOffset.Parse("2026-09-28T20:00:00Z");
        var expired = store.Create(HelixProject(), new BrowserSettings { TimeoutSeconds = 60 }, now);
        Assert.Null(store.Claim(expired.Id, Providers.Helix, now.AddMinutes(5)).Request);
        Assert.Equal(RefreshStatus.Failed, store.Load(expired.Id)!.Status);

        var request = store.Create(HelixProject(), new BrowserSettings(), now);
        store.Claim(request.Id, Providers.Helix, now);
        var other = Build.Snapshot();
        other.ProjectId = "someone-else";
        store.CompleteStep(request.Id, 0, other, now);
        Assert.Equal(RefreshStatus.Failed, store.Load(request.Id)!.Status);
    }

    [Fact]
    public void ProcoreRefreshesContinueToTheNextPage()
    {
        using var temp = new TempFolder();
        var paths = new AppPaths(temp.Root);
        var now = DateTimeOffset.Parse("2026-09-28T20:00:00Z");
        var project = new TrackerProject
        {
            Name = "Tower",
            Connection = new ProviderConnection { Provider = Providers.Procore, ProjectId = "34", AccountId = "12", ProjectName = "Tower" }
        };
        var request = new RefreshRequestStore(paths).Create(project, new BrowserSettings(), now);
        var store = new RefreshRequestStore(paths);
        Assert.NotNull(store.Claim(request.Id, Providers.Procore, now).Request);

        var rfis = new ProviderSnapshot { Provider = Providers.Procore, ProjectId = "34", ProjectName = "Tower", ItemTypes = new List<string> { ItemTypes.Rfi } };
        var first = store.CompleteStep(request.Id, 0, rfis, now.AddSeconds(5))!;
        Assert.False(first.Done);
        Assert.Equal("https://app.procore.com/webclients/host/companies/12/projects/34/tools/submittals", first.NextUrl);
        Assert.Equal(1, first.Request.CompletedSteps);

        var submittals = new ProviderSnapshot { Provider = Providers.Procore, ProjectId = "34", ProjectName = "Tower", ItemTypes = new List<string> { ItemTypes.Submittal } };
        var second = store.CompleteStep(request.Id, 1, submittals, now.AddSeconds(10))!;
        Assert.True(second.Done);
        Assert.Equal(RefreshStatus.Complete, store.Load(request.Id)!.Status);

        var catalog = new ProjectCatalogStore(paths);
        catalog.Merge(new CatalogMessage
        {
            Provider = Providers.Procore,
            Projects = new List<CatalogProject> { new() { ProjectId = "34", AccountId = "12", Name = "Tower", Host = "app.procore.com" } }
        }, now);
        Assert.Contains(catalog.Load(), project => project.ProjectId == "34" && project.AccountId == "12");
    }

    [Theory]
    [InlineData(@"Z:\Shared\Projects\CMH04\CA", "CMH04")]
    [InlineData(@"Z:\Shared\Projects\CMH04\CA\", "CMH04")]
    [InlineData(@"Z:\Shared\Projects\CMH04 Construction Admin", "CMH04 Construction Admin")]
    public void SuggestsProjectNamesFromCaFolders(string folder, string expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, TrackerProject.SuggestName(folder));
    }
}

public class SettingsMigrationTests
{
    [Fact]
    public void Version1MappingsBecomeConnectedProjects()
    {
        using var temp = new TempFolder();
        var paths = new AppPaths(temp.Root);
        File.WriteAllText(paths.SettingsFile, """
        {"version":1,"theme":"Dark","projects":[
          {"provider":"helix","projectId":"p1","projectName":"Aligned CMH04","caFolder":"Z:/CMH04/CA","companyId":null},
          {"provider":"helix","projectId":"p2","projectName":"No folder yet","caFolder":null}
        ]}
        """);
        var settings = new SettingsStore(paths).Load();
        Assert.Equal(2, settings.Version);
        Assert.Equal(ThemePreference.Dark, settings.Theme);
        var project = Assert.Single(settings.Projects);
        Assert.Equal("Aligned CMH04", project.Name);
        Assert.Equal("Z:/CMH04/CA", project.CaFolder);
        Assert.Equal("p1", project.Connection!.ProjectId);
        Assert.Equal(2, new SettingsStore(paths).Load().Version);
    }
}

public class FolderSettingTests
{
    [Theory]
    [InlineData("RFIs", true)]
    [InlineData("~Returned", true)]
    [InlineData(@"03 CA\RFIs", true)]
    [InlineData(@"Z:\Shared\Projects\CMH04\Submittals", true)]
    [InlineData(@"\\server\share\CA\RFIs", true)]
    [InlineData(@"Z:", false)]
    [InlineData(@"Z:RFIs", false)]
    [InlineData(@"..\Other", false)]
    [InlineData(@"RFIs\CON", false)]
    [InlineData(@"RFI?s", false)]
    [InlineData(" RFIs", false)]
    public void FolderSettingsAcceptNamesRelativePathsAndFullPaths(string value, bool expected) =>
        Assert.Equal(expected, FolderNames.IsValidSetting(value));

    [Fact]
    public void FoldersOutsideTheCaFolderAndASharedReturnedFolderAreScanned()
    {
        using var temp = new TempFolder();
        var ca = temp.Make("CMH04", "CA");
        var rfis = temp.Make("CMH04", "CA", "03 CA", "RFIs");
        temp.Make("CMH04", "CA", "03 CA", "RFIs", "RFI-001 - Open");
        var submittals = temp.Make("Elsewhere", "Submittals");
        temp.Make("Elsewhere", "Submittals", "S-1 - Doors");
        var returned = temp.Make("Archive", "Returned");
        temp.Make("Archive", "Returned", "RFI-002 - Done");
        temp.Make("Archive", "Returned", "S-2 - Done");

        var settings = new TrackerSettings();
        var project = new TrackerProject
        {
            Name = "CMH04",
            CaFolder = ca,
            Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = "p1" },
            FoldersOverride = new FolderLayout
            {
                RfiFolder = Path.Combine("03 CA", "RFIs"),
                SubmittalFolder = submittals,
                ReturnedFolder = returned
            }
        };
        settings.Projects.Add(project);
        var snapshot = Build.Snapshot(
            Build.Item("1", "RFI-001", holders: new[] { Build.Holder("smc", "current") }),
            Build.Item("2", "RFI-002", closed: true),
            Build.Item("3", "S-1", ItemTypes.Submittal, holders: new[] { Build.Holder("smc", "current") }),
            Build.Item("4", "S-2", ItemTypes.Submittal, closed: true));

        var comparison = ProjectComparer.Compare(snapshot, project, settings);
        var rfiRows = comparison.For(ItemTypes.Rfi).Rows;
        var submittalRows = comparison.For(ItemTypes.Submittal).Rows;

        Assert.Equal(rfis, comparison.For(ItemTypes.Rfi).Scan.TypeFolderPath);
        Assert.Equal(new[] { RowStatus.Open, RowStatus.Filed }, rfiRows.Select(row => row.Status).OrderBy(status => status));
        Assert.Equal(new[] { RowStatus.Open, RowStatus.Filed }, submittalRows.Select(row => row.Status).OrderBy(status => status));
        Assert.DoesNotContain(rfiRows, row => row.Status == RowStatus.NotInProvider);
        Assert.Equal("Returned", FolderLayout.DisplayName(returned));
    }
}

public class DisciplineTests
{
    [Theory]
    [InlineData("P000210 - Elec - Lighting fixtures", "Electrical")]
    [InlineData("RFI-012 - MECH duct clash", "Mechanical")]
    [InlineData("RFI-013 Plbg riser", "Plbg")]
    [InlineData("S-4 - Fire alarm devices", "Fire")]
    [InlineData("S-5 - ICT pathways", "ICT")]
    [InlineData("S-6 - Security_card readers", "Security")]
    [InlineData("S-7 - Electrical gear", "Electrical")]
    [InlineData("S-8 - Plumbing fixtures", "Plbg")]
    [InlineData("S-9 - Elec + Mech coordination", "Electrical")]
    [InlineData("S-10 - Firestopping", null)]
    [InlineData("S-11 - Electrician access", null)]
    [InlineData("RFI-014 - Door hardware", null)]
    public void DetectsDisciplinesFromWholeWordsInFolderNames(string name, string? expected) =>
        Assert.Equal(expected, Disciplines.Detect(name)?.Code);

    [Fact]
    public void ManualChoicesWinAndSurviveMovesIntoReturned()
    {
        var overrides = new Dictionary<string, string>
        {
            [Disciplines.Key(ItemTypes.Rfi, "RFI-001 - Elec conduit")] = "Mechanical",
            [Disciplines.Key(ItemTypes.Rfi, "RFI-002 - Elec panel")] = Disciplines.NoneCode
        };
        Assert.Equal(("Mechanical", DisciplineSource.Manual),
            Summary(Disciplines.Resolve(ItemTypes.Rfi, "RFI-001 - ELEC CONDUIT", null, overrides)));
        Assert.Equal(((string?)null, DisciplineSource.Manual),
            Summary(Disciplines.Resolve(ItemTypes.Rfi, "RFI-002 - Elec panel", null, overrides)));
        Assert.Equal(("Electrical", DisciplineSource.Detected),
            Summary(Disciplines.Resolve(ItemTypes.Submittal, "RFI-001 - Elec conduit", null, overrides)));
        Assert.Equal(("Fire", DisciplineSource.Detected),
            Summary(Disciplines.Resolve(ItemTypes.Rfi, null, "Fire sprinkler heads", overrides)));
    }

    [Fact]
    public void AnEmptyChoiceMeansDetectAgain()
    {
        var overrides = new Dictionary<string, string>
        {
            [Disciplines.Key(ItemTypes.Rfi, "RFI-001 - Elec conduit")] = "",
            [Disciplines.ItemKey(ItemTypes.Rfi, "abc123")] = "Mechanical"
        };
        // The folder's choice was cleared: the item's preset applies, then detection.
        Assert.Equal(("Mechanical", DisciplineSource.Manual),
            Summary(Disciplines.Resolve(ItemTypes.Rfi, "RFI-001 - Elec conduit", null, overrides, null, "abc123")));
        Assert.Equal(("Electrical", DisciplineSource.Detected),
            Summary(Disciplines.Resolve(ItemTypes.Rfi, "RFI-001 - Elec conduit", null, overrides)));
    }

    [Fact]
    public void AnItemsPresetDisciplineAppliesBeforeAndAfterItHasAFolder()
    {
        var overrides = new Dictionary<string, string>
        {
            [Disciplines.ItemKey(ItemTypes.Submittal, "ABC123")] = "Mechanical"
        };
        // No folder yet: the preset beats the title.
        Assert.Equal(("Mechanical", DisciplineSource.Manual),
            Summary(Disciplines.Resolve(ItemTypes.Submittal, null, "Elec panel", overrides, null, "abc123")));
        // Once downloaded into a folder, it still applies (a choice for the folder itself would win).
        Assert.Equal(("Mechanical", DisciplineSource.Manual),
            Summary(Disciplines.Resolve(ItemTypes.Submittal, "P000500 - Elec panel", null, overrides, null, "ABC123")));
        overrides[Disciplines.Key(ItemTypes.Submittal, "P000500 - Elec panel")] = "Fire";
        Assert.Equal(("Fire", DisciplineSource.Manual),
            Summary(Disciplines.Resolve(ItemTypes.Submittal, "P000500 - Elec panel", null, overrides, null, "ABC123")));
        // Other items and types are untouched.
        Assert.Equal(("Electrical", DisciplineSource.Detected),
            Summary(Disciplines.Resolve(ItemTypes.Rfi, null, "Elec panel", overrides, null, "ABC123")));
    }

    [Fact]
    public void DownloadsGoIntoANewDisciplineFolderOnlyWhenTheTypeFolderIsSplitByDiscipline()
    {
        using var temp = new TempFolder();
        temp.Make("CA", "Submittals", "Elec");
        temp.Make("CA", "RFIs", "RFI-001 - Something");
        var submittals = Path.Combine(temp.Root, "CA", "Submittals");
        var rfis = Path.Combine(temp.Root, "CA", "RFIs");
        Assert.Equal(Path.Combine(submittals, "Mech"),
            FolderScanner.SuggestParent(submittals, Disciplines.Find("Mechanical"), null));
        Assert.Equal(Path.Combine(submittals, "Elec"),
            FolderScanner.SuggestParent(submittals, Disciplines.Find("Electrical"), null));
        Assert.Equal(rfis, FolderScanner.SuggestParent(rfis, Disciplines.Find("Mechanical"), null));
    }

    [Fact]
    public void ComparisonRowsCarryTheirDiscipline()
    {
        using var temp = new TempFolder();
        var ca = temp.Make("CA");
        temp.Make("CA", "RFIs", "RFI-001 - Elec conduit");
        temp.Make("CA", "RFIs", "~Returned", "RFI-002 - Door hardware");
        var settings = new TrackerSettings();
        var project = new TrackerProject { Name = "P", CaFolder = ca };
        project.Disciplines[Disciplines.Key(ItemTypes.Rfi, "RFI-002 - Door hardware")] = "Security";
        var snapshot = Build.Snapshot(Build.Item("1", "RFI-001"), Build.Item("2", "RFI-002", closed: true));
        var rows = ProjectComparer.Compare(snapshot, project, settings).For(ItemTypes.Rfi).Rows;
        Assert.Equal("Electrical", rows.Single(row => row.Item?.ItemId == "1").Discipline?.Code);
        var manual = rows.Single(row => row.Item?.ItemId == "2");
        Assert.Equal("Security", manual.Discipline?.Code);
        Assert.Equal(DisciplineSource.Manual, manual.DisciplineSource);
    }

    private static (string? Code, DisciplineSource Source) Summary((Discipline? Discipline, DisciplineSource Source) value) =>
        (value.Discipline?.Code, value.Source);
}

public class PhaseTests
{
    [Theory]
    [InlineData("RFI-012 - BB - Slab edge", ProjectPhase.BaseBuild)]
    [InlineData("RFI-013 - Base Build lobby", ProjectPhase.BaseBuild)]
    [InlineData("S-4 TFO Level 3 lighting", ProjectPhase.TenantFitOut)]
    [InlineData("S-5 - Tenant Fit-Out Level 3", ProjectPhase.TenantFitOut)]
    [InlineData("S-6 - Fitout doors", ProjectPhase.TenantFitOut)]
    [InlineData(@"Z:\Projects\CMH04 TFO\CA", ProjectPhase.TenantFitOut)]
    [InlineData("S-7 - BB and TFO interface", ProjectPhase.BaseBuild)]
    [InlineData("S-8 - BBQ area", null)]
    [InlineData("RFI-014 - Door hardware", null)]
    public void DetectsTheProjectPart(string text, ProjectPhase? expected) =>
        Assert.Equal(expected, Phases.Detect(text));

    [Fact]
    public void FoldersHiddenByThePartFilterAreCounted()
    {
        using var temp = new TempFolder();
        temp.Make("CA", "RFIs", "RFI-001 - BB - Slab edge");
        temp.Make("CA", "RFIs", "RFI-002 - Lobby doors");
        temp.Make("CA", "RFIs", "RFI-003 - Door hardware");
        temp.Make("CA", "RFIs", "RFI-004 - TFO - Level 3 lighting");
        var project = new TrackerProject
        {
            Name = "CMH02 BB",
            CaFolder = Path.Combine(temp.Root, "CA"),
            Scope = new PhaseScope { Phase = ProjectPhase.BaseBuild, FilterFolders = true }
        };

        var filtered = ProjectComparer.Compare(null, project, new TrackerSettings()).For(ItemTypes.Rfi);
        Assert.Equal(2, filtered.HiddenByPhase);
        Assert.Single(filtered.Scan.Folders);

        project.Scope.IncludeUnlabeled = true;
        Assert.Equal(0, ProjectComparer.Compare(null, project, new TrackerSettings()).For(ItemTypes.Rfi).HiddenByPhase);
    }

    [Fact]
    public void SplitProjectsKeepOnlyTheirPartOfSharedItemsAndFolders()
    {
        using var temp = new TempFolder();
        var ca = temp.Make("CA");
        temp.Make("CA", "RFIs", "RFI-001 - BB - Slab");
        temp.Make("CA", "RFIs", "RFI-002 - TFO - Ceiling");
        temp.Make("CA", "RFIs", "RFI-003 - Door hardware");
        var snapshot = Build.Snapshot(
            Build.Item("1", "RFI-001"),
            Build.Item("2", "RFI-002"),
            Build.Item("3", "RFI-003"),
            Build.Item("4", "RFI-004"));
        snapshot.Items[0].Contract = "Base Build";
        snapshot.Items[1].Title = "TFO ceiling heights";
        snapshot.Items[3].Title = "TFO lighting";
        var settings = new TrackerSettings();
        var fitOut = new TrackerProject
        {
            Name = "CMH04 TFO",
            CaFolder = ca,
            Scope = new PhaseScope { Phase = ProjectPhase.TenantFitOut, FilterItems = true, FilterFolders = true }
        };

        var rows = ProjectComparer.Compare(snapshot, fitOut, settings).For(ItemTypes.Rfi).Rows;
        Assert.Equal(new[] { "RFI-002", "RFI-004" }, rows.Select(row => row.Item!.Number).OrderBy(number => number));
        Assert.Equal("RFI-002 - TFO - Ceiling", rows.Single(row => row.Folder is not null).Folder!.Name);

        fitOut.Scope.IncludeUnlabeled = true;
        var withUnlabeled = ProjectComparer.Compare(snapshot, fitOut, settings).For(ItemTypes.Rfi).Rows;
        Assert.Contains(withUnlabeled, row => row.Item?.Number == "RFI-003" && row.Folder is not null);

        var whole = new TrackerProject { Name = "CMH04", CaFolder = ca };
        Assert.Equal(4, ProjectComparer.Compare(snapshot, whole, settings).For(ItemTypes.Rfi).Rows.Count);
    }
}

public class BuiltInBrowserTests
{
    [Fact]
    public void KeepAliveExtendsAnOpenRefreshButNotAFinishedOne()
    {
        using var temp = new TempFolder();
        var store = new RefreshRequestStore(new AppPaths(temp.Root));
        var now = DateTimeOffset.Parse("2026-09-28T20:00:00Z");
        var project = new TrackerProject
        {
            Name = "P",
            Connection = new ProviderConnection { Provider = Providers.Helix, ProjectId = "p1" }
        };
        var request = store.Create(project, new BrowserSettings { TimeoutSeconds = 60 }, now);

        store.KeepAlive(request.Id, now.AddSeconds(50));
        Assert.Equal(now.AddSeconds(110), store.Load(request.Id)!.ExpiresAt);
        Assert.NotNull(store.Claim(request.Id, Providers.Helix, now.AddSeconds(100)).Request);

        store.Cancel(request.Id, now.AddSeconds(101));
        store.KeepAlive(request.Id, now.AddSeconds(102));
        Assert.Equal(RefreshStatus.Cancelled, store.Load(request.Id)!.Status);
    }

    [Fact]
    public void AccountsRememberWhoSignedInUntilSignOut()
    {
        using var temp = new TempFolder();
        var store = new AccountStore(new AppPaths(temp.Root));
        var now = DateTimeOffset.Parse("2026-09-28T20:00:00Z");
        Assert.Null(store.Get(Providers.Helix));

        store.Record("Helix", "Pat Reviewer", "SMC", now);
        store.Record(Providers.Procore, null, null, now);
        store.Record(Providers.Helix, "", null, now.AddHours(1));
        store.Record("egnyte", "x", null, now);

        var helix = store.Get(Providers.Helix)!;
        Assert.Equal("helix", helix.Provider);
        Assert.Equal("Pat Reviewer", helix.UserName);
        Assert.Equal("SMC", helix.Company);
        Assert.Equal(now.AddHours(1), helix.SignedInAt);
        Assert.Null(store.Get(Providers.Procore)!.UserName);
        Assert.Equal(2, store.Load().Count);

        store.Clear();
        Assert.Empty(store.Load());
    }

    [Fact]
    public void SettingsFromChromeVersionsStillLoad()
    {
        using var temp = new TempFolder();
        var paths = new AppPaths(temp.Root);
        File.WriteAllText(paths.SettingsFile, """
        {"version":2,"theme":"Dark","browser":{"chromePath":"","profileDirectory":"","useBuiltInBrowser":false,
         "closeTabWhenDone":false,"timeoutSeconds":120},
         "projects":[{"id":"7d86","name":"CMH02","caFolder":"E:\\CA","connection":{"provider":"helix","projectId":"66bb"}}],
         "allowedExtensionIds":["kcngamkbiboiljpfbjpmjoekkfpbidbk"]}
        """);
        var settings = new SettingsStore(paths).Load();
        Assert.Equal(ThemePreference.Dark, settings.Theme);
        Assert.Equal(120, settings.Browser.TimeoutSeconds);
        Assert.True(settings.Browser.CloseWindowWhenDone);
        Assert.Equal("66bb", Assert.Single(settings.Projects).Connection!.ProjectId);
    }
}

public class ItemTimingTests
{
    private static readonly DateTime Today = new(2026, 9, 29);

    private static SnapshotItem Item(DateTimeOffset? due, bool closed = false, params SnapshotHolder[] holders) => new()
    {
        ItemType = ItemTypes.Rfi,
        ItemId = "1",
        Number = "RFI-001",
        Closed = closed,
        DueAt = due,
        Holders = holders.ToList()
    };

    private static DateTimeOffset Local(int month, int day) => new(new DateTime(2026, month, day, 12, 0, 0, DateTimeKind.Local));

    [Fact]
    public void DueFiltersUseLocalCalendarDays()
    {
        var overdue = Item(Local(9, 27));
        var today = Item(Local(9, 29));
        var soon = Item(Local(10, 3));
        var later = Item(Local(10, 20));
        var closed = Item(Local(9, 1), closed: true);
        var none = Item(null);

        Assert.Equal(-2, ItemTiming.DaysTillDue(overdue, Today));
        Assert.Equal(0, ItemTiming.DaysTillDue(today, Today));
        Assert.Null(ItemTiming.DaysTillDue(closed, Today));
        Assert.True(ItemTiming.MatchesDue(overdue, DueFilter.Overdue, 7, Today));
        Assert.False(ItemTiming.MatchesDue(closed, DueFilter.Overdue, 7, Today));
        Assert.True(ItemTiming.MatchesDue(today, DueFilter.DueToday, 7, Today));
        Assert.True(ItemTiming.MatchesDue(soon, DueFilter.DueWithin, 7, Today));
        Assert.True(ItemTiming.MatchesDue(today, DueFilter.DueWithin, 7, Today));
        Assert.False(ItemTiming.MatchesDue(later, DueFilter.DueWithin, 7, Today));
        Assert.False(ItemTiming.MatchesDue(overdue, DueFilter.DueWithin, 7, Today));
        Assert.False(ItemTiming.MatchesDue(none, DueFilter.DueToday, 7, Today));
        Assert.True(ItemTiming.MatchesDue(none, DueFilter.Any, 7, Today));
    }

    [Fact]
    public void DaysInCourtUsesTheEarliestCurrentHolder()
    {
        var item = Item(null, false,
            new SnapshotHolder { Name = "Pat", State = "current", Since = Local(9, 20) },
            new SnapshotHolder { Name = "Sam", State = "current", Since = Local(9, 25) },
            new SnapshotHolder { Name = "Alex", State = "upcoming" },
            new SnapshotHolder { Name = "pat", State = "current" });

        Assert.Equal(9, ItemTiming.DaysInCourt(item, Today));
        Assert.Equal(new[] { "Pat", "Sam" }, ItemTiming.CurrentHolders(item).Select(holder => holder.Name));
        Assert.Null(ItemTiming.DaysInCourt(Item(null, false, new SnapshotHolder { Name = "Pat", State = "current" }), Today));
        Assert.Empty(ItemTiming.CurrentHolders(Item(null, closed: true, new SnapshotHolder { Name = "Pat", State = "current" })));
    }
}

public class DownloadNamingTests
{
    [Theory]
    [InlineData("P000210", "Aligned CMH02 - Overhead Coiling Doors - Overhead Coiling - SD", "Aligned CMH02", "P000210 - Overhead Coiling Doors - Overhead Coiling - SD")]
    [InlineData("P000210", "CMH02 - Overhead Coiling Doors", "Aligned CMH02", "P000210 - Overhead Coiling Doors")]
    [InlineData("RFI-012", "Slab edge -- - pour break", "Tower", "RFI-012 - Slab edge - pour break")]
    [InlineData("RFI-013", "What is A/B: 3\"?", null, "RFI-013 - What is A-B- 3")]
    [InlineData("RFI-014", "", "Tower", "RFI-014")]
    public void FolderNamesAreNumberThenName(string number, string title, string? project, string expected) =>
        Assert.Equal(expected, DownloadNaming.FolderName(number, title, project));

    [Fact]
    public void PdfAttachmentsAreDatedAndNamesNeverCollide()
    {
        var uploaded = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("2026-08-03 Review markup.pdf", DownloadNaming.AttachmentFileName("Review markup.pdf", uploaded));
        Assert.Equal("2026-08-03 Review.pdf", DownloadNaming.AttachmentFileName("2026-08-03 Review.pdf", uploaded));
        Assert.Equal("photo.jpg", DownloadNaming.AttachmentFileName("photo.jpg", uploaded));
        Assert.Equal("_con.txt", DownloadNaming.SafeFileName(@"..\con.txt", "x"));
        Assert.Equal("a-b.pdf", DownloadNaming.SafeFileName("a:b.pdf", "x"));

        using var temp = new TempFolder();
        File.WriteAllText(Path.Combine(temp.Root, "a.pdf"), "");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a (2).pdf", DownloadNaming.UniqueFileName(temp.Root, "a.pdf", used));
        Assert.Equal("a (3).pdf", DownloadNaming.UniqueFileName(temp.Root, "a.pdf", used));
        Assert.Equal("b", DownloadNaming.UniqueFileName(temp.Root, "b", used));
    }

    [Fact]
    public void FilesAlreadyDownloadedAreRecognisedWhateverTheyWereSavedAs()
    {
        var key = DownloadNaming.MatchKey("Review Markup.pdf");
        Assert.Equal(key, DownloadNaming.MatchKey("2026-08-03 Review Markup.pdf"));
        Assert.Equal(key, DownloadNaming.MatchKey("review markup (2).PDF"));
        Assert.Equal(key, DownloadNaming.MatchKey(@"Z:\CA\RFIs\RFI-001\2026-08-03_Review  Markup.pdf"));
        Assert.NotEqual(key, DownloadNaming.MatchKey("Review Markup Rev 1.pdf"));
    }
}

public class NestedFolderTests
{
    [Theory]
    [InlineData("Elec", true)]
    [InlineData("BB", true)]
    [InlineData("TFO", true)]
    [InlineData("Base Build", true)]
    [InlineData("Mechanical", true)]
    [InlineData("P000210 - Elec - Lighting", false)]
    [InlineData("~Returned", false)]
    [InlineData("Old stuff", false)]
    public void RecognisesDisciplineAndPartFolders(string name, bool expected) =>
        Assert.Equal(expected, FolderScanner.IsGroupFolder(name));

    [Fact]
    public void ItemFoldersInsideDisciplineAndPartFoldersAreFound()
    {
        using var temp = new TempFolder();
        var ca = temp.Make("CA");
        var elec = temp.Make("CA", "Submittals", "Elec");
        temp.Make("CA", "Submittals", "Elec", "BB", "P000210 - Doors");
        temp.Make("CA", "Submittals", "Elec", "TFO", "P000300 - Lights");
        temp.Make("CA", "Submittals", "Elec", "~Returned", "P000100 - Old");
        temp.Make("CA", "Submittals", "Mech", "P000400 - Duct");
        File.WriteAllText(Path.Combine(elec, "DO NOT SAVE TO THIS FOLDER.txt"), "");

        var scan = FolderScanner.Scan(ca, ItemTypes.Submittal, new FolderLayout());
        Assert.Equal(4, scan.Folders.Count);
        var doors = scan.Folders.Single(folder => folder.Name.StartsWith("P000210"));
        Assert.Equal(new[] { "Elec", "BB" }, doors.Groups);
        Assert.Equal(FolderLocation.Active, doors.Location);
        Assert.Equal(Path.Combine("Elec", "BB"), doors.RelativeParent);
        var old = scan.Folders.Single(folder => folder.Name.StartsWith("P000100"));
        Assert.Equal(FolderLocation.Returned, old.Location);
        Assert.Equal(new[] { "Elec" }, old.Groups);

        // Returned goes to the nearest ~Returned, keeping BB (created there by the plan if missing).
        Assert.Equal(Path.Combine(elec, "~Returned", "BB"), OperationPlanner.ReturnedDestination(doors, scan, new FolderLayout()));
        var comparison = ProjectComparer.Compare(null, new TrackerProject { Name = "P", CaFolder = ca }, new TrackerSettings());
        var doorsRow = comparison.For(ItemTypes.Submittal).Rows.Single(row => row.Folder?.Name.StartsWith("P000210") == true);
        var plan = OperationPlanner.PlanMarkReturned(new[] { doorsRow }, comparison);
        Assert.Equal(OperationKind.CreateFolder, plan.Operations[0].Kind);
        Assert.Equal(Path.Combine(elec, "~Returned", "BB"), plan.Operations[0].DestinationPath);
        Assert.False(plan.Operations[0].IsBlocked);
        Assert.Equal(Path.Combine(elec, "~Returned", "BB", "P000210 - Doors"), plan.Operations[1].DestinationPath);
        Assert.Equal(elec, OperationPlanner.ActiveDestination(old, scan));

        // New download folders go into Elec\BB for an electrical Base Build item.
        Assert.Equal(Path.Combine(elec, "BB"),
            FolderScanner.SuggestParent(scan.TypeFolderPath, Disciplines.Find("Electrical"), ProjectPhase.BaseBuild));
        Assert.Equal(scan.TypeFolderPath, FolderScanner.SuggestParent(scan.TypeFolderPath, null, null));
    }

    [Fact]
    public void MarkingReturnedCreatesTheGroupFoldersMissingInsideReturned()
    {
        using var temp = new TempFolder();
        var ca = temp.Make("CA");
        var submittals = temp.Make("CA", "Submittals");
        var doors = temp.Make("CA", "Submittals", "Elec", "BB", "P000210 - Doors");
        temp.Make("CA", "Submittals", "~Returned");

        // Only Submittals\~Returned exists: ~Returned\Elec\BB is created in one step, then the folder moves.
        var comparison = ProjectComparer.Compare(null, new TrackerProject { Name = "P", CaFolder = ca }, new TrackerSettings());
        var row = comparison.For(ItemTypes.Submittal).Rows.Single(candidate => candidate.Folder?.Name.StartsWith("P000210") == true);
        var plan = OperationPlanner.PlanMarkReturned(new[] { row }, comparison);
        var target = Path.Combine(submittals, "~Returned", "Elec", "BB");
        Assert.Equal(2, plan.Operations.Count);
        Assert.Equal(OperationKind.CreateFolder, plan.Operations[0].Kind);
        Assert.Equal(target, plan.Operations[0].DestinationPath);
        Assert.False(plan.Operations[0].IsBlocked);
        Assert.Equal(Path.Combine(target, "P000210 - Doors"), plan.Operations[1].DestinationPath);
        Assert.False(plan.Operations[1].IsBlocked);

        var results = new OperationExecutor(new AppPaths(temp.Make("appdata"))).Execute(plan);
        Assert.All(results, result => Assert.Equal(OperationOutcome.Done, result.Outcome));
        Assert.True(Directory.Exists(Path.Combine(target, "P000210 - Doors")));
        Assert.False(Directory.Exists(doors));
    }

    [Fact]
    public void ABlockedMoveDoesNotCreateAnEmptyReturnedFolder()
    {
        using var temp = new TempFolder();
        var ca = temp.Make("CA");
        var doors = temp.Make("CA", "Submittals", "Elec", "BB", "P000210 - Doors");
        temp.Make("CA", "Submittals", "~Returned");
        var comparison = ProjectComparer.Compare(null, new TrackerProject { Name = "P", CaFolder = ca }, new TrackerSettings());
        var row = comparison.For(ItemTypes.Submittal).Rows.Single(candidate => candidate.Folder?.Name.StartsWith("P000210") == true);
        Directory.Delete(doors);

        var operation = Assert.Single(OperationPlanner.PlanMarkReturned(new[] { row }, comparison).Operations);
        Assert.Equal(OperationKind.MoveFolder, operation.Kind);
        Assert.True(operation.IsBlocked);
    }

    [Fact]
    public void ATenantFitOutFolderIsNotFiledInTheBaseBuildReturnedFolder()
    {
        using var temp = new TempFolder();
        var ca = temp.Make("CA");
        var submittals = temp.Make("CA", "Submittals");
        temp.Make("CA", "Submittals", "TFO", "P000300 - Lights");
        temp.Make("CA", "Submittals", "~Returned (Base Build)");

        var comparison = ProjectComparer.Compare(null, new TrackerProject { Name = "P", CaFolder = ca }, new TrackerSettings());
        var row = comparison.For(ItemTypes.Submittal).Rows.Single(candidate => candidate.Folder?.Name.StartsWith("P000300") == true);
        var plan = OperationPlanner.PlanMarkReturned(new[] { row }, comparison);
        Assert.Equal(OperationKind.CreateFolder, plan.Operations[0].Kind);
        Assert.Equal(Path.Combine(submittals, "~Returned"), plan.Operations[0].DestinationPath);
        Assert.False(plan.Operations[0].IsBlocked);
        Assert.Equal(Path.Combine(submittals, "~Returned", "P000300 - Lights"), plan.Operations[1].DestinationPath);
    }

    [Fact]
    public void PartAndDisciplineComeFromTheFoldersAnItemSitsIn()
    {
        using var temp = new TempFolder();
        temp.Make("CA", "Submittals", "Elec", "BB", "P000210 - Doors");
        temp.Make("CA", "Submittals", "Elec", "TFO", "P000300 - Lights");
        temp.Make("CA", "Submittals", "Mech", "P000400 - Duct");
        var project = new TrackerProject
        {
            Name = "CMH02 BB",
            CaFolder = Path.Combine(temp.Root, "CA"),
            Scope = new PhaseScope { Phase = ProjectPhase.BaseBuild, FilterFolders = true }
        };

        var submittals = ProjectComparer.Compare(null, project, new TrackerSettings()).For(ItemTypes.Submittal);
        var row = Assert.Single(submittals.Rows);
        Assert.StartsWith("P000210", row.Folder!.Name);
        Assert.Equal("Electrical", row.Discipline?.Code);
        Assert.Equal(1, submittals.HiddenByPhase);
    }
}

public class RevisionTests
{
    [Theory]
    [InlineData("House Generator Fire Pump Breaker Clarification - 00001 - R0", 0)]
    [InlineData("Overhead Coiling Doors - Rev 2", 2)]
    [InlineData("Overhead Coiling Doors Rev. 3", 3)]
    [InlineData("Doors REV#4", 4)]
    [InlineData("Doors - Revision 5", 5)]
    [InlineData("Doors R1 then R2", 2)]
    [InlineData("Overhead Coiling Doors", null)]
    [InlineData("RFI-012 Slab edge", null)]
    [InlineData("R2D2 lighting", null)]
    [InlineData("Revit model coordination", null)]
    public void FindsTheRevisionInATitle(string title, int? expected) =>
        Assert.Equal(expected, DownloadNaming.Revision(title));

    [Fact]
    public void RevisionFilesGoIntoAnRSubfolderReusingAnExistingOne()
    {
        using var temp = new TempFolder();
        var item = temp.Make("P000210 - Doors");
        Assert.Equal(Path.Combine(item, "R1"), DownloadNaming.RevisionFolder(item, 1));
        temp.Make("P000210 - Doors", "Rev 2");
        temp.Make("P000210 - Doors", "R03");
        Assert.Equal(Path.Combine(item, "Rev 2"), DownloadNaming.RevisionFolder(item, 2));
        Assert.Equal(Path.Combine(item, "R03"), DownloadNaming.RevisionFolder(item, 3));
    }

    [Fact]
    public void ARevisionGoesInASubfolderOnlyWhenTheItemAlreadyHasAFolder()
    {
        using var temp = new TempFolder();
        var item = Path.Combine(temp.Root, "P000210 - Doors");
        Assert.Equal((item, false), DownloadFolders.Destination(item, 2));
        temp.Make("P000210 - Doors");
        Assert.Equal((Path.Combine(item, "R2"), true), DownloadFolders.Destination(item, 2));
        Assert.Equal((item, false), DownloadFolders.Destination(item, null));
    }

    [Fact]
    public void ARevisionsFilesAreComparedOnlyWithItsRevisionFolder()
    {
        using var temp = new TempFolder();
        var item = temp.Make("P000210 - Doors");
        File.WriteAllText(Path.Combine(item, "Door schedule.pdf"), "rev 0");
        File.WriteAllText(Path.Combine(temp.Make("P000210 - Doors", "R1"), "Hardware.pdf"), "rev 1");
        var revision = temp.Make("P000210 - Doors", "R2");
        var schedule = DownloadNaming.MatchKey("Door schedule.pdf");
        var hardware = DownloadNaming.MatchKey("Hardware.pdf");

        // The item itself: anything in its folder counts, revision subfolders included.
        var itemKeys = DownloadFolders.ExistingFileKeys(item, item, inRevisionFolder: false);
        Assert.Contains(schedule, itemKeys);
        Assert.Contains(hardware, itemKeys);

        // Revision 2: earlier revisions' files of the same name don't, so they are checked for download.
        var revisionKeys = DownloadFolders.ExistingFileKeys(item, revision, inRevisionFolder: true);
        Assert.DoesNotContain(schedule, revisionKeys);
        Assert.DoesNotContain(hardware, revisionKeys);
        File.WriteAllText(Path.Combine(revision, "2026-10-01 Door schedule.pdf"), "rev 2");
        Assert.Contains(schedule, DownloadFolders.ExistingFileKeys(item, revision, inRevisionFolder: true));
    }
}

public class ReturnedWithNoteTests
{
    [Theory]
    [InlineData("~Returned", true)]
    [InlineData("~Returned (Base Build)", true)]
    [InlineData("~Returned - TFO", true)]
    [InlineData("~ReturnedOld", false)]
    [InlineData("Returned", false)]
    public void RecognisesReturnedFoldersWithANote(string name, bool expected) =>
        Assert.Equal(expected, FolderScanner.IsReturnedName(name, "~Returned"));

    [Fact]
    public void ReturnedFoldersInsidePartFoldersAreReturned()
    {
        using var temp = new TempFolder();
        var ca = temp.Make("CA");
        var bb = temp.Make("CA", "Submittals", "Elec", "BB");
        temp.Make("CA", "Submittals", "Elec", "BB", "~Returned (Base Build)", "000641 - Lighting Controls Drawings");
        temp.Make("CA", "Submittals", "Elec", "BB", "000700 - Panels");

        var scan = FolderScanner.Scan(ca, ItemTypes.Submittal, new FolderLayout());
        var returned = scan.Folders.Single(folder => folder.Name.StartsWith("000641"));
        Assert.Equal(FolderLocation.Returned, returned.Location);
        Assert.Equal(ProjectPhase.BaseBuild, Phases.Detect(returned.DetectionText));
        Assert.Equal(bb, OperationPlanner.ActiveDestination(returned, scan));

        var active = scan.Folders.Single(folder => folder.Name.StartsWith("000700"));
        Assert.Equal(FolderLocation.Active, active.Location);
        Assert.Equal(Path.Combine(bb, "~Returned (Base Build)"), OperationPlanner.ReturnedDestination(active, scan, new FolderLayout()));
    }
}
