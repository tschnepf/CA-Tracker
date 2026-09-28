using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Matching;

public enum FolderLocation
{
    Active,
    Returned
}

public sealed record FolderEntry(string Name, string FullPath, FolderLocation Location, DateTime LastWriteTimeUtc);

/// <summary>What is in one item-type folder (e.g. CA\RFIs and CA\RFIs\~Returned).</summary>
public sealed class TypeFolderScan
{
    public string ItemType { get; init; } = "";
    public string TypeFolderPath { get; init; } = "";
    public bool TypeFolderExists { get; init; }
    public string ReturnedFolderPath { get; init; } = "";
    public bool ReturnedFolderExists { get; init; }
    public IReadOnlyList<FolderEntry> Folders { get; init; } = Array.Empty<FolderEntry>();
    public string? Error { get; init; }
}

/// <summary>
/// Lists item folders. Only names and dates are read — never file contents — so Egnyte
/// "online only" files are not downloaded by a scan.
/// </summary>
public static class FolderScanner
{
    public static TypeFolderScan Scan(string? caFolder, string itemType, FolderLayout layout)
    {
        if (string.IsNullOrWhiteSpace(caFolder))
        {
            return new TypeFolderScan { ItemType = itemType, Error = "No CA folder has been chosen for this project." };
        }

        var typePath = Path.Combine(caFolder, layout.TypeFolder(itemType));
        var returnedPath = Path.Combine(typePath, layout.ReturnedFolder);
        try
        {
            if (!Directory.Exists(caFolder))
            {
                return new TypeFolderScan
                {
                    ItemType = itemType,
                    TypeFolderPath = typePath,
                    ReturnedFolderPath = returnedPath,
                    Error = $"The CA folder {caFolder} cannot be found. Is the Egnyte drive connected?"
                };
            }

            var typeExists = Directory.Exists(typePath);
            var returnedExists = typeExists && Directory.Exists(returnedPath);
            var folders = new List<FolderEntry>();
            if (typeExists)
            {
                folders.AddRange(List(typePath, FolderLocation.Active)
                    .Where(entry => !string.Equals(entry.Name, layout.ReturnedFolder, StringComparison.OrdinalIgnoreCase)));
            }

            if (returnedExists)
            {
                folders.AddRange(List(returnedPath, FolderLocation.Returned));
            }

            return new TypeFolderScan
            {
                ItemType = itemType,
                TypeFolderPath = typePath,
                TypeFolderExists = typeExists,
                ReturnedFolderPath = returnedPath,
                ReturnedFolderExists = returnedExists,
                Folders = folders
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new TypeFolderScan
            {
                ItemType = itemType,
                TypeFolderPath = typePath,
                ReturnedFolderPath = returnedPath,
                Error = $"Could not read {typePath}: {error.Message}"
            };
        }
    }

    private static IEnumerable<FolderEntry> List(string path, FolderLocation location)
    {
        foreach (var directory in new DirectoryInfo(path).EnumerateDirectories())
        {
            if (directory.Name.StartsWith('.')
                || directory.Attributes.HasFlag(FileAttributes.System)
                || directory.Attributes.HasFlag(FileAttributes.Hidden))
            {
                continue;
            }

            yield return new FolderEntry(directory.Name, directory.FullName, location, directory.LastWriteTimeUtc);
        }
    }
}

public enum MatchKind
{
    None,
    Exact,
    Loose,
    Ambiguous
}

public sealed record FolderMatch(FolderEntry Folder, SnapshotItem? Item, MatchKind Kind, IReadOnlyList<SnapshotItem> Candidates);

public static class FolderMatcher
{
    /// <summary>
    /// Matches folders to items by the number at the start of the folder name. An exact match
    /// wins; the looser match (ignoring prefixes and leading zeros) is used only when it is unique.
    /// </summary>
    public static IReadOnlyList<FolderMatch> Match(IEnumerable<FolderEntry> folders, IReadOnlyList<SnapshotItem> items)
    {
        var exact = Index(items, ItemNumber.ExactKey);
        var loose = Index(items, ItemNumber.LooseKey);
        var matches = new List<FolderMatch>();
        foreach (var folder in folders)
        {
            matches.Add(MatchOne(folder, exact, MatchKind.Exact, ItemNumber.ExactKey)
                ?? MatchOne(folder, loose, MatchKind.Loose, ItemNumber.LooseKey)
                ?? new FolderMatch(folder, null, MatchKind.None, Array.Empty<SnapshotItem>()));
        }

        return matches;
    }

    private static Dictionary<string, List<SnapshotItem>> Index(IEnumerable<SnapshotItem> items, Func<string?, string> key)
    {
        var index = new Dictionary<string, List<SnapshotItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var value = key(item.Number);
            if (value.Length == 0)
            {
                continue;
            }

            if (!index.TryGetValue(value, out var list))
            {
                list = new List<SnapshotItem>();
                index[value] = list;
            }

            list.Add(item);
        }

        return index;
    }

    private static FolderMatch? MatchOne(
        FolderEntry folder,
        Dictionary<string, List<SnapshotItem>> index,
        MatchKind kind,
        Func<string?, string> key)
    {
        foreach (var candidate in ItemNumber.FolderNumberCandidates(folder.Name))
        {
            var value = key(candidate);
            if (value.Length == 0 || !index.TryGetValue(value, out var found))
            {
                continue;
            }

            return found.Count == 1
                ? new FolderMatch(folder, found[0], kind, found)
                : new FolderMatch(folder, null, MatchKind.Ambiguous, found);
        }

        return null;
    }
}
