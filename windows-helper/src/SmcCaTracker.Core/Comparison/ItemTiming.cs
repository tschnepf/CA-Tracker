using System.Globalization;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.Core.Comparison;

public enum DueFilter
{
    Any,
    Overdue,
    DueToday,
    DueWithin
}

/// <summary>The item list's "When it's due" headings, in the order they're listed.</summary>
public enum Urgency
{
    Overdue,
    DueToday,
    NextWeek,
    Later,
    NoDueDate,
    Closed
}

/// <summary>Who holds an item now, how long they've had it, and how long until it's due.</summary>
public static class ItemTiming
{
    /// <summary>
    /// People the item is with right now (Helix "In court", Procore "Ball In Court"), once each, also
    /// when Procore wrote one of them with the company in brackets.
    /// </summary>
    public static IReadOnlyList<SnapshotHolder> CurrentHolders(SnapshotItem? item) =>
        item is null || item.Closed
            ? Array.Empty<SnapshotHolder>()
            : item.Holders
                .Where(holder => string.Equals(holder.State, "current", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(holder.Name))
                .GroupBy(holder => PersonNames.WithoutCompany(holder.Name), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

    /// <summary>Whole days until the due date (negative when overdue); null when closed or no due date.</summary>
    public static int? DaysTillDue(SnapshotItem? item, DateTime today) =>
        item is { Closed: false, DueAt: { } due } ? (due.ToLocalTime().Date - today.Date).Days : null;

    /// <summary>Whole days the current holder has had the item; null when the site doesn't say.</summary>
    public static int? DaysInCourt(SnapshotItem? item, DateTime today)
    {
        var since = CurrentHolders(item)
            .Select(holder => holder.Since)
            .Where(value => value is not null)
            .Min();
        return since is null ? null : Math.Max(0, (today.Date - since.Value.ToLocalTime().Date).Days);
    }

    /// <summary>
    /// The Due column: the date and how far off it is ("Sep 7 · 26 days overdue", "Oct 3 · today",
    /// "Oct 7 · in 4 days"). The year shows only when it isn't this year; closed items show just the date.
    /// </summary>
    public static string DueText(SnapshotItem? item, DateTime today, IFormatProvider? culture = null)
    {
        if (item?.DueAt is not { } dueAt)
        {
            return "";
        }

        var due = dueAt.ToLocalTime().Date;
        var date = due.ToString(due.Year == today.Year ? "MMM d" : "MMM d, yyyy", culture ?? CultureInfo.CurrentCulture);
        return DaysTillDue(item, today) switch
        {
            null => date,
            < -1 and var days => $"{date} \u00b7 {-days} days overdue",
            -1 => $"{date} \u00b7 1 day overdue",
            0 => $"{date} \u00b7 today",
            1 => $"{date} \u00b7 tomorrow",
            var days => $"{date} \u00b7 in {days} days"
        };
    }

    /// <summary>Which "When it's due" heading an item goes under. A folder with no item has no due date.</summary>
    public static Urgency UrgencyOf(SnapshotItem? item, DateTime today) =>
        item is { Closed: true } ? Urgency.Closed
        : DaysTillDue(item, today) switch
        {
            null => Urgency.NoDueDate,
            < 0 => Urgency.Overdue,
            0 => Urgency.DueToday,
            <= 7 => Urgency.NextWeek,
            _ => Urgency.Later
        };

    public static string UrgencyLabel(Urgency urgency) => urgency switch
    {
        Urgency.Overdue => "Overdue",
        Urgency.DueToday => "Due today",
        Urgency.NextWeek => "Due in the next 7 days",
        Urgency.Later => "Due later",
        Urgency.Closed => "Closed",
        _ => "No due date"
    };

    public static bool MatchesDue(SnapshotItem? item, DueFilter filter, int withinDays, DateTime today)
    {
        if (filter == DueFilter.Any)
        {
            return true;
        }

        var days = DaysTillDue(item, today);
        return days is not null && filter switch
        {
            DueFilter.Overdue => days < 0,
            DueFilter.DueToday => days == 0,
            DueFilter.DueWithin => days >= 0 && days <= Math.Max(0, withinDays),
            _ => true
        };
    }
}
