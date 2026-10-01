using System.Text.RegularExpressions;

namespace SmcCaTracker.Core.Downloads;

/// <summary>
/// Folder and file names for downloaded RFI/Submittal packages, matching what the earlier Chrome
/// extension produced: "&lt;number&gt; - &lt;name&gt;", with a leading project name or code removed from the
/// name, repeated separators collapsed, and characters Windows doesn't allow replaced.
/// </summary>
public static partial class DownloadNaming
{
    [GeneratedRegex(@"[<>:""/\\|?*\u0000-\u001f]")]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"(?:\s*[-–—]\s*){2,}")]
    private static partial Regex RepeatedSeparators();

    [GeneratedRegex(@"^(?:\s*[-–—:|]\s*)+|(?:\s*[-–—:|]\s*)+$")]
    private static partial Regex EdgeSeparators();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[. ]+$")]
    private static partial Regex TrailingDotsAndSpaces();

    [GeneratedRegex(@"[. \-–—]+$")]
    private static partial Regex TrailingSeparators();

    [GeneratedRegex(@"^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Reserved();

    [GeneratedRegex(@"[^a-zA-Z0-9._-]+")]
    private static partial Regex CodeSplit();

    [GeneratedRegex(@"\s+(?:-|–|—|\||:)\s+")]
    private static partial Regex ProjectNameSplit();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:REV(?:ISION)?\.?\s*#?\s*|R)(\d{1,3})(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionPattern();

    /// <summary>
    /// The revision an item's title (or number) mentions: "Rev 2", "Rev. 2", "Revision 2", "REV#2" or
    /// "R2". The last one wins, since revisions are usually at the end ("… - 00001 - R1"). Null when none.
    /// </summary>
    public static int? Revision(params string?[] texts)
    {
        int? found = null;
        foreach (var text in texts)
        {
            foreach (Match match in RevisionPattern().Matches(text ?? ""))
            {
                found = int.Parse(match.Groups[1].Value);
            }
        }

        return found;
    }

    /// <summary>
    /// The subfolder of an existing item folder that a revision's files go into: an existing folder for
    /// the same revision ("R2", "R02", "Rev 2", "Revision 2") if there is one, otherwise "R2".
    /// </summary>
    public static string RevisionFolder(string itemFolder, int revision)
    {
        try
        {
            if (Directory.Exists(itemFolder))
            {
                foreach (var directory in new DirectoryInfo(itemFolder).EnumerateDirectories())
                {
                    var match = RevisionPattern().Match(directory.Name.Trim());
                    if (match.Success && match.Index == 0 && match.Length == directory.Name.Trim().Length
                        && int.Parse(match.Groups[1].Value) == revision)
                    {
                        return directory.FullName;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return Path.Combine(itemFolder, $"R{revision}");
    }

    /// <summary>"&lt;number&gt; - &lt;name&gt;" for an item's folder.</summary>
    public static string FolderName(string? number, string? title, string? projectName)
    {
        var numberText = string.IsNullOrWhiteSpace(number) ? "Item" : number.Trim();
        var name = TitleWithoutProjectPrefix(title, projectName);
        return SafeDirectoryName(name.Length > 0 ? $"{numberText} - {name}" : numberText, "Item");
    }

    /// <summary>Removes a leading project name or code ("Aligned CMH02 -", "CMH02 -") from an item title.</summary>
    public static string TitleWithoutProjectPrefix(string? title, string? projectName)
    {
        var value = (title ?? "").Trim();
        var project = (projectName ?? "").Trim();
        if (value.Length == 0)
        {
            return value;
        }

        if (project.Length > 0)
        {
            var codes = CodeSplit().Split(project)
                .Where(candidate => candidate.Any(char.IsLetter) && candidate.Any(char.IsDigit));
            var candidates = new[] { project, ProjectNameSplit().Split(project)[0] }
                .Concat(codes)
                .Select(candidate => candidate.Trim())
                .Where(candidate => candidate.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(candidate => candidate.Length);
            foreach (var candidate in candidates)
            {
                var prefix = new Regex($@"^{Regex.Escape(candidate)}(?:(?:\s*[-–—:|]\s*)+|\s+)", RegexOptions.IgnoreCase);
                if (prefix.IsMatch(value))
                {
                    value = prefix.Replace(value, "", 1).Trim();
                    break;
                }
            }
        }

        value = RepeatedSeparators().Replace(value, " - ");
        value = EdgeSeparators().Replace(value, "");
        return Spaces().Replace(value, " ").Trim();
    }

    public static string SafeDirectoryName(string? value, string fallback)
    {
        var cleaned = Unsafe().Replace(value ?? "", "-");
        cleaned = RepeatedSeparators().Replace(cleaned, " - ");
        cleaned = Spaces().Replace(cleaned, " ");
        cleaned = TrailingSeparators().Replace(cleaned, "").Trim();
        return Finish(cleaned, fallback);
    }

    public static string SafeFileName(string? value, string fallback)
    {
        var parts = (value ?? "").Split('/', '\\');
        var cleaned = Unsafe().Replace(parts[^1], "-");
        cleaned = Spaces().Replace(cleaned, " ");
        cleaned = TrailingDotsAndSpaces().Replace(cleaned, "").Trim();
        return Finish(cleaned, fallback);
    }

    /// <summary>PDFs get their upload date in front ("2026-08-03 Review markup.pdf"), as before.</summary>
    public static string AttachmentFileName(string? filename, DateTimeOffset? uploadedAt)
    {
        var name = SafeFileName(filename, "Attachment");
        if (uploadedAt is null || !name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        var date = uploadedAt.Value.ToString("yyyy-MM-dd");
        var alreadyDated = name.StartsWith(date, StringComparison.Ordinal)
            && (name.Length == date.Length || char.IsWhiteSpace(name[date.Length]) || name[date.Length] is '_' or '-');
        return alreadyDated ? name : $"{date} {name}";
    }

    /// <summary>A name not already used in the folder or this download: "file.pdf", "file (2).pdf", …</summary>
    public static string UniqueFileName(string directory, string filename, ISet<string> used)
    {
        for (var sequence = 1; ; sequence++)
        {
            var candidate = Numbered(filename, sequence);
            if (!used.Contains(candidate) && !File.Exists(Path.Combine(directory, candidate)))
            {
                used.Add(candidate);
                return candidate;
            }
        }
    }

    public static string Numbered(string filename, int sequence)
    {
        if (sequence <= 1)
        {
            return filename;
        }

        var dot = filename.LastIndexOf('.');
        return dot > 0 ? $"{filename[..dot]} ({sequence}){filename[dot..]}" : $"{filename} ({sequence})";
    }

    private static string Finish(string cleaned, string fallback)
    {
        if (cleaned.Length > 180)
        {
            cleaned = cleaned[..180].TrimEnd(' ', '.');
        }

        if (Reserved().IsMatch(cleaned))
        {
            cleaned = $"_{cleaned}";
        }

        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
