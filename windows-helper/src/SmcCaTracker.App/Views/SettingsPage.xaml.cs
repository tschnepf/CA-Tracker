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
    Connections,
    People
}

/// <summary>
/// The Settings page inside the main window: a sidebar with General settings, Connections and People.
/// General edits a copy of the settings and applies it on Save (the theme is saved as soon as it's
/// picked); leaving with unsaved changes asks first. Connections shows each site's sign-in and last
/// sync, with Sign in and Sync buttons handled by the main window.
/// </summary>
public partial class SettingsPage : UserControl
{
    private Func<TrackerSettings>? settingsSource;
    private AppPaths? paths;
    private TrackerSettings? editing;
    private string savedFields = "";
    private bool loading;

    public SettingsPage()
    {
        InitializeComponent();

        // Every edit updates Save/Discard and checks the field at once, rather than only on Save.
        foreach (var box in new[] { RfiFolderBox, SubmittalFolderBox, ReturnedFolderBox, AlwaysStatusesBox, NeverStatusesBox, WorkHoursIntervalBox, OffHoursIntervalBox, TimeoutBox })
        {
            box.TextChanged += (_, _) => OnEdited();
        }

        foreach (var box in new[] { WhenNotHeldBox, WhenClosedBox, WhenComingUpBox, AutoSyncBox, CloseWindowBox })
        {
            box.Checked += (_, _) => OnEdited();
            box.Unchecked += (_, _) => OnEdited();
        }
    }

    public event EventHandler? BackRequested;
    public event Func<TrackerSettings, Task>? SaveRequested;
    public event Func<ThemePreference, Task>? ThemeChosen;
    public event Action<string>? ConnectRequested;
    public event Action<string>? SyncRequested;

    /// <summary>Loads a fresh copy of the settings and shows the given section.</summary>
    public void Open(Func<TrackerSettings> source, AppPaths appPaths, SettingsSection section)
    {
        settingsSource = source;
        paths = appPaths;
        Populate(source());
        SectionList.SelectedItem = ItemFor(section);
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
        var model = DataContext as MainViewModel;
        var foldersLocked = model?.AreFoldersLocked == true;
        RfiFolderBox.IsEnabled = SubmittalFolderBox.IsEnabled = ReturnedFolderBox.IsEnabled = !foldersLocked;
        WorkHoursIntervalBox.IsEnabled = OffHoursIntervalBox.IsEnabled = model?.IsSyncLocked != true;
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
        WorkHoursIntervalBox.Text = settings.Sync.WorkHoursIntervalMinutes.ToString();
        OffHoursIntervalBox.Text = settings.Sync.OffHoursIntervalMinutes.ToString();
        TimeoutBox.Text = settings.Browser.TimeoutSeconds.ToString();
        loading = false;

        savedFields = CurrentFields();
        OnEdited();
        SavedText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Everything General settings edits (but the theme, which saves itself), to tell whether it changed.</summary>
    private string CurrentFields() => string.Join("\u001f",
        RfiFolderBox.Text, SubmittalFolderBox.Text, ReturnedFolderBox.Text, AlwaysStatusesBox.Text, NeverStatusesBox.Text,
        WorkHoursIntervalBox.Text, OffHoursIntervalBox.Text, TimeoutBox.Text, WhenNotHeldBox.IsChecked, WhenClosedBox.IsChecked, WhenComingUpBox.IsChecked,
        AutoSyncBox.IsChecked, CloseWindowBox.IsChecked);

    private bool IsDirty => !loading && editing is not null && CurrentFields() != savedFields;

    private void OnEdited()
    {
        if (loading)
        {
            return;
        }

        var dirty = IsDirty;
        SaveButton.IsEnabled = DiscardButton.IsEnabled = dirty;
        UnsavedText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        if (dirty)
        {
            SavedText.Visibility = Visibility.Collapsed;
        }

        Validate();
    }

    /// <summary>Shows each field's problem under it. True when there are none.</summary>
    private bool Validate()
    {
        static void Show(TextBlock target, string? problem)
        {
            target.Text = problem ?? "";
            target.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        }

        var folderProblems = Folders().Validate().ToList();
        Show(FoldersError, folderProblems.Count == 0 ? null : string.Join(" ", folderProblems));
        Show(IntervalError, !InRange(WorkHoursIntervalBox, SyncSettings.WorkHoursMinimum, SyncSettings.WorkHoursMaximum)
            ? $"Enter {SyncSettings.WorkHoursMinimum} to {SyncSettings.WorkHoursMaximum} minutes for 7 am to 5 pm."
            : !InRange(OffHoursIntervalBox, SyncSettings.OffHoursMinimum, SyncSettings.OffHoursMaximum)
                ? $"Enter {SyncSettings.OffHoursMinimum} to {SyncSettings.OffHoursMaximum} minutes for other times."
                : null);
        Show(TimeoutError, int.TryParse(TimeoutBox.Text.Trim(), out var timeout) && timeout is >= 30 and <= 1800
            ? null
            : "Enter 30 to 1800 seconds.");
        var valid = FoldersError.Visibility != Visibility.Visible && IntervalError.Visibility != Visibility.Visible
            && TimeoutError.Visibility != Visibility.Visible;
        ErrorText.Text = valid ? "" : "Fix the fields marked in red to save.";
        return valid;
    }

    private static bool InRange(TextBox box, int minimum, int maximum) =>
        int.TryParse(box.Text.Trim(), out var value) && value >= minimum && value <= maximum;

    private FolderLayout Folders() => new()
    {
        RfiFolder = RfiFolderBox.Text.Trim(),
        SubmittalFolder = SubmittalFolderBox.Text.Trim(),
        ReturnedFolder = ReturnedFolderBox.Text.Trim()
    };

    /// <summary>
    /// Leaving General settings with unsaved changes asks whether to save them. False when you chose
    /// to stay (or they couldn't be saved yet).
    /// </summary>
    public bool ConfirmLeave()
    {
        if (!IsDirty)
        {
            return true;
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            "Save your changes to General settings?",
            "Unsaved changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        switch (answer)
        {
            case MessageBoxResult.Yes:
                if (!Validate())
                {
                    ShowSectionFromOutside(SettingsSection.General);
                    return false;
                }

                _ = SaveAsync();
                return true;
            case MessageBoxResult.No:
                if (settingsSource is not null)
                {
                    Populate(settingsSource());
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>Switches section without reloading the settings being edited.</summary>
    public void ShowSectionFromOutside(SettingsSection section)
    {
        SectionList.SelectedItem = ItemFor(section);
        ShowSection(section);
    }

    private ListBoxItem ItemFor(SettingsSection section) => section switch
    {
        SettingsSection.Connections => ConnectionsItem,
        SettingsSection.People => PeopleItem,
        _ => GeneralItem
    };

    private void Section_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not null)
        {
            ShowSection(SectionList.SelectedItem == ConnectionsItem ? SettingsSection.Connections
                : SectionList.SelectedItem == PeopleItem ? SettingsSection.People
                : SettingsSection.General);
        }
    }

    private void ShowSection(SettingsSection section)
    {
        if (section == SettingsSection.Connections && DataContext is MainViewModel model)
        {
            WorkloadAddressBox.Text = model.WorkloadTrackerAddress;
            WorkloadTokenBox.Clear();
            ShowWorkloadMessage(null);
            SignOutMessage.Text = "";
        }

        GeneralPanel.Visibility = section == SettingsSection.General ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPanel.Visibility = section == SettingsSection.Connections ? Visibility.Visible : Visibility.Collapsed;
        PeoplePanel.Visibility = section == SettingsSection.People ? Visibility.Visible : Visibility.Collapsed;
        if (section == SettingsSection.People && DataContext is MainViewModel viewModel)
        {
            _ = PeoplePanel.OpenAsync(viewModel);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmLeave())
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (loading)
        {
            return;
        }

        var theme = SelectedTheme();
        ThemeManager.Apply(theme);
        if (editing is not null)
        {
            editing.Theme = theme;
        }

        if (ThemeChosen is not null)
        {
            await ThemeChosen(theme);
        }
    }

    private ThemePreference SelectedTheme() =>
        ThemeDark.IsChecked == true ? ThemePreference.Dark
        : ThemeLight.IsChecked == true ? ThemePreference.Light
        : ThemePreference.System;

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (settingsSource is not null)
        {
            Populate(settingsSource());
        }
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

    private async void ExportFixtures_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        ErrorText.Text = "";
        try
        {
            var (folder, problem) = await model.ExportWorkloadTrackerFixturesAsync();
            if (folder is null)
            {
                ErrorText.Text = $"No fixtures exported: {problem}";
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ErrorText.Text = $"No fixtures exported: {error.Message}";
        }
    }

    private async void ClearBrowser_Click(object sender, RoutedEventArgs e) => await SignOutAsync(forgetPasswords: false);

    private async void ForgetBrowser_Click(object sender, RoutedEventArgs e) => await SignOutAsync(forgetPasswords: true);

    /// <summary>Signs out of Helix and Procore, keeping the saved usernames and passwords unless told to forget them.</summary>
    private async Task SignOutAsync(bool forgetPasswords)
    {
        if (paths is null || DataContext is not MainViewModel { CanSignOutOfBrowser: true } model)
        {
            return;
        }

        var owner = Window.GetWindow(this);
        if (model.BrowserProfileInUse?.Invoke() is { } inUse)
        {
            SignOutMessage.Text = $"Close {inUse} first, then sign out.";
            return;
        }

        var answer = MessageBox.Show(
            owner,
            forgetPasswords
                ? "Sign CA Tracker's browser out of Helix and Procore and forget the usernames and passwords it saved? You'll type them again the next time you sign in.\n\nThe item panel closes."
                : "Sign CA Tracker's browser out of Helix and Procore? Syncing stops until you sign in again here; your saved usernames and passwords are kept and fill in when you do.\n\nThe item panel closes.",
            forgetPasswords ? "Sign out and forget saved passwords" : "Sign out",
            MessageBoxButton.YesNo,
            forgetPasswords ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var problem = await model.SignOutOfBrowserAsync(forgetPasswords);
        SignOutMessage.Text = problem is null ? "" : $"Could not sign out: {problem}";
        if (problem is null)
        {
            MessageBox.Show(owner,
                forgetPasswords
                    ? "Signed out of Helix and Procore, and the saved passwords are forgotten."
                    : "Signed out of Helix and Procore. Your saved usernames and passwords are kept.",
                "CA Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (editing is null || !Validate())
        {
            return;
        }

        int.TryParse(TimeoutBox.Text.Trim(), out var timeout);
        int.TryParse(WorkHoursIntervalBox.Text.Trim(), out var workHours);
        int.TryParse(OffHoursIntervalBox.Text.Trim(), out var offHours);
        editing.Theme = SelectedTheme();
        editing.Folders = Folders();
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
        editing.Sync.WorkHoursIntervalMinutes = workHours;
        editing.Sync.OffHoursIntervalMinutes = offHours;

        if (SaveRequested is not null)
        {
            await SaveRequested(editing);
        }

        // Keep editing a fresh copy so a second Save doesn't share objects with the app's settings.
        if (settingsSource is not null)
        {
            Populate(settingsSource());
        }

        SavedText.Visibility = Visibility.Visible;
    }

    private void ShowWorkloadMessage(string? message)
    {
        WorkloadMessage.Text = message ?? "";
        WorkloadMessage.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        WorkloadStatus.Visibility = message is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Opens the tracker's Profile page in the built-in browser; it hands the token over when you press Connect CA Tracker.</summary>
    private async void ConnectWorkload_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        var address = MainViewModel.NormalizeWorkloadTrackerAddress(WorkloadAddressBox.Text);
        if (address is null)
        {
            ShowWorkloadMessage("Enter the Workload Tracker address, starting with https://");
            return;
        }

        WorkloadAddressBox.Text = address;
        var window = new WorkloadTrackerSignInWindow(address, model.Paths) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.Token is not { } token)
        {
            ShowWorkloadMessage("Not connected: the window closed before Workload Tracker handed over a token.");
            return;
        }

        ShowWorkloadMessage("Connecting…");
        var problem = await model.ConnectWorkloadTrackerAsync(address, token.Token, token.SessionId, token.ExpiresAt);
        ShowWorkloadMessage(problem ?? "Connected: what CA Tracker reads is sent every minute while it is open.");
    }

    private async void PasteWorkloadToken_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        var address = MainViewModel.NormalizeWorkloadTrackerAddress(WorkloadAddressBox.Text);
        if (address is null)
        {
            ShowWorkloadMessage("Enter the Workload Tracker address, starting with https://");
            return;
        }

        if (WorkloadTokenBox.Password.Trim().Length == 0)
        {
            ShowWorkloadMessage("Paste the token from Workload Tracker first.");
            return;
        }

        ShowWorkloadMessage("Connecting…");
        var problem = await model.ConnectWorkloadTrackerAsync(address, WorkloadTokenBox.Password, null, null);
        WorkloadTokenBox.Clear();
        ShowWorkloadMessage(problem ?? "Connected: what CA Tracker reads is sent every minute while it is open. To disconnect this PC later, also revoke it under Active Sessions in Workload Tracker.");
    }

    private async void SendWorkload_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        ShowWorkloadMessage("Sending…");
        ShowWorkloadMessage(await model.SendToWorkloadTrackerAsync() ?? $"Sent. {model.WorkloadTrackerStatusText}");
    }

    private async void DisconnectWorkload_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Stop sending to {model.WorkloadTrackerAddress}? Everything stays on this PC, this PC's connection is revoked in Workload Tracker, and you can connect again later.",
            "Disconnect",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            await model.DisconnectWorkloadTrackerAsync();
            ShowWorkloadMessage("Disconnected. Everything stays on this PC until you connect again.");
        }
    }

    /// <summary>A site's main button: Sign in, or Sign out of that site while signed in.</summary>
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ConnectionViewModel connection })
        {
            return;
        }

        if (!connection.IsSignedIn || DataContext is not MainViewModel model)
        {
            ConnectRequested?.Invoke(connection.Provider);
            return;
        }

        var helix = !ProviderSites.IsProcore(connection.Provider);
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Sign out of {connection.Label}? Syncing {connection.Label} stops until you sign in again; your saved username and password are kept and fill in when you do." +
            (helix ? "\n\nThe item panel closes." : ""),
            $"Sign out of {connection.Label}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var problem = await model.SignOutOfSiteAsync(connection.Provider);
        connection.SyncMessage = problem is null
            ? $"Signed out of {connection.Label}. Your saved username and password are kept."
            : $"Could not sign out: {problem}";
    }

    private void Sync_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ConnectionViewModel connection })
        {
            SyncRequested?.Invoke(connection.Provider);
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

    private static List<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
