using System.Text.Json;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

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

    /// <summary>
    /// Runs saves of changes that came from the company's central server: as one batch, and without
    /// adding them to the change log (so they aren't sent back).
    /// </summary>
    void RunFromServer(Action work);

    /// <summary>Changes not yet sent to the company's central server, oldest first.</summary>
    IReadOnlyList<ChangeRecord> PendingChanges(int limit);

    /// <summary>Marks changes up to and including <paramref name="throughSeq"/> as sent.</summary>
    void MarkChangesSent(long throughSeq);

    /// <summary>What has changes waiting to be sent, as "entity|key" (e.g. "project|6c1d…").</summary>
    IReadOnlySet<string> PendingKeys();

    /// <summary>
    /// Puts every project and item in the change log as it is now (what was waiting is marked sent, as
    /// this replaces it): used to send everything to a central server this PC hasn't used before.
    /// </summary>
    void QueueEverything();
}

/// <summary>One entry of the change log: a project or item that was added, changed or removed.</summary>
public sealed record ChangeRecord(long Seq, string Entity, string Key, string Operation, DateTimeOffset ChangedAt, string? Payload);

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
}

public sealed record FolderScanRecord(string ProjectId, TypeFolderScan Scan);

/// <summary>One read of a Helix or Procore project.</summary>
public sealed class SyncRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Provider { get; set; } = "";
    public string ProjectId { get; set; } = "";

    /// <summary>"full" (every item read) or "changes" (only items changed since the last read).</summary>
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
    Accounts
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
        RecentSyncRuns = runs.ToList()
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

    public void RunFromServer(Action work) => work();

    public IReadOnlyList<ChangeRecord> PendingChanges(int limit) => Array.Empty<ChangeRecord>();

    public void MarkChangesSent(long throughSeq)
    {
    }

    public void QueueEverything()
    {
    }

    public IReadOnlySet<string> PendingKeys() => new HashSet<string>();

    public void Dispose()
    {
    }
}
