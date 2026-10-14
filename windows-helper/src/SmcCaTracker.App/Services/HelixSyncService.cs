using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Services;

/// <summary>
/// A Helix or Procore project to read: its id and name on that site, the tracker's name for messages,
/// and for Procore the company id and host.
/// </summary>
public sealed record ProviderSyncTarget(string ProjectId, string HelixName, string DisplayName, string? AccountId = null, string? Host = null)
{
    public string ProviderName => HelixName;
}

public sealed class ProviderSyncResult
{
    /// <summary>Helix projects read successfully.</summary>
    public IReadOnlyCollection<string> SyncedProjectIds { get; init; } = Array.Empty<string>();

    /// <summary>Helix projects that could not be read.</summary>
    public IReadOnlyCollection<string> FailedProjectIds { get; init; } = Array.Empty<string>();

    public int Done => SyncedProjectIds.Count;

    /// <summary>Items added, changed or removed across the projects read.</summary>
    public int Changed { get; init; }
    public bool Stopped { get; init; }

    /// <summary>The CA Tracker browser isn't signed in to Helix (Connect to Helix fixes it).</summary>
    public bool NeedsSignIn { get; init; }

    /// <summary>Why nothing (more) could be read, when that is the case.</summary>
    public string? Problem { get; init; }

    /// <summary>Procore only: reading in the background isn't possible; read in the browser window instead.</summary>
    public bool FallbackToWindow { get; init; }
}

/// <summary>
/// Reads Helix in the background, without opening windows. One hidden page of the CA Tracker browser
/// (same sign-in as Connect to Helix) stays open on Helix; each project is read through Helix's own
/// data service from that page. The first read of a project in a session reads all its items; later
/// reads ask only for items changed since (plus a cheap list of ids, to drop deleted items), so a sync
/// of many projects takes seconds. One sync runs at a time; others wait their turn. Use from the
/// screen's thread.
/// </summary>
public sealed class HelixSyncService : IDisposable
{
    private const string SignInMessage = "Helix needs you to sign in: Settings → Connections → Sign in to Helix.";
    private const string PageStopped = "The hidden Helix page stopped working.";

    private readonly AppPaths paths;
    private readonly TrackerStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> pending = new(StringComparer.Ordinal);
    private Window? host;
    private CoreWebView2Controller? controller;
    private CoreWebView2? core;
    private TaskCompletionSource<bool>? navigated;
    private bool catalogRead;
    private bool cancelRequested;
    private bool pageDead;

    // Cancelled by Cancel(), so every wait of the run in progress ends at once. A new one per run.
    private CancellationTokenSource stopping = new();

    // Projects whose last read on this page was saved. The page remembers what it read; if a read was
    // thrown away (stopped, timed out, not saved), the next read of that project must be a full one.
    private readonly HashSet<string> confirmed = new(StringComparer.Ordinal);
    private int generation;
    private DateTimeOffset pausedUntil = DateTimeOffset.MinValue;
    private bool disposed;

    public HelixSyncService(AppPaths paths, TrackerStore store)
    {
        this.paths = paths;
        this.store = store;
    }

    /// <summary>True while a sync is running (or waiting for one).</summary>
    public bool IsBusy => gate.CurrentCount == 0;

    /// <summary>
    /// Reads each project in turn and saves its items. <paramref name="starting"/> is told which project
    /// is next; <paramref name="stop"/> is checked between projects. <paramref name="full"/> reads every
    /// item even when only changes are due. <paramref name="run"/> names this run, so <see cref="Cancel(object)"/>
    /// can stop it and nothing else.
    /// </summary>
    public async Task<ProviderSyncResult> SyncAsync(
        IReadOnlyList<ProviderSyncTarget> targets,
        Action<int, ProviderSyncTarget>? starting = null,
        Func<bool>? stop = null,
        bool full = false,
        object? run = null)
    {
        if (targets.Count == 0 || disposed)
        {
            return new ProviderSyncResult();
        }

        // Signing out deletes the browser's files: wait until that is over rather than failing the read.
        var pause = pausedUntil - DateTimeOffset.Now;
        if (pause > TimeSpan.Zero)
        {
            AppLog.Info("helix", $"Waiting {pause.TotalSeconds:0} s for the browser to finish signing out.");
            await Task.Delay(pause);
        }

        await gate.WaitAsync();
        cancelRequested = false;
        stopping = new CancellationTokenSource();
        activeRun = run;
        try
        {
            return await SyncCoreAsync(targets, starting, stop, full);
        }
        catch (OperationCanceledException)
        {
            return new ProviderSyncResult { Stopped = true, Problem = "The Helix read was stopped." };
        }
        catch (Exception error)
        {
            // E.g. the hidden page crashed: start it afresh next time.
            AppLog.Error("helix", "Reading Helix failed", error);
            CloseBrowser();
            return Problem($"Helix could not be read: {error.Message}");
        }
        finally
        {
            activeRun = null;
            gate.Release();
        }
    }

    /// <summary>
    /// Reads a project (if this session hasn't) and returns an anonymized copy of its items as JSON, for
    /// building test cases from real projects. Null with a reason when it couldn't.
    /// </summary>
    public async Task<(string? Json, string? Problem)> ExportSampleAsync(ProviderSyncTarget target)
    {
        if (!confirmed.Contains(target.ProjectId))
        {
            var synced = await SyncAsync(new[] { target });
            if (synced.Done == 0)
            {
                return (null, synced.Problem ?? "The project couldn't be read from Helix.");
            }
        }

        await gate.WaitAsync();
        stopping = new CancellationTokenSource();
        try
        {
            var answer = await RequestAsync(id => ProviderScripts.HelixExportSample(id, target.ProjectId), TimeSpan.FromSeconds(60));
            if (TypeOf(answer) == "sync-sample" && answer!.Value.TryGetProperty("sample", out var sample))
            {
                return (JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true }), null);
            }

            return (null, answer is { } failure && failure.TryGetProperty("message", out var message)
                ? message.GetString()
                : "Helix didn't answer in time.");
        }
        catch (OperationCanceledException)
        {
            return (null, "Stopped.");
        }
        catch (PageCrashedException error)
        {
            CloseBrowser();
            return (null, error.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private object? activeRun;

    /// <summary>
    /// Stops one run (the header's Cancel) if it is the one reading now: its current project's read is
    /// abandoned. A run still waiting its turn stops through its own stop check; other runs carry on.
    /// </summary>
    public void Cancel(object run)
    {
        if (ReferenceEquals(activeRun, run))
        {
            Cancel();
        }
    }

    /// <summary>Stops the sync in progress (the current project's read is abandoned).</summary>
    public void Cancel()
    {
        cancelRequested = true;
        stopping.Cancel();
        ResolvePending(canceled: true);
        navigated?.TrySetCanceled();
    }

    private async Task<ProviderSyncResult> SyncCoreAsync(
        IReadOnlyList<ProviderSyncTarget> targets,
        Action<int, ProviderSyncTarget>? starting,
        Func<bool>? stop,
        bool full)
    {
        var started = generation;
        ProviderSyncResult? problem;
        using (AppLog.Time("helix.ready", null, 5000))
        {
            problem = await EnsureReadyAsync(started);
        }

        if (problem is not null)
        {
            AppLog.Warn("helix", problem.Problem ?? "Helix isn't ready.");
            if (problem.NeedsSignIn)
            {
                store.MarkSignInNeeded(Providers.Helix);
            }

            return problem;
        }

        // Starting the browser moves the generation on.
        started = generation;

        var synced = new List<string>();
        var failed = new List<string>();
        var changed = 0;
        var stopped = false;
        using (AppLog.Time("helix.sync-all", $"{targets.Count} projects", 60_000))
        {
            for (var index = 0; index < targets.Count; index++)
            {
                if (stop?.Invoke() == true || cancelRequested || disposed)
                {
                    stopped = true;
                    break;
                }

                if (!OnHelix())
                {
                    // Signed out part-way (the page went to a sign-in page): stop instead of waiting on each project.
                    store.MarkSignInNeeded(Providers.Helix);
                    return new ProviderSyncResult
                    {
                        SyncedProjectIds = synced,
                        FailedProjectIds = failed,
                        Changed = changed,
                        NeedsSignIn = true,
                        Problem = SignInMessage
                    };
                }

                var target = targets[index];
                starting?.Invoke(index, target);
                var outcome = await SyncOneAsync(target, full, started);
                if (outcome is null)
                {
                    failed.Add(target.ProjectId);
                }
                else
                {
                    synced.Add(target.ProjectId);
                    changed += outcome.Value;
                }
            }
        }

        return new ProviderSyncResult { SyncedProjectIds = synced, FailedProjectIds = failed, Changed = changed, Stopped = stopped };
    }

    // ---- The hidden page ----------------------------------------------------------------------

    private static ProviderSyncResult Problem(string message, bool needsSignIn = false) =>
        new() { Problem = message, NeedsSignIn = needsSignIn };

    /// <summary>Stops if the page was closed (sign-out) or the sync cancelled while waiting.</summary>
    private void ThrowIfStopped(int started)
    {
        if (started != generation || cancelRequested || disposed || core is null)
        {
            throw new OperationCanceledException();
        }
    }

    /// <summary>Opens the hidden Helix page if needed and waits until Helix has loaded you. Null when ready.</summary>
    private async Task<ProviderSyncResult?> EnsureReadyAsync(int started)
    {
        if (pageDead)
        {
            CloseBrowser();
        }

        if (core is null)
        {
            var problem = await StartBrowserAsync();
            if (problem is not null)
            {
                return problem;
            }

            started = generation;
        }

        if (!OnHelix())
        {
            var waiting = navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core!.Navigate(ProviderSites.HomeUrl(Providers.Helix));
            var opened = await Task.WhenAny(waiting.Task, Task.Delay(TimeSpan.FromSeconds(60), stopping.Token));
            ThrowIfStopped(started);
            if (opened != waiting.Task)
            {
                return Problem("Helix did not open in time. Check your internet connection.");
            }

            if (!await waiting.Task && !OnHelix())
            {
                return Problem("Helix couldn't be reached. Check your internet connection.");
            }
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        // Signed out in CA Tracker (or the sign-in expired): Helix's home page shows its sign-in without
        // ever loading a user, so a page with no user after a few seconds is that, not a slow load.
        var expectSignedOut = store.Account(Providers.Helix) is null or { NeedsSignIn: true };
        var signedOutDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        string? lastSeen = null;
        while (true)
        {
            ThrowIfStopped(started);
            if (DateTime.UtcNow > deadline)
            {
                // Say what the page was showing, so a stuck page can be told from a sign-in problem.
                AppLog.Warn("helix", $"The background Helix page never became ready. Last seen: {lastSeen ?? "no answer"} (at {core?.Source}).");
                return Problem("Helix is taking too long to load. Try again in a minute; if it keeps happening, sign in again in Settings → Connections.");
            }

            if (!OnHelix())
            {
                return Problem(SignInMessage, needsSignIn: true);
            }

            var answer = await RequestAsync(ProviderScripts.HelixSyncReadiness, TimeSpan.FromSeconds(15));
            ThrowIfStopped(started);
            var type = TypeOf(answer);
            if (type == "page-crashed")
            {
                throw new PageCrashedException(PageStopped);
            }

            if (type is "sync-missing" or "page-changed")
            {
                confirmed.Clear();
                await WaitForPageAsync();
                ThrowIfStopped(started);
                await LoadLibrariesAsync();
                continue;
            }

            var state = answer is { } value && value.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
            if (answer is { } seen && seen.ValueKind == JsonValueKind.Object)
            {
                string Field(string name) => seen.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
                lastSeen = $"{state} ({Field("reason")}) on {Field("path")} \"{Field("title")}\"";
            }

            if (state == "signedOut")
            {
                AppLog.Info("helix", $"The background Helix page is signed out: {lastSeen}.");
                return Problem(SignInMessage, needsSignIn: true);
            }

            if (state == "ready")
            {
                RecordUser(answer!.Value);
                await ReadCatalogOnceAsync();
                return null;
            }

            var reason = answer is { } reasoned && reasoned.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;
            if (expectSignedOut && reason is "no-user-details" or "no-user-info" && DateTime.UtcNow > signedOutDeadline)
            {
                AppLog.Info("helix", $"No Helix user after 10 s and no saved sign-in: treating the page as signed out. Last seen: {lastSeen}.");
                return Problem(SignInMessage, needsSignIn: true);
            }

            await Task.Delay(1000, stopping.Token);
        }
    }

    private async Task<ProviderSyncResult?> StartBrowserAsync()
    {
        try
        {
            Directory.CreateDirectory(paths.BrowserDataDirectory);
            var environment = await PageWaits.WithTimeout(CoreWebView2Environment.CreateAsync(null, paths.BrowserDataDirectory), PageWaits.Start, "creating the browser");
            host = new Window
            {
                Width = 1,
                Height = 1,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Title = "CA Tracker background sync"
            };
            var handle = new WindowInteropHelper(host).EnsureHandle();
            controller = await PageWaits.WithTimeout(environment.CreateCoreWebView2ControllerAsync(handle), PageWaits.Start, "opening the page");
            controller.IsVisible = false;
            core = controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.WebMessageReceived += OnWebMessageReceived;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnProcessFailed;
            generation++;
            return null;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            CloseBrowser();
            return Problem("The Microsoft Edge WebView2 Runtime is not installed. Install it from Microsoft (search \"WebView2 Runtime\").");
        }
        catch (Exception error)
        {
            AppLog.Error("helix", "The background browser could not start", error);
            CloseBrowser();
            return Problem($"The built-in browser could not start: {error.Message}");
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // A new page: anything asked of the old one won't be answered, and the libraries must load again.
        if (navigated is null || navigated.Task.IsCompleted)
        {
            navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        ResolvePending(canceled: false, type: "page-changed");
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // A page that redirects cancels its own navigation; wait for the one it started.
        if (!e.IsSuccess && e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
        {
            return;
        }

        navigated?.TrySetResult(e.IsSuccess);
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // A browser or page process that exited, or a page that stopped responding, leaves the page
        // unusable; others (e.g. a GPU or utility process) recover by themselves.
        if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
        {
            AppLog.Info("helix", $"The hidden Helix page reported {e.ProcessFailedKind}.");
            return;
        }

        AppLog.Warn("helix", $"The hidden Helix page stopped ({e.ProcessFailedKind}).");
        pageDead = true;
        ResolvePending(canceled: false, type: "page-crashed");
        navigated?.TrySetResult(false);
    }

    /// <summary>Waits (briefly) for a page that is loading to finish.</summary>
    private async Task WaitForPageAsync()
    {
        var waiting = navigated;
        if (waiting is not null && !waiting.Task.IsCompleted)
        {
            await Task.WhenAny(waiting.Task, Task.Delay(TimeSpan.FromSeconds(60), stopping.Token));
        }
    }

    private bool OnHelix() =>
        core is not null
        && Uri.TryCreate(core.Source, UriKind.Absolute, out var current)
        && ProviderSites.IsProviderHost(Providers.Helix, current)
        && !ProviderSites.LooksLikeSignIn(current);

    private async Task LoadLibrariesAsync()
    {
        foreach (var library in ProviderScripts.HelixSyncLibraries)
        {
            if (core is null)
            {
                return;
            }

            await RunScriptAsync(library);
        }
    }

    /// <summary>
    /// Runs a script in the page. A page that doesn't take it within <see cref="PageWaits.Script"/> has
    /// stopped responding: it is marked dead and the read ends, rather than waiting on it.
    /// </summary>
    private async Task RunScriptAsync(string script)
    {
        try
        {
            await PageWaits.WithTimeout(core!.ExecuteScriptAsync(script), PageWaits.Script, "running a script", stopping.Token);
        }
        catch (TimeoutException)
        {
            AppLog.Warn("helix", "The hidden Helix page stopped responding.");
            pageDead = true;
            throw new PageCrashedException("The hidden Helix page stopped responding.");
        }
    }

    private void RecordUser(JsonElement answer)
    {
        if (answer.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            var name = user.TryGetProperty("fullName", out var fullName) && fullName.ValueKind == JsonValueKind.String ? fullName.GetString() : null;
            var company = user.TryGetProperty("company", out var companyElement) && companyElement.ValueKind == JsonValueKind.String ? companyElement.GetString() : null;
            var contactId = user.TryGetProperty("contactId", out var contactElement) && contactElement.ValueKind == JsonValueKind.String ? contactElement.GetString() : null;
            var email = user.TryGetProperty("email", out var emailElement) && emailElement.ValueKind == JsonValueKind.String ? emailElement.GetString() : null;
            store.RecordAccount(Providers.Helix, name, company, DateTimeOffset.Now, contactId, email);
        }
    }

    /// <summary>Once per session, refreshes the list of Helix projects you are assigned to.</summary>
    private async Task ReadCatalogOnceAsync()
    {
        if (catalogRead)
        {
            return;
        }

        var answer = await RequestAsync(ProviderScripts.HelixSyncCatalog, TimeSpan.FromSeconds(60));
        if (TypeOf(answer) == "sync-catalog" && answer!.Value.TryGetProperty("catalog", out var catalog))
        {
            try
            {
                store.MergeCatalog(catalog.Deserialize<CatalogMessage>(TrackerJson.Options), DateTimeOffset.Now);
                catalogRead = true;
            }
            catch (JsonException error)
            {
                AppLog.Warn("helix", $"The list of Helix projects could not be read: {error.Message}");
            }
        }
    }

    // ---- One project --------------------------------------------------------------------------

    /// <summary>Helix errors that mean "not signed in all the way yet" rather than "no".</summary>
    private static readonly System.Text.RegularExpressions.Regex StillSigningIn =
        new(@"not ready|still loading|getIdToken", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Reads one project and saves its items. Returns how many changed, or null if it failed.</summary>
    private async Task<int?> SyncOneAsync(ProviderSyncTarget target, bool full, int started)
    {
        var run = new SyncRun { Provider = Providers.Helix, ProjectId = target.ProjectId, StartedAt = DateTimeOffset.Now };
        var fullAfterMs = store.Settings.Sync.FullReadHours * 3_600_000.0;
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                JsonElement? answer;
                using (AppLog.Time("helix.sync", target.DisplayName, 20_000))
                {
                    // A full read unless the page's memory of this project matches what was saved.
                    var readAll = full || !confirmed.Contains(target.ProjectId);
                    answer = await RequestAsync(
                        id => ProviderScripts.HelixSyncProject(id, target.ProjectId, target.HelixName, readAll, fullAfterMs),
                        TimeSpan.FromMinutes(5));
                }

                ThrowIfStopped(started);
                var type = TypeOf(answer);
                if (type == "page-crashed")
                {
                    throw new PageCrashedException(PageStopped);
                }

                if (type is "sync-missing" or "page-changed")
                {
                    // Helix reloaded the page: wait for it, load the libraries again and retry (a full read).
                    confirmed.Clear();
                    await WaitForPageAsync();
                    ThrowIfStopped(started);
                    if (!OnHelix())
                    {
                        run.Error = SignInMessage;
                        store.MarkSignInNeeded(Providers.Helix);
                        break;
                    }

                    await LoadLibrariesAsync();
                    continue;
                }

                if (type == "sync-error")
                {
                    run.Error = answer!.Value.TryGetProperty("message", out var message) ? message.GetString() : "Helix returned an error.";
                    if (attempt < 5 && run.Error is not null && StillSigningIn.IsMatch(run.Error))
                    {
                        // A page that has just opened shows you before Helix has its sign-in token (its own
                        // reads fail on "getIdToken" for a second or so): wait and try again.
                        await Task.Delay(2000, stopping.Token);
                        continue;
                    }

                    break;
                }

                if (type != "sync-result")
                {
                    run.Error = answer is null ? "Helix did not answer in time." : "Helix returned something unexpected.";
                    break;
                }

                var snapshot = answer!.Value.GetProperty("snapshot").Deserialize<ProviderSnapshot>(TrackerJson.Options);
                if (snapshot is null || !string.Equals(snapshot.ProjectId, target.ProjectId, StringComparison.Ordinal) || snapshot.ItemTypes.Count == 0)
                {
                    run.Error = "Helix returned no items for this project.";
                    break;
                }

                run.Mode = answer.Value.TryGetProperty("mode", out var mode) ? mode.GetString() ?? "full" : "full";
                run.ItemCount = answer.Value.TryGetProperty("itemCount", out var count) && count.TryGetInt32(out var items) ? items : snapshot.Items.Count;
                run.ChangedCount = answer.Value.TryGetProperty("changedCount", out var changedElement) && changedElement.TryGetInt32(out var changed) ? changed : 0;
                if (run.Mode == "changes" && run.ChangedCount == 0 && store.Snapshot(Providers.Helix, target.ProjectId) is not null)
                {
                    // Nothing changed: only the read time moves on (nothing is compared or redrawn).
                    store.TouchSnapshot(Providers.Helix, target.ProjectId, snapshot.ScannedAt);
                }
                else
                {
                    store.SaveSnapshot(snapshot);
                }

                confirmed.Add(target.ProjectId);
                run.Status = "complete";
                AppLog.Info("helix", $"{target.DisplayName}: {run.Mode} read, {run.ItemCount} items, {run.ChangedCount} changed.");
                if (answer.Value.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
                {
                    // Reads that failed without failing the sync (tried again next time).
                    foreach (var warning in warnings.EnumerateArray().Where(warning => warning.ValueKind == JsonValueKind.String))
                    {
                        AppLog.Warn("helix", $"{target.DisplayName}: {warning.GetString()}");
                    }
                }
                return run.ChangedCount;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or KeyNotFoundException
                                       || (error is InvalidOperationException && error is not OperationCanceledException))
        {
            run.Error = error.Message;
        }
        catch (PageCrashedException error)
        {
            // Ends the run (the page is closed and opened afresh next time); this project failed.
            run.Error = error.Message;
            throw;
        }
        finally
        {
            run.FinishedAt = DateTimeOffset.Now;
            if (run.Status != "complete")
            {
                confirmed.Remove(target.ProjectId);
                // Stopped on purpose (Cancel) isn't a failure: the project isn't marked "last sync failed".
                run.Status = cancelRequested && run.Error is null ? "stopped" : "failed";
                run.Error ??= cancelRequested ? "Stopped." : "The read did not finish.";
                AppLog.Warn("helix", $"{target.DisplayName}: {run.Error}");
            }

            store.RecordSyncRun(run);
        }

        return null;
    }

    // ---- Talking to the page ------------------------------------------------------------------

    private static string? TypeOf(JsonElement? answer) =>
        answer is { } value && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out var type) ? type.GetString() : null;

    private static JsonElement Synthetic(string type)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { type }));
        return document.RootElement.Clone();
    }

    /// <summary>Answers every waiting request: cancelled, or with a made-up message such as "page-changed".</summary>
    private void ResolvePending(bool canceled, string type = "page-changed")
    {
        if (pending.Count == 0)
        {
            return;
        }

        var message = Synthetic(type);
        foreach (var completion in pending.Values.ToList())
        {
            if (canceled)
            {
                completion.TrySetCanceled();
            }
            else
            {
                completion.TrySetResult(message);
            }
        }
    }

    /// <summary>Runs a script that posts back one message carrying the request id; null on timeout.</summary>
    private async Task<JsonElement?> RequestAsync(Func<string, string> script, TimeSpan timeout)
    {
        if (core is null)
        {
            return null;
        }

        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            await RunScriptAsync(script(id));
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout, stopping.Token));
            stopping.Token.ThrowIfCancellationRequested();
            return finished == completion.Task ? await completion.Task : null;
        }
        finally
        {
            pending.Remove(id);
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
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("requestId", out var idElement)
                && idElement.ValueKind == JsonValueKind.String
                && idElement.GetString() is { } id
                && pending.TryGetValue(id, out var completion))
            {
                completion.TrySetResult(root.Clone());
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>
    /// Closes the hidden page (e.g. before signing out, so the browser's files can be removed). The next
    /// sync opens it again, after <paramref name="pause"/> if given.
    /// </summary>
    public void CloseBrowser(TimeSpan? pause = null)
    {
        if (pause is { } wait)
        {
            pausedUntil = DateTimeOffset.Now + wait;
        }

        generation++;
        ResolvePending(canceled: true);
        navigated?.TrySetCanceled();
        try
        {
            controller?.Close();
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }

        host?.Close();
        controller = null;
        core = null;
        host = null;
        navigated = null;
        catalogRead = false;
        pageDead = false;
        confirmed.Clear();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CloseBrowser();
    }
}
