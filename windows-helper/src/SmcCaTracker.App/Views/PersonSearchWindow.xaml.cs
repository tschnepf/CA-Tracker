using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.WorkloadTracker;

namespace SmcCaTracker.App.Views;

/// <summary>Finds a Workload Tracker person who isn't on the project's team, for the People page.</summary>
public partial class PersonSearchWindow : Window
{
    private readonly WorkloadTrackerSync sync;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private List<TeamMember> found = new();
    private CancellationTokenSource? searching;

    public PersonSearchWindow(WorkloadTrackerSync sync, string helixOrProcoreName)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.sync = sync;
        SubjectText.Text = $"Who in Workload Tracker is {helixOrProcoreName}? Active people only.";
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            await SearchAsync();
        };
        SearchBox.Text = helixOrProcoreName;
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            await SearchAsync();
        };
        Closed += (_, _) =>
        {
            debounce.Stop();
            searching?.Cancel();
        };
    }

    public TeamMember? Result { get; private set; }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            debounce.Stop();
            debounce.Start();
        }
    }

    private async Task SearchAsync()
    {
        searching?.Cancel();
        var cancel = new CancellationTokenSource();
        searching = cancel;
        EmptyText.Text = "Searching…";
        ErrorText.Text = "";
        (IReadOnlyList<TeamMember> People, string? Problem) result;
        try
        {
            result = await sync.SearchPeopleAsync(SearchBox.Text, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancel.IsCancellationRequested)
        {
            return;
        }

        found = result.People.ToList();
        PeopleList.ItemsSource = found.Select(PersonOption.From).ToList();
        if (found.Count > 0)
        {
            PeopleList.SelectedIndex = 0;
        }

        ErrorText.Text = result.Problem ?? "";
        EmptyText.Text = found.Count > 0 || result.Problem is not null ? "" : "Nobody matches. Try part of their first or last name.";
    }

    private void PeopleList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PeopleList.SelectedItem is PersonOption)
        {
            Choose_Click(sender, e);
        }
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (PeopleList.SelectedItem is not PersonOption option)
        {
            ErrorText.Text = "Choose someone from the list.";
            return;
        }

        Result = found.First(member => member.PersonId == option.Id);
        DialogResult = true;
    }
}
