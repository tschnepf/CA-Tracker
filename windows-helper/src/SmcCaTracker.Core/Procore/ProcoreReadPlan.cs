using SmcCaTracker.Core.Models;

namespace SmcCaTracker.Core.Procore;

/// <summary>How much of a Procore project a background read asks for.</summary>
public enum ProcoreReadKind
{
    /// <summary>Every RFI and Submittal.</summary>
    Full,

    /// <summary>Only items changed since the last read.</summary>
    Changes,

    /// <summary>
    /// Only items changed since the last read, plus how many items each list has in Procore: a count
    /// that differs from the saved list means items were removed (or appeared without changing), and
    /// the project is then read in full.
    /// </summary>
    ChangesAndCount
}

/// <summary>
/// Decides how much each background read of a Procore project asks for, so after the first read only
/// changed items are read. A project is read in full the first time, then once a week as a safety net,
/// and whenever its counts in Procore stop matching the saved list. The times come from the sync history,
/// so they hold across restarts of the app.
/// </summary>
public static class ProcoreReadPlan
{
    /// <summary>A project is read in full at least this often, whatever its counts say.</summary>
    public static readonly TimeSpan FullReadEvery = TimeSpan.FromDays(7);

    /// <summary>Sync history modes: a read of every item, and a changes read whose counts matched Procore.</summary>
    public const string FullMode = "full";
    public const string ChangesMode = "changes";
    public const string CheckedMode = "checked";

    /// <param name="hasSnapshot">Whether the project's items are saved.</param>
    /// <param name="lastFullRead">When the project was last read in full (a complete "full" sync run).</param>
    /// <param name="lastCountCheck">When its counts last matched Procore (a complete "checked" sync run).</param>
    /// <param name="countCheckEvery">How often the counts are compared (Settings: FullReadHours).</param>
    public static ProcoreReadKind For(bool hasSnapshot, DateTimeOffset? lastFullRead, DateTimeOffset? lastCountCheck, DateTimeOffset now, TimeSpan countCheckEvery)
    {
        if (!hasSnapshot || lastFullRead is not { } full || now - full >= FullReadEvery || full > now)
        {
            return ProcoreReadKind.Full;
        }

        var confirmed = lastCountCheck is { } check && check > full ? check : full;
        return now - confirmed >= countCheckEvery ? ProcoreReadKind.ChangesAndCount : ProcoreReadKind.Changes;
    }

    /// <summary>
    /// The item types whose count in Procore (<paramref name="totals"/>, by item type; null when Procore
    /// didn't say) differs from the saved list's. Types Procore gave no count for aren't compared.
    /// </summary>
    public static IReadOnlyList<string> Mismatched(ProviderSnapshot snapshot, IReadOnlyDictionary<string, int?> totals)
    {
        var mismatched = new List<string>();
        foreach (var (type, total) in totals)
        {
            if (total is not { } count)
            {
                continue;
            }

            var saved = snapshot.Items.Count(item => string.Equals(item.ItemType, type, StringComparison.OrdinalIgnoreCase));
            if (saved != count)
            {
                mismatched.Add(type);
            }
        }

        return mismatched;
    }
}
