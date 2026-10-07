namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>
/// What waits to be sent to Workload Tracker for one Helix or Procore project (decision D8). Scans
/// merge into one entry per project instead of queueing up, which keeps the outbox capped: it names
/// the items that changed or went away since the last send, and the sender reads their current state
/// from the app's data when it sends. Written only while paired, for the account it was written for.
/// </summary>
public sealed class OutboxEntry
{
    /// <summary>Past this many changed items an entry sends every item instead.</summary>
    public const int CollapseThreshold = 5000;

    public string Provider { get; set; } = "";
    public string ProjectId { get; set; } = "";

    /// <summary>The Workload Tracker account the changes were recorded for.</summary>
    public string Account { get; set; } = "";

    /// <summary>Send every item (first contact, resendAll, a collapsed entry), not only changes.</summary>
    public bool FullSet { get; set; }

    /// <summary>Items added or changed, as "itemType|itemId".</summary>
    public HashSet<string> Changed { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Items that went away, as "itemType|itemId".</summary>
    public HashSet<string> Removed { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Item types a merged scan read completely or saw items leave: their removals can be applied.</summary>
    public HashSet<string> Verified { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped on every merge, so a send only clears the entry when nothing arrived meanwhile.</summary>
    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    public static string ItemKey(string itemType, string itemId) => $"{itemType.Trim().ToLowerInvariant()}|{itemId}";

    public static (string ItemType, string ItemId) SplitKey(string key)
    {
        var bar = key.IndexOf('|');
        return bar < 0 ? ("", key) : (key[..bar], key[(bar + 1)..]);
    }

    public OutboxEntry Copy() => new()
    {
        Provider = Provider,
        ProjectId = ProjectId,
        Account = Account,
        FullSet = FullSet,
        Changed = new HashSet<string>(Changed, StringComparer.Ordinal),
        Removed = new HashSet<string>(Removed, StringComparer.Ordinal),
        Verified = new HashSet<string>(Verified, StringComparer.OrdinalIgnoreCase),
        Version = Version,
        UpdatedAt = UpdatedAt,
        Attempts = Attempts,
        NextAttemptAt = NextAttemptAt,
        LastError = LastError
    };
}
