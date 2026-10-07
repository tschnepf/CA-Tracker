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
