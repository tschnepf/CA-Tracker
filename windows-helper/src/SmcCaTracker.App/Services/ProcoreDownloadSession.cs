using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Services;

/// <summary>
/// The Download window's Procore page, one browser for every Procore item in the window. It asks Procore's REST
/// service, through your Procore sign-in in the built-in browser (the one the background sync uses), for an
/// item's files and their download links. The page stays on a small Procore data address of the item's server
/// (/rest/v1.0/me). That address answers 401 rather than sending you to sign in, so on a 401 the page opens
/// Procore's home page, which does: <see cref="SignInChanged"/> tells the window to show it, and the request
/// carries on by itself once you are in. Read-only in Procore: it only reads, and never clicks or sends
/// anything there. Use from the screen's thread.
/// </summary>
public sealed class ProcoreDownloadSession : IItemFilesSession
{
    private const string DataPath = "/rest/v1.0/me";

    private readonly WebView2 browser;
    private readonly AppPaths paths;
    private readonly CancellationToken closing;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> pending = new(StringComparer.Ordinal);

    // The Procore server the page is being opened on, and what completes once it is there past any sign-in.
    private string? wantedHost;
    private TaskCompletionSource? arrived;
    private int redirects;
    private bool signInShown;

    // Counts page loads, so a sign-in check started for one page doesn't act after another has loaded.
    private int pageLoads;

    public ProcoreDownloadSession(WebView2 browser, AppPaths paths, CancellationToken closing)
    {
        this.browser = browser;
        this.paths = paths;
        this.closing = closing;
    }

    /// <summary>True when Procore shows its sign-in (show the page), false once it is past it.</summary>
    public event Action<bool>? SignInChanged;

    private CoreWebView2 Core => browser.CoreWebView2 ?? throw new InvalidOperationException("The built-in browser isn't open.");

    /// <summary>A page's host and path for the log (no query: it can hold tokens).</summary>
    private static string Where(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" ? $"{uri.Host}{uri.AbsolutePath}" : uri.Scheme;

    public async Task StartAsync()
    {
        Directory.CreateDirectory(paths.BrowserDataDirectory);
        var environment = await PageWaits.WithTimeout(
            CoreWebView2Environment.CreateAsync(null, paths.BrowserDataDirectory), PageWaits.Start, "creating the browser", closing);
        await browser.EnsureCoreWebView2Async(environment);
        var core = Core;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessageReceived;
        // Every Procore page this browser opens gets the read-only library.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(ProviderScripts.ProcoreSyncLibrary);
    }

    /// <summary>Lists an item's attachments (with fresh download links) from the item's Procore server, in its company.</summary>
    public async Task<ItemFiles> ReadItemAsync(string itemId, string itemType, string projectId, string? accountId, string? host)
    {
        await gate.WaitAsync(closing);
        try
        {
            // While Procore has asked CA Tracker to slow down (background sync or an earlier download), ask nothing.
            if (ProcoreThrottle.Shared.WaitingUntil(DateTimeOffset.Now) is { } waitUntil)
            {
                throw new InvalidOperationException(ProcoreThrottle.WaitText(waitUntil));
            }

            var server = Server(host);
            if (!IsOpen(server))
            {
                await OpenAsync(server, DataUrl(server));
            }

            var answer = await AskAsync(itemId, itemType, projectId, accountId);
            if (Status(answer) is 401 or 406)
            {
                // Signed out: Procore's home page asks you to sign in; then back to the data address and ask again.
                AppLog.Info("download", $"Procore answered {Status(answer)} for {itemType} {itemId}; opening its home page to sign in.");
                await OpenAsync(server, $"https://{server}/");
                await OpenAsync(server, DataUrl(server));
                answer = await AskAsync(itemId, itemType, projectId, accountId);
            }

            if (ProcoreThrottle.IsSlowDown(Status(answer)))
            {
                var retryAfter = answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("retryAfter", out var seconds)
                    && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetDouble(out var wait)
                    ? wait
                    : (double?)null;
                var until = ProcoreThrottle.Shared.SlowDown(DateTimeOffset.Now, retryAfter);
                AppLog.Warn("download", $"Procore answered {Status(answer)} for {itemType} {itemId}; not asking again until {until.ToLocalTime():t}.");
                throw new InvalidOperationException(ProcoreThrottle.WaitText(until));
            }

            if (TypeOf(answer) != "procore-item-files")
            {
                throw new InvalidOperationException(Problem(answer));
            }

            return new ItemFiles(null, null, ItemFilesMessages.Attachments(answer), DateTimeOffset.Now);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The item's Procore server ("app.procore.com", "us02.procore.com"); the usual one when it isn't known.</summary>
    private static string Server(string? host) =>
        !string.IsNullOrWhiteSpace(host) && Uri.TryCreate($"https://{host.Trim()}/", UriKind.Absolute, out var uri)
            && ProviderSites.IsProviderHost(Providers.Procore, uri)
                ? uri.Host
                : ProviderLinks.DefaultProcoreHost;

    private static string DataUrl(string server) => $"https://{server}{DataPath}";

    private bool IsOpen(string server) =>
        Uri.TryCreate(browser.CoreWebView2?.Source, UriKind.Absolute, out var source)
        && string.Equals(source.Host, server, StringComparison.OrdinalIgnoreCase)
        && string.Equals(source.AbsolutePath, DataPath, StringComparison.OrdinalIgnoreCase);

    private bool OnServer(Uri uri) =>
        string.Equals(uri.Host, wantedHost, StringComparison.OrdinalIgnoreCase) && uri.Scheme == Uri.UriSchemeHttps && !ProviderSites.LooksLikeSignIn(uri);

    /// <summary>Opens a page of the server and waits until it is there (generous: includes signing in if Procore asks).</summary>
    private async Task OpenAsync(string server, string url)
    {
        wantedHost = server;
        redirects = 0;
        var waiting = arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Core.Navigate(url);
        try
        {
            await waiting.Task.WaitAsync(TimeSpan.FromMinutes(15), closing);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Procore took too long. Check that you are signed in, then try again.");
        }
    }

    private async Task<JsonElement> AskAsync(string itemId, string itemType, string projectId, string? accountId)
    {
        var answer = await RequestAsync(id => ProviderScripts.ProcoreReadItemFiles(id, projectId, accountId, itemType, itemId));
        if (TypeOf(answer) == "procore-missing")
        {
            // The page lost the library (or loaded before it was added): add it and ask again.
            await Core.ExecuteScriptAsync(ProviderScripts.ProcoreSyncLibrary);
            answer = await RequestAsync(id => ProviderScripts.ProcoreReadItemFiles(id, projectId, accountId, itemType, itemId));
        }

        return answer;
    }

    private async Task<JsonElement> RequestAsync(Func<string, string> script)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await Core.ExecuteScriptAsync(script(id));
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), closing);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Procore took too long to list the item's files.");
        }
        finally
        {
            pending.Remove(id);
        }
    }

    private static bool IsConnectionProblem(CoreWebView2WebErrorStatus status) => status is CoreWebView2WebErrorStatus.ServerUnreachable
        or CoreWebView2WebErrorStatus.Timeout or CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset
        or CoreWebView2WebErrorStatus.Disconnected or CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.HostNameNotResolved
        or CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse;

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (arrived is not { Task.IsCompleted: false } waiting || wantedHost is null || !Uri.TryCreate(Core.Source, UriKind.Absolute, out var source))
        {
            return;
        }

        var loads = ++pageLoads;
        // A data address that answers 401 isn't a success either; only a page that couldn't be reached at all is.
        if (!e.IsSuccess && IsConnectionProblem(e.WebErrorStatus))
        {
            waiting.TrySetException(new InvalidOperationException($"Procore couldn't be reached ({e.WebErrorStatus}). Check your internet connection."));
            return;
        }

        if (OnServer(source))
        {
            if (signInShown)
            {
                signInShown = false;
                SignInChanged?.Invoke(false);
            }

            waiting.TrySetResult();
            return;
        }

        if (ProviderSites.IsProviderHost(Providers.Procore, source) && !ProviderSites.LooksLikeSignIn(source))
        {
            // Signed in, but on another Procore server (Procore sends you to your own company's): back to the item's.
            AppLog.Info("download", $"The Procore page is at {Where(source)}, not {wantedHost}; opening it there.");
            if (++redirects > 3)
            {
                waiting.TrySetException(new InvalidOperationException($"Procore didn't open {wantedHost}. Check that you can see the project in Procore."));
                return;
            }

            Core.Navigate(DataUrl(wantedHost));
            return;
        }

        // Procore's sign-in, or a page on the way: show the page only if it is still off Procore a few seconds
        // later, so you are really asked to sign in.
        AppLog.Info("download", $"The Procore page is at {Where(source)}; waiting to see whether it asks you to sign in.");
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (loads == pageLoads && arrived == waiting && !waiting.Task.IsCompleted && !signInShown
            && Uri.TryCreate(browser.CoreWebView2?.Source, UriKind.Absolute, out var still) && !OnServer(still))
        {
            AppLog.Info("download", $"Showing the Procore page: it is still at {Where(still)} (sign-in).");
            signInShown = true;
            SignInChanged?.Invoke(true);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !ProviderSites.IsProviderHost(Providers.Procore, source))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (ItemFilesMessages.Text(root, "requestId") is { } id && pending.TryGetValue(id, out var completion))
            {
                completion.TrySetResult(root.Clone());
            }
        }
        catch (JsonException)
        {
        }
    }

    private static string? TypeOf(JsonElement answer) => ItemFilesMessages.Text(answer, "type");

    private static int Status(JsonElement answer) =>
        answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("status", out var status) && status.TryGetInt32(out var code) ? code : 0;

    /// <summary>Why Procore didn't list the files, in words.</summary>
    private static string Problem(JsonElement answer) => Status(answer) switch
    {
        401 or 406 => "Procore still asks you to sign in. Sign in from Settings → Connections → Sign in to Procore, then select Try again.",
        403 => "You don't have access to this item in Procore.",
        404 => "Procore couldn't find this item. It may have been deleted; sync the project to update the list.",
        _ => ItemFilesMessages.Text(answer, "message") ?? "Procore didn't list the item's files."
    };
}
