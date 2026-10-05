using SmcCaTracker.Core.Comparison;

namespace SmcCaTracker.Core.Storage;

/// <summary>
/// A coordinator's watchlist: which people to follow on which projects ("Ana and Raj on Riverside, Lee on
/// Northgate"). Showing it lists the open items each person holds on the projects they are watched on, and
/// nothing else. Personal to this PC, kept with the rest of the view.
/// </summary>
public sealed class Watchlist
{
    public string Name { get; set; } = "";

    /// <summary>
    /// The people watched on each project, by project key (a tracked project's "project|id", or a Helix/Procore
    /// project's "helix|id" before it has a CA folder: the same keys as archiving and favorites). Names are
    /// kept without a company in brackets, as the Ball in court list shows them. A project with nobody is kept
    /// too: it is part of the watchlist, with no one watched on it yet.
    /// </summary>
    public Dictionary<string, List<string>> People { get; set; } = new();

    /// <summary>Whether the item's holders include someone watched on its project.</summary>
    public bool Matches(string projectKey, IEnumerable<string> holderNames) =>
        People.TryGetValue(projectKey, out var names)
        && holderNames.Any(holder => names.Contains(PersonNames.WithoutCompany(holder), StringComparer.OrdinalIgnoreCase));

    /// <summary>Everyone watched on any project, A–Z.</summary>
    public IReadOnlyList<string> EveryPerson() =>
        People.Values.SelectMany(names => names)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Tidies a watchlist read from disk or just edited: a name, names without a company in brackets, no
    /// blanks or repeats.
    /// </summary>
    public Watchlist Normalized() => new()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Watchlist" : Name.Trim(),
        People = (People ?? new Dictionary<string, List<string>>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
            .ToDictionary(
                entry => entry.Key,
                entry => (entry.Value ?? new List<string>())
                    .Select(PersonNames.WithoutCompany)
                    .Where(name => name.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                StringComparer.Ordinal)
    };

    /// <summary>A name not used by another watchlist: "Electrical team", "Electrical team (2)", …</summary>
    public static string UniqueName(string wanted, IEnumerable<string> taken, string? except = null)
    {
        var used = taken.Where(name => !string.Equals(name, except, StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = string.IsNullOrWhiteSpace(wanted) ? "Watchlist" : wanted.Trim();
        if (!used.Contains(name))
        {
            return name;
        }

        for (var number = 2; ; number++)
        {
            var candidate = $"{name} ({number})";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
