using System.IO;
using System.Windows;
using Microsoft.Win32;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

public sealed record CompanyOption(string? Id, string? Name, string Label);

/// <summary>Creates or edits a project. Changes only the tracker's settings, never the CA folder.</summary>
public partial class ProjectWindow : Window
{
    private readonly TrackerProject project;
    private readonly bool isNew;
    private readonly MainViewModel viewModel;

    public ProjectWindow(TrackerProject project, bool isNew, MainViewModel viewModel)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.project = project;
        this.isNew = isNew;
        this.viewModel = viewModel;

        Title = isNew ? "New project" : $"Edit project — {project.Name}";
        SaveButton.Content = isNew ? "Create project" : "Save";
        RemoveButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        NameBox.Text = project.Name;
        FolderBox.Text = project.CaFolder;
        ShowConnection();
        LoadCompanies();

        var folders = project.FoldersOverride;
        OverrideFoldersBox.IsChecked = folders is not null;
        var effective = folders ?? viewModel.Settings.Folders;
        RfiBox.Text = effective.RfiFolder;
        SubmittalBox.Text = effective.SubmittalFolder;
        ReturnedBox.Text = effective.ReturnedFolder;
        OverrideGrid.IsEnabled = folders is not null;
        Loaded += (_, _) => NameBox.Focus();
    }

    public TrackerProject? Result { get; private set; }
    public bool RemoveRequested { get; private set; }

    private void ShowConnection()
    {
        ConnectionText.Text = project.Connection?.Label ?? "Not connected to Helix or Procore.";
        DisconnectButton.Visibility = project.Connection is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LoadCompanies()
    {
        var snapshot = viewModel.SnapshotFor(project);
        var loginCompany = snapshot?.User.Company ?? snapshot?.User.CompanyId;
        var provider = project.Connection is null ? "Helix or Procore" : Providers.Label(project.Connection.Provider);
        var options = new List<CompanyOption>
        {
            new(null, null, loginCompany is null
                ? $"My company from the {provider} login"
                : $"My company from the {provider} login ({loginCompany})")
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

        if ((project.CompanyId is not null || project.CompanyName is not null)
            && !options.Any(option => option.Id == project.CompanyId && option.Name == project.CompanyName))
        {
            options.Add(new CompanyOption(project.CompanyId, project.CompanyName, project.CompanyName ?? project.CompanyId!));
        }

        var ordered = options.Take(1)
            .Concat(options.Skip(1).OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
        CompanyList.ItemsSource = ordered;
        CompanyList.SelectedItem = ordered.FirstOrDefault(option =>
            option.Id == project.CompanyId && option.Name == project.CompanyName) ?? ordered[0];
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the project's CA folder", Multiselect = false };
        if (Directory.Exists(FolderBox.Text))
        {
            dialog.InitialDirectory = FolderBox.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var window = new ConnectWindow(viewModel.KnownProviderProjects(), project.Connection, viewModel.Settings.Browser) { Owner = this };
        if (window.ShowDialog() == true && window.Result is not null)
        {
            project.Connection = window.Result;
            ShowConnection();
            LoadCompanies();
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        project.Connection = null;
        ShowConnection();
        LoadCompanies();
    }

    private void OverrideFolders_Changed(object sender, RoutedEventArgs e)
    {
        OverrideGrid.IsEnabled = OverrideFoldersBox.IsChecked == true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            $"Remove \"{project.Name}\" from the tracker?\n\nThe CA folder and everything in it stay exactly as they are.",
            "Remove project",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes)
        {
            RemoveRequested = true;
            DialogResult = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var problems = new List<string>();
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            problems.Add("Give the project a name.");
        }

        if (string.IsNullOrWhiteSpace(FolderBox.Text))
        {
            problems.Add("Choose the project's CA folder.");
        }
        else if (viewModel.Settings.Projects.Any(other => other.Id != project.Id
                     && string.Equals(Path.TrimEndingDirectorySeparator(other.CaFolder), Path.TrimEndingDirectorySeparator(FolderBox.Text), StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("Another project already uses this CA folder.");
        }

        if (project.Connection is not null && viewModel.Settings.Projects.Any(other => other.Id != project.Id
                && other.Connection is not null
                && string.Equals(other.Connection.Provider, project.Connection.Provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.Connection.ProjectId, project.Connection.ProjectId, StringComparison.Ordinal)))
        {
            problems.Add($"Another project is already connected to {project.Connection.Label}.");
        }

        FolderLayout? folders = null;
        if (OverrideFoldersBox.IsChecked == true)
        {
            folders = new FolderLayout
            {
                RfiFolder = RfiBox.Text.Trim(),
                SubmittalFolder = SubmittalBox.Text.Trim(),
                ReturnedFolder = ReturnedBox.Text.Trim()
            };
            problems.AddRange(folders.Validate());
        }

        if (problems.Count > 0)
        {
            ErrorText.Text = string.Join(" ", problems);
            return;
        }

        var company = CompanyList.SelectedItem as CompanyOption;
        project.Name = name;
        project.CaFolder = FolderBox.Text.Trim();
        project.CompanyId = company?.Id;
        project.CompanyName = company?.Name;
        project.FoldersOverride = folders;
        Result = project;
        DialogResult = true;
    }
}
