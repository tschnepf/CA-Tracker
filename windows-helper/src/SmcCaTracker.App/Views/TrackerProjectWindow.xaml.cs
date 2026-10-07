using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.WorkloadTracker;

namespace SmcCaTracker.App.Views;

public sealed record TrackerProjectOption(TrackerProjectChoice Project, string Label, string Detail);

/// <summary>
/// Picks the existing Workload Tracker project a project entry's items go to (decision D3). Searches
/// the tracker as you type; only running projects are offered.
/// </summary>
public partial class TrackerProjectWindow : Window
{
    private readonly WorkloadTrackerSync sync;
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly List<TrackerProjectOption> shown = new();
    private CancellationTokenSource? searching;
    private int page = 1;

    public TrackerProjectWindow(WorkloadTrackerSync sync, string initialSearch)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.sync = sync;
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            await SearchAsync(append: false);
        };
        SearchBox.Text = initialSearch;
        Loaded += async (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            await SearchAsync(append: false);
        };
        Closed += (_, _) =>
        {
            debounce.Stop();
            searching?.Cancel();
        };
    }

    public TrackerProjectChoice? Result { get; private set; }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            debounce.Stop();
            debounce.Start();
        }
    }

    private async void More_Click(object sender, RoutedEventArgs e) => await SearchAsync(append: true);

    private async Task SearchAsync(bool append)
    {
        searching?.Cancel();
        var cancel = new CancellationTokenSource();
        searching = cancel;
        page = append ? page + 1 : 1;
        ErrorText.Text = "";
        EmptyText.Text = append ? EmptyText.Text : "Searching…";
        MoreButton.IsEnabled = false;

        (IReadOnlyList<TrackerProjectChoice> Projects, bool More, string? Problem) result;
        try
        {
            result = await sync.SearchProjectsAsync(SearchBox.Text, page, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancel.IsCancellationRequested)
        {
            return;
        }

        if (!append)
        {
            shown.Clear();
        }

        shown.AddRange(result.Projects
            .Where(project => shown.All(existing => existing.Project.Id != project.Id))
            .Select(project => new TrackerProjectOption(project, project.Label, Detail(project))));
        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = shown;
        if (!append && shown.Count > 0)
        {
            ProjectList.SelectedIndex = 0;
        }

        ErrorText.Text = result.Problem ?? "";
        EmptyText.Text = shown.Count > 0 || result.Problem is not null ? ""
            : SearchBox.Text.Trim().Length == 0 ? "No running projects found." : "No running project matches. Try part of its number or name.";
        MoreButton.Visibility = result.More ? Visibility.Visible : Visibility.Collapsed;
        MoreButton.IsEnabled = true;
    }

    private static string Detail(TrackerProjectChoice project)
    {
        var status = project.Status switch
        {
            "active" => "Active",
            "active_ca" => "Active CA",
            { Length: > 0 } other => other,
            _ => null
        };
        return string.Join(" · ", new[] { project.Client, status }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private void ProjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectList.SelectedItem is TrackerProjectOption)
        {
            Choose_Click(sender, e);
        }
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not TrackerProjectOption option)
        {
            ErrorText.Text = "Choose a project from the list.";
            return;
        }

        Result = option.Project;
        DialogResult = true;
    }
}
