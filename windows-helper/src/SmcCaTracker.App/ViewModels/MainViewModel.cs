using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.ViewModels;

public static class Filters
{
    public const string Attention = "attention";
    public const string All = "all";
    public const string Ready = "ready";
    public const string NoFolder = "nofolder";
    public const string Returned = "returned";
}

/// <summary>
/// State for the main window. The app owns the projects: each is a CA folder, optionally
/// connected to a Helix or Procore project. Refresh asks Chrome (through the extension) for fresh
/// items. Reading and comparing never changes a CA folder; every change goes through an
/// OperationPlan that the window previews before anything runs.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore settingsStore;
    private readonly SnapshotStore snapshotStore;
    private readonly RefreshRequestStore refreshStore;
    private readonly ProjectCatalogStore catalogStore;
    private readonly Dictionary<string, List<RowViewModel>> rowsByType = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer snapshotDebounce;
    private readonly DispatcherTimer ticker;
    private FileSystemWatcher? watcher;
    private ProjectViewModel? selectedProject;
    private ProjectComparison? comparison;
    private string selectedType = ItemTypes.Rfi;
    private string filter = Filters.Attention;
    private bool isBusy;
    private string statusMessage = "";
    private string refreshMessage = "";
    private bool refreshMessageIsError;
    private string extensionStatus = "";
    private int compareVersion;
    private int tickCount;
    private bool refreshStarting;

    public MainViewModel(AppPaths paths, SettingsStore settingsStore, TrackerSettings settings)
    {
        Paths = paths;
        this.settingsStore = settingsStore;
        Settings = settings;
        snapshotStore = new SnapshotStore(paths);
        refreshStore = new RefreshRequestStore(paths);
        catalogStore = new ProjectCatalogStore(paths);
        refreshStore.Cleanup(DateTimeOffset.Now);

        snapshotDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        snapshotDebounce.Tick += async (_, _) =>
        {
            snapshotDebounce.Stop();
            await OnSnapshotsChangedAsync();
        };
        ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += async (_, _) => await OnTickAsync();
        ticker.Start();

        LoadProjects();
        UpdateExtensionStatus();
        WatchSnapshots();
    }

    public AppPaths Paths { get; }
    public TrackerSettings Settings { get; private set; }
    public ObservableCollection<ProjectViewModel> Projects { get; } = new();
    public ObservableCollection<RowViewModel> Rows { get; } = new();
    public ProjectComparison? Comparison => comparison;

    public ProjectViewModel? SelectedProject
    {
        get => selectedProject;
        set
        {
            if (Set(ref selectedProject, value))
            {
                SetRefreshMessage("", false);
                OnHeaderChanged();
                _ = CompareAsync();
            }
        }
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

    public string ExtensionStatus
    {
        get => extensionStatus;
        private set => Set(ref extensionStatus, value);
    }

    public bool HasProject => SelectedProject is not null;
    public bool HasNoProjects => Projects.Count == 0;
    public bool HasCaFolder => !string.IsNullOrWhiteSpace(SelectedProject?.Project.CaFolder);
    public bool IsConnected => SelectedProject?.IsConnected == true;
    public bool IsNotConnected => HasProject && !IsConnected;
    public bool IsRefreshing => SelectedProject?.IsRefreshing == true;
    public bool CanRefresh => HasProject && !IsRefreshing && !refreshStarting;
    public string ProjectTitle => SelectedProject?.Name ?? "Choose or add a project";
    public string CaFolderText => HasCaFolder ? SelectedProject!.Project.CaFolder : "No CA folder chosen";
    public string ConnectionText => SelectedProject?.ConnectionText ?? "";
    public string RefreshButtonText => IsRefreshing ? "Refreshing…" : IsConnected ? $"Refresh from {SelectedProject!.ProviderLabel}" : "Refresh";

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
            var snapshot = SelectedProject?.Snapshot;
            if (snapshot is not null && string.Equals(snapshot.Provider, Providers.Procore, StringComparison.OrdinalIgnoreCase))
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
            return string.Join("  ", problems);
        }
    }

    public bool HasFolderWarning => FolderWarning.Length > 0;

    public bool CanCreateTypeFolders => comparison is not null
        && HasCaFolder
        && comparison.Types.Values.Any(type => type.Scan.Error is null && !type.Scan.TypeFolderExists);

    public string RfiTabText => TabText(ItemTypes.Rfi);
    public string SubmittalTabText => TabText(ItemTypes.Submittal);

    public string EmptyText => !HasProject ? ""
        : !HasCaFolder ? "Choose this project's CA folder in Edit project."
        : !IsConnected && Rows.Count == 0 ? "Connect this project to Helix or Procore, then Refresh, to compare its items."
        : Rows.Count == 0 && Filter == Filters.Attention ? "Nothing needs attention here."
        : Rows.Count == 0 ? "No items match this filter."
        : "";

    public bool HasEmptyText => EmptyText.Length > 0;

    public int CheckedCount => rowsByType.Values.SelectMany(rows => rows).Count(row => row.IsChecked);

    public string SelectionText => CheckedCount == 0 ? "No folders ticked" : $"{CheckedCount} folder{(CheckedCount == 1 ? "" : "s")} ticked";

    public IReadOnlyList<ComparisonRow> CheckedRows() =>
        rowsByType.Values.SelectMany(rows => rows).Where(row => row.IsChecked).Select(row => row.Row).ToList();

    public IReadOnlyList<CatalogProject> KnownProviderProjects() => catalogStore.WithSnapshots(snapshotStore.LoadAll());

    public ProviderSnapshot? SnapshotFor(TrackerProject project) =>
        project.Connection is null ? null : snapshotStore.Load(project.Connection.Provider, project.Connection.ProjectId);

    // ---- Projects -------------------------------------------------------------------------

    public void LoadProjects()
    {
        var existing = Projects.ToDictionary(project => project.Id);
        var settingsChanged = false;
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

            var snapshot = SnapshotFor(project);
            if (snapshot?.ScannedAt != viewModel.Snapshot?.ScannedAt
                || snapshot?.ProjectId != viewModel.Snapshot?.ProjectId
                || snapshot?.Items.Count != viewModel.Snapshot?.Items.Count)
            {
                viewModel.Snapshot = snapshot;
            }

            // A project connected from a pasted link learns its name from the first refresh.
            if (project.Connection is not null && string.IsNullOrWhiteSpace(project.Connection.ProjectName)
                && !string.IsNullOrWhiteSpace(snapshot?.ProjectName))
            {
                project.Connection.ProjectName = snapshot!.ProjectName;
                settingsChanged = true;
            }
        }

        var ids = Settings.Projects.Select(project => project.Id).ToHashSet();
        foreach (var stale in Projects.Where(project => !ids.Contains(project.Id)).ToList())
        {
            Projects.Remove(stale);
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

        if (settingsChanged)
        {
            SaveSettings();
        }

        OnPropertyChanged(nameof(HasNoProjects));
        OnHeaderChanged();
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
            SelectedProject = Projects[0];
        }
    }

    public TrackerProject CloneProject(TrackerProject project) =>
        JsonSerializer.Deserialize<TrackerProject>(JsonSerializer.Serialize(project, TrackerJson.Options), TrackerJson.Options)
        ?? new TrackerProject();

    // ---- Refresh --------------------------------------------------------------------------

    /// <summary>
    /// Rechecks the CA folder and, for a connected project, opens the project in Chrome so the
    /// extension reads fresh items. The request completes when the extension reports back.
    /// </summary>
    public async Task StartRefreshAsync()
    {
        var project = SelectedProject;
        if (project is null || project.IsRefreshing || refreshStarting)
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
        await CompareAsync();
        if (project.Project.Connection is null)
        {
            SetRefreshMessage("Folder checked. Connect this project to Helix or Procore to refresh its items.", false);
            return;
        }

        RefreshRequest request;
        try
        {
            request = refreshStore.Create(project.Project, Settings.Browser, DateTimeOffset.Now);
        }
        catch (Exception error)
        {
            SetRefreshMessage(error.Message, true);
            return;
        }

        project.ActiveRefresh = request;
        SetRefreshMessage($"Opening {project.ProviderLabel} in Chrome…", false);
        try
        {
            ChromeLauncher.Open(request.UrlForStep(0), Settings.Browser);
        }
        catch (Exception error)
        {
            project.ActiveRefresh = refreshStore.Fail(request.Id, error.Message, DateTimeOffset.Now);
            SetRefreshMessage($"Could not open Chrome: {error.Message}", true);
        }
    }

    public void CancelRefresh()
    {
        var project = SelectedProject;
        if (project?.ActiveRefresh is null || project.ActiveRefresh.IsFinished)
        {
            return;
        }

        project.ActiveRefresh = refreshStore.Cancel(project.ActiveRefresh.Id, DateTimeOffset.Now);
        SetRefreshMessage("Refresh cancelled. Close the Chrome tab if it is still open.", false);
        OnHeaderChanged();
    }

    private async Task OnTickAsync()
    {
        tickCount++;
        if (tickCount % 5 == 0)
        {
            UpdateExtensionStatus();
        }

        foreach (var project in Projects.Where(project => project.IsRefreshing).ToList())
        {
            try
            {
                await TickRefreshAsync(project);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // The request file is being written by the native host; try again next second.
            }
        }
    }

    private async Task TickRefreshAsync(ProjectViewModel project)
    {
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
                        ? $"Waiting for Chrome to open {project.ProviderLabel}"
                        : latest.Steps.Count > 1
                            ? $"Reading {project.ProviderLabel} page {Math.Min(latest.CompletedSteps + 1, latest.Steps.Count)} of {latest.Steps.Count}"
                            : $"Reading {project.ProviderLabel}";
                    SetRefreshMessage($"{phase}… {waited:m\\:ss}", false);
                }

                return;
            }

            project.ActiveRefresh = latest;
            project.Snapshot = SnapshotFor(project.Project);
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
                project.Compared = false;
            }
        }
    }

    private static string TimeoutMessage(ProjectViewModel project) =>
        $"Chrome did not report back. Check that you are signed in to {project.ProviderLabel} in Chrome, " +
        "that the SMC-CA-Tracker extension is up to date, and that its Windows helper is enabled.";

    private void SetRefreshMessage(string message, bool isError)
    {
        refreshMessage = message;
        refreshMessageIsError = isError;
        OnPropertyChanged(nameof(RefreshMessage));
        OnPropertyChanged(nameof(HasRefreshMessage));
        OnPropertyChanged(nameof(RefreshMessageIsError));
    }

    private void UpdateExtensionStatus()
    {
        var contact = ExtensionContact.Load(Paths);
        ExtensionStatus = contact is null
            ? "Chrome extension has not connected yet"
            : $"Chrome extension last connected {Formatting.When(contact.LastContactAt)}";
    }

    // ---- Comparison -----------------------------------------------------------------------

    public async Task CompareAsync()
    {
        var project = SelectedProject;
        var version = ++compareVersion;
        if (project is null)
        {
            comparison = null;
            rowsByType.Clear();
            RebuildRows();
            OnHeaderChanged();
            return;
        }

        var tracked = project.Project;
        var snapshot = project.Snapshot;
        var settings = Settings;
        IsBusy = true;
        StatusMessage = string.IsNullOrWhiteSpace(tracked.CaFolder) ? "" : $"Reading {tracked.CaFolder}…";
        try
        {
            var result = await Task.Run(() => ProjectComparer.Compare(snapshot, tracked, settings));
            if (version != compareVersion)
            {
                return;
            }

            comparison = result;
            rowsByType.Clear();
            foreach (var type in result.Types.Values)
            {
                var rows = type.Rows.Select(row => new RowViewModel(row, result.Company, result.Folders)).ToList();
                foreach (var row in rows)
                {
                    row.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName == nameof(RowViewModel.IsChecked))
                        {
                            OnPropertyChanged(nameof(CheckedCount));
                            OnPropertyChanged(nameof(SelectionText));
                        }
                    };
                }

                rowsByType[type.ItemType] = rows;
            }

            var hasFolder = !string.IsNullOrWhiteSpace(tracked.CaFolder);
            project.AttentionCount = hasFolder ? result.Types.Values.Sum(type => type.AttentionCount) : 0;
            project.Compared = hasFolder;
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

    public void SelectAllReady()
    {
        if (!rowsByType.TryGetValue(SelectedType, out var rows))
        {
            return;
        }

        foreach (var row in rows.Where(row => row.Status == RowStatus.ReadyToReturn))
        {
            row.IsChecked = true;
        }
    }

    public void ClearChecks()
    {
        foreach (var row in rowsByType.Values.SelectMany(rows => rows))
        {
            row.IsChecked = false;
        }
    }

    public TrackerSettings CloneSettings() =>
        JsonSerializer.Deserialize<TrackerSettings>(JsonSerializer.Serialize(Settings, TrackerJson.Options), TrackerJson.Options)
        ?? new TrackerSettings();

    public async Task ApplySettingsAsync(TrackerSettings updated)
    {
        Settings = updated;
        SaveSettings();
        LoadProjects();
        await CompareAsync();
    }

    public void SaveSettings() => settingsStore.Save(Settings);

    private string TabText(string itemType)
    {
        var label = ItemTypes.Plural(itemType);
        if (comparison is null || !HasCaFolder)
        {
            return label;
        }

        var count = comparison.For(itemType).AttentionCount;
        return count == 0 ? label : $"{label} ({count})";
    }

    private void RebuildRows()
    {
        Rows.Clear();
        if (rowsByType.TryGetValue(SelectedType, out var rows))
        {
            foreach (var row in rows.Where(Matches))
            {
                Rows.Add(row);
            }
        }

        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasEmptyText));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(SelectionText));
    }

    private bool Matches(RowViewModel row) => Filter switch
    {
        Filters.All => true,
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
                     nameof(RfiTabText), nameof(SubmittalTabText), nameof(EmptyText), nameof(HasEmptyText), nameof(Comparison)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ---- Snapshots from Chrome ------------------------------------------------------------

    private void WatchSnapshots()
    {
        try
        {
            Directory.CreateDirectory(snapshotStore.Directory);
            watcher = new FileSystemWatcher(snapshotStore.Directory, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            FileSystemEventHandler changed = (_, _) => snapshotDebounce.Dispatcher.InvokeAsync(() =>
            {
                snapshotDebounce.Stop();
                snapshotDebounce.Start();
            });
            watcher.Created += changed;
            watcher.Changed += changed;
            watcher.Renamed += (sender, args) => changed(sender, args);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = $"Automatic updates from Chrome are off: {error.Message}";
        }
    }

    // Snapshots also arrive when you simply browse a connected project in Chrome.
    private async Task OnSnapshotsChangedAsync()
    {
        var before = SelectedProject?.Snapshot?.ScannedAt;
        LoadProjects();
        if (SelectedProject is not null && !SelectedProject.IsRefreshing && SelectedProject.Snapshot?.ScannedAt != before)
        {
            await CompareAsync();
        }
    }

    public void Dispose()
    {
        watcher?.Dispose();
        snapshotDebounce.Stop();
        ticker.Stop();
    }
}
