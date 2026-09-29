using System.Text.Json;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.NativeMessaging;
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
    public void NativeHostStoresExtensionSnapshots()
    {
        using var temp = new TempFolder();
        var store = new SnapshotStore(new AppPaths(temp.Root));
        var handler = new NativeHostHandler(new AppPaths(temp.Root));
        var json = """
        {"type":"snapshot","snapshot":{"schemaVersion":1,"provider":"helix","accountId":null,"projectId":"p1",
         "projectName":"Aligned CMH04","projectUrl":null,"itemTypes":["rfi"],"complete":true,
         "scannedAt":"2026-09-28T20:00:00.000Z","user":{"fullName":"Pat","companyId":"smc","company":"SMC"},
         "items":[{"itemType":"rfi","itemId":"i1","number":"RFI-012","title":"Slab","status":"Review","statusRank":800,
         "closed":false,"responsible":"","holders":[{"name":"Alex","companyId":"arch","company":"Arch","state":"current"}],
         "attachmentCount":2,"createdAt":null,"modifiedAt":"2026-09-01T00:00:00.000Z","dueAt":null,"sourceUrl":"https://example.test"}]}}
        """;
        var response = JsonDocument.Parse(handler.Handle(json)).RootElement;
        Assert.True(response.GetProperty("ok").GetBoolean());
        var stored = store.Load("helix", "p1");
        Assert.NotNull(stored);
        Assert.Equal("RFI-012", stored!.Items[0].Number);
        Assert.Equal("arch", stored.Items[0].Holders[0].CompanyId);

        var ping = JsonDocument.Parse(handler.Handle("""{"type":"ping"}""")).RootElement;
        Assert.True(ping.GetProperty("ok").GetBoolean());
        var bad = JsonDocument.Parse(handler.Handle("""{"type":"snapshot","snapshot":{"provider":"egnyte"}}""")).RootElement;
        Assert.False(bad.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ProtocolRoundTripsLengthPrefixedMessages()
    {
        using var stream = new MemoryStream();
        await NativeMessageProtocol.WriteAsync(stream, """{"type":"ping"}""", CancellationToken.None);
        stream.Position = 0;
        Assert.Equal("""{"type":"ping"}""", await NativeMessageProtocol.ReadAsync(stream, CancellationToken.None));
        Assert.Null(await NativeMessageProtocol.ReadAsync(stream, CancellationToken.None));
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
    public void OnlyAppCreatedRequestsCanBeClaimedAndCompleted()
    {
        using var temp = new TempFolder();
        var paths = new AppPaths(temp.Root);
        var store = new RefreshRequestStore(paths);
        var now = DateTimeOffset.Parse("2026-09-28T20:00:00Z");
        var request = store.Create(HelixProject(), new BrowserSettings(), now);

        Assert.Matches("^[a-f0-9]{32}$", request.Id);
        Assert.EndsWith($"/Overview#ca-tracker-refresh={request.Id}", request.UrlForStep(0));
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
        var handler = new NativeHostHandler(paths, () => now);

        var claim = JsonDocument.Parse(handler.Handle($$"""{"type":"claimRefresh","id":"{{request.Id}}","provider":"procore"}""")).RootElement;
        Assert.True(claim.GetProperty("ok").GetBoolean());
        Assert.Equal(0, claim.GetProperty("request").GetProperty("step").GetInt32());

        var snapshotJson = $$$"""
        {"type":"snapshot","refresh":{"id":"{{{request.Id}}}","step":0},
         "catalog":{"provider":"procore","projects":[{"projectId":"34","accountId":"12","name":"Tower","host":"app.procore.com"}]},
         "snapshot":{"schemaVersion":1,"provider":"procore","projectId":"34","projectName":"Tower","itemTypes":["rfi"],
         "complete":false,"scannedAt":"2026-09-28T20:00:05Z","user":{"fullName":""},"items":[]}}
        """;
        var first = JsonDocument.Parse(handler.Handle(snapshotJson)).RootElement.GetProperty("refresh");
        Assert.False(first.GetProperty("done").GetBoolean());
        Assert.EndsWith($"/tools/submittals#ca-tracker-refresh={request.Id}", first.GetProperty("nextUrl").GetString());

        var second = JsonDocument.Parse(handler.Handle(snapshotJson.Replace("\"step\":0", "\"step\":1"))).RootElement.GetProperty("refresh");
        Assert.True(second.GetProperty("done").GetBoolean());
        Assert.True(second.GetProperty("closeTab").GetBoolean());
        Assert.Contains(new ProjectCatalogStore(paths).Load(), project => project.ProjectId == "34" && project.AccountId == "12");
        Assert.NotNull(ExtensionContact.Load(paths));
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
