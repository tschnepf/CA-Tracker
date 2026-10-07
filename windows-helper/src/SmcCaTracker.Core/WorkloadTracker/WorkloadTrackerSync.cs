using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>
/// Pairs the app with Workload Tracker and sends the outbox (decisions D2, D7 and D8). Projects set
/// up in the app send their items; every other project seen sends a catalog entry (counts only).
/// Unpaired, nothing here runs and the app works on its own.
/// </summary>
public sealed class WorkloadTrackerSync
{
    /// <summary>A scan that changed nothing still tells the tracker it happened, but not more often than this.</summary>
    public static readonly TimeSpan FreshnessInterval = TimeSpan.FromMinutes(30);

    /// <summary>A catalog entry is sent again only when its counts change or after this long.</summary>
    public static readonly TimeSpan CatalogInterval = TimeSpan.FromHours(6);

    private static readonly TimeSpan QuarantineAfterRefusal = TimeSpan.FromHours(24);

    private readonly TrackerStore store;
    private readonly WorkloadTrackerApi api;
    private readonly ISecretProtector protector;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim running = new(1, 1);
    private readonly Dictionary<string, (long Version, Guid ObservationId)> pendingIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> freshnessSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTimeOffset At, string Counts)> catalogSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> partSizes = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? resendAt;

    public WorkloadTrackerSync(TrackerStore store, WorkloadTrackerApi api, ISecretProtector protector, Func<DateTimeOffset>? clock = null)
    {
        this.store = store;
        this.api = api;
        this.protector = protector;
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Raised (on the sending thread) after a send round, a connect or a disconnect.</summary>
    public event EventHandler? StatusChanged;

    public bool IsPaired => store.Settings.WorkloadTracker.IsPaired;

    /// <summary>The token was refused (expired, revoked or copied): connect again.</summary>
    public bool NeedsSignIn { get; private set; }

    /// <summary>The tracker wants a newer version of this app.</summary>
    public bool NeedsUpdate { get; private set; }

    public string? LastProblem { get; private set; }
    public DateTimeOffset? LastSentAt { get; private set; }

    /// <summary>Projects with something waiting to be sent.</summary>
    public int PendingCount => store.Outbox().Count;

    /// <summary>
    /// Pairs with the tracker at <paramref name="address"/> using a token from its Profile page. Checks
    /// the token, remembers who it belongs to and queues every project for its first send. Returns null
    /// when it worked, otherwise what went wrong.
    /// </summary>
    public async Task<string?> ConnectAsync(string address, string token, int? sessionId, DateTimeOffset? expiresAt, CancellationToken cancel = default)
    {
        var normalized = WorkloadTrackerApi.NormalizeAddress(address);
        if (normalized is null)
        {
            return "Enter the Workload Tracker address, starting with https://.";
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return "No token was received from Workload Tracker.";
        }

        var settings = store.SettingsCopy();
        var sync = settings.WorkloadTracker;
        if (!Guid.TryParse(sync.ClientId, out _))
        {
            sync.ClientId = Guid.NewGuid().ToString("D");
        }

        var me = await api.GetMeAsync(normalized, token.Trim(), sync.ClientId, cancel).ConfigureAwait(false);
        var identity = WorkloadTrackerApi.ReadIdentity(me);
        if (identity is null)
        {
            return me.NetworkError is not null
                ? $"Couldn't reach Workload Tracker: {me.NetworkError}"
                : $"Workload Tracker didn't accept the token ({me.Detail ?? $"HTTP {me.Status}"}).";
        }

        var account = $"{new Uri(normalized).Authority}#{identity.UserId}";
        var accountChanged = !string.Equals(sync.Account, account, StringComparison.Ordinal);
        sync.Address = normalized;
        sync.TokenProtected = protector.Protect(token.Trim());
        sync.SessionId = sessionId;
        sync.ExpiresAt = expiresAt;
        sync.Account = account;
        sync.AccountName = identity.UserName;
        sync.PersonId = identity.PersonId;
        sync.PersonName = identity.PersonName;
        store.SaveSettings(settings);

        NeedsSignIn = false;
        NeedsUpdate = false;
        LastProblem = null;
        if (accountChanged)
        {
            store.ClearOutbox();
        }

        // First contact: everything this PC holds goes once, so the tracker fills straight away.
        store.QueueFullSets();
        AppLog.Info("workload", $"Connected to {normalized} as {identity.UserName}.");
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Stops sending: revokes this PC's session when the tracker can be reached, forgets the token and empties the outbox.</summary>
    public async Task DisconnectAsync(CancellationToken cancel = default)
    {
        var settings = store.SettingsCopy();
        var sync = settings.WorkloadTracker;
        var token = sync.TokenProtected.Length > 0 ? protector.Unprotect(sync.TokenProtected) : null;
        if (token is not null && sync.SessionId is { } sessionId && sync.Address.Length > 0)
        {
            var result = await api.RevokeSessionAsync(sync.Address, token, sync.ClientId, sessionId, cancel).ConfigureAwait(false);
            if (!result.Ok)
            {
                AppLog.Warn("workload", $"Couldn't revoke the Workload Tracker session ({result.NetworkError ?? $"HTTP {result.Status}"}).");
            }
        }

        sync.TokenProtected = "";
        sync.SessionId = null;
        sync.ExpiresAt = null;
        sync.Account = "";
        sync.AccountName = null;
        sync.PersonId = null;
        sync.PersonName = null;
        store.SaveSettings(settings);
        store.ClearOutbox();
        NeedsSignIn = false;
        NeedsUpdate = false;
        LastProblem = null;
        pendingIds.Clear();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sends what is due in the outbox. Safe to call often: one round runs at a time.</summary>
    public async Task SendAsync(CancellationToken cancel = default)
    {
        if (!await running.WaitAsync(0, cancel).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var sync = store.Settings.WorkloadTracker;
            if (!sync.IsPaired || NeedsSignIn || NeedsUpdate)
            {
                return;
            }

            var token = protector.Unprotect(sync.TokenProtected);
            if (string.IsNullOrEmpty(token))
            {
                NeedsSignIn = true;
                LastProblem = "The Workload Tracker token can't be read on this PC. Connect again.";
                return;
            }

            if (resendAt is { } due && clock() >= due)
            {
                resendAt = null;
                store.QueueFullSets();
            }

            foreach (var entry in store.Outbox().OrderBy(entry => entry.UpdatedAt))
            {
                if (entry.NextAttemptAt is { } next && next > clock())
                {
                    continue;
                }

                if (!await SendEntryAsync(entry, sync, token, cancel).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        finally
        {
            running.Release();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sends one project's entry. False when sending should stop for now (signed out, tracker down).</summary>
    private async Task<bool> SendEntryAsync(OutboxEntry entry, WorkloadTrackerSettings sync, string token, CancellationToken cancel)
    {
        var key = $"{entry.Provider}|{entry.ProjectId}";
        var snapshot = store.Snapshot(entry.Provider, entry.ProjectId);
        if (snapshot is null)
        {
            store.CompleteOutbox(entry.Provider, entry.ProjectId, entry.Version);
            return true;
        }

        var connection = store.Settings.FindByConnection(entry.Provider, entry.ProjectId)?.Connection;
        var company = store.Catalog().FirstOrDefault(project =>
            string.Equals(project.Provider, entry.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(project.ProjectId, entry.ProjectId, StringComparison.Ordinal))?.AccountName;
        var project = ObservationBuilder.ProviderProject(snapshot, connection, company);
        var account = store.Account(entry.Provider);
        var sender = new ObservationSender(Guid.Parse(sync.ClientId), TrackerVersion.Current);
        var now = clock();

        if (connection is null)
        {
            // Not set up in this app: a catalog entry, so admins can see the project exists.
            var counts = string.Join("/", ObservationBuilder.Coverage(snapshot, fullSet: false).Select(coverage => $"{coverage.ItemType}:{coverage.ItemCount}:{coverage.OpenCount}"));
            if (catalogSent.TryGetValue(key, out var lastCatalog) && lastCatalog.Counts == counts && now - lastCatalog.At < CatalogInterval)
            {
                store.CompleteOutbox(entry.Provider, entry.ProjectId, entry.Version);
                return true;
            }

            var catalog = ObservationBuilder.Catalog(snapshot, project, sender, Guid.NewGuid(), account);
            var result = await api.PostObservationAsync(sync.Address, token, sync.ClientId, catalog, cancel).ConfigureAwait(false);
            if (!result.Ok)
            {
                return Handle(result, entry, key);
            }

            catalogSent[key] = (now, counts);
            Sent(entry, result);
            return true;
        }

        var freshnessOnly = !entry.FullSet && entry.Changed.Count == 0 && entry.Removed.Count == 0;
        if (freshnessOnly && freshnessSent.TryGetValue(key, out var lastFresh) && now - lastFresh < FreshnessInterval)
        {
            return true; // kept for later: a later scan or the next interval sends it
        }

        // The same observation id while the entry is unchanged, so a retry after a failed part completes it.
        if (!pendingIds.TryGetValue(key, out var pending) || pending.Version != entry.Version)
        {
            pending = (entry.Version, Guid.NewGuid());
            pendingIds[key] = pending;
        }

        var partSize = partSizes.TryGetValue(key, out var size) ? size : ObservationBuilder.MaxItemsPerPart;
        var parts = ObservationBuilder.FromOutbox(entry, snapshot, project, [], sender, pending.ObservationId, account, partSize);
        ApiResult? last = null;
        foreach (var part in parts)
        {
            last = await api.PostObservationAsync(sync.Address, token, sync.ClientId, part, cancel).ConfigureAwait(false);
            if (!last.Ok)
            {
                return Handle(last, entry, key);
            }
        }

        pendingIds.Remove(key);
        freshnessSent[key] = now;
        Sent(entry, last);
        return true;
    }

    private void Sent(OutboxEntry entry, ApiResult? result)
    {
        store.CompleteOutbox(entry.Provider, entry.ProjectId, entry.Version);
        LastSentAt = clock();
        LastProblem = null;
        if (result?.TryRead<CaObservationResult>() is { ResendAll: true } observation && resendAt is null)
        {
            // The tracker lost track (a restore, a missed change): send everything again, after a delay
            // the tracker staggers across desktops.
            resendAt = clock().AddSeconds(observation.ResendAfterSeconds ?? 0);
            AppLog.Info("workload", $"Workload Tracker asked for everything again in {observation.ResendAfterSeconds ?? 0} s.");
        }
    }

    /// <summary>What a failed call means for this project and for the round. True to go on with other projects.</summary>
    private bool Handle(ApiResult result, OutboxEntry entry, string key)
    {
        var detail = result.Detail ?? result.NetworkError ?? $"HTTP {result.Status}";
        var code = result.Error?.Code;
        switch (result.Status)
        {
            case 0:
                Defer(entry, Backoff(entry), $"Can't reach Workload Tracker: {detail}");
                return false;
            case 401:
            case 403:
                NeedsSignIn = true;
                LastProblem = result.Status == 401
                    ? "Workload Tracker needs you to connect again."
                    : $"Workload Tracker refused this PC's token: {detail} Connect again.";
                return false;
            case 400 when code == CaErrorCodeEnum.UnsupportedSchemaVersion:
                NeedsUpdate = true;
                LastProblem = detail;
                return false;
            case 400:
                // A body the tracker won't take: retrying won't help until the app changes.
                AppLog.Warn("workload", $"Workload Tracker refused {key}: {result.Body}");
                Defer(entry, clock() + QuarantineAfterRefusal, $"Refused: {detail}");
                return true;
            case 409:
                pendingIds.Remove(key);
                Defer(entry, clock().AddMinutes(1), detail);
                return true;
            case 413:
                partSizes[key] = Math.Max(25, (partSizes.TryGetValue(key, out var size) ? size : ObservationBuilder.MaxItemsPerPart) / 2);
                pendingIds.Remove(key);
                Defer(entry, clock().AddSeconds(10), "Too large; sending smaller parts.");
                return true;
            default:
                // 429, 503 (sync turned off) and server errors: wait and try again.
                Defer(entry, Backoff(entry), detail);
                return result.Status < 500 && result.Status != 429;
        }
    }

    private void Defer(OutboxEntry entry, DateTimeOffset next, string error)
    {
        store.DeferOutbox(entry.Provider, entry.ProjectId, next, error);
        LastProblem = error;
    }

    /// <summary>Doubling waits from a minute up to an hour, spread a little so desktops don't retry together.</summary>
    private DateTimeOffset Backoff(OutboxEntry entry)
    {
        var minutes = Math.Min(60, Math.Pow(2, Math.Min(entry.Attempts, 6)));
        return clock() + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(Random.Shared.Next(0, 30));
    }
}
