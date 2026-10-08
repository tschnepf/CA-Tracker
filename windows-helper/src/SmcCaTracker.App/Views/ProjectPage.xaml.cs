using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.App.Views;

public sealed record CompanyOption(string? Id, string? Name, string Label);

/// <summary>What a save hands the main window: the project as edited, and what changed for Workload Tracker.</summary>
public sealed record ProjectSaved(TrackerProject Project, bool BindingChanged, TrackerProjectChoice? Binding, bool CoverageChanged);

/// <summary>
/// A project's settings, shown in place of its item list while the project list stays beside it, so
/// several projects can be set up one after another. Every change saves on its own a moment later (a
/// change that can't be saved yet says why), so moving to another project never loses anything. It
/// changes only the tracker's settings, never the CA folder. While connected to Workload Tracker it also
/// binds the project and links the Workload Tracker project's team to who they are in Helix or Procore.
/// </summary>
public partial class ProjectPage : UserControl
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly TeamOptions teamOptions = new();
    private readonly DispatcherTimer autoSave = new() { Interval = SaveDelay };
    private MainViewModel viewModel = null!;
    private TrackerProject project = new();
    private TrackerProject saved = new();
    private ProjectViewModel? placeholder;
    private WorkloadBinding? savedBinding;
    private TrackerProjectChoice? chosenBinding;
    private string baseline = "";
    private bool loading;
    private Task<bool>? saveInFlight;
    private int teamVersion;
    private string? teamLoadedFor;
    private Dictionary<string, CaPersonLink> teamLinks = new(StringComparer.Ordinal);
    private List<ProviderPerson> providerPeople = new();
    private List<TeamLinkRow> teamRows = new();

    public ProjectPage()
    {
        InitializeComponent();
        TeamList.DataContext = teamOptions;
        autoSave.Tick += async (_, _) => await SaveIfValidAsync();
        foreach (var box in new[] { NameBox, FolderBox, RfiBox, SubmittalBox, ReturnedBox })
        {
            box.TextChanged += (_, _) => OnEdited();
        }

        foreach (var box in new[] { OverrideFoldersBox, FilterItemsBox, FilterFoldersBox, IncludeUnlabeledBox })
        {
            box.Checked += (_, _) => OnEdited();
            box.Unchecked += (_, _) => OnEdited();
        }

        foreach (var radio in new[] { PhaseWhole, PhaseBaseBuild, PhaseFitOut })
        {
            radio.Checked += (_, _) => OnEdited();
        }

        CompanyList.SelectionChanged += (_, _) => OnEdited();
    }

    /// <summary>The Helix and Procore people every team row's picker offers.</summary>
    public sealed class TeamOptions
    {
        public ObservableCollection<ProviderOption> Options { get; } = new();
    }

    /// <summary>A change is ready to keep: the main window saves the project (and binds it).</summary>
    public event Func<ProjectSaved, Task>? SaveRequested;

    /// <summary>Back to the item list (Close, or Esc).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Remove from tracker was confirmed for this project.</summary>
    public event EventHandler<string>? RemoveRequested;

    /// <summary>Set up a Helix or Procore project that has no CA folder yet.</summary>
    public event EventHandler<ProjectViewModel>? SetUpRequested;

    public bool IsDirty => placeholder is null && !loading && Signature() != baseline;

    /// <summary>The project (or not-set-up Helix/Procore project) whose settings are showing.</summary>
    public string ShownId => placeholder?.Id ?? project.Id;

    /// <summary>Shows a project's settings (on a copy; saved changes go back through <see cref="SaveRequested"/>).</summary>
    public void Open(TrackerProject projectToEdit, MainViewModel model)
    {
        autoSave.Stop();
        loading = true;
        viewModel = model;
        placeholder = null;
        project = Clone(projectToEdit);
        saved = Clone(projectToEdit);
        savedBinding = model.WorkloadBindingFor(project.Id);
        chosenBinding = savedBinding is null ? null
            : new TrackerProjectChoice(savedBinding.ProjectId, savedBinding.ProjectNumber, savedBinding.ProjectName, null, null);
        teamLoadedFor = null;
        teamRows = new List<TeamLinkRow>();
        TeamList.ItemsSource = null;
        TeamPanel.Visibility = Visibility.Collapsed;
        NotSetUpPanel.Visibility = Visibility.Collapsed;
        SettingsScroll.Visibility = Visibility.Visible;
        RemoveButton.Visibility = Visibility.Visible;

        NameBox.Text = project.Name;
        FolderBox.Text = project.CaFolder;
        ShowConnection();
        LoadCompanies();

        var folders = project.FoldersOverride;
        OverrideFoldersBox.IsChecked = folders is not null;
        var effective = folders ?? model.Settings.Folders;
        RfiBox.Text = effective.RfiFolder;
        SubmittalBox.Text = effective.SubmittalFolder;
        ReturnedBox.Text = effective.ReturnedFolder;
        OverrideGrid.IsEnabled = folders is not null;

        var scope = project.Scope ?? new PhaseScope();
        PhaseWhole.IsChecked = scope.Phase == ProjectPhase.Whole;
        PhaseBaseBuild.IsChecked = scope.Phase == ProjectPhase.BaseBuild;
        PhaseFitOut.IsChecked = scope.Phase == ProjectPhase.TenantFitOut;
        FilterItemsBox.IsChecked = scope.FilterItems;
        FilterFoldersBox.IsChecked = scope.FilterFolders;
        IncludeUnlabeledBox.IsChecked = scope.IncludeUnlabeled;
        PhaseOptions.IsEnabled = scope.IsSplit;

        TitleText.Text = $"Project settings — {project.Name}";
        SetStatus("Changes save as you make them.", null);
        loading = false;
        baseline = Signature();
    }

    /// <summary>A Helix or Procore project with no CA folder yet: nothing to set until it is set up.</summary>
    public void OpenPlaceholder(ProjectViewModel notSetUp, MainViewModel model)
    {
        autoSave.Stop();
        viewModel = model;
        placeholder = notSetUp;
        teamVersion++;
        TitleText.Text = $"Project settings — {notSetUp.Name}";
        NotSetUpText.Text = $"{notSetUp.Name} isn't set up in CA Tracker yet: it has no CA folder. Set it up to choose its folder, "
            + "its part of the job and its Workload Tracker project.";
        NotSetUpPanel.Visibility = Visibility.Visible;
        SettingsScroll.Visibility = Visibility.Collapsed;
        RemoveButton.Visibility = Visibility.Collapsed;
        SetStatus("", null);
    }

    /// <summary>Every field the page edits, to tell whether anything changed since it was opened or saved.</summary>
    private string Signature() => string.Join("\u001f",
        NameBox.Text.Trim(), FolderBox.Text.Trim(), project.Connection?.Provider, project.Connection?.ProjectId, chosenBinding?.Id,
        (CompanyList.SelectedItem as CompanyOption)?.Id, (CompanyList.SelectedItem as CompanyOption)?.Name,
        OverrideFoldersBox.IsChecked, RfiBox.Text.Trim(), SubmittalBox.Text.Trim(), ReturnedBox.Text.Trim(),
        SelectedPhase, FilterItemsBox.IsChecked, FilterFoldersBox.IsChecked, IncludeUnlabeledBox.IsChecked);

    /// <summary>Something changed: it is saved a moment later, once typing pauses.</summary>
    private void OnEdited()
    {
        if (loading || placeholder is not null)
        {
            return;
        }

        autoSave.Stop();
        if (IsDirty)
        {
            autoSave.Start();
        }
    }

    // ---- Saving ------------------------------------------------------------------------------------

    private async Task<bool> SaveIfValidAsync()
    {
        autoSave.Stop();
        if (!IsDirty)
        {
            return true;
        }

        var problems = Validate();
        if (problems.Count > 0)
        {
            SetStatus($"Not saved yet: {string.Join(" ", problems)}", "DangerBrush");
            return false;
        }

        return await SaveAsync();
    }

    /// <summary>
    /// Saves what is on the page through the main window. A save asked for while one is going waits for
    /// it; changes made during a save are saved after it.
    /// </summary>
    private async Task<bool> SaveAsync()
    {
        if (saveInFlight is { } pending)
        {
            await pending;
            return await SaveIfValidAsync();
        }

        var save = SaveOnceAsync();
        if (save.IsCompleted)
        {
            return await save;
        }

        saveInFlight = save;
        try
        {
            return await save;
        }
        finally
        {
            saveInFlight = null;
        }
    }

    private async Task<bool> SaveOnceAsync()
    {
        var signature = Signature();
        var company = CompanyList.SelectedItem as CompanyOption;
        var result = Clone(project);
        result.Name = NameBox.Text.Trim();
        result.CaFolder = FolderBox.Text.Trim();
        result.CompanyId = company?.Id;
        result.CompanyName = company?.Name;
        result.FoldersOverride = OverrideFoldersBox.IsChecked == true
            ? new FolderLayout { RfiFolder = RfiBox.Text.Trim(), SubmittalFolder = SubmittalBox.Text.Trim(), ReturnedFolder = ReturnedBox.Text.Trim() }
            : null;
        result.Scope = new PhaseScope
        {
            Phase = SelectedPhase,
            FilterItems = SelectedPhase != ProjectPhase.Whole && FilterItemsBox.IsChecked == true,
            FilterFolders = SelectedPhase != ProjectPhase.Whole && FilterFoldersBox.IsChecked == true,
            IncludeUnlabeled = SelectedPhase != ProjectPhase.Whole && IncludeUnlabeledBox.IsChecked == true
        };

        var binding = chosenBinding;
        var bindingChanged = binding?.Id != savedBinding?.ProjectId;
        var coverageChanged = MainViewModel.CoverageChanged(saved, result);
        SetStatus("Saving…", null);
        if (SaveRequested is { } save)
        {
            await save(new ProjectSaved(Clone(result), bindingChanged, binding, coverageChanged));
        }

        // What was saved becomes the baseline; anything typed meanwhile goes in the next save.
        saved = Clone(result);
        savedBinding = viewModel.WorkloadBindingFor(project.Id);
        baseline = signature;
        TitleText.Text = $"Project settings — {result.Name}";
        ShowBinding();
        SetStatus($"All changes saved · {DateTime.Now:t}", "SuccessBrush");
        if (IsDirty)
        {
            autoSave.Start();
        }

        return true;
    }

    /// <summary>
    /// True when the page may be left: any change is saved first. A change that can't be saved (an empty
    /// name, a folder another project uses) is discarded only if the person agrees.
    /// </summary>
    public async Task<bool> LeaveAsync()
    {
        autoSave.Stop();
        if (saveInFlight is { } pending)
        {
            await pending;
        }

        if (!IsDirty)
        {
            return true;
        }

        var problems = Validate();
        if (problems.Count == 0)
        {
            return await SaveAsync();
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"The last changes to \"{saved.Name}\" can't be saved:\n\n{string.Join("\n", problems)}\n\nDiscard them? (No goes back to fix them.)",
            "Changes not saved",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return false;
        }

        Open(saved, viewModel);
        return true;
    }

    private void SetStatus(string text, string? brush)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, brush ?? "TextSecondary");
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (await LeaveAsync())
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetUp_Click(object sender, RoutedEventArgs e)
    {
        if (placeholder is not null)
        {
            SetUpRequested?.Invoke(this, placeholder);
        }
    }

    private async void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await SaveIfValidAsync();
        }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && !InOpenDropDown(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            if (await LeaveAsync())
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private static bool InOpenDropDown(DependencyObject? source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is ComboBox { IsDropDownOpen: true })
            {
                return true;
            }
        }

        return false;
    }

    private List<string> Validate()
    {
        var problems = new List<string>();
        if (NameBox.Text.Trim().Length == 0)
        {
            problems.Add("Give the project a name.");
        }

        if (string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            problems.Add("Choose the project's CA folder.");
        }
        else if (viewModel.Settings.Projects.Any(other => other.Id != project.Id && !IsOtherPart(other)
                     && string.Equals(Path.TrimEndingDirectorySeparator(other.CaFolder), Path.TrimEndingDirectorySeparator(FolderBox.Text), StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("Another project already uses this CA folder. If they are the Base Build and Tenant Fit Out parts of one job, set each project's part below.");
        }

        if (project.Connection is not null && viewModel.Settings.Projects.Any(other => other.Id != project.Id && !IsOtherPart(other)
                && other.Connection is not null
                && string.Equals(other.Connection.Provider, project.Connection.Provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.Connection.ProjectId, project.Connection.ProjectId, StringComparison.Ordinal)))
        {
            problems.Add($"Another project is already linked to {project.Connection.Label}. If they are the Base Build and Tenant Fit Out parts of one job, set each project's part below.");
        }

        if (OverrideFoldersBox.IsChecked == true)
        {
            problems.AddRange(new FolderLayout
            {
                RfiFolder = RfiBox.Text.Trim(),
                SubmittalFolder = SubmittalBox.Text.Trim(),
                ReturnedFolder = ReturnedBox.Text.Trim()
            }.Validate());
        }

        return problems;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"Remove \"{saved.Name}\" from the tracker?\n\nThe CA folder and everything in it stay exactly as they are.",
            "Remove project",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            autoSave.Stop();
            RemoveRequested?.Invoke(this, project.Id);
        }
    }

    // ---- Helix / Procore project -----------------------------------------------------------------

    private void ShowConnection()
    {
        ConnectionText.Text = project.Connection?.Label ?? "Not linked to a Helix or Procore project.";
        DisconnectButton.Visibility = project.Connection is null ? Visibility.Collapsed : Visibility.Visible;
        ShowBinding();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var window = new ConnectWindow(viewModel.KnownProviderProjects(), project.Connection) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true && window.Result is not null)
        {
            project.Connection = window.Result;
            ShowConnection();
            LoadCompanies();
            OnEdited();
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        project.Connection = null;
        ShowConnection();
        LoadCompanies();
        OnEdited();
    }

    // ---- Workload Tracker project ------------------------------------------------------------------

    private void ShowBinding()
    {
        var sync = viewModel.WorkloadTracker;
        WorkloadCard.Visibility = sync is { IsPaired: true } ? Visibility.Visible : Visibility.Collapsed;
        if (sync is not { IsPaired: true })
        {
            return;
        }

        var linked = project.Connection is not null;
        BindingText.Text = chosenBinding is { } choice ? choice.Label
            : linked ? "Not bound: this project's items are listed in Workload Tracker for an admin to place."
            : "Link a Helix or Procore project first.";
        ChooseBindingButton.IsEnabled = linked;
        UnbindButton.Visibility = chosenBinding is null ? Visibility.Collapsed : Visibility.Visible;

        // What the tracker said when the binding was last sent (unchanged since).
        var unchanged = chosenBinding?.Id == savedBinding?.ProjectId ? savedBinding : null;
        if (unchanged is { Outcome: WorkloadBinding.Conflict, TheirLabel: { } theirs })
        {
            var by = unchanged.TheirAssertedBy is { Length: > 0 } name ? name : "someone else";
            ConflictText.Text = $"Workload Tracker files this Helix/Procore project under {theirs}, as {by} chose. Your choice is kept as a conflict for an admin.";
            UseTheirsButton.Visibility = Visibility.Visible;
            ConflictPanel.Visibility = Visibility.Visible;
        }
        else if (unchanged is { Outcome: WorkloadBinding.Rejected })
        {
            ConflictText.Text = "Workload Tracker refused this binding: your account can't see that project. Choose another, or ask an admin.";
            UseTheirsButton.Visibility = Visibility.Collapsed;
            ConflictPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ConflictPanel.Visibility = Visibility.Collapsed;
        }

        var teamKey = chosenBinding is { } bound && linked ? $"{bound.Id}|{project.Connection!.Provider}|{project.Connection.ProjectId}" : null;
        if (teamKey is null)
        {
            // Nothing bound (or no Helix/Procore project): no team to show, and any load on its way is dropped.
            teamVersion++;
            teamLoadedFor = null;
            TeamPanel.Visibility = Visibility.Collapsed;
        }
        else if (teamKey != teamLoadedFor)
        {
            _ = LoadTeamAsync(refresh: false);
        }
    }

    private void ChooseBinding_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.WorkloadTracker is not { } sync)
        {
            return;
        }

        var search = chosenBinding?.Number ?? project.Connection?.ProjectName ?? NameBox.Text;
        var window = new TrackerProjectWindow(sync, search) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true && window.Result is not null)
        {
            chosenBinding = window.Result;
            ShowBinding();
            OnEdited();
        }
    }

    private void Unbind_Click(object sender, RoutedEventArgs e)
    {
        chosenBinding = null;
        ShowBinding();
        OnEdited();
    }

    private void UseTheirs_Click(object sender, RoutedEventArgs e)
    {
        if (savedBinding is { TheirProjectId: { } id, TheirProjectName: { } name })
        {
            chosenBinding = new TrackerProjectChoice(id, savedBinding.TheirProjectNumber, name, null, null);
            ShowBinding();
            OnEdited();
        }
    }

    // ---- Team in Workload Tracker ------------------------------------------------------------------

    /// <summary>
    /// The Workload Tracker project's team, each with who they are in Helix or Procore: linked, suggested
    /// by name, or not linked. The people offered are those named on this project's items.
    /// </summary>
    private async Task LoadTeamAsync(bool refresh)
    {
        var version = ++teamVersion;
        var sync = viewModel.WorkloadTracker;
        if (sync is not { IsPaired: true } || chosenBinding is not { } binding || project.Connection is null)
        {
            teamLoadedFor = null;
            TeamPanel.Visibility = Visibility.Collapsed;
            return;
        }

        teamLoadedFor = $"{binding.Id}|{project.Connection.Provider}|{project.Connection.ProjectId}";
        TeamPanel.Visibility = Visibility.Visible;
        ShowTeamProblem(null);
        var snapshot = viewModel.SnapshotFor(project);
        if (snapshot is null)
        {
            TeamNote.Text = "Sync this project first: CA Tracker learns who's who in Helix or Procore from its items.";
            TeamSummary.Text = "";
            TeamList.ItemsSource = null;
            ConfirmTeamButton.Visibility = Visibility.Collapsed;
            return;
        }

        TeamNote.Text = $"The people assigned to {binding.Label} in Workload Tracker. Link each one to who they are in "
            + $"{Providers.Label(snapshot.Provider)}, so the items in their court show on their My Work. A matching name is suggested; "
            + "nothing is linked until you confirm it, and links are kept in Workload Tracker straight away. People not on the team are in Settings → People.";
        TeamSummary.Text = "Loading the team…";
        TeamRefreshButton.IsEnabled = false;
        var people = PeopleLinks.From(snapshot).Where(person => person.CanLink).ToList();
        var (team, teamProblem) = await sync.TeamAsync(binding.Id, refresh);
        var (links, linkProblem) = await sync.ResolvePeopleAsync(snapshot.Provider.ToLowerInvariant(), people);
        TeamRefreshButton.IsEnabled = true;
        if (version != teamVersion)
        {
            return;
        }

        providerPeople = people;
        teamLinks = new Dictionary<string, CaPersonLink>(links, StringComparer.Ordinal);
        teamOptions.Options.Clear();
        foreach (var person in people
                     .OrderByDescending(person => PeopleLinks.AtYourCompany(person, snapshot.User))
                     .ThenBy(person => person.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            teamOptions.Options.Add(new ProviderOption(person));
        }

        // Suggest only people not linked to anyone yet.
        var free = people.Where(person => !links.TryGetValue(person.Key, out var link) || link.PersonId is null).ToList();
        teamRows = team.Select(member =>
        {
            var linked = teamOptions.Options.FirstOrDefault(option =>
                links.TryGetValue(option.Person.Key, out var link) && link.PersonId == member.PersonId);
            var suggestion = linked is null && PeopleLinks.SuggestPerson(member, free) is { } match
                ? teamOptions.Options.First(option => option.Person.Key == match.Key)
                : null;
            return new TeamLinkRow(member, linked, linked is null ? null : links[linked.Person.Key], suggestion, option => LinkedElsewhere(option, member));
        }).ToList();
        foreach (var row in teamRows)
        {
            row.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(TeamLinkRow.State))
                {
                    UpdateTeamSummary();
                }
            };
        }

        TeamList.ItemsSource = teamRows;
        ShowTeamProblem(teamProblem ?? linkProblem);
        UpdateTeamSummary();
    }

    private string? LinkedElsewhere(ProviderOption option, TeamMember member) =>
        teamLinks.TryGetValue(option.Person.Key, out var link) && link.PersonId is { } id && id != member.PersonId ? link.PersonName : null;

    private void UpdateTeamSummary()
    {
        if (teamRows.Count == 0)
        {
            TeamSummary.Text = "Nobody is assigned to this project in Workload Tracker yet.";
            ConfirmTeamButton.Visibility = Visibility.Collapsed;
            return;
        }

        var linked = teamRows.Count(row => row.State == PersonLinkState.Linked);
        var waiting = teamRows.Count(row => row.State is PersonLinkState.Suggested or PersonLinkState.Changed);
        TeamSummary.Text = $"{teamRows.Count} on the team · {linked} linked · {waiting} waiting for confirmation · {teamRows.Count - linked - waiting} not linked";
        var suggested = teamRows.Count(row => row.State == PersonLinkState.Suggested);
        ConfirmTeamButton.Content = $"Confirm all {suggested} suggestions";
        ConfirmTeamButton.Visibility = suggested > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void TeamRefresh_Click(object sender, RoutedEventArgs e) => await LoadTeamAsync(refresh: true);

    private async void ConfirmTeamRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TeamLinkRow { Choice: { } choice } row })
        {
            await LinkTeamAsync([row]);
        }
    }

    private async void UnlinkTeamRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TeamLinkRow { Linked: { } linked } row })
        {
            await SendLinksAsync([row], [(linked.Person, null)]);
        }
    }

    private async void ConfirmTeam_Click(object sender, RoutedEventArgs e)
    {
        var suggested = teamRows.Where(row => row.State == PersonLinkState.Suggested && row.Choice is not null).ToList();
        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"Link these {suggested.Count} people to who they're suggested to be?\n\n" +
            string.Join("\n", suggested.Take(15).Select(row => $"{row.Name}  =  {row.Choice!.Label}")) +
            (suggested.Count > 15 ? $"\n… and {suggested.Count - 15} more" : ""),
            "Confirm suggestions",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK)
        {
            await LinkTeamAsync(suggested);
        }
    }

    /// <summary>Links each row's choice to its team member; a different person linked to them before (by hand) is unlinked.</summary>
    private Task LinkTeamAsync(IReadOnlyList<TeamLinkRow> rows)
    {
        var changes = new List<(ProviderPerson Person, int? PersonId)>();
        foreach (var row in rows.Where(row => row.Choice is not null))
        {
            if (row.Linked is { } old && old != row.Choice && row.Link?.LinkedBy == CaPersonLinkSourceEnum.Manual)
            {
                changes.Add((old.Person, null));
            }

            changes.Add((row.Choice!.Person, row.Member.PersonId));
        }

        return SendLinksAsync(rows, changes);
    }

    private async Task SendLinksAsync(IReadOnlyList<TeamLinkRow> rows, IReadOnlyList<(ProviderPerson Person, int? PersonId)> changes)
    {
        if (viewModel.WorkloadTracker is not { IsPaired: true } sync || project.Connection is null || changes.Count == 0)
        {
            return;
        }

        foreach (var row in rows)
        {
            row.Busy = true;
        }

        var (results, problem) = await sync.LinkPeopleAsync(project.Connection.Provider.ToLowerInvariant(), changes);
        var refused = results.Where(result => result.Outcome == CaPersonLinkOutcomeEnum.Rejected).Select(result => RejectionText(changes[result.Index].Person, result.Reason)).ToList();
        await LoadTeamAsync(refresh: false);
        ShowTeamProblem(problem ?? (refused.Count > 0 ? string.Join(" ", refused) : null));
    }

    private static string RejectionText(ProviderPerson person, string? reason) => reason switch
    {
        CaPersonLinkRejectionEnum.Protected => $"{person.Name} is linked automatically to their own sign-in; an administrator can change that in Workload Tracker's admin.",
        CaPersonLinkRejectionEnum.PersonInactive => "That person has left Workload Tracker.",
        CaPersonLinkRejectionEnum.PersonNotFound => "That person isn't in Workload Tracker.",
        CaPersonLinkRejectionEnum.NoIdentity => $"Helix or Procore gives no id or email for {person.Name}, so the link can't be kept.",
        _ => $"Workload Tracker refused the link for {person.Name}."
    };

    private void ShowTeamProblem(string? problem)
    {
        TeamProblem.Text = problem ?? "";
        TeamProblem.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Company, folders, part --------------------------------------------------------------------

    private void LoadCompanies()
    {
        var snapshot = viewModel.SnapshotFor(project);
        var loginCompany = snapshot?.User.Company ?? snapshot?.User.CompanyId;
        var provider = project.Connection is null ? "Helix or Procore" : Providers.Label(project.Connection.Provider);
        var options = new List<CompanyOption>
        {
            new(null, null, loginCompany is null
                ? $"My company from the {provider} login"
                : $"My company from the {provider} login ({loginCompany})")
        };
        // Helix sometimes gives a company's id without its name: name it by the people in it instead.
        var companies = (snapshot?.Items.SelectMany(item => item.Holders) ?? Enumerable.Empty<SnapshotHolder>())
            .Where(holder => !string.IsNullOrWhiteSpace(holder.CompanyId ?? holder.Company))
            .GroupBy(holder => holder.CompanyId ?? holder.Company!, StringComparer.OrdinalIgnoreCase);
        foreach (var company in companies)
        {
            var name = company.Select(holder => holder.Company).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var people = company.Select(holder => holder.Name.Trim()).Where(person => person.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(person => person, StringComparer.CurrentCultureIgnoreCase).ToList();
            options.Add(new CompanyOption(company.First().CompanyId, name, name ?? UnnamedLabel(people)));
        }

        if ((project.CompanyId is not null || project.CompanyName is not null) && !options.Any(IsSaved))
        {
            options.Add(new CompanyOption(project.CompanyId, project.CompanyName,
                project.CompanyName ?? "Unnamed company (not seen in the latest sync)"));
        }

        var ordered = options.Take(1)
            .Concat(options.Skip(1).OrderBy(option => option.Name is null).ThenBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
        CompanyList.ItemsSource = ordered;
        CompanyList.SelectedItem = ordered.Skip(1).FirstOrDefault(IsSaved) ?? ordered[0];
        CompanyNote.Visibility = ordered.Count > 1 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The company saved for this project (by id when there is one: its name may have been unknown then).</summary>
    private bool IsSaved(CompanyOption option) => option.Id is not null
        ? string.Equals(option.Id, project.CompanyId, StringComparison.OrdinalIgnoreCase)
        : project.CompanyId is null && string.Equals(option.Name, project.CompanyName, StringComparison.OrdinalIgnoreCase);

    private static string UnnamedLabel(IReadOnlyList<string> people) => people.Count switch
    {
        0 => "Unnamed company",
        <= 3 => $"Unnamed company: {string.Join(", ", people)}",
        _ => $"Unnamed company: {string.Join(", ", people.Take(3))} and {people.Count - 3} more"
    };

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the project's CA folder", Multiselect = false };
        if (Directory.Exists(FolderBox.Text))
        {
            dialog.InitialDirectory = FolderBox.Text;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void BrowseRfi_Click(object sender, RoutedEventArgs e) =>
        BrowseSubfolder(RfiBox, FolderBox.Text, "Choose this project's RFI folder");

    private void BrowseSubmittal_Click(object sender, RoutedEventArgs e) =>
        BrowseSubfolder(SubmittalBox, FolderBox.Text, "Choose this project's Submittal folder");

    private void BrowseReturned_Click(object sender, RoutedEventArgs e) =>
        BrowseSubfolder(ReturnedBox, Resolve(FolderBox.Text, RfiBox.Text), "Choose the returned folder (inside the RFI folder, or one shared folder)");

    /// <summary>
    /// Opens a folder picker at the folder currently selected in <paramref name="box"/> (or the nearest
    /// folder that exists). A choice inside <paramref name="baseFolder"/> is stored relative to it,
    /// anything else as a full path.
    /// </summary>
    private void BrowseSubfolder(TextBox box, string? baseFolder, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        var start = NearestExisting(Resolve(baseFolder, box.Text.Trim())) ?? NearestExisting(baseFolder);
        if (start is not null)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var chosen = Path.TrimEndingDirectorySeparator(dialog.FolderName);
        box.Text = IsInside(chosen, baseFolder)
            ? Path.GetRelativePath(Path.GetFullPath(baseFolder!), chosen)
            : chosen;
        if (box == ReturnedBox && Path.IsPathFullyQualified(box.Text))
        {
            FolderNote.Text = "This returned folder is outside the RFI folder, so RFIs and Submittals will share it.";
        }
    }

    private static string? Resolve(string? baseFolder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return baseFolder;
        }

        if (Path.IsPathFullyQualified(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(baseFolder) ? null : Path.Combine(baseFolder, value);
    }

    private static string? NearestExisting(string? path)
    {
        try
        {
            var current = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current))
                {
                    return current;
                }

                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return null;
    }

    private static bool IsInside(string path, string? baseFolder)
    {
        if (string.IsNullOrWhiteSpace(baseFolder))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseFolder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private ProjectPhase SelectedPhase =>
        PhaseBaseBuild.IsChecked == true ? ProjectPhase.BaseBuild
        : PhaseFitOut.IsChecked == true ? ProjectPhase.TenantFitOut
        : ProjectPhase.Whole;

    private void Phase_Changed(object sender, RoutedEventArgs e)
    {
        PhaseOptions.IsEnabled = SelectedPhase != ProjectPhase.Whole;
    }

    /// <summary>Two projects may share a CA folder or a connection only as different parts of the same job.</summary>
    private bool IsOtherPart(TrackerProject other) =>
        SelectedPhase != ProjectPhase.Whole
        && other.Scope is { IsSplit: true }
        && other.Scope.Phase != SelectedPhase;

    private void OverrideFolders_Changed(object sender, RoutedEventArgs e)
    {
        OverrideGrid.IsEnabled = OverrideFoldersBox.IsChecked == true;
    }

    private TrackerProject Clone(TrackerProject value) => viewModel.CloneProject(value);
}
