using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Services;

/// <summary>What a look through your Procore account found.</summary>
public sealed class ProcoreDiscovery
{
    /// <summary>The projects found (null when nothing could be listed).</summary>
    public CatalogMessage? Catalog { get; init; }
    public string? UserName { get; init; }
    public int CompanyCount { get; init; }

    /// <summary>Projects left out because Procore marks them inactive or closed.</summary>
    public int SkippedInactive { get; init; }

    /// <summary>Companies whose projects couldn't be listed, with why.</summary>
    public IReadOnlyList<string> CompanyProblems { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Companies that answered "not found" on the Procore server asked: usually hosted on another
    /// Procore server (app.procore.com / us02.procore.com), where they are asked for next.
    /// </summary>
    public IReadOnlyList<(string Id, string? Name)> OtherServerCompanies { get; init; } = Array.Empty<(string, string?)>();

    /// <summary>The Procore server this listing was read from.</summary>
    public string? Host { get; init; }

    /// <summary>Adds what was found on another Procore server for this listing's missing companies.</summary>
    public ProcoreDiscovery With(ProcoreDiscovery other)
    {
        var catalog = new CatalogMessage { Provider = Providers.Procore };
        catalog.Projects.AddRange(Catalog?.Projects ?? new List<CatalogProject>());
        var known = catalog.Projects.Select(project => project.ProjectId).ToHashSet(StringComparer.Ordinal);
        catalog.Projects.AddRange((other.Catalog?.Projects ?? new List<CatalogProject>()).Where(project => known.Add(project.ProjectId)));
        var stillMissing = other.Catalog is null ? OtherServerCompanies : other.OtherServerCompanies;
        var foundNames = new HashSet<string>(StringComparer.Ordinal);
        return new ProcoreDiscovery
        {
            Catalog = catalog,
            UserName = UserName ?? other.UserName,
            CompanyCount = CompanyCount,
            SkippedInactive = SkippedInactive + other.SkippedInactive,
            // Problems other than "on another server" stay; companies still not found are added back.
            CompanyProblems = CompanyProblems
                .Where(problem => !OtherServerCompanies.Any(company => problem.StartsWith($"{company.Name ?? company.Id}:", StringComparison.Ordinal)))
                .Concat(other.CompanyProblems.Where(problem => !problem.Contains(" 404 ", StringComparison.Ordinal) && !problem.Contains(" 403 ", StringComparison.Ordinal)))
                .Concat(stillMissing.Select(company => $"{company.Name ?? company.Id}: not found on any Procore server"))
                .ToList(),
            OtherServerCompanies = stillMissing,
            FromPage = FromPage,
            Host = Host,
            Problem = Problem
        };
    }

    /// <summary>Procore wouldn't list companies, so only the projects linked on its home page were found.</summary>
    public bool FromPage { get; init; }

    public string? Problem { get; init; }
    public bool NeedsSignIn { get; init; }
    public bool Stopped { get; init; }
    public int ProjectCount => Catalog?.Projects.Count ?? 0;
}

/// <summary>
/// Reads Procore in the background through the CA Tracker browser's Procore sign-in (Settings →
/// Connections → Connect to Procore). One hidden Procore page asks Procore's own REST service — the
/// same one Procore's web pages use, with the same sign-in — for:
/// <list type="bullet">
/// <item>the companies you belong to and every project you can see in each (<see cref="DiscoverAsync"/>), so
/// projects hosted by other companies are found without switching companies or pasting links;</item>
/// <item>every RFI and Submittal of each project (<see cref="SyncAsync"/>).</item>
/// </list>
/// Nothing has to be installed in any company's Procore account; it only sees what you can see. If
/// Procore won't serve its lists to this sign-in, the result says so and the app falls back to reading
/// Procore's lists in the browser window. Read-only. Use from the screen's thread.
/// </summary>
public sealed class ProcoreSyncService : IDisposable
{
    private readonly AppPaths paths;
    private readonly TrackerStore store;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> pending = new(StringComparer.Ordinal);
    private Window? host;
    private CoreWebView2Controller? controller;
    private CoreWebView2? core;
    private TaskCompletionSource<bool>? navigated;
    private bool cancelRequested;
    private bool disposed;

    /// <summary>Until when the hidden page must not be opened again (set when signing out).</summary>
    private DateTimeOffset pausedUntil = DateTimeOffset.MinValue;

    // When each project was last read in full this session. The first read of a project in a session
    // is full; later ones ask only for items changed since, and every few hours it is read in full
    // again so items deleted in Procore drop out (as Helix does).
    private readonly Dictionary<string, DateTimeOffset> lastFullRead = new(StringComparer.Ordinal);

    public ProcoreSyncService(AppPaths paths, TrackerStore store)
    {
        this.paths = paths;
        this.store = store;
    }

    public bool IsBusy => gate.CurrentCount == 0;

    /// <summary>The page way didn't work for this sign-in (Procore refused it); the window way is used instead.</summary>
    public bool PageReadingUnavailable { get; private set; }

    public void Cancel()
    {
        cancelRequested = true;
        foreach (var completion in pending.Values.ToList())
        {
            completion.TrySetCanceled();
        }

        navigated?.TrySetCanceled();
    }

    // ---- Finding your companies and projects ---------------------------------------------------

    /// <summary>
    /// Lists every Procore company you belong to and the active projects you can see in each, through
    /// your own sign-in. Saves nothing; the caller merges the catalog.
    /// </summary>
    public async Task<ProcoreDiscovery> DiscoverAsync(string? siteHost = null)
    {
        if (disposed)
        {
            return new ProcoreDiscovery { Problem = "CA Tracker is closing." };
        }

        await gate.WaitAsync();
        cancelRequested = false;
        siteHost ??= ProviderLinks.DefaultProcoreHost;
        try
        {
            var problem = await EnsurePageAsync(siteHost);
            if (problem is not null)
            {
                AppLog.Warn("procore", $"Listing Procore projects didn't start: {problem.Problem}");
                return new ProcoreDiscovery { Problem = problem.Problem, NeedsSignIn = problem.NeedsSignIn };
            }

            JsonElement? answer;
            using (AppLog.Time("procore.discover", siteHost, 30_000))
            {
                answer = await RequestWithLibraryAsync(ProviderScripts.ProcoreDiscover, TimeSpan.FromMinutes(5));
            }

            if (cancelRequested)
            {
                throw new OperationCanceledException();
            }

            if (TypeOf(answer) != "procore-discovered")
            {
                var status = Status(answer);
                var message = Message(answer) ?? "Procore did not answer in time.";
                AppLog.Warn("procore", $"Listing Procore projects failed ({status}): {message}");
                if (status == 401)
                {
                    store.MarkSignInNeeded(Providers.Procore);
                    return new ProcoreDiscovery { Problem = "Sign in to Procore again: Settings → Connections → Connect to Procore.", NeedsSignIn = true };
                }

                return new ProcoreDiscovery { Problem = $"CA Tracker couldn't list your Procore projects: {message}" };
            }

            return ParseDiscovery(answer!.Value, siteHost);
        }
        catch (OperationCanceledException)
        {
            return new ProcoreDiscovery { Stopped = true, Problem = "Stopped looking for Procore projects." };
        }
        catch (Exception error)
        {
            AppLog.Error("procore", "Listing Procore projects failed", error);
            CloseBrowser();
            return new ProcoreDiscovery { Problem = $"Procore could not be read: {error.Message}" };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Turns a "procore-discovered" message (from the hidden page or the Connect to Procore window)
    /// into the projects found.
    /// </summary>
    public static ProcoreDiscovery ParseDiscovery(JsonElement value, string siteHost)
    {
        var catalog = new CatalogMessage { Provider = Providers.Procore };
        if (value.TryGetProperty("projects", out var projects) && projects.ValueKind == JsonValueKind.Array)
        {
            var now = DateTimeOffset.Now;
            foreach (var project in projects.EnumerateArray())
            {
                var projectId = Text(project, "projectId");
                var name = Text(project, "name");
                if (projectId is null || name is null)
                {
                    continue;
                }

                catalog.Projects.Add(new CatalogProject
                {
                    Provider = Providers.Procore,
                    ProjectId = projectId,
                    AccountId = Text(project, "accountId"),
                    AccountName = Text(project, "accountName"),
                    Name = name,
                    Host = Text(project, "host") ?? siteHost,
                    SeenAt = now
                });
            }
        }

        var companyProblems = new List<string>();
        var otherServer = new List<(string Id, string? Name)>();
        var companyCount = 0;
        if (value.TryGetProperty("companies", out var companies) && companies.ValueKind == JsonValueKind.Array)
        {
            foreach (var company in companies.EnumerateArray())
            {
                companyCount++;
                if (Text(company, "error") is { } error)
                {
                    companyProblems.Add($"{Text(company, "name") ?? Text(company, "id")}: {error}");
                    var status = company.TryGetProperty("status", out var statusElement) && statusElement.TryGetInt32(out var code) ? code : 0;
                    if (status is (403 or 404) && Text(company, "id") is { } companyId)
                    {
                        otherServer.Add((companyId, Text(company, "name")));
                    }
                }
            }
        }

        var fromPage = value.TryGetProperty("fromPage", out var fromPageElement) && fromPageElement.ValueKind == JsonValueKind.True;
        string? userName = null;
        if (value.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            userName = Text(user, "fullName");
        }

        var skipped = value.TryGetProperty("skippedInactive", out var skippedElement) && skippedElement.TryGetInt32(out var count) ? count : 0;
        AppLog.Info("procore", fromPage
            ? $"Procore wouldn't list companies ({Message(value)}); found {catalog.Projects.Count} projects linked on the home page."
            : $"Found {catalog.Projects.Count} Procore projects in {companyCount} companies ({skipped} inactive left out).");
        foreach (var companyProblem in companyProblems)
        {
            AppLog.Warn("procore", $"Projects not listed for {companyProblem}");
        }

        return new ProcoreDiscovery
        {
            Catalog = catalog,
            UserName = userName,
            CompanyCount = companyCount,
            SkippedInactive = skipped,
            CompanyProblems = companyProblems,
            OtherServerCompanies = otherServer,
            FromPage = fromPage,
            Host = siteHost
        };
    }

    /// <summary>The Procore servers (regions) a company can be hosted on.</summary>
    public static readonly IReadOnlyList<string> ProcoreServers = new[] { "app.procore.com", "us02.procore.com" };

    /// <summary>
    /// Asks Procore's other servers for the projects of companies that answered "not found" on the
    /// server already asked (each Procore company lives on one server). Read-only.
    /// </summary>
    public async Task<ProcoreDiscovery> FindOnOtherServersAsync(IReadOnlyList<(string Id, string? Name)> companies, string? askedHost)
    {
        var missing = companies.ToList();
        var found = new CatalogMessage { Provider = Providers.Procore };
        var problems = new List<string>();
        var skipped = 0;
        if (missing.Count == 0 || disposed)
        {
            return new ProcoreDiscovery { Catalog = found, OtherServerCompanies = missing };
        }

        await gate.WaitAsync();
        cancelRequested = false;
        try
        {
            foreach (var server in ProcoreServers.Where(server => !string.Equals(server, askedHost, StringComparison.OrdinalIgnoreCase)))
            {
                if (missing.Count == 0 || cancelRequested)
                {
                    break;
                }

                AppLog.Info("procore", $"Asking {server} for {string.Join(", ", missing.Select(company => company.Name ?? company.Id))}.");
                var problem = await EnsurePageAsync(server);
                if (problem is not null)
                {
                    AppLog.Warn("procore", $"Couldn't open {server}: {problem.Problem}");
                    continue;
                }

                var request = missing;
                var answer = await RequestWithLibraryAsync(id => ProviderScripts.ProcoreListCompanies(id, request), TimeSpan.FromMinutes(5));
                if (TypeOf(answer) != "procore-discovered")
                {
                    AppLog.Warn("procore", $"{server} didn't list projects ({Status(answer)}): {Message(answer) ?? "no answer"}");
                    continue;
                }

                var listed = ParseDiscovery(answer!.Value, server);
                found.Projects.AddRange(listed.Catalog?.Projects ?? new List<CatalogProject>());
                skipped += listed.SkippedInactive;
                var stillMissing = listed.OtherServerCompanies.Select(company => company.Id).ToHashSet(StringComparer.Ordinal);
                problems.AddRange(listed.CompanyProblems.Where(text => !listed.OtherServerCompanies.Any(company => text.StartsWith($"{company.Name ?? company.Id}:", StringComparison.Ordinal))));
                missing = missing.Where(company => stillMissing.Contains(company.Id)).ToList();
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            AppLog.Error("procore", "Asking other Procore servers failed", error);
            CloseBrowser();
        }
        finally
        {
            gate.Release();
        }

        return new ProcoreDiscovery { Catalog = found, CompanyProblems = problems, OtherServerCompanies = missing, SkippedInactive = skipped };
    }

    // ---- Reading projects ----------------------------------------------------------------------

    public async Task<ProviderSyncResult> SyncAsync(
        IReadOnlyList<ProviderSyncTarget> targets,
        Action<int, ProviderSyncTarget>? starting = null,
        Func<bool>? stop = null)
    {
        if (targets.Count == 0 || disposed)
        {
            return new ProviderSyncResult();
        }

        await gate.WaitAsync();
        cancelRequested = false;
        try
        {
            return await SyncThroughPageAsync(targets, starting, stop);
        }
        catch (OperationCanceledException)
        {
            return new ProviderSyncResult { Stopped = true, Problem = "The Procore read was stopped." };
        }
        catch (Exception error)
        {
            AppLog.Error("procore", "Reading Procore failed", error);
            CloseBrowser();
            return new ProviderSyncResult { Problem = $"Procore could not be read: {error.Message}" };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProviderSyncResult> SyncThroughPageAsync(
        IReadOnlyList<ProviderSyncTarget> targets,
        Action<int, ProviderSyncTarget>? starting,
        Func<bool>? stop)
    {
        if (PageReadingUnavailable)
        {
            return new ProviderSyncResult { FallbackToWindow = true };
        }

        var synced = new List<string>();
        var failed = new List<string>();
        var changed = 0;
        for (var index = 0; index < targets.Count; index++)
        {
            if (stop?.Invoke() == true || cancelRequested)
            {
                return new ProviderSyncResult { SyncedProjectIds = synced, FailedProjectIds = failed, Stopped = true, Changed = changed };
            }

            var target = targets[index];
            var siteHost = target.Host ?? ProviderLinks.DefaultProcoreHost;
            var problem = await EnsurePageAsync(siteHost);
            if (problem is not null)
            {
                AppLog.Warn("procore", $"Reading Procore projects didn't start: {problem.Problem}");
                return new ProviderSyncResult
                {
                    SyncedProjectIds = synced,
                    FailedProjectIds = failed,
                    Problem = problem.Problem,
                    NeedsSignIn = problem.NeedsSignIn
                };
            }

            starting?.Invoke(index, target);
            var existing = store.Snapshot(Providers.Procore, target.ProjectId);
            var startedAt = DateTimeOffset.Now;
            var fullEvery = TimeSpan.FromHours(Math.Max(1, store.Settings.Sync.FullReadHours));
            var full = existing is null
                || !lastFullRead.TryGetValue(target.ProjectId, out var lastFull)
                || startedAt - lastFull >= fullEvery;
            // A little overlap, so an item saved while the last read ran isn't missed.
            var since = full ? null : existing!.ScannedAt.ToUniversalTime().AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ssZ");
            var run = new SyncRun { Provider = Providers.Procore, ProjectId = target.ProjectId, StartedAt = startedAt, Mode = full ? "full" : "changes" };
            try
            {
                JsonElement? answer;
                using (AppLog.Time("procore.page", $"{target.DisplayName} ({(full ? "full" : "changes")})", 20_000))
                {
                    answer = await RequestWithLibraryAsync(id => ProviderScripts.ProcoreReadProject(id, target.ProjectId, target.AccountId, since), TimeSpan.FromMinutes(10));
                }

                if (cancelRequested)
                {
                    throw new OperationCanceledException();
                }

                if (TypeOf(answer) == "procore-result")
                {
                    var value = answer!.Value;
                    var unavailable = value.TryGetProperty("unavailable", out var missing) && missing.ValueKind == JsonValueKind.Array
                        ? missing.EnumerateArray().Select(entry => entry.GetString()).OfType<string>().ToList()
                        : new List<string>();
                    // A tool you can't see on this project is left as it was rather than emptied.
                    var types = ItemTypes.All
                        .Where(type => !unavailable.Contains(type == ItemTypes.Rfi ? "rfis" : "submittals", StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    var changesOnly = value.TryGetProperty("changesOnly", out var changesElement) && changesElement.ValueKind == JsonValueKind.True;
                    var snapshot = ProcoreItems.BuildSnapshot(target.ProjectId, target.AccountId, target.ProviderName, siteHost, types,
                        value.GetProperty("rfis").EnumerateArray().ToList(),
                        value.GetProperty("submittals").EnumerateArray().ToList(),
                        startedAt);
                    if (changesOnly && existing is not null)
                    {
                        // Only changed items came back: keep the rest of the saved list.
                        run.Mode = "changes";
                        run.ChangedCount = snapshot.Items.Count;
                        changed += snapshot.Items.Count;
                        if (snapshot.Items.Count == 0)
                        {
                            store.TouchSnapshot(Providers.Procore, target.ProjectId, startedAt);
                        }
                        else
                        {
                            var changedKeys = snapshot.Items.Select(item => $"{item.ItemType}|{item.ItemId}").ToHashSet(StringComparer.Ordinal);
                            snapshot.Items = existing.Items
                                .Where(item => types.Contains(item.ItemType, StringComparer.OrdinalIgnoreCase)
                                    && !changedKeys.Contains($"{item.ItemType}|{item.ItemId}"))
                                .Concat(snapshot.Items)
                                .ToList();
                            store.SaveSnapshot(snapshot);
                        }

                        run.ItemCount = store.Snapshot(Providers.Procore, target.ProjectId)?.Items.Count ?? snapshot.Items.Count;
                    }
                    else
                    {
                        run.Mode = "full";
                        store.SaveSnapshot(snapshot);
                        lastFullRead[target.ProjectId] = startedAt;
                        run.ItemCount = snapshot.Items.Count;
                    }

                    run.Status = "complete";
                    if (unavailable.Count > 0)
                    {
                        AppLog.Info("procore", $"{target.DisplayName}: no access to {string.Join(" and ", unavailable)} in Procore; left as they were.");
                    }

                    synced.Add(target.ProjectId);
                    continue;
                }

                var status = Status(answer);
                run.Error = Message(answer) ?? "Procore did not answer in time.";
                if (status == 401)
                {
                    store.MarkSignInNeeded(Providers.Procore);
                    failed.Add(target.ProjectId);
                    return new ProviderSyncResult
                    {
                        SyncedProjectIds = synced,
                        FailedProjectIds = failed,
                        Problem = "Sign in to Procore again: Settings → Connections → Connect to Procore.",
                        NeedsSignIn = true
                    };
                }

                if (synced.Count == 0 && status == 406)
                {
                    // Procore doesn't serve its REST lists to this sign-in: read its lists in the window instead.
                    PageReadingUnavailable = true;
                    AppLog.Warn("procore", $"Reading through the Procore page isn't available ({status}); using the browser window.");
                    return new ProviderSyncResult { FallbackToWindow = true };
                }

                // No access to this one project (403/404), or it timed out: flag it and carry on with the rest.
                failed.Add(target.ProjectId);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Something about this one project's items couldn't be used: flag it and carry on.
                AppLog.Error("procore", $"{target.DisplayName}: its items couldn't be saved", error);
                run.Error = error.Message;
                failed.Add(target.ProjectId);
            }
            finally
            {
                run.FinishedAt = DateTimeOffset.Now;
                if (run.Status != "complete")
                {
                    run.Status = "failed";
                    run.Error ??= "The read did not finish.";
                    AppLog.Warn("procore", $"{target.DisplayName}: {run.Error}");
                }

                store.RecordSyncRun(run);
            }
        }

        return new ProviderSyncResult { SyncedProjectIds = synced, FailedProjectIds = failed, Changed = changed };
    }

    // ---- The hidden Procore page ---------------------------------------------------------------

    private sealed record PageProblem(string Problem, bool NeedsSignIn);

    private bool OnProcore(string siteHost) =>
        core is not null
        && Uri.TryCreate(core.Source, UriKind.Absolute, out var current)
        && string.Equals(current.Host, siteHost, StringComparison.OrdinalIgnoreCase)
        && !ProviderSites.LooksLikeSignIn(current);

    private async Task<PageProblem?> EnsurePageAsync(string siteHost)
    {
        if (core is null)
        {
            if (DateTimeOffset.Now < pausedUntil)
            {
                return new PageProblem("Procore reads are paused for a moment while the browser signs out.", false);
            }

            try
            {
                AppLog.Info("procore", "Starting the background Procore page.");
                Directory.CreateDirectory(paths.BrowserDataDirectory);
                var environment = await WithTimeout(CoreWebView2Environment.CreateAsync(null, paths.BrowserDataDirectory), "creating the browser");
                host = new Window
                {
                    Width = 1,
                    Height = 1,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    Title = "CA Tracker background sync (Procore)"
                };
                controller = await WithTimeout(environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(host).EnsureHandle()), "opening the page");
                controller.IsVisible = false;
                core = controller.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.WebMessageReceived += OnWebMessageReceived;
                core.NavigationStarting += (_, _) =>
                {
                    if (navigated is null || navigated.Task.IsCompleted)
                    {
                        navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                };
                core.NavigationCompleted += (_, e) =>
                {
                    if (e.IsSuccess || e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    {
                        navigated?.TrySetResult(e.IsSuccess);
                    }
                };

                // Every Procore page this hidden browser opens gets the read-only library.
                await core.AddScriptToExecuteOnDocumentCreatedAsync(ProviderScripts.ProcoreSyncLibrary);
            }
            catch (Exception error)
            {
                AppLog.Error("procore", "The background browser could not start", error);
                CloseBrowser();
                return new PageProblem($"The built-in browser could not start: {error.Message}", false);
            }
        }

        if (OnProcore(siteHost))
        {
            return null;
        }

        // A data address on that server: Procore doesn't redirect it to your own company's server the
        // way its home page does, so each company's server can be asked directly.
        var waiting = navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        core!.Navigate($"https://{siteHost}/rest/v1.0/me");
        var opened = await Task.WhenAny(waiting.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        if (cancelRequested)
        {
            throw new OperationCanceledException();
        }

        if (opened != waiting.Task)
        {
            AppLog.Warn("procore", $"The background Procore page didn't finish opening in 60 s (at {core?.Source}).");
            return new PageProblem("Procore did not open in time. Check your internet connection.", false);
        }

        // Signed-out browsers end up on Procore's login site.
        for (var wait = 0; wait < 10 && !OnProcore(siteHost); wait++)
        {
            await Task.Delay(500);
        }

        if (!OnProcore(siteHost))
        {
            AppLog.Warn("procore", $"The background Procore page ended up at {core?.Source}, not signed in.");
            store.MarkSignInNeeded(Providers.Procore);
            return new PageProblem("Sign in to Procore: Settings → Connections → Connect to Procore.", true);
        }

        AppLog.Info("procore", $"The background Procore page is open at {core?.Source}.");
        return null;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, string step)
    {
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(45)));
        if (finished != task)
        {
            throw new TimeoutException($"The built-in browser got stuck {step}.");
        }

        return await task;
    }

    /// <summary>Runs a request; if the page reloaded and lost the library, adds it again and retries once.</summary>
    private async Task<JsonElement?> RequestWithLibraryAsync(Func<string, string> script, TimeSpan timeout)
    {
        var answer = await RequestAsync(script, timeout);
        if (TypeOf(answer) == "procore-missing" && core is not null)
        {
            await core.ExecuteScriptAsync(ProviderScripts.ProcoreSyncLibrary);
            answer = await RequestAsync(script, timeout);
        }

        return answer;
    }

    private static string? TypeOf(JsonElement? answer) =>
        answer is { } value && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out var type) ? type.GetString() : null;

    private static int Status(JsonElement? answer) =>
        answer is { } value && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("status", out var status) && status.TryGetInt32(out var code) ? code : 0;

    internal static string? Message(JsonElement? answer) =>
        answer is { } value && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : null;

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

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
            await core.ExecuteScriptAsync(script(id));
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout));
            return finished == completion.Task ? await completion.Task : null;
        }
        finally
        {
            pending.Remove(id);
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

    /// <summary>Lets the page way be tried again (e.g. after connecting to Procore again).</summary>
    public void ResetPageReading() => PageReadingUnavailable = false;

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

        foreach (var completion in pending.Values.ToList())
        {
            completion.TrySetCanceled();
        }

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
