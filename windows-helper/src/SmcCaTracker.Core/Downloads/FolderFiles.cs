namespace SmcCaTracker.Core.Downloads;

/// <summary>A file in an item's folder: its path from the folder ("R1\Response.pdf"), full path and when it was written.</summary>
public sealed record FolderFile(string RelativePath, string FullPath, DateTime Modified);

/// <summary>What is in an item's folder, for the item details pane. Read-only.</summary>
public static class FolderFiles
{
    /// <summary>
    /// The files in the folder, subfolders included, newest first, at most <paramref name="max"/>. Hidden and
    /// system files (desktop.ini, Thumbs.db) and unfinished downloads are left out.
    /// </summary>
    public static IReadOnlyList<FolderFile> List(string folder, int max) =>
        new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories)
            .Take(5000)
            .Where(file => (file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0
                && !file.Name.StartsWith('.')
                && !file.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(max)
            .Select(file => new FolderFile(Path.GetRelativePath(folder, file.FullName), file.FullName, file.LastWriteTime))
            .ToList();
}
