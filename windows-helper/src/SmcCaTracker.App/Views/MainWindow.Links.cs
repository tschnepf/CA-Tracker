using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Links;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.App.Views;

/// <summary>
/// Links from Workload Tracker's web list (smc-ca-tracker://item?…). Windows starts CA Tracker with the
/// link; when it is already running, the new start hands the link over and leaves. The item is shown in the
/// list and the link's button runs as if pressed here, with its usual windows: nothing changes a folder or a
/// setting without them.
/// </summary>
public partial class MainWindow
{
    private ItemLink? nextLink;
    private bool openingLink;

    /// <summary>CA Tracker was started again (a link clicked, or opened from the Start menu): come forward, and open the link.</summary>
    public void OnStartedAgain(string message)
    {
        BringToFront();
        if (message.Length == 0)
        {
            return;
        }

        if (ItemLink.Parse(message) is { } link)
        {
            OpenLinkWhenReady(link);
        }
        else
        {
            AppLog.Warn("links", "A link that isn't a CA Tracker item link was ignored.");
        }
    }

    /// <summary>Opens a link once the window has put back the last view (at once when it already has).</summary>
    public void OpenLinkWhenReady(ItemLink link)
    {
        if (IsLoaded)
        {
            _ = OpenLinkAsync(link);
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            Dispatcher.InvokeAsync(() => OpenLinkAsync(link), DispatcherPriority.Background);
        }

        Loaded += OnLoaded;
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            SystemCommands.RestoreWindow(this);
        }

        Show();
        Activate();
        // Activate alone can leave the window behind the browser the link was clicked in.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>One link at a time; a link clicked meanwhile is opened next (only the latest).</summary>
    private async Task OpenLinkAsync(ItemLink link)
    {
        if (openingLink)
        {
            nextLink = link;
            return;
        }

        openingLink = true;
        try
        {
            for (var current = link; current is not null; current = nextLink, nextLink = null)
            {
                try
                {
                    BringToFront();
                    await ShowLinkedItemAsync(current);
                }
                catch (Exception error)
                {
                    AppLog.Error("links", "Opening a link failed", error);
                    viewModel.StatusMessage = $"The link to {current.ItemLabel} couldn't be opened: {error.Message}";
                }
            }
        }
        finally
        {
            openingLink = false;
        }
    }

    /// <summary>Shows the link's item in the list, with nothing hiding it, then runs what the link asks for.</summary>
    private async Task ShowLinkedItemAsync(ItemLink link)
    {
        AppLog.Info("links", $"Link: {link.Action} {link.ItemLabel} ({link.Provider} project {link.ProjectId}).");
        if (ComponentDispatcher.IsThreadModal)
        {
            // A dialog is open: the link doesn't open another window over it.
            viewModel.StatusMessage = $"Close the open dialog in CA Tracker, then click the link to {link.ItemLabel} again.";
            return;
        }

        // Back to the item list: out of Settings and Project settings (each saves or asks first, as usual).
        if (SettingsView.Visibility == Visibility.Visible)
        {
            if (!SettingsView.ConfirmLeave())
            {
                return;
            }

            ShowProjects();
        }

        if (!await LeaveSettingsForListAsync())
        {
            return;
        }

        var provider = Providers.Label(link.Provider);
        viewModel.ProjectSearch = "";
        var project = viewModel.ProjectForLink(link);
        if (project is null)
        {
            viewModel.StatusMessage = $"CA Tracker doesn't have {link.ItemLabel} on this PC yet: its {provider} project isn't in your project list. " +
                $"Connect to {provider} and sync, then try again.";
            return;
        }

        if (project.IsArchived && !viewModel.IsShownInList(project))
        {
            viewModel.StatusMessage = $"{link.ItemLabel} is in {project.Name}, which is archived. Check Show archived in the project list, then try again.";
            return;
        }

        // That project alone, every item of either type, no search: nothing hides the row.
        viewModel.SetSelectedProjects(new[] { project }, project);
        viewModel.ItemSearch = "";
        viewModel.SearchAllItems();
        BothTypeButton.IsChecked = true;
        ShowFilterChoices();
        ApplyItemGrouping();
        await viewModel.CompareAsync();

        var row = viewModel.RowForLink(link, project);
        if (row is null)
        {
            viewModel.StatusMessage = $"CA Tracker doesn't have {link.ItemLabel} on this PC yet. Sync the project, then try again.";
            return;
        }

        ItemsGrid.SelectedItem = row;
        ItemsGrid.ScrollIntoView(row);
        await RunLinkActionAsync(link.Action, row);
    }

    /// <summary>The link's button, through the same code as the row's own buttons and menu.</summary>
    private async Task RunLinkActionAsync(string action, RowViewModel row)
    {
        var label = $"{row.TypeSingular} {row.Number}";
        switch (action)
        {
            case ItemLinkActions.Open:
                await OpenItemAsync(row);
                break;
            case ItemLinkActions.Folder:
                if (row.Row.Folder is { } folder)
                {
                    OpenInExplorer(folder.FullPath);
                }
                else
                {
                    await ShowDetailsAsync(row);
                    viewModel.StatusMessage = $"No folder yet for {label}.";
                }

                break;
            case ItemLinkActions.Newest:
                if (viewModel.DownloadRequestFor(row) is not null)
                {
                    OpenNewestFile(row);
                }
                else
                {
                    await ShowDetailsAsync(row);
                    viewModel.StatusMessage = $"{label}: no Helix or Procore item, so there is no newest file to open.";
                }

                break;
            case ItemLinkActions.Download:
                if (viewModel.DownloadRequestFor(row) is { } request)
                {
                    ShowDownloadWindow(new[] { request });
                }
                else
                {
                    await ShowDetailsAsync(row);
                    viewModel.StatusMessage = $"{label}: no Helix or Procore item, so there is nothing to download.";
                }

                break;
            case ItemLinkActions.Verify:
                if (viewModel.DownloadRequestFor(row) is { } check)
                {
                    ShowDownloadWindow(new[] { check }, verify: true);
                }
                else
                {
                    await ShowDetailsAsync(row);
                    viewModel.StatusMessage = $"{label}: no Helix or Procore item to check its folder against.";
                }

                break;
            case ItemLinkActions.Discipline:
                await ChooseDisciplineAsync(new[] { row }, row.Row.Folder?.Name ?? $"{row.Number} - {row.Title}");
                break;
            default:
                await ShowDetailsAsync(row);
                break;
        }
    }
}
