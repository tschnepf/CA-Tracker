using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
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
/// State for the main window. Reading and comparing never changes a CA folder; every change
/// goes through an OperationPlan that the window previews before anything runs.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore settingsStore;
    private readonly SnapshotStore snapshotStore;
    private readonly Dictionary<string, List<RowViewModel>> rowsByType = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer snapshotDebounce;
    private FileSystemWatcher? watcher;
    private ProjectViewModel? selectedProject;
    private ProjectComparison? comparison;
    private string selectedType = ItemTypes.Rfi;
    private string filter = Filters.Attention;
    private bool isBusy;
    private string statusMessage = "";
    private int refreshVersion;

    public MainViewModel(AppPaths paths, SettingsStore settingsStore, TrackerSettings settings)
    {
        Paths = paths;
        this.settingsStore = settingsStore;
        Settings = settings;
        snapshotStore = new SnapshotStore(paths);
        snapshotDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        snapshotDebounce.Tick += async (_, _) =>
        {
            snapshotDebounce.Stop();
            await OnSnapshotsChangedAsync();
        };
        LoadProjects();
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
                OnHeaderChanged();
                _ = RefreshAsync();
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

    public bool HasProject => SelectedProject is not null;
    public bool HasNoProjects => Projects.Count == 0;
    public bool HasCaFolder => SelectedProject?.HasCaFolder == true;
    public bool NeedsCaFolder => HasProject && !HasCaFolder;
    public string ProjectTitle => SelectedProject?.Name ?? "Choose a project";
    public string CaFolderText => SelectedProject?.Mapping?.CaFolder ?? "No CA folder chosen yet";

    public string ProjectSubtitle
    {
        get
        {
            if (SelectedProject is null)
            {
                return "";
            }

            var snapshot = SelectedProject.Snapshot;
            if (snapshot is null)
            {
                return $"{SelectedProject.ProviderLabel} · No item data yet. Open this project in {SelectedProject.ProviderLabel} with the extension's Windows helper enabled.";
            }

            var partial = snapshot.Provider == Providers.Procore
                ? " · Procore shows only the rows visible in its grid, so missing items are not flagged."
                : "";
            return $"{SelectedProject.ProviderLabel} · Item data from {Formatting.When(snapshot.ScannedAt)}{partial}";
        }
    }

    public string CompanyText
    {
        get
        {
            if (comparison is null)
            {
                return "";
            }

            if (!comparison.Company.IsKnown)
            {
                return "Returned: closed items only — no company is known yet. Choose one in Settings.";
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
        : !HasCaFolder ? "Choose this project's CA folder to compare it."
        : Rows.Count == 0 && Filter == Filters.Attention ? "Nothing needs attention here."
        : Rows.Count == 0 ? "No items match this filter."
        : "";

    public bool HasEmptyText => EmptyText.Length > 0;

    public int CheckedCount => rowsByType.Values.SelectMany(rows => rows).Count(row => row.IsChecked);

    public string SelectionText => CheckedCount == 0 ? "No folders ticked" : $"{CheckedCount} folder{(CheckedCount == 1 ? "" : "s")} ticked";

    public IReadOnlyList<ComparisonRow> CheckedRows() =>
        rowsByType.Values.SelectMany(rows => rows).Where(row => row.IsChecked).Select(row => row.Row).ToList();

    public void LoadProjects()
    {
        var byKey = Projects.ToDictionary(project => project.Key);
        var seen = new HashSet<string>();
        foreach (var snapshot in snapshotStore.LoadAll())
        {
            var project = Upsert(byKey, snapshot.Provider, snapshot.ProjectId);
            project.Snapshot = snapshot;
            project.Mapping = Settings.FindMapping(snapshot.Provider, snapshot.ProjectId);
            seen.Add(project.Key);
        }

        foreach (var mapping in Settings.Projects)
        {
            var project = Upsert(byKey, mapping.Provider, mapping.ProjectId);
            project.Mapping = mapping;
            seen.Add(project.Key);
        }

        foreach (var stale in Projects.Where(project => !seen.Contains(project.Key)).ToList())
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

        OnPropertyChanged(nameof(HasNoProjects));
    }

    private ProjectViewModel Upsert(Dictionary<string, ProjectViewModel> byKey, string provider, string projectId)
    {
        var key = $"{provider}|{projectId}";
        if (!byKey.TryGetValue(key, out var project))
        {
            project = new ProjectViewModel(provider, projectId);
            byKey[key] = project;
            Projects.Add(project);
        }

        return project;
    }

    public async Task RefreshAsync()
    {
        var project = SelectedProject;
        var version = ++refreshVersion;
        if (project is null)
        {
            comparison = null;
            rowsByType.Clear();
            RebuildRows();
            return;
        }

        var mapping = Settings.FindMapping(project.Provider, project.ProjectId)
            ?? new ProjectMapping { Provider = project.Provider, ProjectId = project.ProjectId, ProjectName = project.Name };
        var snapshot = project.Snapshot;
        var settings = Settings;
        IsBusy = true;
        StatusMessage = mapping.CaFolder is null ? "" : $"Reading {mapping.CaFolder}…";
        try
        {
            var result = await Task.Run(() => ProjectComparer.Compare(snapshot, mapping, settings));
            if (version != refreshVersion)
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

            project.AttentionCount = mapping.CaFolder is null ? 0 : result.Types.Values.Sum(type => type.AttentionCount);
            project.Compared = mapping.CaFolder is not null;
            StatusMessage = mapping.CaFolder is null ? "" : $"Compared {Formatting.When(result.ComparedAt)}.";
        }
        catch (Exception error)
        {
            StatusMessage = $"Could not compare: {error.Message}";
        }
        finally
        {
            if (version == refreshVersion)
            {
                IsBusy = false;
                RebuildRows();
                OnHeaderChanged();
            }
        }
    }

    public async Task SetCaFolderAsync(string folder)
    {
        if (SelectedProject is null)
        {
            return;
        }

        var mapping = Settings.GetOrAddMapping(SelectedProject.Provider, SelectedProject.ProjectId, SelectedProject.Name);
        mapping.CaFolder = folder;
        SaveSettings();
        SelectedProject.Mapping = null;
        SelectedProject.Mapping = mapping;
        OnHeaderChanged();
        await RefreshAsync();
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
        foreach (var project in Projects)
        {
            project.Mapping = Settings.FindMapping(project.Provider, project.ProjectId);
        }

        LoadProjects();
        await RefreshAsync();
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
                     nameof(HasProject), nameof(HasCaFolder), nameof(NeedsCaFolder), nameof(ProjectTitle),
                     nameof(ProjectSubtitle), nameof(CaFolderText), nameof(CompanyText), nameof(FolderWarning),
                     nameof(HasFolderWarning), nameof(CanCreateTypeFolders), nameof(RfiTabText), nameof(SubmittalTabText),
                     nameof(EmptyText), nameof(HasEmptyText), nameof(Comparison)
                 })
        {
            OnPropertyChanged(name);
        }
    }

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

    private async Task OnSnapshotsChangedAsync()
    {
        var selectedKey = SelectedProject?.Key;
        var before = SelectedProject?.Snapshot?.ScannedAt;
        LoadProjects();
        if (selectedKey is not null && SelectedProject?.Snapshot?.ScannedAt != before)
        {
            await RefreshAsync();
            StatusMessage = $"New {SelectedProject?.ProviderLabel} data arrived {Formatting.When(DateTimeOffset.Now)}.";
        }
    }

    public void Dispose()
    {
        watcher?.Dispose();
        snapshotDebounce.Stop();
    }
}
