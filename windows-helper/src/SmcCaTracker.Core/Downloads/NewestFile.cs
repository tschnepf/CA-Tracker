using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Downloads;

/// <summary>
/// "Open" on a row: which of the item's files is the newest, whether its folder already has that file, and
/// where a copy fetched from Helix is kept. The copy goes in the app's own folder, never the CA folder, so
/// opening a file changes nothing on the drive (saving it there is the Download window's job).
/// </summary>
public static class NewestFile
{
    /// <summary>The file uploaded last (one with no date counts as oldest; of equals, the one listed last), or null.</summary>
    public static T? Pick<T>(IReadOnlyList<T> files, Func<T, DateTimeOffset?> uploadedAt) where T : class =>
        files.Select((file, index) => (file, index))
            .OrderBy(entry => uploadedAt(entry.file) ?? DateTimeOffset.MinValue)
            .ThenBy(entry => entry.index)
            .Select(entry => entry.file)
            .LastOrDefault();

    /// <summary>
    /// The file in the item's folder (subfolders included) that is this one: saved under the name a download
    /// gives it ("2026-09-16 Response.pdf"), or under its Helix name in any of the forms a download may use
    /// (see <see cref="DownloadNaming.MatchKey"/>) and written since it was uploaded, so an earlier revision's
    /// file of the same name doesn't count. The newest such file, or null.
    /// </summary>
    public static string? LocalCopy(string? itemFolder, string name, string targetName, DateTimeOffset? uploadedAt)
    {
        if (string.IsNullOrWhiteSpace(itemFolder) || !Directory.Exists(itemFolder))
        {
            return null;
        }

        var key = DownloadNaming.MatchKey(name);
        var targetKey = DownloadNaming.MatchKey(targetName);
        try
        {
            var files = Directory.EnumerateFiles(itemFolder, "*", SearchOption.AllDirectories).Take(5000).Select(path => new FileInfo(path)).ToList();
            var exact = files.Where(file => string.Equals(file.Name, targetName, StringComparison.OrdinalIgnoreCase));
            var alike = files.Where(file => DownloadNaming.MatchKey(file.Name) is var fileKey && (fileKey == key || fileKey == targetKey)
                && (uploadedAt is null || file.LastWriteTimeUtc >= uploadedAt.Value.UtcDateTime.AddMinutes(-1)));
            return (exact.MaxBy(file => file.LastWriteTimeUtc) ?? alike.MaxBy(file => file.LastWriteTimeUtc))?.FullName;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Where a copy fetched from Helix is kept: &lt;root&gt;\&lt;project&gt;\&lt;number - title&gt;\&lt;file&gt;.</summary>
    public static string CachePath(string root, string? projectName, string number, string title, string targetName) =>
        Path.Combine(root,
            DownloadNaming.SafeDirectoryName(projectName, "Project"),
            DownloadNaming.FolderName(number, title, projectName),
            DownloadNaming.SafeFileName(targetName, "Attachment"));

    /// <summary>A copy kept from before that is still this file (fetched since it was uploaded), so it needn't be fetched again.</summary>
    public static bool IsCurrentCopy(string path, DateTimeOffset? uploadedAt) =>
        File.Exists(path) && new FileInfo(path) is { Length: > 0 } file
        && (uploadedAt is null || file.LastWriteTimeUtc >= uploadedAt.Value.UtcDateTime);

    /// <summary>Removes copies not opened for a while, so the app's folder doesn't keep growing. Best effort.</summary>
    public static void CleanUp(string root, TimeSpan olderThan, DateTime now)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
            {
                if (now.ToUniversalTime() - File.GetLastAccessTimeUtc(path) > olderThan && now.ToUniversalTime() - File.GetLastWriteTimeUtc(path) > olderThan)
                {
                    LocalFiles.TryDelete(path);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
