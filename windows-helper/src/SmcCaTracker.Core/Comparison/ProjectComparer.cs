using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Comparison;

public enum RowStatus
{
    /// <summary>Returned per the rule, but the folder is still in the active folder.</summary>
    ReadyToReturn,

    /// <summary>Folder is in ~Returned but the item is back in the company's court.</summary>
    Reopened,

    /// <summary>More than one folder matches the same item.</summary>
    Duplicate,

    /// <summary>The folder name matches more than one item.</summary>
    Ambiguous,

    /// <summary>A complete scan has no item for this folder.</summary>
    NotInProvider,

    /// <summary>Item has no folder yet.</summary>
    NoFolder,

    /// <summary>Folder is active and the item is still open for the company.</summary>
    Open,

    /// <summary>Folder is in ~Returned and the item is returned.</summary>
    Filed,

    /// <summary>A partial scan (Procore visible grid) did not include this folder's item.</summary>
    NotInView,

    /// <summary>No Helix/Procore data for this item type yet.</summary>
    NoData
}

public sealed class ComparisonRow
{
    public string ItemType { get; init; } = "";
    public SnapshotItem? Item { get; init; }
    public FolderEntry? Folder { get; init; }
    public CourtState Court { get; init; } = CourtState.Unknown;
    public bool IsReturned { get; init; }
    public RowStatus Status { get; init; }
    public MatchKind Match { get; init; }
    public string Detail { get; init; } = "";

    public bool NeedsAttention => Status is RowStatus.ReadyToReturn or RowStatus.Reopened or RowStatus.Duplicate
        or RowStatus.Ambiguous or RowStatus.NotInProvider;

    public bool CanMarkReturned => Folder?.Location == FolderLocation.Active;
    public bool CanMoveBack => Folder?.Location == FolderLocation.Returned;

    public string SortNumber => Item?.Number ?? Folder?.Name ?? "";

    public static string StatusLabel(RowStatus status) => status switch
    {
        RowStatus.ReadyToReturn => "Ready to move to returned",
        RowStatus.Reopened => "Back in court — folder is in returned",
        RowStatus.Duplicate => "More than one folder",
        RowStatus.Ambiguous => "Folder matches several items",
        RowStatus.NotInProvider => "Not found in provider",
        RowStatus.NoFolder => "No folder",
        RowStatus.Open => "Open",
        RowStatus.Filed => "Returned",
        RowStatus.NotInView => "Not in last Procore view",
        _ => "No provider data yet"
    };
}

public sealed class TypeComparison
{
    public string ItemType { get; init; } = "";
    public TypeFolderScan Scan { get; init; } = new();
    public bool HasProviderData { get; init; }
    public bool ProviderComplete { get; init; }
    public DateTimeOffset? ScannedAt { get; init; }
    public IReadOnlyList<ComparisonRow> Rows { get; init; } = Array.Empty<ComparisonRow>();

    public int Count(RowStatus status) => Rows.Count(row => row.Status == status);
    public int AttentionCount => Rows.Count(row => row.NeedsAttention);
}

public sealed class ProjectComparison
{
    public ProviderSnapshot? Snapshot { get; init; }
    public TrackerProject Project { get; init; } = new();
    public FolderLayout Folders { get; init; } = new();
    public CompanyIdentity Company { get; init; } = new(null, null);
    public IReadOnlyDictionary<string, TypeComparison> Types { get; init; } = new Dictionary<string, TypeComparison>();
    public DateTimeOffset ComparedAt { get; init; } = DateTimeOffset.Now;

    public TypeComparison For(string itemType) => Types[itemType];
}

public static class ProjectComparer
{
    public static ProjectComparison Compare(ProviderSnapshot? snapshot, TrackerProject project, TrackerSettings settings)
    {
        var layout = project.EffectiveFolders(settings.Folders);
        var company = CompanyIdentity.For(project, snapshot);
        var holdersReported = snapshot is not null && string.Equals(snapshot.Provider, Providers.Helix, StringComparison.OrdinalIgnoreCase);
        var types = new Dictionary<string, TypeComparison>(StringComparer.OrdinalIgnoreCase);
        foreach (var itemType in ItemTypes.All)
        {
            var scan = FolderScanner.Scan(project.CaFolder, itemType, layout);
            types[itemType] = CompareType(itemType, scan, snapshot, company, holdersReported, settings.Returned);
        }

        return new ProjectComparison
        {
            Snapshot = snapshot,
            Project = project,
            Folders = layout,
            Company = company,
            Types = types
        };
    }

    public static TypeComparison CompareType(
        string itemType,
        TypeFolderScan scan,
        ProviderSnapshot? snapshot,
        CompanyIdentity company,
        bool holdersReported,
        ReturnedRule rule)
    {
        var hasData = snapshot is not null && snapshot.Covers(itemType);
        var coverage = snapshot?.CoverageFor(itemType);
        var complete = hasData && (coverage?.Complete ?? snapshot!.Complete);
        var items = hasData
            ? snapshot!.Items.Where(item => string.Equals(item.ItemType, itemType, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<SnapshotItem>();

        var matches = FolderMatcher.Match(scan.Folders, items);
        var foldersByItem = matches
            .Where(match => match.Item is not null)
            .GroupBy(match => match.Item!.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var rows = new List<ComparisonRow>();
        foreach (var match in matches)
        {
            if (match.Item is null)
            {
                var status = !hasData ? RowStatus.NoData
                    : match.Kind == MatchKind.Ambiguous ? RowStatus.Ambiguous
                    : complete ? RowStatus.NotInProvider
                    : RowStatus.NotInView;
                rows.Add(new ComparisonRow
                {
                    ItemType = itemType,
                    Folder = match.Folder,
                    Status = status,
                    Match = match.Kind,
                    Detail = status == RowStatus.Ambiguous
                        ? $"Matches {string.Join(", ", match.Candidates.Select(item => item.Number))}"
                        : ""
                });
                continue;
            }

            var item = match.Item;
            var court = CourtEvaluator.Evaluate(item, company, holdersReported);
            var returned = CourtEvaluator.IsReturned(item, court, rule);
            RowStatus rowStatus;
            if (foldersByItem[item.ItemId].Count > 1)
            {
                rowStatus = RowStatus.Duplicate;
            }
            else if (match.Folder.Location == FolderLocation.Active)
            {
                rowStatus = returned ? RowStatus.ReadyToReturn : RowStatus.Open;
            }
            else
            {
                rowStatus = !returned && court is CourtState.InCourt or CourtState.ComingUp
                    ? RowStatus.Reopened
                    : RowStatus.Filed;
            }

            rows.Add(new ComparisonRow
            {
                ItemType = itemType,
                Item = item,
                Folder = match.Folder,
                Court = court,
                IsReturned = returned,
                Status = rowStatus,
                Match = match.Kind,
                Detail = match.Kind == MatchKind.Loose ? "Matched loosely by number" : ""
            });
        }

        foreach (var item in items.Where(item => !foldersByItem.ContainsKey(item.ItemId)))
        {
            var court = CourtEvaluator.Evaluate(item, company, holdersReported);
            rows.Add(new ComparisonRow
            {
                ItemType = itemType,
                Item = item,
                Court = court,
                IsReturned = CourtEvaluator.IsReturned(item, court, rule),
                Status = RowStatus.NoFolder
            });
        }

        rows.Sort((left, right) =>
        {
            var byStatus = left.Status.CompareTo(right.Status);
            return byStatus != 0 ? byStatus : ItemNumber.CompareNatural(left.SortNumber, right.SortNumber);
        });

        return new TypeComparison
        {
            ItemType = itemType,
            Scan = scan,
            HasProviderData = hasData,
            ProviderComplete = complete,
            ScannedAt = coverage?.ScannedAt ?? snapshot?.ScannedAt,
            Rows = rows
        };
    }
}
