namespace SmcCaTracker.Core.Procore;

/// <summary>
/// How CA Tracker keeps Procore up to date. Procore is read only through your own sign-in in the
/// built-in browser (Settings → Connections → Connect to Procore), so nothing has to be installed in
/// any company's Procore account: CA Tracker finds every company and project you can see and reads them.
/// </summary>
public sealed class ProcoreSettings
{
    /// <summary>Procore projects (by Procore project id) you chose not to sync.</summary>
    public List<string> SkippedProjectIds { get; set; } = new();

    /// <summary>When CA Tracker last listed your Procore companies and projects.</summary>
    public DateTimeOffset? LastDiscoveredAt { get; set; }

    /// <summary>How often the list of companies and projects is read again, to pick up new projects.</summary>
    public int DiscoverEveryHours { get; set; } = 6;

    public bool IsSkipped(string? projectId) =>
        !string.IsNullOrWhiteSpace(projectId) && SkippedProjectIds.Contains(projectId, StringComparer.Ordinal);

    /// <summary>Turns syncing of one project on or off. Returns true when something changed.</summary>
    public bool SetSkipped(string projectId, bool skipped)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return false;
        }

        if (skipped)
        {
            if (IsSkipped(projectId))
            {
                return false;
            }

            SkippedProjectIds.Add(projectId);
            return true;
        }

        return SkippedProjectIds.RemoveAll(id => string.Equals(id, projectId, StringComparison.Ordinal)) > 0;
    }

    public bool DiscoveryDue(DateTimeOffset now) =>
        LastDiscoveredAt is not { } last || now - last >= TimeSpan.FromHours(Math.Max(1, DiscoverEveryHours));
}

/// <summary>Encrypts secrets so only this Windows user on this PC can read them (DPAPI in the app).</summary>
public interface ISecretProtector
{
    string Protect(string secret);
    string? Unprotect(string protectedSecret);
}

/// <summary>Keeps secrets as they are (tests).</summary>
public sealed class PlainProtector : ISecretProtector
{
    public string Protect(string secret) => secret;
    public string? Unprotect(string protectedSecret) => protectedSecret;
}
