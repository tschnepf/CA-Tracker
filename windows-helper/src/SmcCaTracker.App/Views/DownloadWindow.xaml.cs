using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Downloads;
using SmcCaTracker.Core.Operations;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

/// <summary>
/// Downloads Helix and Procore RFIs and Submittals, one or many, into "&lt;number&gt; - &lt;name&gt;" folders. Every
/// item is a section with where it goes, its files (Helix's Details report and current attachments, Procore's
/// attachments, with their dates) and its status. The files are read one item after another in the background
/// (one hidden Helix page and one hidden Procore page for all of them), new files start checked, and nothing is
/// written until you select Download, which saves every checked file of every item in one go. Existing files
/// are never replaced: a copy gets " (2)". Uses the same built-in browser sign-ins as syncs.
/// </summary>
public partial class DownloadWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly AppPaths paths;
    private readonly bool verify;
    private readonly ObservableCollection<DownloadItemViewModel> items = new();
    private readonly CancellationTokenSource closing = new();

    // A page for each site the items are on (null when none is).
    private readonly HelixDownloadSession? helix;
    private readonly ProcoreDownloadSession? procore;

    // "Helix", "Procore" or "Helix and Procore": where the window's items are.
    private readonly string sources;
    private bool started;
    private bool downloading;
    private bool stopRequested;
    private bool downloadedOnce;

    // Folders moved for new rounds by this Download (the main window then lists the folders again).
    private bool foldersMoved;

    // Progress of the download in progress.
    private int filesDone;
    private int filesPlanned;

    /// <param name="verify">
    /// Checking the items' folders against Helix or Procore: each says whether its folder has every file the
    /// site has for it, and the missing files start checked, so Download gets exactly those.
    /// </param>
    public DownloadWindow(IReadOnlyList<DownloadRequest> requests, AppPaths paths, bool verify = false)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.paths = paths;
        this.verify = verify;
        foreach (var request in requests)
        {
            var item = new DownloadItemViewModel(request, verify);
            item.CheckedChanged += (_, _) => UpdateTotals();
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DownloadItemViewModel.State))
                {
                    UpdateTotals();
                }
            };
            items.Add(item);
        }

        ItemsList.ItemsSource = items;
        var action = verify ? "Verify the folder of" : "Download";
        if (items.Count == 1)
        {
            var only = items[0];
            only.IsExpanded = true;
            Title = $"{action} {only.Heading} — CA Tracker";
            HeadingText.Text = $"{action} {only.Heading}";
        }
        else
        {
            var heading = verify ? $"Verify the folders of {items.Count} items" : $"Download {items.Count} items";
            Title = $"{heading} — CA Tracker";
            HeadingText.Text = heading;
        }

        if (verify)
        {
            NewOnlyButton.Content = "Missing files only";
            NewOnlyButton.ToolTip = "Check only the files missing from each item's folder, as when the window opened";
        }

        if (items.Any(item => !IsProcore(item)))
        {
            helix = new HelixDownloadSession(Browser, paths, closing.Token);
            helix.SignInChanged += show => ShowSignIn(Browser, BrowserRow, "Helix", show);
        }

        if (items.Any(IsProcore))
        {
            procore = new ProcoreDownloadSession(ProcoreBrowser, paths, closing.Token);
            procore.SignInChanged += show => ShowSignIn(ProcoreBrowser, ProcoreBrowserRow, "Procore", show);
        }

        sources = helix is not null && procore is not null ? "Helix and Procore" : procore is not null ? "Procore" : "Helix";
        SetStatus($"Opening {sources}…");
        UpdateTotals();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            closing.Cancel();
            Browser.Dispose();
            ProcoreBrowser.Dispose();
        };
    }

    private static bool IsProcore(DownloadItemViewModel item) => ProviderSites.IsProcore(item.Request.Provider);

    /// <summary>The page that lists the item's files: Helix's or Procore's.</summary>
    private IItemFilesSession SessionFor(DownloadItemViewModel item) => IsProcore(item) ? procore! : helix!;

    private Task<ItemFiles> ReadFilesAsync(DownloadItemViewModel item)
    {
        var request = item.Request;
        return SessionFor(item).ReadItemAsync(request.ItemId, request.ItemType, request.ProjectId, request.AccountId, request.Host);
    }

    /// <summary>Raised after files were saved, so the main window can re-check the CA folders.</summary>
    public event EventHandler? Downloaded;

    private async Task StartAsync()
    {
        var failedToStart = false;
        foreach (var (session, isProcore) in new (IItemFilesSession? Session, bool IsProcore)[] { (helix, false), (procore, true) })
        {
            if (session is null)
            {
                continue;
            }

            try
            {
                await session.StartAsync();
                started = true;
            }
            catch (Exception error)
            {
                // Its site's items can't be read; the other site's still can.
                var why = error is WebView2RuntimeNotFoundException
                    ? "The Microsoft Edge WebView2 Runtime is not installed. Install it from Microsoft (search \"WebView2 Runtime\") and try again."
                    : $"The built-in browser could not start: {error.Message}";
                SetStatus(why);
                failedToStart = true;
                foreach (var item in items.Where(item => IsProcore(item) == isProcore))
                {
                    item.Fail(why);
                    item.ReadDone.TrySetResult();
                }
            }
        }

        if (!started)
        {
            return;
        }

        if (!failedToStart)
        {
            SetStatus(Instructions);
        }

        // Items of the same project and type together, so the Helix page already open serves the next one.
        foreach (var item in items.OrderBy(item => item.Request.ProjectId, StringComparer.Ordinal)
                     .ThenBy(item => item.Request.ItemType, StringComparer.Ordinal).ToList())
        {
            if (closing.IsCancellationRequested)
            {
                return;
            }

            if (item.State == DownloadItemState.Waiting)
            {
                await ReadItemAsync(item);
            }
        }

        ShowVerifyResult();
    }

    /// <summary>Verifying, once every item is read: how many folders have every file, and what is missing.</summary>
    private void ShowVerifyResult()
    {
        if (!verify || downloading || downloadedOnce || closing.IsCancellationRequested
            || items.Any(item => item.State is DownloadItemState.Waiting or DownloadItemState.Reading))
        {
            return;
        }

        var unread = items.Count(item => item.State == DownloadItemState.Failed);
        var read = items.Where(item => item.State != DownloadItemState.Failed).ToList();
        var complete = read.Count(item => item.IsComplete);
        var missingFiles = read.Sum(item => item.MissingCount);
        string result;
        if (items.Count == 1)
        {
            var only = items[0];
            result = only.State == DownloadItemState.Failed ? $"Its files couldn't be read from {sources} (see above)."
                : only.IsComplete ? $"The folder has every file {sources} has for this item."
                : only.Destination is not null && !Directory.Exists(only.Destination) ? "The item has no folder yet. Its files are checked: select Download to get them."
                : $"{Files(only.MissingCount)} {(only.MissingCount == 1 ? "is" : "are")} missing from the folder. {(only.MissingCount == 1 ? "It is" : "They are")} checked: select Download to get {(only.MissingCount == 1 ? "it" : "them")}.";
        }
        else
        {
            result = complete == read.Count
                ? read.Count == 1 ? $"The folder has every file {sources} has." : $"All {Folders(read.Count)} have every file {sources} has."
                : $"{complete} of {Folders(read.Count)} {(complete == 1 ? "has" : "have")} every file. {Files(missingFiles)} {(missingFiles == 1 ? "is" : "are")} missing from the rest and checked: select Download to get them.";
            if (unread > 0)
            {
                result += $" {unread} {(unread == 1 ? "item" : "items")} couldn't be read from {sources}.";
            }
        }

        SetStatus(result);
        if (complete == read.Count && unread == 0)
        {
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SuccessBrush");
        }
        else
        {
            StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarningBrush");
        }
    }

    private static string Folders(int count) => count == 1 ? "1 folder" : $"{count} folders";

    private async Task ReadItemAsync(DownloadItemViewModel item)
    {
        item.BeginReading();
        try
        {
            item.Load(await ReadFilesAsync(item));
            if (items.Count == 1)
            {
                // On its own the item stays open, whatever its files.
                item.IsExpanded = true;
            }

            if (item.Destination is null)
            {
                item.Message = "Choose where to save it with Change (this project has no CA folder).";
                item.IsExpanded = true;
            }
        }
        catch (OperationCanceledException)
        {
            item.Fail("Stopped.");
        }
        catch (Exception error)
        {
            item.Fail($"Couldn't read its files from {item.Request.ProviderLabel}: {error.Message}");
        }
        finally
        {
            item.ReadDone.TrySetResult();
        }
    }

    // ---- Totals and buttons -------------------------------------------------------------------

    private void UpdateTotals()
    {
        var reading = items.Count(item => item.State is DownloadItemState.Waiting or DownloadItemState.Reading);
        var ready = items.Count - reading;
        ReadingText.Text = reading == 0 ? "" : items.Count == 1 ? $"Reading from {sources}…" : $"{ready} of {items.Count} read · reading from {sources}…";

        var checkedFiles = items.Sum(item => item.CheckedCount);
        var checkedItems = items.Count(item => item.CheckedCount > 0);
        CheckedText.Text = checkedFiles == 0
            ? "Nothing checked"
            : items.Count == 1
                ? $"{Files(checkedFiles)} checked"
                : $"{Files(checkedFiles)} checked in {checkedItems} {(checkedItems == 1 ? "item" : "items")}";

        if (!downloading)
        {
            DownloadButton.Content = checkedFiles > 0 ? $"_Download {Files(checkedFiles)}" : "_Download";
            // Items still being read are downloaded with their new files when their turn comes.
            DownloadButton.IsEnabled = started && (checkedFiles > 0 || reading > 0);
        }
    }

    private static string Files(int count) => count == 1 ? "1 file" : $"{count} files";

    private void SetRunning(bool running)
    {
        downloading = running;
        NewOnlyButton.IsEnabled = !running;
        AllButton.IsEnabled = !running;
        NoneButton.IsEnabled = !running;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = true;
        DownloadButton.IsEnabled = !running;
        if (running)
        {
            DownloadButton.Content = "Downloading…";
            ProgressPanel.Visibility = Visibility.Visible;
        }
        else
        {
            CloseButton.Content = downloadedOnce ? "Close" : "Cancel";
            UpdateTotals();
        }
    }

    private void ShowProgress(string text)
    {
        ProgressText.Text = filesPlanned > 0 ? $"{text} · file {Math.Min(filesDone + 1, filesPlanned)} of {filesPlanned}" : text;
        Progress.Value = filesPlanned > 0 ? (double)filesDone / filesPlanned : 0;
    }

    private void NewOnly_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in items)
        {
            item.ApplyChoices(resetChecks: true);
        }
    }

    private void All_Click(object sender, RoutedEventArgs e) => SetAll(true);

    private void None_Click(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool check)
    {
        foreach (var item in items.Where(item => item.CanChangeFiles))
        {
            item.IsIncluded = check;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (started && sender is FrameworkElement { DataContext: DownloadItemViewModel { CanRetry: true } item })
        {
            await ReadItemAsync(item);
            ShowVerifyResult();
        }
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DownloadItemViewModel item })
        {
            return;
        }

        var start = item.Destination is not null && Directory.Exists(item.Destination) ? item.Destination
            : item.Request.ParentFolder is not null && Directory.Exists(item.Request.ParentFolder) ? item.Request.ParentFolder
            : null;
        var dialog = new OpenFolderDialog { Title = $"Where should \"{item.FolderName}\" go?", Multiselect = false };
        if (start is not null)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) == true)
        {
            // Picking the item's own folder uses it; picking any other folder puts a new item folder inside it.
            item.ChooseFolder(dialog.FolderName);
        }
    }

    private void OpenItemFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DownloadItemViewModel { Destination: { } folder } } && Directory.Exists(folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        stopRequested = true;
        StopButton.IsEnabled = false;
        ShowProgress("Stopping after this file…");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Downloading --------------------------------------------------------------------------

    /// <summary>
    /// Saves every checked file of every item, item by item in list order. An item still being read is
    /// waited for. A file that fails doesn't stop the rest; it stays checked, so Download tries it again.
    /// </summary>
    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (downloading || !started)
        {
            return;
        }

        stopRequested = false;
        foldersMoved = false;
        filesDone = 0;
        filesPlanned = items.Where(item => item.CanChangeFiles).Sum(item => item.CheckedCount);
        var saved = 0;
        var failed = 0;
        var needFolder = 0;
        var cancelled = false;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondary");
        SetRunning(true);
        try
        {
            if (!await MoveForNewRoundsAsync())
            {
                cancelled = true;
                return;
            }

            foreach (var item in items.ToList())
            {
                if (stopRequested || closing.IsCancellationRequested)
                {
                    break;
                }

                if (item.State is DownloadItemState.Waiting or DownloadItemState.Reading)
                {
                    // Not counted at the start: it joins the total with its new files once it is read.
                    ShowProgress($"Waiting for {item.Request.ProviderLabel} to list {item.Heading}");
                    await item.ReadDone.Task;
                    if (item.CanChangeFiles)
                    {
                        filesPlanned += item.CheckedCount;
                    }
                }

                if (!item.CanChangeFiles)
                {
                    continue;
                }

                var selected = item.Files.Where(file => file.IsChecked).ToList();
                if (selected.Count == 0)
                {
                    continue;
                }

                if (item.Destination is null)
                {
                    item.Message = "Choose where to save it with Change, then select Download again.";
                    item.IsExpanded = true;
                    filesPlanned -= selected.Count;
                    needFolder++;
                    continue;
                }

                if (item.WaitsForMove)
                {
                    // Its folder was to move out of Returned first and hasn't: nothing is saved where it isn't yet.
                    item.Message = "Its folder hasn't moved out of Returned yet. Select Download again, or uncheck moving it.";
                    item.IsExpanded = true;
                    filesPlanned -= selected.Count;
                    continue;
                }

                var (itemSaved, itemFailed) = await DownloadItemAsync(item, selected);
                saved += itemSaved;
                failed += itemFailed;
                if (itemSaved > 0)
                {
                    folders.Add(item.Destination);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            downloadedOnce = downloadedOnce || saved > 0 || failed > 0;
            SetRunning(false);
            ProgressPanel.Visibility = Visibility.Collapsed;
            if (!closing.IsCancellationRequested)
            {
                SetStatus(cancelled ? "Cancelled — nothing was moved or saved." : Summary(saved, failed, needFolder, folders.Count, stopRequested));
                if (failed > 0 || needFolder > 0)
                {
                    StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarningBrush");
                }
            }

            if (saved > 0 || foldersMoved)
            {
                // Also when the window was closed part-way, so the main window sees the new files (and moved folders).
                Downloaded?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Items back with you whose folders move before their files are saved (out of Returned, the first round's
    /// files into R0): one preview for all of them, with Cancel, then the moves. False when the preview was
    /// cancelled, so nothing is saved.
    /// </summary>
    private async Task<bool> MoveForNewRoundsAsync()
    {
        foreach (var item in items.Where(item => item.IsNewRound && item.State is DownloadItemState.Waiting or DownloadItemState.Reading).ToList())
        {
            ShowProgress($"Waiting for {item.Request.ProviderLabel} to list {item.Heading}");
            await item.ReadDone.Task;
        }

        var moving = items.Where(item => item.CanChangeFiles && item.CheckedCount > 0 && item.HasRoundMoves).ToList();
        if (moving.Count == 0)
        {
            return true;
        }

        var plans = moving.Select(item => item.RoundPlan()!).ToList();
        var plan = new OperationPlan
        {
            Title = plans[0].Title,
            Description = plans[0].Description,
            Operations = plans.SelectMany(each => each.Operations).ToList()
        };
        if (new PreviewWindow(plan, null) { Owner = this }.ShowDialog() != true)
        {
            return false;
        }

        ShowProgress("Moving folders…");
        var results = await Task.Run(() => new OperationExecutor(paths).Execute(plan));
        foldersMoved = true;
        new PreviewWindow(plan, results) { Owner = this }.ShowDialog();
        foreach (var item in moving)
        {
            item.RoundDone();
        }

        return true;
    }

    private static string Summary(int saved, int failed, int needFolder, int folderCount, bool stopped)
    {
        var parts = new List<string>();
        parts.Add(saved == 0 && failed == 0
            ? "Nothing was saved."
            : $"Saved {Files(saved)} to {folderCount} {(folderCount == 1 ? "folder" : "folders")}.");
        if (failed > 0)
        {
            parts.Add($"{Files(failed)} failed and {(failed == 1 ? "stays" : "stay")} checked: select Download to try again.");
        }

        if (needFolder > 0)
        {
            parts.Add($"{needFolder} {(needFolder == 1 ? "item needs" : "items need")} a folder (Change) first.");
        }

        if (stopped)
        {
            parts.Add("Stopped before the rest.");
        }

        return string.Join(" ", parts);
    }

    private async Task<(int Saved, int Failed)> DownloadItemAsync(DownloadItemViewModel item, List<DownloadFileViewModel> selected)
    {
        var folder = item.Destination!;
        var saved = 0;
        var failed = 0;
        string? problem = null;
        item.BeginDownload();
        try
        {
            // Download links expire (Helix's within minutes: its file store then answers 400; Procore's
            // later), and the list may have been read a while ago: fresh links right before the item's files
            // are saved.
            if (selected.Any(file => !file.IsReport))
            {
                ShowProgress($"Getting download links for {item.Heading}");
                await RefreshLinksAsync(item);
            }

            Directory.CreateDirectory(folder);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in selected.Where(file => !file.IsReport))
            {
                if (stopRequested)
                {
                    break;
                }

                ShowProgress($"Downloading {file.Name}");
                var outcome = await SaveAttachmentAsync(item, file, folder, used);
                if (outcome is null)
                {
                    // The link was refused (it expired while earlier files downloaded): fresh links, once more.
                    ShowProgress($"Getting fresh download links for {item.Heading}");
                    await RefreshLinksAsync(item);
                    outcome = await SaveAttachmentAsync(item, file, folder, used, lastTry: true);
                }

                if (outcome == true)
                {
                    saved++;
                }
                else
                {
                    failed++;
                }

                filesDone++;
            }

            if (!stopRequested && selected.FirstOrDefault(file => file.IsReport) is { } reportFile)
            {
                ShowProgress($"Creating the Details PDF for {item.Heading} in Helix");
                if (await SaveReportAsync(item, reportFile, folder, used))
                {
                    saved++;
                }
                else
                {
                    failed++;
                }

                filesDone++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            problem = $"The download stopped: {error.Message}";
        }
        finally
        {
            item.FinishDownload(saved, failed, problem);
        }

        return (saved, failed);
    }

    private async Task RefreshLinksAsync(DownloadItemViewModel item) =>
        item.RefreshLinks(await ReadFilesAsync(item));

    /// <summary>
    /// Saves one attachment. True when saved, false when it failed (noted on the file), or null when its
    /// link was refused (400/401/403: it expired) and it can be tried again with a fresh link.
    /// </summary>
    private async Task<bool?> SaveAttachmentAsync(DownloadItemViewModel item, DownloadFileViewModel file, string folder, HashSet<string> used, bool lastTry = false)
    {
        if (file.Url is null)
        {
            return Fail(item, file, folder, $"{item.Request.ProviderLabel} didn't provide a download link. Try again.");
        }

        var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.partial");
        try
        {
            using (var response = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, closing.Token))
            {
                var status = (int)response.StatusCode;
                if (!lastTry && status is 400 or 401 or 403)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Fail(item, file, folder, status is 400 or 401 or 403
                        ? $"{item.Request.ProviderLabel} refused the download link ({status}) even after getting a fresh one."
                        : $"{item.Request.ProviderLabel} returned {status}.");
                }

                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(output, closing.Token);
            }

            // Named once saved, so a refused try doesn't use up the name.
            var name = DownloadNaming.UniqueFileName(folder, file.TargetName, used);
            File.Move(temporary, Path.Combine(folder, name), overwrite: false);
            return Saved(item, file, folder, name);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            LocalFiles.TryDelete(temporary);
            if (closing.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            return Fail(item, file, folder, error.Message);
        }
    }

    private async Task<bool> SaveReportAsync(DownloadItemViewModel item, DownloadFileViewModel file, string folder, HashSet<string> used)
    {
        if (item.Report is not { } report)
        {
            return Fail(item, file, folder, "Helix has no Details report for this item.");
        }

        try
        {
            byte[] bytes;
            // Helix draws the Details PDF on its report page, and a hidden page may not draw: the page is
            // shown in its 1-pixel row meanwhile, as the download window always did before. (Only Helix
            // items have a Details report.)
            exportingReport = true;
            Browser.Visibility = Visibility.Visible;
            try
            {
                bytes = await helix!.ExportReportAsync(report);
            }
            finally
            {
                exportingReport = false;
                if (!signIns.ContainsKey(Browser))
                {
                    Browser.Visibility = Visibility.Hidden;
                }
            }

            var name = DownloadNaming.UniqueFileName(folder, file.TargetName, used);
            var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.partial");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, closing.Token);
                File.Move(temporary, Path.Combine(folder, name), overwrite: false);
            }
            catch
            {
                LocalFiles.TryDelete(temporary);
                throw;
            }

            return Saved(item, file, folder, name);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or FormatException or IOException or UnauthorizedAccessException)
        {
            return Fail(item, file, folder, error.Message);
        }
    }

    private bool Saved(DownloadItemViewModel item, DownloadFileViewModel file, string folder, string name)
    {
        file.StatusText = name == file.TargetName ? "Saved" : $"Saved as {name}";
        file.IsChecked = false;
        DownloadJournal.Record(paths, item.FolderName, Path.Combine(folder, name), "Done", $"Downloaded {file.Name}");
        return true;
    }

    private bool Fail(DownloadItemViewModel item, DownloadFileViewModel file, string folder, string message)
    {
        file.StatusText = $"Failed: {message}";
        DownloadJournal.Record(paths, item.FolderName, Path.Combine(folder, file.TargetName), "Failed", message);
        return false;
    }

    // ---- The Helix and Procore pages ----------------------------------------------------------

    private string Instructions =>
        verify
            ? $"Each folder is compared with the files {sources} has for its item. Files missing from it are checked; select Download to get them. Nothing is saved until then."
            : items.Count == 1
                ? "Check the files to download and where they go, then select Download. Nothing is saved until then."
                : "New files are checked; files already in an item's folder aren't. Check what you want, then select Download. Nothing is saved until then.";

    // The pages showing their site's sign-in, with the site's name.
    private readonly Dictionary<WebView2, string> signIns = new();
    private bool exportingReport;

    /// <summary>
    /// Shows a site's page (Helix's or Procore's) in its row while the site asks you to sign in; otherwise it
    /// stays hidden in its 1-pixel row (Helix's is visible there only while Helix draws a Details PDF).
    /// </summary>
    private void ShowSignIn(WebView2 page, System.Windows.Controls.RowDefinition row, string site, bool show)
    {
        var changed = show ? signIns.TryAdd(page, site) : signIns.Remove(page);
        row.Height = show ? new GridLength(3, GridUnitType.Star) : new GridLength(1);
        page.Visibility = show || (page == Browser && exportingReport) ? Visibility.Visible : Visibility.Hidden;
        if (!changed)
        {
            return;
        }

        if (signIns.Count > 0)
        {
            SetStatus($"Sign in to {string.Join(" and ", signIns.Values)} below. Reading the items carries on by itself once you are in.");
            if (show && !IsActive)
            {
                Activate();
            }
        }
        else if (!downloading)
        {
            SetStatus(Instructions);
        }
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
