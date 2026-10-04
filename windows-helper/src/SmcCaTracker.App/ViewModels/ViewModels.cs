using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Operations;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ProjectViewModel : ObservableObject
{
    private TrackerProject project;
    private ProviderSnapshot? snapshot;
    private RefreshRequest? activeRefresh;
    private int attentionCount;
    private bool compared;

    public const string PlaceholderPrefix = "provider|";

    public ProjectViewModel(TrackerProject project)
    {
        this.project = project;
    }

    /// <summary>
    /// A Helix or Procore project you can open that isn't set up in the tracker yet. It is listed
    /// under "No CA folder yet" and is never saved until you choose its CA folder.
    /// </summary>
    public static ProjectViewModel ForProviderProject(CatalogProject source)
    {
        var connection = source.ToConnection();
        return new ProjectViewModel(new TrackerProject
        {
            Id = $"{PlaceholderPrefix}{source.Provider}|{source.ProjectId}",
            Name = source.Name,
            CompanyId = source.AccountId,
            CompanyName = source.AccountName,
            Connection = connection
        });
    }

    public static bool IsPlaceholderId(string? id) => id?.StartsWith(PlaceholderPrefix, StringComparison.Ordinal) == true;

    public bool IsPlaceholder => IsPlaceholderId(project.Id);
    public bool HasCaFolder => !string.IsNullOrWhiteSpace(project.CaFolder);

    /// <summary>The section of the project list this project appears in.</summary>
    public string GroupName => ShowAsFavorite ? "FAVORITES" : HasCaFolder ? "WITH A CA FOLDER" : "NO CA FOLDER YET";
    public int GroupOrder => ShowAsFavorite ? -1 : HasCaFolder ? 0 : 1;

    // An archived favorite shows (dimmed, when archived ones are shown) in its usual section.
    private bool ShowAsFavorite => isFavorite && !isArchived;

    private bool isFavorite;

    /// <summary>One of your favorites: listed first, under "Favorites" (for you only).</summary>
    public bool IsFavorite
    {
        get => isFavorite;
        set
        {
            if (Set(ref isFavorite, value))
            {
                RaiseAll();
            }
        }
    }

    public string Id => project.Id;

    public TrackerProject Project
    {
        get => project;
        set
        {
            project = value;
            RaiseAll();
        }
    }

    public ProviderSnapshot? Snapshot
    {
        get => snapshot;
        set
        {
            snapshot = value;
            RaiseAll();
        }
    }

    private SmcCaTracker.Core.Data.SyncRun? lastRun;

    /// <summary>The latest background read of this project (to show when it failed).</summary>
    public SmcCaTracker.Core.Data.SyncRun? LastRun
    {
        get => lastRun;
        set
        {
            if (!ReferenceEquals(lastRun, value))
            {
                lastRun = value;
                RaiseAll();
            }
        }
    }

    /// <summary>The latest read failed (and nothing was read since).</summary>
    public bool LastReadFailed => lastRun is { Status: "failed" } run && (snapshot is null || run.StartedAt > snapshot.ScannedAt);

    private bool isArchived;

    /// <summary>Archived by you (hidden from your list unless "Show archived" is checked; not synced).</summary>
    public bool IsArchived
    {
        get => isArchived;
        set
        {
            if (Set(ref isArchived, value))
            {
                RaiseAll();
            }
        }
    }

    /// <summary>
    /// How archiving and favorites remember this project, for you only: a tracked project by its own id
    /// (so a Base Build / Tenant Fit Out pair sharing one Helix project is archived separately), a
    /// Helix/Procore project with no CA folder yet by that project.
    /// </summary>
    public string ArchiveKey => IsPlaceholder && ProviderArchiveKey is { } providerKey ? providerKey : $"project|{project.Id}";

    /// <summary>The Helix/Procore project's key: a project's key before its CA folder was chosen (and before 3.1, every connected project's).</summary>
    public string? ProviderArchiveKey => project.Connection is { } connection
        ? $"{connection.Provider.ToLowerInvariant()}|{connection.ProjectId}"
        : null;

    private bool isSyncSkipped;

    /// <summary>Procore project set to not sync (it stays in the list, but isn't read).</summary>
    public bool IsSyncSkipped
    {
        get => isSyncSkipped;
        set
        {
            if (Set(ref isSyncSkipped, value))
            {
                RaiseAll();
            }
        }
    }

    /// <summary>Hover text for the project list: the company hosting it and why the last read failed.</summary>
    public string ListToolTip
    {
        get
        {
            var lines = new List<string> { Name };
            if (!string.IsNullOrWhiteSpace(project.CompanyName))
            {
                lines.Add($"Hosted by {project.CompanyName} in {ProviderLabel}");
            }

            if (IsFavorite)
            {
                lines.Add("★ Favorite (for you only). Right-click or ⋯ → Remove from favorites.");
            }

            if (IsArchived)
            {
                lines.Add("Archived (for you only). Right-click or ⋯ → Unarchive to bring it back.");
            }
            else if (IsSyncSkipped)
            {
                lines.Add("Sync turned off. Right-click or ⋯ → Sync this project to turn it back on.");
            }
            else if (LastReadFailed && !string.IsNullOrWhiteSpace(lastRun!.Error))
            {
                lines.Add($"Last sync failed: {lastRun.Error}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    private string CompanyPart =>
        IsPlaceholder && !string.IsNullOrWhiteSpace(project.CompanyName) ? $" · {project.CompanyName}" : "";

    public RefreshRequest? ActiveRefresh
    {
        get => activeRefresh;
        set
        {
            activeRefresh = value;
            RaiseAll();
        }
    }

    public string Name => string.IsNullOrWhiteSpace(project.Name) ? "Untitled project" : project.Name;
    public bool IsConnected => project.Connection is not null;
    public string ProviderLabel => project.Connection is null ? "" : Providers.Label(project.Connection.Provider);
    public string ConnectionText => project.Connection?.Label ?? "Not linked to Helix or Procore";
    public string PhaseText => project.Scope is { IsSplit: true } ? Phases.Short(project.Scope.Phase) : "";
    public bool IsRefreshing => activeRefresh is { IsFinished: false } || syncDepth > 0;

    private int syncDepth;

    /// <summary>Being read in the background (Helix). Counted, so overlapping reads don't clear each other.</summary>
    public bool IsSyncing => syncDepth > 0;

    private bool isQueued;
    private DateTimeOffset? syncedUntil;

    /// <summary>Waiting its turn in a sync that is running now.</summary>
    public bool IsQueued
    {
        get => isQueued;
        set
        {
            if (Set(ref isQueued, value))
            {
                RaiseAll();
            }
        }
    }

    /// <summary>Finished syncing a moment ago (shows a check for a minute).</summary>
    public bool JustSynced => syncedUntil is { } until && until > DateTimeOffset.Now;

    public void MarkSynced()
    {
        syncedUntil = DateTimeOffset.Now.AddSeconds(60);
        RaiseAll();
    }

    /// <summary>Called every second: the "just synced" check goes away after a minute.</summary>
    public void ExpireJustSynced(DateTimeOffset now)
    {
        if (syncedUntil is { } until && until <= now)
        {
            syncedUntil = null;
            RaiseAll();
        }
    }

    /// <summary>For the project list's status mark: syncing, queued, done, failed or nothing.</summary>
    public string SyncState =>
        IsSyncing || activeRefresh is { IsFinished: false } ? "syncing"
        : isQueued ? "queued"
        : JustSynced && LastReadFailed ? "failed"
        : JustSynced ? "done"
        : "";

    public void BeginSync()
    {
        syncDepth++;
        if (syncDepth == 1)
        {
            RaiseAll();
        }
    }

    public void EndSync()
    {
        if (syncDepth == 0)
        {
            return;
        }

        syncDepth--;
        if (syncDepth == 0)
        {
            RaiseAll();
        }
    }

    public string Subtitle
    {
        get
        {
            var part = PhaseText.Length > 0 ? $"{PhaseText} · " : "";
            if (project.Connection is null)
            {
                return $"{part}Not linked to Helix or Procore";
            }

            if (IsArchived)
            {
                return $"{part}{(project.Connection is null ? "" : ProviderLabel + CompanyPart + " · ")}archived";
            }

            if (IsRefreshing)
            {
                return $"{part}{ProviderLabel}{CompanyPart} · syncing now…";
            }

            if (isQueued)
            {
                return $"{part}{ProviderLabel}{CompanyPart} · waiting to sync…";
            }

            if (JustSynced && !LastReadFailed && snapshot is not null)
            {
                return $"{part}{ProviderLabel}{CompanyPart} · synced just now ({snapshot.Items.Count} items)";
            }

            if (IsSyncSkipped)
            {
                return $"{part}{ProviderLabel}{CompanyPart} · sync turned off";
            }

            if (LastReadFailed)
            {
                return $"{part}{ProviderLabel}{CompanyPart} · ⚠ last sync failed {Formatting.When(lastRun!.StartedAt)}";
            }

            if (IsPlaceholder && snapshot is null)
            {
                return $"{ProviderLabel}{CompanyPart} · waiting for the first sync";
            }

            return snapshot is null
                ? $"{part}{ProviderLabel} · not synced yet"
                : $"{part}{ProviderLabel}{CompanyPart} · synced {Formatting.When(snapshot.ScannedAt)}";
        }
    }

    public int AttentionCount
    {
        get => attentionCount;
        set
        {
            if (Set(ref attentionCount, value))
            {
                OnPropertyChanged(nameof(BadgeText));
                OnPropertyChanged(nameof(HasBadge));
            }
        }
    }

    public bool Compared
    {
        get => compared;
        set
        {
            if (Set(ref compared, value))
            {
                OnPropertyChanged(nameof(HasBadge));
            }
        }
    }

    private int? matchingCount;

    /// <summary>How many of this project's items match the list's current filters (people, due, type, …).</summary>
    public int? MatchingCount
    {
        get => matchingCount;
        set
        {
            if (Set(ref matchingCount, value))
            {
                OnPropertyChanged(nameof(BadgeText));
                OnPropertyChanged(nameof(HasBadge));
                OnPropertyChanged(nameof(BadgeToolTip));
                OnPropertyChanged(nameof(HasOverdue));
            }
        }
    }

    private int overdueCount;

    /// <summary>How many of those matching items are overdue (shown in red before the total).</summary>
    public int OverdueCount
    {
        get => overdueCount;
        set
        {
            if (Set(ref overdueCount, value))
            {
                OnPropertyChanged(nameof(OverdueText));
                OnPropertyChanged(nameof(HasOverdue));
            }
        }
    }

    public bool HasBadge => MatchingCount > 0;
    public string BadgeText => MatchingCount?.ToString() ?? "";
    public string BadgeToolTip => MatchingCount == 1 ? "1 item matches the filters above the item list" : $"{MatchingCount} items match the filters above the item list";
    public string OverdueText => $"{OverdueCount} overdue";
    public bool HasOverdue => OverdueCount > 0 && HasBadge;

    private void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Project), nameof(Snapshot), nameof(ActiveRefresh), nameof(Name), nameof(IsConnected),
                     nameof(ProviderLabel), nameof(ConnectionText), nameof(PhaseText), nameof(IsRefreshing), nameof(Subtitle),
                     nameof(HasCaFolder), nameof(GroupName), nameof(GroupOrder), nameof(IsPlaceholder),
                     nameof(IsSyncSkipped), nameof(ListToolTip), nameof(IsArchived), nameof(ArchiveKey), nameof(IsFavorite),
                     nameof(IsQueued), nameof(JustSynced), nameof(SyncState)
                 })
        {
            OnPropertyChanged(name);
        }
    }
}

public sealed class RowViewModel : ObservableObject
{
    private bool isChecked;

    public RowViewModel(ComparisonRow row, ProjectViewModel project, ProjectComparison comparison, DateTime today)
    {
        Row = row;
        Project = project;
        Comparison = comparison;
        var company = comparison.Company;
        var folders = comparison.Folders;
        CourtText = row.Item is null ? "" : CourtEvaluator.Describe(row.Court, company);
        Holders = ItemTiming.CurrentHolders(row.Item);
        HolderNames = Holders.Select(holder => holder.Name.Trim()).ToList();
        DaysInCourt = ItemTiming.DaysInCourt(row.Item, today);
        DaysTillDue = ItemTiming.DaysTillDue(row.Item, today);
        DueText = ItemTiming.DueText(row.Item, today);
        // Where the folder sits, e.g. "Submittals\Elec\BB" or "Submittals\Elec\~Returned".
        var typeName = FolderLayout.DisplayName(folders.TypeFolder(row.ItemType));
        LocationText = row.Folder is null ? ""
            : row.Folder.Location == FolderLocation.Returned && folders.ReturnedIsShared
                ? FolderLayout.DisplayName(folders.ReturnedFolder)
                : row.Folder.RelativeParent.Length == 0 ? typeName
                : System.IO.Path.IsPathRooted(row.Folder.RelativeParent) ? row.Folder.RelativeParent
                : $"{typeName}\\{row.Folder.RelativeParent}";
    }

    public ComparisonRow Row { get; }

    /// <summary>The project this row belongs to (several can be shown at once).</summary>
    public ProjectViewModel Project { get; }
    public ProjectComparison Comparison { get; }
    public string ProjectName => Project.Name;
    /// <summary>"RFIs" / "Submittals", for the list's headings.</summary>
    public string TypeLabel => ItemTypes.Plural(Row.ItemType);

    /// <summary>"RFI" / "Submittal", for the Type column.</summary>
    public string TypeSingular => ItemTypes.Singular(Row.ItemType);

    /// <summary>People the item is with now.</summary>
    public IReadOnlyList<SnapshotHolder> Holders { get; }
    public IReadOnlyList<string> HolderNames { get; }

    /// <summary>Who has it: names, or the court summary when the site doesn't name anyone.</summary>
    public string HolderText => HolderNames.Count > 0 ? string.Join(", ", HolderNames) : Row.Item is null ? "" : Row.Item.Closed ? "Closed" : CourtText;

    /// <summary>Sorts people A–Z, with items that have nobody named last.</summary>
    public string PersonSortKey => HolderNames.Count > 0 ? HolderNames[0] : "\uffff";

    public int? DaysInCourt { get; }
    public string DaysInCourtText => DaysInCourt is { } days ? days == 1 ? "1 day" : $"{days} days" : "";

    public int? DaysTillDue { get; }
    /// <summary>The Due column: date and countdown, "Sep 7 · 26 days overdue".</summary>
    public string DueText { get; }

    /// <summary>Hover text for the Due column: the full date with its weekday.</summary>
    public string DueToolTip => Row.Item?.DueAt is { } due ? $"Due {due.ToLocalTime():dddd, MMMM d, yyyy}" : "No due date";

    public bool IsOverdue => DaysTillDue < 0;
    public bool IsDueToday => DaysTillDue == 0;

    public bool IsChecked
    {
        get => isChecked;
        set => Set(ref isChecked, value && CanSelect);
    }

    /// <summary>Rows with a folder (for moves) or an item (for presetting its discipline) can be checked.</summary>
    public bool CanSelect => Row.Folder is not null || Row.Item is not null;
    public RowStatus Status => Row.Status;
    public string StatusText => ComparisonRow.StatusLabel(Row.Status);
    /// <summary>The item's number, with its revision past the first ("03 30 00-1 Rev 2").</summary>
    public string Number => Row.Item is not { } item ? "—"
        : string.IsNullOrWhiteSpace(item.Revision) || item.Revision == "0" ? item.Number
        : $"{item.Number} Rev {item.Revision}";
    public string Title => Row.Item?.Title ?? "";
    public string ProviderStatus => Row.Item?.Status ?? "";
    public string CourtText { get; }
    public string FolderName => Row.Folder?.Name ?? "—";
    public string LocationText { get; }
    public string Detail => Row.Detail;
    /// <summary>The discipline; "(auto)" when it was detected rather than set by hand.</summary>
    public string DisciplineText => Row.Discipline is { } discipline
        ? Row.DisciplineSource == DisciplineSource.Detected ? $"{discipline.Label} (auto)" : discipline.Label
        : Row.DisciplineSource == DisciplineSource.Manual ? "None" : "—";

    /// <summary>Hover text for the Folder column: where the folder is, and that double-click opens it.</summary>
    public string? FolderToolTip => Row.Folder is null ? null : $"{LocationText}\nDouble-click the row to open this folder.";
    public bool DisciplineIsDetected => Row.DisciplineSource == DisciplineSource.Detected;

    public string DisciplineToolTip => Row.DisciplineSource switch
    {
        DisciplineSource.Manual => Row.Folder is null ? "Set by hand; downloads go into this discipline's folder" : "Set by hand",
        DisciplineSource.Detected => Row.Folder is null ? "Detected from the item title"
            : Row.Folder.Groups.Count > 0 && Disciplines.Detect(Row.Folder.Name) is null ? $"From the folder it's in ({string.Join("\\", Row.Folder.Groups)})"
            : "Detected from the folder name",
        _ => "Not detected. Use Set to choose one."
    };
    public bool HasFolder => Row.Folder is not null;
    public bool HasItem => Row.Item is not null;

    /// <summary>Downloads work for Helix items only.</summary>
    public bool CanDownload => Row.Item is not null
        && string.Equals(Project.Project.Connection?.Provider, Providers.Helix, StringComparison.OrdinalIgnoreCase);

    public string DownloadToolTip => CanDownload
        ? "Choose files to download into a \"number - name\" folder"
        : Row.Item is null ? "No Helix or Procore item for this folder" : "Downloads work for Helix items only";

    /// <summary>Hover text for the whole row: what double-clicking does.</summary>
    public string RowToolTip => HasFolder ? "Double-click to open this folder" : HasItem ? "Double-click to open this item" : "";
    public string? SourceUrl => Row.Item?.SourceUrl;

    public bool HasLink => Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public string ToolTipText => string.IsNullOrEmpty(Detail) ? StatusText : $"{StatusText}. {Detail}";
}

public sealed class OperationRowViewModel
{
    public OperationRowViewModel(PlannedOperation operation, OperationResult? result)
    {
        Operation = operation;
        Glyph = operation.Kind == OperationKind.CreateFolder ? "+" : "→";
        Title = operation.Title;
        FromText = operation.SourcePath is null ? "" : $"From  {operation.SourcePath}";
        ToText = operation.Kind == OperationKind.CreateFolder ? $"Create  {operation.DestinationPath}" : $"To      {operation.DestinationPath}";
        Warning = result is null ? operation.Warning : null;
        Blocked = result is null && operation.IsBlocked ? $"Will be skipped: {operation.BlockedReason}" : null;
        if (result is not null)
        {
            Outcome = result.Outcome switch
            {
                OperationOutcome.Done => $"Done — {result.Message}",
                OperationOutcome.Skipped => $"Skipped — {result.Message}",
                _ => $"Failed — {result.Message}"
            };
            OutcomeKind = result.Outcome.ToString();
        }
    }

    public PlannedOperation Operation { get; }
    public string Glyph { get; }
    public string Title { get; }
    public string FromText { get; }
    public string ToText { get; }
    public string? Warning { get; }
    public string? Blocked { get; }
    public string? Outcome { get; }
    public string OutcomeKind { get; } = "";
    public bool HasFrom => FromText.Length > 0;
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public bool HasBlocked => !string.IsNullOrEmpty(Blocked);
    public bool HasOutcome => !string.IsNullOrEmpty(Outcome);
}

public static class Formatting
{
    public static string When(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return local.Date == DateTime.Today
            ? $"today {local:h:mm tt}"
            : local.Date == DateTime.Today.AddDays(-1)
                ? $"yesterday {local:h:mm tt}"
                : $"{local:MMM d, h:mm tt}";
    }
}

/// <summary>One site (Helix or Procore) on the Connections page, the sign-in banner and the Sync all menu.</summary>
public sealed class ConnectionViewModel : ObservableObject
{
    private string statusText = "";
    private string lastSyncedText = "";
    private string projectsText = "";
    private int projectCount;
    private bool isSignedIn;
    private bool isSyncing;
    private string syncMessage = "";
    private bool needsSignIn;
    private bool hasSynced;
    private int failedCount;
    private string failedText = "";
    private int busyDepth;
    private double? progress;

    public ConnectionViewModel(string provider)
    {
        Provider = provider;
    }

    public string Provider { get; }
    public string Label => Providers.Label(Provider);

    /// <summary>Set while a sync is running; Stop asks it to finish after the current project.</summary>
    public bool StopRequested { get; set; }

    public string StatusText
    {
        get => statusText;
        set => Set(ref statusText, value);
    }

    public string LastSyncedText
    {
        get => lastSyncedText;
        set => Set(ref lastSyncedText, value);
    }

    public string ProjectsText
    {
        get => projectsText;
        set => Set(ref projectsText, value);
    }

    public int ProjectCount
    {
        get => projectCount;
        set
        {
            if (Set(ref projectCount, value))
            {
                OnPropertyChanged(nameof(CanSync));
                OnPropertyChanged(nameof(WantsSignIn));
            }
        }
    }

    public bool IsSignedIn
    {
        get => isSignedIn;
        set
        {
            if (Set(ref isSignedIn, value))
            {
                OnPropertyChanged(nameof(ConnectButtonText));
                OnPropertyChanged(nameof(ConnectButtonToolTip));
                OnPropertyChanged(nameof(CanSync));
                RaiseState();
            }
        }
    }

    /// <summary>Signed in before, but that sign-in has expired.</summary>
    public bool NeedsSignIn
    {
        get => needsSignIn;
        set
        {
            if (Set(ref needsSignIn, value))
            {
                RaiseState();
            }
        }
    }

    /// <summary>At least one of its projects has been read.</summary>
    public bool HasSynced
    {
        get => hasSynced;
        set
        {
            if (Set(ref hasSynced, value))
            {
                RaiseState();
            }
        }
    }

    /// <summary>Projects whose latest read failed, with nothing read since.</summary>
    public int FailedCount
    {
        get => failedCount;
        set
        {
            if (Set(ref failedCount, value))
            {
                RaiseState();
            }
        }
    }

    /// <summary>Which projects couldn't be read, shown under the card when nothing is running.</summary>
    public string FailedText
    {
        get => failedText;
        set
        {
            if (Set(ref failedText, value))
            {
                OnPropertyChanged(nameof(HasFailedText));
            }
        }
    }

    public bool HasFailedText => !IsBusy && !string.IsNullOrWhiteSpace(FailedText);

    /// <summary>
    /// Its projects are being read now, by Sync, a Refresh or the background sync. Counted, so
    /// overlapping reads don't end each other.
    /// </summary>
    public void BeginBusy()
    {
        if (++busyDepth == 1)
        {
            Progress = null;
            RaiseState();
        }
    }

    public void EndBusy()
    {
        if (busyDepth > 0 && --busyDepth == 0)
        {
            RaiseState();
        }
    }

    public bool IsBusy => IsSyncing || busyDepth > 0;

    /// <summary>0–100 across the projects being read; null until that is known (finding projects, waiting a turn).</summary>
    public double? Progress
    {
        get => progress;
        set
        {
            if (Set(ref progress, value))
            {
                OnPropertyChanged(nameof(ProgressValue));
                OnPropertyChanged(nameof(IsProgressUnknown));
            }
        }
    }

    public double ProgressValue => progress ?? 0;
    public bool IsProgressUnknown => IsBusy && progress is null;

    /// <summary>For the status badge: syncing, signin, off, failed, synced or idle.</summary>
    public string State =>
        IsBusy ? "syncing"
        : NeedsSignIn ? "signin"
        : !IsSignedIn ? "off"
        : FailedCount > 0 ? "failed"
        : HasSynced ? "synced"
        : "idle";

    public string StateText => State switch
    {
        "syncing" => "Syncing",
        "signin" => "Sign-in needed",
        "off" => "Not connected",
        "failed" => FailedCount == 1 ? "1 project not read" : $"{FailedCount} projects not read",
        "synced" => "Synced",
        _ => "Not synced yet"
    };

    private void RaiseState()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(State), nameof(StateText), nameof(HasFailedText), nameof(IsProgressUnknown), nameof(WantsSignIn) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>The card's main button: Sign in, or Sign out while signed in.</summary>
    public string ConnectButtonText => IsSignedIn ? $"Sign out of {Label}…" : $"Sign in to {Label}";

    public string ConnectButtonToolTip => IsSignedIn
        ? $"Signs CA Tracker's browser out of {Label}; your saved username and password are kept"
        : $"Opens {Label} in CA Tracker's browser to sign in once";
    public string SignInText => $"Sign in to {Label}";

    /// <summary>For the main window's banner: not signed in, but its sign-in expired or it still has projects to sync.</summary>
    public bool WantsSignIn => !IsSignedIn && (NeedsSignIn || ProjectCount > 0);

    public bool IsSyncing
    {
        get => isSyncing;
        set
        {
            if (Set(ref isSyncing, value))
            {
                if (value)
                {
                    Progress = null;
                }

                OnPropertyChanged(nameof(CanSync));
                OnPropertyChanged(nameof(IsNotSyncing));
                RaiseState();
            }
        }
    }

    public bool IsNotSyncing => !IsSyncing;
    /// <summary>Needs a sign-in. Procore can sync with no projects listed yet: Sync first finds them.</summary>
    public bool CanSync => !IsSyncing && IsSignedIn
        && (ProjectCount > 0 || string.Equals(Provider, Providers.Procore, StringComparison.OrdinalIgnoreCase));

    public string SyncMessage
    {
        get => syncMessage;
        set
        {
            if (Set(ref syncMessage, value))
            {
                OnPropertyChanged(nameof(HasSyncMessage));
            }
        }
    }

    public bool HasSyncMessage => !string.IsNullOrWhiteSpace(SyncMessage);
}

/// <summary>One name (or "Anyone at &lt;company&gt;") in the Person filter.</summary>
public sealed class PersonChoiceViewModel : ObservableObject
{
    private bool isChecked;

    public PersonChoiceViewModel(string name, bool isCompany)
    {
        Name = name;
        IsCompany = isCompany;
    }

    public string Name { get; }
    public bool IsCompany { get; }

    /// <summary>Checked earlier but holding nothing in the shown projects (listed greyed, still filtering).</summary>
    public bool IsAbsent { get; init; }

    /// <summary>You: the name you're signed in to Helix or Procore as.</summary>
    public bool IsYou { get; init; }

    public string Label => IsYou && IsAbsent ? $"{Name} (you, nothing here)"
        : IsYou ? $"{Name} (you)"
        : IsAbsent ? $"{Name} (nothing here)"
        : Name;

    public bool IsChecked
    {
        get => isChecked;
        set => Set(ref isChecked, value);
    }
}
