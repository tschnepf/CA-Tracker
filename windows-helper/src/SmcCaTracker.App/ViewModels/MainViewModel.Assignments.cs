using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;

namespace SmcCaTracker.App.ViewModels;

/// <summary>Workload Tracker bindings (decision D3) and item assignments (D4) on the main list.</summary>
public sealed partial class MainViewModel
{
    private IReadOnlyDictionary<string, IReadOnlyList<WorkloadAssignment>> assignmentIndex =
        new Dictionary<string, IReadOnlyList<WorkloadAssignment>>(StringComparer.Ordinal);

    /// <summary>The Workload Tracker connection, for the windows that bind projects and assign people (null in a build without it).</summary>
    public WorkloadTrackerSync? WorkloadTracker => workloadTracker;

    /// <summary>The Assigned column shows: connected, and a shown project is bound to a Workload Tracker project.</summary>
    public bool ShowsAssignments
    {
        get
        {
            var sync = Settings.WorkloadTracker;
            return workloadTracker is not null && sync.IsPaired && selectedProjects.Any(project => sync.Bindings.ContainsKey(project.Id));
        }
    }

    /// <summary>The Workload Tracker project a project entry is bound to, if any.</summary>
    public WorkloadBinding? WorkloadBindingFor(string projectId) =>
        Settings.WorkloadTracker.Bindings.TryGetValue(projectId, out var binding) ? binding : null;

    /// <summary>
    /// After Edit project or New project is saved: binds or unbinds the entry when that changed, and
    /// otherwise sends its items again in full when what it covers changed (its link or its part).
    /// </summary>
    public void ApplyWorkloadBinding(string projectId, bool bindingChanged, TrackerProjectChoice? choice, bool coverageChanged)
    {
        if (workloadTracker is null)
        {
            return;
        }

        if (bindingChanged)
        {
            workloadTracker.SetBinding(projectId, choice);
        }
        else if (coverageChanged)
        {
            workloadTracker.SendAgain(projectId);
        }
        else
        {
            return;
        }

        SendToWorkloadTrackerSoon();
    }

    /// <summary>Checked rows can be assigned (the action bar's Assign button).</summary>
    public bool CanAssignChecked => ShowsAssignments && allRows.Any(row => row.IsChecked && row.CanAssign);

    public string AssignCheckedText
    {
        get
        {
            var count = allRows.Count(row => row.IsChecked && row.CanAssign);
            return count == 1 ? "Assign 1 item…" : $"Assign {count} items…";
        }
    }

    /// <summary>What an edit changed that the tracker files items by: the linked project, or the part of it covered.</summary>
    public static bool CoverageChanged(TrackerProject before, TrackerProject after) =>
        !string.Equals(before.Connection?.Provider, after.Connection?.Provider, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(before.Connection?.ProjectId, after.Connection?.ProjectId, StringComparison.Ordinal)
        || before.Scope.Phase != after.Scope.Phase
        || before.Scope.FilterItems != after.Scope.FilterItems
        || before.Scope.IncludeUnlabeled != after.Scope.IncludeUnlabeled;

    /// <summary>The rows (of those given) whose items can be assigned.</summary>
    public List<RowViewModel> AssignableRows(IEnumerable<RowViewModel> rows) => rows.Where(row => row.CanAssign).ToList();

    /// <summary>Assigns and unassigns people on the rows' items: shown at once, then sent.</summary>
    public void ChangeAssignments(IReadOnlyList<RowViewModel> rows, IReadOnlyList<TeamMember> assign, IReadOnlyList<int> unassign)
    {
        if (workloadTracker is null || (assign.Count == 0 && unassign.Count == 0))
        {
            return;
        }

        var items = rows.Select(row => row.AssignRef).OfType<WorkloadItemRef>().Distinct().ToList();
        workloadTracker.ChangeAssignments(items, assign, unassign);
        SendToWorkloadTrackerSoon();
    }

    private void SendToWorkloadTrackerSoon()
    {
        if (workloadTracker is { IsPaired: true } service)
        {
            _ = RunTimerAsync("Workload Tracker", () => service.SendAsync());
        }
    }

    /// <summary>
    /// Fills in each row's assignment: the item as Workload Tracker names it (when its project is
    /// bound) and who is assigned to it.
    /// </summary>
    private void ApplyAssignments(IEnumerable<RowViewModel> rows)
    {
        var sync = Settings.WorkloadTracker;
        var paired = workloadTracker is not null && sync.IsPaired;
        var me = paired ? sync.PersonId : null;
        foreach (var row in rows)
        {
            WorkloadItemRef? reference = null;
            if (paired && row.Row.Item is { } item && row.Project.Project.Connection is { } connection
                && sync.Bindings.TryGetValue(row.Project.Id, out var binding))
            {
                reference = ObservationBuilder.ItemRef(binding.ProjectId, connection, item);
            }

            var assignees = reference is not null && assignmentIndex.TryGetValue(reference.Key, out var list) ? list : [];
            row.SetAssignments(reference, assignees, me, paired);
        }
    }

    /// <summary>Assignments changed (here, or read back from the tracker): the column, My work and the counts follow.</summary>
    private void OnAssignmentsChanged(bool rowsWillBeRebuilt)
    {
        assignmentIndex = store.AssignmentIndex();
        foreach (var source in badgeSources.Values)
        {
            ApplyAssignments(source.Rows);
        }

        if (rowsWillBeRebuilt)
        {
            return;
        }

        ApplyAssignments(allRows);
        if (IsMyWorkActive || sortColumn == nameof(RowViewModel.AssignedSortKey))
        {
            // Who is in the list (or its order) depends on assignments.
            RebuildRows();
        }
        else
        {
            UpdateViewCounts();
        }

        RaiseWorkloadTracker();
    }
}
