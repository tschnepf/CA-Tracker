using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Matching;

public enum FolderLocation
{
    Active,
    Returned
}

public sealed record FolderEntry(string Name, string FullPath, FolderLocation Location, DateTime LastWriteTimeUtc)
{
    /// <summary>
    /// Discipline or part folders the item folder sits in, outermost first, e.g. ["Elec", "BB"] for
    /// Submittals\Elec\BB\P000210 or Submittals\Elec\~Returned\BB\P000210.
    /// </summary>
    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();

    /// <summary>The ~Returned folder this item folder is in (returned folders only).</summary>
    public string? ReturnedFolder { get; init; }

    /// <summary>Where the item folder sits, relative to the RFI/Submittal folder (e.g. "Elec\BB").</summary>
    public string RelativeParent { get; init; } = "";

    public string ParentPath => Path.GetDirectoryName(FullPath) ?? "";

    /// <summary>Folder name plus the folders it sits in, for detecting discipline and BB/TFO.</summary>
    public string DetectionText => Groups.Count == 0 ? Name : $"{string.Join(" ", Groups)} {Name}";
}

/// <summary>A folder's last-modified time when it was read, to tell later whether anything in it changed.</summary>
public sealed record FolderStamp(string Path, DateTime LastWriteTimeUtc, bool Exists);

/// <summary>What is in one item-type folder (e.g. CA\RFIs and CA\RFIs\~Returned).</summary>
public sealed class TypeFolderScan
{
    public string ItemType { get; init; } = "";
    public string TypeFolderPath { get; init; } = "";
    public bool TypeFolderExists { get; init; }
    public string ReturnedFolderPath { get; init; } = "";
    public bool ReturnedFolderExists { get; init; }

    /// <summary>The returned folder is a full path used by both RFIs and Submittals.</summary>
    public bool ReturnedShared { get; init; }
    public IReadOnlyList<FolderEntry> Folders { get; init; } = Array.Empty<FolderEntry>();
    public string? Error { get; init; }

    /// <summary>
    /// Every folder that was listed (the CA folder, the RFI/Submittal folder, discipline, part and
    /// returned folders) with its last-modified time. Adding, removing or renaming an item folder
    /// changes the time of the folder it is in, so comparing these says whether a re-read is needed.
    /// </summary>
    public IReadOnlyList<FolderStamp> Containers { get; init; } = Array.Empty<FolderStamp>();

    /// <summary>The CA folder and folder layout this was read for (see <see cref="FolderScanner.SignatureFor"/>).</summary>
    public string Signature { get; init; } = "";

    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.Now;

    public TypeFolderScan WithFolders(IReadOnlyList<FolderEntry> folders) => new()
    {
        ItemType = ItemType,
        TypeFolderPath = TypeFolderPath,
        TypeFolderExists = TypeFolderExists,
        ReturnedFolderPath = ReturnedFolderPath,
        ReturnedFolderExists = ReturnedFolderExists,
        ReturnedShared = ReturnedShared,
        Folders = folders,
        Error = Error,
        Containers = Containers,
        Signature = Signature,
        ScannedAt = ScannedAt
    };

    /// <summary>Same folders in the same places (ignores when it was read).</summary>
    public bool SameFoldersAs(TypeFolderScan other) =>
        TypeFolderExists == other.TypeFolderExists
        && ReturnedFolderExists == other.ReturnedFolderExists
        && string.Equals(Error, other.Error, StringComparison.Ordinal)
        && string.Equals(TypeFolderPath, other.TypeFolderPath, StringComparison.OrdinalIgnoreCase)
        && Folders.Count == other.Folders.Count
        && Folders.Zip(other.Folders).All(pair =>
            string.Equals(pair.First.FullPath, pair.Second.FullPath, StringComparison.OrdinalIgnoreCase)
            && pair.First.Location == pair.Second.Location);
}

/// <summary>
/// Lists item folders. Only names and dates are read — never file contents — so Egnyte
/// "online only" files are not downloaded by a scan.
/// </summary>
public static class FolderScanner
{
    /// <summary>Identifies what a scan was made for: the CA folder, item type and folder layout.</summary>
    public static string SignatureFor(string? caFolder, string itemType, FolderLayout layout) =>
        $"{Path.TrimEndingDirectorySeparator(caFolder ?? "")}|{itemType}|{layout.TypeFolder(itemType)}|{layout.ReturnedFolder}".ToLowerInvariant();

    public static TypeFolderScan Scan(string? caFolder, string itemType, FolderLayout layout)
    {
        var signature = SignatureFor(caFolder, itemType, layout);
        if (string.IsNullOrWhiteSpace(caFolder))
        {
            return new TypeFolderScan { ItemType = itemType, Signature = signature, Error = "No CA folder has been chosen for this project." };
        }

        var typePath = Path.Combine(caFolder, layout.TypeFolder(itemType));
        var returnedPath = Path.Combine(typePath, layout.ReturnedFolder);
        var containers = new List<FolderStamp>();
        try
        {
            if (!Directory.Exists(caFolder))
            {
                return new TypeFolderScan
                {
                    ItemType = itemType,
                    TypeFolderPath = typePath,
                    ReturnedFolderPath = returnedPath,
                    Signature = signature,
                    Containers = new[] { new FolderStamp(caFolder, default, false) },
                    Error = $"The CA folder {caFolder} cannot be found. Is the Egnyte drive connected?"
                };
            }

            // The CA folder changes when the RFI/Submittal folder is created or renamed.
            containers.Add(Stamp(caFolder));
            var typeExists = Directory.Exists(typePath);
            var returnedExists = (typeExists || layout.ReturnedIsShared) && Directory.Exists(returnedPath);
            var folders = new List<FolderEntry>();
            var returnedName = Path.GetFileName(Path.TrimEndingDirectorySeparator(returnedPath));
            var visitedReturned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (typeExists)
            {
                Walk(new DirectoryInfo(typePath), typePath, returnedPath, returnedName, Array.Empty<string>(), null, 0, folders, visitedReturned, containers);
            }

            if (!typeExists)
            {
                // Notice when the RFI/Submittal folder appears (it may be a full path outside the CA folder).
                containers.Add(new FolderStamp(typePath, default, false));
            }

            if (returnedExists && !visitedReturned.Contains(Normal(returnedPath)))
            {
                // The configured returned folder wasn't reached by walking the type folder: it is shared
                // and elsewhere, or deeper (e.g. "Old\~Returned").
                visitedReturned.Add(Normal(returnedPath));
                Walk(new DirectoryInfo(returnedPath), typePath, returnedPath, returnedName, Array.Empty<string>(), returnedPath, 0, folders, visitedReturned, containers);
            }
            else if (!returnedExists)
            {
                // Notice when the returned folder appears (wherever it is configured to be).
                containers.Add(new FolderStamp(returnedPath, default, false));
            }

            return new TypeFolderScan
            {
                ItemType = itemType,
                TypeFolderPath = typePath,
                TypeFolderExists = typeExists,
                ReturnedFolderPath = returnedPath,
                ReturnedFolderExists = returnedExists,
                ReturnedShared = layout.ReturnedIsShared,
                Folders = folders,
                Containers = containers,
                Signature = signature
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new TypeFolderScan
            {
                ItemType = itemType,
                TypeFolderPath = typePath,
                ReturnedFolderPath = returnedPath,
                Signature = signature,
                Error = $"Could not read {typePath}: {error.Message}"
            };
        }
    }

    /// <summary>
    /// True when none of the folders the scan listed has changed since (only their dates are read,
    /// which is far cheaper than listing them again). A scan that failed is never current.
    /// </summary>
    public static bool IsCurrent(TypeFolderScan scan)
    {
        if (scan.Containers.Count == 0)
        {
            return false;
        }

        try
        {
            foreach (var stamp in scan.Containers)
            {
                var exists = Directory.Exists(stamp.Path);
                if (exists != stamp.Exists)
                {
                    return false;
                }

                if (exists && Directory.GetLastWriteTimeUtc(stamp.Path) != stamp.LastWriteTimeUtc)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static FolderStamp Stamp(string path) => new(path, Directory.GetLastWriteTimeUtc(path), true);

    private const int MaxGroupDepth = 4;

    /// <summary>
    /// Lists item folders under <paramref name="directory"/>. Discipline folders (Elec, Mech, …), part
    /// folders (BB, TFO, …) and returned folders (~Returned) are walked into; any other folder is an
    /// item folder. Only names and dates are read.
    /// </summary>
    private static void Walk(
        DirectoryInfo directory,
        string typePath,
        string returnedPath,
        string returnedName,
        IReadOnlyList<string> groups,
        string? inReturned,
        int depth,
        List<FolderEntry> folders,
        HashSet<string> visitedReturned,
        List<FolderStamp> containers)
    {
        // Read the same way IsCurrent reads it later (a folder's listing in its parent can lag behind).
        containers.Add(Stamp(directory.FullName));
        foreach (var child in directory.EnumerateDirectories())
        {
            if (child.Name.StartsWith('.')
                || child.Attributes.HasFlag(FileAttributes.System)
                || child.Attributes.HasFlag(FileAttributes.Hidden))
            {
                continue;
            }

            var isReturned = SamePath(child.FullName, returnedPath) || IsReturnedName(child.Name, returnedName);
            if (isReturned && inReturned is null && depth < MaxGroupDepth)
            {
                visitedReturned.Add(Normal(child.FullName));
                // "~Returned (Base Build)" also says which part its folders belong to.
                var suffix = child.Name.Length > returnedName.Length ? child.Name[returnedName.Length..].Trim(' ', '(', ')', '-', '_') : "";
                var inner = suffix.Length > 0 && IsGroupFolder(suffix) ? groups.Append(suffix).ToList() : groups;
                Walk(child, typePath, returnedPath, returnedName, inner, child.FullName, depth + 1, folders, visitedReturned, containers);
                continue;
            }

            if (depth < MaxGroupDepth && IsGroupFolder(child.Name))
            {
                Walk(child, typePath, returnedPath, returnedName, groups.Append(child.Name.Trim()).ToList(), inReturned, depth + 1, folders, visitedReturned, containers);
                continue;
            }

            folders.Add(new FolderEntry(
                child.Name,
                child.FullName,
                inReturned is null ? FolderLocation.Active : FolderLocation.Returned,
                child.LastWriteTimeUtc)
            {
                Groups = groups,
                ReturnedFolder = inReturned,
                RelativeParent = RelativeTo(typePath, directory.FullName)
            });
        }
    }

    /// <summary>
    /// A returned folder: "~Returned", or "~Returned" followed by a note such as "~Returned (Base Build)"
    /// or "~Returned - TFO".
    /// </summary>
    public static bool IsReturnedName(string name, string returnedName)
    {
        if (returnedName.Length == 0)
        {
            return false;
        }

        var trimmed = name.Trim();
        if (string.Equals(trimmed, returnedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return trimmed.StartsWith(returnedName, StringComparison.OrdinalIgnoreCase)
            && trimmed.Length > returnedName.Length
            && trimmed[returnedName.Length] is ' ' or '(' or '-' or '_';
    }

    /// <summary>
    /// The returned folder directly inside <paramref name="level"/>: "~Returned" itself if it's there,
    /// otherwise one like "~Returned (Base Build)" for the item's part, or one whose note names no part.
    /// One for another part is never used, even when it is the only one: a Tenant Fit Out folder doesn't
    /// go in "~Returned (Base Build)".
    /// </summary>
    public static string? FindReturnedChild(string level, string returnedName, ProjectPhase? phase)
    {
        if (!Directory.Exists(level))
        {
            return null;
        }

        var candidates = new DirectoryInfo(level).EnumerateDirectories()
            .Where(directory => IsReturnedName(directory.Name, returnedName))
            .ToList();
        return candidates.FirstOrDefault(directory => string.Equals(directory.Name.Trim(), returnedName, StringComparison.OrdinalIgnoreCase))?.FullName
            ?? (phase is null ? null : candidates.FirstOrDefault(directory => Phases.Detect(directory.Name) == phase)?.FullName)
            ?? candidates.FirstOrDefault(directory => Phases.Detect(directory.Name) is null)?.FullName;
    }

    /// <summary>
    /// A folder that groups item folders rather than being one: its name is only a discipline or a
    /// part of the job, e.g. "Elec", "Mechanical", "BB", "TFO", "Base Build". Item folders start with
    /// a number, so a name with digits never counts.
    /// </summary>
    public static bool IsGroupFolder(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || trimmed.Any(char.IsDigit) || trimmed.StartsWith('~'))
        {
            return false;
        }

        var words = trimmed.Split(new[] { ' ', '-', '_', '&', '+', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 3)
        {
            return false;
        }

        return Disciplines.Detect(trimmed) is not null || Phases.Detect(trimmed) is not null;
    }

    /// <summary>
    /// Where a new item folder should go: inside the discipline folder and part folder that match,
    /// when the RFI/Submittal folder is organised that way (e.g. Submittals\Elec\BB). Otherwise the
    /// RFI/Submittal folder itself. When the folder is split by discipline but this item's discipline
    /// has no folder yet, a new one (e.g. "Elec") is suggested; it is made only when files are downloaded.
    /// </summary>
    public static string SuggestParent(string typePath, Discipline? discipline, ProjectPhase? phase)
    {
        var current = typePath;
        try
        {
            if (discipline is not null)
            {
                var groups = Subfolders(current).Where(IsGroupFolder).ToList();
                var match = groups.FirstOrDefault(name => Disciplines.Detect(name)?.Code == discipline.Code);
                if (match is not null)
                {
                    current = Path.Combine(current, match);
                }
                else if (groups.Any(name => Disciplines.Detect(name) is not null))
                {
                    return Path.Combine(current, discipline.FolderToken);
                }
            }

            if (phase is ProjectPhase.BaseBuild or ProjectPhase.TenantFitOut)
            {
                var match = Subfolders(current).FirstOrDefault(name => IsGroupFolder(name) && Phases.Detect(name) == phase
                    && Disciplines.Detect(name) is null);
                if (match is not null)
                {
                    current = Path.Combine(current, match);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return current;
    }

    private static IEnumerable<string> Subfolders(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path).EnumerateDirectories().Select(directory => directory.Name)
            : Enumerable.Empty<string>();

    private static string RelativeTo(string root, string path)
    {
        if (!IsInside(path, root))
        {
            return path;
        }

        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? "" : relative;
    }

    private static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool IsInside(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(full, parent, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
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
