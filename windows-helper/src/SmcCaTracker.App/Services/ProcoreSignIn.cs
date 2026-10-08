using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using SmcCaTracker.Core.Diagnostics;

namespace SmcCaTracker.App.Services;

/// <summary>
/// Keeps your Procore sign-in when CA Tracker closes. Procore signs you in with session cookies, which a
/// browser drops when it closes, so every start of the app asked you to sign in to Procore again (Helix
/// keeps its sign-in in a lasting cookie, so it never did). After each successful read of Procore, its
/// session cookies are saved as ordinary cookies of the built-in browser's profile, which keeps them
/// encrypted for your Windows user, the way a browser set to "continue where you left off" does. They
/// last <see cref="KeepFor"/> from the latest read. Procore still decides how long the sign-in lasts:
/// when it ends, Procore answers 401 and CA Tracker asks you to sign in. Signing out deletes them as before.
/// </summary>
public static class ProcoreSignIn
{
    /// <summary>How long a kept sign-in cookie lasts after the latest successful read.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);

    /// <summary>
    /// Saves the profile's Procore session cookies as lasting ones. Cookies already lasting are left as
    /// they are. Use from the screen's thread; never fails (a problem is logged).
    /// </summary>
    public static async Task KeepAsync(CoreWebView2? core)
    {
        if (core is null)
        {
            return;
        }

        try
        {
            var kept = 0;
            foreach (var cookie in await core.CookieManager.GetCookiesAsync(null))
            {
                if (!cookie.IsSession || !ProviderSites.IsProcoreCookieDomain(cookie.Domain))
                {
                    continue;
                }

                cookie.Expires = DateTime.Now + KeepFor;
                core.CookieManager.AddOrUpdateCookie(cookie);
                kept++;
            }

            if (kept > 0)
            {
                AppLog.Info("procore", $"Kept {kept} Procore sign-in cookie{(kept == 1 ? "" : "s")} so the sign-in lasts when CA Tracker closes.");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or COMException or ArgumentException)
        {
            // The page closed meanwhile; the next successful read keeps them.
            AppLog.Warn("procore", $"Couldn't keep the Procore sign-in for the next start: {error.Message}");
        }
    }
}
