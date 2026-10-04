using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Services;

/// <summary>A Helix item's Details report, as offered for download.</summary>
public sealed record HelixReport(string Name, string Kind, string Filename, string Url);

/// <summary>A Helix attachment: its id, file name, upload date and short-lived download link.</summary>
public sealed record HelixAttachment(string Id, string Filename, DateTimeOffset? UploadedAt, string Url);

/// <summary>What Helix offers for one item, and when that was read (the links expire after a few minutes).</summary>
public sealed record HelixItemFiles(HelixReport? Report, string? ReportError, IReadOnlyList<HelixAttachment> Attachments, DateTimeOffset ReadAt);

/// <summary>
/// The Download window's Helix page, one browser for every item in the window. It lists an item's Details
/// report and attachments, and has Helix create the Details PDF. One request at a time; items of the same
/// project and type reuse the list page already open, so only the first of them waits for Helix to load.
/// When Helix asks you to sign in, <see cref="SignInChanged"/> tells the window to show the page; the request
/// carries on by itself once you are in. Read-only in Helix. Use from the screen's thread.
/// </summary>
public sealed class HelixDownloadSession
{
    private readonly WebView2 browser;
    private readonly AppPaths paths;
    private readonly CancellationToken closing;
    private readonly SemaphoreSlim gate = new(1, 1);
    private PageRequest? current;

    // "project|type" whose RFI or Submittal list is open with the page library loaded.
    private string? openList;

    // Counts page loads, so a sign-in check started for one page doesn't act after another has loaded.
    private int pageLoads;

    /// <summary>A page's host and path for the log (no query: it can hold tokens).</summary>
    private static string Where(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" ? $"{uri.Host}{uri.AbsolutePath}" : uri.Scheme;

    public HelixDownloadSession(WebView2 browser, AppPaths paths, CancellationToken closing)
    {
        this.browser = browser;
        this.paths = paths;
        this.closing = closing;
    }

    /// <summary>True when Helix shows its sign-in (show the page), false once it is past it.</summary>
    public event Action<bool>? SignInChanged;

    private CoreWebView2 Core => browser.CoreWebView2 ?? throw new InvalidOperationException("The built-in browser isn't open.");

    /// <summary>One page load and what to run on it, answered by a message from the page.</summary>
    private sealed class PageRequest
    {
        public required string Url { get; init; }
        public required Func<Uri, bool> IsWanted { get; init; }
        public required Func<Task> Run { get; init; }

        /// <summary>The message type that answers it: "prepared" or "report".</summary>
        public required string Expects { get; init; }

        /// <summary>The item asked about, so a late answer about another item is ignored.</summary>
        public string? ItemId { get; init; }

        public TaskCompletionSource<JsonElement> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started { get; set; }
        public int Redirects { get; set; }
    }

    public async Task StartAsync()
    {
        Directory.CreateDirectory(paths.BrowserDataDirectory);
        var environment = await PageWaits.WithTimeout(
            CoreWebView2Environment.CreateAsync(null, paths.BrowserDataDirectory), PageWaits.Start, "creating the browser", closing);
        await browser.EnsureCoreWebView2Async(environment);
        var core = Core;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, _) =>
        {
            // A new page: the library must load again, and a request waiting on the page runs again there.
            openList = null;
            if (current is { } page)
            {
                page.Started = false;
            }
        };
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessageReceived;
    }

    /// <summary>The project's RFI or Submittal list: Helix's report and file services for that item type are set up there.</summary>
    private static string ListUrl(string projectId, string itemType) =>
        $"https://{ProviderLinks.HelixHost}/Projects/{Uri.EscapeDataString(projectId)}/{(itemType == ItemTypes.Submittal ? "Submittals" : "RFI")}";

    /// <summary>Lists an item's Details report and attachments (with fresh download links).</summary>
    public async Task<HelixItemFiles> ReadItemAsync(string itemId, string itemType, string projectId)
    {
        await gate.WaitAsync(closing);
        try
        {
            var list = $"{projectId}|{itemType}";
            var script = ProviderScripts.HelixPrepareDownload(itemId, itemType, projectId);
            JsonElement answer;
            if (movedLists.Contains(list))
            {
                answer = await AskAsync(HomeList, ProviderSites.HomeUrl(Providers.Helix), IsHelixPage, script, itemId);
            }
            else
            {
                try
                {
                    answer = await AskAsync(list, ListUrl(projectId, itemType),
                        uri => uri.AbsolutePath.StartsWith($"/Projects/{projectId}", StringComparison.OrdinalIgnoreCase), script, itemId);
                }
                catch (HelixNewAppException)
                {
                    // Helix shows this list in its new app, where the page library can't work: read the items
                    // from the Helix home page instead, where the background sync reads everything.
                    AppLog.Info("download", $"Helix shows the {itemType} list of project {projectId} in its new app; reading its items from the Helix home page.");
                    movedLists.Add(list);
                    answer = await AskAsync(HomeList, ProviderSites.HomeUrl(Providers.Helix), IsHelixPage, script, itemId);
                }
            }

            return new HelixItemFiles(ReadReport(answer), Text(answer, "reportError"), ReadAttachments(answer).ToList(), DateTimeOffset.Now);
        }
        finally
        {
            gate.Release();
        }
    }

    // "project|type" lists Helix shows in its new app: their items are read from the Helix home page.
    private readonly HashSet<string> movedLists = new(StringComparer.Ordinal);
    private const string HomeList = "home";

    private static bool IsHelixPage(Uri uri) => ProviderSites.IsProviderHost(Providers.Helix, uri) && !ProviderSites.LooksLikeSignIn(uri);

    /// <summary>Asks about an item on the page given, reusing that page when it is already open.</summary>
    private async Task<JsonElement> AskAsync(string list, string url, Func<Uri, bool> isWanted, string script, string itemId)
    {
        var page = new PageRequest
        {
            Url = url,
            IsWanted = isWanted,
            Run = async () =>
            {
                await Core.ExecuteScriptAsync(ProviderScripts.HelixBridge);
                openList = list;
                await Core.ExecuteScriptAsync(script);
            },
            Expects = "prepared",
            ItemId = itemId
        };

        if (openList == list)
        {
            // The page is open already: just ask about this item.
            current = page;
            page.Started = true;
            await Core.ExecuteScriptAsync(script);
            return await AnswerAsync(page, TimeSpan.FromMinutes(2), "Helix took too long to list the item's files.");
        }

        // Generous: includes signing in to Helix if it asks.
        return await OpenAsync(page, TimeSpan.FromMinutes(15), "Helix took too long. Check that you are signed in, then try again.");
    }

    /// <summary>Helix opened its new app instead of the page asked for (for a Details PDF, that file fails).</summary>
    private sealed class HelixNewAppException : InvalidOperationException
    {
        public HelixNewAppException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Has Helix create the item's Details PDF (on its report page) and returns its bytes.</summary>
    public async Task<byte[]> ExportReportAsync(HelixReport report)
    {
        await gate.WaitAsync(closing);
        try
        {
            var page = new PageRequest
            {
                Url = report.Url,
                IsWanted = uri => uri.AbsolutePath.StartsWith("/Reports/", StringComparison.OrdinalIgnoreCase),
                Run = () => Core.ExecuteScriptAsync(ProviderScripts.HelixReportExport(report.Kind, report.Filename)),
                Expects = "report"
            };
            var answer = await OpenAsync(page, TimeSpan.FromMinutes(10), "Helix took too long to create the Details PDF.");
            var dataUrl = Text(answer, "dataUrl") ?? "";
            var comma = dataUrl.IndexOf(',');
            if (comma < 0 || !dataUrl[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Helix returned an empty report.");
            }

            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            return bytes.Length > 0 ? bytes : throw new InvalidOperationException("Helix returned an empty report.");
        }
        finally
        {
            gate.Release();
        }
    }

    private Task<JsonElement> OpenAsync(PageRequest page, TimeSpan timeout, string timeoutMessage)
    {
        current = page;
        Core.Navigate(page.Url);
        return AnswerAsync(page, timeout, timeoutMessage);
    }

    private async Task<JsonElement> AnswerAsync(PageRequest page, TimeSpan timeout, string timeoutMessage)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(closing);
        timer.CancelAfter(timeout);
        await using (timer.Token.Register(() => page.Answer.TrySetException(closing.IsCancellationRequested
                         ? new OperationCanceledException()
                         : new TimeoutException(timeoutMessage))))
        {
            try
            {
                return await page.Answer.Task;
            }
            finally
            {
                if (current == page)
                {
                    current = null;
                }
            }
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (current is not { Started: false } page || !Uri.TryCreate(Core.Source, UriKind.Absolute, out var source))
        {
            return;
        }

        var loads = ++pageLoads;
        if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled && page.IsWanted(source))
        {
            page.Answer.TrySetException(new InvalidOperationException($"Helix couldn't be reached ({e.WebErrorStatus}). Check your internet connection."));
            return;
        }

        if (ProviderSites.IsHelixNewApp(source))
        {
            // Signed in, but in Helix's new app, where the page library can't work (never a sign-in page).
            AppLog.Info("download", $"Helix opened its new app at {Where(source)}.");
            page.Answer.TrySetException(new HelixNewAppException(page.Expects == "report"
                ? "Helix shows this item's Details PDF only in its new app, where CA Tracker can't save it yet. Download it from Helix."
                : "Helix opened this project in its new app (app.helixteam.com), which CA Tracker can't read yet."));
            return;
        }

        if (!ProviderSites.IsProviderHost(Providers.Helix, source) || ProviderSites.LooksLikeSignIn(source))
        {
            // Helix can pass through another page on its way (your sign-in being refreshed, for one): show the
            // page only if it is still not on Helix a few seconds later, so you are really asked to sign in.
            AppLog.Info("download", $"The Helix page is at {Where(source)}; waiting to see whether it asks you to sign in.");
            await Task.Delay(TimeSpan.FromSeconds(3));
            if (loads == pageLoads && current == page && !page.Started
                && Uri.TryCreate(browser.CoreWebView2?.Source, UriKind.Absolute, out var still)
                && !ProviderSites.IsHelixNewApp(still)
                && (!ProviderSites.IsProviderHost(Providers.Helix, still) || ProviderSites.LooksLikeSignIn(still)))
            {
                AppLog.Info("download", $"Showing the Helix page: it is still at {Where(still)} (sign-in).");
                SignInChanged?.Invoke(true);
            }

            return;
        }

        if (!page.IsWanted(source))
        {
            // Signed in and landed elsewhere (e.g. the Helix home page): go to the page wanted, a few times at most.
            AppLog.Info("download", $"The Helix page is at {Where(source)}, not the page wanted; opening {Where(new Uri(page.Url))}.");
            if (++page.Redirects > 3)
            {
                page.Answer.TrySetException(new InvalidOperationException("Helix didn't open the item's project. Check that you can see it in Helix."));
                return;
            }

            Core.Navigate(page.Url);
            return;
        }

        page.Started = true;
        SignInChanged?.Invoke(false);
        try
        {
            await page.Run();
        }
        catch (Exception error)
        {
            page.Answer.TrySetException(error);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (current is not { } page
            || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !ProviderSites.IsProviderHost(Providers.Helix, source))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = Text(root, "type");
            var about = Text(root, "itemId");
            if (page.ItemId is not null && about is not null && about != page.ItemId)
            {
                // A late answer about an item asked about before.
                return;
            }

            if (type == page.Expects)
            {
                page.Answer.TrySetResult(root.Clone());
            }
            else if (type is "error" or "report-error")
            {
                page.Answer.TrySetException(new InvalidOperationException(Text(root, "message") ?? "Helix could not provide the file."));
            }
        }
        catch (JsonException)
        {
        }
    }

    private static HelixReport? ReadReport(JsonElement prepared)
    {
        if (!prepared.TryGetProperty("report", out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Only Helix's own report pages.
        var url = Text(element, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, ProviderLinks.HelixHost, StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/Reports/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new HelixReport(Text(element, "name") ?? "Details", Text(element, "kind") ?? "client", Text(element, "filename") ?? "Details.pdf", url!);
    }

    private static IEnumerable<HelixAttachment> ReadAttachments(JsonElement prepared)
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
            yield return new HelixAttachment(id, Text(element, "filename") ?? "Attachment", uploaded, url!);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
