using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

/// <summary>
/// Connect to Helix / Connect to Procore: opens the site in the built-in browser so you can sign in.
/// The sign-in is kept in the same browser profile refreshes use, so they don't ask again. For Helix
/// it also reads the projects you are assigned to, so they can be picked in Connect…. Read-only.
/// </summary>
public partial class SignInWindow : Window
{
    private readonly string provider;
    private readonly AppPaths paths;
    private readonly TrackerStore store;
    private bool ready;

    public SignInWindow(string provider, AppPaths paths, TrackerStore store)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.provider = provider;
        this.paths = paths;
        this.store = store;

        Title = $"Sign in to {Label} — CA Tracker";
        HeadingText.Text = $"Sign in to {Label}";
        SetStatus($"Opening {Label}…");
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Browser.Dispose();
    }

    /// <summary>True once this window has seen you signed in.</summary>
    public bool SignedIn { get; private set; }

    /// <summary>Opened by itself because a sync found the sign-in expired (says so at the top).</summary>
    public bool SignInExpired { get; init; }

    /// <summary>Raised the first time this window sees you signed in.</summary>
    public event Action? SignedInDetected;

    /// <summary>Procore: this window listed every company and project you can see.</summary>
    public event Action<ProcoreDiscovery>? ProcoreProjectsFound;

    /// <summary>Procore: this window couldn't list your projects (the app then tries in the background).</summary>
    public event Action<string>? ProcoreDiscoveryFailed;

    /// <summary>Procore: the projects were found (or finding them failed) on this page.</summary>
    public bool ProcoreSearchFinished { get; private set; }

    private bool procoreSearching;

    private string Label => Providers.Label(provider);

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
            SetStatus("The Microsoft Edge WebView2 Runtime is not installed. Install it from Microsoft (search \"WebView2 Runtime\") and try again.");
            return;
        }
        catch (Exception error)
        {
            SetStatus($"The built-in browser could not start: {error.Message}");
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
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessageReceived;
        ready = true;
        core.Navigate(ProviderSites.HomeUrl(provider));
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var current))
        {
            return;
        }

        if (!ProviderSites.IsProviderHost(provider, current) || ProviderSites.LooksLikeSignIn(current))
        {
            ShowSignIn();
            return;
        }

        if (!SignedIn)
        {
            SetStatus($"Checking your {Label} sign-in…");
        }

        try
        {
            if (ProviderSites.IsProcore(provider))
            {
                await Browser.CoreWebView2.ExecuteScriptAsync(ProviderScripts.ProcoreSignInCheck);
            }
            else
            {
                await Browser.CoreWebView2.ExecuteScriptAsync(ProviderScripts.HelixBridge);
                await Browser.CoreWebView2.ExecuteScriptAsync(ProviderScripts.HelixSignInCheck);
            }
        }
        catch (Exception)
        {
            // The page moved on (for example to the sign-in page); its own load runs the check again.
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || !ProviderSites.IsProviderHost(provider, source))
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
                case "signedIn":
                    OnSignedIn(root);
                    break;
                case "signedOut":
                    ShowSignIn();
                    break;
                case "procore-discovered":
                    OnProcoreDiscovered(root, source.Host);
                    break;
                case "procore-error":
                    OnProcoreSearchFailed(ProcoreSyncService.Message(root) ?? "Procore didn't answer.");
                    break;
                case "procore-missing":
                    procoreSearching = false;
                    _ = FindProcoreProjectsAsync();
                    break;
                case "unknown" when !SignedIn:
                    // Signed in or not, the page didn't say who: log its shape (names only) to see why.
                    AppLog.Warn(provider, $"Couldn't tell who is signed in to {Label}. Page shape: " +
                        (root.TryGetProperty("shape", out var shape) ? shape.GetRawText() : "(none)"));
                    SetStatus($"If {Label} shows a sign-in page, sign in below. Otherwise you're connected and can close this window.");
                    break;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or IOException)
        {
        }
    }

    private void OnSignedIn(JsonElement root)
    {
        string? name = null;
        string? company = null;
        if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            name = user.TryGetProperty("fullName", out var fullName) && fullName.ValueKind == JsonValueKind.String ? fullName.GetString() : null;
            company = user.TryGetProperty("company", out var companyElement) && companyElement.ValueKind == JsonValueKind.String ? companyElement.GetString() : null;
        }

        var now = DateTimeOffset.Now;
        store.RecordAccount(provider, name, company, now);

        var projectCount = 0;
        if (root.TryGetProperty("catalog", out var catalogElement) && catalogElement.ValueKind == JsonValueKind.Object)
        {
            var message = catalogElement.Deserialize<CatalogMessage>(TrackerJson.Options);
            projectCount = message?.Projects.Count ?? 0;
            try
            {
                store.MergeCatalog(message, now);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        var first = !SignedIn;
        if (first && !ProviderSites.IsProcore(provider))
        {
            var listError = root.TryGetProperty("listError", out var errorElement) && errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : null;
            AppLog.Info(provider, $"Signed in to {Label}; {projectCount} assigned projects listed" + (listError is null ? "." : $" (listing failed: {listError})."));
        }

        SignedIn = true;
        var who = string.IsNullOrWhiteSpace(name) ? "" : $" as {name}";
        var projects = ProviderSites.IsProcore(provider)
            ? "CA Tracker is now finding your projects in every Procore company and syncing them (progress shows in Sync…)."
            : projectCount > 0
                ? $"Your {projectCount} Helix projects can now be linked to CA folders (Link… in the main window)."
                : "Sync will use this sign-in.";
        SetStatus($"Signed in to {Label}{who}. {projects} You can close this window.");
        DoneButton.Content = "Done";
        DoneButton.Style = (Style)FindResource("AccentButton");
        if (first)
        {
            SignedInDetected?.Invoke();
        }

        if (ProviderSites.IsProcore(provider))
        {
            _ = FindProcoreProjectsAsync();
        }
    }

    /// <summary>
    /// Lists every company and project you can see, right here on this signed-in Procore page, so it
    /// doesn't depend on the app's hidden background page. Read-only.
    /// </summary>
    private async Task FindProcoreProjectsAsync()
    {
        if (ProcoreSearchFinished || procoreSearching || !ready)
        {
            return;
        }

        procoreSearching = true;
        SetStatus("Signed in to Procore. Finding your projects in every Procore company you belong to…");
        AppLog.Info("procore", "Listing Procore companies and projects in the Connect to Procore window.");
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(ProviderScripts.ProcoreSyncLibrary);
            await Browser.CoreWebView2.ExecuteScriptAsync(ProviderScripts.ProcoreDiscover("connect-window"));
        }
        catch (Exception error)
        {
            // The page moved on; its next load tries again.
            procoreSearching = false;
            AppLog.Warn("procore", $"Couldn't start listing projects in the Connect window: {error.Message}");
        }
    }

    private void OnProcoreDiscovered(JsonElement root, string siteHost)
    {
        if (ProcoreSearchFinished)
        {
            return;
        }

        var discovery = ProcoreSyncService.ParseDiscovery(root, siteHost);
        ProcoreSearchFinished = true;
        procoreSearching = false;
        if (!string.IsNullOrWhiteSpace(discovery.UserName))
        {
            store.RecordAccount(provider, discovery.UserName, null, DateTimeOffset.Now);
        }

        SetStatus(discovery.ProjectCount > 0
            ? $"Signed in to Procore. {MainViewModelText(discovery)} Syncing them now (progress shows above the project list). You can close this window."
            : "Signed in to Procore, but no projects were found. Check that you can see projects under View All Projects, or open the log folder (Settings → General → Troubleshooting).");
        ProcoreProjectsFound?.Invoke(discovery);
    }

    private static string MainViewModelText(ProcoreDiscovery discovery) => ViewModels.MainViewModel.FoundText(discovery);

    private void OnProcoreSearchFailed(string message)
    {
        if (ProcoreSearchFinished)
        {
            return;
        }

        ProcoreSearchFinished = true;
        procoreSearching = false;
        AppLog.Warn("procore", $"Listing projects in the Connect window failed: {message}");
        SetStatus($"Signed in to Procore, but CA Tracker couldn't list your projects here ({message}). It will try in the background.");
        ProcoreDiscoveryFailed?.Invoke(message);
    }

    private void ShowSignIn()
    {
        if (SignedIn)
        {
            return;
        }

        SetStatus(SignInExpired
            ? $"{Label} needs you to sign in again before CA Tracker can sync. Sign in below (your saved password fills in); syncing then carries on by itself."
            : $"Sign in to {Label} below. CA Tracker remembers the sign-in, so refreshes won't ask again.");
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        if (ready)
        {
            Browser.CoreWebView2.Navigate(ProviderSites.HomeUrl(provider));
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
