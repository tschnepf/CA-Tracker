using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

/// <summary>
/// The item panel beside the item list: opens one Helix RFI or Submittal in CA Tracker's browser
/// (already signed in). Helix has no web address for a single item, so it opens the project's RFI or
/// Submittal list and then the item in Helix's own detail viewer; you work on it there as in Helix.
/// While an item is open the panel checks it every few seconds; when Helix saves a change to whose court
/// it is in (sent on, answered, closed), CA Tracker re-reads the project's items and saves them, so the
/// list updates without a refresh. Closing the item (or opening another) does the same once more, to
/// catch changes made to other items here. The browser stays open between items, so the next opens fast.
/// If Helix sends you to sign in (or reloads the list) while an item is open, the item opens again once
/// the list is back, and a change made meanwhile is still noticed.
/// When Helix has moved a project's list to its new app (app.helixteam.com), which does have an address for
/// each item, the item opens there instead, straight away from then on. The new app shows an item's review
/// steps correctly where the old viewer doesn't (a reviewer listed twice shows both as waiting). CA Tracker
/// can't watch an item there, so it syncs the project when the item is closed.
/// </summary>
public partial class ItemPanel : UserControl
{
    private readonly DispatcherTimer watchTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    // The Helix page. Replaced with a fresh one when it stops responding or crashes, so opening an
    // item never needs CA Tracker to be restarted.
    private WebView2 Browser = new();
    private bool pageDead;
    private int openAttempt;
    private AppPaths? paths;
    private TrackerStore? store;
    private string itemId = "";
    private string itemType = ItemTypes.Rfi;
    private string projectId = "";
    private bool started;
    private Task? startTask;
    private bool opening;
    private bool opened;

    // Lists Helix has moved to its new app, seen this run: "project|type" → the list's address there. Their
    // items open straight in the new app ("<list>/<item id>").
    private static readonly Dictionary<string, string> newAppLists = new(StringComparer.OrdinalIgnoreCase);

    // The item's address in the new app has been loaded (or is loading).
    private bool newAppItemRequested;

    // The item is open in the new app: when it closes, the project is synced (CA Tracker can't watch it there).
    private bool newAppItem;

    // Watching the item for changes made here.
    private string? courtBaseline;
    private DateTime? checkStartedAt;
    private DateTime? updateStartedAt;
    private TaskCompletionSource? updateDone;

    // An update was cut off by the page loading something else; it runs again once the item is open.
    private bool updatePending;

    public ItemPanel()
    {
        InitializeComponent();
        BrowserHost.Child = Browser;
        watchTimer.Tick += async (_, _) =>
        {
            // An error escaping a timer's handler would close the app: log it, check again next tick.
            try
            {
                await CheckCourtAsync();
            }
            catch (Exception error)
            {
                AppLog.Error("panel", "Checking the open item failed", error);
            }
        };
    }

    /// <summary>Raised when the Close button has finished (the main window then hides the panel).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised with the Helix project's id when an item that was open in Helix's new app is closed: CA Tracker
    /// can't see what was done there, so the project is synced.
    /// </summary>
    public event Action<string>? ProjectSyncRequested;

    /// <summary>The item shown, as "type|id" (empty when none).</summary>
    public string CurrentItemKey => opened || opening ? $"{itemType}|{itemId}" : "";

    /// <summary>Shows an item: finishes with the one before (checking it once more), then opens this one.</summary>
    public async Task OpenAsync(AppPaths appPaths, TrackerStore trackerStore, string id, string type, string helixProjectId, string heading)
    {
        var attempt = ++openAttempt;
        RetryButton.Visibility = Visibility.Collapsed;
        if (opened || updateStartedAt is not null)
        {
            SetStatus("Checking the last item's project for changes made here…");
        }

        if (!pageDead)
        {
            await FinishAsync(TimeSpan.FromSeconds(20));
        }

        if (attempt != openAttempt)
        {
            // Another item was chosen (or the panel shut down) while the last one was checked.
            return;
        }

        paths = appPaths;
        store = trackerStore;
        itemId = id;
        itemType = type;
        projectId = helixProjectId;
        opened = false;
        opening = false;
        courtBaseline = null;
        checkStartedAt = null;
        newAppShown = false;
        newAppItemRequested = false;
        newAppItem = false;
        // An update of the last item still running would keep this one from being checked.
        updateStartedAt = null;
        updateDone?.TrySetResult();
        updatePending = false;
        HeadingText.Text = heading;
        SetStatus("Opening Helix…");
        AppLog.Info("panel", $"Opening {type} {id} in project {helixProjectId}.");
        if (pageDead)
        {
            await ResetBrowserAsync("the page had stopped responding");
        }
        else if (!started)
        {
            await EnsureStartedAsync();
        }
        else if (Browser.CoreWebView2 is { } core)
        {
            GoToItem(core);
        }

        _ = WatchOpenAsync(attempt);
    }

    /// <summary>
    /// If the item hasn't opened (or said why not) within a minute, the page is replaced with a fresh
    /// one and the item opened again, once; after that the panel offers "Open the item again".
    /// </summary>
    private async Task WatchOpenAsync(int attempt, bool retried = false)
    {
        await Task.Delay(TimeSpan.FromSeconds(60));
        if (attempt != openAttempt || opened || RetryButton.Visibility == Visibility.Visible || IsSignInShown || newAppShown)
        {
            return;
        }

        if (!retried)
        {
            AppLog.Warn("panel", $"{itemType} {itemId} didn't open within a minute; starting a fresh Helix page.");
            await ResetBrowserAsync("the item didn't open within a minute");
            _ = WatchOpenAsync(attempt, retried: true);
            return;
        }

        AppLog.Warn("panel", $"{itemType} {itemId} still didn't open with a fresh page.");
        opening = false;
        SetStatus("Helix didn't open the item. Select \"Open the item again\" to try once more, or open it from the list below.");
        RetryButton.Visibility = Visibility.Visible;
    }

    private bool IsSignInShown => StatusText.Text.StartsWith("Sign in to Helix", StringComparison.Ordinal);

    // Helix showed the item's list in its new app: you find the item there, so nothing is retried.
    private bool newAppShown;

    /// <summary>Runs a script in the Helix page, giving up if the page has stopped responding.</summary>
    private async Task RunScriptAsync(string script)
    {
        var core = Browser.CoreWebView2 ?? throw new InvalidOperationException("The Helix page isn't open.");
        var run = core.ExecuteScriptAsync(script);
        if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20))) != run)
        {
            pageDead = true;
            AppLog.Warn("panel", "The Helix page stopped responding.");
            throw new TimeoutException("the Helix page stopped responding");
        }

        await run;
    }

    /// <summary>Throws away the Helix page and starts a fresh one (same sign-in), then opens the item's list.</summary>
    private async Task ResetBrowserAsync(string why)
    {
        AppLog.Info("panel", $"Starting a fresh Helix page ({why}).");
        watchTimer.Stop();
        opened = false;
        opening = false;
        updateStartedAt = null;
        checkStartedAt = null;
        updateDone?.TrySetResult();
        var old = Browser;
        Browser = new WebView2();
        BrowserHost.Child = Browser;
        try
        {
            old.Dispose();
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }

        started = false;
        startTask = null;
        pageDead = false;
        SetStatus("Opening Helix again…");
        await EnsureStartedAsync();
    }

    private string ListUrl =>
        $"https://{ProviderLinks.HelixHost}/Projects/{Uri.EscapeDataString(projectId)}/{(itemType == ItemTypes.Submittal ? "Submittals" : "RFI")}";

    private string NewAppKey => $"{projectId}|{itemType}";

    /// <summary>The item's address in Helix's new app, once Helix has moved its list there; otherwise null.</summary>
    private string? NewAppItemUrl =>
        newAppLists.TryGetValue(NewAppKey, out var list) ? $"{list}/{Uri.EscapeDataString(itemId)}" : null;

    /// <summary>Straight to the item in Helix's new app when its list has moved there; otherwise to its list.</summary>
    private void GoToItem(CoreWebView2 core)
    {
        if (NewAppItemUrl is { } url)
        {
            newAppItemRequested = true;
            core.Navigate(url);
        }
        else
        {
            core.Navigate(ListUrl);
        }
    }

    /// <summary>
    /// The address of this item's list in Helix's new app, from a page Helix sent the list to there
    /// ("https://app.helixteam.com/projects/{project}/submittals"); null for any other new-app page.
    /// </summary>
    private string? NewAppListUrl(Uri page)
    {
        var segments = page.AbsolutePath.Trim('/').Split('/');
        var word = itemType == ItemTypes.Submittal ? "submittal" : "rfi";
        return segments.Length >= 3
               && string.Equals(segments[0], "projects", StringComparison.OrdinalIgnoreCase)
               && string.Equals(Uri.UnescapeDataString(segments[1]), projectId, StringComparison.OrdinalIgnoreCase)
               && segments[2].Contains(word, StringComparison.OrdinalIgnoreCase)
            ? $"https://{page.Host}/{segments[0]}/{segments[1]}/{segments[2]}"
            : null;
    }

    /// <summary>
    /// Starts the page once: opening another item while it starts waits for the same start, so its
    /// handlers aren't added twice. Starts again if the last start failed.
    /// </summary>
    private Task EnsureStartedAsync()
    {
        if (startTask is null || startTask.IsCompleted && !started)
        {
            startTask = StartAsync();
        }

        return startTask;
    }

    private async Task StartAsync()
    {
        var browser = Browser;
        try
        {
            Directory.CreateDirectory(paths!.BrowserDataDirectory);
            var environment = await CoreWebView2Environment.CreateAsync(null, paths.BrowserDataDirectory);
            await browser.EnsureCoreWebView2Async(environment);
        }
        catch (Exception error)
        {
            if (browser != Browser)
            {
                return;
            }

            SetStatus(error is WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 Runtime is not installed. Install it from Microsoft (search \"WebView2 Runtime\") and try again."
                : $"The built-in browser could not start: {error.Message}");
            return;
        }

        if (browser != Browser)
        {
            // The page was replaced (or the panel shut down) while it started.
            return;
        }

        started = true;
        var core = browser.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        // Saves your Helix/Procore password in this browser's profile, encrypted for your Windows user
        // (Windows DPAPI), and fills it in on their sign-in pages. Signing out (Settings →
        // Connections) keeps it.
        core.Settings.IsPasswordAutosaveEnabled = true;
        core.Settings.IsGeneralAutofillEnabled = true;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += (_, e) =>
        {
            // The page crashed or hung: the next Open (or "Open the item again") starts a fresh one.
            pageDead = true;
            opening = false;
            AppLog.Warn("panel", $"The Helix page failed ({e.ProcessFailedKind}).");
            Dispatcher.InvokeAsync(() =>
            {
                // Nothing to check on a dead page (the court seen before is kept for when it opens again).
                watchTimer.Stop();
                SetStatus("The Helix page stopped working. Select \"Open the item again\" to reload it.");
                RetryButton.Visibility = Visibility.Visible;
            });
        };
        GoToItem(core);
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        opening = false;
        if (updateStartedAt is not null)
        {
            // The update runs in the page that is going away, so it won't answer.
            updateStartedAt = null;
            updateDone?.TrySetResult();
            updatePending = true;
        }

        if (opened && Uri.TryCreate(e.Uri, UriKind.Absolute, out var destination)
            && ProviderSites.ClosesHelixItem(destination, new Uri(ListUrl)))
        {
            // Opened again when the list is back (after signing in). The court seen before is kept, so a
            // change made meanwhile is still noticed.
            AppLog.Info("panel", $"{itemType} {itemId} closed as Helix loaded {destination.GetLeftPart(UriPartial.Path)}; it opens again when the list is back.");
            opened = false;
            watchTimer.Stop();
            checkStartedAt = null;
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (opened || opening || !Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var current))
        {
            return;
        }

        if (ProviderSites.IsHelixNewApp(current))
        {
            newAppShown = true;
            if (!newAppItemRequested && NewAppListUrl(current) is { } list)
            {
                // Helix sent the item's list to its new app, which has an address for each item: open it there,
                // and next time go there straight away.
                newAppLists[NewAppKey] = list;
                AppLog.Info("panel", $"Helix moved the {itemType} list of project {projectId} to its new app; opening {itemType} {itemId} there.");
            }

            if (!newAppItemRequested && NewAppItemUrl is { } itemUrl)
            {
                newAppItemRequested = true;
                Browser.CoreWebView2.Navigate(itemUrl);
                return;
            }

            if (newAppItemRequested)
            {
                newAppItem = true;
                AppLog.Info("panel", $"Opened {itemType} {itemId} in Helix's new app.");
                SetStatus("Opened in Helix's new app. Work on it here; when you close it, CA Tracker syncs the project to pick up your changes.");
                return;
            }

            // Somewhere else in the new app: find the item there.
            AppLog.Info("panel", $"Helix opened its new app for {itemType} {itemId}.");
            SetStatus($"Helix shows this project's {(itemType == ItemTypes.Submittal ? "Submittals" : "RFIs")} in its new app (below). Find the item there.");
            return;
        }

        if (!ProviderSites.IsProviderHost(Providers.Helix, current) || ProviderSites.LooksLikeSignIn(current))
        {
            // After signing in, the item's address is loaded again.
            newAppItemRequested = false;
            SetStatus("Sign in to Helix below; the item opens by itself afterwards.");
            return;
        }

        var listPath = new Uri(ListUrl).AbsolutePath;
        if (!current.AbsolutePath.StartsWith(listPath, StringComparison.OrdinalIgnoreCase))
        {
            // Signed in and landed elsewhere (e.g. the Helix home page): go to the item (or its list).
            GoToItem(Browser.CoreWebView2);
            return;
        }

        await OpenItemAsync();
    }

    private async Task OpenItemAsync()
    {
        opening = true;
        RetryButton.Visibility = Visibility.Collapsed;
        SetStatus("Opening the item…");
        try
        {
            await RunScriptAsync(ProviderScripts.HelixBridge);
            await RunScriptAsync(ProviderScripts.HelixOpenItem(itemId, itemType, projectId));
        }
        catch (Exception error)
        {
            opening = false;
            AppLog.Warn("panel", $"{itemType} {itemId} couldn't be opened: {error.Message}");
            SetStatus($"The item could not be opened: {error.Message}");
            RetryButton.Visibility = Visibility.Visible;
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
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            if (type is "opened" or "error"
                && root.TryGetProperty("itemId", out var forItem) && forItem.GetString() is { } answeredFor
                && !string.Equals(answeredFor, itemId, StringComparison.Ordinal))
            {
                // A late answer about an item opened before this one.
                return;
            }

            if (type == "opened")
            {
                opened = true;
                opening = false;
                AppLog.Info("panel", $"Opened {itemType} {itemId}.");
                SetStatus("Opened in Helix. Work on it here; when you send it on, CA Tracker updates by itself.");
                watchTimer.Start();
                _ = CheckCourtAsync();
            }
            else if (type is "court" or "court-missing" or "court-error")
            {
                _ = OnCourtCheckedAsync(type, root);
            }
            else if (type == "quick-result")
            {
                OnQuickResult(root);
            }
            else if (type == "quick-error")
            {
                var message = root.TryGetProperty("message", out var text) ? text.GetString() : null;
                FinishUpdate($"CA Tracker couldn't update from Helix ({message}). Sync the project to update it.");
            }
            else if (type == "error")
            {
                opening = false;
                var message = root.TryGetProperty("message", out var text) ? text.GetString() : null;
                AppLog.Warn("panel", $"Helix couldn't open {itemType} {itemId}: {message}");
                SetStatus($"Helix couldn't open the item: {message} The list is shown instead.");
                RetryButton.Visibility = Visibility.Visible;
            }
        }
        catch (JsonException)
        {
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.Visibility = Visibility.Collapsed;
        var attempt = ++openAttempt;
        if (pageDead || Browser.CoreWebView2 is null)
        {
            await ResetBrowserAsync("Open the item again");
            _ = WatchOpenAsync(attempt);
            return;
        }

        _ = WatchOpenAsync(attempt);
        opened = false;
        var current = Browser.CoreWebView2?.Source;
        if (current is not null && Uri.TryCreate(current, UriKind.Absolute, out var uri)
            && uri.AbsolutePath.StartsWith(new Uri(ListUrl).AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            await OpenItemAsync();
        }
        else if (Browser.CoreWebView2 is { } core)
        {
            GoToItem(core);
        }
    }

    // ---- Noticing when the item is sent on ---------------------------------------------------

    private async Task CheckCourtAsync()
    {
        var now = DateTime.UtcNow;
        if (!opened || pageDead || Browser.CoreWebView2 is null
            || checkStartedAt is { } started && now - started < TimeSpan.FromSeconds(20)
            || updateStartedAt is { } updating && now - updating < TimeSpan.FromSeconds(90))
        {
            return;
        }

        if (updatePending)
        {
            updatePending = false;
            await StartUpdateAsync(quiet: false);
            return;
        }

        checkStartedAt = now;
        try
        {
            await RunScriptAsync(ProviderScripts.HelixItemCourt(itemId, itemType, projectId));
        }
        catch (Exception)
        {
            checkStartedAt = null;
        }
    }

    private async Task OnCourtCheckedAsync(string type, JsonElement root)
    {
        checkStartedAt = null;
        if (type == "court-missing")
        {
            // Helix reloaded the page: load the library again for the next check.
            try
            {
                await RunScriptAsync(ProviderScripts.HelixBridge);
            }
            catch (Exception)
            {
            }

            return;
        }

        if (type != "court" || !root.TryGetProperty("signature", out var signatureElement))
        {
            return;
        }

        var signature = signatureElement.GetString();
        if (courtBaseline is null)
        {
            courtBaseline = signature;
        }
        else if (signature != courtBaseline)
        {
            courtBaseline = signature;
            await StartUpdateAsync(quiet: false);
        }
    }

    /// <summary>Re-reads the project's items of this type from Helix and saves them for the main list.</summary>
    private async Task StartUpdateAsync(bool quiet)
    {
        if (updateStartedAt is { } started && DateTime.UtcNow - started < TimeSpan.FromSeconds(90))
        {
            return;
        }

        updateStartedAt = DateTime.UtcNow;
        updateDone = new TaskCompletionSource();
        if (!quiet)
        {
            SetStatus("Helix saved a change to this item. Updating CA Tracker…");
        }

        try
        {
            foreach (var library in ProviderScripts.LibrariesFor(Providers.Helix))
            {
                await RunScriptAsync(library);
            }

            await RunScriptAsync(ProviderScripts.HelixQuickScan);
        }
        catch (Exception error)
        {
            FinishUpdate($"CA Tracker couldn't update from Helix ({error.Message}). Sync the project to update it.");
        }
    }

    private void OnQuickResult(JsonElement root)
    {
        ProviderSnapshot? snapshot;
        try
        {
            snapshot = root.GetProperty("snapshot").Deserialize<ProviderSnapshot>(TrackerJson.Options);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            snapshot = null;
        }

        // Only save what belongs to this item's project and covers its type (the page could have moved on).
        if (snapshot is null
            || !string.Equals(snapshot.Provider, Providers.Helix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(snapshot.ProjectId, projectId, StringComparison.Ordinal)
            || !snapshot.Covers(itemType))
        {
            FinishUpdate("The Helix page moved to another project, so CA Tracker wasn't updated. Sync the project to update it.");
            return;
        }

        try
        {
            store!.SaveSnapshot(snapshot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            FinishUpdate($"CA Tracker couldn't save the update: {error.Message}");
            return;
        }

        FinishUpdate(DescribeItem(snapshot));
    }

    /// <summary>Whose court the item is in now, for the status line.</summary>
    private string DescribeItem(ProviderSnapshot snapshot)
    {
        var item = snapshot.Items.FirstOrDefault(candidate => candidate.ItemId == itemId
            && string.Equals(candidate.ItemType, itemType, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return "CA Tracker is updated. Helix no longer lists this item.";
        }

        if (item.Closed)
        {
            return $"Now {item.Status}. CA Tracker is updated.";
        }

        var current = item.Holders.Where(holder => holder.State == "current")
            .Select(holder => holder.Name.Trim()).Where(name => name.Length > 0).ToList();
        var you = snapshot.User.FullName.Trim();
        if (you.Length > 0 && current.Contains(you, StringComparer.OrdinalIgnoreCase))
        {
            return "Still in your court. CA Tracker is updated.";
        }

        return current.Count > 0
            ? $"Out of your court, now with {string.Join(", ", current)}. CA Tracker is updated."
            : $"Out of your court ({(item.Responsible.Length > 0 ? item.Responsible : item.Status)}). CA Tracker is updated.";
    }

    private void FinishUpdate(string message)
    {
        updateStartedAt = null;
        if (itemId.Length > 0)
        {
            // Once the panel has closed its last re-read finishes behind it, with nothing to show.
            SetStatus(message);
        }

        updateDone?.TrySetResult();
    }

    /// <summary>
    /// Finishes with the item shown: stops watching it and re-reads its project once more, so changes made
    /// to other items here are picked up too, waiting up to a minute (or <paramref name="wait"/>). Also waits
    /// for a re-read still running for an item closed before (Close doesn't wait for it).
    /// </summary>
    public async Task FinishAsync(TimeSpan? wait = null)
    {
        watchTimer.Stop();
        if (newAppItem)
        {
            // Open in Helix's new app, where CA Tracker can't see what was done: sync the project instead.
            newAppItem = false;
            ProjectSyncRequested?.Invoke(projectId);
        }

        if (pageDead || Browser.CoreWebView2 is null || store is null)
        {
            return;
        }

        try
        {
            if (opened)
            {
                opened = false;
                if (updateStartedAt is null)
                {
                    await StartUpdateAsync(quiet: true);
                }
            }

            if (updateStartedAt is not null && updateDone is { } done)
            {
                await Task.WhenAny(done.Task, Task.Delay(wait ?? TimeSpan.FromSeconds(60)));
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Releases the browser without the final check (the app is closing, or the browser profile is about
    /// to be deleted). The panel can open an item again afterwards: it starts a fresh page then.
    /// </summary>
    public void Shutdown()
    {
        watchTimer.Stop();
        openAttempt++;
        opened = false;
        opening = false;
        newAppItem = false;
        started = false;
        startTask = null;
        pageDead = false;
        courtBaseline = null;
        checkStartedAt = null;
        updateStartedAt = null;
        updatePending = false;
        updateDone?.TrySetResult();
        var old = Browser;
        Browser = new WebView2();
        BrowserHost.Child = Browser;
        try
        {
            old.Dispose();
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }

        HeadingText.Text = "";
        RetryButton.Visibility = Visibility.Collapsed;
        SetStatus("");
    }

    /// <summary>With item details on, Close goes back to them (the item is closed the same way).</summary>
    public void SetCloseLabel(bool backToDetails)
    {
        CloseButton.Content = backToDetails ? "← Details" : "Close";
        CloseButton.ToolTip = backToDetails
            ? "Close the item and go back to its details (CA Tracker checks the project once more for changes you made here)"
            : "Close the item (CA Tracker checks the project once more for changes you made here)";
    }

    /// <summary>Closes the item as the Close button does.</summary>
    public Task CloseItemAsync() => CloseAsync();

    private async void Close_Click(object sender, RoutedEventArgs e) => await CloseAsync();

    private async Task CloseAsync()
    {
        // The "didn't open within a minute" check for this item no longer applies: without this it
        // reopened the item in the hidden panel a minute after it was opened.
        openAttempt++;
        // The panel closes at once. The last re-read of the project (a few seconds on a big one) carries on
        // behind it, and opening another item waits for it.
        var finishing = FinishAsync();
        // An item still opening may answer later: with no item set, that answer is ignored.
        opening = false;
        itemId = "";
        HeadingText.Text = "";
        SetStatus("");
        CloseRequested?.Invoke(this, EventArgs.Empty);
        await finishing;
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
