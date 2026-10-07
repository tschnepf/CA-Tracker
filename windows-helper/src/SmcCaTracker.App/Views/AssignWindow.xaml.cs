using System.Windows;
using System.Windows.Controls;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.WorkloadTracker;

namespace SmcCaTracker.App.Views;

/// <summary>A person in the Assign to list. Checked: assigned to every item; mixed (null): to some of them.</summary>
public sealed class AssignOption : ObservableObject
{
    private bool? isChecked;

    public AssignOption(TeamMember member, bool? assigned)
    {
        Member = member;
        Initial = assigned;
        isChecked = assigned;
    }

    public TeamMember Member { get; }

    /// <summary>How the items were when the window opened.</summary>
    public bool? Initial { get; }

    public bool? IsChecked
    {
        get => isChecked;
        set => Set(ref isChecked, value);
    }

    /// <summary>Only a person some of the items have can be left as they are (the dash).</summary>
    public bool IsMixed => Initial is null;

    public string Name => Member.Name;

    public string Detail => string.Join(" · ", new[] { Member.Role, Member.Department }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public bool HasDetail => Detail.Length > 0;
}

/// <summary>
/// Assigns people on a Workload Tracker project's team to one or more of its items (decision D4), and
/// unassigns them. Nothing is sent until Save.
/// </summary>
public partial class AssignWindow : Window
{
    private readonly WorkloadTrackerSync sync;
    private readonly int trackerProjectId;
    private readonly IReadOnlyList<RowViewModel> rows;
    private List<AssignOption> options = new();

    public AssignWindow(WorkloadTrackerSync sync, int trackerProjectId, IReadOnlyList<RowViewModel> rows, string subject)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.sync = sync;
        this.trackerProjectId = trackerProjectId;
        this.rows = rows;
        SubjectText.Text = $"Assign {subject} to";
        Loaded += async (_, _) => await LoadAsync(refresh: false);
    }

    public IReadOnlyList<TeamMember> Assign { get; private set; } = [];
    public IReadOnlyList<int> Unassign { get; private set; } = [];

    private async Task LoadAsync(bool refresh)
    {
        ErrorText.Text = "";
        EmptyText.Text = "Loading the team…";
        ReloadButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        var (team, problem) = await sync.TeamAsync(trackerProjectId, refresh);
        ReloadButton.IsEnabled = true;
        SaveButton.IsEnabled = team.Count > 0 || rows.Any(row => row.Assignees.Count > 0);

        // Everyone on the team, plus anyone assigned now who isn't (someone who left), so they can be unassigned.
        var people = team.ToDictionary(member => member.PersonId);
        foreach (var assignee in rows.SelectMany(row => row.Assignees))
        {
            if (!people.ContainsKey(assignee.PersonId))
            {
                people[assignee.PersonId] = new TeamMember(assignee.PersonId, assignee.PersonName, null,
                    assignee.PersonActive ? "Not on the project's team" : "Has left");
            }
        }

        options = people.Values
            .OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(member => new AssignOption(member, AssignedOn(member.PersonId)))
            .ToList();
        MixedNote.Visibility = options.Any(option => option.IsMixed) ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = problem ?? "";
        Refilter();
    }

    /// <summary>True when every item has the person, false when none does, null when some do.</summary>
    private bool? AssignedOn(int personId)
    {
        var count = rows.Count(row => row.Assignees.Any(assignee => assignee.PersonId == personId));
        return count == 0 ? false : count == rows.Count ? true : null;
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => Refilter();

    private void Refilter()
    {
        var text = FilterBox.Text.Trim();
        var shown = options
            .Where(option => text.Length == 0
                || option.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                || option.Detail.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        PeopleList.ItemsSource = shown;
        EmptyText.Text = shown.Count > 0 ? ""
            : options.Count == 0 ? (ErrorText.Text.Length > 0 ? "" : "Nobody is on this project's team in Workload Tracker yet.")
            : "Nobody matches.";
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync(refresh: true);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Assign = options.Where(option => option.IsChecked == true && option.Initial != true).Select(option => option.Member).ToList();
        Unassign = options.Where(option => option.IsChecked == false && option.Initial != false).Select(option => option.Member.PersonId).ToList();
        DialogResult = true;
    }
}
