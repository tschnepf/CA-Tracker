namespace SmcCaTracker.Core.Comparison;

/// <summary>
/// Procore's list page writes Ball In Court people as "Pat Lee (Acme Electric)", with the company in
/// brackets, while its other reads give "Pat Lee" and the company separately. These keep one person to
/// one name in the item list, the Ball in court filter and sorting.
/// </summary>
public static class PersonNames
{
    /// <summary>The name without a company in brackets at its end: "Pat Lee (Acme)" → "Pat Lee".</summary>
    public static string WithoutCompany(string? name) => Split(name).Name;

    /// <summary>The company in brackets at the end of a name ("Pat Lee (Acme)" → "Acme"), or null.</summary>
    public static string? CompanyIn(string? name) => Split(name).Company;

    private static (string Name, string? Company) Split(string? name)
    {
        var text = (name ?? "").Trim();
        if (!text.EndsWith(')'))
        {
            return (text, null);
        }

        // The bracket the final ")" closes, so brackets inside it stay with the company ("Acme (West)").
        var depth = 0;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == ')')
            {
                depth++;
            }
            else if (text[i] == '(' && --depth == 0)
            {
                var person = text[..i].TrimEnd();
                var company = text[(i + 1)..^1].Trim();
                // Only "name (company)": a name that is all brackets, or brackets glued to a word, stays whole.
                return i > 0 && text[i - 1] == ' ' && person.Length > 0 && company.Length > 0
                    ? (person, company)
                    : (text, null);
            }
        }

        return (text, null);
    }
}
