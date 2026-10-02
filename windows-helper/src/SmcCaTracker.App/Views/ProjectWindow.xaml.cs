using System.IO;
using System.Windows;
using Microsoft.Win32;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Comparison;
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
        SaveButton.Content = isNew ? "_Create project" : "_Save";
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

        var scope = project.Scope ?? new PhaseScope();
        PhaseWhole.IsChecked = scope.Phase == ProjectPhase.Whole;
        PhaseBaseBuild.IsChecked = scope.Phase == ProjectPhase.BaseBuild;
        PhaseFitOut.IsChecked = scope.Phase == ProjectPhase.TenantFitOut;
        FilterItemsBox.IsChecked = scope.FilterItems;
        FilterFoldersBox.IsChecked = scope.FilterFolders;
        IncludeUnlabeledBox.IsChecked = scope.IncludeUnlabeled;
        PhaseOptions.IsEnabled = scope.IsSplit;
        Loaded += (_, _) => NameBox.Focus();
    }

    public TrackerProject? Result { get; private set; }
    public bool RemoveRequested { get; private set; }

    private void ShowConnection()
    {
        ConnectionText.Text = project.Connection?.Label ?? "Not linked to a Helix or Procore project.";
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
        // Helix sometimes gives a company's id without its name: name it by the people in it instead.
        var companies = (snapshot?.Items.SelectMany(item => item.Holders) ?? Enumerable.Empty<SnapshotHolder>())
            .Where(holder => !string.IsNullOrWhiteSpace(holder.CompanyId ?? holder.Company))
            .GroupBy(holder => holder.CompanyId ?? holder.Company!, StringComparer.OrdinalIgnoreCase);
        foreach (var company in companies)
        {
            var name = company.Select(holder => holder.Company).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            var people = company.Select(holder => holder.Name.Trim()).Where(person => person.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(person => person, StringComparer.CurrentCultureIgnoreCase).ToList();
            options.Add(new CompanyOption(company.First().CompanyId, name, name ?? UnnamedLabel(people)));
        }

        if ((project.CompanyId is not null || project.CompanyName is not null) && !options.Any(IsSaved))
        {
            options.Add(new CompanyOption(project.CompanyId, project.CompanyName,
                project.CompanyName ?? "Unnamed company (not seen in the latest sync)"));
        }

        var ordered = options.Take(1)
            .Concat(options.Skip(1).OrderBy(option => option.Name is null).ThenBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
        CompanyList.ItemsSource = ordered;
        CompanyList.SelectedItem = ordered.Skip(1).FirstOrDefault(IsSaved) ?? ordered[0];
        CompanyNote.Visibility = ordered.Count > 1 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The company saved for this project (by id when there is one: its name may have been unknown then).</summary>
    private bool IsSaved(CompanyOption option) => option.Id is not null
        ? string.Equals(option.Id, project.CompanyId, StringComparison.OrdinalIgnoreCase)
        : project.CompanyId is null && string.Equals(option.Name, project.CompanyName, StringComparison.OrdinalIgnoreCase);

    private static string UnnamedLabel(IReadOnlyList<string> people) => people.Count switch
    {
        0 => "Unnamed company",
        <= 3 => $"Unnamed company: {string.Join(", ", people)}",
        _ => $"Unnamed company: {string.Join(", ", people.Take(3))} and {people.Count - 3} more"
    };

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
        var window = new ConnectWindow(viewModel.KnownProviderProjects(), project.Connection) { Owner = this };
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

    private void BrowseRfi_Click(object sender, RoutedEventArgs e) =>
        BrowseSubfolder(RfiBox, FolderBox.Text, "Choose this project's RFI folder");

    private void BrowseSubmittal_Click(object sender, RoutedEventArgs e) =>
        BrowseSubfolder(SubmittalBox, FolderBox.Text, "Choose this project's Submittal folder");

    private void BrowseReturned_Click(object sender, RoutedEventArgs e) =>
        BrowseSubfolder(ReturnedBox, Resolve(FolderBox.Text, RfiBox.Text), "Choose the returned folder (inside the RFI folder, or one shared folder)");

    /// <summary>
    /// Opens a folder picker at the folder currently selected in <paramref name="box"/> (or the nearest
    /// folder that exists). A choice inside <paramref name="baseFolder"/> is stored relative to it,
    /// anything else as a full path.
    /// </summary>
    private void BrowseSubfolder(System.Windows.Controls.TextBox box, string? baseFolder, string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        var start = NearestExisting(Resolve(baseFolder, box.Text.Trim())) ?? NearestExisting(baseFolder);
        if (start is not null)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var chosen = Path.TrimEndingDirectorySeparator(dialog.FolderName);
        box.Text = IsInside(chosen, baseFolder)
            ? Path.GetRelativePath(Path.GetFullPath(baseFolder!), chosen)
            : chosen;
        if (box == ReturnedBox && Path.IsPathFullyQualified(box.Text))
        {
            FolderNote.Text = "This returned folder is outside the RFI folder, so RFIs and Submittals will share it.";
        }
    }

    private static string? Resolve(string? baseFolder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return baseFolder;
        }

        if (Path.IsPathFullyQualified(value))
        {
            return value;
        }

        return string.IsNullOrWhiteSpace(baseFolder) ? null : Path.Combine(baseFolder, value);
    }

    private static string? NearestExisting(string? path)
    {
        try
        {
            var current = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current))
                {
                    return current;
                }

                current = Path.GetDirectoryName(current);
            }
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return null;
    }

    private static bool IsInside(string path, string? baseFolder)
    {
        if (string.IsNullOrWhiteSpace(baseFolder))
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseFolder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private ProjectPhase SelectedPhase =>
        PhaseBaseBuild.IsChecked == true ? ProjectPhase.BaseBuild
        : PhaseFitOut.IsChecked == true ? ProjectPhase.TenantFitOut
        : ProjectPhase.Whole;

    private void Phase_Changed(object sender, RoutedEventArgs e)
    {
        PhaseOptions.IsEnabled = SelectedPhase != ProjectPhase.Whole;
    }

    /// <summary>Two projects may share a CA folder or a connection only as different parts of the same job.</summary>
    private bool IsOtherPart(TrackerProject other) =>
        SelectedPhase != ProjectPhase.Whole
        && other.Scope is { IsSplit: true }
        && other.Scope.Phase != SelectedPhase;

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
        else if (viewModel.Settings.Projects.Any(other => other.Id != project.Id && !IsOtherPart(other)
                     && string.Equals(Path.TrimEndingDirectorySeparator(other.CaFolder), Path.TrimEndingDirectorySeparator(FolderBox.Text), StringComparison.OrdinalIgnoreCase)))
        {
            problems.Add("Another project already uses this CA folder. If they are the Base Build and Tenant Fit Out parts of one job, set each project's part below.");
        }

        if (project.Connection is not null && viewModel.Settings.Projects.Any(other => other.Id != project.Id && !IsOtherPart(other)
                && other.Connection is not null
                && string.Equals(other.Connection.Provider, project.Connection.Provider, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.Connection.ProjectId, project.Connection.ProjectId, StringComparison.Ordinal)))
        {
            problems.Add($"Another project is already linked to {project.Connection.Label}. If they are the Base Build and Tenant Fit Out parts of one job, set each project's part below.");
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
        project.Scope = new PhaseScope
        {
            Phase = SelectedPhase,
            FilterItems = SelectedPhase != ProjectPhase.Whole && FilterItemsBox.IsChecked == true,
            FilterFolders = SelectedPhase != ProjectPhase.Whole && FilterFoldersBox.IsChecked == true,
            IncludeUnlabeled = SelectedPhase != ProjectPhase.Whole && IncludeUnlabeledBox.IsChecked == true
        };
        Result = project;
        DialogResult = true;
    }
}
