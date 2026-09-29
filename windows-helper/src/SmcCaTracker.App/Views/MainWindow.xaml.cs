using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Operations;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly OperationExecutor executor;

    public MainWindow(MainViewModel viewModel, AppPaths paths)
    {
        this.viewModel = viewModel;
        executor = new OperationExecutor(paths);
        InitializeComponent();
        DataContext = viewModel;
        ThemeManager.Attach(this);
        Closed += (_, _) => viewModel.Dispose();
        Loaded += (_, _) =>
        {
            if (viewModel.SelectedProject is null && viewModel.Projects.Count > 0)
            {
                viewModel.SelectedProject = viewModel.Projects[0];
            }
        };
    }

    private void Type_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && sender is FrameworkElement { Tag: string tag })
        {
            model.SelectedType = tag;
        }
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model && sender is FrameworkElement { Tag: string tag })
        {
            model.Filter = tag;
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the new project's CA folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var existing = viewModel.Settings.Projects.FirstOrDefault(project =>
            string.Equals(Path.TrimEndingDirectorySeparator(project.CaFolder), Path.TrimEndingDirectorySeparator(dialog.FolderName), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ShowInfo($"That CA folder already belongs to the project \"{existing.Name}\".");
            viewModel.SelectedProject = viewModel.Projects.FirstOrDefault(project => project.Id == existing.Id);
            return;
        }

        // Creating a project only records it in the tracker's settings; nothing in the folder changes.
        var draft = new TrackerProject { Name = TrackerProject.SuggestName(dialog.FolderName), CaFolder = dialog.FolderName };
        var window = new ProjectWindow(draft, isNew: true, viewModel) { Owner = this };
        if (window.ShowDialog() == true && window.Result is not null)
        {
            viewModel.AddProject(window.Result);
            await viewModel.CompareAsync();
        }
    }

    private async void EditProject_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedProject is null)
        {
            return;
        }

        var window = new ProjectWindow(viewModel.CloneProject(viewModel.SelectedProject.Project), isNew: false, viewModel) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        if (window.RemoveRequested)
        {
            viewModel.RemoveProject(viewModel.SelectedProject.Id);
        }
        else if (window.Result is not null)
        {
            await viewModel.UpdateProjectAsync(window.Result);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedProject is null)
        {
            return;
        }

        var project = viewModel.CloneProject(viewModel.SelectedProject.Project);
        var window = new ConnectWindow(viewModel.KnownProviderProjects(), project.Connection, viewModel.Settings.Browser) { Owner = this };
        if (window.ShowDialog() == true && window.Result is not null)
        {
            project.Connection = window.Result;
            await viewModel.UpdateProjectAsync(project);
        }
    }

    private void OpenCaFolder_Click(object sender, RoutedEventArgs e) =>
        OpenInExplorer(viewModel.SelectedProject?.Project.CaFolder);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await viewModel.StartRefreshAsync();

    private void CancelRefresh_Click(object sender, RoutedEventArgs e) => viewModel.CancelRefresh();

    private void SelectReady_Click(object sender, RoutedEventArgs e) => viewModel.SelectAllReady();

    private void ClearTicks_Click(object sender, RoutedEventArgs e) => viewModel.ClearChecks();

    private async void MarkReturned_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.Comparison is null)
        {
            return;
        }

        var rows = viewModel.CheckedRows().Where(row => row.CanMarkReturned).ToList();
        if (rows.Count == 0)
        {
            ShowInfo("Tick the active folders you want to move into the returned folder first. \"Tick all ready to return\" selects the suggested ones.");
            return;
        }

        await PreviewAndRunAsync(OperationPlanner.PlanMarkReturned(rows, viewModel.Comparison));
    }

    private async void MoveBack_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.Comparison is null)
        {
            return;
        }

        var rows = viewModel.CheckedRows().Where(row => row.CanMoveBack).ToList();
        if (rows.Count == 0)
        {
            ShowInfo("Tick folders that are currently in the returned folder to move them back.");
            return;
        }

        await PreviewAndRunAsync(OperationPlanner.PlanMoveBack(rows, viewModel.Comparison));
    }

    private async void CreateFolders_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.Comparison is not null)
        {
            await PreviewAndRunAsync(OperationPlanner.PlanCreateTypeFolders(viewModel.Comparison));
        }
    }

    /// <summary>Every folder change goes through here: preview, explicit confirmation, then run.</summary>
    private async Task PreviewAndRunAsync(OperationPlan plan)
    {
        if (plan.Operations.Count == 0)
        {
            ShowInfo("There is nothing to change.");
            return;
        }

        var preview = new PreviewWindow(plan, null) { Owner = this };
        if (preview.ShowDialog() != true)
        {
            viewModel.StatusMessage = "Cancelled — nothing was changed.";
            return;
        }

        viewModel.IsBusy = true;
        IReadOnlyList<OperationResult> results;
        try
        {
            results = await Task.Run(() => executor.Execute(plan));
        }
        finally
        {
            viewModel.IsBusy = false;
        }

        var done = results.Count(result => result.Outcome == OperationOutcome.Done);
        var failed = results.Count(result => result.Outcome == OperationOutcome.Failed);
        viewModel.StatusMessage = $"{plan.Title}: {done} done, {failed} failed, {results.Count - done - failed} skipped.";
        new PreviewWindow(plan, results) { Owner = this }.ShowDialog();
        await viewModel.CompareAsync();
    }

    private void OpenRowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RowViewModel row })
        {
            OpenInExplorer(row.Row.Folder?.FullPath);
        }
    }

    private void OpenRowLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RowViewModel { HasLink: true } row })
        {
            Process.Start(new ProcessStartInfo(row.SourceUrl!) { UseShellExecute = true });
        }
    }

    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ItemsGrid, source) is DataGridRow { Item: RowViewModel row }
            && row.HasFolder)
        {
            OpenInExplorer(row.Row.Folder!.FullPath);
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(viewModel.CloneSettings(), viewModel.Paths) { Owner = this };
        if (window.ShowDialog() == true)
        {
            ThemeManager.Apply(window.Result.Theme);
            await viewModel.ApplySettingsAsync(window.Result);
        }
        else
        {
            ThemeManager.Apply(viewModel.Settings.Theme);
        }
    }

    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowInfo("That folder cannot be found. Select Refresh to update the list.");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void ShowInfo(string message) =>
        MessageBox.Show(this, message, "CA Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
}
