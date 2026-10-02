namespace SmcCaTracker.Core.Downloads;

/// <summary>Where an item's downloads go, and which files are already saved there.</summary>
public static class DownloadFolders
{
    /// <summary>
    /// A revised item (its title says "Rev 2" or "R2") whose folder already exists gets its files in
    /// an R2 subfolder of that folder, reusing an existing "R2"/"Rev 2" subfolder.
    /// </summary>
    public static (string Folder, bool InRevisionFolder) Destination(string itemFolder, int? revision) =>
        revision is { } number && Directory.Exists(itemFolder)
            ? (DownloadNaming.RevisionFolder(itemFolder, number), true)
            : (itemFolder, false);

    /// <summary>
    /// Files already saved for the item, by <see cref="DownloadNaming.MatchKey"/>, so a file saved with a
    /// date in front or as a copy still counts. Looks through the item's folder and the destination,
    /// subfolders included; a revision's files are looked for only in its revision subfolder, because
    /// earlier revisions often have files of the same name.
    /// </summary>
    public static HashSet<string> ExistingFileKeys(string? itemFolder, string? destination, bool inRevisionFolder)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var roots = (inRevisionFolder ? new[] { destination } : new[] { itemFolder, destination })
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root!, "*", SearchOption.AllDirectories).Take(5000))
                {
                    keys.Add(DownloadNaming.MatchKey(path));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        return keys;
    }
}
