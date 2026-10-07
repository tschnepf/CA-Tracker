using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;

namespace SmcCaTracker.Core.Data;

/// <summary>
/// The app's data, held in memory and saved to the database in the background. Everything is read
/// once at startup, so the screen never waits on the disk: reads come from memory, and each change
/// updates memory at once, is queued for the database, and raises <see cref="Changed"/>. Safe to use
/// from any thread; <see cref="Changed"/> is raised on the thread that made the change.
/// </summary>
public sealed class TrackerStore : IDisposable
{
    private const int MaxCatalogProjects = 5000;

    private readonly object gate = new();
    private readonly ITrackerPersistence persistence;
    private readonly BlockingCollection<Action<ITrackerPersistence>> writes = new();
    private readonly Thread writer;
    private readonly Dictionary<string, ProviderSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TypeFolderScan> folderScans = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SyncRun> recentRuns = new();
    private readonly Dictionary<string, OutboxEntry> outbox = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkloadAssignment> assignments = new(StringComparer.Ordinal);
    private TrackerSettings settings;
    private List<CatalogProject> catalog;
    private List<ProviderAccount> accounts;
    private ViewState view;
    private bool disposed;

    public TrackerStore(ITrackerPersistence persistence)
    {
        this.persistence = persistence;
        PersistedState state;
        using (AppLog.Time("store.load", null, 400))
        {
            state = persistence.Load();
        }

        settings = SettingsStore.Normalize(state.Settings ?? new TrackerSettings());
        foreach (var snapshot in state.Snapshots)
        {
            snapshots[SnapshotKey(snapshot.Provider, snapshot.ProjectId)] = snapshot;
        }

        foreach (var record in state.FolderScans)
        {
            folderScans[ScanKey(record.ProjectId, record.Scan.ItemType)] = record.Scan;
        }

        catalog = state.Catalog ?? new List<CatalogProject>();
        accounts = state.Accounts ?? new List<ProviderAccount>();
        view = state.View ?? new ViewState();
        recentRuns.AddRange(state.RecentSyncRuns ?? new List<SyncRun>());
        foreach (var entry in state.Outbox ?? new List<OutboxEntry>())
        {
            outbox[SnapshotKey(entry.Provider, entry.ProjectId)] = entry;
        }

        foreach (var row in state.Assignments ?? new List<WorkloadAssignment>())
        {
            assignments[row.Key] = row;
        }

        AppLog.Info("store",$"Loaded {settings.Projects.Count} projects, {snapshots.Count} item lists " +
            $"({snapshots.Values.Sum(snapshot => snapshot.Items.Count)} items), {folderScans.Count} folder indexes.");

        writer = new Thread(RunWriter) { IsBackground = true, Name = "CA Tracker database" };
        writer.Start();
    }

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public event EventHandler<StoreChange>? Changed;

    /// <summary>The last error saving to the database, if any (the data is still in memory).</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>Raised (on the database thread) when a save fails, with the reason.</summary>
    public event Action<string>? SaveFailed;

    private static string SnapshotKey(string provider, string projectId) => $"{provider.ToLowerInvariant()}|{projectId}";

    private static string ScanKey(string projectId, string itemType) => $"{projectId}|{itemType.ToLowerInvariant()}";

    // ---- Settings and projects ------------------------------------------------------------

    /// <summary>The live settings object. Change it, then call <see cref="SaveSettings"/>.</summary>
    public TrackerSettings Settings
    {
        get
        {
            lock (gate)
            {
                return settings;
            }
        }
    }

    /// <summary>
    /// Changes a copy of the settings and saves it, in one step against other saves. <paramref name="change"/>
    /// returns false when it changed nothing; then nothing is saved.
    /// </summary>
    public bool UpdateSettings(Func<TrackerSettings, bool> change)
    {
        lock (gate)
        {
            var copy = Clone(settings);
            if (!change(copy))
            {
                return false;
            }

            SettingsStore.Normalize(copy);
            settings = copy;
            var saved = Clone(copy);
            Enqueue(target => target.SaveSettings(saved));
        }

        Raise(new StoreChange { Kind = StoreChangeKind.Settings });
        return true;
    }

    /// <summary>A copy of the settings to edit and then pass to <see cref="SaveSettings"/>.</summary>
    public TrackerSettings SettingsCopy() => Clone(Settings);

    /// <summary>Replaces the settings (projects included) and saves them in the background.</summary>
    public void SaveSettings(TrackerSettings updated)
    {
        SettingsStore.Normalize(updated);
        var copy = Clone(updated);
        lock (gate)
        {
            settings = updated;

            // Queued while holding the lock (here and below), so the database gets saves in the order
            // memory changed, whichever thread made them.
            Enqueue(target => target.SaveSettings(copy));
        }

        Raise(new StoreChange { Kind = StoreChangeKind.Settings });
    }

    // ---- Helix and Procore items ----------------------------------------------------------

    public ProviderSnapshot? Snapshot(string provider, string projectId)
    {
        lock (gate)
        {
            return snapshots.TryGetValue(SnapshotKey(provider, projectId), out var snapshot) ? snapshot : null;
        }
    }

    public IReadOnlyList<ProviderSnapshot> Snapshots()
    {
        lock (gate)
        {
            return snapshots.Values.ToList();
        }
    }

    /// <summary>
    /// Merges a fresh read with what is stored (see <see cref="SnapshotStore.Merge"/>), keeps the
    /// result and saves it in the background. Returns the merged item list.
    /// </summary>
    public ProviderSnapshot SaveSnapshot(ProviderSnapshot incoming)
    {
        SnapshotStore.Validate(incoming);
        ProviderSnapshot merged;
        lock (gate)
        {
            snapshots.TryGetValue(SnapshotKey(incoming.Provider, incoming.ProjectId), out var existing);
            merged = SnapshotStore.Merge(existing, incoming);
            snapshots[SnapshotKey(merged.Provider, merged.ProjectId)] = merged;
            Enqueue(target => target.SaveSnapshot(merged));
            RecordOutbox(existing, merged);
        }

        Raise(new StoreChange { Kind = StoreChangeKind.Snapshot, Provider = merged.Provider, ProviderProjectId = merged.ProjectId });
        return merged;
    }

    /// <summary>
    /// Records that a project was read again and nothing changed: only its read time moves on. The
    /// item list stays the same object's items, so nothing is compared again.
    /// </summary>
    public void TouchSnapshot(string provider, string projectId, DateTimeOffset scannedAt)
    {
        ProviderSnapshot touched;
        lock (gate)
        {
            if (!snapshots.TryGetValue(SnapshotKey(provider, projectId), out var existing))
            {
                return;
            }

            touched = new ProviderSnapshot
            {
                SchemaVersion = existing.SchemaVersion,
                Provider = existing.Provider,
                AccountId = existing.AccountId,
                ProjectId = existing.ProjectId,
                ProjectName = existing.ProjectName,
                ProjectUrl = existing.ProjectUrl,
                ItemTypes = existing.ItemTypes,
                Complete = existing.Complete,
                ScannedAt = scannedAt,
                User = existing.User,
                Items = existing.Items,
                Coverage = existing.Coverage.ToDictionary(
                    pair => pair.Key,
                    pair => new TypeCoverage { ScannedAt = scannedAt, Complete = pair.Value.Complete },
                    StringComparer.OrdinalIgnoreCase)
            };
            snapshots[SnapshotKey(provider, projectId)] = touched;
            Enqueue(target => target.SaveSnapshot(touched));
            RecordOutbox(existing, touched);
        }

        Raise(new StoreChange
        {
            Kind = StoreChangeKind.Snapshot,
            Provider = touched.Provider,
            ProviderProjectId = touched.ProjectId,
            ItemsChanged = false
        });
    }

    /// <summary>
    /// Adds, replaces or removes items read back from a server (another person's sync), without
    /// touching the rest of the project's items. When the project or an item type hasn't been read
    /// here, its "read at" time is <paramref name="asOf"/> (when the other person read it); the times of
    /// reads made here are left as they are. Not sent back: the outbox only records this PC's scans.
    /// </summary>
    public void ApplyRemoteItems(
        string provider,
        string projectId,
        IReadOnlyList<SnapshotItem> upserts,
        IReadOnlyList<(string ItemType, string ItemId)> deletes,
        DateTimeOffset? asOf = null)
    {
        if (upserts.Count == 0 && deletes.Count == 0)
        {
            return;
        }

        ProviderSnapshot updated;
        lock (gate)
        {
            snapshots.TryGetValue(SnapshotKey(provider, projectId), out var existing);
            if (existing is null && upserts.Count == 0)
            {
                // Removing items from a project that has none here: nothing to do.
                return;
            }

            var byKey = new Dictionary<string, SnapshotItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in existing?.Items ?? new List<SnapshotItem>())
            {
                byKey[$"{item.ItemType}|{item.ItemId}"] = item;
            }

            foreach (var (itemType, itemId) in deletes)
            {
                byKey.Remove($"{itemType}|{itemId}");
            }

            foreach (var item in upserts)
            {
                byKey[$"{item.ItemType}|{item.ItemId}"] = item;
            }

            var types = (existing?.ItemTypes ?? new List<string>())
                .Concat(upserts.Select(item => item.ItemType))
                .Where(type => ItemTypes.All.Contains(type, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var readAt = asOf ?? DateTimeOffset.Now;
            var coverage = existing is null
                ? new Dictionary<string, TypeCoverage>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, TypeCoverage>(existing.Coverage, StringComparer.OrdinalIgnoreCase);
            foreach (var type in types.Where(type => !coverage.ContainsKey(type)))
            {
                coverage[type] = new TypeCoverage { ScannedAt = readAt, Complete = false };
            }

            updated = new ProviderSnapshot
            {
                SchemaVersion = existing?.SchemaVersion ?? 1,
                Provider = existing?.Provider ?? provider.ToLowerInvariant(),
                AccountId = existing?.AccountId,
                ProjectId = projectId,
                ProjectName = existing?.ProjectName ?? projectId,
                ProjectUrl = existing?.ProjectUrl,
                ItemTypes = types,
                Complete = existing?.Complete ?? false,
                ScannedAt = existing?.ScannedAt ?? readAt,
                User = existing?.User ?? new SnapshotUser(),
                Items = byKey.Values.ToList(),
                Coverage = coverage
            };
            snapshots[SnapshotKey(provider, projectId)] = updated;
            Enqueue(target => target.SaveSnapshot(updated));
        }

        Raise(new StoreChange { Kind = StoreChangeKind.Snapshot, Provider = updated.Provider, ProviderProjectId = projectId });
    }

    // ---- Workload Tracker outbox ----------------------------------------------------------

    /// <summary>What waits to be sent to Workload Tracker, as copies.</summary>
    public IReadOnlyList<OutboxEntry> Outbox()
    {
        lock (gate)
        {
            return outbox.Values.Select(entry => entry.Copy()).ToList();
        }
    }

    /// <summary>
    /// A project's entry was sent. It goes away unless a scan merged into it meanwhile; then it stays
    /// for the next send (sending an item twice is harmless: the latest scan wins).
    /// </summary>
    public void CompleteOutbox(string provider, string projectId, long version)
    {
        var key = SnapshotKey(provider, projectId);
        lock (gate)
        {
            if (!outbox.TryGetValue(key, out var entry))
            {
                return;
            }

            if (entry.Version == version)
            {
                outbox.Remove(key);
                Enqueue(target => target.DeleteOutboxEntry(provider, projectId));
                return;
            }

            entry.Attempts = 0;
            entry.NextAttemptAt = null;
            entry.LastError = null;
            var copy = entry.Copy();
            Enqueue(target => target.SaveOutboxEntry(copy));
        }
    }

    /// <summary>A send failed: try this project again at <paramref name="next"/>.</summary>
    public void DeferOutbox(string provider, string projectId, DateTimeOffset next, string error)
    {
        lock (gate)
        {
            if (!outbox.TryGetValue(SnapshotKey(provider, projectId), out var entry))
            {
                return;
            }

            entry.Attempts++;
            entry.NextAttemptAt = next;
            entry.LastError = error;
            var copy = entry.Copy();
            Enqueue(target => target.SaveOutboxEntry(copy));
        }
    }

    /// <summary>
    /// Queues every item of every project (or of the given ones) to be sent as a full set: pairing, a
    /// resendAll from the tracker, or a part the tracker refused as inconsistent. Only while paired.
    /// </summary>
    public void QueueFullSets(IEnumerable<(string Provider, string ProjectId)>? only = null)
    {
        lock (gate)
        {
            var sync = settings.WorkloadTracker;
            if (!sync.IsPaired)
            {
                return;
            }

            var keys = only?.Select(project => SnapshotKey(project.Provider, project.ProjectId)).ToList() ?? snapshots.Keys.ToList();
            foreach (var key in keys)
            {
                if (!snapshots.TryGetValue(key, out var snapshot))
                {
                    continue;
                }

                var entry = EntryFor(snapshot, sync.Account, firstSeen: true);
                entry.FullSet = true;
                entry.Changed.Clear();
                entry.Attempts = 0;
                entry.NextAttemptAt = null;
                entry.Version++;
                entry.UpdatedAt = DateTimeOffset.Now;
                var copy = entry.Copy();
                Enqueue(target => target.SaveOutboxEntry(copy));
            }
        }
    }

    /// <summary>Empties the outbox (unpaired: nothing is sent, so nothing is kept).</summary>
    public void ClearOutbox()
    {
        lock (gate)
        {
            outbox.Clear();
            Enqueue(target => target.ClearOutbox());
        }
    }

    // ---- Workload Tracker item assignments ------------------------------------------------

    /// <summary>Every assignment kept (all tracker projects, or one), as copies.</summary>
    public IReadOnlyList<WorkloadAssignment> Assignments(int? trackerProjectId = null)
    {
        lock (gate)
        {
            return assignments.Values
                .Where(row => trackerProjectId is null || row.TrackerProjectId == trackerProjectId)
                .Select(row => row.Copy())
                .ToList();
        }
    }

    /// <summary>
    /// Who is assigned to what, for showing: active assignments grouped by item
    /// (<see cref="WorkloadItemRef.Key"/>), names in order. Built once per refresh of the list.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<WorkloadAssignment>> AssignmentIndex()
    {
        lock (gate)
        {
            return assignments.Values
                .Where(row => row.Active)
                .GroupBy(row => row.ItemKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<WorkloadAssignment>)group
                        .OrderBy(row => row.PersonName, StringComparer.CurrentCultureIgnoreCase)
                        .Select(row => row.Copy())
                        .ToList(),
                    StringComparer.Ordinal);
        }
    }

    /// <summary>Changes made here and not yet accepted by the tracker.</summary>
    public IReadOnlyList<WorkloadAssignment> PendingAssignments()
    {
        lock (gate)
        {
            return assignments.Values.Where(row => row.Pending).OrderBy(row => row.AssignedAt).Select(row => row.Copy()).ToList();
        }
    }

    /// <summary>
    /// Assigns or unassigns people here, at once; each change is pending until the tracker accepts it.
    /// A change that matches what is kept already is left out. Only while paired.
    /// </summary>
    public void ChangeAssignments(IEnumerable<WorkloadAssignment> changes)
    {
        var touched = new HashSet<int>();
        lock (gate)
        {
            if (!settings.WorkloadTracker.IsPaired)
            {
                return;
            }

            foreach (var change in changes)
            {
                assignments.TryGetValue(change.Key, out var kept);
                if (kept is null ? !change.Active : kept.Active == change.Active)
                {
                    continue; // already so, or unassigning someone who isn't assigned
                }

                var row = change.Copy();
                if (row.PersonName.Length == 0 && kept is not null)
                {
                    row.PersonName = kept.PersonName;
                }

                row.Pending = true;
                row.CompletedAt = row.Active ? kept?.CompletedAt : null;
                row.PersonActive = true;
                assignments[row.Key] = row;
                touched.Add(row.TrackerProjectId);
            }

            SaveAssignmentsOf(touched);
        }

        if (touched.Count > 0)
        {
            Raise(new StoreChange { Kind = StoreChangeKind.Assignments });
        }
    }

    /// <summary>
    /// What the tracker answered for sent changes: its row replaces the pending one, unless the
    /// assignment was changed here again meanwhile. A refused change is dropped (the next read-back
    /// shows what the tracker has).
    /// </summary>
    public void CompleteAssignments(IReadOnlyList<(WorkloadAssignment Sent, WorkloadAssignment? Tracker)> results)
    {
        var touched = new HashSet<int>();
        lock (gate)
        {
            foreach (var (sent, tracker) in results)
            {
                if (assignments.TryGetValue(sent.Key, out var kept) && kept.Pending && kept.AssignedAt > sent.AssignedAt)
                {
                    continue; // changed again since it was sent: that change goes next
                }

                if (tracker is null)
                {
                    assignments.Remove(sent.Key);
                }
                else
                {
                    var row = tracker.Copy();
                    row.Pending = false;
                    assignments.Remove(sent.Key);
                    assignments[row.Key] = row;
                }

                touched.Add(sent.TrackerProjectId);
            }

            SaveAssignmentsOf(touched);
        }

        if (touched.Count > 0)
        {
            Raise(new StoreChange { Kind = StoreChangeKind.Assignments });
        }
    }

    /// <summary>
    /// A tracker project's assignments as the tracker has them now. Changes made here that are newer
    /// (or not sent yet) are kept on top.
    /// </summary>
    public void ReplaceAssignments(int trackerProjectId, IReadOnlyList<WorkloadAssignment> fromTracker)
    {
        bool changed;
        lock (gate)
        {
            var before = assignments.Values.Where(row => row.TrackerProjectId == trackerProjectId).ToList();
            var next = fromTracker.Select(row => row.Copy()).ToDictionary(row => row.Key, StringComparer.Ordinal);
            foreach (var row in next.Values)
            {
                row.Pending = false;
            }

            foreach (var local in before.Where(row => row.Pending))
            {
                if (!next.TryGetValue(local.Key, out var theirs) || local.AssignedAt > theirs.AssignedAt)
                {
                    next[local.Key] = local;
                }
            }

            changed = before.Count != next.Count
                || before.Any(row => !next.TryGetValue(row.Key, out var other) || !SameAssignment(row, other));
            if (changed)
            {
                foreach (var row in before)
                {
                    assignments.Remove(row.Key);
                }

                foreach (var row in next.Values)
                {
                    assignments[row.Key] = row;
                }

                SaveAssignmentsOf([trackerProjectId]);
            }
        }

        if (changed)
        {
            Raise(new StoreChange { Kind = StoreChangeKind.Assignments });
        }
    }

    /// <summary>Forgets every assignment (unpaired, or paired with another account).</summary>
    public void ClearAssignments()
    {
        bool had;
        lock (gate)
        {
            had = assignments.Count > 0;
            assignments.Clear();
            Enqueue(target => target.ClearAssignments());
        }

        if (had)
        {
            Raise(new StoreChange { Kind = StoreChangeKind.Assignments });
        }
    }

    private static bool SameAssignment(WorkloadAssignment a, WorkloadAssignment b) =>
        a.Active == b.Active && a.Pending == b.Pending && a.AssignedAt == b.AssignedAt && a.CompletedAt == b.CompletedAt
        && a.PersonActive == b.PersonActive && a.PersonName == b.PersonName && a.AssignedByName == b.AssignedByName;

    /// <summary>Queues the given tracker projects' assignments for the database (under the lock).</summary>
    private void SaveAssignmentsOf(IEnumerable<int> trackerProjectIds)
    {
        foreach (var trackerProjectId in trackerProjectIds)
        {
            var rows = assignments.Values.Where(row => row.TrackerProjectId == trackerProjectId).Select(row => row.Copy()).ToList();
            Enqueue(target => target.ReplaceAssignments(trackerProjectId, rows));
        }
    }

    /// <summary>
    /// Merges what a scan changed into the project's outbox entry (under the lock). Only while paired:
    /// unpaired, the app keeps everything to itself as it always has.
    /// </summary>
    private void RecordOutbox(ProviderSnapshot? before, ProviderSnapshot after)
    {
        var sync = settings.WorkloadTracker;
        if (!sync.IsPaired)
        {
            return;
        }

        var entry = EntryFor(after, sync.Account, firstSeen: before is null);
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in before?.Items ?? new List<SnapshotItem>())
        {
            previous.TryAdd(OutboxEntry.ItemKey(item.ItemType, item.ItemId), Fingerprint(item));
        }

        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in after.Items)
        {
            var key = OutboxEntry.ItemKey(item.ItemType, item.ItemId);
            if (!current.Add(key) || (previous.TryGetValue(key, out var print) && print == Fingerprint(item)))
            {
                continue;
            }

            entry.Removed.Remove(key);
            if (!entry.FullSet)
            {
                entry.Changed.Add(key);
            }
        }

        foreach (var key in previous.Keys.Where(key => !current.Contains(key)))
        {
            // The merge only drops items a scan found gone, so their type's presence was verified.
            entry.Changed.Remove(key);
            entry.Removed.Add(key);
            entry.Verified.Add(OutboxEntry.SplitKey(key).ItemType);
        }

        foreach (var (itemType, coverage) in after.Coverage)
        {
            if (coverage.Complete)
            {
                entry.Verified.Add(itemType);
            }
        }

        if (entry.Changed.Count > OutboxEntry.CollapseThreshold)
        {
            entry.FullSet = true;
            entry.Changed.Clear();
        }

        entry.Version++;
        entry.UpdatedAt = DateTimeOffset.Now;
        var copy = entry.Copy();
        Enqueue(target => target.SaveOutboxEntry(copy));
    }

    /// <summary>The project's entry for this account; a new one sends everything when the project is new or the account changed.</summary>
    private OutboxEntry EntryFor(ProviderSnapshot snapshot, string account, bool firstSeen)
    {
        var key = SnapshotKey(snapshot.Provider, snapshot.ProjectId);
        if (outbox.TryGetValue(key, out var entry) && string.Equals(entry.Account, account, StringComparison.Ordinal))
        {
            return entry;
        }

        // Changes recorded for another account are not sent under this one: everything goes again instead.
        entry = new OutboxEntry
        {
            Provider = snapshot.Provider.ToLowerInvariant(),
            ProjectId = snapshot.ProjectId,
            Account = account,
            FullSet = firstSeen || entry is not null
        };
        outbox[key] = entry;
        return entry;
    }

    /// <summary>The fields Workload Tracker receives, dates in UTC: other changes don't need sending.</summary>
    private static string Fingerprint(SnapshotItem item)
    {
        var text = new StringBuilder();
        void Add(string? value) => text.Append(value ?? "").Append('\u001f');
        static string? When(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        Add(item.Number);
        Add(item.Revision);
        Add(item.Title);
        Add(item.Status);
        Add(item.Closed ? "1" : "0");
        Add(item.Contract);
        Add(When(item.DueAt));
        Add(When(item.CreatedAt));
        Add(item.SourceUrl);
        foreach (var holder in item.Holders)
        {
            Add(holder.Name);
            Add(holder.Company);
            Add(holder.CompanyId);
            Add(holder.State);
            Add(When(holder.Since));
            Add(holder.ProviderUserId);
            Add(holder.Email);
        }

        return text.ToString();
    }

    // ---- CA folder index ------------------------------------------------------------------

    public TypeFolderScan? FolderScan(string projectId, string itemType)
    {
        lock (gate)
        {
            return folderScans.TryGetValue(ScanKey(projectId, itemType), out var scan) ? scan : null;
        }
    }

    /// <summary>Keeps a folder index; <paramref name="notify"/> false when its folders didn't change (only its dates).</summary>
    public void SaveFolderScan(string projectId, TypeFolderScan scan, bool notify = true)
    {
        lock (gate)
        {
            folderScans[ScanKey(projectId, scan.ItemType)] = scan;
            Enqueue(target => target.SaveFolderScan(projectId, scan));
        }

        if (notify)
        {
            Raise(new StoreChange { Kind = StoreChangeKind.FolderScan, ProjectId = projectId });
        }
    }

    // ---- Projects the user can see in Helix and Procore -----------------------------------

    public IReadOnlyList<CatalogProject> Catalog()
    {
        lock (gate)
        {
            return catalog.ToList();
        }
    }

    public void MergeCatalog(CatalogMessage? message, DateTimeOffset now) => MergeCatalog(message, now, replace: false);

    /// <summary>
    /// Adds the projects in <paramref name="message"/> to the catalog. With <paramref name="replace"/>,
    /// the message is the complete list for its site: that site's projects not in it are dropped
    /// (e.g. after a full look through your Procore companies, projects you lost access to).
    /// </summary>
    public void MergeCatalog(CatalogMessage? message, DateTimeOffset now, bool replace)
    {
        if (message is null || !Providers.All.Contains(message.Provider))
        {
            return;
        }

        List<CatalogProject> merged;
        lock (gate)
        {
            var byKey = catalog
                .Where(project => !replace || !string.Equals(project.Provider, message.Provider, StringComparison.OrdinalIgnoreCase))
                .GroupBy(project => project.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(project => project.SeenAt).First(), StringComparer.OrdinalIgnoreCase);
            foreach (var project in message.Projects.Take(MaxCatalogProjects))
            {
                if (string.IsNullOrWhiteSpace(project.ProjectId) || project.ProjectId.Length > 120 || string.IsNullOrWhiteSpace(project.Name))
                {
                    continue;
                }

                project.Provider = message.Provider;
                project.SeenAt = now;
                byKey[project.Key] = project;
            }

            merged = byKey.Values
                .OrderByDescending(project => project.SeenAt)
                .Take(MaxCatalogProjects)
                .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            catalog = merged;
            Enqueue(target => target.SaveCatalog(merged));
        }

        Raise(new StoreChange { Kind = StoreChangeKind.Catalog });
    }

    /// <summary>The catalog plus any project that has items stored, one entry per project.</summary>
    public IReadOnlyList<CatalogProject> CatalogWithSnapshots()
    {
        lock (gate)
        {
            var byKey = catalog
                .GroupBy(project => $"{project.Provider}|{project.ProjectId}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(project => project.SeenAt).First(), StringComparer.OrdinalIgnoreCase);
            foreach (var snapshot in snapshots.Values)
            {
                var key = $"{snapshot.Provider}|{snapshot.ProjectId}";
                if (!byKey.ContainsKey(key))
                {
                    byKey[key] = new CatalogProject
                    {
                        Provider = snapshot.Provider,
                        ProjectId = snapshot.ProjectId,
                        AccountId = snapshot.AccountId,
                        Name = snapshot.ProjectName,
                        SeenAt = snapshot.ScannedAt
                    };
                }
            }

            return byKey.Values.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
    }

    // ---- Sign-ins -------------------------------------------------------------------------

    public ProviderAccount? Account(string provider)
    {
        lock (gate)
        {
            return accounts.FirstOrDefault(account => string.Equals(account.Provider, provider, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Records a sign-in. A missing name, company, id or email keeps the one seen before.</summary>
    public void RecordAccount(string provider, string? userName, string? company, DateTimeOffset now, string? providerUserId = null, string? email = null)
    {
        if (!Providers.All.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        provider = provider.ToLowerInvariant();
        List<ProviderAccount> updated;
        lock (gate)
        {
            var previous = accounts.FirstOrDefault(account => string.Equals(account.Provider, provider, StringComparison.OrdinalIgnoreCase));
            updated = accounts.Where(account => !string.Equals(account.Provider, provider, StringComparison.OrdinalIgnoreCase)).ToList();
            updated.Add(new ProviderAccount
            {
                Provider = provider,
                SignedInAt = now,
                UserName = string.IsNullOrWhiteSpace(userName) ? previous?.UserName : userName.Trim(),
                Company = string.IsNullOrWhiteSpace(company) ? previous?.Company : company.Trim(),
                ProviderUserId = string.IsNullOrWhiteSpace(providerUserId) ? previous?.ProviderUserId : providerUserId.Trim(),
                Email = string.IsNullOrWhiteSpace(email) ? previous?.Email : email.Trim(),
                NeedsSignIn = false
            });
            accounts = updated;
            Enqueue(target => target.SaveAccounts(updated));
        }

        Raise(new StoreChange { Kind = StoreChangeKind.Accounts });
    }

    /// <summary>Notes that a site's sign-in has expired (background syncs pause until you connect again).</summary>
    public void MarkSignInNeeded(string provider)
    {
        List<ProviderAccount> updated;
        lock (gate)
        {
            var previous = accounts.FirstOrDefault(account => string.Equals(account.Provider, provider, StringComparison.OrdinalIgnoreCase));
            if (previous is null || previous.NeedsSignIn)
            {
                return;
            }

            updated = accounts.Where(account => !ReferenceEquals(account, previous)).ToList();
            updated.Add(new ProviderAccount
            {
                Provider = previous.Provider,
                SignedInAt = previous.SignedInAt,
                UserName = previous.UserName,
                Company = previous.Company,
                ProviderUserId = previous.ProviderUserId,
                Email = previous.Email,
                NeedsSignIn = true
            });
            accounts = updated;
            Enqueue(target => target.SaveAccounts(updated));
        }

        Raise(new StoreChange { Kind = StoreChangeKind.Accounts });
    }

    /// <summary>Forgets one site's sign-in (Sign out of Helix / Procore); the other site's stays.</summary>
    public void ClearAccount(string provider)
    {
        List<ProviderAccount> updated;
        lock (gate)
        {
            updated = accounts.Where(account => !string.Equals(account.Provider, provider, StringComparison.OrdinalIgnoreCase)).ToList();
            if (updated.Count == accounts.Count)
            {
                return;
            }

            accounts = updated;
            Enqueue(target => target.SaveAccounts(updated));
        }

        Raise(new StoreChange { Kind = StoreChangeKind.Accounts });
    }

    public void ClearAccounts()
    {
        lock (gate)
        {
            accounts = new List<ProviderAccount>();
            Enqueue(target => target.SaveAccounts(Array.Empty<ProviderAccount>()));
        }

        Raise(new StoreChange { Kind = StoreChangeKind.Accounts });
    }

    // ---- The main window's last view ------------------------------------------------------

    public ViewState View
    {
        get
        {
            lock (gate)
            {
                return view;
            }
        }
    }

    public void SaveViewState(ViewState state)
    {
        lock (gate)
        {
            view = state;
            Enqueue(target => target.SaveViewState(state));
        }
    }

    // ---- Sync history ---------------------------------------------------------------------

    public void RecordSyncRun(SyncRun run)
    {
        lock (gate)
        {
            recentRuns.Add(run);
            if (recentRuns.Count > 500)
            {
                recentRuns.RemoveRange(0, recentRuns.Count - 500);
            }

            Enqueue(target => target.RecordSyncRun(run));
        }
    }

    /// <summary>The latest finished sync of one Helix/Procore project (or the latest that read everything it meant to).</summary>
    public SyncRun? LastSyncRun(string provider, string projectId, bool completeOnly = false)
    {
        lock (gate)
        {
            return recentRuns
                .Where(run => string.Equals(run.Provider, provider, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(run.ProjectId, projectId, StringComparison.Ordinal)
                    && (!completeOnly || run.Status == "complete"))
                .OrderByDescending(run => run.StartedAt)
                .FirstOrDefault();
        }
    }

    // ---- Background saving ----------------------------------------------------------------

    /// <summary>Runs work on the database thread (after everything queued before it) and returns its result.</summary>
    public Task<T> OnDatabaseAsync<T>(Func<ITrackerPersistence, T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            writes.Add(target =>
            {
                try
                {
                    completion.TrySetResult(work(target));
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
            });
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            completion.TrySetException(new ObjectDisposedException(nameof(TrackerStore)));
        }

        return completion.Task;
    }

    /// <summary>Waits until everything queued so far is saved (for tests and closing the app).</summary>
    public bool Flush(TimeSpan timeout)
    {
        if (writes.IsAddingCompleted)
        {
            return true;
        }

        // Not disposed here: after a timeout the writer may still set it.
        var done = new ManualResetEventSlim();
        try
        {
            writes.Add(_ => done.Set());
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }

        return done.Wait(timeout);
    }

    private void Enqueue(Action<ITrackerPersistence> write)
    {
        try
        {
            writes.Add(write);
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            // Closing: nothing more is saved.
        }
    }

    private void RunWriter()
    {
        foreach (var write in writes.GetConsumingEnumerable())
        {
            try
            {
                write(persistence);
            }
            catch (Exception error)
            {
                LastSaveError = error.Message;
                AppLog.Error("store", "Saving to the database failed", error);
                try
                {
                    SaveFailed?.Invoke(error.Message);
                }
                catch (Exception handlerError)
                {
                    AppLog.Error("store", "A save-failed handler failed", handlerError);
                }
            }
        }
    }

    private void Raise(StoreChange change)
    {
        try
        {
            Changed?.Invoke(this, change);
        }
        catch (Exception error)
        {
            AppLog.Error("store", $"A {change.Kind} change handler failed", error);
        }
    }

    private static TrackerSettings Clone(TrackerSettings value) =>
        JsonSerializer.Deserialize<TrackerSettings>(JsonSerializer.Serialize(value, TrackerJson.Options), TrackerJson.Options)
        ?? new TrackerSettings();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        writes.CompleteAdding();
        if (!writer.Join(TimeSpan.FromSeconds(10)))
        {
            // Still writing: leave the connection to the writer (it closes with the process).
            AppLog.Warn("store", "Some changes were still being saved when the app closed.");
            return;
        }

        persistence.Dispose();
    }
}
