using System.Windows;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

/// <summary>Edits a copy of the settings; the caller applies <see cref="Result"/> only after Save.</summary>
public partial class SettingsWindow : Window
{
    private readonly ChromeRegistration registration;
    private readonly bool loading;

    public SettingsWindow(TrackerSettings settings, AppPaths paths)
    {
        loading = true;
        InitializeComponent();
        ThemeManager.Attach(this);
        Result = settings;
        registration = new ChromeRegistration(paths);

        ThemeSystem.IsChecked = settings.Theme == ThemePreference.System;
        ThemeLight.IsChecked = settings.Theme == ThemePreference.Light;
        ThemeDark.IsChecked = settings.Theme == ThemePreference.Dark;

        RfiFolderBox.Text = settings.Folders.RfiFolder;
        SubmittalFolderBox.Text = settings.Folders.SubmittalFolder;
        ReturnedFolderBox.Text = settings.Folders.ReturnedFolder;

        WhenNotHeldBox.IsChecked = settings.Returned.WhenNoCompanyHolder;
        WhenClosedBox.IsChecked = settings.Returned.WhenClosed;
        WhenComingUpBox.IsChecked = settings.Returned.WhenOnlyComingUp;
        AlwaysStatusesBox.Text = string.Join(", ", settings.Returned.AlwaysReturnedStatuses);
        NeverStatusesBox.Text = string.Join(", ", settings.Returned.NeverReturnedStatuses);
        ExtensionIdsBox.Text = string.Join(", ", settings.AllowedExtensionIds);

        ChromePathBox.Text = settings.Browser.ChromePath;
        ProfileBox.Text = settings.Browser.ProfileDirectory;
        CloseTabBox.IsChecked = settings.Browser.CloseTabWhenDone;
        TimeoutBox.Text = settings.Browser.TimeoutSeconds.ToString();
        var found = ChromeLauncher.FindChrome(settings.Browser.ChromePath);
        ChromeFoundText.Text = found is null ? "Chrome was not found automatically. Browse to chrome.exe." : $"Using {found}";
        RefreshRegistration();
        loading = false;
    }

    public TrackerSettings Result { get; }

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

    private void BrowseChrome_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Find chrome.exe",
            Filter = "Chrome|chrome.exe|Programs|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
        {
            ChromePathBox.Text = dialog.FileName;
            ChromeFoundText.Text = $"Using {dialog.FileName}";
        }
    }

    private void RefreshRegistration()
    {
        var status = registration.GetStatus();
        RegistrationText.Text = status.Detail;
        RegistrationText.Foreground = (System.Windows.Media.Brush)FindResource(status.IsRegistered ? "SuccessBrush" : "WarningBrush");
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            registration.Register(SplitList(ExtensionIdsBox.Text));
            ErrorText.Text = "";
        }
        catch (Exception error)
        {
            ErrorText.Text = error.Message;
        }

        RefreshRegistration();
    }

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            registration.Unregister();
            ErrorText.Text = "";
        }
        catch (Exception error)
        {
            ErrorText.Text = error.Message;
        }

        RefreshRegistration();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var folders = new FolderLayout
        {
            RfiFolder = RfiFolderBox.Text.Trim(),
            SubmittalFolder = SubmittalFolderBox.Text.Trim(),
            ReturnedFolder = ReturnedFolderBox.Text.Trim()
        };
        var problems = folders.Validate().ToList();

        if (!int.TryParse(TimeoutBox.Text.Trim(), out var timeout) || timeout < 30 || timeout > 1800)
        {
            problems.Add("Wait between 30 and 1800 seconds for Chrome.");
        }

        var extensionIds = SplitList(ExtensionIdsBox.Text);
        if (extensionIds.Count == 0)
        {
            problems.Add("Keep at least one Chrome extension ID.");
        }

        if (problems.Count > 0)
        {
            ErrorText.Text = string.Join(" ", problems);
            return;
        }

        Result.Theme = SelectedTheme();
        Result.Folders = folders;
        Result.Returned.WhenNoCompanyHolder = WhenNotHeldBox.IsChecked == true;
        Result.Returned.WhenClosed = WhenClosedBox.IsChecked == true;
        Result.Returned.WhenOnlyComingUp = WhenComingUpBox.IsChecked == true;
        Result.Returned.AlwaysReturnedStatuses = SplitList(AlwaysStatusesBox.Text);
        Result.Returned.NeverReturnedStatuses = SplitList(NeverStatusesBox.Text);
        Result.AllowedExtensionIds = extensionIds;

        Result.Browser = new BrowserSettings
        {
            ChromePath = ChromePathBox.Text.Trim(),
            ProfileDirectory = ProfileBox.Text.Trim(),
            CloseTabWhenDone = CloseTabBox.IsChecked == true,
            TimeoutSeconds = timeout
        };

        DialogResult = true;
    }

    private static List<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
