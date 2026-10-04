using System.Text.RegularExpressions;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Refresh;

/// <summary>One page the built-in browser opens during a refresh, and the item types that page provides.</summary>
public sealed class RefreshStep
{
    public string Url { get; set; } = "";
    public List<string> ItemTypes { get; set; } = new();
}

/// <summary>Reads Helix/Procore project links and builds the pages a refresh opens.</summary>
public static partial class ProviderLinks
{
    public const string HelixHost = "project.helixteam.com";

    /// <summary>
    /// Helix's new web app. Helix sends some projects' RFI or Submittals pages there; CA Tracker's page
    /// library works on <see cref="HelixHost"/> only, so it reads those projects from there instead.
    /// </summary>
    public const string HelixNewAppHost = "app.helixteam.com";

    public const string DefaultProcoreHost = "app.procore.com";

    [GeneratedRegex(@"^/Projects/([^/]+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex HelixPath();

    [GeneratedRegex(@"^/webclients/host/companies/(\d+)/projects/(\d+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ProcoreModernPath();

    [GeneratedRegex(@"^/(\d+)/project(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ProcoreLegacyPath();

    [GeneratedRegex(@"^(?:app|[a-z]{2}\d{2})\.procore\.com$", RegexOptions.IgnoreCase)]
    private static partial Regex ProcoreHost();

    /// <summary>
    /// Understands a Helix project link (https://project.helixteam.com/Projects/{id}/...) or a Procore
    /// project tool link (https://app.procore.com/webclients/host/companies/{company}/projects/{project}/...).
    /// </summary>
    public static bool TryParse(string? input, out ProviderConnection? connection, out string error)
    {
        connection = null;
        error = "";
        if (!Uri.TryCreate((input ?? "").Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            error = "Paste a full https:// link to the project in Helix or Procore.";
            return false;
        }

        if (string.Equals(uri.Host, HelixHost, StringComparison.OrdinalIgnoreCase))
        {
            var match = HelixPath().Match(uri.AbsolutePath);
            if (!match.Success)
            {
                error = "That Helix link does not include a project. Open the project's Overview page and copy its link.";
                return false;
            }

            connection = new ProviderConnection
            {
                Provider = Providers.Helix,
                ProjectId = Uri.UnescapeDataString(match.Groups[1].Value)
            };
            return true;
        }

        if (ProcoreHost().IsMatch(uri.Host))
        {
            var modern = ProcoreModernPath().Match(uri.AbsolutePath);
            if (modern.Success)
            {
                connection = new ProviderConnection
                {
                    Provider = Providers.Procore,
                    AccountId = modern.Groups[1].Value,
                    ProjectId = modern.Groups[2].Value,
                    Host = uri.Host.ToLowerInvariant()
                };
                return true;
            }

            error = ProcoreLegacyPath().IsMatch(uri.AbsolutePath)
                ? "That older Procore link has no company in it. Open the project's RFIs tool and copy that link instead."
                : "That Procore link does not include a project. Open the project's RFIs tool and copy its link.";
            return false;
        }

        error = "Only Helix (project.helixteam.com) and Procore (procore.com) links are supported.";
        return false;
    }

    /// <summary>The pages a refresh opens, in order.</summary>
    public static List<RefreshStep> RefreshSteps(ProviderConnection connection)
    {
        if (string.Equals(connection.Provider, Providers.Helix, StringComparison.OrdinalIgnoreCase))
        {
            // The Overview page scan reads every RFI and Submittal in the project.
            return new List<RefreshStep>
            {
                new()
                {
                    Url = $"https://{HelixHost}/Projects/{Uri.EscapeDataString(connection.ProjectId)}/Overview",
                    ItemTypes = new List<string> { ItemTypes.Rfi, ItemTypes.Submittal }
                }
            };
        }

        if (string.IsNullOrWhiteSpace(connection.AccountId))
        {
            throw new InvalidOperationException("This Procore connection has no company. Connect it again using the project's RFIs link.");
        }

        var host = !string.IsNullOrWhiteSpace(connection.Host) && ProcoreHost().IsMatch(connection.Host)
            ? connection.Host
            : DefaultProcoreHost;
        var project = $"https://{host}/webclients/host/companies/{Uri.EscapeDataString(connection.AccountId)}/projects/{Uri.EscapeDataString(connection.ProjectId)}";
        return new List<RefreshStep>
        {
            new() { Url = $"{project}/tools/rfis", ItemTypes = new List<string> { ItemTypes.Rfi } },
            new() { Url = $"{project}/tools/submittals", ItemTypes = new List<string> { ItemTypes.Submittal } }
        };
    }

    public static string ProjectUrl(ProviderConnection connection) => RefreshSteps(connection)[0].Url;
}
