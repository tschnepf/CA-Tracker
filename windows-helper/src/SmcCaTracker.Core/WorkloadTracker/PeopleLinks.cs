using System.Globalization;
using System.Text;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>A Helix or Procore person seen on a project: someone who holds, held or will hold one of its items.</summary>
public sealed record ProviderPerson(
    string Provider,
    string? ProviderUserId,
    string? Email,
    string Name,
    string? Company,
    string? CompanyId,
    int OpenItems,
    bool IsYou)
{
    /// <summary>The same person across items: their provider id, else their email, else name and company.</summary>
    public string Key => ProviderUserId is { Length: > 0 } id ? $"id:{id}"
        : Email is { Length: > 0 } email ? $"email:{email.ToLowerInvariant()}"
        : $"name:{Name.ToLowerInvariant()}|{Company?.ToLowerInvariant()}";

    /// <summary>The tracker can only keep a link for someone with a provider id or an email.</summary>
    public bool CanLink => ProviderUserId is { Length: > 0 } || Email is { Length: > 0 };
}

/// <summary>
/// Gathers the Helix and Procore people of a project, and suggests the Workload Tracker person each one
/// probably is, by name. A suggestion is only that: it becomes a link once someone confirms it.
/// </summary>
public static class PeopleLinks
{
    /// <summary>Everyone named on the project's items (and the person who scanned it), each once, open items first.</summary>
    public static List<ProviderPerson> From(ProviderSnapshot snapshot)
    {
        var provider = snapshot.Provider.ToLowerInvariant();
        var people = new Dictionary<string, (ProviderPerson Person, int Open)>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            foreach (var holder in item.Holders)
            {
                var name = PersonNames.WithoutCompany(holder.Name).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                var company = holder.Company ?? PersonNames.CompanyIn(holder.Name);
                var person = new ProviderPerson(provider, Blank(holder.ProviderUserId), Blank(holder.Email), name, Blank(company), Blank(holder.CompanyId), 0, false);
                var open = !item.Closed && string.Equals(holder.State, "current", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (people.TryGetValue(person.Key, out var seen))
                {
                    // Keep whatever the other mentions add (an email, a company).
                    var merged = seen.Person with
                    {
                        Email = seen.Person.Email ?? person.Email,
                        Company = seen.Person.Company ?? person.Company,
                        CompanyId = seen.Person.CompanyId ?? person.CompanyId
                    };
                    people[person.Key] = (merged, seen.Open + open);
                }
                else
                {
                    people[person.Key] = (person, open);
                }
            }
        }

        var user = snapshot.User;
        if (user is not null && PersonNames.WithoutCompany(user.FullName).Trim() is { Length: > 0 } yourName)
        {
            var you = new ProviderPerson(provider, Blank(user.ProviderUserId), Blank(user.Email), yourName, Blank(user.Company), Blank(user.CompanyId), 0, true);
            people[you.Key] = people.TryGetValue(you.Key, out var seen) ? (seen.Person with { IsYou = true }, seen.Open) : (you, 0);
        }

        return people.Values
            .Select(entry => entry.Person with { OpenItems = entry.Open })
            .OrderByDescending(person => person.IsYou)
            .ThenBy(person => person.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Whether a person works for the company the project was scanned as (yours).</summary>
    public static bool AtYourCompany(ProviderPerson person, SnapshotUser? you)
    {
        if (person.IsYou || you is null)
        {
            return true; // with no company to compare against, everyone is shown
        }

        if (!string.IsNullOrWhiteSpace(person.CompanyId) && !string.IsNullOrWhiteSpace(you.CompanyId))
        {
            return string.Equals(person.CompanyId, you.CompanyId, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(person.Company)
            && string.Equals(person.Company.Trim(), you.Company?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one team member whose name matches: the same words in any order (so "Smith, Pat" is Pat Smith),
    /// or else the same first and last name with middle names or initials left out. None when no one or
    /// more than one matches.
    /// </summary>
    public static TeamMember? Suggest(ProviderPerson person, IReadOnlyList<TeamMember> team)
    {
        var words = Words(person.Name);
        if (words.Count == 0)
        {
            return null;
        }

        var exact = team.Where(member => SameWords(Words(member.Name), words)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        if (exact.Count > 1)
        {
            return null;
        }

        var ends = team.Where(member => SameEnds(Words(member.Name), words)).ToList();
        return ends.Count == 1 ? ends[0] : null;
    }

    /// <summary>
    /// The other way round: the one Helix or Procore person whose name matches a Workload Tracker person,
    /// by the same rules as <see cref="Suggest"/>. None when no one or more than one matches.
    /// </summary>
    public static ProviderPerson? SuggestPerson(TeamMember member, IReadOnlyList<ProviderPerson> people)
    {
        var words = Words(member.Name);
        if (words.Count == 0)
        {
            return null;
        }

        var exact = people.Where(person => SameWords(Words(person.Name), words)).ToList();
        if (exact.Count != 0)
        {
            return exact.Count == 1 ? exact[0] : null;
        }

        var ends = people.Where(person => SameEnds(Words(person.Name), words)).ToList();
        return ends.Count == 1 ? ends[0] : null;
    }

    /// <summary>A name as lowercase words without accents or punctuation; "Last, First" becomes "first last".</summary>
    public static List<string> Words(string? name)
    {
        var text = PersonNames.WithoutCompany(name ?? "");
        var comma = text.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0 && text.IndexOf(',', comma + 1) < 0)
        {
            text = $"{text[(comma + 1)..]} {text[..comma]}";
        }

        var plain = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            plain.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static bool SameWords(List<string> a, List<string> b) =>
        a.Count == b.Count && a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal));

    private static bool SameEnds(List<string> a, List<string> b) =>
        a.Count >= 2 && b.Count >= 2 && a[0] == b[0] && a[^1] == b[^1];

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
