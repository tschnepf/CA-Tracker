using System.Windows;
using SmcCaTracker.App.ViewModels;

namespace SmcCaTracker.App.Views;

/// <summary>Assign to… on the item list (Workload Tracker, decision D4).</summary>
public partial class MainWindow
{
    /// <summary>
    /// A row's Assign to… (right-click or ▾). Like Set discipline: on one of several checked rows it
    /// assigns them all; on one of several selected rows, all of those; otherwise just this row.
    /// </summary>
    private void AssignRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RowViewModel row } || !row.CanAssign)
        {
            return;
        }

        var checkedRows = viewModel.CheckedRows().ToList();
        if (row.IsChecked && checkedRows.Count > 1)
        {
            ChooseAssignees(checkedRows, $"{checkedRows.Count} checked items");
            return;
        }

        var selectedRows = selectionAtPress ?? ItemsGrid.SelectedItems.OfType<RowViewModel>().ToList();
        selectionAtPress = null;
        if (selectedRows.Count > 1 && selectedRows.Contains(row))
        {
            ChooseAssignees(selectedRows, $"{selectedRows.Count} selected items");
            return;
        }

        ChooseAssignees([row], $"{row.TypeSingular} {row.Number}");
    }

    private void AssignChecked_Click(object sender, RoutedEventArgs e)
    {
        var rows = viewModel.CheckedRows().ToList();
        ChooseAssignees(rows, $"{rows.Count} checked item{(rows.Count == 1 ? "" : "s")}");
    }

    private void ChooseAssignees(IReadOnlyList<RowViewModel> rows, string subject)
    {
        if (viewModel.WorkloadTracker is not { } sync)
        {
            return;
        }

        var assignable = viewModel.AssignableRows(rows);
        if (assignable.Count == 0)
        {
            ShowInfo("These items can't be assigned yet: bind their project to a Workload Tracker project first (Edit project).");
            return;
        }

        var projects = assignable.Select(row => row.AssignRef!.TrackerProjectId).Distinct().ToList();
        if (projects.Count > 1)
        {
            ShowInfo("The items belong to different Workload Tracker projects, each with its own team. Assign one project's items at a time.");
            return;
        }

        if (assignable.Count < rows.Count)
        {
            subject = $"{assignable.Count} of the {rows.Count} items (the others can't be assigned)";
        }

        var window = new AssignWindow(sync, projects[0], assignable, subject) { Owner = this };
        if (window.ShowDialog() == true)
        {
            viewModel.ChangeAssignments(assignable, window.Assign, window.Unassign);
        }
    }
}
