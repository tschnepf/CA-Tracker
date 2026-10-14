using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>
/// A person assigned to an item lineage (provider project, type and number) on a Workload Tracker
/// project (decision D4), as this PC knows it: read back from the tracker, or changed here and not yet
/// sent (<see cref="Pending"/>). The latest change wins on the tracker.
/// </summary>
public sealed class WorkloadAssignment
{
    public int TrackerProjectId { get; set; }
    public string Provider { get; set; } = "";
    public string Host { get; set; } = "";
    public string ProviderProjectId { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string Number { get; set; } = "";
    public int PersonId { get; set; }
    public string PersonName { get; set; } = "";

    /// <summary>False when the person has left: shown so the item can be reassigned.</summary>
    public bool PersonActive { get; set; } = true;

    /// <summary>False once unassigned (kept, so the latest change can win everywhere).</summary>
    public bool Active { get; set; }

    public DateTimeOffset AssignedAt { get; set; }
    public string? AssignedByName { get; set; }

    /// <summary>Set by the tracker when the item closed or left the provider.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Changed here and not yet accepted by the tracker.</summary>
    public bool Pending { get; set; }

    public string Key => $"{ItemKey}|{PersonId}";

    /// <summary>The tracker project and item lineage (<see cref="WorkloadItemRef.Key"/>).</summary>
    public string ItemKey => $"{TrackerProjectId}|{LineageKey}";

    /// <summary>The item across its revisions: provider, project, type and number.</summary>
    public string LineageKey => LineageKeyOf(Provider, ProviderProjectId, ItemType, Number);

    public static string LineageKeyOf(string provider, string providerProjectId, string itemType, string number) =>
        $"{provider.ToLowerInvariant()}|{providerProjectId}|{itemType.ToLowerInvariant()}|{number}";

    public WorkloadItemRef Item => new(TrackerProjectId, Provider, Host, ProviderProjectId, ItemType, Number);

    public WorkloadAssignment Copy() => (WorkloadAssignment)MemberwiseClone();
}

/// <summary>
/// Someone marked an item revision complete in Workload Tracker, read back with a tracker project's
/// assignments. It is the person's word, not the provider's: the item stays open until it is returned in
/// Helix or Procore. The tracker lists a mark only while the item is open there and still at the revision
/// that was marked. Not the same as <see cref="WorkloadAssignment.CompletedAt"/> (the item closed in the provider).
/// </summary>
public sealed class WorkloadCompletion
{
    public int TrackerProjectId { get; set; }
    public string Provider { get; set; } = "";
    public string Host { get; set; } = "";
    public string ProviderProjectId { get; set; } = "";
    public string ItemType { get; set; } = "";

    /// <summary>The marked revision's id in Helix or Procore (Procore keeps each revision as its own item).</summary>
    public string ProviderItemId { get; set; } = "";
    public string Number { get; set; } = "";
    public string? Revision { get; set; }
    public int PersonId { get; set; }
    public string PersonName { get; set; } = "";

    /// <summary>Who clicked it, when not the person themself.</summary>
    public string? MarkedByName { get; set; }
    public DateTimeOffset MarkedAt { get; set; }

    public string Key => $"{TrackerProjectId}|{ItemKey}|{PersonId}";

    /// <summary>The item revision that was marked: provider, project, type and provider item id.</summary>
    public string ItemKey => ItemKeyOf(Provider, ProviderProjectId, ItemType, ProviderItemId);

    public static string ItemKeyOf(string provider, string providerProjectId, string itemType, string providerItemId) =>
        $"{provider.ToLowerInvariant()}|{providerProjectId}|{itemType.ToLowerInvariant()}|{providerItemId}";

    public WorkloadCompletion Copy() => (WorkloadCompletion)MemberwiseClone();

    /// <summary>
    /// The marks on an item as this PC has it now, from an index by <see cref="ItemKey"/>: the same Helix or
    /// Procore item, still at the revision that was marked (a newer revision is new work). One per person.
    /// </summary>
    public static IReadOnlyList<WorkloadCompletion> On(
        IReadOnlyDictionary<string, IReadOnlyList<WorkloadCompletion>> index, ProviderConnection connection, SnapshotItem item)
    {
        if (!index.TryGetValue(ObservationBuilder.RevisionKey(connection, item), out var marks))
        {
            return [];
        }

        var revision = ObservationBuilder.RevisionOf(item);
        return marks
            .Where(mark => string.Equals(string.IsNullOrWhiteSpace(mark.Revision) ? null : mark.Revision, revision, StringComparison.Ordinal))
            .DistinctBy(mark => mark.PersonId)
            .ToList();
    }

    /// <summary>
    /// Marked complete in Workload Tracker but not done here yet: still open in Helix or Procore and neither
    /// returned (by the returned rule) nor filed in a Returned folder. The list flags these.
    /// </summary>
    public static bool AwaitsReturn(ComparisonRow row, IReadOnlyCollection<WorkloadCompletion> marks) =>
        marks.Count > 0 && row.Item is { Closed: false } && !row.IsReturned && row.Folder?.Location != FolderLocation.Returned;
}

/// <summary>An item lineage on a tracker project: what people are assigned to.</summary>
public sealed record WorkloadItemRef(int TrackerProjectId, string Provider, string Host, string ProviderProjectId, string ItemType, string Number)
{
    public string LineageKey => WorkloadAssignment.LineageKeyOf(Provider, ProviderProjectId, ItemType, Number);

    /// <summary>The key the store indexes active assignments by: tracker project and lineage.</summary>
    public string Key => $"{TrackerProjectId}|{LineageKey}";
}

/// <summary>A person on a Workload Tracker project's team (from its assignments list), who can be assigned items.</summary>
public sealed record TeamMember(int PersonId, string Name, string? Department, string? Role);

/// <summary>A Workload Tracker project found by search, offered when binding a local project entry.</summary>
public sealed record TrackerProjectChoice(int Id, string? Number, string Name, string? Status, string? Client)
{
    public string Label => string.IsNullOrWhiteSpace(Number) ? Name : $"{Number} · {Name}";
}
