using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.App.Views;

/// <summary>A project bound to a Workload Tracker project, offered on the People page.</summary>
public sealed record BoundProjectChoice(TrackerProject Project, WorkloadBinding Binding)
{
    public string Label => $"{Project.Name}  →  {Binding.Label}";
}

/// <summary>
/// Settings → People: the Helix and Procore people CA Tracker has seen on a bound project, beside the
/// Workload Tracker people on that project's team. Matching names are suggested; a link is only made
/// when it is confirmed here, and it is kept in Workload Tracker for everyone.
/// </summary>
public partial class PeoplePanel : UserControl
{
    private readonly PickerOptions options = new();
    private MainViewModel? model;
    private List<PersonLinkRow> rows = new();
    private ProviderSnapshot? snapshot;
    private int loadVersion;

    public PeoplePanel()
    {
        InitializeComponent();
        PeopleList.DataContext = options;
    }

    /// <summary>The Workload Tracker people every row's picker offers.</summary>
    public sealed class PickerOptions
    {
        public ObservableCollection<PersonOption> Options { get; } = new();
    }

    private WorkloadTrackerSync? Sync => model?.WorkloadTracker is { IsPaired: true } sync ? sync : null;

    private BoundProjectChoice? Selected => ProjectBox.SelectedItem as BoundProjectChoice;

    /// <summary>Shows the projects bound to Workload Tracker projects and loads the chosen one's people.</summary>
    public async Task OpenAsync(MainViewModel viewModel)
    {
        model = viewModel;
        if (Sync is null)
        {
            Clear("Connect to Workload Tracker first (Settings → Connections).");
            return;
        }

        var projects = viewModel.Settings.Projects
            .Where(project => project.Connection is not null)
            .Select(project => viewModel.WorkloadBindingFor(project.Id) is { } binding ? new BoundProjectChoice(project, binding) : null)
            .OfType<BoundProjectChoice>()
            .OrderBy(choice => choice.Project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (projects.Count == 0)
        {
            Clear("Bind a project to its Workload Tracker project first: select it, then Edit project → Workload Tracker → Choose….");
            return;
        }

        var previous = Selected?.Project.Id;
        ProjectBox.SelectionChanged -= Project_Changed;
        ProjectBox.ItemsSource = projects;
        ProjectBox.SelectedItem = projects.FirstOrDefault(choice => choice.Project.Id == previous) ?? projects[0];
        ProjectBox.SelectionChanged += Project_Changed;
        await LoadAsync();
    }

    private void Clear(string message)
    {
        ProjectBox.ItemsSource = null;
        rows = new List<PersonLinkRow>();
        PeopleList.ItemsSource = null;
        SummaryText.Text = "";
        ConfirmAllButton.Visibility = Visibility.Collapsed;
        ShowProblem(null);
        EmptyText.Text = message;
    }

    private async void Project_Changed(object sender, SelectionChangedEventArgs e) => await LoadAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (model is not null)
        {
            await OpenAsync(model);
        }
    }

    private async Task LoadAsync()
    {
        var version = ++loadVersion;
        if (Selected is not { } choice || Sync is not { } sync || model is null)
        {
            return;
        }

        snapshot = model.SnapshotFor(choice.Project);
        rows = new List<PersonLinkRow>();
        PeopleList.ItemsSource = null;
        ShowProblem(null);
        if (snapshot is null)
        {
            EmptyText.Text = "Sync this project first: its people come from its Helix or Procore items.";
            SummaryText.Text = "";
            return;
        }

        EmptyText.Text = "Loading the people…";
        RefreshButton.IsEnabled = false;
        var people = PeopleLinks.From(snapshot);
        var (team, teamProblem) = await sync.TeamAsync(choice.Binding.ProjectId, refresh: true);
        var (links, linkProblem) = await sync.ResolvePeopleAsync(snapshot.Provider.ToLowerInvariant(), people);
        RefreshButton.IsEnabled = true;
        if (version != loadVersion)
        {
            return;
        }

        options.Options.Clear();
        foreach (var member in team)
        {
            options.Options.Add(PersonOption.From(member));
        }

        // People linked already but not on the team (yet) can still be shown as linked.
        foreach (var link in links.Values.Where(link => link.PersonId is not null).DistinctBy(link => link.PersonId))
        {
            if (options.Options.All(option => option.Id != link.PersonId))
            {
                options.Options.Add(new PersonOption(link.PersonId!.Value, link.PersonName ?? $"Person {link.PersonId}", "Not on the project's team"));
            }
        }

        rows = people.Select(person =>
        {
            links.TryGetValue(person.Key, out var link);
            var suggestion = link?.PersonId is null && PeopleLinks.Suggest(person, team) is { } member
                ? options.Options.First(option => option.Id == member.PersonId)
                : null;
            return new PersonLinkRow(person, link, suggestion)
            {
                Choice = link?.PersonId is { } id ? options.Options.First(option => option.Id == id) : suggestion
            };
        }).ToList();

        YourCompanyBox.Content = $"Only people at {snapshot.User?.Company ?? "your company"}";
        ShowProblem(teamProblem ?? linkProblem);
        Refilter();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            Refilter();
        }
    }

    private List<PersonLinkRow> Shown() =>
        YourCompanyBox.IsChecked == true ? rows.Where(row => PeopleLinks.AtYourCompany(row.Person, snapshot?.User)).ToList() : rows;

    private void Refilter()
    {
        var shown = Shown();
        PeopleList.ItemsSource = shown;
        EmptyText.Text = shown.Count > 0 ? ""
            : rows.Count == 0 ? "Nobody is named on this project's items yet."
            : "Nobody at your company is named on this project's items. Clear the box to see everyone.";
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var shown = Shown();
        var linked = shown.Count(row => row.State == PersonLinkState.Linked);
        var waiting = shown.Count(row => row.State is PersonLinkState.Suggested or PersonLinkState.Changed);
        var notLinked = shown.Count(row => row.State is PersonLinkState.NotLinked or PersonLinkState.CantLink);
        var hidden = rows.Count - shown.Count;
        SummaryText.Text = $"{linked} linked · {waiting} waiting for confirmation · {notLinked} not linked"
            + (hidden > 0 ? $" · {hidden} at other companies hidden" : "");
        var suggested = shown.Count(row => row.State == PersonLinkState.Suggested);
        ConfirmAllButton.Content = suggested == 1 ? "Confirm 1 suggestion" : $"Confirm all {suggested} suggestions";
        ConfirmAllButton.Visibility = suggested > 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PersonLinkRow { Choice: { } choice } row })
        {
            await LinkAsync([(row, choice.Id)]);
        }
    }

    private async void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PersonLinkRow row })
        {
            await LinkAsync([(row, null)]);
        }
    }

    private async void ConfirmAll_Click(object sender, RoutedEventArgs e)
    {
        var suggested = Shown().Where(row => row.State == PersonLinkState.Suggested && row.Choice is not null).ToList();
        var answer = MessageBox.Show(
            Window.GetWindow(this)!,
            $"Link these {suggested.Count} people to the Workload Tracker people suggested for them?\n\n" +
            string.Join("\n", suggested.Take(15).Select(row => $"{row.Name}  →  {row.Choice!.Name}")) +
            (suggested.Count > 15 ? $"\n… and {suggested.Count - 15} more" : ""),
            "Confirm suggestions",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK)
        {
            await LinkAsync(suggested.Select(row => (row, (int?)row.Choice!.Id)).ToList());
        }
    }

    private void SomeoneElse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PersonLinkRow row } || Sync is not { } sync)
        {
            return;
        }

        var window = new PersonSearchWindow(sync, row.Name) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true && window.Result is { } person)
        {
            var option = options.Options.FirstOrDefault(existing => existing.Id == person.PersonId);
            if (option is null)
            {
                option = PersonOption.From(person);
                options.Options.Add(option);
            }

            row.Choice = option; // still to be confirmed
            UpdateSummary();
        }
    }

    private async Task LinkAsync(IReadOnlyList<(PersonLinkRow Row, int? PersonId)> changes)
    {
        if (Sync is not { } sync || snapshot is null || changes.Count == 0)
        {
            return;
        }

        foreach (var (row, _) in changes)
        {
            row.Busy = true;
            row.Problem = null;
        }

        var (results, problem) = await sync.LinkPeopleAsync(snapshot.Provider.ToLowerInvariant(), changes.Select(change => (change.Row.Person, change.PersonId)).ToList());
        foreach (var result in results.Where(result => result.Index >= 0 && result.Index < changes.Count))
        {
            var row = changes[result.Index].Row;
            if (result.Outcome == CaPersonLinkOutcomeEnum.Rejected)
            {
                row.Problem = RejectionText(result.Reason);
            }
            else if (result.Link is { } link)
            {
                row.Link = link;
                row.Choice = link.PersonId is { } id ? options.Options.FirstOrDefault(option => option.Id == id) ?? row.Choice : null;
            }
        }

        foreach (var (row, _) in changes)
        {
            row.Busy = false;
        }

        ShowProblem(problem);
        UpdateSummary();
    }

    private static string RejectionText(string? reason) => reason switch
    {
        CaPersonLinkRejectionEnum.Protected => "Linked automatically to their own sign-in; an administrator can change it in Workload Tracker's admin.",
        CaPersonLinkRejectionEnum.PersonInactive => "That person has left Workload Tracker.",
        CaPersonLinkRejectionEnum.PersonNotFound => "That person isn't in Workload Tracker.",
        CaPersonLinkRejectionEnum.NoIdentity => "Helix or Procore gives no id or email for them, so the link can't be kept.",
        _ => "Workload Tracker refused the link."
    };

    private void ShowProblem(string? problem)
    {
        ProblemText.Text = problem ?? "";
        ProblemText.Visibility = string.IsNullOrEmpty(problem) ? Visibility.Collapsed : Visibility.Visible;
    }
}
