using System.Windows;
using System.Windows.Controls;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

public enum SettingsSection
{
    General,
    Connections
}

/// <summary>
/// The Settings page inside the main window: a sidebar with General settings and Connections.
/// General edits a copy of the settings and applies it only on Save. Connections shows each
/// site's sign-in and last sync, with Connect and Sync buttons handled by the main window.
/// </summary>
public partial class SettingsPage : UserControl
{
    private Func<TrackerSettings>? settingsSource;
    private AppPaths? paths;
    private TrackerSettings? editing;
    private bool loading;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public event EventHandler? BackRequested;
    public event Func<TrackerSettings, Task>? SaveRequested;
    public event Action<string>? ConnectRequested;
    public event Action<string>? SyncRequested;

    /// <summary>Loads a fresh copy of the settings and shows the given section.</summary>
    public void Open(Func<TrackerSettings> source, AppPaths appPaths, SettingsSection section)
    {
        settingsSource = source;
        paths = appPaths;
        Populate(source());
        SectionList.SelectedItem = section == SettingsSection.Connections ? ConnectionsItem : GeneralItem;
        ShowSection(section);
    }

    private void Populate(TrackerSettings settings)
    {
        loading = true;
        editing = settings;
        ErrorText.Text = "";
        SavedText.Visibility = Visibility.Collapsed;

        ThemeSystem.IsChecked = settings.Theme == ThemePreference.System;
        ThemeLight.IsChecked = settings.Theme == ThemePreference.Light;
        ThemeDark.IsChecked = settings.Theme == ThemePreference.Dark;

        // Settings your company manages can't be changed here.
        var foldersLocked = (DataContext as MainViewModel)?.AreFoldersLocked == true;
        RfiFolderBox.IsEnabled = SubmittalFolderBox.IsEnabled = ReturnedFolderBox.IsEnabled = !foldersLocked;
        RfiFolderBox.Text = settings.Folders.RfiFolder;
        SubmittalFolderBox.Text = settings.Folders.SubmittalFolder;
        ReturnedFolderBox.Text = settings.Folders.ReturnedFolder;

        WhenNotHeldBox.IsChecked = settings.Returned.WhenNoCompanyHolder;
        WhenClosedBox.IsChecked = settings.Returned.WhenClosed;
        WhenComingUpBox.IsChecked = settings.Returned.WhenOnlyComingUp;
        AlwaysStatusesBox.Text = string.Join(", ", settings.Returned.AlwaysReturnedStatuses);
        NeverStatusesBox.Text = string.Join(", ", settings.Returned.NeverReturnedStatuses);

        CloseWindowBox.IsChecked = settings.Browser.CloseWindowWhenDone;
        AutoSyncBox.IsChecked = settings.Sync.AutoSync;
        SyncIntervalBox.Text = settings.Sync.IntervalMinutes.ToString();
        TimeoutBox.Text = settings.Browser.TimeoutSeconds.ToString();
        loading = false;
    }

    /// <summary>Switches section without reloading the settings being edited.</summary>
    public void ShowSectionFromOutside(SettingsSection section)
    {
        SectionList.SelectedItem = section == SettingsSection.Connections ? ConnectionsItem : GeneralItem;
        ShowSection(section);
    }

    private void Section_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not null)
        {
            ShowSection(SectionList.SelectedItem == ConnectionsItem ? SettingsSection.Connections : SettingsSection.General);
        }
    }

    private void ShowSection(SettingsSection section)
    {
        if (section == SettingsSection.Connections && DataContext is MainViewModel model)
        {
            CentralServerBox.Text = model.CentralServerText;
            CentralServerBox.IsEnabled = !model.IsCentralServerLocked;
            CentralTokenBox.Clear();
            CentralMessage.Text = "";
        }

        GeneralPanel.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPanel.Visibility = section == SettingsSection.Connections ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!loading)
        {
            ThemeManager.Apply(SelectedTheme());
        }
    }

    private ThemePreference SelectedTheme() =>
        ThemeDark.IsChecked == true ? ThemePreference.Dark
        : ThemeLight.IsChecked == true ? ThemePreference.Light
        : ThemePreference.System;

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (settingsSource is null)
        {
            return;
        }

        var settings = settingsSource();
        Populate(settings);
        ThemeManager.Apply(settings.Theme);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (paths is null)
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(paths.LogsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{paths.LogsDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            ErrorText.Text = $"Could not open the log folder: {error.Message}";
        }
    }

    private async void ExportSample_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        ErrorText.Text = "";
        try
        {
            var (file, problem) = await model.ExportHelixSampleAsync();
            if (file is null)
            {
                ErrorText.Text = $"No sample exported: {problem}";
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ErrorText.Text = $"No sample exported: {error.Message}";
        }
    }

    private async void ClearBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (paths is null || DataContext is not MainViewModel model)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        if (model.BrowserProfileInUse?.Invoke() is { } inUse)
        {
            ErrorText.Text = $"Close {inUse} first, then sign out.";
            return;
        }

        var answer = MessageBox.Show(
            owner,
            "Sign out of Helix and Procore in the CA Tracker browser? You'll be asked to sign in on the next refresh.\n\nThe item panel closes.",
            "Sign out",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var problem = await model.SignOutOfBrowserAsync();
        if (problem is not null)
        {
            ErrorText.Text = $"Could not sign out: {problem}";
            return;
        }

        ErrorText.Text = "";
        MessageBox.Show(owner, "Signed out of the built-in browser.", "CA Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (editing is null)
        {
            return;
        }

        var folders = new FolderLayout
        {
            RfiFolder = RfiFolderBox.Text.Trim(),
            SubmittalFolder = SubmittalFolderBox.Text.Trim(),
            ReturnedFolder = ReturnedFolderBox.Text.Trim()
        };
        var problems = folders.Validate().ToList();

        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout) || timeout < 30 || timeout > 1800)
        {
            problems.Add("Wait between 30 and 1800 seconds.");
        }

        if (!int.TryParse(SyncIntervalBox.Text.Trim(), out var interval) || interval < 5 || interval > 240)
        {
            problems.Add("Update Helix every 5 to 240 minutes.");
        }

        if (problems.Count > 0)
        {
            SavedText.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Join(" ", problems);
            return;
        }

        editing.Theme = SelectedTheme();
        editing.Folders = folders;
        editing.Returned.WhenNoCompanyHolder = WhenNotHeldBox.IsChecked == true;
        editing.Returned.WhenClosed = WhenClosedBox.IsChecked == true;
        editing.Returned.WhenOnlyComingUp = WhenComingUpBox.IsChecked == true;
        editing.Returned.AlwaysReturnedStatuses = SplitList(AlwaysStatusesBox.Text);
        editing.Returned.NeverReturnedStatuses = SplitList(NeverStatusesBox.Text);
        editing.Browser = new BrowserSettings
        {
            CloseWindowWhenDone = CloseWindowBox.IsChecked == true,
            TimeoutSeconds = timeout
        };
        editing.Sync.AutoSync = AutoSyncBox.IsChecked == true;
        editing.Sync.IntervalMinutes = interval;

        if (SaveRequested is not null)
        {
            await SaveRequested(editing);
        }

        // Keep editing a fresh copy so a second Save doesn't share objects with the app's settings.
        if (settingsSource is not null)
        {
            Populate(settingsSource());
        }

        ErrorText.Text = "";
        SavedText.Visibility = Visibility.Visible;
    }

    private async void SaveCentral_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        var server = CentralServerBox.Text.Trim();
        if (!model.IsCentralServerLocked && server.Length > 0 && !MainViewModel.IsValidCentralServer(server))
        {
            CentralMessage.Text = "Enter the server's full address, starting with https://";
            return;
        }

        CentralMessage.Text = "Connecting…";
        var problem = await model.ConnectCentralAsync(server, CentralTokenBox.Password);
        CentralTokenBox.Clear();
        CentralServerBox.Text = model.CentralServerText;
        CentralMessage.Text = problem ?? "Connected: your data is shared every couple of minutes while CA Tracker is open.";
    }

    private async void ShareCentral_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        CentralMessage.Text = "Sharing…";
        CentralMessage.Text = await model.ShareWithCentralAsync() ?? "Shared.";
    }

    private void DisconnectCentral_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            model.DisconnectCentral();
            CentralMessage.Text = "Disconnected. Everything stays on this PC until you connect again.";
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionViewModel connection })
        {
            ConnectRequested?.Invoke(connection.Provider);
        }
    }

    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionViewModel connection })
        {
            SyncRequested?.Invoke(connection.Provider);
        }
    }

    private static List<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
