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

    /// <summary>
    /// A sign-in page: a whole path segment such as /login, /sign-in, /auth, /sso or /account (optionally
    /// with an extension, e.g. Login.aspx). Words that only contain one ("Associated", "Author",
    /// "Accounts") are ordinary pages.
    /// </summary>
    public static bool LooksLikeSignIn(Uri uri) =>
        Regex.IsMatch(uri.AbsolutePath, @"(^|/)(log-?in|sign-?in|auth|sso|account)(\.[a-z]+)?(/|$)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Whether loading <paramref name="destination"/> in the item panel closes the Helix item open there:
    /// Helix sent you to sign in (or off Helix), or reloaded the item's list, which closes its viewer.
    /// Moving to another Helix page yourself doesn't count.
    /// </summary>
    public static bool ClosesHelixItem(Uri destination, Uri listUrl) =>
        !IsProviderHost(Providers.Helix, destination)
        || LooksLikeSignIn(destination)
        || string.Equals(destination.AbsolutePath.TrimEnd('/'), listUrl.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
