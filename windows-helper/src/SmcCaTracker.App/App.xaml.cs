using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Windows;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.App.Views;
using SmcCaTracker.Core.WorkloadTracker;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Links;
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
    private InstanceChannel? channel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppPaths.ForArguments(e.Args);

        // A clicked smc-ca-tracker:// link from Workload Tracker (Windows passes it as the argument).
        var linkText = ItemLink.FindInArguments(e.Args);
        var link = ItemLink.Parse(linkText);
        var pipeName = InstanceNames.Pipe(paths, WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName,
            Process.GetCurrentProcess().SessionId);

        // One CA Tracker at a time: two copies would overwrite each other's changes. A copy run with
        // --data-folder has data of its own, so it only excludes another copy on the same folder.
        instance = new Mutex(true, InstanceNames.Mutex(paths), out var first);
        if (!first)
        {
            instance.Dispose();
            instance = null;

            // Hand the running copy the link (or just bring it forward) and leave quietly. An earlier version
            // doesn't listen for that.
            if (!InstanceChannel.TrySend(pipeName, link?.ToUri() ?? "", TimeSpan.FromSeconds(5)))
            {
                MessageBox.Show("CA Tracker is already open.", "CA Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown(0);
            return;
        }

        AppLog.Start(paths.LogsDirectory);
        AppLog.Info("app", $"CA Tracker {TrackerVersion.Current} starting.");

        // Later starts (a clicked link, or the app opened again) come here instead of starting another copy.
        // Listening starts now, so a start while this one is loading isn't turned away; what it hands over is
        // dealt with on the screen's thread, once startup is done and the window is there.
        channel = new InstanceChannel(pipeName);
        channel.Start(message => Dispatcher.InvokeAsync(() => (MainWindow as Views.MainWindow)?.OnStartedAgain(message)));
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
                viewModel.UseWorkloadTracker(new WorkloadTrackerSync(store, new WorkloadTrackerApi(http), protector));
                var window = new MainWindow(viewModel, paths);
                MainWindow = window;
                window.Show();
                if (link is not null)
                {
                    window.OpenLinkWhenReady(link);
                }
                else if (linkText is not null)
                {
                    AppLog.Warn("links", "Started with a link that isn't a CA Tracker item link; it was ignored.");
                }

                // Workload Tracker's buttons open smc-ca-tracker:// links with this app (never a --data-folder copy).
                if (paths.IsDefault)
                {
                    var exePath = Environment.ProcessPath;
                    _ = Task.Run(() => ProtocolRegistry.EnsureRegistered(exePath));
                }
            }
        }
        catch (DatabaseInUseException)
        {
            Shutdown(1);
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
    /// A database another program holds is retried, then the app says so and closes. Only a database
    /// SQLite reports as corrupt is set aside (a copy is kept) and rebuilt from the last JSON backup; any
    /// other problem leaves the file alone and the app runs in memory and says so.
    /// </summary>
    private static TrackerStore OpenStore(AppPaths paths)
    {
        var opened = DatabaseOpener.Open(paths.DatabaseFile);
        switch (opened.Outcome)
        {
            case DatabaseOpenOutcome.Opened:
                return Load(paths, opened.Database!);

            case DatabaseOpenOutcome.InUse:
                AppLog.Error("app", "The database is in use by another program", opened.Error);
                MessageBox.Show(
                    "CA Tracker's database is in use by another program (a backup or sync tool, or a database viewer) or can't be accessed. " +
                    "Close that program and start CA Tracker again.",
                    "CA Tracker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                throw new DatabaseInUseException(opened.Error);

            case DatabaseOpenOutcome.Corrupt:
                AppLog.Error("app", "The database is corrupt", opened.Error);
                var rebuilt = TryRebuild(paths, opened.Error!);
                if (rebuilt is not null)
                {
                    return rebuilt;
                }

                break;

            default:
                AppLog.Error("app", "The database could not be opened", opened.Error);
                break;
        }

        MessageBox.Show(
            $"CA Tracker's database could not be opened ({opened.Error?.Message}). The app will run, but changes made now won't be kept after it closes.",
            "CA Tracker",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        var memory = new InMemoryPersistence();
        LegacyImport.RunIfNeeded(paths, memory, moveAside: false);
        return new TrackerStore(memory);
    }

    private static TrackerStore Load(AppPaths paths, SqlitePersistence database)
    {
        try
        {
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
        catch
        {
            database.Dispose();
            throw;
        }
    }

    /// <summary>Keeps a copy of a corrupt database and starts a new one from the newest JSON backup.</summary>
    private static TrackerStore? TryRebuild(AppPaths paths, Exception cause)
    {
        SqlitePersistence? database = null;
        try
        {
            var copy = DatabaseOpener.SetAside(paths.DatabaseFile, DateTime.Now);
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
            AppLog.Warn("app", $"The database is corrupt ({cause.Message}); a copy was kept as {copy} and a new one was started.");
            MessageBox.Show(
                $"CA Tracker's database is corrupt ({cause.Message}). A copy was kept as\n{copy}\nand a new one was started " +
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

    /// <summary>Thrown so startup stops (the person was already told) without the generic "could not start" message.</summary>
    private sealed class DatabaseInUseException(Exception? inner) : Exception("The database is in use by another program.", inner);

    protected override void OnExit(ExitEventArgs e)
    {
        channel?.Dispose();
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
