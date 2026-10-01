using System.Text;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Comparison;

public enum ProjectPhase
{
    /// <summary>The whole project; nothing is filtered.</summary>
    Whole,
    BaseBuild,
    TenantFitOut
}

/// <summary>
/// Base Build / Tenant Fit Out: which part of a job a tracker project covers, and whether items and
/// folders that are shared with the other part need filtering by what their names say.
/// </summary>
public sealed class PhaseScope
{
    public ProjectPhase Phase { get; set; } = ProjectPhase.Whole;

    /// <summary>The Helix/Procore project holds both parts; keep items whose title, number or contract says this part.</summary>
    public bool FilterItems { get; set; }

    /// <summary>The CA folders hold both parts; keep folders whose name says this part.</summary>
    public bool FilterFolders { get; set; }

    /// <summary>Also keep items and folders that mention neither part.</summary>
    public bool IncludeUnlabeled { get; set; }

    public bool IsSplit => Phase != ProjectPhase.Whole;
}

public static class Phases
{
    private static readonly string[] BaseBuildWords =
    {
        "BB", "BASE BUILD", "BASEBUILD", "BASE BUILDING", "BASE BLDG", "BASE BLD"
    };

    private static readonly string[] TenantFitOutWords =
    {
        "TFO", "TENANT FIT OUT", "TENANT FITOUT", "TENANT FIT", "FIT OUT", "FITOUT", "TENANT IMPROVEMENT", "TENANT IMPROVEMENTS"
    };

    public static string Short(ProjectPhase phase) => phase switch
    {
        ProjectPhase.BaseBuild => "BB",
        ProjectPhase.TenantFitOut => "TFO",
        _ => ""
    };

    public static string Label(ProjectPhase phase) => phase switch
    {
        ProjectPhase.BaseBuild => "Base Build (BB)",
        ProjectPhase.TenantFitOut => "Tenant Fit Out (TFO)",
        _ => "Whole project"
    };

    /// <summary>
    /// Finds BB/TFO (or Base Build / Tenant Fit Out, Fit-Out, ...) as whole words. When both appear,
    /// the one that comes first wins.
    /// </summary>
    public static ProjectPhase? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = Normalize(text);
        var baseBuild = FirstIndex(normalized, BaseBuildWords);
        var fitOut = FirstIndex(normalized, TenantFitOutWords);
        if (baseBuild < 0 && fitOut < 0)
        {
            return null;
        }

        if (fitOut < 0 || (baseBuild >= 0 && baseBuild <= fitOut))
        {
            return ProjectPhase.BaseBuild;
        }

        return ProjectPhase.TenantFitOut;
    }

    public static bool IncludesItem(PhaseScope? scope, SnapshotItem item) =>
        scope is null || !scope.IsSplit || !scope.FilterItems
        || Includes(scope, Detect($"{item.Number} {item.Title} {item.Contract}"));

    public static bool IncludesFolder(PhaseScope? scope, string folderName) =>
        scope is null || !scope.IsSplit || !scope.FilterFolders
        || Includes(scope, Detect(folderName));

    /// <summary>A phase suggested by a folder path, e.g. "...\CMH04 TFO\CA" → Tenant Fit Out.</summary>
    public static ProjectPhase SuggestFromPath(string? path) => Detect(path) ?? ProjectPhase.Whole;

    private static bool Includes(PhaseScope scope, ProjectPhase? detected) =>
        detected == scope.Phase || (detected is null && scope.IncludeUnlabeled);

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(" ");
        var lastWasSpace = true;
        foreach (var character in text.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        if (!lastWasSpace)
        {
            builder.Append(' ');
        }

        return builder.ToString();
    }

    private static int FirstIndex(string normalized, IEnumerable<string> words)
    {
        var first = -1;
        foreach (var word in words)
        {
            var index = normalized.IndexOf($" {word} ", StringComparison.Ordinal);
            if (index >= 0 && (first < 0 || index < first))
            {
                first = index;
            }
        }

        return first;
    }
}
