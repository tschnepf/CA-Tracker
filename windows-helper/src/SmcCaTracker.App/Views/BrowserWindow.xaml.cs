using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

/// <summary>
/// The app's own browser for refreshing a project. It keeps its own sign-in (in
/// %LOCALAPPDATA%\SMC-CA-Tracker\browser), opens each page of the refresh request, runs the
/// read-only scan in the page and stores the result. If Helix or
/// Procore asks you to sign in, you do it here once; the refresh continues when the project opens.
/// </summary>
public partial class BrowserWindow : Window
{
    private readonly AppPaths paths;
    private readonly BrowserSettings browser;
    private readonly RefreshRequestStore refreshes;
    private readonly TrackerStore store;
    private readonly DispatcherTimer keepAlive;
    private RefreshRequest request;
    private int step;
    private bool scanning;
    private bool finished;
    private bool waitingForSignIn;
    private int redirects;
    private DateTimeOffset lastProgressAt;

    public BrowserWindow(RefreshRequest request, AppPaths paths, BrowserSettings browser, TrackerStore store)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.request = request;
        this.paths = paths;
        this.browser = browser;
        refreshes = new RefreshRequestStore(paths);
        this.store = store;
        step = Math.Min(request.CompletedSteps, request.Steps.Count - 1);

        var provider = Providers.Label(request.Provider);
        Title = $"{provider} — CA Tracker";
        HeadingText.Text = $"Syncing {(string.IsNullOrWhiteSpace(request.ProjectName) ? $"{provider} project" : request.ProjectName)}";
        SetStatus($"Opening {provider}…");

        keepAlive = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        keepAlive.Tick += (_, _) => OnTick();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => OnClosed();
    }

    private string ProviderLabel => Providers.Label(request.Provider);

    private async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(paths.BrowserDataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(null, paths.BrowserDataDirectory);
            await Browser.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Fail("The Microsoft Edge WebView2 Runtime is not installed. Install it from Microsoft (search \"WebView2 Runtime\") and try again.");
            return;
        }
        catch (Exception error)
        {
            Fail($"The built-in browser could not start: {error.Message}");
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // Saves your Helix/Procore password in this browser's profile, encrypted for your Windows user
        // (Windows DPAPI), and fills it in on their sign-in pages. Signing out (Settings →
        // Connections) keeps it.
        core.Settings.IsPasswordAutosaveEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = true;
        // A new page (e.g. a sign-in redirect in the middle of a scan) ends any scan of the old one.
        core.NavigationStarting += (_, _) => scanning = false;
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessageReceived;
        keepAlive.Start();
        NavigateToStep(step);
    }

    private void NavigateToStep(int index)
    {
        step = index;
        scanning = false;
        redirects = 0;
        OpenProjectButton.Visibility = Visibility.Collapsed;
        SetStatus(request.Steps.Count > 1
            ? $"Opening {ProviderLabel} page {index + 1} of {request.Steps.Count}…"
            : $"Opening {ProviderLabel}…");
        Browser.CoreWebView2.Navigate(request.Steps[index].Url);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (finished || scanning)
        {
            return;
        }

        if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var current))
        {
            return;
        }

        if (IsTarget(current))
        {
            await ScanAsync();
            return;
        }

        // Signed in and landed somewhere else on the site (e.g. a dashboard): go back to the project.
        if (IsProviderHost(current) && !LooksLikeSignIn(current) && redirects < 3)
        {
            redirects++;
            Browser.CoreWebView2.Navigate(request.Steps[step].Url);
            return;
        }

        ShowSignIn();
    }

    private bool IsTarget(Uri current)
    {
        var target = new Uri(request.Steps[step].Url);
        return IsProviderHost(current)
            && current.AbsolutePath.TrimEnd('/').StartsWith(target.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsProviderHost(Uri uri) => ProviderSites.IsProviderHost(request.Provider, uri);

    private static bool LooksLikeSignIn(Uri uri) => ProviderSites.LooksLikeSignIn(uri);

    private void ShowSignIn()
    {
        waitingForSignIn = true;
        SetStatus($"Sign in to {ProviderLabel} below. The sync continues by itself when the project opens — you only need to sign in once.");
        OpenProjectButton.Visibility = Visibility.Visible;
        if (!IsActive)
        {
            Activate();
        }
    }

    private async Task ScanAsync()
    {
        scanning = true;
        waitingForSignIn = false;
        OpenProjectButton.Visibility = Visibility.Collapsed;
        var claim = refreshes.Claim(request.Id, request.Provider, DateTimeOffset.Now);
        if (claim.Request is null)
        {
            Fail(claim.Error ?? "This sync can no longer run.");
            return;
        }

        request = claim.Request;
        SetStatus($"Reading {ProviderLabel}…");
        try
        {
            foreach (var library in ProviderScripts.LibrariesFor(request.Provider))
            {
                await Browser.CoreWebView2.ExecuteScriptAsync(library);
            }

            var script = string.Equals(request.Provider, Providers.Procore, StringComparison.OrdinalIgnoreCase)
                ? ProviderScripts.ProcoreScan(request.ProviderProjectId, request.AccountId, request.ProjectName, request.Steps[step].ItemTypes.FirstOrDefault() ?? ItemTypes.Rfi)
                : ProviderScripts.HelixScan;
            await Browser.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception error)
        {
            Fail($"The page could not be read: {error.Message}");
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (finished || !Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !IsProviderHost(source))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            switch (type)
            {
                case "progress":
                    lastProgressAt = DateTimeOffset.Now;
                    SetStatus(root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "");
                    break;
                case "error":
                    Fail(root.TryGetProperty("message", out var error) ? error.GetString() ?? "The page could not be read." : "The page could not be read.");
                    break;
                case "result":
                    HandleResult(root);
                    break;
            }
        }
        catch (Exception error)
        {
            Fail($"The page's answer could not be read: {error.Message}");
        }
    }

    private void HandleResult(JsonElement root)
    {
        var snapshot = root.GetProperty("snapshot").Deserialize<ProviderSnapshot>(TrackerJson.Options)
            ?? throw new InvalidDataException("The page returned no items.");
        if (!string.Equals(snapshot.Provider, request.Provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.ProjectId, request.ProviderProjectId, StringComparison.Ordinal))
        {
            Fail("The page that opened belongs to a different project.");
            return;
        }

        if (snapshot.ItemTypes.Count == 0)
        {
            Fail($"{ProviderLabel} could not read this project's RFIs and Submittals. Try again, or open the project here to check your access.");
            return;
        }

        var now = DateTimeOffset.Now;
        store.SaveSnapshot(snapshot);
        store.RecordSyncRun(new SyncRun
        {
            Provider = snapshot.Provider,
            ProjectId = snapshot.ProjectId,
            Mode = "full",
            StartedAt = snapshot.ScannedAt,
            FinishedAt = now,
            // Procore is read one item type per step: only a read of every type counts as complete.
            Status = snapshot.Complete && ItemTypes.All.All(snapshot.Covers) ? "complete" : "partial",
            ItemCount = snapshot.Items.Count
        });
        store.RecordAccount(request.Provider, snapshot.User?.FullName, snapshot.User?.Company, now);
        if (root.TryGetProperty("catalog", out var catalogElement) && catalogElement.ValueKind == JsonValueKind.Object)
        {
            store.MergeCatalog(catalogElement.Deserialize<CatalogMessage>(TrackerJson.Options), now);
        }

        var result = refreshes.CompleteStep(request.Id, step, snapshot, now);
        if (result is null)
        {
            Fail("The sync was cancelled or timed out in the app.");
            return;
        }

        request = result.Request;
        if (!result.Done)
        {
            NavigateToStep(request.CompletedSteps);
            return;
        }

        if (request.Status != RefreshStatus.Complete)
        {
            Fail(request.Error ?? "The sync did not complete.");
            return;
        }

        finished = true;
        SetStatus($"Done — {request.ItemCount} items read. You can close this window.");
        CloseButton.Content = "Close";
        if (browser.CloseWindowWhenDone)
        {
            // Not from inside the WebView2 event handler itself.
            Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void OnTick()
    {
        if (finished)
        {
            return;
        }

        // Keep the request alive while this window is open (e.g. during sign-in), and close if the
        // app cancelled it.
        var latest = refreshes.Load(request.Id);
        if (latest is null || latest.IsFinished)
        {
            finished = true;
            keepAlive.Stop();
            SetStatus(latest?.Status == RefreshStatus.Complete ? "Done." : latest?.Error ?? "The sync has ended.");
            CloseButton.Content = "Close";
            if (latest?.Status == RefreshStatus.Cancelled)
            {
                Dispatcher.BeginInvoke(new Action(Close));
            }

            return;
        }

        // Only waiting on a person (before the page opens, or while signing in) extends the deadline;
        // a scan that hangs still times out.
        // A scan that is still reporting progress (e.g. reading Helix's edit history for days in
        // court) keeps going too.
        var progressing = DateTimeOffset.Now - lastProgressAt < TimeSpan.FromSeconds(60);
        if ((latest.ClaimedAt is null || waitingForSignIn || progressing) && DateTimeOffset.Now > latest.ExpiresAt.AddSeconds(-30))
        {
            refreshes.KeepAlive(request.Id, DateTimeOffset.Now);
        }
    }

    private void Fail(string message)
    {
        if (finished)
        {
            return;
        }

        finished = true;
        scanning = false;
        waitingForSignIn = false;
        refreshes.Fail(request.Id, message, DateTimeOffset.Now);
        SetStatus(message);
        CloseButton.Content = "Close";
        OpenProjectButton.Visibility = Visibility.Collapsed;
        if (!IsActive)
        {
            Activate();
        }
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void OpenProject_Click(object sender, RoutedEventArgs e) => NavigateToStep(step);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed()
    {
        keepAlive.Stop();
        if (!finished)
        {
            refreshes.Cancel(request.Id, DateTimeOffset.Now);
        }

        // Shut the browser processes down so the sign-in folder is not left locked.
        Browser.Dispose();
    }
}
