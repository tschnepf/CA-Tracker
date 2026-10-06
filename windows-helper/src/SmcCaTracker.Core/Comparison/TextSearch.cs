namespace SmcCaTracker.Core.Comparison;

/// <summary>
/// The item list's search box. Each word typed has to be in the item's number, title or folder name, in any
/// case, so "E-101" finds "E-101 - Lighting" and "light fixture" finds "Fixture schedule - light levels".
/// In the number, spaces, dashes, dots and underscores don't count, so "033000" finds "03 30 00-1" and
/// "e101" finds "E-101".
/// </summary>
public static class TextSearch
{
    /// <summary>The words of what was typed; none when it is blank.</summary>
    public static string[] Words(string? search) =>
        (search ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when every word is in the number or one of the other texts (true when there are no words).</summary>
    public static bool Matches(IReadOnlyList<string> words, string? number, params string?[] texts)
    {
        foreach (var word in words)
        {
            var compact = Compact(word);
            var found = Contains(number, word)
                || (compact.Length > 0 && Contains(Compact(number), compact))
                || texts.Any(text => Contains(text, word));
            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(string? text, string word) =>
        !string.IsNullOrEmpty(text) && text.Contains(word, StringComparison.CurrentCultureIgnoreCase);

    private static string Compact(string? text) =>
        string.Concat((text ?? "").Where(character => !char.IsWhiteSpace(character) && character is not ('-' or '–' or '—' or '.' or '_')));
}
