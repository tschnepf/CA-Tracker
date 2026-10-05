using System.Text.Json;

namespace SmcCaTracker.Core.Storage;

/// <summary>
/// How the main window was last set up: which projects were shown and the list's filters, people,
/// grouping and sort. Kept apart from settings.json so saving settings never touches it.
/// </summary>
public sealed class ViewState
{
    /// <summary>The layout of the saved view. Older ones are brought up to date once by <see cref="Upgrade"/>.</summary>
    public const int CurrentVersion = 2;

    /// <summary>0 in views saved before versions were kept.</summary>
    public int Version { get; set; }

    public List<string> SelectedProjectIds { get; set; } = new();
    public string? PrimaryProjectId { get; set; }
    public string ItemType { get; set; } = "all";
    public string Filter { get; set; } = "open";
    public List<string> People { get; set; } = new();
    public bool AnyoneAtCompany { get; set; }

    /// <summary>
    /// Ball in court was set to you once, when your name first became known, so choosing Everyone
    /// afterwards sticks. False in files from before this was added: those get it once too.
    /// </summary>
    public bool PeopleDefaulted { get; set; }
    public string Due { get; set; } = "Any";
    public int DueWithinDays { get; set; } = 7;
    public string Discipline { get; set; } = "";
    public string GroupBy { get; set; } = "urgency";
    public string SortColumn { get; set; } = "Number";
    public bool SortDescending { get; set; }
    public string SortPreset { get; set; } = "number";

    /// <summary>
    /// Projects you archived: hidden from your list and not synced. Personal to this PC: kept with the
    /// rest of your view, never sent to Helix, Procore or the company server. A connected project is
    /// remembered by its Helix/Procore project ("procore|123"), others by their id ("project|abc").
    /// </summary>
    public List<string> ArchivedProjects { get; set; } = new();

    /// <summary>
    /// Your favorite projects, listed first under "Favorites". Personal to this PC like the archive,
    /// and remembered the same way.
    /// </summary>
    public List<string> FavoriteProjects { get; set; } = new();

    /// <summary>Show archived projects in the list (dimmed) so they can be unarchived.</summary>
    public bool ShowArchived { get; set; }

    /// <summary>The item details pane beside the list is on (View → Item details).</summary>
    public bool ShowItemDetails { get; set; }

    /// <summary>Width of the project list, in pixels (drag the bar beside it).</summary>
    public double ProjectsPaneWidth { get; set; } = 290;

    /// <summary>
    /// Item-list columns you turned off (right-click a column heading), by the column's name. The Folder
    /// column starts hidden: the title says the same, and hovering it shows where the folder is. Names of
    /// columns that no longer exist ("DueColumn", merged into Due) are ignored.
    /// </summary>
    public List<string> HiddenColumns { get; set; } = new() { "FolderColumn" };

    /// <summary>
    /// Brings a view saved by an older version up to date, once. Version 1: a list still grouped the old
    /// default way (project, then type) is grouped by when items are due. Version 2: the Folder column
    /// is hidden (right-click a heading to show it again).
    /// </summary>
    public void Upgrade()
    {
        if (Version < 1 && GroupBy == "project-type")
        {
            GroupBy = "urgency";
        }

        if (Version < 2 && !HiddenColumns.Contains("FolderColumn"))
        {
            HiddenColumns.Add("FolderColumn");
        }

        Version = CurrentVersion;
    }
}

/// <summary>Who the Ball in court filter starts with: you, once your Helix or Procore name is known.</summary>
public static class DefaultPeople
{
    /// <summary>
    /// Your names to check (Helix and Procore may spell them differently), or none when someone, or
    /// anyone at your company, is already checked.
    /// </summary>
    public static IReadOnlyList<string> ToCheck(IReadOnlyCollection<string> checkedPeople, bool anyoneAtCompany, IEnumerable<string?> yourNames) =>
        checkedPeople.Count > 0 || anyoneAtCompany
            ? Array.Empty<string>()
            : yourNames.Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
            state.GroupBy ??= "urgency";
            state.SortColumn ??= "Number";
            state.SortPreset ??= "";
            state.ArchivedProjects ??= new List<string>();
            state.FavoriteProjects ??= new List<string>();
            state.HiddenColumns ??= new List<string>();
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
