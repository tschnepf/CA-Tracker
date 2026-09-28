using System.Windows;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.App.Views;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"Something went wrong: {args.Exception.Message}\n\nNo folders were changed unless a preview was confirmed.",
                "CA Tracker",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            args.Handled = true;
        };

        try
        {
            var paths = AppPaths.Default;
            var settingsStore = new SettingsStore(paths);
            var settings = settingsStore.Load();
            ThemeManager.Apply(settings.Theme);

            var viewModel = new MainViewModel(paths, settingsStore, settings);
            var window = new MainWindow(viewModel, paths);
            MainWindow = window;
            window.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show($"CA Tracker could not start: {error.Message}", "CA Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
