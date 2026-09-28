using System.Text;

namespace SmcCaTracker.Core.Matching;

/// <summary>
/// Turns item numbers and folder names into comparable keys.
/// Exact keys keep only letters and digits ("RFI-012" → "RFI012").
/// Loose keys also drop a leading RFI/SUB prefix and leading zeros ("RFI 012" → "12").
/// </summary>
public static class ItemNumber
{
    private static readonly string[] TypePrefixes = { "RFI", "RFIS", "SUB", "SUBS", "SUBMITTAL", "SUBMITTALS" };
    private static readonly string[] TitleSeparators = { " - ", " – ", " — " };

    public static string ExactKey(string? value)
    {
        var builder = new StringBuilder();
        foreach (var character in value ?? "")
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    public static string LooseKey(string? value)
    {
        var runs = Runs(value);
        if (runs.Count > 1 && TypePrefixes.Contains(runs[0]))
        {
            runs.RemoveAt(0);
        }

        return string.Join("|", runs.Select(run => char.IsDigit(run[0]) ? TrimLeadingZeros(run) : run));
    }

    /// <summary>
    /// The part of a folder name that should hold the item number. "P000210 - Doors" → "P000210".
    /// Without a " - " separator, the first word and the first two words are tried ("RFI 012 Slab").
    /// </summary>
    public static IReadOnlyList<string> FolderNumberCandidates(string folderName)
    {
        var name = (folderName ?? "").Trim();
        foreach (var separator in TitleSeparators)
        {
            var index = name.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0)
            {
                return new[] { name[..index].Trim() };
            }
        }

        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var candidates = new List<string>();
        if (words.Length > 0)
        {
            candidates.Add(words[0]);
        }

        if (words.Length > 1)
        {
            candidates.Add($"{words[0]} {words[1]}");
        }

        return candidates;
    }

    /// <summary>Orders numbers the way people read them: RFI-2 before RFI-10.</summary>
    public static int CompareNatural(string? left, string? right)
    {
        var leftRuns = Runs(left);
        var rightRuns = Runs(right);
        for (var index = 0; index < Math.Min(leftRuns.Count, rightRuns.Count); index++)
        {
            var a = leftRuns[index];
            var b = rightRuns[index];
            int result;
            if (char.IsDigit(a[0]) && char.IsDigit(b[0]))
            {
                var trimmedA = TrimLeadingZeros(a);
                var trimmedB = TrimLeadingZeros(b);
                result = trimmedA.Length != trimmedB.Length
                    ? trimmedA.Length.CompareTo(trimmedB.Length)
                    : string.CompareOrdinal(trimmedA, trimmedB);
            }
            else
            {
                result = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return leftRuns.Count.CompareTo(rightRuns.Count);
    }

    private static List<string> Runs(string? value)
    {
        var runs = new List<string>();
        var current = new StringBuilder();
        var currentIsDigit = false;
        foreach (var raw in value ?? "")
        {
            if (!char.IsLetterOrDigit(raw))
            {
                Flush();
                continue;
            }

            var character = char.ToUpperInvariant(raw);
            var isDigit = char.IsDigit(character);
            if (current.Length > 0 && isDigit != currentIsDigit)
            {
                Flush();
            }

            currentIsDigit = isDigit;
            current.Append(character);
        }

        Flush();
        return runs;

        void Flush()
        {
            if (current.Length > 0)
            {
                runs.Add(current.ToString());
                current.Clear();
            }
        }
    }

    private static string TrimLeadingZeros(string digits)
    {
        var trimmed = digits.TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }
}
