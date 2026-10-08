using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>
/// What CA Tracker itself knows about an item, sent with it so Workload Tracker's lists can show the
/// desktop's Discipline and Folder status columns: the discipline as the list shows it (without "(auto)")
/// and whether it was detected or set by hand, where the item's CA folder stands, and the folder relative
/// to the project's CA folder. Values are already cut to the contract, so they hash the same as sent.
/// </summary>
public sealed record ItemFacts(string? Discipline, string? DisciplineSource, string FolderStatus, string? FolderPath)
{
    private const int MaxDiscipline = 100;
    private const int MaxFolderPath = 500;

    /// <summary>Changes when anything sent changes; kept per item once the tracker has accepted it.</summary>
    public string Fingerprint => FingerprintOf(Discipline, DisciplineSource, FolderStatus, FolderPath);

    /// <summary>The facts a built item carries (null when it carries none).</summary>
    public static string? FingerprintOf(CaItemRequest item) =>
        item.FolderStatus is null ? null : FingerprintOf(item.Discipline, item.DisciplineSource, item.FolderStatus, item.FolderPath);

    private static string FingerprintOf(string? discipline, string? source, string status, string? path) =>
        string.Join('\u001f', discipline ?? "", source ?? "", status, path ?? "");

    /// <summary>The contract's folder status for a row; null for rows that have no item (they aren't sent).</summary>
    public static string? FolderStatusOf(RowStatus status) => status switch
    {
        RowStatus.ReadyToReturn => FolderStatusEnum.ReadyToReturn,
        RowStatus.Reopened => FolderStatusEnum.Reopened,
        RowStatus.Duplicate => FolderStatusEnum.Duplicate,
        RowStatus.Open => FolderStatusEnum.Open,
        RowStatus.Filed => FolderStatusEnum.Filed,
        RowStatus.NoFolder => FolderStatusEnum.NoFolder,
        _ => null
    };

    /// <summary>
    /// A compared row's facts, or null when it has no item. "Set" with no discipline means it was set to
    /// none by hand; neither a discipline nor a source means none was found.
    /// </summary>
    public static ItemFacts? Of(ComparisonRow row, TrackerProject project)
    {
        if (row.Item is null || FolderStatusOf(row.Status) is not { } status)
        {
            return null;
        }

        var source = row.DisciplineSource switch
        {
            SmcCaTracker.Core.Comparison.DisciplineSource.Detected => DisciplineSourceEnum.Detected,
            SmcCaTracker.Core.Comparison.DisciplineSource.Manual => DisciplineSourceEnum.Set,
            _ => null
        };
        var discipline = row.Discipline is null ? null : Cut(row.Discipline.Label, MaxDiscipline);
        string? path = null;
        if (row.Folder is { } folder)
        {
            path = Cut(string.IsNullOrWhiteSpace(project.CaFolder) ? folder.FullPath : Path.GetRelativePath(project.CaFolder, folder.FullPath), MaxFolderPath);
        }

        return new ItemFacts(discipline, source, status, path);
    }

    /// <summary>
    /// The facts of every item of a Helix or Procore project that this PC compares, keyed by
    /// <see cref="OutboxEntry.ItemKey"/>. Each local project linked to it is compared with its saved folder
    /// lists (<paramref name="folders"/>; the drive isn't read). Of a Base Build / Tenant Fit Out pair, an
    /// item's row comes from the entry whose part includes it, and a row with a folder wins over one without.
    /// Items no entry compares (outside both parts, older Procore revisions) and item types whose folders
    /// haven't been listed or couldn't be read have no facts: what the tracker has for them is kept.
    /// </summary>
    public static Dictionary<string, ItemFacts> ForProject(
        ProviderSnapshot snapshot,
        TrackerSettings settings,
        Func<TrackerProject, string, FolderLayout, TypeFolderScan?> folders)
    {
        var chosen = new Dictionary<string, (ItemFacts Facts, FolderEntry? Folder)>(StringComparer.Ordinal);
        var linked = settings.Projects.Where(project => project.Connection is { } connection
            && string.Equals(connection.Provider, snapshot.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(connection.ProjectId, snapshot.ProjectId, StringComparison.Ordinal));
        foreach (var project in linked)
        {
            var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var comparison = ProjectComparer.Compare(snapshot, project, settings, (itemType, layout) =>
            {
                var scan = folders(project, itemType, layout);
                if (scan is null || (scan.Error is not null && !string.IsNullOrWhiteSpace(project.CaFolder)))
                {
                    // Not listed yet, or the drive couldn't be read: nothing is known about its folders.
                    unknown.Add(itemType);
                    return new TypeFolderScan { ItemType = itemType };
                }

                return scan;
            });

            foreach (var type in comparison.Types.Values.Where(type => !unknown.Contains(type.ItemType)))
            {
                foreach (var row in type.Rows)
                {
                    if (Of(row, project) is not { } facts)
                    {
                        continue;
                    }

                    var key = OutboxEntry.ItemKey(row.Item!.ItemType, row.Item.ItemId);
                    if (!chosen.TryGetValue(key, out var kept) || Better(row.Folder, kept.Folder))
                    {
                        chosen[key] = (facts, row.Folder);
                    }
                }
            }
        }

        return chosen.ToDictionary(pair => pair.Key, pair => pair.Value.Facts, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a row's folder beats the one kept: any folder beats none, then an active folder beats a
    /// returned one, then the first path. Rows of an item with several folders come in no fixed order, so
    /// the choice is made the same way every time (the folder path would otherwise change between sends).
    /// </summary>
    private static bool Better(FolderEntry? candidate, FolderEntry? kept)
    {
        if (candidate is null)
        {
            return false;
        }

        if (kept is null)
        {
            return true;
        }

        if (candidate.Location != kept.Location)
        {
            return candidate.Location == FolderLocation.Active;
        }

        return string.Compare(candidate.FullPath, kept.FullPath, StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static string Cut(string value, int maxLength)
    {
        var text = value.Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..cut];
    }
}
