using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Services;

/// <summary>
/// Signs CA Tracker's built-in browser out of every site. A plain sign-out clears cookies, site storage
/// (where Helix keeps its sign-in), cache, history and site settings, and keeps the saved usernames and
/// passwords and the form autofill, so the next sign-in fills them in. Forgetting clears those too.
/// </summary>
public static class BrowserSignOut
{
    // The kinds are separate flags, not nested masks: AllSite and AllProfile are one bit each, and
    // AllProfile includes the saved passwords whatever else is left out. So the plain sign-out lists
    // what it clears instead of taking the passwords out of AllProfile.
    private const CoreWebView2BrowsingDataKinds SignInsOnly =
        CoreWebView2BrowsingDataKinds.AllSite
        | CoreWebView2BrowsingDataKinds.ServiceWorkers
        | CoreWebView2BrowsingDataKinds.DiskCache
        | CoreWebView2BrowsingDataKinds.DownloadHistory
        | CoreWebView2BrowsingDataKinds.BrowsingHistory
        | CoreWebView2BrowsingDataKinds.Settings;

    /// <summary>Everything in the profile, saved usernames and passwords and form autofill included.</summary>
    private const CoreWebView2BrowsingDataKinds Everything = CoreWebView2BrowsingDataKinds.AllProfile;

    /// <summary>Null when signed out; otherwise what went wrong.</summary>
    public static Task<string?> ClearSignInsAsync(AppPaths paths, bool forgetPasswords) =>
        WithProfileAsync(paths, core => core.Profile.ClearBrowsingDataAsync(forgetPasswords ? Everything : SignInsOnly));

    /// <summary>
    /// Signs out of one site only: its cookies and its pages' storage (where Helix keeps its sign-in)
    /// are cleared. The other site's sign-in and every saved username and password stay. Null when
    /// signed out; otherwise what went wrong.
    /// </summary>
    public static Task<string?> ClearSiteAsync(AppPaths paths, string provider)
    {
        var site = ProviderSites.IsProcore(provider) ? "procore.com" : "helixteam.com";
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { site, $"www.{site}" };
        hosts.UnionWith(ProviderSites.IsProcore(provider)
            ? new[] { "app.procore.com", "us02.procore.com", "login.procore.com" }
            : new[] { "project.helixteam.com", "api.helixteam.com" });
        return WithProfileAsync(paths, async core =>
        {
            foreach (var cookie in await core.CookieManager.GetCookiesAsync(null))
            {
                var host = cookie.Domain.TrimStart('.');
                if (string.Equals(host, site, StringComparison.OrdinalIgnoreCase) || host.EndsWith($".{site}", StringComparison.OrdinalIgnoreCase))
                {
                    hosts.Add(host);
                    core.CookieManager.DeleteCookie(cookie);
                }
            }

            foreach (var host in hosts)
            {
                await core.CallDevToolsProtocolMethodAsync("Storage.clearDataForOrigin",
                    JsonSerializer.Serialize(new { origin = $"https://{host}", storageTypes = "all" }));
            }
        });
    }

    /// <summary>Runs <paramref name="work"/> on a hidden page of the built-in browser's profile.</summary>
    private static async Task<string?> WithProfileAsync(AppPaths paths, Func<CoreWebView2, Task> work)
    {
        Window? host = null;
        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, paths.BrowserDataDirectory);
            host = new Window
            {
                Width = 1,
                Height = 1,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Title = "CA Tracker sign-out"
            };
            controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(host).EnsureHandle());
            controller.IsVisible = false;
            await work(controller.CoreWebView2);
            return null;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            return "The Microsoft Edge WebView2 Runtime is not installed.";
        }
        catch (Exception error)
        {
            AppLog.Error("app", "Signing out of the built-in browser failed", error);
            return error.Message;
        }
        finally
        {
            controller?.Close();
            host?.Close();
        }
    }
}
