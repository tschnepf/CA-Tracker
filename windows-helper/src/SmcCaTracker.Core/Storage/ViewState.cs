using System.Text.Json;

namespace SmcCaTracker.Core.Storage;

/// <summary>
/// How the main window was last set up: which projects were shown and the list's filters, people,
/// grouping and sort. Kept apart from settings.json so saving settings never touches it.
/// </summary>
public sealed class ViewState
{
    public List<string> SelectedProjectIds { get; set; } = new();
    public string? PrimaryProjectId { get; set; }
    public string ItemType { get; set; } = "all";
    public string Filter { get; set; } = "open";
    public List<string> People { get; set; } = new();
    public bool AnyoneAtCompany { get; set; }
    public string Due { get; set; } = "Any";
    public int DueWithinDays { get; set; } = 7;
    public string Discipline { get; set; } = "";
    public string GroupBy { get; set; } = "project-type";
    public string SortColumn { get; set; } = "Number";
    public bool SortDescending { get; set; }
    public string SortPreset { get; set; } = "number";

    /// <summary>
    /// Projects you archived: hidden from your list and not synced. Personal to this PC: kept with the
    /// rest of your view, never sent to Helix, Procore or the company server. A connected project is
    /// remembered by its Helix/Procore project ("procore|123"), others by their id ("project|abc").
    /// </summary>
    public List<string> ArchivedProjects { get; set; } = new();

    /// <summary>Show archived projects in the list (dimmed) so they can be unarchived.</summary>
    public bool ShowArchived { get; set; }

    /// <summary>Width of the project list, in pixels (drag the bar beside it).</summary>
    public double ProjectsPaneWidth { get; set; } = 290;
}

public sealed class ViewStateStore
{
    private readonly AppPaths paths;

    public ViewStateStore(AppPaths paths)
    {
        this.paths = paths;
    }

    public ViewState Load()
    {
        try
        {
            var json = LocalFiles.ReadAllTextWithRetry(paths.ViewStateFile);
            var state = json is null ? null : JsonSerializer.Deserialize<ViewState>(json, TrackerJson.Options);
            if (state is null)
            {
                return new ViewState();
            }

            state.SelectedProjectIds ??= new List<string>();
            state.People ??= new List<string>();
            state.ItemType ??= "all";
            state.Filter ??= "open";
            state.Due ??= "Any";
            state.Discipline ??= "";
            state.GroupBy ??= "project-type";
            state.SortColumn ??= "Number";
            state.SortPreset ??= "";
            state.ArchivedProjects ??= new List<string>();
            return state;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ViewState();
        }
    }

    public void Save(ViewState state)
    {
        try
        {
            LocalFiles.WriteAllTextAtomic(paths.ViewStateFile, JsonSerializer.Serialize(state, TrackerJson.Options));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Remembering the view is a convenience; never let it stop the app.
        }
    }
}
