using System.Text.Json;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;

namespace SmcCaTracker.Core.Tests;

public class ProcoreItemsTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void RfisBecomeItemsWithWhoHasTheBall()
    {
        var rfi = Json("""
            {
              "id": 1234, "number": "12", "subject": "Conduit routing at grid B", "status": "open",
              "due_date": "2026-10-03", "created_at": "2026-09-01T15:00:00Z", "updated_at": "2026-09-20T15:00:00Z",
              "ball_in_court": [ { "id": 7, "name": "Jane Architect", "login": "jane@arch.com", "vendor": { "id": 55, "name": "Arch Co" } } ]
            }
            """);
        var item = ProcoreItems.ParseRfi(rfi, "999", "app.procore.com")!;
        Assert.Equal("1234", item.ItemId);
        Assert.Equal("12", item.Number);
        Assert.Equal("Conduit routing at grid B", item.Title);
        Assert.Equal("Open", item.Status);
        Assert.False(item.Closed);
        var holder = Assert.Single(item.Holders);
        Assert.Equal("Jane Architect", holder.Name);
        Assert.Equal("Arch Co", holder.Company);
        Assert.Equal("55", holder.CompanyId);
        Assert.Equal("current", holder.State);
        Assert.Equal(new DateTime(2026, 10, 3), item.DueAt!.Value.Date);
        Assert.Equal("https://app.procore.com/999/project/rfi/show/1234", item.SourceUrl);
    }

    [Fact]
    public void SubmittalsKeepTheirRevisionAndStatusObjects()
    {
        var submittal = Json("""
            {
              "id": 88, "number": "03 30 00-1", "revision": "2", "title": "Concrete mix",
              "status": { "id": 3, "name": "Closed", "status": "closed" },
              "ball_in_court": [], "responsible_contractor": { "id": 4, "name": "Concrete Inc" }
            }
            """);
        var item = ProcoreItems.ParseSubmittal(submittal, "999", "app.procore.com")!;
        Assert.Equal("03 30 00-1 Rev 2", item.Number);
        Assert.Equal("Closed", item.Status);
        Assert.True(item.Closed);
        Assert.Empty(item.Holders);
        Assert.Equal("Concrete Inc", item.Contract);
    }

    [Fact]
    public void BallInCourtAlsoComesAsOnePersonOrText()
    {
        Assert.Equal("Sam", Assert.Single(ProcoreItems.Holders(Json("""{ "ball_in_court": { "name": "Sam" } }"""))).Name);
        Assert.Equal(new[] { "A B", "C D" },
            ProcoreItems.Holders(Json("""{ "ball_in_court": "A B, C D" }""")).Select(holder => holder.Name));
        Assert.Empty(ProcoreItems.Holders(Json("""{ "ball_in_court": null }""")));
    }

    [Fact]
    public void AProjectsItemsMakeACompleteList()
    {
        var snapshot = ProcoreItems.BuildSnapshot("999", "12", "Job", "app.procore.com",
            new[] { ItemTypes.Rfi, ItemTypes.Submittal },
            new[] { Json("""{ "id": 1, "number": "1", "subject": "A" }"""), Json("""{ "id": 1, "number": "1", "subject": "A" }""") },
            new[] { Json("""{ "id": 2, "number": "S-1", "title": "B" }"""), Json("""{ "number": "no id" }""") },
            DateTimeOffset.Now);
        Assert.True(snapshot.Complete);
        Assert.Equal(Providers.Procore, snapshot.Provider);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.True(snapshot.CoverageFor(ItemTypes.Submittal)!.Complete);
    }
}

public class ProcoreSettingsTests
{
    [Fact]
    public void ProjectsCanBeLeftOutOfSyncingAndBroughtBack()
    {
        var settings = new ProcoreSettings();
        Assert.False(settings.IsSkipped("111"));

        Assert.True(settings.SetSkipped("111", true));
        Assert.False(settings.SetSkipped("111", true));
        Assert.True(settings.IsSkipped("111"));
        Assert.False(settings.IsSkipped("222"));
        Assert.False(settings.IsSkipped(null));

        Assert.True(settings.SetSkipped("111", false));
        Assert.False(settings.IsSkipped("111"));
        Assert.False(settings.SetSkipped("", true));
    }

    [Fact]
    public void ProjectsAreLookedForAgainEveryFewHours()
    {
        var now = DateTimeOffset.Now;
        var settings = new ProcoreSettings { DiscoverEveryHours = 6 };
        Assert.True(settings.DiscoveryDue(now));

        settings.LastDiscoveredAt = now.AddHours(-1);
        Assert.False(settings.DiscoveryDue(now));

        settings.LastDiscoveredAt = now.AddHours(-7);
        Assert.True(settings.DiscoveryDue(now));
    }

    [Fact]
    public void OldProcoreApiSettingsAreReadAsEmptySyncSettings()
    {
        var old = """{ "clientId": "abc", "clientSecretProtected": "xyz", "useServiceAccount": true, "refreshTokenProtected": "r" }""";
        var settings = JsonSerializer.Deserialize<ProcoreSettings>(old, SmcCaTracker.Core.Storage.TrackerJson.Options)!;
        Assert.Empty(settings.SkippedProjectIds);
        Assert.Null(settings.LastDiscoveredAt);
    }
}
