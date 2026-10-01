using System.IO;
using System.Net.Http;
using System.Windows;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.App.Views;
using SmcCaTracker.Core.Central;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Data;

namespace SmcCaTracker.App;

public partial class App : Application
{
    private TrackerStore? store;
    private HelixSyncService? helixSync;
    private ProcoreSyncService? procoreSync;
    private HttpClient? http;
    private Mutex? instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppPaths.Default;

        // One CA Tracker at a time: two copies would overwrite each other's changes.
        instance = new Mutex(true, @"Local\SMC-CA-Tracker", out var first);
        if (!first)
        {
            MessageBox.Show("CA Tracker is already open.", "CA Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
            instance.Dispose();
            instance = null;
            Shutdown(0);
            return;
        }

        AppLog.Start(paths.LogsDirectory);
        AppLog.Info("app", $"CA Tracker {TrackerVersion.Current} starting.");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Error("app", "CA Tracker stopped unexpectedly", args.ExceptionObject as Exception);
            AppLog.Flush(TimeSpan.FromSeconds(2));
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("app", "A background task failed", args.Exception);
            args.SetObserved();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("app", "Unhandled error", args.Exception);
            MessageBox.Show(
                $"Something went wrong: {args.Exception.Message}\n\nNo folders were changed unless a preview was confirmed.",
                "CA Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };

        try
        {
            using (AppLog.Time("app.startup", null, 1500))
            {
                store = OpenStore(paths);

                // The company's own settings (shipped with the installer), applied before anything uses them.
                var (company, companyPath, companyError) = CompanySettingsLoader.Load(AppContext.BaseDirectory);
                if (company is not null && CompanySettingsLoader.Apply(company, store.Settings))
                {
                    store.SaveSettings(store.Settings);
                }

                ThemeManager.Apply(store.Settings.Theme);

                // Earlier versions registered a Chrome extension bridge; remove what they left behind.
                LegacyCleanup.Run(paths);

                var folderIndex = new FolderIndex(store);
                helixSync = new HelixSyncService(paths, store);
                var protector = new DpapiProtector();
                http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
                procoreSync = new ProcoreSyncService(paths, store);
                var viewModel = new MainViewModel(paths, store, folderIndex, helixSync, procoreSync, protector);
                viewModel.UseCompanySettings(company, companyPath, companyError);
                viewModel.UseUpdates(new UpdateService(http));
                viewModel.UseCentral(new CentralSync(store, http, protector) { CompanyServer = company?.CentralServer });
                var window = new MainWindow(viewModel, paths);
                MainWindow = window;
                window.Show();
            }
        }
        catch (Exception error)
        {
            AppLog.Error("app", "CA Tracker could not start", error);
            MessageBox.Show($"CA Tracker could not start: {error.Message}", "CA Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Opens the database (importing the JSON files of an earlier version the first time) and loads it.
    /// A database that can't be read is set aside and rebuilt from the last JSON backup; as a last
    /// resort the app runs in memory and says so.
    /// </summary>
    private static TrackerStore OpenStore(AppPaths paths)
    {
        SqlitePersistence? database = null;
        try
        {
            database = new SqlitePersistence(paths.DatabaseFile);
            var imported = LegacyImport.RunIfNeeded(paths, database);
            if (imported.Error is not null)
            {
                MessageBox.Show(
                    $"Your earlier CA Tracker data couldn't be moved into the new database ({imported.Error}). " +
                    "Nothing was lost; it will be tried again next time.",
                    "CA Tracker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return new TrackerStore(database);
        }
        catch (Exception error)
        {
            database?.Dispose();
            AppLog.Error("app", "The database could not be opened", error);
            var rebuilt = TryRebuild(paths, error);
            if (rebuilt is not null)
            {
                return rebuilt;
            }

            MessageBox.Show(
                $"CA Tracker's database could not be opened ({error.Message}). The app will run, but changes made now won't be kept after it closes.",
                "CA Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            var memory = new InMemoryPersistence();
            LegacyImport.RunIfNeeded(paths, memory, moveAside: false);
            return new TrackerStore(memory);
        }
    }

    /// <summary>Moves an unreadable database aside and starts a new one from the newest JSON backup.</summary>
    private static TrackerStore? TryRebuild(AppPaths paths, Exception cause)
    {
        SqlitePersistence? database = null;
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var file = paths.DatabaseFile + suffix;
                if (File.Exists(file))
                {
                    File.Move(file, $"{paths.DatabaseFile}.{stamp}.bad{suffix}");
                }
            }

            database = new SqlitePersistence(paths.DatabaseFile);
            var result = LegacyImport.RunIfNeeded(paths, database);
            if (!result.Imported && Directory.Exists(paths.LegacyBackupDirectory))
            {
                var newest = new DirectoryInfo(paths.LegacyBackupDirectory)
                    .EnumerateDirectories()
                    .OrderByDescending(directory => directory.Name, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (newest is not null)
                {
                    LegacyImport.ImportFrom(new AppPaths(newest.FullName), database);
                }
            }

            var store = new TrackerStore(database);
            AppLog.Warn("app", $"The database couldn't be read ({cause.Message}); it was set aside as tracker.db.{stamp}.bad and rebuilt.");
            MessageBox.Show(
                $"CA Tracker's database couldn't be read ({cause.Message}), so it was set aside and a new one was started " +
                "from your last saved projects. Items come back with the next sync.",
                "CA Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return store;
        }
        catch (Exception error)
        {
            database?.Dispose();
            AppLog.Error("app", "Rebuilding the database failed", error);
            return null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        helixSync?.Dispose();
        procoreSync?.Dispose();
        http?.Dispose();
        store?.Dispose();
        try
        {
            instance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        instance?.Dispose();
        AppLog.Info("app", "CA Tracker closed.");
        AppLog.Flush(TimeSpan.FromSeconds(2));
        base.OnExit(e);
    }
}
