namespace SmcCaTracker.Core.Downloads;

/// <summary>A file Helix or Procore offers for an item: its name there, the name it is saved as, and whether it is the Details PDF.</summary>
public sealed record DownloadFileFacts(string Name, string TargetName, bool IsReport);

/// <summary>What the download window starts a file as: whether it is already saved, checked, and the note shown.</summary>
public sealed record DownloadFileChoice(bool AlreadySaved, bool Checked, string Note);

/// <summary>Which files start checked when an item's files are listed for download.</summary>
public static class DownloadChoices
{
    /// <summary>
    /// A file already in the item's folder (by <see cref="DownloadNaming.MatchKey"/>, so a dated or copied
    /// name still counts) starts unchecked. For a new folder everything else is checked. For a folder that
    /// already exists only PDFs not in it yet are checked (items added to the RFI/Submittal since); a fresh
    /// Details PDF and other new files are left for you to check.
    /// </summary>
    public static IReadOnlyList<DownloadFileChoice> Choose(
        IReadOnlyList<DownloadFileFacts> files,
        IReadOnlySet<string> existingKeys,
        string? destination,
        bool folderExists)
    {
        var choices = new List<DownloadFileChoice>(files.Count);
        foreach (var file in files)
        {
            var saved = existingKeys.Contains(DownloadNaming.MatchKey(file.TargetName))
                || existingKeys.Contains(DownloadNaming.MatchKey(file.Name))
                || (destination is not null && File.Exists(Path.Combine(destination, file.TargetName)));
            choices.Add(saved
                ? new DownloadFileChoice(true, false, "Already in folder (a copy is added if checked)")
                : !folderExists
                    ? new DownloadFileChoice(false, true, "")
                    : file.IsReport
                        ? new DownloadFileChoice(false, false, "Folder exists: check to save a fresh copy")
                        : file.TargetName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                            ? new DownloadFileChoice(false, true, "New: not in the folder yet")
                            : new DownloadFileChoice(false, false, "New (not a PDF): check to download"));
        }

        return choices;
    }

    /// <summary>
    /// The item's status before downloading: "New folder · 6 files", "3 new files", or "Up to date" when
    /// nothing starts checked (with how many other files are new, if any). <paramref name="source"/> is
    /// where the files are ("Helix", "Procore").
    /// </summary>
    public static string Summary(IReadOnlyList<DownloadFileChoice> choices, bool folderExists, string source = "Helix")
    {
        if (choices.Count == 0)
        {
            return $"No files in {source}";
        }

        if (!folderExists)
        {
            return $"New folder · {Files(choices.Count)}";
        }

        var checkedCount = choices.Count(choice => choice.Checked);
        if (checkedCount > 0)
        {
            return $"{checkedCount} new {(checkedCount == 1 ? "file" : "files")}";
        }

        var otherNew = choices.Count(choice => !choice.AlreadySaved);
        return otherNew > 0 ? $"Up to date · {otherNew} other new {(otherNew == 1 ? "file" : "files")}" : "Up to date";
    }

    /// <summary>
    /// For checking an item's folder against Helix or Procore: every file it has that isn't in the folder (any
    /// kind, Helix's Details PDF too) starts checked, so Download gets exactly what is missing.
    /// </summary>
    public static IReadOnlyList<DownloadFileChoice> ChooseMissing(
        IReadOnlyList<DownloadFileFacts> files,
        IReadOnlySet<string> existingKeys,
        string? destination)
    {
        var present = Choose(files, existingKeys, destination, folderExists: true);
        return present
            .Select(choice => choice.AlreadySaved
                ? new DownloadFileChoice(true, false, "In the folder")
                : new DownloadFileChoice(false, true, "Missing from the folder"))
            .ToList();
    }

    /// <summary>The item's status when checking its folder: "All 7 files in folder", "3 of 7 missing", "No folder yet".</summary>
    public static string MissingSummary(IReadOnlyList<DownloadFileChoice> choices, bool folderExists, string source = "Helix")
    {
        if (choices.Count == 0)
        {
            return $"No files in {source}";
        }

        if (!folderExists)
        {
            return $"No folder yet · {Files(choices.Count)}";
        }

        var missing = choices.Count(choice => !choice.AlreadySaved);
        return missing == 0
            ? choices.Count == 1 ? "The file is in the folder" : $"All {choices.Count} files in folder"
            : $"{missing} of {choices.Count} missing";
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";
}
