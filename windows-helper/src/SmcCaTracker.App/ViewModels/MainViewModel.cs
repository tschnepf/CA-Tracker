using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Central;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.ViewModels;

/// <summary>How the item list is split into sections.</summary>
public static class GroupModes
{
    public const string None = "none";
    public const string Project = "project";
    public const string Type = "type";
    public const string ProjectAndType = "project-type";
}

public static class ItemSorts
{
    public const string Number = "number";
    public const string Person = "person";
    public const string DaysInCourt = "court";
    public const string DaysTillDue = "due";
}

public static class ItemTypeChoices
{
    /// <summary>RFIs and Submittals together.</summary>
    public const string Both = "all";
}

public static class Filters
{
    /// <summary>Items that aren't closed (the default view).</summary>
    public const string Open = "open";
    public const string Attention = "attention";
    public const string All = "all";
    public const string Ready = "ready";
    public const string NoFolder = "nofolder";
    public const string Returned = "returned";
}

/// <summary>
/// State for the main window. The app owns the projects: each is a CA folder, optionally
/// connected to a Helix or Procore project. All data lives in memory in the <see cref="TrackerStore"/>
/// (saved to the database in the background), CA folders are listed by the <see cref="FolderIndex"/>
/// in the background, and Helix is read by the <see cref="HelixSyncService"/> without windows — so
/// nothing on screen waits on the disk, the Egnyte drive or Helix. Reading and comparing never
/// changes a CA folder; every change goes through an OperationPlan that the window previews first.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly TrackerStore store;
    private readonly FolderIndex folderIndex;
    private readonly HelixSyncService helixSync;
    private readonly ProcoreSyncService procoreSync;
    private readonly ISecretProtector protector;
    private readonly RefreshRequestStore refreshStore;
    private readonly List<ProjectViewModel> selectedProjects = new();
    private readonly Dictionary<string, BadgeSource> badgeSources = new(StringComparer.Ordinal);
    private readonly DispatcherTimer badgeTimer;
    private int badgeVersion;
    private readonly ViewState savedView;
    private readonly DispatcherTimer viewStateTimer;
    private readonly HashSet<string> rememberedPeople = new(StringComparer.OrdinalIgnoreCase);
    private bool rememberCompany;
    private bool viewStateReady;
    private readonly Dictionary<string, ProjectComparison> comparisons = new(StringComparer.Ordinal);
    private List<RowViewModel> allRows = new();
    private string sortColumn = "Number";
    private bool sortDescending;
    private string groupBy = GroupModes.ProjectAndType;
    private bool refreshingMany;
    private bool stopMany;
    private readonly DispatcherTimer storeDebounce;
    private readonly DispatcherTimer ticker;
    private readonly DispatcherTimer folderTimer;
    private readonly DispatcherTimer autoSyncTimer;
    private readonly object changeGate = new();
    private readonly HashSet<string> changedItemLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> changedFolderIndexes = new(StringComparer.Ordinal);
    private bool settingsChanged;
    private bool snapshotsTouched;
    private int settingsVersion;
    private bool catalogChanged;
    private bool accountsChanged;
    private int folderTicks;
    private bool folderCheckRunning;
    private bool autoSyncRunning;
    private DateTimeOffset lastAutoSync;
    private ProjectViewModel? selectedProject;
    private ProjectComparison? comparison;
    private string selectedType = ItemTypeChoices.Both;
    private string filter = Filters.Open;
    private DueFilter dueFilter = DueFilter.Any;
    private int dueWithinDays = 7;
    private string sortBy = ItemSorts.Number;
    private string disciplineFilter = "";
    private bool isBusy;
    private string statusMessage = "";
    private string refreshMessage = "";
    private bool refreshMessageIsError;
    private int compareVersion;
    private bool refreshStarting;

    public MainViewModel(
        AppPaths paths,
        TrackerStore store,
        FolderIndex folderIndex,
        HelixSyncService helixSync,
        ProcoreSyncService procoreSync,
        ISecretProtector protector)
    {
        Paths = paths;
        this.store = store;
        this.folderIndex = folderIndex;
        this.helixSync = helixSync;
        this.procoreSync = procoreSync;
        this.protector = protector;
        refreshStore = new RefreshRequestStore(paths);
        refreshStore.Cleanup(DateTimeOffset.Now);

        // The last session's selection, filters and sort (applied before the first compare).
        savedView = store.View;
        ApplySavedView(savedView);
        viewStateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        viewStateTimer.Tick += (_, _) =>
        {
            viewStateTimer.Stop();
            SaveViewState();
        };

        badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        badgeTimer.Tick += async (_, _) =>
        {
            badgeTimer.Stop();
            await UpdateBadgesAsync();
        };

        // Changes to the data (a sync finished, folders changed, settings saved) are gathered for a
        // moment and handled together.
        storeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        storeDebounce.Tick += async (_, _) =>
        {
            storeDebounce.Stop();
            await OnStoreChangedAsync();
        };
        store.Changed += OnStoreChanged;
        store.SaveFailed += OnSaveFailed;

        ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += async (_, _) => await OnTickAsync();
        ticker.Start();

        // CA folders: their dates are checked every 20 seconds and a folder is listed again only if
        // something in it changed.
        folderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        folderTimer.Tick += async (_, _) => await CheckFoldersAsync();
        folderTimer.Start();

        // Helix: tracked projects are read in the background every few minutes (changes only).
        autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        autoSyncTimer.Tick += async (_, _) => await AutoSyncAsync();
        autoSyncTimer.Start();
        lastAutoSync = DateTimeOffset.Now - TimeSpan.FromMinutes(Settings.Sync.IntervalMinutes) + TimeSpan.FromSeconds(20);

        LoadProjects();
        RefreshAccounts();
    }

    /// <summary>The app's data (settings, items, folder index, sign-ins), for the windows that need it.</summary>
    public TrackerStore Store => store;

    /// <summary>
    /// After Connect to Helix / Procore: background reading can be tried again. For Procore, CA Tracker
    /// then finds every company and project you can see and syncs them all, without opening each one.
    /// </summary>
    public void OnConnected(string provider, bool signedIn = true)
    {
        LoadProjects();
        if (string.Equals(provider, Providers.Procore, StringComparison.OrdinalIgnoreCase))
        {
            procoreSync.ResetPageReading();
            if (signedIn)
            {
                _ = SyncAsync(Providers.Procore, discover: true);
            }
        }
    }

    /// <summary>Closes the hidden pages used for background syncs (they reopen on the next sync).</summary>
    public void CloseBackgroundBrowser()
    {
        helixSync.CloseBrowser(pause: TimeSpan.FromSeconds(30));
        procoreSync.CloseBrowser();
        procoreSync.ResetPageReading();
    }

    /// <summary>Raised when a refresh should run in the app's built-in browser window.</summary>
    public event Action<RefreshRequest>? BrowserRefreshRequested;

    /// <summary>
    /// Raised once when Procore's sign-in has expired and a sync needs you to sign in again, so the
    /// Connect to Procore window can open by itself instead of the sync just looking like it failed.
    /// </summary>
    public event Action<string>? SignInRequested;

    private bool procoreSignInRequested;

    // ---- Archive (yours only) -------------------------------------------------------------

    private readonly HashSet<string> archived = new(StringComparer.OrdinalIgnoreCase);
    private bool showArchived;
    private double projectsPaneWidth = 290;

    /// <summary>Raised when the project list should re-check which projects it shows.</summary>
    public event Action? ProjectFilterChanged;

    /// <summary>Show archived projects in the list (dimmed), to unarchive them.</summary>
    public bool ShowArchived
    {
        get => showArchived;
        set
        {
            if (Set(ref showArchived, value))
            {
                ProjectFilterChanged?.Invoke();
                ScheduleViewStateSave();
            }
        }
    }

    public int ArchivedCount => Projects.Count(project => project.IsArchived);
    public bool HasArchived => ArchivedCount > 0 || showArchived;
    public string ShowArchivedText => $"Show archived ({ArchivedCount})";

    /// <summary>Whether the project list shows this project now.</summary>
    public bool IsShownInList(ProjectViewModel project) => showArchived || !project.IsArchived;

    /// <summary>Width of the project list (remembered).</summary>
    public double ProjectsPaneWidth
    {
        get => projectsPaneWidth;
        set
        {
            var width = Math.Clamp(value, 200, 700);
            if (Math.Abs(width - projectsPaneWidth) > 0.5)
            {
                projectsPaneWidth = width;
                ScheduleViewStateSave();
            }
        }
    }

    /// <summary>
    /// Archives or unarchives projects for you only: they leave your list and stop syncing. Nothing
    /// changes in Helix, Procore or on the company server (this is kept with your view on this PC).
    /// </summary>
    public void SetArchived(IEnumerable<ProjectViewModel> projects, bool archive)
    {
        var changed = false;
        foreach (var project in projects.ToList())
        {
            changed |= archive ? archived.Add(project.ArchiveKey) : archived.Remove(project.ArchiveKey);
            project.IsArchived = archive;
        }

        if (!changed)
        {
            return;
        }

        if (archive && !showArchived)
        {
            // Archived projects leave the list, so they leave the selection too.
            var remaining = selectedProjects.Where(project => !project.IsArchived).ToList();
            if (remaining.Count != selectedProjects.Count)
            {
                SetSelectedProjects(remaining, remaining.FirstOrDefault());
            }
        }

        OnArchiveChanged();
        ScheduleViewStateSave();
        RefreshAccounts();
    }

    // The list hides archived projects as they change (live filtering); only the counts need telling.
    private void OnArchiveChanged()
    {
        OnPropertyChanged(nameof(ArchivedCount));
        OnPropertyChanged(nameof(HasArchived));
        OnPropertyChanged(nameof(ShowArchivedText));
    }

    public AppPaths Paths { get; }

    /// <summary>The live settings (owned by the store; change them and call <see cref="SaveSettings"/>).</summary>
    public TrackerSettings Settings => store.Settings;
    public ObservableCollection<ProjectViewModel> Projects { get; } = new();
    public BulkObservableCollection<RowViewModel> Rows { get; } = new();

    /// <summary>
    /// People who have items now, for the Person filter: your company first, then names. Tick any
    /// number; none ticked means everyone.
    /// </summary>
    public ObservableCollection<PersonChoiceViewModel> PersonChoices { get; } = new();

    private IEnumerable<PersonChoiceViewModel> CheckedPeople => PersonChoices.Where(choice => choice.IsChecked);

    public bool HasPersonFilter => CheckedPeople.Any();
    public bool HasNoPersonChoices => PersonChoices.Count == 0;

    /// <summary>What the Person button shows: Everyone, one or two names, or a count.</summary>
    public string PersonFilterText
    {
        get
        {
            var names = CheckedPeople.Select(choice => choice.Name).ToList();
            return names.Count switch
            {
                0 => "Everyone",
                1 or 2 => string.Join(", ", names),
                _ => $"{names.Count} people"
            };
        }
    }

    private bool suppressPersonEvents;

    public void ClearPeople()
    {
        rememberedPeople.Clear();
        rememberCompany = false;
        suppressPersonEvents = true;
        try
        {
            foreach (var choice in PersonChoices)
            {
                choice.IsChecked = false;
            }
        }
        finally
        {
            suppressPersonEvents = false;
        }

        OnPropertyChanged(nameof(PersonFilterText));
        OnPropertyChanged(nameof(HasPersonFilter));
        RebuildRows();
    }

    private void OnPersonChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (suppressPersonEvents || args.PropertyName != nameof(PersonChoiceViewModel.IsChecked))
        {
            return;
        }

        if (sender is PersonChoiceViewModel choice)
        {
            // Remembered by name, so a tick survives switching projects and restarting.
            if (choice.IsCompany)
            {
                rememberCompany = choice.IsChecked;
            }
            else if (choice.IsChecked)
            {
                rememberedPeople.Add(choice.Name);
            }
            else
            {
                rememberedPeople.Remove(choice.Name);
            }
        }

        OnPropertyChanged(nameof(PersonFilterText));
        OnPropertyChanged(nameof(HasPersonFilter));
        RebuildRows();
    }

    public DueFilter DueFilter
    {
        get => dueFilter;
        set
        {
            if (Set(ref dueFilter, value))
            {
                OnPropertyChanged(nameof(IsDueWithin));
                RebuildRows();
            }
        }
    }

    public bool IsDueWithin => DueFilter == DueFilter.DueWithin;

    /// <summary>The # in "due within # days".</summary>
    public int DueWithinDays
    {
        get => dueWithinDays;
        set
        {
            if (Set(ref dueWithinDays, Math.Clamp(value, 0, 365)))
            {
                if (IsDueWithin)
                {
                    RebuildRows();
                }

                ScheduleViewStateSave();
            }
        }
    }

    /// <summary>The Sort dropdown's presets; each sets a column and direction.</summary>
    public string SortBy
    {
        get => sortBy;
        set
        {
            if (!Set(ref sortBy, value ?? ItemSorts.Number))
            {
                return;
            }

            switch (sortBy)
            {
                case ItemSorts.Person:
                    SetSort(nameof(RowViewModel.PersonSortKey), false);
                    break;
                case ItemSorts.DaysInCourt:
                    SetSort(nameof(RowViewModel.DaysInCourt), true);
                    break;
                case ItemSorts.DaysTillDue:
                    SetSort(nameof(RowViewModel.DaysTillDue), false);
                    break;
                default:
                    SetSort(nameof(RowViewModel.Number), false);
                    break;
            }
        }
    }
    public ProjectComparison? Comparison => comparison;

    /// <summary>
    /// The project the header describes (the one clicked last). Setting it from code selects only
    /// that project; the list's multi-selection goes through <see cref="SetSelectedProjects"/>.
    /// </summary>
    public ProjectViewModel? SelectedProject
    {
        get => selectedProject;
        set => SetSelectedProjects(value is null ? Array.Empty<ProjectViewModel>() : new[] { value }, value);
    }

    /// <summary>All projects whose items are shown (Ctrl/Shift-click in the project list).</summary>
    public IReadOnlyList<ProjectViewModel> SelectedProjects => selectedProjects;
    public bool IsMultiProject => selectedProjects.Count > 1;

    public void SetSelectedProjects(IEnumerable<ProjectViewModel> projects, ProjectViewModel? primary)
    {
        var list = projects.Distinct().ToList();
        primary = primary is not null && list.Contains(primary) ? primary : list.FirstOrDefault();
        if (list.SequenceEqual(selectedProjects) && ReferenceEquals(primary, selectedProject))
        {
            return;
        }

        selectedProjects.Clear();
        selectedProjects.AddRange(list);
        selectedProject = primary;
        OnPropertyChanged(nameof(SelectedProject));
        OnPropertyChanged(nameof(SelectedProjects));
        OnPropertyChanged(nameof(IsMultiProject));
        SetRefreshMessage("", false);
        OnHeaderChanged();
        ScheduleViewStateSave();
        _ = CompareAsync();
        // Then make sure the newly shown projects' folder lists are current.
        CheckFoldersSoon();
    }

    /// <summary>Sections in the item list; see <see cref="GroupModes"/>.</summary>
    public string GroupBy
    {
        get => groupBy;
        set
        {
            if (Set(ref groupBy, value ?? GroupModes.None))
            {
                RebuildRows();
            }
        }
    }

    public string SortColumn => sortColumn;
    public bool SortDescending => sortDescending;

    /// <summary>Sorts by a column (a RowViewModel property name, as used by the grid's headers).</summary>
    public void SetSort(string column, bool descending, bool fromHeader = false)
    {
        if (fromHeader)
        {
            // A header click isn't one of the dropdown's presets.
            sortBy = "";
            OnPropertyChanged(nameof(SortBy));
        }

        sortColumn = column;
        sortDescending = descending;
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        RebuildRows();
    }

    public string SelectedType
    {
        get => selectedType;
        set
        {
            if (Set(ref selectedType, value))
            {
                RebuildRows();
            }
        }
    }

    public string Filter
    {
        get => filter;
        set
        {
            if (Set(ref filter, value))
            {
                RebuildRows();
            }
        }
    }

    /// <summary>Discipline code to show, "" for all, or Disciplines.NoneCode for folders without one.</summary>
    public string DisciplineFilter
    {
        get => disciplineFilter;
        set
        {
            if (Set(ref disciplineFilter, value ?? ""))
            {
                RebuildRows();
            }
        }
    }

    /// <summary>
    /// Records disciplines chosen by hand for folders (null code = go back to detecting from the name).
    /// Only the tracker's settings change; folders are not renamed.
    /// </summary>
    public async Task SetDisciplineAsync(IEnumerable<RowViewModel> rows, string? code)
    {
        // Each choice is saved on its own project: by folder name when the item has a folder, otherwise
        // by item, so its download goes into that discipline's folder.
        foreach (var group in rows.Where(row => (row.Row.Folder is not null || row.Row.Item is not null) && !row.Project.IsPlaceholder)
                     .GroupBy(row => row.Project))
        {
            var edited = CloneProject(group.Key.Project);
            foreach (var row in group)
            {
                var key = row.Row.Folder is not null
                    ? Disciplines.Key(row.Row.ItemType, row.Row.Folder.Name)
                    : Disciplines.ItemKey(row.Row.ItemType, row.Row.Item!.ItemId);
                // Back to detecting: kept as an empty choice (not removed) so the company server shares it too.
                edited.Disciplines[key] = code ?? "";
            }

            await UpdateProjectAsync(edited);
        }
    }

    public bool IsBusy
    {
        get => isBusy;
        set => Set(ref isBusy, value);
    }

    public string StatusMessage
    {
        get => statusMessage;
        set => Set(ref statusMessage, value);
    }

    /// <summary>Helix and Procore, for the Connections settings page and the Sync window.</summary>
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new(
        Providers.All.Select(provider => new ConnectionViewModel(provider)));

    public bool IsAnySyncing => Connections.Any(connection => connection.IsSyncing);
    public bool HasNoListedConnections => !Connections.Any(connection => connection.IsListed);

    public ConnectionViewModel ConnectionFor(string provider) =>
        Connections.First(connection => string.Equals(connection.Provider, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>Updates each connection's sign-in, last sync and project count.</summary>
    public void RefreshAccounts()
    {
        foreach (var connection in Connections)
        {
            var account = store.Account(connection.Provider);
            connection.IsSignedIn = account is { NeedsSignIn: false };
            connection.StatusText = account is null
                ? "Not connected"
                : account.NeedsSignIn
                    ? "Sign-in needed — connect again"
                : string.IsNullOrWhiteSpace(account.UserName)
                    ? $"Connected (signed in {Formatting.When(account.SignedInAt)})"
                    : $"Connected as {account.UserName}{(string.IsNullOrWhiteSpace(account.Company) ? "" : $" · {account.Company}")}";

            var projects = ProjectsFor(connection.Provider, includeUntracked: true);
            var tracked = projects.Count(project => !project.IsPlaceholder);
            var skipped = projects.Count(project => IsSyncSkipped(project) || project.IsArchived);
            connection.ProjectCount = projects.Count;
            connection.ProjectsText = projects.Count switch
            {
                0 when ProviderSites.IsProcore(connection.Provider) => "No Procore projects found yet. Connect to Procore, then Sync, to find them.",
                0 => $"No {connection.Label} projects listed yet",
                1 => $"1 project ({tracked} with a CA folder)",
                _ => $"{projects.Count} projects ({tracked} with a CA folder)"
            } + (skipped > 0 ? $" · {skipped} not synced" : "");
            var lastSynced = projects
                .Select(project => project.Snapshot?.ScannedAt)
                .Where(value => value is not null)
                .Max();
            connection.LastSyncedText = lastSynced is null
                ? "Never synced"
                : $"Last synced {Formatting.When(lastSynced.Value)}";
        }

        OnPropertyChanged(nameof(HasNoListedConnections));

        // Procore needs you to sign in again: ask once (opening the Connect window), not on every
        // check. Signing in clears it, so the next expiry asks again.
        var procoreNeedsSignIn = store.Account(Providers.Procore) is { NeedsSignIn: true };
        if (procoreNeedsSignIn && !procoreSignInRequested)
        {
            procoreSignInRequested = true;
            AppLog.Info("procore", "Procore needs a new sign-in: opening Connect to Procore.");
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => SignInRequested?.Invoke(Providers.Procore));
        }
        else if (!procoreNeedsSignIn)
        {
            procoreSignInRequested = false;
        }
    }

    private List<ProjectViewModel> ProjectsFor(string provider, bool includeUntracked = false) =>
        Projects.Where(project => (includeUntracked || !project.IsPlaceholder) && project.Project.Connection is not null
                && string.Equals(project.Project.Connection.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Syncs a connection: refreshes every project from that site (tracked or not yet, except projects
    /// set to not sync), one after another. For Procore it first finds every company and project you can
    /// see, so new projects are picked up without pasting links. A project that can't be read is
    /// flagged and the rest carry on. Stop finishes the current project and skips the rest.
    /// </summary>
    public Task SyncAsync(string provider) => SyncAsync(provider, discover: true);

    private async Task SyncAsync(string provider, bool discover)
    {
        var connection = ConnectionFor(provider);
        if (connection.IsSyncing)
        {
            return;
        }

        connection.IsSyncing = true;
        connection.StopRequested = false;
        OnPropertyChanged(nameof(IsAnySyncing));
        try
        {
            string? found = null;
            if (discover && ProviderSites.IsProcore(provider))
            {
                connection.SyncMessage = "Finding your Procore companies and projects…";
                var discovery = await DiscoverProcoreAsync();
                if (discovery.NeedsSignIn || connection.StopRequested)
                {
                    connection.SyncMessage = discovery.Problem ?? "Stopped.";
                    return;
                }

                found = discovery.Problem ?? FoundText(discovery);
            }

            var all = ProjectsFor(provider, includeUntracked: true);
            var targets = all.Where(project => !IsSyncSkipped(project) && !project.IsArchived).ToList();
            if (targets.Count == 0)
            {
                connection.SyncMessage = all.Count > 0
                    ? $"Every {connection.Label} project is archived or set to not sync. Right-click a project to turn syncing back on."
                    : found ?? $"No {connection.Label} projects yet. Use Connect to {connection.Label} in Settings → Connections to list them.";
                return;
            }

            var distinct = targets.Select(project => project.Project.Connection!.ProjectId).Distinct(StringComparer.Ordinal).Count();
            var result = await RefreshSequentiallyAsync(
                targets,
                (index, project) => connection.SyncMessage = $"Syncing {project.Name} ({Math.Min(index + 1, distinct)} of {distinct})…",
                () => connection.StopRequested);
            connection.SyncMessage = (found is null ? "" : found + " ") + result.Summary(targets.Count, "Synced");
        }
        finally
        {
            connection.IsSyncing = false;
            OnPropertyChanged(nameof(IsAnySyncing));
            RefreshAccounts();
        }
    }

    // ---- Procore: finding projects and choosing which to sync ------------------------------

    /// <summary>
    /// Lists every Procore company and project your sign-in can see (projects hosted by other
    /// companies included) and adds them to the project list under "No CA folder yet".
    /// </summary>
    private async Task<ProcoreDiscovery> DiscoverProcoreAsync()
    {
        var discovery = await WithOtherServersAsync(await procoreSync.DiscoverAsync());
        ApplyProcoreDiscovery(discovery);
        return discovery;
    }

    /// <summary>
    /// Each Procore company lives on one Procore server (app.procore.com or us02.procore.com).
    /// Companies that answered "not found" on your server are asked for on the other one.
    /// </summary>
    private async Task<ProcoreDiscovery> WithOtherServersAsync(ProcoreDiscovery discovery)
    {
        if (discovery.Catalog is null || discovery.OtherServerCompanies.Count == 0)
        {
            return discovery;
        }

        var connection = ConnectionFor(Providers.Procore);
        connection.SyncMessage = $"Looking for projects of {string.Join(", ", discovery.OtherServerCompanies.Select(company => company.Name ?? company.Id))} on Procore's other server…";
        var others = await procoreSync.FindOnOtherServersAsync(discovery.OtherServerCompanies, discovery.Host);
        var merged = discovery.With(others);
        AppLog.Info("procore", $"With Procore's other server: {merged.ProjectCount} projects; still not found: {merged.OtherServerCompanies.Count}.");
        return merged;
    }

    /// <summary>
    /// The Connect to Procore window listed your companies and projects (on its own signed-in page):
    /// adds them to the project list and syncs them all.
    /// </summary>
    public async void OnProcoreProjectsFound(ProcoreDiscovery discovery)
    {
        procoreSync.ResetPageReading();
        ApplyProcoreDiscovery(discovery);
        var connection = ConnectionFor(Providers.Procore);
        if (connection.IsSyncing)
        {
            return;
        }

        try
        {
            if (discovery.OtherServerCompanies.Count > 0)
            {
                discovery = await WithOtherServersAsync(discovery);
                ApplyProcoreDiscovery(discovery);
            }
        }
        catch (Exception error)
        {
            AppLog.Error("procore", "Looking on Procore's other server failed", error);
        }

        connection.SyncMessage = FoundText(discovery);
        await SyncAsync(Providers.Procore, discover: false);
    }

    private void ApplyProcoreDiscovery(ProcoreDiscovery discovery)
    {
        if (discovery.Catalog is { } catalog)
        {
            var now = DateTimeOffset.Now;
            // A full listing replaces the old one, so projects you lost access to drop off; a partial
            // one (from the home page, or with companies that couldn't be listed) only adds.
            var complete = !discovery.FromPage && discovery.CompanyProblems.Count == 0;
            store.MergeCatalog(catalog, now, replace: complete && catalog.Projects.Count > 0);
            if (!string.IsNullOrWhiteSpace(discovery.UserName))
            {
                store.RecordAccount(Providers.Procore, discovery.UserName, null, now);
            }

            Settings.Procore.LastDiscoveredAt = now;
            SaveSettings();
            LoadProjects();
        }
    }

    public static string FoundText(ProcoreDiscovery discovery)
    {
        if (discovery.FromPage)
        {
            return $"Found {discovery.ProjectCount} recent Procore projects (Procore wouldn't list all your companies; see the log).";
        }

        var text = $"Found {discovery.ProjectCount} Procore projects in {discovery.CompanyCount} {(discovery.CompanyCount == 1 ? "company" : "companies")}.";
        if (discovery.CompanyProblems.Count > 0)
        {
            text += $" {discovery.CompanyProblems.Count} couldn't be listed (see the log).";
        }

        return text;
    }

    /// <summary>True for a Procore project you chose not to sync.</summary>
    public bool IsSyncSkipped(ProjectViewModel project) =>
        IsProcore(project) && Settings.Procore.IsSkipped(project.Project.Connection?.ProjectId);

    /// <summary>Turns syncing on or off for Procore projects (they stay in the list either way).</summary>
    public void SetSyncSkipped(IEnumerable<ProjectViewModel> projects, bool skipped)
    {
        var changed = false;
        foreach (var project in projects.Where(IsProcore))
        {
            if (Settings.Procore.SetSkipped(project.Project.Connection!.ProjectId, skipped))
            {
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        SaveSettings();
        foreach (var project in Projects.Where(IsProcore))
        {
            project.IsSyncSkipped = IsSyncSkipped(project);
        }

        RefreshAccounts();
    }

    private sealed record SequenceResult(int Done, IReadOnlyList<string> Failed)
    {
        public string Summary(int total, string verb)
        {
            var skipped = total - Done - Failed.Count;
            return (Failed.Count, skipped) switch
            {
                (0, 0) => $"{verb} {Done} of {total} projects {Formatting.When(DateTimeOffset.Now)}.",
                (0, _) => $"Stopped. {verb} {Done} of {total} projects.",
                _ => $"{verb} {Done} of {total}. Did not finish: {string.Join(", ", Failed)}."
            };
        }
    }

    private static bool IsHelix(ProjectViewModel project) =>
        string.Equals(project.Project.Connection?.Provider, Providers.Helix, StringComparison.OrdinalIgnoreCase);

    private static ProviderSyncTarget HelixTarget(ProjectViewModel project)
    {
        var connection = project.Project.Connection!;
        var helixName = !string.IsNullOrWhiteSpace(connection.ProjectName) ? connection.ProjectName
            : !string.IsNullOrWhiteSpace(project.Snapshot?.ProjectName) ? project.Snapshot!.ProjectName
            : project.Name;
        return new ProviderSyncTarget(connection.ProjectId, helixName, project.Name);
    }

    private static bool IsProcore(ProjectViewModel project) =>
        string.Equals(project.Project.Connection?.Provider, Providers.Procore, StringComparison.OrdinalIgnoreCase);

    private static ProviderSyncTarget ProcoreTarget(ProjectViewModel project)
    {
        var connection = project.Project.Connection!;
        var name = !string.IsNullOrWhiteSpace(connection.ProjectName) ? connection.ProjectName : project.Name;
        return new ProviderSyncTarget(connection.ProjectId, name, project.Name, connection.AccountId,
            string.IsNullOrWhiteSpace(connection.Host) ? null : connection.Host);
    }

    /// <summary>
    /// Reads projects in the background (no windows) and saves their items. Projects sharing one Helix
    /// or Procore project (Base Build and Tenant Fit Out) are read once. While they're read, their rows
    /// in the project list say "updating…".
    /// </summary>
    private async Task<ProviderSyncResult> SyncInBackgroundAsync(
        IReadOnlyList<ProjectViewModel> projects,
        Func<ProjectViewModel, ProviderSyncTarget> toTarget,
        Func<IReadOnlyList<ProviderSyncTarget>, Action<int, ProviderSyncTarget>, Task<ProviderSyncResult>> sync,
        Action<int, ProjectViewModel>? starting)
    {
        var groups = projects
            .GroupBy(project => project.Project.Connection!.ProjectId, StringComparer.Ordinal)
            .Select(group => group.ToList())
            .ToList();
        var ended = new HashSet<ProjectViewModel>();
        foreach (var project in projects)
        {
            project.BeginSync();
        }

        OnHeaderChanged();
        try
        {
            return await sync(
                groups.Select(group => toTarget(group[0])).ToList(),
                (index, _) =>
                {
                    if (index > 0)
                    {
                        foreach (var done in groups[index - 1].Where(ended.Add))
                        {
                            done.EndSync();
                        }
                    }

                    starting?.Invoke(index, groups[index][0]);
                });
        }
        finally
        {
            foreach (var project in projects.Where(ended.Add))
            {
                project.EndSync();
            }

            foreach (var project in projects)
            {
                // The project may have been disconnected or edited while it was read.
                if (project.Project.Connection is { } connection)
                {
                    project.LastRun = store.LastSyncRun(connection.Provider, connection.ProjectId);
                }
            }

            OnHeaderChanged();
        }
    }

    private Task<ProviderSyncResult> SyncHelixAsync(
        IReadOnlyList<ProjectViewModel> projects,
        Action<int, ProjectViewModel>? starting,
        Func<bool>? stop,
        bool full = false) =>
        SyncInBackgroundAsync(projects, HelixTarget, (targets, next) => helixSync.SyncAsync(targets, next, stop, full), starting);

    private Task<ProviderSyncResult> SyncProcoreAsync(
        IReadOnlyList<ProjectViewModel> projects,
        Action<int, ProjectViewModel>? starting,
        Func<bool>? stop) =>
        SyncInBackgroundAsync(projects, ProcoreTarget, (targets, next) => procoreSync.SyncAsync(targets, next, stop), starting);

    /// <summary>
    /// Refreshes projects: Helix ones in the background, all on one hidden page; Procore ones one after
    /// another in the browser window, waiting for each to finish.
    /// </summary>
    private async Task<SequenceResult> RefreshSequentiallyAsync(
        IReadOnlyList<ProjectViewModel> targets,
        Action<int, ProjectViewModel> starting,
        Func<bool> stop)
    {
        var done = 0;
        var failed = new List<string>();
        var helix = targets.Where(IsHelix).ToList();
        if (helix.Count > 0)
        {
            var result = await SyncHelixAsync(helix, (index, project) => starting(index, project), stop);
            foreach (var project in helix)
            {
                if (project.Project.Connection?.ProjectId is { } projectId && result.SyncedProjectIds.Contains(projectId))
                {
                    done++;
                }
                else if (!result.Stopped)
                {
                    failed.Add(project.Name);
                }
            }

            if (result.Problem is not null && !result.Stopped)
            {
                SetRefreshMessage(result.Problem, true);
            }
        }

        var others = targets.Where(project => !IsHelix(project)).ToList();
        if (others.Count > 0)
        {
            var result = await SyncProcoreAsync(others, (index, project) => starting(helix.Count + index, project), stop);
            if (!result.FallbackToWindow)
            {
                foreach (var project in others)
                {
                    if (project.Project.Connection?.ProjectId is { } projectId && result.SyncedProjectIds.Contains(projectId))
                    {
                        done++;
                    }
                    else if (!result.Stopped)
                    {
                        failed.Add(project.Name);
                    }
                }

                if (result.Problem is not null && !result.Stopped)
                {
                    SetRefreshMessage(result.Problem, true);
                }

                others = new List<ProjectViewModel>();
            }
        }

        // Procore in the browser window (when it can't be read in the background), one after another.
        for (var index = 0; index < others.Count; index++)
        {
            if (stop())
            {
                break;
            }

            var project = others[index];
            starting(helix.Count + index, project);
            var alreadyRunning = project.IsRefreshing;
            var previousId = project.ActiveRefresh?.Id;
            if (!alreadyRunning)
            {
                await StartRefreshAsync(project);
            }

            var started = alreadyRunning || project.ActiveRefresh?.Id != previousId;
            // Stops waiting if the project is removed while it refreshes.
            while (project.IsRefreshing && Projects.Contains(project))
            {
                await Task.Delay(500);
            }

            if (started && project.ActiveRefresh?.Status == RefreshStatus.Complete)
            {
                done++;
            }
            else
            {
                failed.Add(project.Name);
            }
        }

        return new SequenceResult(done, failed);
    }

    public bool HasProject => SelectedProject is not null;
    public bool HasNoProjects => Projects.Count == 0;
    public bool HasCaFolder => !string.IsNullOrWhiteSpace(SelectedProject?.Project.CaFolder);
    public bool IsConnected => SelectedProject?.IsConnected == true;
    public bool IsNotConnected => HasProject && !IsConnected;
    public bool IsRefreshing => refreshingMany || SelectedProject?.IsRefreshing == true;
    public bool CanRefresh => HasProject && !IsRefreshing && !refreshStarting;
    public string ProjectTitle => IsMultiProject
        ? $"{selectedProjects.Count} projects selected"
        : SelectedProject?.Name ?? "Choose or add a project";
    public string CaFolderText => HasCaFolder ? SelectedProject!.Project.CaFolder : "No CA folder chosen";
    public string ConnectionText
    {
        get
        {
            var text = SelectedProject?.ConnectionText ?? "";
            var scope = SelectedProject?.Project.Scope;
            if (scope is not { IsSplit: true })
            {
                return text;
            }

            var filters = new List<string>();
            if (scope.FilterItems)
            {
                filters.Add("items");
            }

            if (scope.FilterFolders)
            {
                filters.Add("folders");
            }

            var suffix = filters.Count == 0
                ? Phases.Label(scope.Phase)
                : $"{Phases.Label(scope.Phase)}: {string.Join(" and ", filters)} that say {Phases.Short(scope.Phase)}{(scope.IncludeUnlabeled ? " or neither" : "")}";
            return $"{text}  ·  {suffix}";
        }
    }
    public string RefreshButtonText => IsRefreshing ? "Refreshing…"
        : IsMultiProject ? $"Refresh {selectedProjects.Count} projects"
        : IsConnected ? $"Refresh from {SelectedProject!.ProviderLabel}" : "Refresh";

    public string DataTimestampText
    {
        get
        {
            if (SelectedProject is null || !IsConnected)
            {
                return "";
            }

            var snapshot = SelectedProject.Snapshot;
            var label = SelectedProject.ProviderLabel;
            if (SelectedProject.LastReadFailed)
            {
                var run = SelectedProject.LastRun!;
                return $"{label}: the last read {Formatting.When(run.StartedAt)} failed — {run.Error}" +
                    (snapshot is null ? "" : $" · showing items read {Formatting.When(snapshot.ScannedAt)}");
            }

            if (snapshot is null)
            {
                return $"{label} data: never refreshed";
            }

            var types = ItemTypes.All
                .Select(type => (type, coverage: snapshot.CoverageFor(type)))
                .Where(entry => entry.coverage is not null)
                .ToList();
            var distinctTimes = types.Select(entry => entry.coverage!.ScannedAt).Distinct().Count();
            return distinctTimes > 1
                ? $"{label} data: " + string.Join(" · ", types.Select(entry => $"{ItemTypes.Plural(entry.type)} {Formatting.When(entry.coverage!.ScannedAt)}"))
                : $"{label} data refreshed {Formatting.When(snapshot.ScannedAt)}";
        }
    }

    public string FolderTimestampText => comparison is null || !HasCaFolder
        ? ""
        : $"CA folder checked {Formatting.When(comparison.ComparedAt)}";

    public string RefreshMessage => refreshMessage;
    public bool HasRefreshMessage => refreshMessage.Length > 0;
    public bool RefreshMessageIsError => refreshMessageIsError;

    public string ProjectSubtitle
    {
        get
        {
            if (IsMultiProject)
            {
                return $"Showing items from all {selectedProjects.Count} selected projects. The CA folder and connection above are for {SelectedProject?.Name}.";
            }

            var snapshot = SelectedProject?.Snapshot;
            if (snapshot is { Complete: false } && string.Equals(snapshot.Provider, Providers.Procore, StringComparison.OrdinalIgnoreCase))
            {
                return "Procore shows only the rows visible in its RFI and Submittal lists, so folders missing from them are not flagged.";
            }

            return "";
        }
    }

    public bool HasProjectSubtitle => ProjectSubtitle.Length > 0;

    public string CompanyText
    {
        get
        {
            if (comparison is null || !IsConnected)
            {
                return "";
            }

            if (!comparison.Company.IsKnown)
            {
                return "Returned: closed items only until a company is known (refresh, or choose one in Edit project).";
            }

            var rule = Settings.Returned;
            var parts = new List<string>();
            if (rule.WhenNoCompanyHolder)
            {
                parts.Add($"no one from {comparison.Company.DisplayName} holds it");
            }

            if (rule.WhenClosed)
            {
                parts.Add("it is closed");
            }

            if (rule.WhenOnlyComingUp)
            {
                parts.Add("it is only coming up later");
            }

            return parts.Count == 0
                ? "Returned: only the status lists in Settings decide."
                : $"Returned when {string.Join(" or ", parts)}.";
        }
    }

    public string FolderWarning
    {
        get
        {
            if (comparison is null || !HasCaFolder)
            {
                return "";
            }

            var problems = comparison.Types.Values
                .Select(type => type.Scan.Error ?? (type.Scan.TypeFolderExists ? null : $"No \"{Path.GetFileName(type.Scan.TypeFolderPath)}\" folder in this CA folder."))
                .Where(message => message is not null)
                .Distinct()
                .ToList();
            var hidden = comparison.Types.Values.Sum(type => type.HiddenByPhase);
            if (hidden > 0 && comparison.Project.Scope is { IsSplit: true } scope)
            {
                var part = Phases.Short(scope.Phase);
                problems.Add($"{hidden} folder{(hidden == 1 ? " is" : "s are")} hidden because the name says neither BB nor TFO. " +
                    $"To include them in {part}, open Edit project → Base Build / Tenant Fit Out and tick \"Also include items and folders that mention neither BB nor TFO\".");
            }

            return string.Join("  ", problems);
        }
    }

    public bool HasFolderWarning => FolderWarning.Length > 0;

    /// <summary>The selected project is a Helix/Procore project with no CA folder chosen yet.</summary>
    public bool IsPlaceholder => SelectedProject?.IsPlaceholder == true;
    public bool CanEditConnection => HasProject && !IsPlaceholder;
    public string EditButtonText => IsPlaceholder ? "Choose CA folder…" : "Edit project…";

    public bool CanCreateTypeFolders => comparison is not null
        && HasCaFolder
        && comparison.Types.Values.Any(type => type.Scan.Error is null && !type.Scan.TypeFolderExists);

    public string RfiTabText => TabText(ItemTypes.Rfi);
    public string SubmittalTabText => TabText(ItemTypes.Submittal);
    public string BothTabText => TabText(ItemTypeChoices.Both);

    public string EmptyText => !HasProject ? ""
        : IsPlaceholder && Rows.Count == 0 ? "Select Choose CA folder… to track this project's RFIs and Submittals against its CA folder."
        : !HasCaFolder && Rows.Count == 0 ? "Choose this project's CA folder in Edit project."
        : !IsConnected && Rows.Count == 0 ? "Connect this project to Helix or Procore, then Refresh, to compare its items."
        : Rows.Count == 0 && Filter == Filters.Attention && !HasPersonFilter && DueFilter == DueFilter.Any ? "Nothing needs attention here."
        : Rows.Count == 0 ? "No items match this filter."
        : "";

    public bool HasEmptyText => EmptyText.Length > 0;

    public int CheckedCount => allRows.Count(row => row.IsChecked);

    public string SelectionText => CheckedCount == 0 ? "Nothing ticked" : $"{CheckedCount} ticked";

    /// <summary>Header tick: true when every tickable shown row is ticked, null when some are, false when none.</summary>
    public bool? AllShownChecked
    {
        get
        {
            var tickable = Rows.Where(row => row.CanSelect).ToList();
            var ticked = tickable.Count(row => row.IsChecked);
            return ticked == 0 ? false : ticked == tickable.Count ? true : null;
        }
    }

    /// <summary>Ticks (or unticks) every row the current filters show.</summary>
    public void SetAllShownChecked(bool value)
    {
        foreach (var row in Rows.Where(row => row.CanSelect))
        {
            row.IsChecked = value;
        }
    }

    /// <summary>Ticked rows, from every shown project.</summary>
    public IReadOnlyList<RowViewModel> CheckedRows() => allRows.Where(row => row.IsChecked).ToList();

    /// <summary>
    /// What the Download button needs for a row: the Helix item, and where its files go by default
    /// (its matched folder, else a new "&lt;number&gt; - &lt;name&gt;" folder in the project's RFI/Submittal folder).
    /// Null when the item can't be downloaded (not Helix, or no item).
    /// </summary>
    public Views.DownloadRequest? DownloadRequestFor(RowViewModel row)
    {
        var project = row.Project.Project;
        var item = row.Row.Item;
        if (project?.Connection is null || item is null
            || !string.Equals(project.Connection.Provider, Providers.Helix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? parent = null;
        if (!string.IsNullOrWhiteSpace(project.CaFolder)
            && row.Comparison.For(row.Row.ItemType).Scan is { Error: null } scan && scan.TypeFolderPath.Length > 0)
        {
            // Into the matching discipline and BB/TFO folders when the project is organised that way
            // (e.g. Submittals\Elec\BB); otherwise the RFI/Submittal folder itself. The discipline is
            // the one set by hand for the item, else the one detected from its title.
            var phase = project.Scope is { IsSplit: true } scope ? scope.Phase : (ProjectPhase?)null;
            parent = FolderScanner.SuggestParent(scan.TypeFolderPath, row.Row.Discipline, phase);
        }

        var projectName = !string.IsNullOrWhiteSpace(project.Connection.ProjectName)
            ? project.Connection.ProjectName
            : row.Project.Snapshot?.ProjectName ?? "";
        return new Views.DownloadRequest(item.ItemType, item.ItemId, project.Connection.ProjectId, item.Number, item.Title,
            projectName, row.Row.Folder?.FullPath, parent);
    }

    public IReadOnlyList<CatalogProject> KnownProviderProjects() => store.CatalogWithSnapshots();

    public ProviderSnapshot? SnapshotFor(TrackerProject project) =>
        project.Connection is null ? null : store.Snapshot(project.Connection.Provider, project.Connection.ProjectId);

    // ---- Projects -------------------------------------------------------------------------

    public void LoadProjects()
    {
        var existing = Projects.ToDictionary(project => project.Id);
        var namesLearned = false;
        foreach (var project in Settings.Projects)
        {
            if (!existing.TryGetValue(project.Id, out var viewModel))
            {
                viewModel = new ProjectViewModel(project);
                Projects.Add(viewModel);
            }
            else if (!ReferenceEquals(viewModel.Project, project))
            {
                viewModel.Project = project;
            }

            // The store hands back the same object until the items change.
            var snapshot = SnapshotFor(project);
            if (!ReferenceEquals(snapshot, viewModel.Snapshot))
            {
                viewModel.Snapshot = snapshot;
            }

            viewModel.LastRun = project.Connection is null ? null : store.LastSyncRun(project.Connection.Provider, project.Connection.ProjectId);

            // A project connected from a pasted link learns its name from the first refresh.
            if (project.Connection is not null && string.IsNullOrWhiteSpace(project.Connection.ProjectName)
                && !string.IsNullOrWhiteSpace(snapshot?.ProjectName))
            {
                project.Connection.ProjectName = snapshot!.ProjectName;
                namesLearned = true;
            }
        }

        var ids = Settings.Projects.Select(project => project.Id).ToHashSet();
        AddProviderProjects(existing, ids);
        foreach (var stale in Projects.Where(project => !ids.Contains(project.Id)).ToList())
        {
            Projects.Remove(stale);
            selectedProjects.Remove(stale);
        }

        var archiveChanged = false;
        foreach (var project in Projects)
        {
            project.IsSyncSkipped = IsSyncSkipped(project);
            var isArchived = archived.Contains(project.ArchiveKey);
            if (project.IsArchived != isArchived)
            {
                project.IsArchived = isArchived;
                archiveChanged = true;
            }
        }

        if (archiveChanged)
        {
            OnArchiveChanged();
        }

        var ordered = Projects.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var current = Projects.IndexOf(ordered[index]);
            if (current != index)
            {
                Projects.Move(current, index);
            }
        }

        if (namesLearned)
        {
            SaveSettings();
        }

        OnPropertyChanged(nameof(HasNoProjects));
        OnHeaderChanged();
        RefreshAccounts();
        ScheduleBadges();
    }

    /// <summary>
    /// Lists every Helix/Procore project seen so far (Connect to Helix, refreshes) that no tracker
    /// project is connected to yet, so it shows under "No CA folder yet".
    /// </summary>
    private void AddProviderProjects(Dictionary<string, ProjectViewModel> existing, HashSet<string> ids)
    {
        static string Key(string provider, string projectId) => $"{provider.ToLowerInvariant()}|{projectId}";
        var linked = Settings.Projects
            .Where(project => project.Connection is not null)
            .Select(project => Key(project.Connection!.Provider, project.Connection.ProjectId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = store.Catalog()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ProjectId) && !linked.Contains(Key(entry.Provider, entry.ProjectId)))
            .GroupBy(entry => Key(entry.Provider, entry.ProjectId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(entry => entry.SeenAt).First());
        foreach (var entry in available)
        {
            var placeholder = ProjectViewModel.ForProviderProject(entry);
            if (!existing.TryGetValue(placeholder.Id, out var viewModel))
            {
                viewModel = placeholder;
                viewModel.Snapshot = SnapshotFor(viewModel.Project);
                Projects.Add(viewModel);
            }
            else
            {
                if (viewModel.Project.Name != placeholder.Project.Name)
                {
                    viewModel.Project = placeholder.Project;
                }

                var snapshot = SnapshotFor(viewModel.Project);
                if (!ReferenceEquals(snapshot, viewModel.Snapshot))
                {
                    viewModel.Snapshot = snapshot;
                }
            }

            ids.Add(viewModel.Id);
        }
    }

    public ProjectViewModel AddProject(TrackerProject project)
    {
        Settings.Projects.Add(project);
        SaveSettings();
        LoadProjects();
        var viewModel = Projects.First(candidate => candidate.Id == project.Id);
        SelectedProject = viewModel;
        return viewModel;
    }

    /// <summary>Replaces a project with an edited copy. Only the app's settings change.</summary>
    public async Task UpdateProjectAsync(TrackerProject edited)
    {
        if (ProjectViewModel.IsPlaceholderId(edited.Id))
        {
            // Not a tracker project yet: it is saved only once its CA folder is chosen.
            return;
        }

        var index = Settings.Projects.FindIndex(project => project.Id == edited.Id);
        if (index < 0)
        {
            Settings.Projects.Add(edited);
        }
        else
        {
            Settings.Projects[index] = edited;
        }

        SaveSettings();
        LoadProjects();
        await CompareAsync();
    }

    /// <summary>Removes a project from the tracker. Its CA folder is not touched.</summary>
    public void RemoveProject(string projectId)
    {
        Settings.Projects.RemoveAll(project => project.Id == projectId);
        SaveSettings();
        if (SelectedProject?.Id == projectId)
        {
            SelectedProject = null;
        }

        LoadProjects();
        if (SelectedProject is null && Projects.Count > 0)
        {
            SelectedProject = Projects.FirstOrDefault(project => project.HasCaFolder) ?? Projects[0];
        }
    }

    public TrackerProject CloneProject(TrackerProject project) =>
        JsonSerializer.Deserialize<TrackerProject>(JsonSerializer.Serialize(project, TrackerJson.Options), TrackerJson.Options)
        ?? new TrackerProject();

    // ---- Refresh --------------------------------------------------------------------------

    /// <summary>
    /// Rechecks the CA folder and, for a connected project, opens it in the built-in browser window,
    /// which reads fresh items. The request completes when that window has read every page.
    /// </summary>
    /// <summary>Refresh button: the selected project, or each selected project in turn.</summary>
    public async Task StartRefreshAsync()
    {
        if (!IsMultiProject)
        {
            await StartRefreshAsync(SelectedProject);
            return;
        }

        if (refreshingMany)
        {
            return;
        }

        var targets = selectedProjects.Where(project => project.IsConnected).ToList();
        refreshingMany = true;
        stopMany = false;
        OnHeaderChanged();
        try
        {
            var result = await RefreshSequentiallyAsync(
                targets,
                (index, project) => SetRefreshMessage($"Refreshing {project.Name} ({index + 1} of {targets.Count})…", false),
                () => stopMany);
            SetRefreshMessage(result.Summary(targets.Count, "Refreshed"), result.Failed.Count > 0);
            await RecheckFoldersAsync();
        }
        finally
        {
            refreshingMany = false;
            OnHeaderChanged();
        }
    }

    /// <summary>Starts a refresh of any project (the selected one, or one being synced).</summary>
    public async Task StartRefreshAsync(ProjectViewModel? project)
    {
        if (project is null || project.IsRefreshing)
        {
            return;
        }

        if (IsHelix(project) || (IsProcore(project) && !procoreSync.PageReadingUnavailable))
        {
            // Read in the background; other refreshes can start meanwhile. (A Procore read that has to
            // fall back to the browser window starts that window from here too.)
            await StartRefreshCoreAsync(project);
            OnHeaderChanged();
            return;
        }

        if (refreshStarting)
        {
            return;
        }

        refreshStarting = true;
        OnPropertyChanged(nameof(CanRefresh));
        try
        {
            await StartRefreshCoreAsync(project);
        }
        finally
        {
            refreshStarting = false;
            OnHeaderChanged();
        }
    }

    private async Task StartRefreshCoreAsync(ProjectViewModel project)
    {
        var selected = project == SelectedProject;
        if (selected)
        {
            // Refresh also lists the CA folders again, in case something changed the dates didn't show.
            await RecheckFoldersAsync(new[] { project });
        }

        if (project.Project.Connection is null)
        {
            if (selected)
            {
                SetRefreshMessage("Folder checked. Connect this project to Helix or Procore to refresh its items.", false);
            }

            return;
        }

        if (IsHelix(project))
        {
            if (selected)
            {
                SetRefreshMessage($"Reading {project.Name} from Helix…", false);
            }

            // An explicit Refresh reads everything, to be sure.
            var result = await SyncHelixAsync(new[] { project }, null, null, full: true);
            if (selected)
            {
                SetRefreshMessage(
                    result.Problem ?? (result.Done > 0
                        ? $"Updated from Helix {Formatting.When(DateTimeOffset.Now)} · {(result.Changed == 0 ? "no changes" : result.Changed == 1 ? "1 item changed" : $"{result.Changed} items changed")}."
                        : $"Helix could not be read for {project.Name}. Try again, or see the log in Settings."),
                    result.Problem is not null || result.Done == 0);
            }

            return;
        }

        if (IsProcore(project) && !procoreSync.PageReadingUnavailable)
        {
            if (selected)
            {
                SetRefreshMessage($"Reading {project.Name} from Procore…", false);
            }

            var procore = await SyncProcoreAsync(new[] { project }, null, null);
            if (!procore.FallbackToWindow)
            {
                if (selected)
                {
                    SetRefreshMessage(
                        procore.Problem ?? (procore.Done > 0
                            ? $"Updated from Procore {Formatting.When(DateTimeOffset.Now)} · every RFI and Submittal read."
                            : $"Procore could not be read for {project.Name}. Try again, or see the log in Settings."),
                        procore.Problem is not null || procore.Done == 0);
                }

                return;
            }
        }

        RefreshRequest request;
        try
        {
            request = refreshStore.Create(project.Project, Settings.Browser, DateTimeOffset.Now);
        }
        catch (Exception error)
        {
            if (selected)
            {
                SetRefreshMessage(error.Message, true);
            }

            return;
        }

        project.ActiveRefresh = request;
        if (selected)
        {
            SetRefreshMessage($"Opening {project.ProviderLabel} in the CA Tracker browser…", false);
        }

        BrowserRefreshRequested?.Invoke(request);
    }

    public void CancelRefresh()
    {
        if (helixSync.IsBusy)
        {
            helixSync.Cancel();
            SetRefreshMessage("Stopped reading Helix.", false);
        }

        if (procoreSync.IsBusy)
        {
            procoreSync.Cancel();
            SetRefreshMessage("Stopped reading Procore.", false);
        }

        // Several projects being refreshed from here: skip the rest and cancel the one in progress.
        if (refreshingMany)
        {
            stopMany = true;
            foreach (var other in selectedProjects.Where(project => project != SelectedProject && project.ActiveRefresh is { IsFinished: false }))
            {
                other.ActiveRefresh = refreshStore.Cancel(other.ActiveRefresh!.Id, DateTimeOffset.Now);
            }

            SetRefreshMessage("Stopping after the current project…", false);
            OnHeaderChanged();
        }

        var project = SelectedProject;
        if (project?.ActiveRefresh is null || project.ActiveRefresh.IsFinished)
        {
            return;
        }

        project.ActiveRefresh = refreshStore.Cancel(project.ActiveRefresh.Id, DateTimeOffset.Now);
        SetRefreshMessage("Refresh cancelled.", false);
        OnHeaderChanged();
    }

    private async Task OnTickAsync()
    {
        // Procore refreshes run in the browser window and are tracked through their request files.
        foreach (var project in Projects.Where(project => project.ActiveRefresh is { IsFinished: false }).ToList())
        {
            try
            {
                await TickRefreshAsync(project);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // The request file is being written by the browser window; try again next second.
            }
        }
    }

    private async Task TickRefreshAsync(ProjectViewModel project)
    {
        if (project.ActiveRefresh is null)
        {
            return;
        }

        {
            var now = DateTimeOffset.Now;
            var latest = refreshStore.Load(project.ActiveRefresh!.Id) ?? project.ActiveRefresh;
            if (!latest.IsFinished && now > latest.ExpiresAt)
            {
                latest = refreshStore.Fail(latest.Id, TimeoutMessage(project), now) ?? new RefreshRequest
                {
                    Id = latest.Id,
                    Provider = latest.Provider,
                    Status = RefreshStatus.Failed,
                    Error = TimeoutMessage(project),
                    CreatedAt = latest.CreatedAt,
                    FinishedAt = now
                };
            }

            if (!latest.IsFinished)
            {
                if (project == SelectedProject)
                {
                    var waited = now - latest.CreatedAt;
                    var phase = latest.ClaimedAt is null
                        ? $"Opening {project.ProviderLabel} in the CA Tracker browser. If it asks you to sign in, do that there once. Waiting"
                        : latest.Steps.Count > 1
                            ? $"Reading {project.ProviderLabel} page {Math.Min(latest.CompletedSteps + 1, latest.Steps.Count)} of {latest.Steps.Count}"
                            : $"Reading {project.ProviderLabel}";
                    SetRefreshMessage($"{phase}… {waited:m\\:ss}", false);
                }

                return;
            }

            project.ActiveRefresh = latest;
            project.Snapshot = SnapshotFor(project.Project);
            RefreshAccounts();
            if (project == SelectedProject)
            {
                if (latest.Status == RefreshStatus.Complete)
                {
                    SetRefreshMessage($"Refreshed from {project.ProviderLabel} {Formatting.When(latest.FinishedAt ?? now)} · {latest.ItemCount} items.", false);
                    LoadProjects();
                    await CompareAsync();
                }
                else if (latest.Status == RefreshStatus.Failed)
                {
                    SetRefreshMessage($"Refresh did not finish: {latest.Error}", true);
                }

                OnHeaderChanged();
            }
            else if (latest.Status == RefreshStatus.Complete)
            {
                if (selectedProjects.Contains(project))
                {
                    // Another shown project: update the list (a multi-project Refresh does it once at the end).
                    if (!refreshingMany)
                    {
                        await CompareAsync();
                    }
                }
                else
                {
                    project.Compared = false;
                }
            }
        }
    }

    private static string TimeoutMessage(ProjectViewModel project) =>
        $"The CA Tracker browser did not finish. Check that you are signed in to {project.ProviderLabel} in that window.";

    private void SetRefreshMessage(string message, bool isError)
    {
        refreshMessage = message;
        refreshMessageIsError = isError;
        OnPropertyChanged(nameof(RefreshMessage));
        OnPropertyChanged(nameof(HasRefreshMessage));
        OnPropertyChanged(nameof(RefreshMessageIsError));
    }

    // ---- Comparison -----------------------------------------------------------------------

    public async Task CompareAsync()
    {
        var projects = selectedProjects.Count > 0
            ? selectedProjects.ToList()
            : SelectedProject is null ? new List<ProjectViewModel>() : new List<ProjectViewModel> { SelectedProject };
        var version = ++compareVersion;
        if (projects.Count == 0)
        {
            IsBusy = false;
            StatusMessage = "";
            comparison = null;
            comparisons.Clear();
            allRows = new List<RowViewModel>();
            RebuildPersonOptions();
            RebuildRows();
            OnHeaderChanged();
            return;
        }

        var settings = Settings;
        var inputs = projects.Select(project => (View: project, Tracked: project.Project, project.Snapshot)).ToList();
        IsBusy = true;
        StatusMessage = projects.Count == 1
            ? string.IsNullOrWhiteSpace(inputs[0].Tracked.CaFolder) ? "" : $"Reading {inputs[0].Tracked.CaFolder}…"
            : $"Reading {projects.Count} projects…";
        try
        {
            // Saved folder lists make this quick; a project never listed before is listed now (once).
            var results = await Task.Run(() =>
            {
                using var timing = AppLog.Time("compare", $"{inputs.Count} project(s)", 250);
                return inputs
                    .Select(input => (input.View, Result: ProjectComparer.Compare(input.Snapshot, input.Tracked, settings,
                        (itemType, layout) => folderIndex.GetOrScan(input.Tracked, itemType, layout))))
                    .ToList();
            });
            if (version != compareVersion)
            {
                return;
            }

            // Keep ticks on folders and items that are still shown.
            var ticked = allRows
                .Where(row => row.IsChecked && row.CanSelect)
                .Select(TickKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            comparisons.Clear();
            var rows = new List<RowViewModel>();
            var today = DateTime.Today;
            foreach (var (view, result) in results)
            {
                comparisons[view.Id] = result;
                foreach (var type in result.Types.Values)
                {
                    foreach (var row in type.Rows)
                    {
                        var rowView = new RowViewModel(row, view, result, today);
                        if (rowView.CanSelect && ticked.Contains(TickKey(rowView)))
                        {
                            rowView.IsChecked = true;
                        }

                        rowView.PropertyChanged += (_, args) =>
                        {
                            if (args.PropertyName == nameof(RowViewModel.IsChecked))
                            {
                                OnPropertyChanged(nameof(CheckedCount));
                                OnPropertyChanged(nameof(SelectionText));
                                OnPropertyChanged(nameof(AllShownChecked));
                            }
                        };
                        rows.Add(rowView);
                    }
                }

                var hasFolder = !string.IsNullOrWhiteSpace(view.Project.CaFolder);
                view.AttentionCount = hasFolder ? result.Types.Values.Sum(type => type.AttentionCount) : 0;
                view.Compared = hasFolder;
            }

            allRows = rows;
            comparison = SelectedProject is not null && comparisons.TryGetValue(SelectedProject.Id, out var primary) ? primary : null;
            RebuildPersonOptions();
            StatusMessage = "";
        }
        catch (Exception error)
        {
            StatusMessage = $"Could not compare: {error.Message}";
        }
        finally
        {
            if (version == compareVersion)
            {
                IsBusy = false;
                RebuildRows();
                OnHeaderChanged();
            }
        }
    }

    /// <summary>
    /// Lists the CA folders of the given projects (default: the shown ones) again, then compares. Used
    /// after the app moves or creates folders, and by Refresh.
    /// </summary>
    public async Task RecheckFoldersAsync(IEnumerable<ProjectViewModel>? projects = null)
    {
        var settings = Settings;
        var targets = (projects ?? selectedProjects).Where(project => project.HasCaFolder && !project.IsPlaceholder)
            .Select(project => project.Project)
            .ToList();
        if (targets.Count > 0)
        {
            IsBusy = true;
            try
            {
                await Task.Run(() =>
                {
                    using var timing = AppLog.Time("folders.recheck", $"{targets.Count} project(s)", 1500);
                    foreach (var project in targets)
                    {
                        folderIndex.Refresh(project, project.EffectiveFolders(settings.Folders), force: true);
                    }
                });
            }
            catch (Exception error)
            {
                AppLog.Error("folders", "Listing CA folders failed", error);
            }
        }

        await CompareAsync();
    }

    /// <summary>Checks the CA folders soon (the window calls this when you come back to it, e.g. from Explorer).</summary>
    public void CheckFoldersSoon() => _ = CheckFoldersAsync();

    /// <summary>
    /// Background folder check: the shown projects' folders by date every time, projects never listed
    /// yet once, everyone else every few minutes, and a full re-list of the shown projects every 15
    /// minutes. Changes come back through the store and update the list.
    /// </summary>
    private async Task CheckFoldersAsync()
    {
        if (folderCheckRunning)
        {
            return;
        }

        folderCheckRunning = true;
        folderTicks++;
        var settings = Settings;
        var force = folderTicks % 45 == 0;
        var shown = selectedProjects.Where(project => project.HasCaFolder && !project.IsPlaceholder).Select(project => project.Project).ToList();
        var others = Projects
            .Where(project => project.HasCaFolder && !project.IsPlaceholder && !selectedProjects.Contains(project))
            .Select(project => project.Project)
            .Where(project => folderTicks % 15 == 0 || !folderIndex.HasCached(project, project.EffectiveFolders(settings.Folders)))
            .ToList();
        try
        {
            await Task.Run(() =>
            {
                using var timing = AppLog.Time("folders.check", $"{shown.Count} shown, {others.Count} others{(force ? ", full" : "")}", 3000);
                // Lists older than these are re-read even if no date changed (in case the drive
                // didn't update a folder's date for a change made elsewhere).
                foreach (var project in shown)
                {
                    folderIndex.Refresh(project, project.EffectiveFolders(settings.Folders), force, maxAge: TimeSpan.FromMinutes(5));
                }

                foreach (var project in others)
                {
                    folderIndex.Refresh(project, project.EffectiveFolders(settings.Folders), maxAge: TimeSpan.FromHours(1));
                }
            });
        }
        catch (Exception error)
        {
            AppLog.Error("folders", "Checking CA folders failed", error);
        }
        finally
        {
            folderCheckRunning = false;
        }
    }

    /// <summary>
    /// Background sync of the tracked projects on the interval in Settings: Helix (only changed items)
    /// and Procore (when it can be read in the background). Sites whose sign-in has expired are skipped.
    /// </summary>
    private async Task AutoSyncAsync()
    {
        var sync = Settings.Sync;
        if (autoSyncRunning || !sync.AutoSync || refreshingMany
            || DateTimeOffset.Now - lastAutoSync < TimeSpan.FromMinutes(sync.IntervalMinutes))
        {
            return;
        }

        lastAutoSync = DateTimeOffset.Now;
        autoSyncRunning = true;
        try
        {
            if (!helixSync.IsBusy && !ConnectionFor(Providers.Helix).IsSyncing && store.Account(Providers.Helix) is { NeedsSignIn: false })
            {
                await AutoSyncSiteAsync(Providers.Helix, project => IsHelix(project) && !project.IsArchived, (targets, starting) => SyncHelixAsync(targets, starting, null));
            }

            var procoreReady = store.Account(Providers.Procore) is { NeedsSignIn: false } && !procoreSync.PageReadingUnavailable;
            if (procoreReady && !procoreSync.IsBusy && !ConnectionFor(Providers.Procore).IsSyncing)
            {
                // New projects appear without anyone pasting links: your companies are looked through
                // again every few hours.
                if (Settings.Procore.DiscoveryDue(DateTimeOffset.Now))
                {
                    var connection = ConnectionFor(Providers.Procore);
                    connection.SyncMessage = "Finding your Procore companies and projects…";
                    var discovery = await DiscoverProcoreAsync();
                    connection.SyncMessage = discovery.Problem ?? FoundText(discovery);
                }

                if (store.Account(Providers.Procore) is { NeedsSignIn: false })
                {
                    // Every Procore project you can see is kept up to date, with or without a CA folder,
                    // except those set to not sync.
                    await AutoSyncSiteAsync(Providers.Procore, project => IsProcore(project) && !IsSyncSkipped(project) && !project.IsArchived,
                        (targets, starting) => SyncProcoreAsync(targets, starting, null), includeUntracked: true);
                }
            }
        }
        finally
        {
            autoSyncRunning = false;
            RefreshAccounts();
        }
    }

    private async Task AutoSyncSiteAsync(
        string provider,
        Func<ProjectViewModel, bool> isSite,
        Func<IReadOnlyList<ProjectViewModel>, Action<int, ProjectViewModel>, Task<ProviderSyncResult>> run,
        bool includeUntracked = false)
    {
        var targets = Projects.Where(project => isSite(project)
                && (includeUntracked || !project.IsPlaceholder || selectedProjects.Contains(project))
                && !project.IsRefreshing)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var connection = ConnectionFor(provider);
        var label = Providers.Label(provider);
        var distinct = targets.Select(project => project.Project.Connection!.ProjectId).Distinct().Count();
        var result = await run(targets, (index, project) =>
            connection.SyncMessage = $"Updating {project.Name} from {label} ({index + 1} of {distinct})…");
        if (result.FallbackToWindow)
        {
            // Can't be read in the background: Sync or Refresh reads it in the browser window.
            connection.SyncMessage = $"{label} is read in the browser window when you Sync or Refresh.";
            return;
        }

        connection.SyncMessage = result.Problem
            ?? $"Updated {result.Done} of {distinct} {label} projects {Formatting.When(DateTimeOffset.Now)}" +
               (result.Changed > 0 ? $" · {result.Changed} items changed." : "") +
               (result.FailedProjectIds.Count > 0 ? $" {result.FailedProjectIds.Count} couldn't be read (marked in the project list)." : "");
        if (result.NeedsSignIn)
        {
            StatusMessage = $"{label} needs you to sign in again: Settings → Connections → Connect to {label}.";
        }
    }

    /// <summary>
    /// Saves an anonymized copy of the selected Helix project's items to the exports folder, for turning
    /// real projects into test cases. Returns the file, or a reason it couldn't.
    /// </summary>
    public async Task<(string? File, string? Problem)> ExportHelixSampleAsync()
    {
        var project = SelectedProject;
        if (project is null || !IsHelix(project))
        {
            return (null, "Select a project connected to Helix first.");
        }

        var (json, problem) = await helixSync.ExportSampleAsync(HelixTarget(project));
        if (json is null)
        {
            return (null, problem);
        }

        Directory.CreateDirectory(Paths.ExportsDirectory);
        var file = Path.Combine(Paths.ExportsDirectory, $"helix-sample-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(file, json);
        AppLog.Info("export", $"Exported an anonymized Helix sample to {file}.");
        return (file, null);
    }

    // ---- Company settings and updates ------------------------------------------------------

    private CompanySettings? company;
    private string? companyPath;
    private string? companyError;
    private UpdateService? updates;
    private UpdateInfo? update;
    private DispatcherTimer? updateTimer;

    public void UseCompanySettings(CompanySettings? settings, string? path, string? error)
    {
        company = settings;
        companyPath = path;
        companyError = error;
        foreach (var name in new[] { nameof(ManagedByText), nameof(HasManagedByText), nameof(AreFoldersLocked) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>What Settings says about settings your company manages.</summary>
    public string ManagedByText => companyError is not null
        ? $"The company settings file couldn't be read ({companyError}); the app's own settings are used."
        : company is null ? ""
        : $"Some settings are set by {(string.IsNullOrWhiteSpace(company.CompanyName) ? "your company" : company.CompanyName)}" +
          (company.Locked.Count > 0 ? $" and can't be changed here ({string.Join(", ", company.Locked)})." : ".");

    public bool HasManagedByText => ManagedByText.Length > 0;
    public bool AreFoldersLocked => company?.IsLocked("folders") == true;

    // ---- Company server (optional) ---------------------------------------------------------

    private CentralSync? central;
    private DispatcherTimer? centralTimer;

    /// <summary>Shares with the company's CA Tracker server (when one is set up) shortly after starting, then every two minutes.</summary>
    public void UseCentral(CentralSync service)
    {
        central = service;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        centralTimer = timer;
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromMinutes(2);
            if (service.IsConfigured)
            {
                await ShareWithCentralAsync();
            }
        };
        timer.Start();
        RaiseCentral();
    }

    public static bool IsValidCentralServer(string server) => CentralSync.NormalizeServer(server).Length > 0;

    /// <summary>The server's address (set by the company settings file when it names one).</summary>
    public string CentralServerText => central?.Server ?? "";

    public bool IsCentralServerLocked => central?.IsCompanyServer == true;

    /// <summary>What Settings → Connections shows about the company server.</summary>
    public string CentralStatusText
    {
        get
        {
            if (central is { IsCompanyServer: true, Server.Length: 0 })
            {
                return "⚠ The company settings file's server address isn't a valid https:// address.";
            }

            if (central is null || central.Server.Length == 0)
            {
                return "Not set up: everything stays on this PC.";
            }

            if (!central.HasToken)
            {
                return $"Enter your access token for {central.Server}.";
            }

            if (central.LastProblem is { } problem)
            {
                return $"⚠ {problem}";
            }

            if (central.LastSyncAt is not { } at)
            {
                return $"Set up: sharing with {central.Server} starts in a moment.";
            }

            var who = string.IsNullOrWhiteSpace(central.ConnectedAs) ? "" : $" as {central.ConnectedAs}";
            var received = central.LastReceived > 0 ? $" · {central.LastReceived} changes from others" : "";
            return $"Sharing with {central.Server}{who} · last shared {Formatting.When(at)}{received}";
        }
    }

    private void RaiseCentral()
    {
        OnPropertyChanged(nameof(CentralServerText));
        OnPropertyChanged(nameof(IsCentralServerLocked));
        OnPropertyChanged(nameof(CentralStatusText));
    }

    /// <summary>Saves the server and token, checks them and shares. Null when it worked; otherwise what's wrong.</summary>
    public async Task<string?> ConnectCentralAsync(string server, string? token)
    {
        if (central is null)
        {
            return "The company server isn't available in this version.";
        }

        central.Configure(server, token);
        RaiseCentral();
        if (central.Server.Length == 0)
        {
            return IsCentralServerLocked
                ? "The company settings file's server address isn't a valid https:// address. Ask whoever manages CA Tracker."
                : "Saved. With no server address, everything stays on this PC.";
        }

        var problem = await central.TestAsync();
        if (problem is null)
        {
            problem = (await central.SyncAsync()).Problem;
        }

        RaiseCentral();
        return problem;
    }

    /// <summary>Shares now. Null when it worked; otherwise what's wrong.</summary>
    public async Task<string?> ShareWithCentralAsync()
    {
        if (central is not { IsConfigured: true })
        {
            return "Set up the company server first.";
        }

        if (central.IsRunning)
        {
            return "Already sharing; it finishes in a moment.";
        }

        var result = await central.SyncAsync();
        RaiseCentral();
        return result.Problem;
    }

    public void DisconnectCentral()
    {
        central?.Disconnect();
        RaiseCentral();
    }

    /// <summary>Checks the company's update location now, then every six hours.</summary>
    public void UseUpdates(UpdateService service)
    {
        updates = service;
        if (string.IsNullOrWhiteSpace(company?.UpdateFeed))
        {
            return;
        }

        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(6);
            await CheckForUpdateAsync();
        };
        updateTimer.Start();
    }

    public async Task CheckForUpdateAsync()
    {
        if (updates is null || string.IsNullOrWhiteSpace(company?.UpdateFeed))
        {
            return;
        }

        update = await updates.CheckAsync(company.UpdateFeed);
        OnPropertyChanged(nameof(IsUpdateAvailable));
        OnPropertyChanged(nameof(UpdateText));
    }

    public bool IsUpdateAvailable => update is not null;
    public string UpdateText => update is null ? "" : $"Update to {update.Version}";
    public string? UpdateNotes => update?.Notes;

    /// <summary>Starts the newer version's installer. Null when it started (the app should then close).</summary>
    public async Task<string?> StartUpdateAsync() =>
        update is null || updates is null ? "No update is available." : await updates.StartInstallAsync(update);

    private static string TickKey(RowViewModel row) =>
        $"{row.Project.Id}|{row.Row.ItemType}|{row.Row.Folder?.FullPath ?? "item:" + row.Row.Item?.ItemId}";

    public void SelectAllReady()
    {
        foreach (var row in Rows.Where(row => row.Status == RowStatus.ReadyToReturn))
        {
            row.IsChecked = true;
        }
    }

    public void ClearChecks()
    {
        foreach (var row in allRows)
        {
            row.IsChecked = false;
        }
    }

    public TrackerSettings CloneSettings() =>
        JsonSerializer.Deserialize<TrackerSettings>(JsonSerializer.Serialize(Settings, TrackerJson.Options), TrackerJson.Options)
        ?? new TrackerSettings();

    public async Task ApplySettingsAsync(TrackerSettings updated)
    {
        // Settings doesn't edit the Procore section (which projects to sync, last look): keep the live one.
        updated.Procore = Settings.Procore;
        store.SaveSettings(updated);
        LoadProjects();
        await CompareAsync();
    }

    /// <summary>Saves the settings (projects included) in the background.</summary>
    public void SaveSettings() => store.SaveSettings(Settings);

    /// <summary>
    /// A type tab's label with how many items it would show under the current filters (person, due,
    /// discipline and the Open/All/… filter), so the counts match the list.
    /// </summary>
    private string TabText(string itemType)
    {
        var label = itemType == ItemTypeChoices.Both ? "Both" : ItemTypes.Plural(itemType);
        var count = allRows.Count(row => (itemType == ItemTypeChoices.Both
                || string.Equals(row.Row.ItemType, itemType, StringComparison.OrdinalIgnoreCase))
            && Matches(row));
        return count == 0 ? label : Filter == Filters.Open ? $"{label} ({count} open)" : $"{label} ({count})";
    }

    private void RebuildRows()
    {
        using var timing = AppLog.Time("list.rebuild", $"{allRows.Count} rows", 100);
        var visible = allRows
            .Where(MatchesType)
            .Where(Matches)
            .OrderBy(row => row, Comparer<RowViewModel>.Create(CompareRows))
            .ToList();
        Rows.ReplaceAll(visible);

        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasEmptyText));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(AllShownChecked));
        OnPropertyChanged(nameof(RfiTabText));
        OnPropertyChanged(nameof(SubmittalTabText));
        OnPropertyChanged(nameof(BothTabText));
        ScheduleViewStateSave();
        ScheduleBadges();
    }

    // ---- Counts next to each project ------------------------------------------------------

    /// <summary>A project's rows for counting, and what they were built from (to know when they're stale).</summary>
    private sealed record BadgeSource(
        ProviderSnapshot? Snapshot,
        TrackerProject Tracked,
        TypeFolderScan? RfiScan,
        TypeFolderScan? SubmittalScan,
        IReadOnlyList<RowViewModel> Rows,
        int SettingsVersion,
        DateTime Day);

    private void ScheduleBadges()
    {
        badgeTimer.Stop();
        badgeTimer.Start();
    }

    /// <summary>
    /// Sets each project's number to how many of its items pass the list's current filters. Shown
    /// projects use the rows on screen; others are compared in the background from saved data (never
    /// the drive) and cached until their items, settings or folder lists change. A project whose CA
    /// folder hasn't been listed yet gets its number once the background folder check has listed it.
    /// </summary>
    private async Task UpdateBadgesAsync()
    {
        var version = ++badgeVersion;
        var settingsAtStart = settingsVersion;
        var today = DateTime.Today;
        var settings = Settings;
        foreach (var project in selectedProjects)
        {
            badgeSources[project.Id] = new BadgeSource(project.Snapshot, project.Project, null, null,
                allRows.Where(row => row.Project == project).ToList(), settingsVersion, today);
        }

        var stale = new List<(ProjectViewModel View, TrackerProject Tracked, ProviderSnapshot? Snapshot, TypeFolderScan Rfi, TypeFolderScan Submittal)>();
        foreach (var project in Projects.Where(project => !selectedProjects.Contains(project)))
        {
            var layout = project.Project.EffectiveFolders(settings.Folders);
            var rfi = folderIndex.CachedOrEmpty(project.Project, ItemTypes.Rfi, layout);
            var submittal = folderIndex.CachedOrEmpty(project.Project, ItemTypes.Submittal, layout);
            if (rfi is null || submittal is null)
            {
                continue;
            }

            if (badgeSources.TryGetValue(project.Id, out var source)
                && source.SettingsVersion == settingsVersion
                && source.Day == today
                && ReferenceEquals(source.Snapshot, project.Snapshot)
                && ReferenceEquals(source.Tracked, project.Project)
                && (string.IsNullOrWhiteSpace(project.Project.CaFolder)
                    || (ReferenceEquals(source.RfiScan, rfi) && ReferenceEquals(source.SubmittalScan, submittal))))
            {
                continue;
            }

            stale.Add((project, project.Project, project.Snapshot, rfi, submittal));
        }

        if (stale.Count > 0)
        {
            List<(ProjectViewModel View, BadgeSource Source)> results;
            try
            {
                results = await Task.Run(() =>
                {
                    using var timing = AppLog.Time("badges", $"{stale.Count} project(s)", 500);
                    return stale.Select(input =>
                    {
                        var compared = input.Snapshot is null && string.IsNullOrWhiteSpace(input.Tracked.CaFolder)
                            ? null
                            : ProjectComparer.Compare(input.Snapshot, input.Tracked, settings,
                                (itemType, _) => itemType == ItemTypes.Submittal ? input.Submittal : input.Rfi);
                        var rows = compared is null
                            ? new List<RowViewModel>()
                            : compared.Types.Values.SelectMany(type => type.Rows).Select(row => new RowViewModel(row, input.View, compared, today)).ToList();
                        return (input.View, new BadgeSource(input.Snapshot, input.Tracked, input.Rfi, input.Submittal, rows, settingsAtStart, today));
                    }).ToList();
                });
            }
            catch (Exception error)
            {
                // Counts are a convenience; leave them as they were.
                AppLog.Error("badges", "Counting items failed", error);
                return;
            }

            if (version != badgeVersion)
            {
                return;
            }

            foreach (var (view, source) in results)
            {
                badgeSources[view.Id] = source;
            }
        }

        if (version != badgeVersion)
        {
            return;
        }

        foreach (var project in Projects)
        {
            if (!badgeSources.TryGetValue(project.Id, out var source))
            {
                continue;
            }

            var matching = source.Rows.Where(row => MatchesType(row) && Matches(row)).ToList();
            project.OverdueCount = matching.Count(row => row.IsOverdue);
            project.MatchingCount = matching.Count;
        }

        foreach (var gone in badgeSources.Keys.Where(id => Projects.All(project => project.Id != id)).ToList())
        {
            badgeSources.Remove(gone);
        }
    }

    private bool MatchesType(RowViewModel row) =>
        SelectedType == ItemTypeChoices.Both || string.Equals(row.Row.ItemType, SelectedType, StringComparison.OrdinalIgnoreCase);

    private bool Matches(RowViewModel row) =>
        MatchesDiscipline(row) && MatchesFilter(row) && MatchesPerson(row)
        && ItemTiming.MatchesDue(row.Row.Item, DueFilter, DueWithinDays, DateTime.Today);

    private bool MatchesPerson(RowViewModel row)
    {
        var chosen = CheckedPeople.ToList();
        if (chosen.Count == 0)
        {
            return true;
        }

        // Any ticked person (or the company) having the item is a match.
        return chosen.Any(choice => choice.IsCompany
            ? row.Holders.Any(holder => row.Comparison.Company.Matches(holder.CompanyId, holder.Company))
            : row.HolderNames.Contains(choice.Name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Section order first (project, then type, as grouped), then the chosen column. Empty values
    /// always go last; ties fall back to the item number.
    /// </summary>
    private int CompareRows(RowViewModel left, RowViewModel right)
    {
        if (groupBy is GroupModes.Project or GroupModes.ProjectAndType)
        {
            var byProject = string.Compare(left.ProjectName, right.ProjectName, StringComparison.CurrentCultureIgnoreCase);
            if (byProject != 0)
            {
                return byProject;
            }
        }

        if (groupBy is GroupModes.Type or GroupModes.ProjectAndType)
        {
            var byType = string.Compare(left.TypeLabel, right.TypeLabel, StringComparison.CurrentCultureIgnoreCase);
            if (byType != 0)
            {
                return byType;
            }
        }

        var leftEmpty = IsEmpty(left, sortColumn);
        var rightEmpty = IsEmpty(right, sortColumn);
        if (leftEmpty != rightEmpty)
        {
            return leftEmpty ? 1 : -1;
        }

        var result = CompareBy(left, right, sortColumn);
        if (sortDescending)
        {
            result = -result;
        }

        if (result != 0)
        {
            return result;
        }

        // Stable, readable ties: number, then project, type and folder.
        result = ItemNumber.CompareNatural(left.Number, right.Number);
        result = result != 0 ? result : string.Compare(left.ProjectName, right.ProjectName, StringComparison.CurrentCultureIgnoreCase);
        result = result != 0 ? result : string.Compare(left.TypeLabel, right.TypeLabel, StringComparison.CurrentCultureIgnoreCase);
        return result != 0 ? result : string.Compare(left.Row.Folder?.FullPath, right.Row.Folder?.FullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEmpty(RowViewModel row, string column) => column switch
    {
        nameof(RowViewModel.DaysInCourt) => row.DaysInCourt is null,
        nameof(RowViewModel.DaysTillDue) => row.DaysTillDue is null,
        nameof(RowViewModel.Number) => row.Row.Item is null,
        nameof(RowViewModel.PersonSortKey) => row.HolderNames.Count == 0,
        nameof(RowViewModel.Status) => false,
        _ => string.IsNullOrWhiteSpace(SortText(row, column))
    };

    private static int CompareBy(RowViewModel left, RowViewModel right, string column) => column switch
    {
        nameof(RowViewModel.Number) => ItemNumber.CompareNatural(left.Number, right.Number),
        nameof(RowViewModel.DaysInCourt) => Nullable.Compare(left.DaysInCourt, right.DaysInCourt),
        nameof(RowViewModel.DaysTillDue) => Nullable.Compare(left.DaysTillDue, right.DaysTillDue),
        nameof(RowViewModel.Status) => left.Status.CompareTo(right.Status),
        _ => string.Compare(SortText(left, column), SortText(right, column), StringComparison.CurrentCultureIgnoreCase)
    };

    private static string SortText(RowViewModel row, string column) => column switch
    {
        nameof(RowViewModel.ProjectName) => row.ProjectName,
        nameof(RowViewModel.TypeLabel) => row.TypeLabel,
        nameof(RowViewModel.Title) => row.Title,
        nameof(RowViewModel.ProviderStatus) => row.ProviderStatus,
        nameof(RowViewModel.PersonSortKey) or nameof(RowViewModel.HolderText) => row.PersonSortKey,
        nameof(RowViewModel.FolderName) => row.HasFolder ? row.FolderName : "",
        nameof(RowViewModel.DisciplineText) => row.Row.Discipline?.Label ?? "",
        nameof(RowViewModel.LocationText) => row.LocationText,
        _ => row.Number
    };

    /// <summary>Fills the Person filter from everyone holding an item in this project, keeping ticks.</summary>
    private void RebuildPersonOptions()
    {
        var choices = new List<PersonChoiceViewModel>();
        if (comparison is { Company.IsKnown: true } current)
        {
            choices.Add(new PersonChoiceViewModel($"Anyone at {current.Company.DisplayName}", isCompany: true));
        }

        choices.AddRange(allRows
            .SelectMany(row => row.HolderNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Select(name => new PersonChoiceViewModel(name, isCompany: false)));

        foreach (var old in PersonChoices)
        {
            old.PropertyChanged -= OnPersonChoiceChanged;
        }

        PersonChoices.Clear();
        foreach (var choice in choices)
        {
            choice.IsChecked = choice.IsCompany ? rememberCompany : rememberedPeople.Contains(choice.Name);
            choice.PropertyChanged += OnPersonChoiceChanged;
            PersonChoices.Add(choice);
        }

        OnPropertyChanged(nameof(PersonFilterText));
        OnPropertyChanged(nameof(HasPersonFilter));
        OnPropertyChanged(nameof(HasNoPersonChoices));
    }

    private bool MatchesDiscipline(RowViewModel row) => DisciplineFilter.Length == 0
        || (DisciplineFilter == Disciplines.NoneCode
            ? row.Row.Discipline is null
            : string.Equals(row.Row.Discipline?.Code, DisciplineFilter, StringComparison.OrdinalIgnoreCase));

    private bool MatchesFilter(RowViewModel row) => Filter switch
    {
        Filters.All => true,
        Filters.Open => row.Row.Item is { Closed: false },
        Filters.Ready => row.Status == RowStatus.ReadyToReturn,
        Filters.NoFolder => row.Status == RowStatus.NoFolder,
        Filters.Returned => row.Row.Folder?.Location == FolderLocation.Returned,
        _ => row.Row.NeedsAttention
    };

    private void OnHeaderChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(HasProject), nameof(HasCaFolder), nameof(IsConnected), nameof(IsNotConnected), nameof(IsRefreshing), nameof(CanRefresh),
                     nameof(ProjectTitle), nameof(CaFolderText), nameof(ConnectionText), nameof(RefreshButtonText),
                     nameof(DataTimestampText), nameof(FolderTimestampText), nameof(ProjectSubtitle), nameof(HasProjectSubtitle),
                     nameof(CompanyText), nameof(FolderWarning), nameof(HasFolderWarning), nameof(CanCreateTypeFolders),
                     nameof(IsPlaceholder), nameof(CanEditConnection), nameof(EditButtonText),
                     nameof(RfiTabText), nameof(SubmittalTabText), nameof(BothTabText), nameof(IsMultiProject), nameof(EmptyText), nameof(HasEmptyText), nameof(Comparison)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ---- Changes to the data --------------------------------------------------------------

    /// <summary>
    /// Called by the store on whatever thread made the change (a background folder check, a sync, a
    /// window). Notes what changed and handles it all together on the screen's thread shortly after.
    /// </summary>
    private void OnStoreChanged(object? sender, StoreChange change)
    {
        lock (changeGate)
        {
            switch (change.Kind)
            {
                case StoreChangeKind.Snapshot when change.ItemsChanged:
                    changedItemLists.Add($"{change.Provider}|{change.ProviderProjectId}");
                    break;
                case StoreChangeKind.Snapshot:
                    // Read again with no changes: only "updated …" times move on.
                    snapshotsTouched = true;
                    break;
                case StoreChangeKind.FolderScan when change.ProjectId is not null:
                    changedFolderIndexes.Add(change.ProjectId);
                    break;
                case StoreChangeKind.Settings:
                    settingsChanged = true;
                    break;
                case StoreChangeKind.Catalog:
                    catalogChanged = true;
                    break;
                case StoreChangeKind.Accounts:
                    accountsChanged = true;
                    break;
            }
        }

        storeDebounce.Dispatcher.InvokeAsync(() =>
        {
            storeDebounce.Stop();
            storeDebounce.Start();
        });
    }

    private void OnSaveFailed(string message) =>
        storeDebounce.Dispatcher.InvokeAsync(() =>
            StatusMessage = $"Couldn't save to the database ({message}). Your changes stay while the app is open; see the log (Settings).");

    private async Task OnStoreChangedAsync()
    {
        HashSet<string> itemLists;
        HashSet<string> folderIndexes;
        bool settings;
        bool catalog;
        bool accounts;
        bool touched;
        lock (changeGate)
        {
            itemLists = new HashSet<string>(changedItemLists, StringComparer.OrdinalIgnoreCase);
            folderIndexes = new HashSet<string>(changedFolderIndexes, StringComparer.Ordinal);
            settings = settingsChanged;
            catalog = catalogChanged;
            accounts = accountsChanged;
            touched = snapshotsTouched;
            changedItemLists.Clear();
            changedFolderIndexes.Clear();
            settingsChanged = catalogChanged = accountsChanged = snapshotsTouched = false;
        }

        if (settings)
        {
            // Rules may have changed in place (e.g. the returned rule): counts are worked out again.
            settingsVersion++;
        }

        // The tracker projects on screen before reloading: settings saved from elsewhere (such as discipline
        // choices from the company server) replace them, and then the list is compared again.
        var shownBefore = new Dictionary<string, TrackerProject>(StringComparer.Ordinal);
        foreach (var project in selectedProjects)
        {
            shownBefore[project.Id] = project.Project;
        }

        if (settings || catalog || touched || itemLists.Count > 0)
        {
            // Cheap now: everything comes from memory.
            LoadProjects();
        }
        else if (accounts)
        {
            RefreshAccounts();
        }

        var shownChanged = selectedProjects.Any(project =>
            folderIndexes.Contains(project.Id)
            || (shownBefore.TryGetValue(project.Id, out var before) && !ReferenceEquals(before, project.Project))
            || (project.Project.Connection is { } connection && !project.IsRefreshing
                && itemLists.Contains($"{connection.Provider}|{connection.ProjectId}")));
        if (shownChanged)
        {
            await CompareAsync();
        }
        else
        {
            ScheduleBadges();
        }
    }

    // ---- Remembering the view between sessions ---------------------------------------------

    /// <summary>Projects shown last time (the window restores them once the list is loaded).</summary>
    public IReadOnlyList<string> RememberedProjectIds => savedView.SelectedProjectIds;
    public string? RememberedPrimaryProjectId => savedView.PrimaryProjectId;

    private void ApplySavedView(ViewState view)
    {
        selectedType = view.ItemType is ItemTypeChoices.Both or ItemTypes.Rfi or ItemTypes.Submittal ? view.ItemType : ItemTypeChoices.Both;
        filter = string.IsNullOrWhiteSpace(view.Filter) ? Filters.Open : view.Filter;
        dueFilter = Enum.TryParse<DueFilter>(view.Due, out var due) ? due : DueFilter.Any;
        dueWithinDays = Math.Clamp(view.DueWithinDays, 0, 365);
        disciplineFilter = view.Discipline ?? "";
        groupBy = view.GroupBy is GroupModes.None or GroupModes.Project or GroupModes.Type or GroupModes.ProjectAndType
            ? view.GroupBy
            : GroupModes.ProjectAndType;
        sortColumn = string.IsNullOrWhiteSpace(view.SortColumn) ? nameof(RowViewModel.Number) : view.SortColumn;
        sortDescending = view.SortDescending;
        sortBy = view.SortPreset ?? "";
        archived.Clear();
        foreach (var key in view.ArchivedProjects ?? new List<string>())
        {
            archived.Add(key);
        }

        showArchived = view.ShowArchived;
        projectsPaneWidth = view.ProjectsPaneWidth is >= 200 and <= 700 ? view.ProjectsPaneWidth : 290;
        rememberedPeople.Clear();
        rememberedPeople.UnionWith(view.People);
        rememberCompany = view.AnyoneAtCompany;
    }

    /// <summary>Called by the window once it has restored the saved view; changes are saved from then on.</summary>
    public void ViewStateRestored() => viewStateReady = true;

    private void ScheduleViewStateSave()
    {
        if (!viewStateReady)
        {
            return;
        }

        viewStateTimer.Stop();
        viewStateTimer.Start();
    }

    private void SaveViewState()
    {
        if (!viewStateReady)
        {
            return;
        }

        store.SaveViewState(new ViewState
        {
            // Projects not yet tracked are remembered too; they're matched by id next time.
            SelectedProjectIds = selectedProjects.Select(project => project.Id).ToList(),
            PrimaryProjectId = SelectedProject?.Id,
            ItemType = selectedType,
            Filter = filter,
            People = rememberedPeople.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            AnyoneAtCompany = rememberCompany,
            Due = dueFilter.ToString(),
            DueWithinDays = dueWithinDays,
            Discipline = disciplineFilter,
            GroupBy = groupBy,
            SortColumn = sortColumn,
            SortDescending = sortDescending,
            SortPreset = sortBy,
            ArchivedProjects = archived.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList(),
            ShowArchived = showArchived,
            ProjectsPaneWidth = projectsPaneWidth
        });
    }

    public void Dispose()
    {
        store.Changed -= OnStoreChanged;
        store.SaveFailed -= OnSaveFailed;
        badgeTimer.Stop();
        viewStateTimer.Stop();
        SaveViewState();
        storeDebounce.Stop();
        ticker.Stop();
        folderTimer.Stop();
        autoSyncTimer.Stop();
        updateTimer?.Stop();
        centralTimer?.Stop();
    }
}
