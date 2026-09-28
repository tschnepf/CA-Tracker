using System.Windows;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

public sealed record CompanyOption(string? Id, string? Name, string Label);

/// <summary>Edits a copy of the settings; the caller applies <see cref="Result"/> only after Save.</summary>
public partial class SettingsWindow : Window
{
    private readonly ProjectViewModel? project;
    private readonly ChromeRegistration registration;
    private readonly bool loading;

    public SettingsWindow(TrackerSettings settings, ProjectViewModel? project, AppPaths paths)
    {
        loading = true;
        InitializeComponent();
        ThemeManager.Attach(this);
        Result = settings;
        this.project = project;
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

        LoadProject(settings);
        RefreshRegistration();
        loading = false;
    }

    public TrackerSettings Result { get; }

    private void LoadProject(TrackerSettings settings)
    {
        if (project is null)
        {
            ProjectCard.Visibility = Visibility.Collapsed;
            return;
        }

        ProjectHeading.Text = $"THIS PROJECT — {project.Name.ToUpperInvariant()}";
        var mapping = settings.FindMapping(project.Provider, project.ProjectId);
        var snapshot = project.Snapshot;
        var loginCompany = snapshot?.User.Company ?? snapshot?.User.CompanyId;
        var options = new List<CompanyOption>
        {
            new(null, null, loginCompany is null
                ? $"My company from the {project.ProviderLabel} login (not known yet)"
                : $"My company from the {project.ProviderLabel} login ({loginCompany})")
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var holder in snapshot?.Items.SelectMany(item => item.Holders) ?? Enumerable.Empty<SnapshotHolder>())
        {
            var key = holder.CompanyId ?? holder.Company;
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key))
            {
                continue;
            }

            options.Add(new CompanyOption(holder.CompanyId, holder.Company, holder.Company ?? holder.CompanyId!));
        }

        if (mapping is not null && (mapping.CompanyId is not null || mapping.CompanyName is not null)
            && !options.Any(option => option.Id == mapping.CompanyId && option.Name == mapping.CompanyName))
        {
            options.Add(new CompanyOption(mapping.CompanyId, mapping.CompanyName, mapping.CompanyName ?? mapping.CompanyId!));
        }

        var ordered = options.Take(1).Concat(options.Skip(1).OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)).ToList();
        CompanyList.ItemsSource = ordered;
        CompanyList.SelectedItem = ordered.FirstOrDefault(option =>
            option.Id == mapping?.CompanyId && option.Name == mapping?.CompanyName) ?? ordered[0];

        var folders = mapping?.FoldersOverride;
        OverrideFoldersBox.IsChecked = folders is not null;
        var effective = folders ?? settings.Folders;
        ProjectRfiBox.Text = effective.RfiFolder;
        ProjectSubmittalBox.Text = effective.SubmittalFolder;
        ProjectReturnedBox.Text = effective.ReturnedFolder;
        OverrideGrid.IsEnabled = folders is not null;
    }

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

    private void OverrideFolders_Changed(object sender, RoutedEventArgs e)
    {
        OverrideGrid.IsEnabled = OverrideFoldersBox.IsChecked == true;
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

        FolderLayout? projectFolders = null;
        if (project is not null && OverrideFoldersBox.IsChecked == true)
        {
            projectFolders = new FolderLayout
            {
                RfiFolder = ProjectRfiBox.Text.Trim(),
                SubmittalFolder = ProjectSubmittalBox.Text.Trim(),
                ReturnedFolder = ProjectReturnedBox.Text.Trim()
            };
            problems.AddRange(projectFolders.Validate().Select(problem => $"This project: {problem}"));
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

        if (project is not null)
        {
            var company = CompanyList.SelectedItem as CompanyOption;
            var needsMapping = projectFolders is not null || company?.Id is not null || company?.Name is not null;
            var mapping = needsMapping
                ? Result.GetOrAddMapping(project.Provider, project.ProjectId, project.Name)
                : Result.FindMapping(project.Provider, project.ProjectId);
            if (mapping is not null)
            {
                mapping.CompanyId = company?.Id;
                mapping.CompanyName = company?.Name;
                mapping.FoldersOverride = projectFolders;
            }
        }

        DialogResult = true;
    }

    private static List<string> SplitList(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
