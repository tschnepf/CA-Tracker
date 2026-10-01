using System.Collections.Concurrent;
using System.Text.Json;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

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
        AppLog.Info("store", $"Loaded {settings.Projects.Count} projects, {snapshots.Count} item lists " +
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

    /// <summary>A copy of the settings to edit and then pass to <see cref="SaveSettings"/>.</summary>
    public TrackerSettings SettingsCopy() => Clone(Settings);

    /// <summary>Replaces the settings (projects included) and saves them in the background.</summary>
    public void SaveSettings(TrackerSettings updated) => ReplaceSettings(updated, fromServer: false);

    /// <summary>Like <see cref="SaveSettings"/>, for changes received from the central server (not sent back).</summary>
    public void SaveSettingsFromServer(TrackerSettings updated) => ReplaceSettings(updated, fromServer: true);

    private void ReplaceSettings(TrackerSettings updated, bool fromServer)
    {
        SettingsStore.Normalize(updated);
        var copy = Clone(updated);
        lock (gate)
        {
            settings = updated;

            // Queued while holding the lock (here and below), so the database gets saves in the order
            // memory changed, whichever thread made them.
            if (fromServer)
            {
                Enqueue(target => target.RunFromServer(() => target.SaveSettings(copy)));
            }
            else
            {
                Enqueue(target => target.SaveSettings(copy));
            }
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
    /// Adds, replaces or removes items received from the company's central server (another person's
    /// sync), without touching the rest of the project's items. When the project or an item type hasn't
    /// been read here, its "read at" time is <paramref name="asOf"/> (when the other person read it); the
    /// times of reads made here are left as they are. Not sent back to the server.
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
            Enqueue(target => target.RunFromServer(() => target.SaveSnapshot(updated)));
        }

        Raise(new StoreChange { Kind = StoreChangeKind.Snapshot, Provider = updated.Provider, ProviderProjectId = projectId });
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

    /// <summary>Records a sign-in. A missing name or company keeps the one seen before.</summary>
    public void RecordAccount(string provider, string? userName, string? company, DateTimeOffset now)
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
                NeedsSignIn = true
            });
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
