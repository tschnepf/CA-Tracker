using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.Tests;

/// <summary>A Workload Tracker that records what it is sent and answers like the real one (or as told).</summary>
internal sealed class FakeTracker : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, Dictionary<string, string> Headers, JsonNode? Body)> Requests { get; } = new();

    /// <summary>Answers the next observation parts with these statuses (then 200 again).</summary>
    public Queue<(int Status, string Body)> Refusals { get; } = new();

    public bool ResendAll { get; set; }
    public int MeStatus { get; set; } = 200;

    public IEnumerable<JsonNode> Observations => Requests.Where(request => request.Path == "/api/ca/observations/").Select(request => request.Body!);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        var body = text is null ? null : JsonNode.Parse(text);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, headers, body));

        return request.RequestUri.AbsolutePath switch
        {
            "/api/auth/me/" => Json(MeStatus, MeStatus == 200
                ? """{"id": 1, "user": {"id": 7, "username": "casey"}, "person": {"id": 42, "name": "Casey Lead"}}"""
                : """{"detail": "Invalid token."}"""),
            "/api/ca/observations/" when Refusals.TryDequeue(out var refusal) => Json(refusal.Status, refusal.Body),
            "/api/ca/observations/" => Json(200, JsonSerializer.Serialize(new
            {
                observationId = body!["observationId"]!.GetValue<string>(),
                part = body["part"]!.GetValue<int>(),
                totalParts = body["totalParts"]!.GetValue<int>(),
                status = body["part"]!.GetValue<int>() == body["totalParts"]!.GetValue<int>() ? "applied" : "pending",
                partsReceived = body["part"]!.GetValue<int>(),
                counts = new { stored = 0, unchanged = 0, stale = 0, removed = 0, unrouted = 0 },
                bindings = Array.Empty<object>(),
                resendAll = ResendAll,
                resendAfterSeconds = ResendAll ? 60 : (int?)null
            })),
            _ when request.RequestUri.AbsolutePath.EndsWith("/revoke/", StringComparison.Ordinal) => Json(200, """{"revoked": true}"""),
            _ => Json(404, """{"detail": "Not found."}""")
        };
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

public sealed class WorkloadTrackerSyncTests : IDisposable
{
    private const string Address = "https://tracker.example";
    private readonly FakeTracker tracker = new();
    private readonly TrackerStore store = new(new InMemoryPersistence());
    private DateTimeOffset now = new(2026, 10, 7, 15, 0, 0, TimeSpan.Zero);
    private readonly WorkloadTrackerSync sync;

    public WorkloadTrackerSyncTests()
    {
        sync = new WorkloadTrackerSync(store, new WorkloadTrackerApi(new HttpClient(tracker)), new PlainProtector(), () => now);
    }

    public void Dispose() => store.Dispose();

    private static ProviderSnapshot Snapshot(string projectId, int items, string provider = Providers.Helix, Func<int, string>? title = null)
    {
        var scanned = new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);
        return new ProviderSnapshot
        {
            SchemaVersion = 1,
            Provider = provider,
            ProjectId = projectId,
            ProjectName = $"Project {projectId}",
            ItemTypes = [ItemTypes.Rfi],
            Complete = true,
            ScannedAt = scanned,
            User = new SnapshotUser { FullName = "Casey Lead", Company = "SMC", ProviderUserId = "contact-1" },
            Coverage = { [ItemTypes.Rfi] = new TypeCoverage { ScannedAt = scanned, Complete = true } },
            Items = Enumerable.Range(1, items).Select(n => new SnapshotItem
            {
                ItemType = ItemTypes.Rfi,
                ItemId = $"{n}",
                Number = $"RFI-{n:D4}",
                Title = title?.Invoke(n) ?? $"Item {n}",
                Status = "Open",
                Holders = [new SnapshotHolder { Name = "Pat Vendor", Company = "Vendor", State = "current", ProviderUserId = $"c-{n % 7}" }]
            }).ToList()
        };
    }

    private void Track(string projectId, string provider = Providers.Helix)
    {
        var settings = store.SettingsCopy();
        settings.Projects.Add(new TrackerProject { Name = projectId, Connection = new ProviderConnection { Provider = provider, ProjectId = projectId } });
        store.SaveSettings(settings);
    }

    private async Task PairAsync()
    {
        Assert.Null(await sync.ConnectAsync(Address, "token-1", 99, now.AddDays(180)));
    }

    private OutboxEntry Entry(string projectId) => Assert.Single(store.Outbox(), entry => entry.ProjectId == projectId);

    [Fact]
    public async Task ConnectingChecksTheTokenAndQueuesEverythingOnce()
    {
        store.SaveSnapshot(Snapshot("H-1", 3));
        Assert.Empty(store.Outbox()); // unpaired: nothing is recorded

        await PairAsync();

        var settings = store.Settings.WorkloadTracker;
        Assert.Equal(("tracker.example#7", "casey", 42), (settings.Account, settings.AccountName, settings.PersonId));
        Assert.True(Guid.TryParse(settings.ClientId, out _));
        var me = Assert.Single(tracker.Requests);
        Assert.Equal("Bearer token-1", me.Headers["Authorization"]);
        Assert.Equal(settings.ClientId, me.Headers[WorkloadTrackerApi.ClientIdHeader]);
        Assert.True(Entry("H-1").FullSet);
    }

    [Fact]
    public async Task ARefusedTokenDoesNotPair()
    {
        tracker.MeStatus = 401;
        var problem = await sync.ConnectAsync(Address, "bad", null, null);
        Assert.Contains("didn't accept", problem);
        Assert.False(store.Settings.WorkloadTracker.IsPaired);
        Assert.NotNull(await sync.ConnectAsync("http://tracker.example", "token", null, null)); // https only
    }

    [Fact]
    public async Task ScansMergeIntoOneEntryPerProject()
    {
        store.SaveSnapshot(Snapshot("H-1", 3));
        await PairAsync();
        var first = Entry("H-1");
        store.CompleteOutbox("helix", "H-1", first.Version); // as if sent

        var changed = Snapshot("H-1", 3, title: n => n == 2 ? "Renamed" : $"Item {n}");
        changed.Items.RemoveAll(item => item.ItemId == "3");
        store.SaveSnapshot(changed);
        var entry = Entry("H-1");
        Assert.False(entry.FullSet);
        Assert.Equal(["rfi|2"], entry.Changed);
        Assert.Equal(["rfi|3"], entry.Removed);
        Assert.Contains("rfi", entry.Verified);

        store.SaveSnapshot(Snapshot("H-1", 3, title: n => n == 2 ? "Renamed" : $"Item {n}"));
        entry = Entry("H-1");
        Assert.Equal(["rfi|2", "rfi|3"], entry.Changed.Order());
        Assert.Empty(entry.Removed);
    }

    [Fact]
    public async Task AnItemReadBackWithOtherOffsetsIsNotAChange()
    {
        store.SaveSnapshot(Snapshot("H-1", 1));
        await PairAsync();
        store.CompleteOutbox("helix", "H-1", Entry("H-1").Version);

        var again = Snapshot("H-1", 1);
        again.Items[0].DueAt = new DateTimeOffset(2026, 10, 3, 17, 0, 0, TimeSpan.FromHours(-7));
        store.SaveSnapshot(again);
        store.CompleteOutbox("helix", "H-1", Entry("H-1").Version);
        var utc = Snapshot("H-1", 1);
        utc.Items[0].DueAt = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        store.SaveSnapshot(utc);

        Assert.Empty(Entry("H-1").Changed);
    }

    [Fact]
    public async Task ATrackedProjectIsSentInPartsAndCleared()
    {
        Track("H-1");
        store.SaveSnapshot(Snapshot("H-1", 1203));
        await PairAsync();

        await sync.SendAsync();

        var parts = tracker.Observations.ToList();
        Assert.Equal([1, 2, 3], parts.Select(part => part["part"]!.GetValue<int>()));
        Assert.Single(parts.Select(part => part["observationId"]!.GetValue<string>()).Distinct());
        Assert.All(parts, part =>
        {
            Assert.Equal("items", part["observationKind"]!.GetValue<string>());
            Assert.True(part["coverage"]![0]!["fullSet"]!.GetValue<bool>());
            Assert.Equal("contact-1", part["collector"]!["providerUserId"]!.GetValue<string>());
            Assert.Empty(PinnedSchema.Problems("CaObservationPartRequest", part.ToJsonString()));
        });
        Assert.Equal(1203, parts.Sum(part => part["items"]!.AsArray().Count));
        Assert.Empty(store.Outbox());
        Assert.Equal(now, sync.LastSentAt);
    }

    [Fact]
    public async Task AProjectNotSetUpHereSendsACatalogEntryOnlyWhenItsCountsChange()
    {
        store.SaveSnapshot(Snapshot("P-9", 4, Providers.Procore));
        await PairAsync();

        await sync.SendAsync();
        var catalog = Assert.Single(tracker.Observations);
        Assert.Equal("catalog", catalog["observationKind"]!.GetValue<string>());
        Assert.Empty(catalog["items"]!.AsArray());
        Assert.Empty(PinnedSchema.Problems("CaObservationPartRequest", catalog.ToJsonString()));

        store.TouchSnapshot(Providers.Procore, "P-9", now);
        await sync.SendAsync();
        Assert.Single(tracker.Observations); // same counts, not 6 hours yet
        Assert.Empty(store.Outbox());
    }

    [Fact]
    public async Task AFailedPartIsRetriedLaterUnderTheSameObservation()
    {
        Track("H-1");
        store.SaveSnapshot(Snapshot("H-1", 700));
        await PairAsync();

        // The second part meets a tracker with sync turned off.
        await SendWithFailureOnPart(2, 503);
        var deferred = Entry("H-1");
        Assert.True(deferred.NextAttemptAt > now);
        Assert.Equal(1, deferred.Attempts);

        await sync.SendAsync(); // not due yet
        Assert.Equal(2, tracker.Observations.Count());

        now = deferred.NextAttemptAt!.Value.AddSeconds(1);
        await sync.SendAsync();
        Assert.Equal(4, tracker.Observations.Count());
        Assert.Single(tracker.Observations.Select(part => part["observationId"]!.GetValue<string>()).Distinct());
        Assert.Empty(store.Outbox());
    }

    private async Task SendWithFailureOnPart(int failingPart, int status)
    {
        // The fake answers in order; let the parts before the failing one through.
        for (var part = 1; part < failingPart; part++)
        {
            tracker.Refusals.Enqueue((200, OkBody(part)));
        }

        tracker.Refusals.Enqueue((status, """{"code": "ingest_disabled", "detail": "Sync is off."}"""));
        await sync.SendAsync();
    }

    private static string OkBody(int part) =>
        $$"""{"observationId": "00000000-0000-0000-0000-000000000000", "part": {{part}}, "totalParts": 2, "status": "pending", "partsReceived": {{part}}, "counts": {"stored": 0, "unchanged": 0, "stale": 0, "removed": 0, "unrouted": 0}, "bindings": [], "resendAll": false, "resendAfterSeconds": null}""";

    [Fact]
    public async Task ARefusedTokenStopsSendingUntilConnectedAgain()
    {
        Track("H-1");
        store.SaveSnapshot(Snapshot("H-1", 2));
        await PairAsync();
        tracker.Refusals.Enqueue((401, """{"detail": "Session is no longer active."}"""));

        await sync.SendAsync();
        Assert.True(sync.NeedsSignIn);
        var calls = tracker.Requests.Count;
        await sync.SendAsync();
        Assert.Equal(calls, tracker.Requests.Count);

        await PairAsync();
        await sync.SendAsync();
        Assert.False(sync.NeedsSignIn);
        Assert.Empty(store.Outbox());
    }

    [Fact]
    public async Task AnUnsupportedSchemaAsksForAnUpdate()
    {
        Track("H-1");
        store.SaveSnapshot(Snapshot("H-1", 2));
        await PairAsync();
        tracker.Refusals.Enqueue((400, """{"code": "unsupported_schema_version", "detail": "Update CA Tracker."}"""));

        await sync.SendAsync();

        Assert.True(sync.NeedsUpdate);
        Assert.Equal("Update CA Tracker.", sync.LastProblem);
        Assert.Single(store.Outbox());
    }

    [Fact]
    public async Task AnInvalidBodyIsSetAsideForADayWhileOtherProjectsGoOn()
    {
        Track("H-1");
        Track("H-2");
        store.SaveSnapshot(Snapshot("H-1", 2));
        store.SaveSnapshot(Snapshot("H-2", 2));
        await PairAsync();
        tracker.Refusals.Enqueue((400, """{"code": "invalid", "detail": "The request body is invalid.", "errors": {"items": ["bad"]}}"""));

        await sync.SendAsync();

        var setAside = Assert.Single(store.Outbox());
        Assert.True(setAside.NextAttemptAt >= now.AddHours(23));
        Assert.Equal(2, tracker.Observations.Count());
    }

    [Fact]
    public async Task ResendAllSendsEverythingAgainAfterTheTrackersDelay()
    {
        Track("H-1");
        store.SaveSnapshot(Snapshot("H-1", 2));
        await PairAsync();
        tracker.ResendAll = true;
        await sync.SendAsync();
        tracker.ResendAll = false;
        Assert.Empty(store.Outbox());

        now = now.AddSeconds(61);
        await sync.SendAsync();

        var resent = tracker.Observations.Last();
        Assert.True(resent["coverage"]![0]!["fullSet"]!.GetValue<bool>());
        Assert.Equal(2, tracker.Observations.Count());
    }

    [Fact]
    public async Task AScanWithNoChangesIsReportedAtMostEveryHalfHour()
    {
        Track("H-1");
        store.SaveSnapshot(Snapshot("H-1", 2));
        await PairAsync();
        await sync.SendAsync();

        store.TouchSnapshot(Providers.Helix, "H-1", now);
        await sync.SendAsync();
        Assert.Single(tracker.Observations);
        Assert.Single(store.Outbox());

        now = now.AddMinutes(31);
        await sync.SendAsync();
        var freshness = tracker.Observations.Last();
        Assert.Empty(freshness["items"]!.AsArray());
        Assert.Empty(store.Outbox());
    }

    [Fact]
    public async Task DisconnectingRevokesTheSessionAndForgetsTheToken()
    {
        store.SaveSnapshot(Snapshot("H-1", 2));
        await PairAsync();

        await sync.DisconnectAsync();

        Assert.Contains(tracker.Requests, request => request.Path == "/api/auth/sessions/99/revoke/");
        Assert.False(store.Settings.WorkloadTracker.IsPaired);
        Assert.Equal(Address, store.Settings.WorkloadTracker.Address);
        Assert.Empty(store.Outbox());
        store.SaveSnapshot(Snapshot("H-1", 3));
        Assert.Empty(store.Outbox());
    }

    [Fact]
    public async Task ChangesRecordedForAnotherAccountAreNotSentUnderThisOne()
    {
        store.SaveSnapshot(Snapshot("H-1", 2));
        await PairAsync();
        store.CompleteOutbox("helix", "H-1", Entry("H-1").Version);
        store.SaveSnapshot(Snapshot("H-1", 2, title: n => $"Changed {n}"));
        Assert.False(Entry("H-1").FullSet);

        var settings = store.SettingsCopy();
        settings.WorkloadTracker.Account = "tracker.example#8";
        store.SaveSettings(settings);
        store.SaveSnapshot(Snapshot("H-1", 2, title: n => $"Again {n}"));

        var entry = Entry("H-1");
        Assert.Equal("tracker.example#8", entry.Account);
        Assert.True(entry.FullSet);
    }

    [Fact]
    public void ProcoreHoldersKeepTheirUserIdAndLoginEmail()
    {
        using var document = JsonDocument.Parse("""
            {"ball_in_court": [
              {"id": 551, "name": "Lee Vendor", "login": "lee@vendor.example", "vendor": {"id": 9, "name": "Vendor Co"}},
              {"id": 552, "name": "No Email", "login": "not-an-email"}
            ]}
            """);

        var holders = ProcoreItems.Holders(document.RootElement);

        Assert.Equal(("551", "lee@vendor.example", "9"), (holders[0].ProviderUserId, holders[0].Email, holders[0].CompanyId));
        Assert.Equal(("552", (string?)null), (holders[1].ProviderUserId, holders[1].Email));
    }

    [Theory]
    [InlineData("tracker.example", "https://tracker.example")]
    [InlineData("https://tracker.example/profile?x=1", "https://tracker.example")]
    [InlineData("http://localhost:8000", "http://localhost:8000")]
    [InlineData("http://tracker.example", null)]
    [InlineData("https://user:pw@tracker.example", null)]
    [InlineData("", null)]
    public void OnlyHttpsAddressesAreAccepted(string address, string? expected)
    {
        Assert.Equal(expected, WorkloadTrackerApi.NormalizeAddress(address));
    }
}
