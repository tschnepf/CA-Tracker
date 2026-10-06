using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Downloads;

namespace SmcCaTracker.App.Views;

/// <summary>
/// The item details pane beside the list: the selected item's due date, who has it and who's next, its status,
/// folder and discipline, its actions, and the files in its folder (newest first, each with Open). Built from
/// what CA Tracker has synced, so showing it reads nothing from Helix; the folder is listed in the background.
/// It shares the panel with the Helix page: Open in Helix switches the panel to it.
/// </summary>
public partial class DetailsPanel : UserControl
{
    // Counts folder reads, so a slow read for an item shown before doesn't replace the current one's.
    private int filesVersion;
    private string? filesFolder;

    public DetailsPanel()
    {
        InitializeComponent();
    }

    /// <summary>A button asks for one of the row's actions ("open", "download", "folder", "verify", "newest", "discipline").</summary>
    public event Action<string, RowViewModel>? ActionRequested;

    /// <summary>The ✕: hide the details.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>One file in the folder list.</summary>
    public sealed record FileLine(string Name, string When, string Path);

    /// <summary>Shows a row (or nothing). The folder is listed again only when it is a different folder.</summary>
    public void Show(RowViewModel? row)
    {
        DataContext = row;
        EmptyText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        var folder = row?.Row.Folder?.FullPath;
        if (!string.Equals(folder, filesFolder, StringComparison.OrdinalIgnoreCase) || folder is null)
        {
            _ = ListFolderAsync(row, folder);
        }
    }

    private async Task ListFolderAsync(RowViewModel? row, string? folder)
    {
        var attempt = ++filesVersion;
        filesFolder = folder;
        FilesList.ItemsSource = null;
        if (folder is null)
        {
            FilesStatus.Text = row is { CanDownload: true } ? "No folder yet. Download creates one." : row is null ? "" : "No folder yet.";
            return;
        }

        FilesStatus.Text = "Reading the folder…";
        IReadOnlyList<FolderFile> files;
        try
        {
            files = await Task.Run(() => FolderFiles.List(folder, 40));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (attempt == filesVersion)
            {
                filesFolder = null;
                FilesStatus.Text = $"The folder couldn't be read: {error.Message}";
            }

            return;
        }

        if (attempt != filesVersion)
        {
            return;
        }

        FilesList.ItemsSource = files.Select(file => new FileLine(file.RelativePath, Formatting.When(new DateTimeOffset(file.Modified)), file.FullPath)).ToList();
        FilesStatus.Text = files.Count == 0 ? "The folder is empty." : files.Count == 40 ? "The 40 newest files:" : "";
    }

    private void Ask(string action)
    {
        if (DataContext is RowViewModel row)
        {
            ActionRequested?.Invoke(action, row);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Ask("open");
    private void Download_Click(object sender, RoutedEventArgs e) => Ask("download");
    private void Folder_Click(object sender, RoutedEventArgs e) => Ask("folder");
    private void Verify_Click(object sender, RoutedEventArgs e) => Ask("verify");
    private void Newest_Click(object sender, RoutedEventArgs e) => Ask("newest");
    private void Discipline_Click(object sender, RoutedEventArgs e) => Ask("discipline");
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens a file from the item's folder in its own app (nothing in the folder changes).</summary>
    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileLine file })
        {
            try
            {
                Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                AppLog.Error("details", "Opening a file from an item's folder failed", error);
                FilesStatus.Text = $"{file.Name} couldn't be opened: {error.Message}";
            }
        }
    }
}
