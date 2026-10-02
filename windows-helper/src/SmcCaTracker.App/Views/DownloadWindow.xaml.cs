using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using SmcCaTracker.App.Services;
using SmcCaTracker.App.ViewModels;
using SmcCaTracker.Core.Downloads;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

/// <summary>What to download and where it would go by default.</summary>
/// <param name="ExistingFolder">The item's matched folder, if it already has one.</param>
/// <param name="ParentFolder">Where a new "&lt;number&gt; - &lt;name&gt;" folder goes (the project's RFI or Submittal folder).</param>
public sealed record DownloadRequest(
    string ItemType,
    string ItemId,
    string ProjectId,
    string Number,
    string Title,
    string ProjectName,
    string? ExistingFolder,
    string? ParentFolder);

/// <summary>One file offered for download.</summary>
public sealed class DownloadFileViewModel : ObservableObject
{
    private bool isChecked = true;
    private bool canChange = true;
    private string statusText = "";
    private string targetName;

    public DownloadFileViewModel(string key, string name, DateTimeOffset? uploadedAt, string targetName, bool isReport)
    {
        Key = key;
        Name = name;
        UploadedAt = uploadedAt;
        this.targetName = targetName;
        IsReport = isReport;
    }

    /// <summary>"report" or the Helix attachment id.</summary>
    public string Key { get; }
    public string Name { get; }
    public DateTimeOffset? UploadedAt { get; }
    public bool IsReport { get; }
    public string UploadedText => IsReport ? "Created by Helix now" : UploadedAt?.ToLocalTime().ToString("MMM d, yyyy h:mm tt") ?? "";

    /// <summary>Short-lived download link from Helix (attachments only).</summary>
    public string? Url { get; set; }

    public string TargetName
    {
        get => targetName;
        set => Set(ref targetName, value);
    }

    public bool IsChecked
    {
        get => isChecked;
        set => Set(ref isChecked, value);
    }

    public bool CanChange
    {
        get => canChange;
        set => Set(ref canChange, value);
    }

    public string StatusText
    {
        get => statusText;
        set => Set(ref statusText, value);
    }
}

/// <summary>
/// Downloads a Helix RFI or Submittal into a "&lt;number&gt; - &lt;name&gt;" folder. It lists the Details report
/// and the item's current attachments with their upload dates, and nothing is written until you
/// pick the files and select Download. Existing files are never replaced: a copy gets " (2)".
/// Uses the same built-in browser sign-in as refreshes.
/// </summary>
public partial class DownloadWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private enum Mode
    {
        Prepare,
        Report,
        Idle
    }

    private readonly DownloadRequest request;
    private readonly AppPaths paths;
    private readonly string folderName;
    private readonly int? revision;
    private bool inRevisionFolder;
    private readonly List<DownloadFileViewModel> files = new();
    private readonly CancellationTokenSource closing = new();
    private string? destination;
    private Mode mode = Mode.Idle;
    private bool scriptStarted;
    private bool ready;
    private bool busy;
    private DateTimeOffset preparedAt;
    private ReportInfo? report;
    private TaskCompletionSource<JsonElement>? pending;

    private sealed record ReportInfo(string Name, string Kind, string Filename, string Url);

    /// <summary>
    /// The project's RFI or Submittal list in Helix: its report and file services are set up for
    /// that item type there (the Overview page may not offer the Details report).
    /// </summary>
    private string ItemListUrl =>
        $"https://{ProviderLinks.HelixHost}/Projects/{Uri.EscapeDataString(request.ProjectId)}/{(request.ItemType == ItemTypes.Submittal ? "Submittals" : "RFI")}";

    private string? reportError;

    private readonly int batchIndex;
    private readonly int batchCount;

    private bool InBatch => batchCount > 1;
    private bool LastInBatch => batchIndex >= batchCount - 1;

    /// <summary>
    /// Downloading several checked items: true when the next item should open (Skip, or this one was
    /// downloaded); false when you stopped or closed the window.
    /// </summary>
    public bool ContinueBatch { get; private set; }

    public DownloadWindow(DownloadRequest request, AppPaths paths, int batchIndex = 0, int batchCount = 1)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.request = request;
        this.paths = paths;
        this.batchIndex = batchIndex;
        this.batchCount = batchCount;
        folderName = DownloadNaming.FolderName(request.Number, request.Title, request.ProjectName);
        var label = request.ItemType == ItemTypes.Submittal ? "Submittal" : "RFI";
        var position = InBatch ? $"Item {batchIndex + 1} of {batchCount} · " : "";
        Title = $"{position}Download {label} {request.Number} — CA Tracker";
        HeadingText.Text = $"{position}Download {label} {folderName}";
        if (InBatch)
        {
            CloseButton.Content = LastInBatch ? "Skip" : "Skip to next";
            StopBatchButton.Visibility = LastInBatch ? Visibility.Collapsed : Visibility.Visible;
        }
        revision = DownloadNaming.Revision(request.Title, request.Number);
        destination = request.ExistingFolder is not null
            ? WithRevision(request.ExistingFolder)
            : string.IsNullOrWhiteSpace(request.ParentFolder) ? null : WithRevision(Path.Combine(request.ParentFolder, folderName));
        ShowDestination();
        SetStatus("Opening Helix…");
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            closing.Cancel();
            pending?.TrySetCanceled();
            Browser.Dispose();
        };
    }

    /// <summary>Raised after files were saved, so the main window can re-check the CA folder.</summary>
    public event EventHandler? Downloaded;

    private async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(paths.BrowserDataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(null, paths.BrowserDataDirectory);
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (Exception error)
        {
            SetStatus(error is WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 Runtime is not installed. Install it from Microsoft (search \"WebView2 Runtime\") and try again."
                : $"The built-in browser could not start: {error.Message}");
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, _) => scriptStarted = false;
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessageReceived;
        ready = true;

        try
        {
            SetStatus("Reading the item's files from Helix…");
            var prepared = await PrepareAsync();
            BuildList(prepared);
            DownloadButton.IsEnabled = files.Count > 0;
            var reportNote = report is null && reportError is not null ? $" {reportError} The attachments can still be downloaded." : "";
            var warning = files.Count == 0 ? null : ExistingFolderWarning();
            SetStatus(files.Count == 0
                ? $"Helix has no files for this item.{reportNote}"
                : warning is not null
                    ? $"{warning}{reportNote}"
                    : $"Check the files to download, confirm where they'll be saved, then select Download. Nothing is saved until then.{reportNote}");
            if (warning is not null)
            {
                StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarningBrush");
                if (newPdfCount == 0 && InBatch)
                {
                    // Nothing new here: moving on is the likely choice.
                    CloseButton.Style = (Style)FindResource("AccentButton");
                    DownloadButton.ClearValue(StyleProperty);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            mode = Mode.Idle;
            SetStatus($"Could not read the item's files: {error.Message} Close this window and select Download again to retry.");
        }
    }

    // ---- Talking to Helix -------------------------------------------------------------------

    /// <summary>Opens the item's project in Helix and asks for its Details report and attachments.</summary>
    private async Task<JsonElement> PrepareAsync()
    {
        mode = Mode.Prepare;
        // Generous: the time includes signing in to Helix if it asks.
        var result = await WaitForPageAsync(
            ItemListUrl,
            TimeSpan.FromMinutes(15));
        preparedAt = DateTimeOffset.Now;
        return result;
    }

    private async Task<JsonElement> WaitForPageAsync(string url, TimeSpan timeout)
    {
        pending?.TrySetCanceled();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion;
        Browser.CoreWebView2.Navigate(url);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        timer.CancelAfter(timeout);
        await using (timer.Token.Register(() => completion.TrySetException(closing.IsCancellationRequested
                         ? new OperationCanceledException()
                         : (Exception)new TimeoutException("Helix took too long. Check that you are signed in, then try again."))))
        {
            return await completion.Task;
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var current) || scriptStarted || mode == Mode.Idle)
        {
            return;
        }

        if (!ProviderSites.IsProviderHost(Providers.Helix, current) || ProviderSites.LooksLikeSignIn(current))
        {
            ShowBrowser(true);
            SetStatus("Sign in to Helix below. The download carries on by itself once the project opens.");
            return;
        }

        var onProject = current.AbsolutePath.StartsWith($"/Projects/{request.ProjectId}", StringComparison.OrdinalIgnoreCase);
        var onReport = current.AbsolutePath.StartsWith("/Reports/", StringComparison.OrdinalIgnoreCase);
        try
        {
            if (mode == Mode.Prepare && onProject)
            {
                scriptStarted = true;
                ShowBrowser(false);
                await Browser.CoreWebView2.ExecuteScriptAsync(ProviderScripts.HelixBridge);
                await Browser.CoreWebView2.ExecuteScriptAsync(ProviderScripts.HelixPrepareDownload(request.ItemId, request.ItemType, request.ProjectId));
            }
            else if (mode == Mode.Report && onReport && report is not null)
            {
                scriptStarted = true;
                ShowBrowser(false);
                await Browser.CoreWebView2.ExecuteScriptAsync(ProviderScripts.HelixReportExport(report.Kind, report.Filename));
            }
            else if (mode == Mode.Report && !onReport && report is not null)
            {
                // Signed in again and landed elsewhere: go back to the report.
                Browser.CoreWebView2.Navigate(report.Url);
            }
            else if (mode == Mode.Prepare && !onProject)
            {
                // Signed in but somewhere else (e.g. the Helix home page): go to the project.
                Browser.CoreWebView2.Navigate(ItemListUrl);
            }
        }
        catch (Exception error)
        {
            pending?.TrySetException(error);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !ProviderSites.IsProviderHost(Providers.Helix, source))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement.Clone();
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            switch (type)
            {
                case "prepared" when mode == Mode.Prepare:
                case "report" when mode == Mode.Report:
                    pending?.TrySetResult(root);
                    break;
                case "error":
                case "report-error":
                    pending?.TrySetException(new InvalidOperationException(
                        root.TryGetProperty("message", out var message) ? message.GetString() : "Helix could not provide the file."));
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    // ---- The list ---------------------------------------------------------------------------

    private void BuildList(JsonElement prepared)
    {
        files.Clear();
        report = ReadReport(prepared);
        reportError = Text(prepared, "reportError");
        if (report is not null)
        {
            files.Add(new DownloadFileViewModel("report", $"{report.Name} (Details PDF)", null,
                DownloadNaming.SafeFileName(report.Filename, "Details.pdf"), isReport: true));
        }

        foreach (var attachment in ReadAttachments(prepared))
        {
            files.Add(new DownloadFileViewModel(attachment.Id, attachment.Filename, attachment.UploadedAt,
                DownloadNaming.AttachmentFileName(attachment.Filename, attachment.UploadedAt?.ToLocalTime()), isReport: false)
            {
                Url = attachment.Url
            });
        }

        FilesGrid.ItemsSource = files;
        MarkExisting(untickExisting: true);
    }

    private sealed record AttachmentInfo(string Id, string Filename, DateTimeOffset? UploadedAt, string Url);

    private static ReportInfo? ReadReport(JsonElement prepared)
    {
        if (!prepared.TryGetProperty("report", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var url = Text(element, "url");
        // Only Helix's own report pages.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, ProviderLinks.HelixHost, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/Reports/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new ReportInfo(Text(element, "name") ?? "Details", Text(element, "kind") ?? "client",
            Text(element, "filename") ?? "Details.pdf", url!);
    }

    private static IEnumerable<AttachmentInfo> ReadAttachments(JsonElement prepared)
    {
        if (!prepared.TryGetProperty("attachments", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var element in list.EnumerateArray())
        {
            var id = Text(element, "id");
            var url = Text(element, "url");
            if (id is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            DateTimeOffset? uploaded = DateTimeOffset.TryParse(Text(element, "uploadedAt"), out var parsed) ? parsed : null;
            yield return new AttachmentInfo(id, Text(element, "filename") ?? "Attachment", uploaded, url!);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Notes files already in the destination; on first load those start unchecked.</summary>
    private bool folderExisted;
    private int newPdfCount;

    /// <summary>
    /// The item already has a folder (its matched folder, or the folder files would go into). A revision's
    /// folder is its revision subfolder: a new revision starts out like a new item.
    /// </summary>
    private bool FolderAlreadyExists => inRevisionFolder
        ? Directory.Exists(destination)
        : (request.ExistingFolder is not null && Directory.Exists(request.ExistingFolder))
            || (destination is not null && Directory.Exists(destination));

    private HashSet<string> ExistingFileKeys() =>
        DownloadFolders.ExistingFileKeys(request.ExistingFolder, destination, inRevisionFolder);

    /// <summary>
    /// Marks files already in the item's folder. When the folder already exists nothing is checked by
    /// default, except PDFs from Helix that aren't in it yet (items added to the RFI/Submittal since).
    /// </summary>
    private void MarkExisting(bool untickExisting)
    {
        var keys = ExistingFileKeys();
        folderExisted = FolderAlreadyExists;
        newPdfCount = 0;
        foreach (var file in files)
        {
            var exists = keys.Contains(DownloadNaming.MatchKey(file.TargetName))
                || keys.Contains(DownloadNaming.MatchKey(file.Name))
                || (destination is not null && File.Exists(Path.Combine(destination, file.TargetName)));
            bool check;
            if (exists)
            {
                file.StatusText = "Already in folder (a copy is added if checked)";
                check = false;
            }
            else if (!folderExisted)
            {
                file.StatusText = "";
                check = true;
            }
            else if (file.IsReport)
            {
                file.StatusText = "Folder exists: check to save a fresh copy";
                check = false;
            }
            else if (file.TargetName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                file.StatusText = "New: not in the folder yet";
                newPdfCount++;
                check = true;
            }
            else
            {
                file.StatusText = "New (not a PDF): check to download";
                check = false;
            }

            if (untickExisting)
            {
                file.IsChecked = check;
            }
        }
    }

    /// <summary>The warning shown when the item already has a folder.</summary>
    private string? ExistingFolderWarning() => !folderExisted
        ? null
        : newPdfCount > 0
            ? $"⚠ This item already has a folder. {newPdfCount} PDF{(newPdfCount == 1 ? "" : "s")} in Helix {(newPdfCount == 1 ? "isn't" : "aren't")} in it yet and {(newPdfCount == 1 ? "is" : "are")} checked; everything else is left unchecked. Select Download to add {(newPdfCount == 1 ? "it" : "them")}."
            : "⚠ This item already has a folder and nothing new was found in Helix, so nothing is checked. Check files only if you want extra copies.";

    /// <summary>The folder files go into (a revision subfolder, see <see cref="DownloadFolders.Destination"/>).</summary>
    private string WithRevision(string itemFolder)
    {
        (var folder, inRevisionFolder) = DownloadFolders.Destination(itemFolder, revision);
        return folder;
    }

    private void ShowDestination()
    {
        if (destination is null)
        {
            DestinationText.Text = "Choose a folder with Change…";
            DestinationNote.Text = $"This project has no CA folder. A \"{folderName}\" folder will be created where you choose.";
            return;
        }

        DestinationText.Text = destination;
        var revisionNote = inRevisionFolder
            ? $"This is revision {revision} and the item's folder already exists, so the files go in its revision subfolder. "
            : "";
        DestinationNote.Text = revisionNote + (Directory.Exists(destination)
            ? "Existing folder. Files already there are never replaced."
            : "This folder will be created.");
    }

    // ---- Buttons ----------------------------------------------------------------------------

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        var start = destination is not null && Directory.Exists(destination) ? destination
            : request.ParentFolder is not null && Directory.Exists(request.ParentFolder) ? request.ParentFolder
            : null;
        var dialog = new OpenFolderDialog { Title = $"Where should \"{folderName}\" go?", Multiselect = false };
        if (start is not null)
        {
            dialog.InitialDirectory = start;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // Picking the item's own folder uses it; picking any other folder puts a new item folder inside it.
        var chosen = dialog.FolderName;
        var chosenName = Path.GetFileName(Path.TrimEndingDirectorySeparator(chosen));
        destination = WithRevision(string.Equals(chosenName, folderName, StringComparison.OrdinalIgnoreCase)
            || chosenName.StartsWith($"{request.Number} ", StringComparison.OrdinalIgnoreCase)
                ? chosen
                : Path.Combine(chosen, folderName));
        ShowDestination();
        MarkExisting(untickExisting: false);
    }

    private void All_Click(object sender, RoutedEventArgs e) => files.ForEach(file => file.IsChecked = true);

    private void None_Click(object sender, RoutedEventArgs e) => files.ForEach(file => file.IsChecked = false);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (destination is not null && Directory.Exists(destination))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{destination}\"") { UseShellExecute = true });
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // In a batch, Skip / Next goes on to the next checked item.
        ContinueBatch = InBatch && !LastInBatch;
        Close();
    }

    private void StopBatch_Click(object sender, RoutedEventArgs e)
    {
        ContinueBatch = false;
        Close();
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !ready)
        {
            return;
        }

        if (destination is null)
        {
            SetStatus("Choose where to save the files with Change… first.");
            return;
        }

        var selected = files.Where(file => file.IsChecked).ToList();
        if (selected.Count == 0)
        {
            SetStatus("Check at least one file.");
            return;
        }

        SetBusy(true);
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondary");
        var saved = 0;
        var failed = 0;
        try
        {
            // Helix's download links are short-lived; get fresh ones if the list is a few minutes old.
            if (selected.Any(file => !file.IsReport) && DateTimeOffset.Now - preparedAt > TimeSpan.FromMinutes(4))
            {
                SetStatus("Getting fresh download links from Helix…");
                var fresh = ReadAttachments(await PrepareAsync()).ToDictionary(attachment => attachment.Id);
                foreach (var file in files.Where(file => !file.IsReport))
                {
                    file.Url = fresh.TryGetValue(file.Key, out var attachment) ? attachment.Url : null;
                }
            }

            Directory.CreateDirectory(destination);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in selected.Where(file => !file.IsReport))
            {
                SetStatus($"Downloading {file.Name}…");
                if (await SaveAttachmentAsync(file, destination, used))
                {
                    saved++;
                }
                else
                {
                    failed++;
                }
            }

            var reportFile = selected.FirstOrDefault(file => file.IsReport);
            if (reportFile is not null)
            {
                SetStatus("Creating the Details PDF in Helix…");
                if (await SaveReportAsync(reportFile, destination, used))
                {
                    saved++;
                }
                else
                {
                    failed++;
                }
            }

            SetStatus(failed == 0
                ? $"Saved {saved} file{(saved == 1 ? "" : "s")} to {destination}."
                : $"Saved {saved}, {failed} failed (see the list). Nothing was replaced.");
            OpenFolderButton.Visibility = saved > 0 ? Visibility.Visible : Visibility.Collapsed;
            CloseButton.Content = InBatch && !LastInBatch ? "Next item" : "Close";
            if (InBatch && !LastInBatch && failed == 0 && saved > 0)
            {
                // All saved: go on to the next checked item by itself.
                SetStatus($"Saved {saved} file{(saved == 1 ? "" : "s")} to {destination}. Opening the next item…");
                ContinueBatch = true;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(1200);
                    if (IsLoaded)
                    {
                        Close();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            SetStatus($"The download stopped: {error.Message}");
        }
        finally
        {
            mode = Mode.Idle;
            ShowBrowser(false);
            SetBusy(false);
            ShowDestination();
            if (saved > 0)
            {
                // Also when the window was closed part-way, so the main window sees the new files.
                Downloaded?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private async Task<bool> SaveAttachmentAsync(DownloadFileViewModel file, string folder, HashSet<string> used)
    {
        if (file.Url is null)
        {
            return Fail(file, folder, "Helix didn't provide a download link. Try again.");
        }

        var name = DownloadNaming.UniqueFileName(folder, file.TargetName, used);
        var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.partial");
        try
        {
            using (var response = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, closing.Token))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return Fail(file, folder, $"Helix returned {(int)response.StatusCode}.");
                }

                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(output, closing.Token);
            }

            File.Move(temporary, Path.Combine(folder, name), overwrite: false);
            return Saved(file, folder, name);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            LocalFiles.TryDelete(temporary);
            if (closing.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            return Fail(file, folder, error.Message);
        }
    }

    private async Task<bool> SaveReportAsync(DownloadFileViewModel file, string folder, HashSet<string> used)
    {
        if (report is null)
        {
            return Fail(file, folder, "Helix has no Details report for this item.");
        }

        try
        {
            mode = Mode.Report;
            var result = await WaitForPageAsync(report.Url, TimeSpan.FromMinutes(10));
            var dataUrl = Text(result, "dataUrl") ?? "";
            var comma = dataUrl.IndexOf(',');
            if (comma < 0 || !dataUrl[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(file, folder, "Helix returned an empty report.");
            }

            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            if (bytes.Length == 0)
            {
                return Fail(file, folder, "Helix returned an empty report.");
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

            return Saved(file, folder, name);
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or FormatException or IOException or UnauthorizedAccessException)
        {
            return Fail(file, folder, error.Message);
        }
    }

    private bool Saved(DownloadFileViewModel file, string folder, string name)
    {
        file.StatusText = name == file.TargetName ? "Saved" : $"Saved as {name}";
        file.IsChecked = false;
        DownloadJournal.Record(paths, folderName, Path.Combine(folder, name), "Done", $"Downloaded {file.Name}");
        return true;
    }

    private bool Fail(DownloadFileViewModel file, string folder, string message)
    {
        file.StatusText = $"Failed: {message}";
        DownloadJournal.Record(paths, folderName, Path.Combine(folder, file.TargetName), "Failed", message);
        return false;
    }

    private void SetBusy(bool value)
    {
        busy = value;
        DownloadButton.IsEnabled = !value && files.Count > 0;
        ChangeButton.IsEnabled = !value;
        AllButton.IsEnabled = !value;
        NoneButton.IsEnabled = !value;
        foreach (var file in files)
        {
            file.CanChange = !value;
        }
    }

    private void ShowBrowser(bool show)
    {
        BrowserRow.Height = show ? new GridLength(3, GridUnitType.Star) : new GridLength(1);
        if (show && !IsActive)
        {
            Activate();
        }
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
