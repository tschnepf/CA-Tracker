using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Views;

/// <summary>A token handed over by Workload Tracker's Profile page.</summary>
public sealed record WorkloadTrackerToken(string Token, int? SessionId, DateTimeOffset? ExpiresAt);

/// <summary>
/// Connect to Workload Tracker (decision D2): opens the tracker's Profile → CA Tracker page in the
/// built-in browser, where you sign in as usual and press Connect CA Tracker. The page hands the token
/// to this window (<c>window.chrome.webview.postMessage</c>); only messages from the tracker's own
/// address are accepted. Nothing is read from the page otherwise.
/// </summary>
public partial class WorkloadTrackerSignInWindow : Window
{
    /// <summary>The message type the Profile page posts (ConnectCaTrackerSection.tsx).</summary>
    private const string TokenMessage = "workload-tracker-ca-token";

    private readonly Uri address;
    private readonly AppPaths paths;

    public WorkloadTrackerSignInWindow(string address, AppPaths paths)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        this.address = new Uri(address);
        this.paths = paths;
        StatusText.Text = $"Opening {this.address.Host}… Sign in, then press Connect CA Tracker.";
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Browser.Dispose();
    }

    /// <summary>The token, once the page has handed it over.</summary>
    public WorkloadTrackerToken? Token { get; private set; }

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
            StatusText.Text = "The Microsoft Edge WebView2 Runtime is not installed. Install it from Microsoft (search \"WebView2 Runtime\") and try again.";
            return;
        }
        catch (Exception error)
        {
            StatusText.Text = $"The built-in browser could not start: {error.Message}";
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += OnWebMessageReceived;
        var device = Uri.EscapeDataString(Environment.MachineName);
        core.Navigate(new Uri(address, $"/profile?section=ca-tracker&device={device}").ToString());
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
            || !string.Equals(source.GetLeftPart(UriPartial.Authority), address.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type) || type.GetString() != TokenMessage
                || !root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
            {
                return;
            }

            int? sessionId = root.TryGetProperty("sessionId", out var id) && id.TryGetInt32(out var number) ? number : null;
            DateTimeOffset? expiresAt = root.TryGetProperty("expiresAt", out var expires) && expires.TryGetDateTimeOffset(out var when) ? when : null;
            Token = new WorkloadTrackerToken(token.GetString()!, sessionId, expiresAt);
            DialogResult = true;
        }
        catch (JsonException error)
        {
            AppLog.Warn("workload", $"Ignored a message from the Workload Tracker page: {error.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
