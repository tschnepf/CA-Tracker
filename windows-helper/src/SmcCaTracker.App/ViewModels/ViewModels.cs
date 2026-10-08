using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Operations;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;

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
    /// under "No CA folder yet" and is never saved: the first change in its Project settings saves a
    /// tracker project linked to it in its place.
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

            if (snapshot is not null)
            {
                lines.Add($"Synced {Formatting.When(snapshot.ScannedAt)}");
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

    private string nextDue = "";

    /// <summary>"next due Oct 7": the soonest due date, today or later, among its items that match the list's filters.</summary>
    public string NextDueText
    {
        get => nextDue;
        set
        {
            if (Set(ref nextDue, value))
            {
                OnPropertyChanged(nameof(Subtitle));
            }
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

            // When it was synced is in the tooltip (and the pill above the list); this says what's coming up.
            return snapshot is null
                ? $"{part}{ProviderLabel} · not synced yet"
                : $"{part}{ProviderLabel}{CompanyPart}{(nextDue.Length > 0 ? $" · {nextDue}" : "")}";
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

    private string matchingNoun = "open";

    /// <summary>What the count is of, after the Show filter: "open", "items", "ready", …</summary>
    public string MatchingNoun
    {
        get => matchingNoun;
        set
        {
            if (Set(ref matchingNoun, value))
            {
                OnPropertyChanged(nameof(BadgeText));
            }
        }
    }

    public bool HasBadge => MatchingCount > 0;
    public string BadgeText => MatchingCount is { } count ? $"{count} {matchingNoun}" : "";
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
        // Names without a company in brackets, so a person Procore writes both ways is one person.
        HolderNames = Holders.Select(holder => PersonNames.WithoutCompany(holder.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        DaysInCourt = ItemTiming.DaysInCourt(row.Item, today);
        DaysTillDue = ItemTiming.DaysTillDue(row.Item, today);
        DueText = ItemTiming.DueText(row.Item, today);
        Urgency = ItemTiming.UrgencyOf(row.Item, today);
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

    /// <summary>The "When it's due" heading this row is listed under.</summary>
    public Urgency Urgency { get; }
    public string UrgencyText => ItemTiming.UrgencyLabel(Urgency);

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

    /// <summary>The Title column: the item's title, or the folder's name for a folder with no item.</summary>
    public string TitleText => Row.Item?.Title ?? Row.Folder?.Name ?? "";

    /// <summary>Hover text for the Title column: the title and where its folder is (the Folder column starts hidden).</summary>
    public string TitleToolTip => Row.Folder is null ? Title
        : Row.Item is null ? $"A folder with no Helix or Procore item: {LocationText}\\{Row.Folder.Name}"
        : $"{Title}\nFolder: {LocationText}\\{Row.Folder.Name}\nDouble-click the row (or press Enter) to open the folder.";
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

    /// <summary>Identifies the row across list rebuilds: its project, type, and folder or item.</summary>
    public string Key => $"{Project.Id}|{Row.ItemType}|{Row.Folder?.FullPath ?? "item:" + Row.Item?.ItemId}";

    /// <summary>The details pane's first line: "RFI 000252 · Aligned CMH03 · Helix".</summary>
    public string DetailsCrumb => string.Join(" · ", new[] { HasItem ? $"{TypeSingular} {Number}" : $"{TypeSingular} folder", ProjectName, Project.ProviderLabel }
        .Where(part => part.Length > 0));

    /// <summary>Who has it now and for how long, for the details pane.</summary>
    public string WithText => HolderNames.Count == 0 ? (Row.Item is null ? "—" : HolderText)
        : DaysInCourt is null ? string.Join(", ", HolderNames)
        : $"{string.Join(", ", HolderNames)} · {DaysInCourtText}";

    /// <summary>Who has the next step (Helix's "upcoming" people), for the details pane.</summary>
    public string NextText => Row.Item is not { Closed: false } item ? "" : string.Join(", ", item.Holders
        .Where(holder => string.Equals(holder.State, "upcoming", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(holder.Name))
        .Select(holder => PersonNames.WithoutCompany(holder.Name))
        .Distinct(StringComparer.OrdinalIgnoreCase));

    public bool HasNext => NextText.Length > 0;

    /// <summary>Where its folder is ("Submittals\Elec\000437 - …"), or that it has none.</summary>
    public string FolderPathText => Row.Folder is null ? "No folder yet" : $"{LocationText}\\{Row.Folder.Name}";

    /// <summary>Its folder status is worth a line in the details pane (more than "Open" or "No folder").</summary>
    public bool HasFolderNote => Row.Status is not (RowStatus.Open or RowStatus.NoFolder);

    /// <summary>Hover text for the row's Open Online: where the item opens (a folder with no item has nothing to open online).</summary>
    public string OpenToolTip => CanDownload
        ? "Open it in Helix, in the panel on the right"
        : HasItem ? $"Open it in {Project.ProviderLabel} (in your web browser)"
        : "No Helix or Procore item for this folder. ▾ → Open folder opens the folder.";

    /// <summary>"Open in Helix" / "Open in Procore" in the row's menu.</summary>
    public string OpenOnlineText => Project.ProviderLabel.Length > 0 ? $"Open in {Project.ProviderLabel}" : "Open online";

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

    // ---- Workload Tracker assignments (decision D4) ------------------------------------------

    public const string AssignedToMeHeading = "Assigned to me";

    /// <summary>The item as Workload Tracker names it, when its project is bound; null when it can't be assigned.</summary>
    public WorkloadItemRef? AssignRef { get; private set; }

    /// <summary>Who is assigned to the item in Workload Tracker, names in order.</summary>
    public IReadOnlyList<WorkloadAssignment> Assignees { get; private set; } = [];

    /// <summary>Assigned to you (the Workload Tracker person you're connected as) and not done.</summary>
    public bool IsAssignedToMe { get; private set; }

    public bool CanAssign => AssignRef is not null;

    /// <summary>Connected to Workload Tracker: the row's menus offer Assign to… (enabled once the project is bound).</summary>
    public bool ShowsAssignMenu { get; private set; }

    public string AssignMenuToolTip => CanAssign
        ? "Choose who on the project's team in Workload Tracker works on this item"
        : HasItem ? "Bind this project to a Workload Tracker project first (Project settings)" : "Only Helix and Procore items can be assigned";

    /// <summary>The Assigned column: names, with people who have left marked so the item can be reassigned.</summary>
    public string AssignedText => string.Join(", ", Assignees.Select(row => row.PersonActive ? row.PersonName : $"{row.PersonName} (left)"));

    public string AssignedSortKey => Assignees.Count > 0 ? Assignees[0].PersonName : "\uffff";

    /// <summary>Hover text for the Assigned column: who assigned each person and when, and what isn't sent yet.</summary>
    public string? AssignedToolTip => Assignees.Count == 0
        ? CanAssign ? "Nobody is assigned. Right-click → Assign to… to assign someone." : null
        : string.Join("\n", Assignees.Select(row =>
        {
            var by = row.AssignedByName is { Length: > 0 } name ? $" by {name}" : "";
            var note = row.Pending ? " (not sent yet)"
                : !row.PersonActive ? " · has left: reassign it"
                : row.CompletedAt is not null ? " · done" : "";
            return $"{row.PersonName}: assigned{by} {row.AssignedAt.ToLocalTime():MMM d}{note}";
        }));

    /// <summary>Set by the list: My work shows items assigned to you first, under their own heading.</summary>
    public bool ListAssignedFirst { get; set; }

    /// <summary>The heading the list groups this row under when grouped by when it's due.</summary>
    public string Heading => ListAssignedFirst && IsAssignedToMe ? AssignedToMeHeading : UrgencyText;

    /// <summary>The order of <see cref="Heading"/>: "Assigned to me" first, then by when it's due.</summary>
    public int HeadingOrder => ListAssignedFirst && IsAssignedToMe ? -1 : (int)Urgency;

    /// <summary>The details pane's Assigned line: shown once the item can be assigned (or someone is).</summary>
    public bool ShowsAssignedDetails => CanAssign || Assignees.Count > 0;

    public string AssignedDetailsText => Assignees.Count == 0 ? "Nobody yet" : AssignedText;

    // ---- Marked complete in Workload Tracker -------------------------------------------------

    /// <summary>The pill after the names on items marked complete in Workload Tracker and not returned yet.</summary>
    public const string DoneFlagText = "Done in Workload Tracker";

    /// <summary>Who marked this revision of the item complete in Workload Tracker, names in order.</summary>
    public IReadOnlyList<WorkloadCompletion> Completions { get; private set; } = [];

    public bool HasCompletions => Completions.Count > 0;

    /// <summary>Marked complete in Workload Tracker, but still open in Helix or Procore and not returned: flagged.</summary>
    public bool ShowsDoneFlag => WorkloadCompletion.AwaitsReturn(Row, Completions);

    /// <summary>Hover text for the pill: who marked it and when, and that it still needs returning.</summary>
    public string? DoneFlagToolTip => ShowsDoneFlag
        ? $"Marked complete in Workload Tracker by {MarkedByText} on {LastMarkedText}. Not returned in {ProviderName} yet."
        : null;

    /// <summary>The details pane's Workload Tracker line: "Marked complete by Pat Lead on Oct 7".</summary>
    public string CompletedText => HasCompletions ? $"Marked complete by {MarkedByText} on {LastMarkedText}" : "";

    private string MarkedByText => string.Join(", ", Completions.Select(mark => mark.PersonName));

    private string LastMarkedText => HasCompletions ? $"{Completions.Max(mark => mark.MarkedAt).ToLocalTime():MMM d}" : "";

    private string ProviderName => Project.ProviderLabel.Length > 0 ? Project.ProviderLabel : "Helix or Procore";

    public void SetAssignments(WorkloadItemRef? reference, IReadOnlyList<WorkloadAssignment> assignees, int? me, bool paired,
        IReadOnlyList<WorkloadCompletion> completions)
    {
        var mine = me is { } person && assignees.Any(row => row.PersonId == person && row.CompletedAt is null);
        if (reference == AssignRef && mine == IsAssignedToMe && paired == ShowsAssignMenu
            && assignees.SequenceEqual(Assignees, AssignmentComparer.Instance)
            && completions.SequenceEqual(Completions, CompletionComparer.Instance))
        {
            return;
        }

        ShowsAssignMenu = paired;
        AssignRef = reference;
        Assignees = assignees;
        IsAssignedToMe = mine;
        Completions = completions;
        foreach (var name in new[]
                 {
                     nameof(AssignRef), nameof(Assignees), nameof(IsAssignedToMe), nameof(CanAssign), nameof(AssignedText),
                     nameof(ShowsAssignMenu), nameof(AssignMenuToolTip),
                     nameof(AssignedSortKey), nameof(AssignedToolTip), nameof(Heading), nameof(HeadingOrder),
                     nameof(ShowsAssignedDetails), nameof(AssignedDetailsText),
                     nameof(Completions), nameof(HasCompletions), nameof(ShowsDoneFlag), nameof(DoneFlagToolTip), nameof(CompletedText)
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Two assignment lists show the same thing.</summary>
    private sealed class AssignmentComparer : IEqualityComparer<WorkloadAssignment>
    {
        public static readonly AssignmentComparer Instance = new();

        public bool Equals(WorkloadAssignment? x, WorkloadAssignment? y) =>
            x is not null && y is not null && x.Key == y.Key && x.Active == y.Active && x.Pending == y.Pending
            && x.PersonName == y.PersonName && x.PersonActive == y.PersonActive && x.CompletedAt == y.CompletedAt
            && x.AssignedAt == y.AssignedAt && x.AssignedByName == y.AssignedByName;

        public int GetHashCode(WorkloadAssignment obj) => obj.Key.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>Two lists of "marked complete" marks show the same thing.</summary>
    private sealed class CompletionComparer : IEqualityComparer<WorkloadCompletion>
    {
        public static readonly CompletionComparer Instance = new();

        public bool Equals(WorkloadCompletion? x, WorkloadCompletion? y) =>
            x is not null && y is not null && x.Key == y.Key && x.PersonName == y.PersonName
            && x.MarkedAt == y.MarkedAt && x.MarkedByName == y.MarkedByName && x.Revision == y.Revision;

        public int GetHashCode(WorkloadCompletion obj) => obj.Key.GetHashCode(StringComparison.Ordinal);
    }
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

/// <summary>A watchlist in the project list's VIEWS: its name, what it covers, its counts, and whether it is showing.</summary>
public sealed class WatchlistItemViewModel : ObservableObject
{
    private int open;
    private int overdue;
    private bool isActive;

    public WatchlistItemViewModel(Watchlist watchlist)
    {
        Watchlist = watchlist;
    }

    public Watchlist Watchlist { get; }
    public string Name => Watchlist.Name;

    public string Summary
    {
        get
        {
            var people = Watchlist.EveryPerson().Count;
            var projects = Watchlist.People.Count;
            return $"{people} {(people == 1 ? "person" : "people")} · {projects} {(projects == 1 ? "project" : "projects")}";
        }
    }

    public string OpenText => $"{open} open";
    public bool HasOpen => open > 0;
    public string OverdueText => $"{overdue} overdue";
    public bool HasOverdue => overdue > 0;

    /// <summary>The list is filtered by this watchlist.</summary>
    public bool IsActive
    {
        get => isActive;
        set => Set(ref isActive, value);
    }

    public void SetCounts(int openCount, int overdueCount)
    {
        if (open == openCount && overdue == overdueCount)
        {
            return;
        }

        open = openCount;
        overdue = overdueCount;
        foreach (var name in new[] { nameof(OpenText), nameof(HasOpen), nameof(OverdueText), nameof(HasOverdue) })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>Someone who can be watched on a project: their company, whether it is the project's company, and what they hold there now.</summary>
public sealed record WatchPerson(string Name, string? Company, bool IsOurs, int Open, int Overdue);

/// <summary>
/// A project a watchlist can cover: its watchlist key (and the key it had before its CA folder was chosen, which
/// older watchlists may use), its name and company, and the people who hold or are next on its open items.
/// </summary>
public sealed record WatchProject(string Key, string? OtherKey, string Name, string? CompanyName, IReadOnlyList<WatchPerson> People);

/// <summary>An item on the team board: where it is, what it is, and when it's due.</summary>
public sealed record BoardItem(string Project, string Kind, string Number, string Title, string DueText, int? DaysTillDue, bool IsOverdue);

/// <summary>A person on a project on the team board: what they hold there now (only for pairs the watchlist follows).</summary>
public sealed record BoardCell(int Open, int Overdue, IReadOnlyList<BoardItem> Items)
{
    public bool IsWatched => true;
    public bool HasOverdue => Overdue > 0;
    public string Text => Open == 0 ? "nothing now" : Overdue > 0 ? $"{Overdue} overdue · {Open} open" : $"{Open} open";
}

/// <summary>A person on the team board: a cell per project (null where they aren't watched), and their totals.</summary>
public sealed record BoardPerson(string Name, IReadOnlyList<BoardCell?> Cells, int Open, int Overdue)
{
    public bool HasOverdue => Overdue > 0;
    public string TotalText => Open == 0 ? "nothing now" : Overdue > 0 ? $"{Overdue} overdue · {Open} open" : $"{Open} open";
}

/// <summary>A project column on the team board, by its watchlist key, with its totals.</summary>
public sealed record BoardProject(string Key, string Name, int Open, int Overdue);

/// <summary>A watchlist as a grid of people by project.</summary>
public sealed record TeamBoard(string Name, IReadOnlyList<BoardProject> Projects, IReadOnlyList<BoardPerson> People)
{
    public int Open => People.Sum(person => person.Open);
    public int Overdue => People.Sum(person => person.Overdue);
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
