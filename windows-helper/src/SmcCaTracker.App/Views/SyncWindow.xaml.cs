using System.Windows;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;

namespace SmcCaTracker.App.Views;

/// <summary>Lists each connected site with its last sync, and syncs one or all of them.</summary>
public partial class SyncWindow : Window
{
    private readonly MainViewModel viewModel;

    public SyncWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.viewModel = viewModel;
        DataContext = viewModel;
        viewModel.RefreshAccounts();
    }

    /// <summary>Raised by Manage connections…; the main window opens Settings → Connections.</summary>
    public event EventHandler? ManageConnectionsRequested;

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionViewModel connection })
        {
            await viewModel.SyncAsync(connection.Provider);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionViewModel connection })
        {
            connection.StopRequested = true;
            connection.SyncMessage = "Stopping after the current project…";
        }
    }

    private async void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsAnySyncing)
        {
            return;
        }

        // One site at a time, so only one CA Tracker browser window is open at once.
        foreach (var connection in viewModel.Connections.Where(connection => connection.CanSync).ToList())
        {
            await viewModel.SyncAsync(connection.Provider);
        }
    }

    private void Manage_Click(object sender, RoutedEventArgs e) => ManageConnectionsRequested?.Invoke(this, EventArgs.Empty);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
