using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Matching;

/// <summary>
/// Each project's CA folders, listed once and kept in the database. Comparing uses the saved lists, so
/// it never waits on the Egnyte drive; <see cref="Refresh"/> (run in the background) checks the folders'
/// dates and lists a folder again only when something in it changed. All methods read the drive and
/// belong off the screen's thread, except <see cref="Cached"/>.
/// </summary>
public sealed class FolderIndex
{
    private readonly TrackerStore store;

    // One listing of a project at a time, so an older listing never overwrites a newer one.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> projectLocks = new(StringComparer.Ordinal);

    public FolderIndex(TrackerStore store)
    {
        this.store = store;
    }

    /// <summary>The saved list for the project's current CA folder and layout, or null when there is none.</summary>
    public TypeFolderScan? Cached(TrackerProject project, string itemType, FolderLayout layout)
    {
        var scan = store.FolderScan(project.Id, itemType);
        return scan is not null && scan.Signature == FolderScanner.SignatureFor(project.CaFolder, itemType, layout) ? scan : null;
    }

    /// <summary>True when every item type of the project has a saved list (or the project has no CA folder).</summary>
    public bool HasCached(TrackerProject project, FolderLayout layout) =>
        string.IsNullOrWhiteSpace(project.CaFolder) || ItemTypes.All.All(itemType => Cached(project, itemType, layout) is not null);

    /// <summary>The saved list, or a fresh one when there is none yet.</summary>
    public TypeFolderScan GetOrScan(TrackerProject project, string itemType, FolderLayout layout) =>
        string.IsNullOrWhiteSpace(project.CaFolder)
            ? FolderScanner.Scan(project.CaFolder, itemType, layout) // nothing to read or keep
            : Cached(project, itemType, layout) ?? Rescan(project, itemType, layout);

    /// <summary>
    /// The saved list without touching the drive: for a project with no CA folder, the "no folder"
    /// result; null when the project has a CA folder that hasn't been listed yet.
    /// </summary>
    public TypeFolderScan? CachedOrEmpty(TrackerProject project, string itemType, FolderLayout layout) =>
        string.IsNullOrWhiteSpace(project.CaFolder)
            ? FolderScanner.Scan(project.CaFolder, itemType, layout)
            : Cached(project, itemType, layout);

    /// <summary>Lists the folders again and saves the result.</summary>
    public TypeFolderScan Rescan(TrackerProject project, string itemType, FolderLayout layout)
    {
        lock (projectLocks.GetOrAdd(project.Id, _ => new object()))
        {
            var previous = Cached(project, itemType, layout);
            TypeFolderScan scan;
            using (AppLog.Time("folders.list", $"{project.Name} · {ItemTypes.Plural(itemType)}", 750))
            {
                scan = FolderScanner.Scan(project.CaFolder, itemType, layout);
            }

            store.SaveFolderScan(project.Id, scan, notify: previous is null || !previous.SameFoldersAs(scan));
            return scan;
        }
    }

    /// <summary>
    /// Brings the project's saved lists up to date: each is listed again only if one of its folders has
    /// changed since, or it is older than <paramref name="maxAge"/> (a safety net for drives that don't
    /// always update folder dates), or always with <paramref name="force"/>. Returns true when any list
    /// changed.
    /// </summary>
    public bool Refresh(TrackerProject project, FolderLayout layout, bool force = false, TimeSpan? maxAge = null)
    {
        if (string.IsNullOrWhiteSpace(project.CaFolder))
        {
            return false;
        }

        var changed = false;
        foreach (var itemType in ItemTypes.All)
        {
            var cached = Cached(project, itemType, layout);
            var tooOld = maxAge is { } age && cached is not null && DateTimeOffset.Now - cached.ScannedAt > age;
            if (!force && !tooOld && cached is not null && FolderScanner.IsCurrent(cached))
            {
                continue;
            }

            var fresh = Rescan(project, itemType, layout);
            changed |= cached is null || !cached.SameFoldersAs(fresh);
        }

        return changed;
    }
}
