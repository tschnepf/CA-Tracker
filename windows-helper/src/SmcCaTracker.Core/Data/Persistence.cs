using System.Text.Json;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;

namespace SmcCaTracker.Core.Data;

/// <summary>
/// Durable storage behind <see cref="TrackerStore"/>. The app uses a local SQLite database
/// (SmcCaTracker.Data); a central database can implement the same contract later. Calls come from
/// one thread at a time: <see cref="Load"/> at startup, then the store's background writer.
/// </summary>
public interface ITrackerPersistence : IDisposable
{
    /// <summary>Everything the app needs at startup.</summary>
    PersistedState Load();

    /// <summary>A small value kept with the data, such as whether earlier JSON files were imported.</summary>
    string? GetMeta(string key);

    void SetMeta(string key, string value);

    /// <summary>App settings and every tracker project (with its discipline choices). Projects left out are marked deleted.</summary>
    void SaveSettings(TrackerSettings settings);

    /// <summary>One Helix or Procore project's items, already merged with what was stored.</summary>
    void SaveSnapshot(ProviderSnapshot snapshot);

    /// <summary>The CA folder index for one tracker project and item type.</summary>
    void SaveFolderScan(string projectId, TypeFolderScan scan);

    /// <summary>The Helix and Procore projects the user can see.</summary>
    void SaveCatalog(IReadOnlyList<CatalogProject> projects);

    void SaveAccounts(IReadOnlyList<ProviderAccount> accounts);

    void SaveViewState(ViewState view);

    void RecordSyncRun(SyncRun run);

    /// <summary>Runs several saves as one all-or-nothing change (used by the one-time JSON import).</summary>
    void RunBatch(Action work);

    /// <summary>Keeps (adds or replaces) one project's entry of the Workload Tracker outbox.</summary>
    void SaveOutboxEntry(OutboxEntry entry);

    /// <summary>Removes one project's outbox entry (sent, or no longer wanted).</summary>
    void DeleteOutboxEntry(string provider, string projectId);

    /// <summary>Empties the outbox (unpaired).</summary>
    void ClearOutbox();

    /// <summary>Replaces every item assignment kept for one Workload Tracker project.</summary>
    void ReplaceAssignments(int trackerProjectId, IReadOnlyList<WorkloadAssignment> assignments);

    /// <summary>Forgets every item assignment (unpaired, or paired with another account).</summary>
    void ClearAssignments();

    /// <summary>Replaces the "marked complete" marks kept for one Workload Tracker project.</summary>
    void ReplaceCompletions(int trackerProjectId, IReadOnlyList<WorkloadCompletion> completions);

    /// <summary>Forgets every "marked complete" mark (unpaired, or paired with another account).</summary>
    void ClearCompletions();

    /// <summary>
    /// Keeps the facts (<see cref="ItemFacts.Fingerprint"/>) Workload Tracker accepted for items of one Helix or
    /// Procore project, by item key ("itemType|itemId"), and forgets those of items that went away.
    /// </summary>
    void SaveSentFacts(string provider, string projectId, IReadOnlyList<(string ItemKey, string Fingerprint)> sent, IReadOnlyList<string> forgotten);

    /// <summary>Forgets every sent fact (unpaired, or paired with another account: everything goes again).</summary>
    void ClearSentFacts();
}

/// <summary>What <see cref="ITrackerPersistence.Load"/> returns.</summary>
public sealed class PersistedState
{
    public TrackerSettings Settings { get; set; } = new();
    public List<ProviderSnapshot> Snapshots { get; set; } = new();
    public List<FolderScanRecord> FolderScans { get; set; } = new();
    public List<CatalogProject> Catalog { get; set; } = new();
    public List<ProviderAccount> Accounts { get; set; } = new();
    public ViewState View { get; set; } = new();
    public List<SyncRun> RecentSyncRuns { get; set; } = new();
    public List<OutboxEntry> Outbox { get; set; } = new();
    public List<WorkloadAssignment> Assignments { get; set; } = new();
    public List<WorkloadCompletion> Completions { get; set; } = new();
    public List<SentItemFacts> SentFacts { get; set; } = new();
}

public sealed record FolderScanRecord(string ProjectId, TypeFolderScan Scan);

/// <summary>The facts Workload Tracker last accepted for one item (see <see cref="ItemFacts.Fingerprint"/>).</summary>
public sealed record SentItemFacts(string Provider, string ProjectId, string ItemKey, string Fingerprint);

/// <summary>One read of a Helix or Procore project.</summary>
public sealed class SyncRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "";
    public string ProjectId { get; set; } = "";

    /// <summary>
    /// "full" (every item read), "changes" (only items changed since the last read) or, for Procore,
    /// "checked" (only changed items, and the counts in Procore matched the saved list).
    /// </summary>
    public string Mode { get; set; } = "full";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>"complete", "partial" (some item types couldn't be read) or "failed".</summary>
    public string Status { get; set; } = "";
    public int ItemCount { get; set; }
    public int ChangedCount { get; set; }
    public string? Error { get; set; }
}

public enum StoreChangeKind
{
    Settings,
    Snapshot,
    FolderScan,
    Catalog,
    Accounts,

    /// <summary>
    /// Workload Tracker item assignments (changed here, or read back from the tracker) and the items people
    /// marked complete there (read back with them).
    /// </summary>
    Assignments
}

/// <summary>What changed in the <see cref="TrackerStore"/>.</summary>
public sealed class StoreChange : EventArgs
{
    public StoreChangeKind Kind { get; init; }

    /// <summary>For a snapshot: "helix" or "procore".</summary>
    public string? Provider { get; init; }

    /// <summary>For a snapshot: the Helix/Procore project id.</summary>
    public string? ProviderProjectId { get; init; }

    /// <summary>For a folder index change: the tracker project id.</summary>
    public string? ProjectId { get; init; }

    /// <summary>For a snapshot: false when only its read time changed (no item was added, changed or removed).</summary>
    public bool ItemsChanged { get; init; } = true;
}

/// <summary>Keeps everything in memory (tests, and a fallback when the database can't be opened).</summary>
public sealed class InMemoryPersistence : ITrackerPersistence
{
    private readonly Dictionary<string, string> meta = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FolderScanRecord> scans = new(StringComparer.Ordinal);
    private readonly List<SyncRun> runs = new();
    private string settingsJson = "";
    private List<CatalogProject> catalog = new();
    private List<ProviderAccount> accounts = new();
    private ViewState view = new();
    private readonly Dictionary<string, OutboxEntry> outbox = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<WorkloadAssignment>> assignments = new();
    private readonly Dictionary<int, List<WorkloadCompletion>> completions = new();
    private readonly Dictionary<(string Provider, string ProjectId, string ItemKey), string> sentFacts = new();

    public int SaveCount { get; private set; }

    public PersistedState Load() => new()
    {
        Settings = settingsJson.Length == 0
            ? new TrackerSettings()
            : JsonSerializer.Deserialize<TrackerSettings>(settingsJson, TrackerJson.Options) ?? new TrackerSettings(),
        Snapshots = snapshots.Values.ToList(),
        FolderScans = scans.Values.ToList(),
        Catalog = catalog.ToList(),
        Accounts = accounts.ToList(),
        View = view,
        RecentSyncRuns = runs.ToList(),
        Outbox = outbox.Values.Select(entry => entry.Copy()).ToList(),
        Assignments = assignments.Values.SelectMany(rows => rows).Select(row => row.Copy()).ToList(),
        Completions = completions.Values.SelectMany(rows => rows).Select(row => row.Copy()).ToList(),
        SentFacts = sentFacts.Select(pair => new SentItemFacts(pair.Key.Provider, pair.Key.ProjectId, pair.Key.ItemKey, pair.Value)).ToList()
    };

    public string? GetMeta(string key) => meta.TryGetValue(key, out var value) ? value : null;

    public void SetMeta(string key, string value) => meta[key] = value;

    public void SaveSettings(TrackerSettings settings)
    {
        settingsJson = JsonSerializer.Serialize(settings, TrackerJson.Options);
        SaveCount++;
    }

    public void SaveSnapshot(ProviderSnapshot snapshot)
    {
        snapshots[$"{snapshot.Provider.ToLowerInvariant()}|{snapshot.ProjectId}"] = snapshot;
        SaveCount++;
    }

    public void SaveFolderScan(string projectId, TypeFolderScan scan)
    {
        scans[$"{projectId}|{scan.ItemType}"] = new FolderScanRecord(projectId, scan);
        SaveCount++;
    }

    public void SaveCatalog(IReadOnlyList<CatalogProject> projects)
    {
        catalog = projects.ToList();
        SaveCount++;
    }

    public void SaveAccounts(IReadOnlyList<ProviderAccount> list)
    {
        accounts = list.ToList();
        SaveCount++;
    }

    public void SaveViewState(ViewState state)
    {
        view = state;
        SaveCount++;
    }

    public void RecordSyncRun(SyncRun run)
    {
        runs.Add(run);
        SaveCount++;
    }

    public void RunBatch(Action work) => work();

    public void SaveOutboxEntry(OutboxEntry entry)
    {
        outbox[$"{entry.Provider.ToLowerInvariant()}|{entry.ProjectId}"] = entry.Copy();
        SaveCount++;
    }

    public void DeleteOutboxEntry(string provider, string projectId)
    {
        outbox.Remove($"{provider.ToLowerInvariant()}|{projectId}");
        SaveCount++;
    }

    public void ClearOutbox()
    {
        outbox.Clear();
        SaveCount++;
    }

    public void ReplaceAssignments(int trackerProjectId, IReadOnlyList<WorkloadAssignment> rows)
    {
        assignments[trackerProjectId] = rows.Select(row => row.Copy()).ToList();
        SaveCount++;
    }

    public void ClearAssignments()
    {
        assignments.Clear();
        SaveCount++;
    }

    public void ReplaceCompletions(int trackerProjectId, IReadOnlyList<WorkloadCompletion> rows)
    {
        completions[trackerProjectId] = rows.Select(row => row.Copy()).ToList();
        SaveCount++;
    }

    public void ClearCompletions()
    {
        completions.Clear();
        SaveCount++;
    }

    public void SaveSentFacts(string provider, string projectId, IReadOnlyList<(string ItemKey, string Fingerprint)> sent, IReadOnlyList<string> forgotten)
    {
        foreach (var (key, fingerprint) in sent)
        {
            sentFacts[(provider.ToLowerInvariant(), projectId, key)] = fingerprint;
        }

        foreach (var key in forgotten)
        {
            sentFacts.Remove((provider.ToLowerInvariant(), projectId, key));
        }

        SaveCount++;
    }

    public void ClearSentFacts()
    {
        sentFacts.Clear();
        SaveCount++;
    }

    public void Dispose()
    {
    }
}
