using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Links;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.App.ViewModels;

/// <summary>Links from Workload Tracker's web list (smc-ca-tracker://item?…): the project and row they name.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// The project entry for a link's item: one linked to its Helix or Procore project. Of a Base Build /
    /// Tenant Fit Out pair, the one whose part includes the item; a set-up entry before one without a CA
    /// folder; one not archived before an archived one. Null when none is linked.
    /// </summary>
    public ProjectViewModel? ProjectForLink(ItemLink link)
    {
        var candidates = Projects
            .Where(project => project.Project.Connection is { } connection
                && string.Equals(connection.Provider, link.Provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(connection.ProjectId, link.ProjectId, StringComparison.Ordinal))
            .ToList();
        var item = candidates
            .Select(project => project.Snapshot)
            .OfType<ProviderSnapshot>()
            .SelectMany(snapshot => snapshot.Items)
            .FirstOrDefault(entry => string.Equals(entry.ItemType, link.ItemType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.ItemId, link.ItemId, StringComparison.Ordinal));
        return candidates
            .OrderBy(project => project.IsArchived)
            .ThenByDescending(project => item is not null && Phases.IncludesItem(project.Project.Scope, item))
            .ThenBy(project => project.IsPlaceholder)
            .ThenByDescending(project => project.HasCaFolder)
            .FirstOrDefault();
    }

    /// <summary>
    /// The listed row for a link's item: by its Helix or Procore id, else by its number (a link to an older
    /// Procore revision finds the newest, which is the one listed). Null when it isn't listed.
    /// </summary>
    public RowViewModel? RowForLink(ItemLink link, ProjectViewModel project)
    {
        var rows = Rows
            .Where(row => ReferenceEquals(row.Project, project) && row.Row.Item is not null
                && string.Equals(row.Row.ItemType, link.ItemType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return rows.FirstOrDefault(row => string.Equals(row.Row.Item!.ItemId, link.ItemId, StringComparison.Ordinal))
            ?? rows.FirstOrDefault(row => string.Equals(row.Row.Item!.Number.Trim(), link.Number, StringComparison.OrdinalIgnoreCase));
    }
}
