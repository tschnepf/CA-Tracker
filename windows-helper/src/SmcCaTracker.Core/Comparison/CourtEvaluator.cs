using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Comparison;

public enum CourtState
{
    /// <summary>Someone from the company is responsible right now.</summary>
    InCourt,

    /// <summary>Someone from the company has a later, unreviewed workflow step.</summary>
    ComingUp,

    /// <summary>No one from the company holds the item.</summary>
    NotHeld,

    /// <summary>Closed, complete, or voided.</summary>
    Closed,

    /// <summary>The provider did not say who holds it (e.g. Procore's visible grid).</summary>
    Unknown
}

public sealed record CompanyIdentity(string? CompanyId, string? CompanyName)
{
    public bool IsKnown => !string.IsNullOrWhiteSpace(CompanyId) || !string.IsNullOrWhiteSpace(CompanyName);

    public string DisplayName => !string.IsNullOrWhiteSpace(CompanyName) ? CompanyName! : CompanyId ?? "your company";

    public bool Matches(string? companyId, string? companyName)
    {
        if (!string.IsNullOrWhiteSpace(CompanyId) && !string.IsNullOrWhiteSpace(companyId))
        {
            return string.Equals(CompanyId.Trim(), companyId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(CompanyName)
            && !string.IsNullOrWhiteSpace(companyName)
            && string.Equals(CompanyName.Trim(), companyName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static CompanyIdentity For(ProjectMapping? mapping, ProviderSnapshot? snapshot)
    {
        if (mapping is not null && (!string.IsNullOrWhiteSpace(mapping.CompanyId) || !string.IsNullOrWhiteSpace(mapping.CompanyName)))
        {
            return new CompanyIdentity(mapping.CompanyId, mapping.CompanyName);
        }

        return new CompanyIdentity(snapshot?.User.CompanyId, snapshot?.User.Company);
    }
}

public static class CourtEvaluator
{
    public static CourtState Evaluate(SnapshotItem item, CompanyIdentity company, bool holdersReported)
    {
        if (item.Closed)
        {
            return CourtState.Closed;
        }

        if (!holdersReported || !company.IsKnown)
        {
            return CourtState.Unknown;
        }

        var ours = item.Holders.Where(holder => company.Matches(holder.CompanyId, holder.Company)).ToList();
        if (ours.Any(holder => string.Equals(holder.State, "current", StringComparison.OrdinalIgnoreCase)))
        {
            return CourtState.InCourt;
        }

        return ours.Count > 0 ? CourtState.ComingUp : CourtState.NotHeld;
    }

    public static bool IsReturned(SnapshotItem item, CourtState state, ReturnedRule rule)
    {
        var status = item.Status.Trim();
        if (rule.NeverReturnedStatuses.Any(value => string.Equals(value.Trim(), status, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (rule.AlwaysReturnedStatuses.Any(value => string.Equals(value.Trim(), status, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return state switch
        {
            CourtState.Closed => rule.WhenClosed,
            CourtState.NotHeld => rule.WhenNoCompanyHolder,
            CourtState.ComingUp => rule.WhenOnlyComingUp,
            _ => false
        };
    }

    public static string Describe(CourtState state, CompanyIdentity company) => state switch
    {
        CourtState.InCourt => $"In {company.DisplayName}'s court",
        CourtState.ComingUp => $"Coming up for {company.DisplayName}",
        CourtState.NotHeld => $"Out of {company.DisplayName}'s court",
        CourtState.Closed => "Closed",
        _ => "Holder not reported"
    };
}
