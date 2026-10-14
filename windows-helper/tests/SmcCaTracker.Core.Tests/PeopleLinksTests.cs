using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.Tests;

/// <summary>Linking Helix and Procore people to Workload Tracker people (the People settings page).</summary>
public sealed class PeopleLinksTests : IDisposable
{
    private readonly FakeTracker tracker = new();
    private readonly TrackerStore store = new(new InMemoryPersistence());
    private readonly WorkloadTrackerSync sync;

    public PeopleLinksTests()
    {
        sync = new WorkloadTrackerSync(store, new WorkloadTrackerApi(new HttpClient(tracker), TimeSpan.Zero), new PlainProtector());
    }

    public void Dispose() => store.Dispose();

    private static readonly TeamMember[] Team =
    [
        new(1, "Pat Engineer", "Electrical", "Lead"),
        new(2, "Robin A. Reviewer", "Mechanical", null),
        new(3, "Sam Second", null, null),
        new(4, "Sam Second", null, null),
        new(5, "José Núñez", null, null)
    ];

    private static ProviderPerson Person(string name, string? id = null) => new("helix", id ?? name, null, name, "SMC", null, 0, false);

    [Theory]
    [InlineData("Pat Engineer", 1)]
    [InlineData("engineer, pat", 1)]
    [InlineData("Pat Engineer (SMC)", 1)]
    [InlineData("Robin Reviewer", 2)]
    [InlineData("Jose Nunez", 5)]
    public void ANameSuggestsTheOneTeamMemberItMatches(string name, int expected)
    {
        Assert.Equal(expected, PeopleLinks.Suggest(Person(name), Team)?.PersonId);
    }

    [Theory]
    [InlineData("Sam Second")] // two people of that name: no guess
    [InlineData("Pat")] // one word is not enough to go on
    [InlineData("Lee Lead")]
    public void NoSuggestionWhenNobodyOrMoreThanOneMatches(string name)
    {
        Assert.Null(PeopleLinks.Suggest(Person(name), Team));
    }

    [Fact]
    public void ATeamMemberSuggestsTheOneHelixPersonWithTheirName()
    {
        ProviderPerson[] people = [Person("Engineer, Pat", "c-1"), Person("Robin Reviewer", "c-2"), Person("Sam Second", "c-3"), Person("Sam Second", "c-4")];

        Assert.Equal("c-1", PeopleLinks.SuggestPerson(Team[0], people)?.ProviderUserId);
        Assert.Equal("c-2", PeopleLinks.SuggestPerson(Team[1], people)?.ProviderUserId); // "Robin A. Reviewer"
        Assert.Null(PeopleLinks.SuggestPerson(Team[2], people)); // two Helix people called Sam Second
        Assert.Null(PeopleLinks.SuggestPerson(Team[4], people));
    }

    [Fact]
    public void ThePeopleOfAProjectAreEveryHolderOnceWithTheirOpenItems()
    {
        var snapshot = new ProviderSnapshot
        {
            Provider = Providers.Helix,
            ProjectId = "H-1",
            User = new SnapshotUser { FullName = "Tim Lead", Company = "SMC", CompanyId = "smc", ProviderUserId = "c-1" },
            Items =
            [
                Item(closed: false, new SnapshotHolder { Name = "Pat Engineer", Company = "SMC", CompanyId = "smc", ProviderUserId = "c-2", State = "current" },
                    new SnapshotHolder { Name = "Vic Vendor", Company = "Vendor Co", CompanyId = "v", ProviderUserId = "c-3", State = "upcoming" }),
                Item(closed: false, new SnapshotHolder { Name = "Pat Engineer", CompanyId = "smc", ProviderUserId = "c-2", Email = "pat@smc.example", State = "current" }),
                Item(closed: true, new SnapshotHolder { Name = "Pat Engineer", ProviderUserId = "c-2", State = "current" },
                    new SnapshotHolder { Name = "Tim Lead", ProviderUserId = "c-1", State = "current" },
                    new SnapshotHolder { Name = "No Id (Vendor Co)", State = "current" })
            ]
        };

        var people = PeopleLinks.From(snapshot);

        Assert.Equal(["Tim Lead", "No Id", "Pat Engineer", "Vic Vendor"], people.Select(person => person.Name));
        var pat = people.Single(person => person.Name == "Pat Engineer");
        Assert.Equal((2, "pat@smc.example", "SMC"), (pat.OpenItems, pat.Email, pat.Company));
        Assert.True(people[0].IsYou);
        Assert.False(people.Single(person => person.Name == "No Id").CanLink);
        Assert.Equal("Vendor Co", people.Single(person => person.Name == "No Id").Company);
        Assert.Equal(["Tim Lead", "Pat Engineer"], people.Where(person => PeopleLinks.AtYourCompany(person, snapshot.User)).Select(person => person.Name));

        static SnapshotItem Item(bool closed, params SnapshotHolder[] holders) => new()
        {
            ItemType = ItemTypes.Rfi, ItemId = Guid.NewGuid().ToString("N"), Number = "RFI", Closed = closed, Holders = holders.ToList()
        };
    }

    [Fact]
    public async Task LinksAreReadConfirmedAndRemovedThroughTheTracker()
    {
        Assert.Null(await sync.ConnectAsync("https://tracker.example", "token-1", 99, null));
        tracker.PersonLinks["c-1"] = (42, "Casey Lead", "auto");
        var you = new ProviderPerson("helix", "c-1", null, "Casey Lead", "SMC", null, 0, true);
        var pat = new ProviderPerson("helix", "c-2", "pat@smc.example", "Pat Engineer", "SMC", null, 3, false);
        var noId = new ProviderPerson("helix", null, null, "No Id", null, null, 1, false);

        var (links, problem) = await sync.ResolvePeopleAsync("helix", [you, pat, noId]);
        Assert.Null(problem);
        Assert.Equal((42, CaPersonLinkSourceEnum.Auto), (links[you.Key].PersonId, links[you.Key].LinkedBy));
        Assert.Null(links[pat.Key].PersonId);
        Assert.False(links.ContainsKey(noId.Key));
        var sent = tracker.Requests.Last();
        Assert.Empty(PinnedSchema.Problems("CaPeopleResolveRequest", sent.Body!.ToJsonString()));

        var (results, linkProblem) = await sync.LinkPeopleAsync("helix", [(pat, 1), (you, 7)]);
        Assert.Null(linkProblem);
        Assert.Equal([CaPersonLinkOutcomeEnum.Linked, CaPersonLinkOutcomeEnum.Rejected], results.Select(result => result.Outcome));
        Assert.Equal(CaPersonLinkRejectionEnum.Protected, results[1].Reason);
        Assert.Equal((1, CaPersonLinkSourceEnum.Manual), (results[0].Link!.PersonId, results[0].Link!.LinkedBy));
        Assert.Empty(PinnedSchema.Problems("CaPeopleLinkRequest", tracker.Requests.Last().Body!.ToJsonString()));

        (results, _) = await sync.LinkPeopleAsync("helix", [(pat, null)]);
        Assert.Equal(CaPersonLinkOutcomeEnum.Unlinked, Assert.Single(results).Outcome);
    }

    [Fact]
    public async Task SomeoneOffTheTeamIsFoundByPeopleSearch()
    {
        Assert.Null(await sync.ConnectAsync("https://tracker.example", "token-1", 99, null));
        tracker.PeopleSearchBody = """
            {"count": 2, "next": null, "previous": null, "results": [
              {"id": 7, "name": "Lee Lead", "departmentName": "Electrical", "roleName": "Engineer", "isActive": true, "email": "lee@smc.example"},
              {"id": 8, "name": "Gone Person", "isActive": false}
            ]}
            """;

        var (people, problem) = await sync.SearchPeopleAsync("lee");

        Assert.Null(problem);
        Assert.Equal([new TeamMember(7, "Lee Lead", "Electrical", "Engineer")], people);
        var request = tracker.Requests.Last();
        Assert.Equal("lee", request.Body!["search_tokens"]![0]!["term"]!.GetValue<string>());
        Assert.Empty(PinnedSchema.Problems("PeopleSearchRequestRequest", request.Body.ToJsonString()));
    }
}
