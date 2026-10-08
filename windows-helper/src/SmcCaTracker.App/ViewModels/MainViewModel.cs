using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Operations;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.Updates;
using SmcCaTracker.Core.WorkloadTracker;

namespace SmcCaTracker.App.ViewModels;

/// <summary>How the item list is split into sections.</summary>
public static class GroupModes
{
    /// <summary>Overdue, due today, in the next 7 days, later, no due date, closed (the default).</summary>
    public const string Urgency = "urgency";
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

    /// <summary>Folder in Returned, but the item is back in your company's court.</summary>
    public const string Reopened = "reopened";

    /// <summary>More than one folder for an item, or a folder matching several items.</summary>
    public const string Duplicates = "duplicates";

    /// <summary>A folder with no Helix or Procore item.</summary>
    public const string NoItem = "noitem";

    /// <summary>The Filing view's tabs: everything but Open and All is folder housekeeping.</summary>
    public static bool IsFiling(string filter) => filter is not (Open or All);
}

/// <summary>
/// State for the main window. The app owns the projects: each is a CA folder, optionally
/// connected to a Helix or Procore project. All data lives in memory in the <see cref="TrackerStore"/>
/// (saved to the database in the background), CA folders are listed by the <see cref="FolderIndex"/>
/// in the background, and Helix is read by the <see cref="HelixSyncService"/> without windows — so
/// nothing on screen waits on the disk, the Egnyte drive or Helix. Reading and comparing never
/// changes a CA folder; every change goes through an OperationPlan that the window previews first.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
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
    private bool peopleDefaulted;
    private bool showItemDetails;
    private bool viewStateReady;
    private readonly Dictionary<string, ProjectComparison> comparisons = new(StringComparer.Ordinal);
    private List<RowViewModel> allRows = new();
    private string sortColumn = "Number";
    private bool sortDescending;
    private string groupBy = GroupModes.Urgency;
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
    private bool assignmentsChanged;
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

        // The sync pill follows each site's state, a background sync's included.
        foreach (var connection in Connections)
        {
            connection.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(ConnectionViewModel.State) or nameof(ConnectionViewModel.LastSyncedText))
                {
                    RaiseSyncPill();
                }
            };
        }
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
            await RunTimerAsync("badge", UpdateBadgesAsync);
        };

        // Changes to the data (a sync finished, folders changed, settings saved) are gathered for a
        // moment and handled together.
        storeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        storeDebounce.Tick += async (_, _) =>
        {
            storeDebounce.Stop();
            await RunTimerAsync("data change", OnStoreChangedAsync);
        };
        store.Changed += OnStoreChanged;
        store.SaveFailed += OnSaveFailed;

        refreshMessageHide.Tick += (_, _) =>
        {
            refreshMessageHide.Stop();
            if (!refreshMessageIsError && !IsRefreshing)
            {
                SetRefreshMessage("", false);
            }
        };

        syncProgressHide.Tick += (_, _) =>
        {
            syncProgressHide.Stop();
            if (syncRuns.Count == 0)
            {
                IsSyncProgressVisible = false;
            }
        };

        ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += async (_, _) => await RunTimerAsync("clock", OnTickAsync);
        ticker.Start();

        // CA folders: their dates are checked every 20 seconds and a folder is listed again only if
        // something in it changed.
        folderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        folderTimer.Tick += async (_, _) => await RunTimerAsync("folder check", CheckFoldersAsync);
        folderTimer.Start();

        // Helix: tracked projects are read in the background every few minutes (changes only).
        autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        autoSyncTimer.Tick += async (_, _) => await RunTimerAsync("automatic sync", AutoSyncAsync);
        autoSyncTimer.Start();
        lastAutoSync = DateTimeOffset.Now - TimeSpan.FromMinutes(Settings.Sync.IntervalMinutes) + TimeSpan.FromSeconds(20);

        LoadProjects();
        RefreshAccounts();
    }

    /// <summary>
    /// Runs a timer's work. An error escaping a timer's handler would close the app, so it is logged
    /// instead and the timer carries on next time.
    /// </summary>
    private static async Task RunTimerAsync(string name, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception error)
        {
            AppLog.Error("timer", $"The {name} timer failed", error);
        }
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
        if (signedIn && string.Equals(provider, Providers.Helix, StringComparison.OrdinalIgnoreCase))
        {
            // Signed in again: the hidden Helix page starts fresh and tracked projects sync now,
            // not at the next 15-minute round.
            helixSync.CloseBrowser(pause: TimeSpan.Zero);
            lastAutoSync = DateTimeOffset.MinValue;
        }

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
        procoreSync.CloseBrowser(pause: TimeSpan.FromSeconds(30));
        procoreSync.ResetPageReading();
    }

    /// <summary>
    /// Set by the main window: what is still using the browser profile (an open browser, download or
    /// sign-in window), or null when nothing is. Signing out is refused while something is.
    /// </summary>
    public Func<string?>? BrowserProfileInUse { get; set; }

    /// <summary>Raised before the browser's sign-ins are cleared: the item panel's page must be released.</summary>
    public event Action? BrowserProfileReleasing;

    /// <summary>
    /// Signs out of the built-in browser: stops everything that uses its profile, then clears its
    /// sign-ins (cookies, site storage, cache). Saved usernames and passwords are kept, so signing in
    /// again fills them in, unless <paramref name="forgetPasswords"/>. Returns the problem, or null
    /// when signed out.
    /// </summary>
    public async Task<string?> SignOutOfBrowserAsync(bool forgetPasswords = false)
    {
        if (BrowserProfileInUse?.Invoke() is { } inUse)
        {
            return $"Close {inUse} first, then sign out.";
        }

        // 1. No account to sync: background reads stop asking for the browser.
        Store.ClearAccounts();
        // 2. The item panel lets go of its page; 3. the hidden sync pages close and stay closed for a moment.
        BrowserProfileReleasing?.Invoke();
        CloseBackgroundBrowser();

        // 4. The sign-ins are cleared (cookies and site storage); saved usernames and passwords stay, so
        //    signing in again fills them in, unless they are to be forgotten too.
        var problem = await BrowserSignOut.ClearSignInsAsync(Paths, forgetPasswords);
        if (problem is not null)
        {
            helixSync.CloseBrowser(pause: TimeSpan.Zero);
            procoreSync.CloseBrowser(pause: TimeSpan.Zero);
            RefreshAccounts();
            return $"{problem} Close CA Tracker and start it again, then sign out.";
        }

        // Signed out: the hidden pages may open again (they start signed out and ask to sign in).
        helixSync.CloseBrowser(pause: TimeSpan.Zero);
        procoreSync.CloseBrowser(pause: TimeSpan.Zero);
        AppLog.Info("app", forgetPasswords
            ? "Signed out of the built-in browser and forgot its saved passwords."
            : "Signed out of the built-in browser (saved passwords kept).");
        RefreshAccounts();
        return null;
    }

    /// <summary>
    /// Signs the built-in browser out of one site (its card's Sign out button): its cookies and page
    /// storage are cleared; the other site's sign-in and every saved username and password stay.
    /// Returns the problem, or null when signed out.
    /// </summary>
    public async Task<string?> SignOutOfSiteAsync(string provider)
    {
        if (BrowserProfileInUse?.Invoke() is { } inUse)
        {
            return $"Close {inUse} first, then sign out.";
        }

        var label = Providers.Label(provider);
        var helix = !ProviderSites.IsProcore(provider);
        // No account to sync, then the pages using this site let go of it.
        Store.ClearAccount(provider);
        if (helix)
        {
            BrowserProfileReleasing?.Invoke();
            helixSync.CloseBrowser(pause: TimeSpan.FromSeconds(30));
        }
        else
        {
            procoreSync.CloseBrowser(pause: TimeSpan.FromSeconds(30));
            procoreSync.ResetPageReading();
        }

        var problem = await BrowserSignOut.ClearSiteAsync(Paths, provider);
        if (helix)
        {
            helixSync.CloseBrowser(pause: TimeSpan.Zero);
        }
        else
        {
            procoreSync.CloseBrowser(pause: TimeSpan.Zero);
        }

        AppLog.Info(provider, problem is null ? $"Signed out of {label} (saved passwords kept)." : $"Signing out of {label} failed: {problem}");
        RefreshAccounts();
        return problem;
    }

    /// <summary>Raised when a refresh should run in the app's built-in browser window.</summary>
    public event Action<RefreshRequest>? BrowserRefreshRequested;

    private readonly HashSet<string> signInRequested = new(StringComparer.OrdinalIgnoreCase);
    private bool signInBannerDismissed;

    /// <summary>
    /// A banner under the header while Helix or Procore needs you to sign in (its sign-in expired, or you
    /// signed out while its projects are still listed), so a sync doesn't just look like it failed.
    /// </summary>
    public bool ShowSignInBanner => !signInBannerDismissed && (Connections.Any(connection => connection.WantsSignIn) || WorkloadTrackerNeedsConnect);

    public string SignInBannerText
    {
        get
        {
            var sites = Connections.Where(connection => connection.WantsSignIn).ToList();
            var names = string.Join(" and ", sites.Select(site => site.Label));
            var text = sites.Count == 0 ? ""
                : sites.All(site => site.NeedsSignIn) ? $"Your {names} sign-in has expired. Sign in again to keep your projects up to date."
                : $"Sign in to {names} to keep your projects up to date.";
            return WorkloadTrackerNeedsConnect
                ? $"{text} Workload Tracker needs you to connect again to keep sending.".Trim()
                : text;
        }
    }

    /// <summary>Hides the banner until the next sign-in expires.</summary>
    public void DismissSignInBanner()
    {
        signInBannerDismissed = true;
        OnPropertyChanged(nameof(ShowSignInBanner));
    }

    // ---- Archive (yours only) -------------------------------------------------------------

    private readonly HashSet<string> archived = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> favorites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds projects to, or removes them from, your Favorites section (for you only).</summary>
    public void SetFavorite(IEnumerable<ProjectViewModel> projects, bool favorite)
    {
        var changed = false;
        foreach (var project in projects.ToList())
        {
            changed |= favorite ? favorites.Add(project.ArchiveKey) : favorites.Remove(project.ArchiveKey);
            project.IsFavorite = favorite;
        }

        if (changed)
        {
            ScheduleViewStateSave();
        }
    }
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
    public bool IsShownInList(ProjectViewModel project)
    {
        if (!showArchived && project.IsArchived)
        {
            return false;
        }

        var search = projectSearch.Trim();
        return search.Length == 0
            || project.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || project.Project.CompanyName?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    private string projectSearch = "";

    /// <summary>"Find a project": the list shows only projects whose name (or company) contains this.</summary>
    public string ProjectSearch
    {
        get => projectSearch;
        set
        {
            if (Set(ref projectSearch, value ?? ""))
            {
                ProjectFilterChanged?.Invoke();
                OnPropertyChanged(nameof(IsProjectSearchEmpty));
                OnPropertyChanged(nameof(HasNoSearchResults));
            }
        }
    }

    public bool IsProjectSearchEmpty => projectSearch.Length == 0;
    public bool HasNoSearchResults => projectSearch.Trim().Length > 0 && !Projects.Any(IsShownInList);

    private string itemSearch = "";
    private string[] itemSearchWords = [];

    /// <summary>
    /// The search box above the list: only items whose number, title or folder name contains each word typed.
    /// Just the list (and the RFIs / Submittals counts); the other counts don't change, and it isn't remembered.
    /// </summary>
    public string ItemSearch
    {
        get => itemSearch;
        set
        {
            if (Set(ref itemSearch, value ?? ""))
            {
                itemSearchWords = TextSearch.Words(itemSearch);
                OnPropertyChanged(nameof(HasItemSearch));
                RebuildRows();
            }
        }
    }

    public bool HasItemSearch => itemSearchWords.Length > 0;

    private bool MatchesSearch(RowViewModel row) =>
        itemSearchWords.Length == 0
        || TextSearch.Matches(itemSearchWords, row.Row.Item is null ? null : row.Number, row.Row.Item?.Title, row.Row.Folder?.Name);

    /// <summary>Nothing shown contains what was typed, but items the filters hide do: "Search all items" shows them.</summary>
    public bool HasHiddenSearchMatches => Rows.Count == 0 && HasItemSearch && allRows.Any(MatchesSearch);

    /// <summary>"Clear filters" under an empty list; while searching, the search's own buttons take its place.</summary>
    public bool CanClearFilters => HasActiveFilters && !HasItemSearch;

    /// <summary>"Search all items": every item of either type, everyone's, any due date and discipline.</summary>
    public void SearchAllItems()
    {
        ClearFilters();
        Filter = Filters.All;
        SelectedType = ItemTypeChoices.Both;
    }

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
    /// changes in Helix or Procore (this is kept with your view on this PC).
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
    /// People who have items now, for the Person filter: your company first, then names. Check any
    /// number; none checked means everyone.
    /// </summary>
    public ObservableCollection<PersonChoiceViewModel> PersonChoices { get; } = new();

    // The checks are remembered by name (and "anyone at your company"), not by the list's entries: the
    // list only has people holding something in the shown projects, and a check must keep filtering
    // when you switch to a project where that person holds nothing.
    public bool HasPersonFilter => activeWatchlist is not null || rememberCompany || rememberedPeople.Count > 0;

    /// <summary>Exactly one person checked: every row is theirs, so the Ball in court column is hidden.</summary>
    public bool IsOnePersonFilter => activeWatchlist is null && !rememberCompany && rememberedPeople.Count == 1;
    public bool HasNoPersonChoices => PersonChoices.Count == 0;

    /// <summary>What the Ball in court button shows: Everyone, one or two names, or a count.</summary>
    public string PersonFilterText
    {
        get
        {
            if (activeWatchlist is { } list)
            {
                return $"{list.Name} (watchlist)";
            }

            var names = rememberedPeople.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (rememberCompany)
            {
                names.Insert(0, PersonChoices.FirstOrDefault(choice => choice.IsCompany)?.Name ?? "Anyone at your company");
            }

            return names.Count switch
            {
                0 => "Everyone",
                1 or 2 => string.Join(", ", names),
                _ => $"{names.Count} people"
            };
        }
    }

    private bool suppressPersonEvents;

    /// <summary>The names you're signed in to Helix and Procore as (the same form as the ball-in-court names).</summary>
    private List<string> YourNames() => new[] { store.Account(Providers.Helix)?.UserName, store.Account(Providers.Procore)?.UserName }
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => PersonNames.WithoutCompany(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// The first time your name is known, Ball in court starts as you (unless someone is already
    /// checked). Only once, so choosing Everyone afterwards sticks.
    /// </summary>
    private void DefaultPeopleToYou()
    {
        if (peopleDefaulted || !viewStateReady)
        {
            return;
        }

        var names = YourNames();
        if (names.Count == 0)
        {
            return;
        }

        peopleDefaulted = true;
        // A watchlist being shown counts as a choice already made.
        var add = DefaultPeople.ToCheck(rememberedPeople, rememberCompany || activeWatchlist is not null, names);
        if (add.Count > 0)
        {
            AppLog.Info("view", $"Ball in court starts as you ({string.Join(", ", add)}).");
            rememberedPeople.UnionWith(add);
            RebuildPersonOptions();
            RebuildRows();
        }

        ScheduleViewStateSave();
    }

    // ---- Views across projects: My work, Overdue -------------------------------------------

    /// <summary>The projects the views look across: every project in your list linked to Helix or Procore, except archived ones.</summary>
    private List<ProjectViewModel> ViewProjects() => Projects.Where(project => project.IsConnected && !project.IsArchived).ToList();

    private bool ShowsEveryViewProject
    {
        get
        {
            var all = ViewProjects();
            return all.Count > 1 && all.Count == selectedProjects.Count && all.All(selectedProjects.Contains);
        }
    }

    /// <summary>My work is showing: every linked project, your open items, no other filter.</summary>
    public bool IsMyWorkActive => ShowsEveryViewProject && Filter == Filters.Open && DueFilter == DueFilter.Any
        && DisciplineFilter.Length == 0 && !rememberCompany && YourNames() is { Count: > 0 } you && rememberedPeople.SetEquals(you);

    /// <summary>Overdue, all projects is showing: every linked project, overdue items, whoever has them.</summary>
    public bool IsAllOverdueActive => ShowsEveryViewProject && Filter == Filters.Open && DueFilter == DueFilter.Overdue
        && DisciplineFilter.Length == 0 && !HasPersonFilter;

    /// <summary>My work needs your name, from the Helix or Procore sign-in.</summary>
    public bool CanShowMyWork => YourNames().Count > 0;

    public string MyWorkToolTip => CanShowMyWork
        ? Settings.WorkloadTracker is { IsPaired: true, PersonId: not null }
            ? "Your open items in every linked project (in your court, or assigned to you in Workload Tracker), by when they're due"
            : "Your open items in every linked project, by when they're due"
        : "Sign in to Helix or Procore (Settings → Connections) so CA Tracker knows your name";

    private int myWorkOpen;
    private int myWorkOverdue;
    private int allOverdue;

    public string MyWorkOpenText => $"{myWorkOpen} open";
    public bool HasMyWorkOpen => myWorkOpen > 0;
    public string MyWorkOverdueText => $"{myWorkOverdue} overdue";
    public bool HasMyWorkOverdue => myWorkOverdue > 0;
    public string AllOverdueText => allOverdue.ToString();
    public bool HasAllOverdue => allOverdue > 0;

    /// <summary>Shows your open items in every linked project.</summary>
    public void ShowMyWork()
    {
        var names = YourNames();
        if (names.Count == 0)
        {
            return;
        }

        LeaveWatchlist();
        rememberedPeople.Clear();
        rememberedPeople.UnionWith(names);
        rememberCompany = false;
        ShowView(DueFilter.Any);
    }

    /// <summary>Shows every overdue item in every linked project, whoever has it.</summary>
    public void ShowAllOverdue()
    {
        LeaveWatchlist();
        rememberedPeople.Clear();
        rememberCompany = false;
        ShowView(DueFilter.Overdue);
    }

    private void ShowView(DueFilter due)
    {
        var projects = ViewProjects();
        if (projects.Count == 0)
        {
            return;
        }

        // Set together, so the list is rebuilt once.
        filter = Filters.Open;
        dueFilter = due;
        disciplineFilter = "";
        foreach (var name in new[] { nameof(Filter), nameof(DueFilter), nameof(IsDueWithin), nameof(DisciplineFilter) })
        {
            OnPropertyChanged(name);
        }

        RebuildPersonOptions();
        SetSelectedProjects(projects, SelectedProject is { } current && projects.Contains(current) ? current : projects[0]);
        RebuildRows();
    }

    private void RaiseViews()
    {
        foreach (var name in new[]
                 {
                     nameof(IsMyWorkActive), nameof(IsAllOverdueActive), nameof(CanShowMyWork), nameof(MyWorkToolTip), nameof(HasActiveWatchlist),
                     nameof(ProjectTitle), nameof(ProjectSubtitle), nameof(HasProjectSubtitle)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>The views' counts, from every linked project's rows, counted as the list shows them.</summary>
    private void UpdateViewCounts()
    {
        var you = YourNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var open = ViewProjects()
            .SelectMany(project => badgeSources.TryGetValue(project.Id, out var source) ? source.Rows : new List<RowViewModel>())
            .Where(row => row.Row.Item is { Closed: false })
            .ToList();
        var mine = open.Where(row => row.HolderNames.Any(you.Contains) || row.IsAssignedToMe).ToList();
        myWorkOpen = mine.Count;
        myWorkOverdue = mine.Count(row => row.IsOverdue);
        allOverdue = open.Count(row => row.IsOverdue);
        foreach (var name in new[]
                 {
                     nameof(MyWorkOpenText), nameof(HasMyWorkOpen), nameof(MyWorkOverdueText), nameof(HasMyWorkOverdue),
                     nameof(AllOverdueText), nameof(HasAllOverdue)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ---- Watchlists: the people to follow on each project ------------------------------------

    private readonly List<Watchlist> watchlists = new();
    private Watchlist? activeWatchlist;

    /// <summary>The watchlists in the project list's VIEWS, A–Z.</summary>
    public ObservableCollection<WatchlistItemViewModel> WatchlistItems { get; } = new();
    public bool HasWatchlists => WatchlistItems.Count > 0;
    public bool HasNoWatchlists => WatchlistItems.Count == 0;
    public bool HasActiveWatchlist => activeWatchlist is not null;

    /// <summary>The key a watchlist knows a project by: its own, or its Helix/Procore project's (from before it had a CA folder).</summary>
    private static string? WatchKey(Watchlist list, ProjectViewModel project) =>
        list.People.ContainsKey(project.ArchiveKey) ? project.ArchiveKey
        : project.ProviderArchiveKey is { } provider && list.People.ContainsKey(provider) ? provider
        : null;

    /// <summary>Someone watched on the row's project holds it.</summary>
    private static bool Watched(Watchlist list, RowViewModel row) =>
        WatchKey(list, row.Project) is { } key && list.Matches(key, row.HolderNames);

    private List<ProjectViewModel> WatchlistProjects(Watchlist list) =>
        ViewProjects().Where(project => WatchKey(list, project) is not null).ToList();

    /// <summary>The watchlist is what the list shows: its projects, filtered by it.</summary>
    private bool IsWatchlistShowing(out Watchlist list)
    {
        list = activeWatchlist!;
        if (activeWatchlist is null)
        {
            return false;
        }

        var projects = WatchlistProjects(activeWatchlist);
        return projects.Count > 0 && projects.Count == selectedProjects.Count && projects.All(selectedProjects.Contains);
    }

    private void RebuildWatchlistItems()
    {
        WatchlistItems.Clear();
        foreach (var list in watchlists.OrderBy(list => list.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            WatchlistItems.Add(new WatchlistItemViewModel(list));
        }

        OnPropertyChanged(nameof(HasWatchlists));
        OnPropertyChanged(nameof(HasNoWatchlists));
        UpdateWatchlistCounts();
    }

    /// <summary>Each watchlist's counts: the open items its people hold on the projects they're watched on.</summary>
    private void UpdateWatchlistCounts()
    {
        foreach (var item in WatchlistItems)
        {
            var rows = WatchlistProjects(item.Watchlist)
                .SelectMany(project => badgeSources.TryGetValue(project.Id, out var source) ? source.Rows : new List<RowViewModel>())
                .Where(row => row.Row.Item is { Closed: false } && Watched(item.Watchlist, row))
                .ToList();
            item.SetCounts(rows.Count, rows.Count(row => row.IsOverdue));
            item.IsActive = ReferenceEquals(item.Watchlist, activeWatchlist);
        }
    }

    /// <summary>Shows a watchlist: its projects, and on each the open items held by the people watched there.</summary>
    public void ShowWatchlist(Watchlist list)
    {
        activeWatchlist = list;
        OnPropertyChanged(nameof(HasActiveWatchlist));
        rememberedPeople.Clear();
        rememberCompany = false;
        filter = Filters.Open;
        dueFilter = DueFilter.Any;
        disciplineFilter = "";
        foreach (var name in new[] { nameof(Filter), nameof(DueFilter), nameof(IsDueWithin), nameof(DisciplineFilter) })
        {
            OnPropertyChanged(name);
        }

        RebuildPersonOptions();
        var projects = WatchlistProjects(list);
        if (projects.Count > 0)
        {
            SetSelectedProjects(projects, SelectedProject is { } current && projects.Contains(current) ? current : projects[0]);
        }

        UpdateWatchlistCounts();
        RebuildRows();
        ScheduleViewStateSave();
    }

    /// <summary>People are being chosen another way: the list stops following the watchlist.</summary>
    private void LeaveWatchlist()
    {
        if (activeWatchlist is null)
        {
            return;
        }

        activeWatchlist = null;
        foreach (var item in WatchlistItems)
        {
            item.IsActive = false;
        }

        OnPropertyChanged(nameof(HasActiveWatchlist));
        UpdateColumnsSoon();
    }

    /// <summary>Raised so the window shows or hides the Ball in court column (it shows while a watchlist is followed).</summary>
    private void UpdateColumnsSoon() => OnPropertyChanged(nameof(PersonFilterText));

    /// <summary>Saves a watchlist made or edited in the watchlist window, and shows it.</summary>
    public void SaveWatchlist(Watchlist edited, Watchlist? original)
    {
        var list = edited.Normalized();
        list.Name = Watchlist.UniqueName(list.Name, watchlists.Select(other => other.Name), except: original?.Name);
        var index = original is null ? -1 : watchlists.IndexOf(original);
        if (index >= 0)
        {
            watchlists[index] = list;
        }
        else
        {
            watchlists.Add(list);
        }

        if (original is not null && ReferenceEquals(original, activeWatchlist))
        {
            activeWatchlist = list;
        }

        RebuildWatchlistItems();
        ShowWatchlist(list);
    }

    public void DeleteWatchlist(Watchlist list)
    {
        watchlists.Remove(list);
        if (ReferenceEquals(list, activeWatchlist))
        {
            activeWatchlist = null;
            OnPropertyChanged(nameof(HasActiveWatchlist));
            RebuildPersonOptions();
            RebuildRows();
        }

        RebuildWatchlistItems();
        ScheduleViewStateSave();
    }

    /// <summary>The watchlists, A–Z (for the team board's choice).</summary>
    public IReadOnlyList<Watchlist> Watchlists => watchlists.OrderBy(list => list.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>Raised when the counts are worked out again (after a sync, a folder change or a filter), so the team board can follow.</summary>
    public event Action? CountsUpdated;

    /// <summary>
    /// A watchlist as a grid: its people down the side, its projects across, and in each watched pair the open
    /// items that person holds there now (as the list counts them), with totals.
    /// </summary>
    public TeamBoard TeamBoardFor(Watchlist list)
    {
        var projects = WatchlistProjects(list);
        var items = projects.ToDictionary(project => project, project =>
            (badgeSources.TryGetValue(project.Id, out var source) ? source.Rows : new List<RowViewModel>())
                .Where(row => row.Row.Item is { Closed: false })
                .ToList());
        var people = list.EveryPerson().Select(name =>
        {
            var cells = projects.Select(project =>
            {
                var key = WatchKey(list, project)!;
                if (!list.People[key].Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    return null;
                }

                var held = items[project].Where(row => row.HolderNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(row => row.DaysTillDue ?? int.MaxValue)
                    .Select(row => new BoardItem(project.Name, row.TypeSingular, row.Number, row.Title, row.DueText, row.DaysTillDue, row.IsOverdue))
                    .ToList();
                return new BoardCell(held.Count, held.Count(item => item.IsOverdue), held);
            }).ToList();
            return new BoardPerson(name, cells, cells.Sum(cell => cell?.Open ?? 0), cells.Sum(cell => cell?.Overdue ?? 0));
        }).ToList();
        var columns = projects.Select((project, index) => new BoardProject(project.ArchiveKey, project.Name,
            people.Sum(person => person.Cells[index]?.Open ?? 0), people.Sum(person => person.Cells[index]?.Overdue ?? 0))).ToList();
        return new TeamBoard(list.Name, columns, people);
    }

    /// <summary>One person's items on one project (a team board cell): that project, that person, open items.</summary>
    public void ShowPersonOnProject(string projectKey, string name)
    {
        var project = Projects.FirstOrDefault(candidate => candidate.ArchiveKey == projectKey);
        if (project is null)
        {
            return;
        }

        LeaveWatchlist();
        rememberedPeople.Clear();
        rememberedPeople.Add(name);
        rememberCompany = false;
        filter = Filters.Open;
        dueFilter = DueFilter.Any;
        disciplineFilter = "";
        foreach (var property in new[] { nameof(Filter), nameof(DueFilter), nameof(IsDueWithin), nameof(DisciplineFilter) })
        {
            OnPropertyChanged(property);
        }

        RebuildPersonOptions();
        SetSelectedProjects(new[] { project }, project);
        RebuildRows();
    }

    /// <summary>The selected projects' watchlist keys, to start a new watchlist with.</summary>
    public IReadOnlyList<string> SelectedWatchKeys() =>
        selectedProjects.Where(project => project.IsConnected && !project.IsArchived).Select(project => project.ArchiveKey).ToList();

    /// <summary>
    /// What a watchlist can cover: every linked project with the people who hold or are next on its open items
    /// (current and upcoming holders), what each holds now, and whether they're with the project's company.
    /// </summary>
    public IReadOnlyList<WatchProject> WatchlistCandidates()
    {
        var result = new List<WatchProject>();
        foreach (var project in ViewProjects().OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var rows = badgeSources.TryGetValue(project.Id, out var source) ? source.Rows : new List<RowViewModel>();
            var company = rows.FirstOrDefault()?.Comparison.Company ?? CompanyIdentity.For(project.Project, project.Snapshot);
            var people = new Dictionary<string, (string? Company, bool Ours, int Open, int Overdue)>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Where(row => row.Row.Item is { Closed: false }))
            {
                foreach (var holder in row.Row.Item!.Holders.Where(holder => !string.IsNullOrWhiteSpace(holder.Name)
                             && (string.Equals(holder.State, "current", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(holder.State, "upcoming", StringComparison.OrdinalIgnoreCase))))
                {
                    var name = PersonNames.WithoutCompany(holder.Name);
                    var bracketed = PersonNames.CompanyIn(holder.Name);
                    var ours = company.Matches(holder.CompanyId, holder.Company) || company.Matches(null, bracketed);
                    var holds = string.Equals(holder.State, "current", StringComparison.OrdinalIgnoreCase);
                    people.TryGetValue(name, out var entry);
                    people[name] = (entry.Company ?? holder.Company ?? bracketed, entry.Ours || ours,
                        entry.Open + (holds ? 1 : 0), entry.Overdue + (holds && row.IsOverdue ? 1 : 0));
                }
            }

            result.Add(new WatchProject(project.ArchiveKey, project.ProviderArchiveKey == project.ArchiveKey ? null : project.ProviderArchiveKey,
                project.Name, company.IsKnown ? company.DisplayName : null,
                // Someone known to be with the project's company only by its id is shown with its name.
                people.Select(entry => new WatchPerson(entry.Key,
                    entry.Value.Company ?? (entry.Value.Ours && company.IsKnown ? company.DisplayName : null),
                    entry.Value.Ours, entry.Value.Open, entry.Value.Overdue)).ToList()));
        }

        return result;
    }

    /// <summary>"Just you" in the Ball in court list: only your own names checked.</summary>
    public bool CanShowOnlyYou => YourNames().Count > 0;

    public void ShowOnlyYou()
    {
        var names = YourNames();
        if (names.Count == 0)
        {
            return;
        }

        LeaveWatchlist();
        rememberedPeople.Clear();
        rememberedPeople.UnionWith(names);
        rememberCompany = false;
        RebuildPersonOptions();
        RebuildRows();
    }

    /// <summary>Back to the default view: open items, everyone, any date, every discipline.</summary>
    public void ClearFilters()
    {
        Filter = Filters.Open;
        DueFilter = DueFilter.Any;
        DisciplineFilter = "";
        ClearPeople();
    }

    public void ClearPeople()
    {
        LeaveWatchlist();
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

        // Choosing people by hand replaces the watchlist (its people aren't checked in the list).
        LeaveWatchlist();
        if (sender is PersonChoiceViewModel choice)
        {
            // Remembered by name, so a check survives switching projects and restarting.
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
                else
                {
                    UpdateDueCounts();
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
                OnPropertyChanged(nameof(IsFiling));
                OnPropertyChanged(nameof(IsNotFiling));
                RebuildRows();
            }
        }
    }

    // ---- Filing: folder housekeeping, apart from the everyday list --------------------------

    /// <summary>The list shows one of the Filing tabs (folder housekeeping) instead of open or all items.</summary>
    public bool IsFiling => Filters.IsFiling(filter);
    public bool IsNotFiling => !IsFiling;

    private Dictionary<string, int> filingCounts = new();

    /// <summary>How many rows each Filing tab has (the type and discipline filters apply; who has it and when it's due don't).</summary>
    private void UpdateFilingCounts()
    {
        var rows = allRows.Where(MatchesType).Where(MatchesDiscipline).ToList();
        filingCounts = new Dictionary<string, int>
        {
            [Filters.Attention] = rows.Count(row => row.Row.NeedsAttention),
            [Filters.Ready] = rows.Count(row => row.Status == RowStatus.ReadyToReturn),
            [Filters.Reopened] = rows.Count(row => row.Status == RowStatus.Reopened),
            [Filters.Duplicates] = rows.Count(row => row.Status is RowStatus.Duplicate or RowStatus.Ambiguous),
            [Filters.NoItem] = rows.Count(row => row.Status == RowStatus.NotInProvider),
            [Filters.NoFolder] = rows.Count(row => row.Status == RowStatus.NoFolder),
            [Filters.Returned] = rows.Count(row => row.Row.Folder?.Location == FolderLocation.Returned)
        };
        foreach (var name in new[]
                 {
                     nameof(FilingButtonText), nameof(FilingButtonToolTip), nameof(FilingAttentionText), nameof(FilingReadyText),
                     nameof(FilingReopenedText), nameof(FilingDuplicatesText), nameof(FilingNoItemText), nameof(FilingNoFolderText),
                     nameof(FilingReturnedText), nameof(IsFilingAttention), nameof(IsFilingReady), nameof(IsFilingReopened),
                     nameof(IsFilingDuplicates), nameof(IsFilingNoItem), nameof(IsFilingNoFolder), nameof(IsFilingReturned), nameof(ShowDueChips)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private int FilingCount(string tab) => filingCounts.TryGetValue(tab, out var count) ? count : 0;

    /// <summary>The header's Filing button: how many folders need a look, or the way back to the items.</summary>
    public string FilingButtonText => IsFiling ? "← Back to items"
        : FilingCount(Filters.Attention) > 0 ? $"Filing · {FilingCount(Filters.Attention)}"
        : "Filing";

    public string FilingButtonToolTip => IsFiling
        ? "Back to the open items"
        : "Folder housekeeping: folders ready to move to Returned, back with you, with two folders or no item, and items with no folder";

    public string FilingAttentionText => $"To look at ({FilingCount(Filters.Attention)})";
    public string FilingReadyText => $"Ready to return ({FilingCount(Filters.Ready)})";
    public string FilingReopenedText => $"Back with you ({FilingCount(Filters.Reopened)})";
    public string FilingDuplicatesText => $"Two folders ({FilingCount(Filters.Duplicates)})";
    public string FilingNoItemText => $"No item ({FilingCount(Filters.NoItem)})";
    public string FilingNoFolderText => $"No folder yet ({FilingCount(Filters.NoFolder)})";
    public string FilingReturnedText => $"In Returned ({FilingCount(Filters.Returned)})";
    public bool IsFilingAttention => filter == Filters.Attention;
    public bool IsFilingReady => filter == Filters.Ready;
    public bool IsFilingReopened => filter == Filters.Reopened;
    public bool IsFilingDuplicates => filter == Filters.Duplicates;
    public bool IsFilingNoItem => filter == Filters.NoItem;
    public bool IsFilingNoFolder => filter == Filters.NoFolder;
    public bool IsFilingReturned => filter == Filters.Returned;

    /// <summary>The due counts are about the items, so they hide while filing.</summary>
    public bool ShowDueChips => HasDueChips && !IsFiling;

    /// <summary>The Filing button: into Filing (its first tab with something in it), or back to the open items.</summary>
    public void ToggleFiling() =>
        Filter = IsFiling ? Filters.Open
            : FilingCount(Filters.Attention) > 0 || FilingCount(Filters.NoFolder) == 0 ? Filters.Attention
            : Filters.NoFolder;

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
                // Back to detecting: kept as an empty choice (not removed).
                edited.Disciplines[key] = code ?? "";
            }

            await UpdateProjectAsync(edited);
        }

        // Workload Tracker's lists show the discipline too: send it now rather than at the next round.
        SendToWorkloadTrackerSoon();
    }

    public bool IsBusy
    {
        get => isBusy;
        set
        {
            if (Set(ref isBusy, value))
            {
                OnPropertyChanged(nameof(HasFooter));
            }
        }
    }

    public string StatusMessage
    {
        get => statusMessage;
        set
        {
            if (Set(ref statusMessage, value ?? ""))
            {
                OnPropertyChanged(nameof(HasFooter));
            }
        }
    }

    /// <summary>The footer shows only while there is a message or work going on.</summary>
    public bool HasFooter => statusMessage.Length > 0 || isBusy;

    /// <summary>Helix and Procore, for the Connections settings page, the sign-in banner and Sync all.</summary>
    public ObservableCollection<ConnectionViewModel> Connections { get; } = new(
        Providers.All.Select(provider => new ConnectionViewModel(provider)));

    public bool IsAnySyncing => Connections.Any(connection => connection.IsSyncing);

    // ---- The sync pill: how syncing stands, in one place ------------------------------------

    /// <summary>For the pill's mark and colour: syncing, signin, failed, synced or idle.</summary>
    public string SyncPillState =>
        Connections.Any(connection => connection.IsBusy) ? "syncing"
        : Connections.Any(connection => connection.WantsSignIn && connection.NeedsSignIn) ? "signin"
        : Connections.Any(connection => connection.IsSignedIn && connection.FailedCount > 0) ? "failed"
        : Connections.Any(connection => connection.HasSynced) ? "synced"
        : "idle";

    public string SyncPillText => SyncPillState switch
    {
        "syncing" => "Syncing…",
        "signin" => "Sign-in needed",
        "failed" => Connections.Sum(connection => connection.FailedCount) is var failed && failed == 1 ? "1 not read" : $"{Connections.Sum(connection => connection.FailedCount)} not read",
        "synced" => LastSyncedAt is { } at ? $"Synced {(at.ToLocalTime().Date == DateTime.Today ? at.ToLocalTime().ToString("h:mm tt") : at.ToLocalTime().ToString("MMM d"))}" : "Synced",
        _ => "Not synced yet"
    };

    /// <summary>Each site's state, and what the pill's menu does.</summary>
    public string SyncPillToolTip => string.Join(Environment.NewLine,
        Connections.Select(connection => $"{connection.Label}: {connection.StateText}{(connection.LastSyncedText.Length > 0 ? $" · {connection.LastSyncedText}" : "")}")
            .Append("Helix and Procore are synced in the background. Click to sync now, sync one site, or open Connections."));

    /// <summary>The pill menu's first item: sync everything now, or stop the sync that is running.</summary>
    public string SyncNowText => IsAnySyncing ? "Stop syncing" : "Sync all now";

    /// <summary>When a project's items were last read, the latest of all.</summary>
    private DateTimeOffset? LastSyncedAt => Projects.Select(project => project.Snapshot?.ScannedAt).Where(at => at is not null).Max();

    private void RaiseSyncPill()
    {
        foreach (var name in new[] { nameof(SyncPillState), nameof(SyncPillText), nameof(SyncPillToolTip), nameof(SyncNowText) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>The header's button: Sync all, or Stop while a sync it started runs.</summary>
    public string SyncAllText => IsAnySyncing ? "Stop syncing" : "Sync all";

    /// <summary>
    /// Syncs every signed-in site, one after another (so only one browser window is ever open).
    /// False when no site is signed in, so the window can open Connections instead.
    /// </summary>
    public async Task<bool> SyncAllAsync()
    {
        var ready = Connections.Where(connection => connection.CanSync).ToList();
        foreach (var connection in ready)
        {
            await SyncAsync(connection.Provider);
        }

        return ready.Count > 0;
    }

    /// <summary>Stop: each running sync finishes its current project and skips the rest.</summary>
    public void StopSyncs()
    {
        foreach (var connection in Connections.Where(connection => connection.IsSyncing))
        {
            connection.StopRequested = true;
            connection.SyncMessage = "Stopping after the current project…";
        }
    }


    public ConnectionViewModel ConnectionFor(string provider) =>
        Connections.First(connection => string.Equals(connection.Provider, provider, StringComparison.OrdinalIgnoreCase));

    /// <summary>Updates each connection's sign-in, last sync and project count.</summary>
    public void RefreshAccounts()
    {
        foreach (var connection in Connections)
        {
            var account = store.Account(connection.Provider);
            connection.IsSignedIn = account is { NeedsSignIn: false };
            connection.NeedsSignIn = account is { NeedsSignIn: true };
            // The badge says the state; this line says who, or what to do.
            connection.StatusText = account is null
                ? $"Sign in once and CA Tracker keeps your {connection.Label} projects up to date in the background."
                : account.NeedsSignIn
                    ? "Your sign-in has expired. Sign in again to keep syncing."
                : string.IsNullOrWhiteSpace(account.UserName)
                    ? $"Signed in {Formatting.When(account.SignedInAt)}"
                    : $"Signed in as {account.UserName}{(string.IsNullOrWhiteSpace(account.Company) ? "" : $" · {account.Company}")}";

            var projects = ProjectsFor(connection.Provider, includeUntracked: true);
            var tracked = projects.Count(project => !project.IsPlaceholder);
            var skipped = projects.Count(project => IsSyncSkipped(project) || project.IsArchived);
            connection.ProjectCount = projects.Count;
            connection.ProjectsText = projects.Count switch
            {
                0 when ProviderSites.IsProcore(connection.Provider) => "No Procore projects found yet. Sign in to Procore and they are found by themselves.",
                0 => $"No {connection.Label} projects listed yet",
                1 => $"1 project ({tracked} with a CA folder)",
                _ => $"{projects.Count} projects ({tracked} with a CA folder)"
            } + (skipped > 0 ? $" · {skipped} archived or with sync turned off" : "");
            var lastSynced = projects
                .Select(project => project.Snapshot?.ScannedAt)
                .Where(value => value is not null)
                .Max();
            connection.LastSyncedText = lastSynced is null
                ? "Never synced"
                : $"Last synced {Formatting.When(lastSynced.Value)}";
            connection.HasSynced = lastSynced is not null;

            var failed = projects
                .Where(project => project.LastReadFailed && !IsSyncSkipped(project) && !project.IsArchived)
                .Select(project => project.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            connection.FailedCount = failed.Count;
            connection.FailedText = failed.Count == 0
                ? ""
                : $"⚠ Couldn't be read last time: {string.Join(", ", failed.Take(5))}" +
                  (failed.Count > 5 ? $" and {failed.Count - 5} more" : "") +
                  ". Hover one in the project list to see why.";
        }

        // Helix or Procore needs you to sign in again: the banner says so. A new expiry brings back a
        // banner you dismissed; signing in clears it.
        foreach (var provider in Providers.All)
        {
            var needsSignIn = store.Account(provider) is { NeedsSignIn: true };
            if (needsSignIn && signInRequested.Add(provider))
            {
                AppLog.Info(provider, $"{Providers.Label(provider)} needs a new sign-in.");
                signInBannerDismissed = false;
            }
            else if (!needsSignIn)
            {
                signInRequested.Remove(provider);
            }
        }

        OnPropertyChanged(nameof(ShowSignInBanner));
        OnPropertyChanged(nameof(SignInBannerText));
        OnPropertyChanged(nameof(CanSignOutOfBrowser));
        OnPropertyChanged(nameof(IsSignedOutOfBrowser));
        OnPropertyChanged(nameof(CanShowOnlyYou));
        OnPropertyChanged(nameof(CanShowMyWork));
        OnPropertyChanged(nameof(MyWorkToolTip));
        RaiseSyncPill();
        DefaultPeopleToYou();
    }

    /// <summary>Signed in to Helix or Procore (or the sign-in has expired): the sign-out buttons apply.</summary>
    public bool CanSignOutOfBrowser => Connections.Any(connection => connection.IsSignedIn || connection.NeedsSignIn);
    public bool IsSignedOutOfBrowser => !CanSignOutOfBrowser;

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
        OnPropertyChanged(nameof(SyncAllText));
        RaiseSyncPill();
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
                    : found ?? $"No {connection.Label} projects yet. Sign in to {connection.Label} in Settings → Connections to list them.";
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
            OnPropertyChanged(nameof(SyncAllText));
            RefreshAccounts();
            RaiseSyncPill();
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
                store.RecordAccount(Providers.Procore, discovery.UserName, null, now, discovery.UserId, discovery.UserEmail);
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

    /// <summary>
    /// Syncs the project linked to this Helix project: after an item was worked on in Helix's new app, where the
    /// item panel can't see changes as they're made.
    /// </summary>
    public Task SyncHelixProjectAsync(string helixProjectId)
    {
        var project = Projects.FirstOrDefault(candidate => IsHelix(candidate)
            && string.Equals(candidate.Project.Connection?.ProjectId, helixProjectId, StringComparison.Ordinal));
        if (project is null)
        {
            return Task.CompletedTask;
        }

        AppLog.Info("panel", $"Syncing {project.Name}: an item was open in Helix's new app.");
        return StartRefreshAsync(project);
    }

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
        var label = projects.Count > 0 ? projects[0].ProviderLabel : "";
        var ended = new HashSet<ProjectViewModel>();

        // The project list shows each project's turn: waiting, syncing now, then a check when it's done.
        foreach (var project in projects)
        {
            project.IsQueued = true;
        }

        OnHeaderChanged();

        void Finish(ProjectViewModel project)
        {
            project.EndSync();
            project.IsQueued = false;
            if (project.Project.Connection is { } connection)
            {
                project.LastRun = store.LastSyncRun(connection.Provider, connection.ProjectId);
            }

            project.MarkSynced();
        }

        // Settings → Connections shows the site reading too, whichever way the sync started.
        var provider = projects.Count > 0 ? projects[0].Project.Connection!.Provider : null;
        var site = Connections.FirstOrDefault(connection => string.Equals(connection.Provider, provider, StringComparison.OrdinalIgnoreCase));
        site?.BeginBusy();
        ProviderSyncResult? result = null;

        var run = BeginSyncProgress(label, groups.Count);
        OnHeaderChanged();
        try
        {
            result = await sync(
                groups.Select(group => toTarget(group[0])).ToList(),
                (index, _) =>
                {
                    if (index > 0)
                    {
                        foreach (var done in groups[index - 1].Where(ended.Add))
                        {
                            Finish(done);
                        }
                    }

                    foreach (var current in groups[index])
                    {
                        current.IsQueued = false;
                        current.BeginSync();
                    }

                    UpdateSyncProgress(run, index, groups[index][0].Name);
                    if (site is not null)
                    {
                        site.Progress = 100.0 * index / groups.Count;
                        site.SyncMessage = groups.Count == 1
                            ? $"Syncing {groups[index][0].Name}…"
                            : $"Syncing {groups[index][0].Name} ({index + 1} of {groups.Count})…";
                    }

                    starting?.Invoke(index, groups[index][0]);
                    // The header shows the selected project syncing (and the one before it done).
                    OnHeaderChanged();
                });
            return result;
        }
        finally
        {
            foreach (var project in projects.Where(ended.Add))
            {
                if (project.IsSyncing)
                {
                    Finish(project);
                }
                else
                {
                    // Never reached (stopped, or the site needs a sign-in): no check mark.
                    project.IsQueued = false;
                    if (project.Project.Connection is { } connection)
                    {
                        project.LastRun = store.LastSyncRun(connection.Provider, connection.ProjectId);
                    }
                }
            }

            EndSyncProgress(run, projects, result?.NeedsSignIn == true);
            if (site is not null)
            {
                site.EndBusy();
                if (!site.IsBusy)
                {
                    // A Refresh from the project list: the badge shows how it went. (Sync and the
                    // background sync write their own summary after this.)
                    site.SyncMessage = result?.Problem ?? "";
                }
            }

            RefreshAccounts();
            OnHeaderChanged();
        }
    }

    // ---- Sync progress on the projects page -----------------------------------------------

    private sealed class SyncProgressRun
    {
        public string Label = "";
        public int Total;
        public int Index;
    }

    private readonly List<SyncProgressRun> syncRuns = new();
    private string syncProgressText = "";
    private double syncProgressValue;
    private bool isSyncProgressVisible;
    private bool syncProgressRunning;
    private readonly DispatcherTimer syncProgressHide = new() { Interval = TimeSpan.FromSeconds(20) };

    /// <summary>What is syncing now ("Syncing Procore: Lubbock (3 of 9)"), or how the last sync ended.</summary>
    public string SyncProgressText
    {
        get => syncProgressText;
        private set => Set(ref syncProgressText, value);
    }

    /// <summary>0–100 across the projects in the sync that is running.</summary>
    public double SyncProgressValue
    {
        get => syncProgressValue;
        private set => Set(ref syncProgressValue, value);
    }

    public bool IsSyncProgressVisible
    {
        get => isSyncProgressVisible;
        private set => Set(ref isSyncProgressVisible, value);
    }

    public bool IsSyncProgressRunning
    {
        get => syncProgressRunning;
        private set => Set(ref syncProgressRunning, value);
    }

    private SyncProgressRun BeginSyncProgress(string label, int total)
    {
        var run = new SyncProgressRun { Label = label, Total = Math.Max(1, total) };
        syncRuns.Add(run);
        syncProgressHide.Stop();
        IsSyncProgressVisible = true;
        IsSyncProgressRunning = true;
        SyncProgressValue = 0;
        SyncProgressText = $"Syncing {label}: starting…";
        return run;
    }

    private void UpdateSyncProgress(SyncProgressRun run, int index, string name)
    {
        run.Index = index;
        if (syncRuns.LastOrDefault() != run)
        {
            return;
        }

        SyncProgressValue = 100.0 * index / run.Total;
        SyncProgressText = run.Total == 1
            ? $"Syncing {run.Label}: {name}…"
            : $"Syncing {run.Label}: {name} ({index + 1} of {run.Total})…";
    }

    private void EndSyncProgress(SyncProgressRun run, IReadOnlyList<ProjectViewModel> projects, bool needsSignIn = false)
    {
        syncRuns.Remove(run);
        if (syncRuns.Count > 0)
        {
            var other = syncRuns[^1];
            SyncProgressValue = 100.0 * other.Index / other.Total;
            SyncProgressText = $"Syncing {other.Label}…";
            return;
        }

        var failed = projects.Count(project => project.LastReadFailed);
        var done = projects.Count(project => project.JustSynced && !project.LastReadFailed);
        IsSyncProgressRunning = false;
        SyncProgressValue = 100;
        SyncProgressText = needsSignIn && done == 0
            ? $"{run.Label} needs you to sign in, so nothing was synced."
            : $"{run.Label} synced {DateTimeOffset.Now.ToLocalTime():h:mm tt}: {done} of {projects.Count} project{(projects.Count == 1 ? "" : "s")} updated" +
              (failed > 0 ? $", {failed} couldn't be read (marked ⚠)." : ".");
        syncProgressHide.Stop();
        syncProgressHide.Start();
    }

    private Task<ProviderSyncResult> SyncHelixAsync(
        IReadOnlyList<ProjectViewModel> projects,
        Action<int, ProjectViewModel>? starting,
        Func<bool>? stop,
        bool full = false,
        HeaderRun? run = null) =>
        SyncInBackgroundAsync(projects, HelixTarget, (targets, next) => helixSync.SyncAsync(targets, next, stop, full, run), starting);

    private Task<ProviderSyncResult> SyncProcoreAsync(
        IReadOnlyList<ProjectViewModel> projects,
        Action<int, ProjectViewModel>? starting,
        Func<bool>? stop,
        HeaderRun? run = null) =>
        SyncInBackgroundAsync(projects, ProcoreTarget, (targets, next) => procoreSync.SyncAsync(targets, next, stop, run), starting);

    /// <summary>A sync started with the header's Sync button: its Cancel stops this run and no other.</summary>
    private sealed class HeaderRun
    {
        public bool Stopped;
    }

    // Header syncs running now, by project (a multi-project sync uses manyRun).
    private readonly Dictionary<ProjectViewModel, HeaderRun> headerRuns = new();
    private HeaderRun? manyRun;

    /// <summary>
    /// Refreshes projects: Helix ones in the background, all on one hidden page; Procore ones one after
    /// another in the browser window, waiting for each to finish.
    /// </summary>
    private async Task<SequenceResult> RefreshSequentiallyAsync(
        IReadOnlyList<ProjectViewModel> targets,
        Action<int, ProjectViewModel> starting,
        Func<bool> stop,
        HeaderRun? run = null)
    {
        var done = 0;
        var failed = new List<string>();
        var helix = targets.Where(IsHelix).ToList();
        if (helix.Count > 0)
        {
            var result = await SyncHelixAsync(helix, (index, project) => starting(index, project), stop, run: run);
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
            var result = await SyncProcoreAsync(others, (index, project) => starting(helix.Count + index, project), stop, run);
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
    /// <summary>The header's project is syncing, waiting its turn, or starting a sync from the header.</summary>
    public bool IsRefreshing => refreshingMany || SelectedProject is { } project
        && (project.IsRefreshing || project.IsQueued || headerRuns.ContainsKey(project));

    /// <summary>Cancel shows only for a sync started here (a background sync finishes by itself).</summary>
    public bool CanCancelRefresh => refreshingMany || SelectedProject is { } project
        && (headerRuns.ContainsKey(project) || project.ActiveRefresh is { IsFinished: false });
    public bool CanRefresh => HasProject && !IsRefreshing && !refreshStarting;
    public string ProjectTitle => IsWatchlistShowing(out var shownList) ? shownList.Name
        : IsMyWorkActive ? "My work"
        : IsAllOverdueActive ? "Overdue, all projects"
        : IsMultiProject ? $"{selectedProjects.Count} projects selected"
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
    public string RefreshButtonText => IsRefreshing ? "Syncing…"
        : IsMultiProject ? $"Sync {selectedProjects.Count} projects"
        : IsConnected ? $"Sync from {SelectedProject!.ProviderLabel}" : "Rescan CA folder";

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
                return $"{label}: the last sync {Formatting.When(run.StartedAt)} failed — {run.Error}" +
                    (snapshot is null ? "" : $" · showing items synced {Formatting.When(snapshot.ScannedAt)}");
            }

            if (snapshot is null)
            {
                return $"{label} data: never synced";
            }

            var types = ItemTypes.All
                .Select(type => (type, coverage: snapshot.CoverageFor(type)))
                .Where(entry => entry.coverage is not null)
                .ToList();
            var distinctTimes = types.Select(entry => entry.coverage!.ScannedAt).Distinct().Count();
            return distinctTimes > 1
                ? $"{label} data: " + string.Join(" · ", types.Select(entry => $"{ItemTypes.Plural(entry.type)} {Formatting.When(entry.coverage!.ScannedAt)}"))
                : $"{label} data synced {Formatting.When(snapshot.ScannedAt)}";
        }
    }

    public bool LastSyncFailed => SelectedProject is { LastReadFailed: true } && IsConnected;

    /// <summary>When the items and the CA folder were last read, on one line above the list.</summary>
    public string TimestampText => string.Join("  ·  ", new[] { DataTimestampText, FolderTimestampText }.Where(text => text.Length > 0));

    public string FolderTimestampText => comparison is null || !HasCaFolder
        ? ""
        : $"CA folder rescanned {Formatting.When(comparison.ComparedAt)}";

    public string RefreshMessage => refreshMessage;
    public bool HasRefreshMessage => refreshMessage.Length > 0;
    public bool RefreshMessageIsError => refreshMessageIsError;

    public string ProjectSubtitle
    {
        get
        {
            if (IsMyWorkActive)
            {
                return $"Your open items in all {selectedProjects.Count} linked projects. Select a project on the left to see just that one.";
            }

            if (IsWatchlistShowing(out var list))
            {
                var people = list.EveryPerson().Count;
                return $"Watchlist: {people} {(people == 1 ? "person" : "people")} on {selectedProjects.Count} projects. Each person's items show only on the projects they're watched on.";
            }

            if (IsAllOverdueActive)
            {
                return $"Overdue items in all {selectedProjects.Count} linked projects, whoever has them.";
            }

            if (IsMultiProject)
            {
                return $"Showing items from the {selectedProjects.Count} selected projects. Select just one to see its CA folder and link, or to edit it.";
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
                return "Returned: closed items only until your company is known (sync, or choose it in Project settings).";
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

    /// <summary>Problems with the CA folder setup (missing type folders, folders hidden by the BB/TFO split).</summary>
    private List<string> SetupProblems()
    {
        if (comparison is null || !HasCaFolder)
        {
            return new List<string>();
        }

        var problems = comparison.Types.Values
            .Select(type => type.Scan.Error ?? (type.Scan.TypeFolderExists ? null : $"No \"{Path.GetFileName(type.Scan.TypeFolderPath)}\" folder in this CA folder."))
            .Where(message => message is not null)
            .Distinct()
            .ToList();
        var hidden = HiddenByPhaseCount;
        if (hidden > 0)
        {
            problems.Add($"{hidden} folder{(hidden == 1 ? " is" : "s are")} hidden because {(hidden == 1 ? "its name says" : "their names say")} neither BB nor TFO.");
        }

        return problems.OfType<string>().ToList();
    }

    public string FolderWarning => string.Join("  ", SetupProblems());
    public bool HasFolderWarning => FolderWarning.Length > 0;

    /// <summary>The header's setup chip, "⚠ 1 setup issue" (one project at a time: the problems are its own).</summary>
    public bool HasSetupChip => HasFolderWarning && !IsMultiProject;
    public string SetupIssuesText => SetupProblems().Count == 1 ? "⚠ 1 setup issue" : $"⚠ {SetupProblems().Count} setup issues";

    /// <summary>The CA folder and link (Details) describe one project, so they hide when several are shown.</summary>
    public bool CanShowProjectDetails => HasProject && !IsMultiProject;

    /// <summary>Project settings / Choose CA folder acts on one project: hidden while several are shown (My work, Ctrl-click).</summary>
    public bool CanEditProject => HasProject && !IsMultiProject;

    // ---- The header's counts by when items are due (each a quick Due filter) ----------------

    private (int Overdue, int Today, int Soon, int All) dueCounts;

    /// <summary>Counts the list as filtered, except for the Due filter itself, so each chip says what it would show.</summary>
    private void UpdateDueCounts()
    {
        var rows = allRows.Where(MatchesType).Where(row => MatchesDiscipline(row) && MatchesFilter(row) && MatchesPerson(row)).ToList();
        dueCounts = (
            rows.Count(row => row.IsOverdue),
            rows.Count(row => row.IsDueToday),
            rows.Count(row => row.DaysTillDue is { } days && days >= 0 && days <= DueWithinDays),
            rows.Count);
        foreach (var name in new[]
                 {
                     nameof(HasDueChips), nameof(AllChipText), nameof(IsDueAnyFilter),
                     nameof(OverdueChipText), nameof(HasOverdueChip), nameof(IsDueOverdueFilter),
                     nameof(DueTodayChipText), nameof(HasDueTodayChip), nameof(IsDueTodayFilter),
                     nameof(DueSoonChipText), nameof(HasDueSoonChip), nameof(IsDueSoonFilter)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    public bool HasDueChips => HasProject && (dueCounts.All > 0 || DueFilter != DueFilter.Any);
    public string AllChipText => $"{dueCounts.All} {MatchingNoun(dueCounts.All)}";
    public bool IsDueAnyFilter => DueFilter == DueFilter.Any;
    public string OverdueChipText => $"{dueCounts.Overdue} overdue";
    public bool HasOverdueChip => dueCounts.Overdue > 0 || DueFilter == DueFilter.Overdue;
    public bool IsDueOverdueFilter => DueFilter == DueFilter.Overdue;
    public string DueTodayChipText => $"{dueCounts.Today} due today";
    public bool HasDueTodayChip => dueCounts.Today > 0 || DueFilter == DueFilter.DueToday;
    public bool IsDueTodayFilter => DueFilter == DueFilter.DueToday;
    public string DueSoonChipText => $"{dueCounts.Soon} due within {DueWithinDays} days";
    public bool HasDueSoonChip => dueCounts.Soon > 0 || DueFilter == DueFilter.DueWithin;
    public bool IsDueSoonFilter => DueFilter == DueFilter.DueWithin;

    /// <summary>Folders the Base Build / Tenant Fit Out split hides because their names say neither part.</summary>
    private int HiddenByPhaseCount => comparison is null || !HasCaFolder || comparison.Project.Scope is not { IsSplit: true }
        ? 0
        : comparison.Types.Values.Sum(type => type.HiddenByPhase);

    /// <summary>The folder warning's button: count those folders (and items) as this project's part.</summary>
    public bool CanIncludeUnlabeled => HiddenByPhaseCount > 0 && !IsMultiProject && comparison?.Project.Scope is { IncludeUnlabeled: false };

    public string IncludeUnlabeledText => comparison?.Project.Scope is { IsSplit: true } scope
        ? $"Include them in {Phases.Short(scope.Phase)}…"
        : "Include them…";

    /// <summary>
    /// Counts folders and items that say neither BB nor TFO as the selected project's part (the "Also
    /// include…" box in Project settings). Only the tracker's settings change.
    /// </summary>
    public async Task IncludeUnlabeledAsync()
    {
        if (SelectedProject is not { IsPlaceholder: false } project)
        {
            return;
        }

        var edited = CloneProject(project.Project);
        edited.Scope ??= new PhaseScope();
        edited.Scope.IncludeUnlabeled = true;
        await UpdateProjectAsync(edited);
    }

    /// <summary>Hover text for Move to Returned: what "returned" means for this project.</summary>
    public string MarkReturnedToolTip
    {
        get
        {
            var rule = CompanyText.Length > 0 ? CompanyText : "Moves the checked folders that are ready to return into the Returned folder.";
            var others = checks.Returnable - checks.Ready;
            var note = checks.Returnable == 0 ? ""
                : checks.Ready == 0 ? " None of the checked folders is ready to return, so you're asked first."
                : others == 1 ? " 1 checked folder isn't ready to return and stays where it is."
                : others > 1 ? $" {others} checked folders aren't ready to return and stay where they are."
                : "";
            return $"{rule}{note} A preview shows every move before anything changes.";
        }
    }

    /// <summary>The selected project is a Helix/Procore project with no CA folder chosen yet.</summary>
    public bool IsPlaceholder => SelectedProject?.IsPlaceholder == true;
    public bool CanEditConnection => HasProject && !IsPlaceholder;
    public string EditButtonText => IsPlaceholder ? "Choose CA folder…" : "Project settings";

    public bool CanCreateTypeFolders => comparison is not null
        && HasCaFolder
        && comparison.Types.Values.Any(type => type.Scan.Error is null && !type.Scan.TypeFolderExists);

    public string RfiTabText => TabText(ItemTypes.Rfi);
    public string SubmittalTabText => TabText(ItemTypes.Submittal);
    public string BothTabText => TabText(ItemTypeChoices.Both);

    public string EmptyText => !HasProject ? ""
        : IsPlaceholder && Rows.Count == 0 ? "Select Choose CA folder… to track this project's RFIs and Submittals against its CA folder."
        : !HasCaFolder && Rows.Count == 0 ? "Choose this project's CA folder in Project settings."
        : !IsConnected && Rows.Count == 0 ? "Link this project to its Helix or Procore project (Link… above), then Sync, to compare its items."
        : Rows.Count == 0 && HasItemSearch ? SearchEmptyText()
        : Rows.Count == 0 && IsFiling ? Filter switch
        {
            Filters.Ready => "No folders are ready to return.",
            Filters.Reopened => "No folders in Returned are back with you.",
            Filters.Duplicates => "No item has more than one folder.",
            Filters.NoItem => "Every folder has its Helix or Procore item.",
            Filters.NoFolder => "Every open item has a folder.",
            Filters.Returned => "Nothing is in the Returned folder.",
            _ => "Nothing to look at: the folders match the items."
        }
        : Rows.Count == 0 && HasActiveFilters ? "No items match the filters above."
        : Rows.Count == 0 ? "No items here."
        : "";

    public bool HasEmptyText => EmptyText.Length > 0;

    private string SearchEmptyText()
    {
        var hidden = allRows.Count(MatchesSearch);
        var search = itemSearch.Trim();
        return hidden == 0 ? $"No item's number, title or folder contains \"{search}\"."
            : hidden == 1 ? $"None of the items shown contain \"{search}\". One hidden by the filters above does."
            : $"None of the items shown contain \"{search}\". {hidden} hidden by the filters above do.";
    }

    /// <summary>Anything narrower than the default view (open items, everyone, any date, every discipline).</summary>
    public bool HasActiveFilters => Filter != Filters.Open || HasPersonFilter || DueFilter != DueFilter.Any || DisciplineFilter.Length > 0;

    /// <summary>What is checked, worked out once per change (checking every shown row is one change).</summary>
    private sealed record CheckSummary(int Checked, int Hidden, int Returnable, int Ready, int MoveBack, int Discipline, int Download)
    {
        public static readonly CheckSummary None = new(0, 0, 0, 0, 0, 0, 0);
    }

    private CheckSummary checks = CheckSummary.None;
    private bool checksPending;

    private CheckSummary SummarizeChecks()
    {
        var checkedRows = allRows.Where(row => row.IsChecked).ToList();
        if (checkedRows.Count == 0)
        {
            return CheckSummary.None;
        }

        // A folder shown under both the BB and TFO projects is moved once, an item downloaded once.
        static int Folders(IEnumerable<RowViewModel> rows) =>
            rows.Select(row => row.Row.Folder!.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new CheckSummary(
            checkedRows.Count,
            checkedRows.Count - Rows.Count(row => row.IsChecked),
            Folders(checkedRows.Where(row => row.Row.CanMarkReturned)),
            Folders(checkedRows.Where(row => row.Row.CanMarkReturned && row.Status == RowStatus.ReadyToReturn)),
            Folders(checkedRows.Where(row => row.Row.CanMoveBack)),
            checkedRows.Count(row => row.CanSelect),
            checkedRows.Where(row => row.CanDownload)
                .Select(row => $"{row.Project.Project.Connection?.ProjectId}|{row.Row.ItemType}|{row.Row.Item!.ItemId}")
                .Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>A row was checked or unchecked: update the action bar once the batch is done.</summary>
    private void ChecksChangedSoon()
    {
        if (checksPending)
        {
            return;
        }

        checksPending = true;
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            checksPending = false;
            RaiseChecks();
        }, DispatcherPriority.Background);
    }

    private void RaiseChecks()
    {
        checks = SummarizeChecks();
        foreach (var name in new[]
                 {
                     nameof(CheckedCount), nameof(HasChecks), nameof(SelectionText), nameof(AllShownChecked),
                     nameof(CanMarkReturned), nameof(MarkReturnedText), nameof(CanMoveBack), nameof(MoveBackText),
                     nameof(CanSetDiscipline), nameof(SetDisciplineText), nameof(CanDownloadChecked), nameof(DownloadCheckedText),
                     nameof(VerifyCheckedText), nameof(MarkReturnedToolTip), nameof(ShowActionBar),
                     nameof(CanAssignChecked), nameof(AssignCheckedText)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    public int CheckedCount => checks.Checked;
    public bool HasChecks => checks.Checked > 0;

    private int readyShown;

    /// <summary>Shown rows that are ready to return, for "Check all ready to return".</summary>
    public int ReadyShownCount => readyShown;
    public bool HasReadyShown => readyShown > 0;
    public string CheckReadyText => readyShown == 1 ? "Check the 1 ready to return" : $"Check all {readyShown} ready to return";

    /// <summary>The action bar shows only while something is checked or something shown is ready to return.</summary>
    public bool ShowActionBar => HasChecks || HasReadyShown;

    public string SelectionText => checks.Checked == 0 ? "Nothing checked"
        : checks.Hidden > 0 ? $"{checks.Checked} checked ({checks.Hidden} hidden by filters)"
        : $"{checks.Checked} checked";

    /// <summary>Some checked folders are in the active folder, ready to return or not.</summary>
    public bool CanMarkReturned => checks.Returnable > 0;

    /// <summary>
    /// Move to Returned moves the checked folders that are ready to return. Only when none of them is
    /// does it offer the other checked folders, and then it asks first.
    /// </summary>
    public string MarkReturnedText => checks.Returnable == 0 ? "Move to Returned…"
        : checks.Ready == 0 ? $"Move {checks.Returnable} to Returned anyway…"
        : checks.Ready < checks.Returnable ? $"Move {checks.Ready} ready to Returned…"
        : $"Move {checks.Ready} to Returned…";
    public bool CanMoveBack => checks.MoveBack > 0;
    public string MoveBackText => checks.MoveBack == 0 ? "Move back to active…" : $"Move {checks.MoveBack} back to active…";

    /// <summary>
    /// Items back with your company whose folders are in Returned, in the projects shown (whatever the filters):
    /// "↩ 2 back with you" offers to move them back with a new R folder.
    /// </summary>
    public IReadOnlyList<RowViewModel> ReopenedRows() =>
        allRows.Where(row => row.Status == RowStatus.Reopened && row.Row.CanMoveBack).ToList();

    public bool HasReopened => allRows.Any(row => row.Status == RowStatus.Reopened && row.Row.CanMoveBack);
    public string ReopenedText => $"↩ {ReopenedRows().DistinctBy(row => row.Row.Folder!.FullPath, StringComparer.OrdinalIgnoreCase).Count()} back with you";
    public bool CanSetDiscipline => checks.Discipline > 0;
    public string SetDisciplineText => checks.Discipline == 0 ? "Set discipline…" : $"Set discipline of {checks.Discipline}…";
    public bool CanDownloadChecked => checks.Download > 0;
    public string DownloadCheckedText => checks.Download == 0 ? "Download…" : $"Download {checks.Download}…";
    public string VerifyCheckedText => checks.Download == 0 ? "Verify folders…" : $"Verify {checks.Download} folder{(checks.Download == 1 ? "" : "s")}…";

    /// <summary>Header box: true when every checkable shown row is checked, null when some are, false when none.</summary>
    public bool? AllShownChecked
    {
        get
        {
            var tickable = Rows.Where(row => row.CanSelect).ToList();
            var ticked = tickable.Count(row => row.IsChecked);
            return ticked == 0 ? false : ticked == tickable.Count ? true : null;
        }
    }

    /// <summary>Checks (or unchecks) every row the current filters show.</summary>
    public void SetAllShownChecked(bool value)
    {
        foreach (var row in Rows.Where(row => row.CanSelect))
        {
            row.IsChecked = value;
        }
    }

    /// <summary>Checked rows, from every shown project.</summary>
    public IReadOnlyList<RowViewModel> CheckedRows() => allRows.Where(row => row.IsChecked).ToList();

    /// <summary>
    /// What the Download button needs for a row: the Helix item, and where its files go by default
    /// (its matched folder, else a new "&lt;number&gt; - &lt;name&gt;" folder in the project's RFI/Submittal folder).
    /// Null when the item can't be downloaded (not Helix, or no item).
    /// </summary>
    public DownloadRequest? DownloadRequestFor(RowViewModel row)
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

        // Back with you while its folder is in Returned: a new round, which the download window saves in an R folder
        // (moving the folder out of Returned first, if you leave that checked).
        var moveBackTo = row.Status == RowStatus.Reopened && row.Row.CanMoveBack && row.Row.Folder is { } folder
            && row.Comparison.For(row.Row.ItemType).Scan is { Error: null } typeScan
                ? OperationPlanner.ActiveDestination(folder, typeScan)
                : null;
        return new DownloadRequest(item.ItemType, item.ItemId, project.Connection.ProjectId, item.Number, item.Title,
            projectName, row.Row.Folder?.FullPath, parent, moveBackTo);
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
        MigrateArchiveKeys();
        foreach (var project in Projects)
        {
            project.IsSyncSkipped = IsSyncSkipped(project);
            project.IsFavorite = favorites.Contains(project.ArchiveKey);
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

    /// <summary>
    /// A tracked project is archived and favorited by its own id, a project without a CA folder by its
    /// Helix/Procore project (see <see cref="ProjectViewModel.ArchiveKey"/>). A mark under the
    /// Helix/Procore key — from before 3.1, or set before the CA folder was chosen — moves to each tracked
    /// project with that connection, so nothing changes on screen; the old key goes once no project
    /// without a CA folder uses it.
    /// </summary>
    private void MigrateArchiveKeys()
    {
        var changed = false;
        var stillUsed = Projects.Where(project => project.IsPlaceholder).Select(project => project.ArchiveKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in Projects.Where(project => !project.IsPlaceholder && project.ProviderArchiveKey is not null))
        {
            foreach (var marks in new[] { archived, favorites })
            {
                if (marks.Contains(project.ProviderArchiveKey!))
                {
                    changed |= marks.Add(project.ArchiveKey);
                    carried.Add(project.ProviderArchiveKey!);
                }
            }
        }

        foreach (var key in carried.Where(key => !stillUsed.Contains(key)))
        {
            changed |= archived.Remove(key);
            changed |= favorites.Remove(key);
        }

        if (changed)
        {
            ScheduleViewStateSave();
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
        var run = manyRun = new HeaderRun();
        OnHeaderChanged();
        try
        {
            var result = await RefreshSequentiallyAsync(
                targets,
                (index, project) => SetRefreshMessage($"Syncing {project.Name} ({index + 1} of {targets.Count})…", false),
                () => stopMany || run.Stopped,
                run);
            SetRefreshMessage(result.Summary(targets.Count, "Synced"), result.Failed.Count > 0);
            await RecheckFoldersAsync();
        }
        finally
        {
            refreshingMany = false;
            manyRun = null;
            OnHeaderChanged();
        }
    }

    /// <summary>Starts a refresh of any project (the selected one, or one being synced).</summary>
    public async Task StartRefreshAsync(ProjectViewModel? project)
    {
        if (project is null || project.IsRefreshing || project.IsQueued || headerRuns.ContainsKey(project))
        {
            return;
        }

        if (IsHelix(project) || (IsProcore(project) && !procoreSync.PageReadingUnavailable))
        {
            // Read in the background; other refreshes can start meanwhile. (A Procore read that has to
            // fall back to the browser window starts that window from here too.) The header says
            // "Syncing…" from the click on, also while the folders are rescanned and the read waits its turn.
            var run = new HeaderRun();
            headerRuns[project] = run;
            OnHeaderChanged();
            try
            {
                await StartRefreshCoreAsync(project, run);
            }
            finally
            {
                headerRuns.Remove(project);
                OnHeaderChanged();
            }

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

    private async Task StartRefreshCoreAsync(ProjectViewModel project, HeaderRun? run = null)
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
                SetRefreshMessage("CA folder rescanned. Link this project to its Helix or Procore project to sync its items.", false);
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
            var result = await SyncHelixAsync(new[] { project }, null, run is null ? null : () => run.Stopped, full: true, run);
            if (selected && (result.Stopped || run?.Stopped == true))
            {
                SetRefreshMessage("Sync stopped.", false);
            }
            else if (selected)
            {
                SetRefreshMessage(
                    result.Problem ?? (result.Done > 0
                        ? $"Synced from Helix {Formatting.When(DateTimeOffset.Now)} · {(result.Changed == 0 ? "no changes" : result.Changed == 1 ? "1 item changed" : $"{result.Changed} items changed")}."
                        : $"Helix could not be read for {project.Name}. Try again, or open the log folder (Settings → General → Troubleshooting)."),
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

            var procore = await SyncProcoreAsync(new[] { project }, null, run is null ? null : () => run.Stopped, run);
            if (!procore.FallbackToWindow)
            {
                if (selected && (procore.Stopped || run?.Stopped == true))
                {
                    SetRefreshMessage("Sync stopped.", false);
                }
                else if (selected)
                {
                    SetRefreshMessage(
                        procore.Problem ?? (procore.Done > 0
                            ? $"Synced from Procore {Formatting.When(DateTimeOffset.Now)} · every RFI and Submittal read."
                            : $"Procore could not be read for {project.Name}. Try again, or open the log folder (Settings → General → Troubleshooting)."),
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
        // A sync started here stops; a background sync (or Sync now in Connections) carries on.
        if (SelectedProject is { } current && headerRuns.TryGetValue(current, out var run))
        {
            run.Stopped = true;
            helixSync.Cancel(run);
            procoreSync.Cancel(run);
            SetRefreshMessage("Sync stopped.", false);
        }

        // Several projects being refreshed from here: skip the rest and cancel the one in progress.
        if (refreshingMany)
        {
            stopMany = true;
            if (manyRun is { } many)
            {
                many.Stopped = true;
                helixSync.Cancel(many);
                procoreSync.Cancel(many);
            }

            foreach (var other in selectedProjects.Where(project => project != SelectedProject && project.ActiveRefresh is { IsFinished: false }))
            {
                other.ActiveRefresh = refreshStore.Cancel(other.ActiveRefresh!.Id, DateTimeOffset.Now);
            }

            SetRefreshMessage("Sync stopped.", false);
            OnHeaderChanged();
        }

        var project = SelectedProject;
        if (project?.ActiveRefresh is null || project.ActiveRefresh.IsFinished)
        {
            return;
        }

        project.ActiveRefresh = refreshStore.Cancel(project.ActiveRefresh.Id, DateTimeOffset.Now);
        SetRefreshMessage("Sync cancelled.", false);
        OnHeaderChanged();
    }

    private async Task OnTickAsync()
    {
        var now = DateTimeOffset.Now;
        foreach (var project in Projects)
        {
            project.ExpireJustSynced(now);
        }

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
                    SetRefreshMessage($"Synced from {project.ProviderLabel} {Formatting.When(latest.FinishedAt ?? now)} · {latest.ItemCount} items.", false);
                    LoadProjects();
                    await CompareAsync();
                }
                else if (latest.Status == RefreshStatus.Failed)
                {
                    SetRefreshMessage($"Sync did not finish: {latest.Error}", true);
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

        // How a sync went fades after a while (the times above the list keep it); progress and errors stay.
        refreshMessageHide.Stop();
        if (!isError && message.Length > 0 && !message.EndsWith('…'))
        {
            refreshMessageHide.Start();
        }
    }

    private readonly DispatcherTimer refreshMessageHide = new() { Interval = TimeSpan.FromSeconds(20) };

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

            // Keep checks on folders and items that are still shown.
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
                                ChecksChangedSoon();
                            }
                        };
                        rows.Add(rowView);
                    }
                }

                var hasFolder = !string.IsNullOrWhiteSpace(view.Project.CaFolder);
                view.AttentionCount = hasFolder ? result.Types.Values.Sum(type => type.AttentionCount) : 0;
                view.Compared = hasFolder;
            }

            ReadAssignmentIndexes();
            ApplyAssignments(rows);
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
                    connection.BeginBusy();
                    try
                    {
                        var discovery = await DiscoverProcoreAsync();
                        connection.SyncMessage = discovery.Problem ?? FoundText(discovery);
                    }
                    finally
                    {
                        connection.EndBusy();
                    }
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
            connection.SyncMessage = $"{label} is read in the browser window when you sync.";
            return;
        }

        connection.SyncMessage = result.Problem
            ?? $"Updated {result.Done} of {distinct} {label} projects {Formatting.When(DateTimeOffset.Now)}" +
               (result.Changed > 0 ? $" · {result.Changed} items changed." : "") +
               (result.FailedProjectIds.Count > 0 ? $" {result.FailedProjectIds.Count} couldn't be read (marked in the project list)." : "");
        if (result.NeedsSignIn)
        {
            StatusMessage = $"{label} needs you to sign in again: Settings → Connections → Sign in to {label}.";
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

    /// <summary>
    /// Saves anonymized Workload Tracker observations built from the app's own data to a new folder in the
    /// exports folder: projects set up in the app as item observations (a sample of up to 600 items each),
    /// the others as catalog observations. Returns the folder, or a reason it couldn't.
    /// </summary>
    public async Task<(string? Folder, string? Problem)> ExportWorkloadTrackerFixturesAsync()
    {
        var snapshots = store.Snapshots();
        if (snapshots.Count == 0)
        {
            return (null, "Refresh a Helix or Procore project first.");
        }

        var projects = store.SettingsCopy().Projects;
        var files = await Task.Run(() => FixtureExport.Build(snapshots, projects, TrackerVersion.Current));

        var folder = Path.Combine(Paths.ExportsDirectory, $"workload-tracker-fixtures-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(folder);
        foreach (var file in files)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, file.Name), file.Json);
        }

        AppLog.Info("export", $"Exported {files.Count} anonymized Workload Tracker fixtures to {folder}.");
        return (folder, null);
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
        foreach (var name in new[] { nameof(ManagedByText), nameof(HasManagedByText), nameof(AreFoldersLocked), nameof(IsSyncLocked) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>What Settings says about settings your company manages.</summary>
    public string ManagedByText => companyError is not null
        ? $"The company settings file couldn't be read ({companyError}); the app's own settings are used."
        : company is null ? ""
        : $"Some settings are set by {(string.IsNullOrWhiteSpace(company.CompanyName) ? "your company" : company.CompanyName)}" +
          (company.Locked.Count > 0 ? $" and can't be changed here: {string.Join(", ", company.Locked.Select(LockedLabel))}." : ".");

    private static string LockedLabel(string name) => name.ToLowerInvariant() switch
    {
        "folders" => "folder names",
        "sync" => "how often projects sync",
        "disciplines" => "disciplines",
        _ => name
    };

    public bool HasManagedByText => ManagedByText.Length > 0;
    public bool AreFoldersLocked => company?.IsLocked("folders") == true;
    public bool IsSyncLocked => company?.IsLocked("sync") == true;

    // ---- Workload Tracker (optional) -------------------------------------------------------

    private WorkloadTrackerSync? workloadTracker;
    private DispatcherTimer? workloadTimer;

    /// <summary>Sends what scans changed to Workload Tracker (while paired) shortly after starting, then every minute.</summary>
    public void UseWorkloadTracker(WorkloadTrackerSync service)
    {
        workloadTracker = service;
        service.StatusChanged += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(RaiseWorkloadTracker));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        workloadTimer = timer;
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromMinutes(1);
            if (service.IsPaired)
            {
                await RunTimerAsync("Workload Tracker", () => service.SendAsync());
            }
        };
        timer.Start();
        RaiseWorkloadTracker();
    }

    /// <summary>The tracker's address as https://host, or null when it isn't one.</summary>
    public static string? NormalizeWorkloadTrackerAddress(string address) => WorkloadTrackerApi.NormalizeAddress(address);

    public string WorkloadTrackerAddress => Settings.WorkloadTracker.Address;

    public bool IsWorkloadTrackerPaired => Settings.WorkloadTracker.IsPaired;

    /// <summary>The token was refused or can't be read: Connect again (also shown in the sign-in banner).</summary>
    public bool WorkloadTrackerNeedsConnect => workloadTracker is { NeedsSignIn: true };

    /// <summary>What Settings → Connections shows about Workload Tracker.</summary>
    public string WorkloadTrackerStatusText
    {
        get
        {
            var sync = Settings.WorkloadTracker;
            if (!sync.IsPaired)
            {
                return sync.Address.Length == 0
                    ? "Not connected: everything stays on this PC."
                    : $"Not connected. Connect to send your projects to {sync.Address}.";
            }

            if (workloadTracker is null)
            {
                return "Workload Tracker isn't available in this version.";
            }

            var waiting = workloadTracker.PendingCount;
            var queued = waiting == 0 ? "" : $" · {waiting} project{(waiting == 1 ? "" : "s")} waiting to send";
            var assignments = workloadTracker.PendingAssignmentCount;
            queued += assignments == 0 ? "" : $" · {assignments} assignment change{(assignments == 1 ? "" : "s")} waiting to send";
            if (workloadTracker.LastProblem is { } problem)
            {
                return $"⚠ {problem}{queued}";
            }

            var person = sync.PersonId is null
                ? " ⚠ No person is linked to this account in Workload Tracker, so assignments and My Work can't use it."
                : "";
            var sent = workloadTracker.LastSentAt is { } at ? $" · last sent {Formatting.When(at)}" : " · sending starts in a moment";
            return $"Connected to {sync.Address} as {sync.AccountName}{sent}{queued}.{person}";
        }
    }

    private void RaiseWorkloadTracker()
    {
        OnPropertyChanged(nameof(WorkloadTrackerAddress));
        OnPropertyChanged(nameof(IsWorkloadTrackerPaired));
        OnPropertyChanged(nameof(WorkloadTrackerNeedsConnect));
        OnPropertyChanged(nameof(WorkloadTrackerStatusText));
        OnPropertyChanged(nameof(ShowsAssignments));
        OnPropertyChanged(nameof(ShowSignInBanner));
        OnPropertyChanged(nameof(SignInBannerText));
    }

    /// <summary>Pairs with Workload Tracker using a token from its Profile page. Null when it worked; otherwise what's wrong.</summary>
    public async Task<string?> ConnectWorkloadTrackerAsync(string address, string token, int? sessionId, DateTimeOffset? expiresAt)
    {
        if (workloadTracker is null)
        {
            return "Workload Tracker isn't available in this version.";
        }

        var problem = await workloadTracker.ConnectAsync(address, token, sessionId, expiresAt);
        if (problem is null)
        {
            signInBannerDismissed = false;
            await workloadTracker.SendAsync();
        }

        RaiseWorkloadTracker();
        return problem;
    }

    /// <summary>Sends what is waiting now. Null when it worked; otherwise what's wrong.</summary>
    public async Task<string?> SendToWorkloadTrackerAsync()
    {
        if (workloadTracker is not { IsPaired: true })
        {
            return "Connect to Workload Tracker first.";
        }

        await workloadTracker.SendAsync();
        RaiseWorkloadTracker();
        return workloadTracker.LastProblem;
    }

    /// <summary>Stops sending: revokes this PC's connection in Workload Tracker and keeps everything on this PC.</summary>
    public async Task DisconnectWorkloadTrackerAsync()
    {
        if (workloadTracker is not null)
        {
            await workloadTracker.DisconnectAsync();
        }

        RaiseWorkloadTracker();
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
            await RunTimerAsync("update check", CheckForUpdateAsync);
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
    public string UpdateText => update is null ? "" : $"Install version {update.Version}…";
    public string? UpdateNotes => update?.Notes;
    /// <summary>Where the installer comes from, shown before installing so people can see it's the company's location.</summary>
    public string? UpdateSource => update?.Installer;

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
        // Nor the Workload Tracker connection, which Connect and Disconnect save as they happen.
        updated.WorkloadTracker = Settings.WorkloadTracker;
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
            && Matches(row) && MatchesSearch(row));
        return count == 0 ? label : $"{label} ({count})";
    }

    private void RebuildRows()
    {
        using var timing = AppLog.Time("list.rebuild", $"{allRows.Count} rows", 100);
        var assignedFirst = groupBy is GroupModes.Urgency && !IsFiling && IsMyWorkActive;
        foreach (var row in allRows)
        {
            row.ListAssignedFirst = assignedFirst;
        }

        var visible = allRows
            .Where(MatchesType)
            .Where(Matches)
            .Where(MatchesSearch)
            .OrderBy(row => row, Comparer<RowViewModel>.Create(CompareRows))
            .ToList();
        Rows.ReplaceAll(visible);
        readyShown = visible.Count(row => row.Status == RowStatus.ReadyToReturn && row.CanSelect);

        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ReadyShownCount));
        OnPropertyChanged(nameof(HasReadyShown));
        OnPropertyChanged(nameof(CheckReadyText));
        OnPropertyChanged(nameof(HasReopened));
        OnPropertyChanged(nameof(ReopenedText));
        OnPropertyChanged(nameof(HasEmptyText));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(CanClearFilters));
        OnPropertyChanged(nameof(HasHiddenSearchMatches));
        RaiseChecks();
        OnPropertyChanged(nameof(RfiTabText));
        OnPropertyChanged(nameof(SubmittalTabText));
        OnPropertyChanged(nameof(BothTabText));
        UpdateDueCounts();
        UpdateFilingCounts();
        RaiseViews();
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
                ApplyAssignments(source.Rows);
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
            project.MatchingNoun = MatchingNoun(matching.Count);
            project.MatchingCount = matching.Count;
            var next = matching.Where(row => row.DaysTillDue >= 0).MinBy(row => row.DaysTillDue);
            project.NextDueText = next?.Row.Item?.DueAt is not { } due ? ""
                : next.DaysTillDue == 0 ? "next due today"
                : $"next due {due.ToLocalTime():MMM d}";
        }

        foreach (var gone in badgeSources.Keys.Where(id => Projects.All(project => project.Id != id)).ToList())
        {
            badgeSources.Remove(gone);
        }

        UpdateViewCounts();
        UpdateWatchlistCounts();
        CountsUpdated?.Invoke();
    }

    /// <summary>What a project's count is of, for its badge ("12 open", "1 item", "3 ready").</summary>
    private string MatchingNoun(int count) => Filter switch
    {
        Filters.All => count == 1 ? "item" : "items",
        Filters.Attention => count == 1 ? "needs attention" : "need attention",
        Filters.Ready => "ready",
        Filters.NoFolder => "with no folder",
        Filters.Returned => "in Returned",
        Filters.Reopened => "back with you",
        Filters.Duplicates => "with two folders",
        Filters.NoItem => "with no item",
        _ => "open"
    };

    private bool MatchesType(RowViewModel row) =>
        SelectedType == ItemTypeChoices.Both || string.Equals(row.Row.ItemType, SelectedType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The list's filters. Filing is about folders, not who has an item or when it's due, so those two filters
    /// don't apply there (an item ready to return is, by the rule, with no one from your company).
    /// </summary>
    private bool Matches(RowViewModel row) =>
        MatchesDiscipline(row) && MatchesFilter(row)
        && (IsFiling || (MatchesPerson(row) && ItemTiming.MatchesDue(row.Row.Item, DueFilter, DueWithinDays, DateTime.Today)));

    private bool MatchesPerson(RowViewModel row)
    {
        if (activeWatchlist is { } list)
        {
            return Watched(list, row);
        }

        if (!HasPersonFilter)
        {
            return true;
        }

        // Any checked person (or anyone at the company) having the item is a match. A person Procore lists
        // only by name may have the company in brackets after it.
        return (rememberCompany && row.Holders.Any(holder => row.Comparison.Company.Matches(holder.CompanyId, holder.Company)
                    || row.Comparison.Company.Matches(null, PersonNames.CompanyIn(holder.Name))))
            || row.HolderNames.Any(rememberedPeople.Contains)
            || (row.IsAssignedToMe && YourNames().Any(rememberedPeople.Contains));
    }

    /// <summary>
    /// Section order first (project, then type, as grouped), then the chosen column. Empty values
    /// always go last; ties fall back to the item number.
    /// </summary>
    private int CompareRows(RowViewModel left, RowViewModel right)
    {
        if (groupBy is GroupModes.Urgency && IsFiling)
        {
            var byProject = string.Compare(left.ProjectName, right.ProjectName, StringComparison.CurrentCultureIgnoreCase);
            if (byProject != 0)
            {
                return byProject;
            }
        }
        else if (groupBy is GroupModes.Urgency)
        {
            // "Assigned to me" (in My work), then by when it's due.
            var byUrgency = left.HeadingOrder.CompareTo(right.HeadingOrder);
            if (byUrgency != 0)
            {
                return byUrgency;
            }
        }

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
        nameof(RowViewModel.AssignedSortKey) => row.Assignees.Count == 0,
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
        nameof(RowViewModel.Title) => row.TitleText,
        nameof(RowViewModel.ProviderStatus) => row.ProviderStatus,
        nameof(RowViewModel.PersonSortKey) or nameof(RowViewModel.HolderText) => row.PersonSortKey,
        nameof(RowViewModel.AssignedSortKey) => row.AssignedSortKey,
        nameof(RowViewModel.FolderName) => row.HasFolder ? row.FolderName : "",
        nameof(RowViewModel.DisciplineText) => row.Row.Discipline?.Label ?? "",
        nameof(RowViewModel.LocationText) => row.LocationText,
        _ => row.Number
    };

    /// <summary>Fills the Person filter from everyone holding an item in this project, keeping checks.</summary>
    private void RebuildPersonOptions()
    {
        var choices = new List<PersonChoiceViewModel>();
        if (comparison is { Company.IsKnown: true } current)
        {
            choices.Add(new PersonChoiceViewModel($"Anyone at {current.Company.DisplayName}", isCompany: true));
        }

        else if (rememberCompany)
        {
            choices.Add(new PersonChoiceViewModel("Anyone at your company", isCompany: true) { IsAbsent = true });
        }

        var holding = allRows.SelectMany(row => row.HolderNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var you = YourNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // People checked earlier who hold nothing here stay listed (greyed), so the list shows what filters.
        // You come first among the names.
        choices.AddRange(holding.Concat(rememberedPeople)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => !holding.Contains(name))
            .ThenBy(name => !you.Contains(name))
            .ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .Select(name => new PersonChoiceViewModel(name, isCompany: false) { IsAbsent = !holding.Contains(name), IsYou = you.Contains(name) }));

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
        Filters.Reopened => row.Status == RowStatus.Reopened,
        Filters.Duplicates => row.Status is RowStatus.Duplicate or RowStatus.Ambiguous,
        Filters.NoItem => row.Status == RowStatus.NotInProvider,
        _ => row.Row.NeedsAttention
    };

    private void OnHeaderChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(HasProject), nameof(HasCaFolder), nameof(IsConnected), nameof(IsNotConnected), nameof(IsRefreshing), nameof(CanRefresh), nameof(CanCancelRefresh),
                     nameof(ProjectTitle), nameof(CaFolderText), nameof(ConnectionText), nameof(RefreshButtonText),
                     nameof(DataTimestampText), nameof(FolderTimestampText), nameof(TimestampText), nameof(ProjectSubtitle), nameof(HasProjectSubtitle),
                     nameof(CompanyText), nameof(FolderWarning), nameof(HasFolderWarning), nameof(CanCreateTypeFolders),
                     nameof(HasSetupChip), nameof(SetupIssuesText), nameof(CanShowProjectDetails), nameof(CanEditProject), nameof(HasDueChips),
                     nameof(LastSyncFailed), nameof(CanIncludeUnlabeled), nameof(IncludeUnlabeledText), nameof(MarkReturnedToolTip),
                     nameof(HasActiveFilters), nameof(CanClearFilters), nameof(HasHiddenSearchMatches),
                     nameof(IsPlaceholder), nameof(CanEditConnection), nameof(EditButtonText),
                     nameof(RfiTabText), nameof(SubmittalTabText), nameof(BothTabText), nameof(IsMultiProject), nameof(EmptyText), nameof(HasEmptyText), nameof(Comparison),
                     nameof(ShowsAssignments)
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
                case StoreChangeKind.Assignments:
                    assignmentsChanged = true;
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
            StatusMessage = $"Couldn't save to the database ({message}). Your changes stay while the app is open; open the log folder (Settings → General → Troubleshooting) to see why.");

    private async Task OnStoreChangedAsync()
    {
        HashSet<string> itemLists;
        HashSet<string> folderIndexes;
        bool settings;
        bool catalog;
        bool accounts;
        bool touched;
        bool assignments;
        lock (changeGate)
        {
            itemLists = new HashSet<string>(changedItemLists, StringComparer.OrdinalIgnoreCase);
            folderIndexes = new HashSet<string>(changedFolderIndexes, StringComparer.Ordinal);
            settings = settingsChanged;
            catalog = catalogChanged;
            accounts = accountsChanged;
            touched = snapshotsTouched;
            assignments = assignmentsChanged;
            changedItemLists.Clear();
            changedFolderIndexes.Clear();
            settingsChanged = catalogChanged = accountsChanged = snapshotsTouched = assignmentsChanged = false;
        }

        if (settings)
        {
            // Rules may have changed in place (e.g. the returned rule): counts are worked out again.
            settingsVersion++;
        }

        // The tracker projects on screen before reloading: settings saved from elsewhere replace them, and
        // then the list is compared again.
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
        if (assignments)
        {
            OnAssignmentsChanged(rowsWillBeRebuilt: shownChanged);
        }

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
        view.Upgrade();
        selectedType = view.ItemType is ItemTypeChoices.Both or ItemTypes.Rfi or ItemTypes.Submittal ? view.ItemType : ItemTypeChoices.Both;
        filter = string.IsNullOrWhiteSpace(view.Filter) ? Filters.Open : view.Filter;
        dueFilter = Enum.TryParse<DueFilter>(view.Due, out var due) ? due : DueFilter.Any;
        dueWithinDays = Math.Clamp(view.DueWithinDays, 0, 365);
        disciplineFilter = view.Discipline ?? "";
        groupBy = view.GroupBy is GroupModes.Urgency or GroupModes.None or GroupModes.Project or GroupModes.Type or GroupModes.ProjectAndType
            ? view.GroupBy
            : GroupModes.Urgency;
        sortColumn = string.IsNullOrWhiteSpace(view.SortColumn) ? nameof(RowViewModel.Number) : view.SortColumn;
        sortDescending = view.SortDescending;
        sortBy = view.SortPreset ?? "";
        archived.Clear();
        foreach (var key in view.ArchivedProjects ?? new List<string>())
        {
            archived.Add(key);
        }

        favorites.Clear();
        foreach (var key in view.FavoriteProjects ?? new List<string>())
        {
            favorites.Add(key);
        }

        showArchived = view.ShowArchived;
        projectsPaneWidth = view.ProjectsPaneWidth is >= 200 and <= 700 ? view.ProjectsPaneWidth : 290;
        hiddenColumns.Clear();
        hiddenColumns.UnionWith(view.HiddenColumns ?? new List<string>());
        // Checked names are kept without a company in brackets, as the list now shows them.
        rememberedPeople.Clear();
        rememberedPeople.UnionWith(view.People.Select(PersonNames.WithoutCompany).Where(name => name.Length > 0));
        rememberCompany = view.AnyoneAtCompany;
        peopleDefaulted = view.PeopleDefaulted;
        showItemDetails = view.ShowItemDetails;
        watchlists.Clear();
        foreach (var list in (view.Watchlists ?? new List<Watchlist>()).Select(list => list.Normalized()))
        {
            list.Name = Watchlist.UniqueName(list.Name, watchlists.Select(other => other.Name));
            watchlists.Add(list);
        }

        activeWatchlist = watchlists.FirstOrDefault(list => string.Equals(list.Name, view.ActiveWatchlist, StringComparison.OrdinalIgnoreCase));
        RebuildWatchlistItems();
    }

    /// <summary>The item details pane beside the list (View → Item details); remembered.</summary>
    public bool ShowItemDetails
    {
        get => showItemDetails;
        set
        {
            if (Set(ref showItemDetails, value))
            {
                ScheduleViewStateSave();
            }
        }
    }

    /// <summary>Called by the window once it has restored the saved view; changes are saved from then on.</summary>
    public void ViewStateRestored()
    {
        viewStateReady = true;
        DefaultPeopleToYou();
    }

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
            Version = ViewState.CurrentVersion,
            // Projects not yet tracked are remembered too; they're matched by id next time.
            SelectedProjectIds = selectedProjects.Select(project => project.Id).ToList(),
            PrimaryProjectId = SelectedProject?.Id,
            ItemType = selectedType,
            Filter = filter,
            People = rememberedPeople.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList(),
            AnyoneAtCompany = rememberCompany,
            PeopleDefaulted = peopleDefaulted,
            ShowItemDetails = showItemDetails,
            Watchlists = watchlists.ToList(),
            ActiveWatchlist = activeWatchlist?.Name,
            Due = dueFilter.ToString(),
            DueWithinDays = dueWithinDays,
            Discipline = disciplineFilter,
            GroupBy = groupBy,
            SortColumn = sortColumn,
            SortDescending = sortDescending,
            SortPreset = sortBy,
            ArchivedProjects = archived.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList(),
            FavoriteProjects = favorites.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList(),
            ShowArchived = showArchived,
            ProjectsPaneWidth = projectsPaneWidth,
            HiddenColumns = hiddenColumns.OrderBy(name => name, StringComparer.Ordinal).ToList()
        });
    }

    private readonly HashSet<string> hiddenColumns = new(StringComparer.Ordinal);

    /// <summary>Item-list columns turned off (right-click a heading), by the column's name.</summary>
    public bool IsColumnHidden(string column) => hiddenColumns.Contains(column);

    public void SetColumnHidden(string column, bool hidden)
    {
        if (hidden ? hiddenColumns.Add(column) : hiddenColumns.Remove(column))
        {
            ScheduleViewStateSave();
        }
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
        workloadTimer?.Stop();
    }
}
