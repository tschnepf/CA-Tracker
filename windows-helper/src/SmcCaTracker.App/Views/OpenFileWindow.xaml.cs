using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Downloads;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

/// <summary>
/// "Open" on a Helix row: finds the item's newest attachment and opens it. When the item's folder already has
/// that file, the copy there opens; otherwise a copy is fetched from Helix into the app's own folder
/// (<see cref="AppPaths.OpenedFilesDirectory"/>) and opens from there. Nothing is written to the CA folder:
/// saving a file there is the Download window's job. Shows the Helix page only if Helix asks you to sign in,
/// and closes by itself once the file is open. Read-only in Helix, like the Download window.
/// </summary>
public partial class OpenFileWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly DownloadRequest request;
    private readonly AppPaths paths;
    private readonly CancellationTokenSource closing = new();
    private readonly HelixDownloadSession session;

    public OpenFileWindow(DownloadRequest request, AppPaths paths)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.request = request;
        this.paths = paths;
        var heading = $"{(request.ItemType == ItemTypes.Submittal ? "Submittal" : "RFI")} {request.Number} · {request.Title}";
        Title = $"Open {heading} — CA Tracker";
        HeadingText.Text = heading;
        HeadingText.ToolTip = heading;
        StatusText.Text = "Finding its newest file in Helix…";
        session = new HelixDownloadSession(Browser, paths, closing.Token);
        session.SignInChanged += ShowBrowser;
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) =>
        {
            closing.Cancel();
            Browser.Dispose();
        };
    }

    /// <summary>Says what was opened, for the main window's status line.</summary>
    public event Action<string>? FileOpened;

    /// <summary>The item has no attachments in Helix (the main window opens the item in Helix instead).</summary>
    public event Action? NoFiles;

    private async Task RunAsync()
    {
        try
        {
            await session.StartAsync();
            var files = await session.ReadItemAsync(request.ItemId, request.ItemType, request.ProjectId);
            var newest = NewestFile.Pick(files.Attachments, attachment => attachment.UploadedAt);
            if (newest is null)
            {
                NoFiles?.Invoke();
                Close();
                return;
            }

            var target = DownloadNaming.AttachmentFileName(newest.Filename, newest.UploadedAt?.ToLocalTime());
            string path;
            string message;
            if (NewestFile.LocalCopy(request.ExistingFolder, newest.Filename, target, newest.UploadedAt) is { } local)
            {
                path = local;
                message = $"Opened {Path.GetFileName(local)} from the item's folder.";
            }
            else
            {
                path = NewestFile.CachePath(paths.OpenedFilesDirectory, request.ProjectName, request.Number, request.Title, target);
                if (!NewestFile.IsCurrentCopy(path, newest.UploadedAt))
                {
                    StatusText.Text = $"Getting {newest.Filename} from Helix…";
                    await FetchAsync(newest.Url, path);
                }

                message = $"Opened {Path.GetFileName(path)}, a copy from Helix. It isn't in the item's folder: Download saves it there.";
            }

            AppLog.Info("open", $"Opening the newest file of {request.ItemType} {request.Number} ({(path.StartsWith(paths.OpenedFilesDirectory, StringComparison.OrdinalIgnoreCase) ? "copy from Helix" : "item folder")}).");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            FileOpened?.Invoke(message);
            Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            AppLog.Error("open", $"Opening the newest file of {request.ItemType} {request.Number} failed", error);
            Progress.Visibility = Visibility.Collapsed;
            StatusText.Text = error is WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 Runtime is not installed. Install it from Microsoft (search \"WebView2 Runtime\") and try again."
                : $"Its newest file couldn't be opened: {error.Message}";
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            CloseButton.Content = "Close";
        }
    }

    /// <summary>Fetches the file into the app's folder (a partial file first, so a broken fetch leaves nothing half-written).</summary>
    private async Task FetchAsync(string url, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.partial";
        try
        {
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, closing.Token))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException($"Helix returned {(int)response.StatusCode}.");
                }

                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(output, closing.Token);
            }

            File.Move(temporary, path, overwrite: true);
            NewestFile.CleanUp(paths.OpenedFilesDirectory, TimeSpan.FromDays(30), DateTime.Now);
        }
        finally
        {
            LocalFiles.TryDelete(temporary);
        }
    }

    private void ShowBrowser(bool show)
    {
        BrowserRow.Height = show ? new GridLength(1, GridUnitType.Star) : new GridLength(1);
        Browser.Visibility = show ? Visibility.Visible : Visibility.Hidden;
        if (show)
        {
            Width = Math.Max(Width, 1000);
            Height = Math.Max(Height, 700);
            StatusText.Text = "Sign in to Helix below. The file opens by itself once you are in.";
            if (!IsActive)
            {
                Activate();
            }
        }
        else
        {
            StatusText.Text = "Finding its newest file in Helix…";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
