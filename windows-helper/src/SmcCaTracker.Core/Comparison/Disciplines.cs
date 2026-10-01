namespace SmcCaTracker.Core.Comparison;

/// <summary>A trade discipline. <see cref="FolderToken"/> is how it is written in folder names.</summary>
public sealed record Discipline(string Code, string Label, string FolderToken, IReadOnlyList<string> Aliases);

public enum DisciplineSource
{
    None,
    Detected,
    Manual
}

public static class Disciplines
{
    /// <summary>Stored when someone says a folder has no discipline, overriding detection.</summary>
    public const string NoneCode = "None";

    public static readonly IReadOnlyList<Discipline> Defaults = new[]
    {
        new Discipline("Electrical", "Electrical", "Elec", new[] { "ELEC", "ELECTRICAL" }),
        new Discipline("Fire", "Fire", "Fire", new[] { "FIRE" }),
        new Discipline("ICT", "ICT", "ICT", new[] { "ICT" }),
        new Discipline("Mechanical", "Mechanical", "Mech", new[] { "MECH", "MECHANICAL" }),
        new Discipline("Plbg", "Plbg", "Plbg", new[] { "PLBG", "PLUMBING" }),
        new Discipline("Security", "Security", "Security", new[] { "SECURITY" })
    };

    private static IReadOnlyList<Discipline> all = Defaults;

    /// <summary>The disciplines in use: the defaults, or your company's list (see CompanySettings).</summary>
    public static IReadOnlyList<Discipline> All => all;

    /// <summary>
    /// Uses a company's own list of disciplines (set once at startup). Codes must be unique; each word a
    /// folder name may use for it (its folder token and aliases) is matched whole and case-insensitively.
    /// </summary>
    public static void Configure(IEnumerable<Discipline>? disciplines)
    {
        var list = (disciplines ?? Defaults)
            .Where(discipline => !string.IsNullOrWhiteSpace(discipline.Code) && !string.Equals(discipline.Code, NoneCode, StringComparison.OrdinalIgnoreCase))
            .GroupBy(discipline => discipline.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(discipline => discipline with
            {
                Label = string.IsNullOrWhiteSpace(discipline.Label) ? discipline.Code : discipline.Label,
                FolderToken = string.IsNullOrWhiteSpace(discipline.FolderToken) ? discipline.Code : discipline.FolderToken,
                Aliases = discipline.Aliases
                    .Append(discipline.FolderToken ?? "")
                    .Append(discipline.Code)
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Select(alias => alias.Trim().ToUpperInvariant())
                    .Distinct()
                    .ToList()
            })
            .ToList();
        all = list.Count > 0 ? list : Defaults;
    }

    public static Discipline? Find(string? code) =>
        All.FirstOrDefault(discipline => string.Equals(discipline.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds the first discipline word in a name, e.g. "P000210 - Elec - Lighting" → Electrical.
    /// Only whole words count, so "Electrician" or "Firestop" do not match.
    /// </summary>
    public static Discipline? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var words = text.Split(
            text.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            var upper = word.ToUpperInvariant();
            var match = All.FirstOrDefault(discipline => discipline.Aliases.Contains(upper));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>Key for a folder's manual discipline; folder names survive moves into ~Returned.</summary>
    public static string Key(string itemType, string folderName) =>
        $"{itemType}|{folderName}".ToLowerInvariant();

    /// <summary>
    /// Key for an item's manual discipline, chosen before it has a folder (so a download goes into
    /// the right discipline folder). Kept by item id, so it still applies once the folder exists.
    /// </summary>
    public static string ItemKey(string itemType, string itemId) =>
        $"item|{itemType}|{itemId}".ToLowerInvariant();

    /// <summary>
    /// The discipline for a row: a manual choice for the folder wins, then a manual choice for the
    /// item, then the folder name, then the folder it sits in, then (for items with no folder yet)
    /// the item title.
    /// </summary>
    public static (Discipline? Discipline, DisciplineSource Source) Resolve(
        string itemType,
        string? folderName,
        string? itemTitle,
        IReadOnlyDictionary<string, string>? overrides,
        IReadOnlyList<string>? folderGroups = null,
        string? itemId = null)
    {
        // An empty choice means "detect it again" (kept, rather than removed, so it is shared like any other choice).
        string? code = null;
        if (overrides is not null)
        {
            if (folderName is not null && overrides.TryGetValue(Key(itemType, folderName), out var folderCode) && !string.IsNullOrWhiteSpace(folderCode))
            {
                code = folderCode;
            }
            else if (!string.IsNullOrWhiteSpace(itemId) && overrides.TryGetValue(ItemKey(itemType, itemId), out var itemCode) && !string.IsNullOrWhiteSpace(itemCode))
            {
                code = itemCode;
            }
        }

        if (code is not null)
        {
            return string.Equals(code, NoneCode, StringComparison.OrdinalIgnoreCase)
                ? (null, DisciplineSource.Manual)
                : (Find(code), DisciplineSource.Manual);
        }

        // The folder's own name, then the discipline folder it sits in (e.g. Submittals\Elec\BB\…),
        // then — for items with no folder yet — the item title.
        var detected = Detect(folderName)
            ?? (folderGroups is { Count: > 0 } ? folderGroups.Select(Detect).FirstOrDefault(found => found is not null) : null)
            ?? (folderName is null ? Detect(itemTitle) : null);
        return detected is null ? (null, DisciplineSource.None) : (detected, DisciplineSource.Detected);
    }
}
