using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

public sealed record ConnectOption(CatalogProject Project, string Title, string Detail, string ToolTip);

/// <summary>Picks the Helix or Procore project a tracker project is linked to.</summary>
public partial class ConnectWindow : Window
{
    private readonly IReadOnlyList<CatalogProject> known;
    private readonly bool loading;

    public ConnectWindow(IReadOnlyList<CatalogProject> known, ProviderConnection? current)
    {
        loading = true;
        InitializeComponent();
        ThemeManager.Attach(this);
        // Choosing from the list replaces a pasted link (and a pasted link clears the list's choice).
        ProjectList.SelectionChanged += (_, _) =>
        {
            if (ProjectList.SelectedItem is not null && LinkBox.Text.Length > 0)
            {
                LinkBox.Clear();
            }
        };
        this.known = known;
        var provider = current?.Provider ?? Providers.Helix;
        HelixOption.IsChecked = provider == Providers.Helix;
        ProcoreOption.IsChecked = provider == Providers.Procore;
        loading = false;
        Refilter();
        if (current is not null)
        {
            ProjectList.SelectedItem = ProjectList.Items.OfType<ConnectOption>().FirstOrDefault(option =>
                option.Project.ProjectId == current.ProjectId && option.Project.Provider == current.Provider);
        }
    }

    public ProviderConnection? Result { get; private set; }

    private string SelectedProvider => ProcoreOption.IsChecked == true ? Providers.Procore : Providers.Helix;

    private void Provider_Checked(object sender, RoutedEventArgs e)
    {
        if (!loading)
        {
            Refilter();
        }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!loading)
        {
            Refilter();
        }
    }

    private void Refilter()
    {
        var provider = SelectedProvider;
        var search = SearchBox.Text.Trim();
        var options = known
            .Where(project => string.Equals(project.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .Where(project => search.Length == 0
                || project.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || (project.AccountName ?? "").Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || project.ProjectId.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(project => new ConnectOption(project, project.Name, Detail(project), $"{Providers.Label(project.Provider)} project id {project.ProjectId}"))
            .ToList();
        ProjectList.ItemsSource = options;
        var label = Providers.Label(provider);
        EmptyText.Text = options.Count > 0 ? ""
            : search.Length > 0 ? "No matching projects."
            : provider == Providers.Helix
                ? "No Helix projects listed yet. Sign in to Helix in Settings → Connections and your projects appear here, or paste a project link below."
                : "No Procore projects listed yet. Sign in to Procore in Settings → Connections and every project you can see appears here, or paste a project's RFIs link below.";
        EmptyText.Visibility = options.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        OpenSiteButton.Content = $"Open {label} to copy a link";
    }

    private static string Detail(CatalogProject project)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(project.AccountName))
        {
            parts.Add(project.AccountName!);
        }

        if (project.SeenAt != default)
        {
            parts.Add($"listed {Formatting.When(project.SeenAt)}");
        }

        return string.Join(" · ", parts);
    }

    private void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Opens your usual web browser so you can find the project and copy its link.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                SelectedProvider == Providers.Procore ? "https://app.procore.com/" : "https://project.helixteam.com/")
            {
                UseShellExecute = true
            });
        }
        catch (Exception error)
        {
            ErrorText.Text = error.Message;
        }
    }

    /// <summary>A pasted link is used instead of the list: say so, and clear the list's choice.</summary>
    private void Link_Changed(object sender, TextChangedEventArgs e)
    {
        if (LinkBox.Text.Trim().Length > 0)
        {
            ProjectList.SelectedItem = null;
            ErrorText.Text = "";
        }
    }

    private void ProjectList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProjectList.SelectedItem is ConnectOption)
        {
            Connect_Click(sender, e);
        }
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var link = LinkBox.Text.Trim();
        if (link.Length > 0)
        {
            if (!ProviderLinks.TryParse(link, out var parsed, out var error))
            {
                ErrorText.Text = error;
                return;
            }

            var match = known.FirstOrDefault(project =>
                string.Equals(project.Provider, parsed!.Provider, StringComparison.OrdinalIgnoreCase)
                && project.ProjectId == parsed.ProjectId);
            parsed!.ProjectName = match?.Name ?? "";
            parsed.Host ??= match?.Host;
            Result = parsed;
            DialogResult = true;
            return;
        }

        if (ProjectList.SelectedItem is not ConnectOption option)
        {
            ErrorText.Text = "Choose a project from the list or paste its link.";
            return;
        }

        if (option.Project.Provider == Providers.Procore && string.IsNullOrWhiteSpace(option.Project.AccountId))
        {
            ErrorText.Text = "Procore did not say which company this project belongs to. Paste the project's RFIs link instead.";
            return;
        }

        Result = option.Project.ToConnection();
        DialogResult = true;
    }
}
