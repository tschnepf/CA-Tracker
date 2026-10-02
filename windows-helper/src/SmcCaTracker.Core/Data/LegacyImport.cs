using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Data;

/// <summary>
/// Moves the JSON files earlier versions kept (settings.json, snapshots\, catalog.json,
/// accounts.json, view.json) into the database, once. The files are then moved to
/// "imported-json" so nothing reads them again; they stay there as a backup.
/// </summary>
public static class LegacyImport
{
    public const string MetaKey = "legacy_json_imported";

    public sealed record Result(bool Imported, int Projects, int ItemLists, string? Error);

    /// <param name="moveAside">False when importing into memory only (the files must stay for next time).</param>
    public static Result RunIfNeeded(AppPaths paths, ITrackerPersistence persistence, bool moveAside = true) =>
        Run(paths, persistence, moveAside, onlyIfNeeded: true);

    /// <summary>
    /// Imports from a folder of JSON files (an earlier version's data, or a backup in imported-json),
    /// all or nothing. Used to rebuild a database that can no longer be read.
    /// </summary>
    public static Result ImportFrom(AppPaths source, ITrackerPersistence persistence) =>
        Run(source, persistence, moveAside: false, onlyIfNeeded: false);

    private static Result Run(AppPaths paths, ITrackerPersistence persistence, bool moveAside, bool onlyIfNeeded)
    {
        if (onlyIfNeeded && persistence.GetMeta(MetaKey) is not null)
        {
            return new Result(false, 0, 0, null);
        }

        var hasFiles = File.Exists(paths.SettingsFile) || Directory.Exists(paths.SnapshotsDirectory)
            || File.Exists(paths.CatalogFile) || File.Exists(paths.AccountsFile) || File.Exists(paths.ViewStateFile);
        if (!hasFiles)
        {
            persistence.SetMeta(MetaKey, DateTimeOffset.Now.ToString("O"));
            return new Result(false, 0, 0, null);
        }

        using var timing = AppLog.Time("import.json", null, 2000);
        try
        {
            if (!new SettingsStore(paths).TryLoad(out var settings))
            {
                // Importing now would save no projects in place of yours: leave everything for next time.
                AppLog.Error("import", $"{paths.SettingsFile} isn't valid JSON; nothing was imported.");
                return new Result(false, 0, 0,
                    $"{paths.SettingsFile} can't be read. Fix or replace it (a copy was kept next to it as .bad), then start CA Tracker again");
            }

            var snapshots = new SnapshotStore(paths).LoadAll();
            var imported = 0;

            // All or nothing: a failure part-way leaves the database as it was, and the files in place.
            persistence.RunBatch(() =>
            {
                persistence.SaveSettings(settings);
                foreach (var snapshot in snapshots)
                {
                    try
                    {
                        SnapshotStore.Validate(snapshot);
                        persistence.SaveSnapshot(snapshot);
                        imported++;
                    }
                    catch (Exception error) when (error is InvalidDataException or NullReferenceException or ArgumentException)
                    {
                        AppLog.Warn("import", $"Skipped an unreadable item list for {snapshot.Provider} {snapshot.ProjectId}: {error.Message}");
                    }
                }

                persistence.SaveCatalog(new ProjectCatalogStore(paths).Load());
                persistence.SaveAccounts(new AccountStore(paths).Load());
                persistence.SaveViewState(new ViewStateStore(paths).Load());
                persistence.SetMeta(MetaKey, DateTimeOffset.Now.ToString("O"));
            });

            if (moveAside)
            {
                MoveAside(paths);
            }

            AppLog.Info("import", $"Imported {settings.Projects.Count} projects and {imported} item lists from {paths.Root}.");
            return new Result(true, settings.Projects.Count, imported, null);
        }
        catch (Exception error)
        {
            // The files are left in place, so the import runs again next time.
            AppLog.Error("import", "Importing the JSON files of an earlier version failed", error);
            return new Result(false, 0, 0, error.Message);
        }
    }

    private static void MoveAside(AppPaths paths)
    {
        var backup = Path.Combine(paths.LegacyBackupDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        try
        {
            Directory.CreateDirectory(backup);
            foreach (var file in new[] { paths.SettingsFile, paths.CatalogFile, paths.AccountsFile, paths.ViewStateFile })
            {
                if (File.Exists(file))
                {
                    File.Move(file, Path.Combine(backup, Path.GetFileName(file)), overwrite: true);
                }
            }

            if (Directory.Exists(paths.SnapshotsDirectory))
            {
                Directory.Move(paths.SnapshotsDirectory, Path.Combine(backup, "snapshots"));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Imported already (the database says so); the old files are simply not read again.
            AppLog.Warn("import", $"The old JSON files could not be moved to {backup}: {error.Message}");
        }
    }
}
