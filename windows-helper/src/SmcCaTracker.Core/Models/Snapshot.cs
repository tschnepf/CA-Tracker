namespace SmcCaTracker.Core.Models;

/// <summary>
/// What the Chrome extension last saw in Helix or Procore for one project.
/// Mirrors integrations/windows-helper/snapshot.js in the extension.
/// </summary>
public sealed class ProviderSnapshot
{
    public int SchemaVersion { get; set; }
    public string Provider { get; set; } = "";
    public string? AccountId { get; set; }
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string? ProjectUrl { get; set; }
    public List<string> ItemTypes { get; set; } = new();
    public bool Complete { get; set; }
    public DateTimeOffset ScannedAt { get; set; }
    public SnapshotUser User { get; set; } = new();
    public List<SnapshotItem> Items { get; set; } = new();

    /// <summary>Per item type: when it was last scanned and whether that scan saw every item.</summary>
    public Dictionary<string, TypeCoverage> Coverage { get; set; } = new();

    public bool Covers(string itemType) => ItemTypes.Contains(itemType, StringComparer.OrdinalIgnoreCase);

    public TypeCoverage? CoverageFor(string itemType) =>
        Coverage.TryGetValue(itemType, out var coverage) ? coverage : null;
}

public sealed class TypeCoverage
{
    public DateTimeOffset ScannedAt { get; set; }
    public bool Complete { get; set; }
}

public sealed class SnapshotUser
{
    public string FullName { get; set; } = "";
    public string? CompanyId { get; set; }
    public string? Company { get; set; }
}

public sealed class SnapshotItem
{
    public string ItemType { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public int? StatusRank { get; set; }
    public bool Closed { get; set; }
    public string Responsible { get; set; } = "";
    public List<SnapshotHolder> Holders { get; set; } = new();
    public int AttachmentCount { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public string? SourceUrl { get; set; }
}

public sealed class SnapshotHolder
{
    public string Name { get; set; } = "";
    public string? CompanyId { get; set; }
    public string? Company { get; set; }

    /// <summary>"current" (in court now) or "upcoming" (a later workflow step).</summary>
    public string State { get; set; } = "";
}

public static class Providers
{
    public const string Helix = "helix";
    public const string Procore = "procore";
    public static readonly IReadOnlyList<string> All = new[] { Helix, Procore };

    public static string Label(string provider) =>
        string.Equals(provider, Procore, StringComparison.OrdinalIgnoreCase) ? "Procore" : "Helix";
}

public static class ItemTypes
{
    public const string Rfi = "rfi";
    public const string Submittal = "submittal";
    public static readonly IReadOnlyList<string> All = new[] { Rfi, Submittal };

    public static string Plural(string itemType) =>
        string.Equals(itemType, Submittal, StringComparison.OrdinalIgnoreCase) ? "Submittals" : "RFIs";
}
