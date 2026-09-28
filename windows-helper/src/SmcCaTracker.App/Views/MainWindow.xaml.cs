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

    private async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.SelectedProject is null)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = $"Choose the CA folder for {viewModel.SelectedProject.Name}",
            Multiselect = false
        };
        var current = viewModel.SelectedProject.Mapping?.CaFolder;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
        {
            dialog.InitialDirectory = current;
        }

        if (dialog.ShowDialog(this) == true)
        {
            // Choosing a folder only records it in the tracker's settings; nothing in it changes.
            await viewModel.SetCaFolderAsync(dialog.FolderName);
        }
    }

    private void OpenCaFolder_Click(object sender, RoutedEventArgs e) =>
        OpenInExplorer(viewModel.SelectedProject?.Mapping?.CaFolder);

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await viewModel.RefreshAsync();

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
        await viewModel.RefreshAsync();
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
        var window = new SettingsWindow(viewModel.CloneSettings(), viewModel.SelectedProject, viewModel.Paths) { Owner = this };
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
            ShowInfo("That folder cannot be found. Rescan to refresh the list.");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void ShowInfo(string message) =>
        MessageBox.Show(this, message, "CA Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
}
