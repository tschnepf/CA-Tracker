using System.Text.RegularExpressions;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;

namespace SmcCaTracker.App.Services;

/// <summary>Which pages belong to Helix or Procore, for the built-in browser windows.</summary>
public static class ProviderSites
{
    private static readonly Regex ProcoreHost = new(@"^(?:app|[a-z]{2}\d{2})\.procore\.com$", RegexOptions.IgnoreCase);

    public static bool IsProcore(string provider) =>
        string.Equals(provider, Providers.Procore, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where Connect to Helix / Connect to Procore starts.</summary>
    public static string HomeUrl(string provider) =>
        IsProcore(provider) ? $"https://{ProviderLinks.DefaultProcoreHost}/" : $"https://{ProviderLinks.HelixHost}/";

    public static bool IsProviderHost(string provider, Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && (IsProcore(provider)
            ? ProcoreHost.IsMatch(uri.Host)
            : string.Equals(uri.Host, ProviderLinks.HelixHost, StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikeSignIn(Uri uri) =>
        Regex.IsMatch(uri.AbsolutePath, "log-?in|sign-?in|auth|sso|account", RegexOptions.IgnoreCase);
}
