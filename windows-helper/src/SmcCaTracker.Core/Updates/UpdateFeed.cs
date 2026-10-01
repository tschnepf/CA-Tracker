using System.Security.Cryptography;
using System.Text.Json;

namespace SmcCaTracker.Core.Updates;

/// <summary>A newer version published to the company's update location.</summary>
/// <param name="Version">The version on offer.</param>
/// <param name="Installer">Where the installer is: a file under the update folder, or an https address under the feed.</param>
/// <param name="Sha256">The installer's SHA-256 as published in <c>latest.json</c> (lower-case hex).</param>
/// <param name="Notes">Release notes shown before installing.</param>
public sealed record UpdateInfo(Version Version, string Installer, string Sha256, string? Notes);

/// <summary>
/// Reads the company's <c>latest.json</c> and decides whether it describes a newer, installable version.
/// The installer named there must be a bare file name that lives next to <c>latest.json</c> (never another
/// folder, a UNC path or a web address), the feed must be a folder or an https address, and the file's
/// SHA-256 must match the published one before it is run.
/// </summary>
public static class UpdateFeed
{
    /// <summary>True for an https address; plain http is never accepted for a feed or an installer.</summary>
    public static bool IsWeb(string location) => location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for an address the feed must never use (an unencrypted web address).</summary>
    public static bool IsInsecureWeb(string location) => location.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="name"/> is a plain file name: no folders, drives, separators or web schemes.</summary>
    public static bool IsBareFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(['\\', '/', ':']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name != "." && name != ".."
        && string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);

    /// <summary>Compares on major.minor.build only, so a four-part <c>latest.json</c> never outranks the same three-part app version.</summary>
    public static bool IsNewer(Version candidate, Version current) => Trim(candidate) > Trim(current);

    private static Version Trim(Version version) =>
        new(Math.Max(version.Major, 0), Math.Max(version.Minor, 0), Math.Max(version.Build, 0));

    /// <summary>The address of a file published next to <c>latest.json</c>. Only bare names are combined; anything else is refused.</summary>
    public static string Resolve(string feed, string name)
    {
        if (!IsBareFileName(name))
        {
            throw new NotSupportedException($"The update feed may only name a file next to latest.json, not '{name}'.");
        }

        if (IsInsecureWeb(feed))
        {
            throw new NotSupportedException("The update feed must be a folder or an https address, not plain http.");
        }

        return IsWeb(feed) ? $"{feed.TrimEnd('/')}/{Uri.EscapeDataString(name)}" : Path.Combine(feed, name);
    }

    /// <summary>
    /// Parses <c>latest.json</c>. Null when nothing newer is on offer or the file can't be trusted;
    /// <paramref name="reason"/> says why so the log can tell.
    /// </summary>
    public static UpdateInfo? Parse(string feed, string json, Version current, out string? reason)
    {
        reason = null;
        if (IsInsecureWeb(feed))
        {
            reason = "the update feed uses plain http";
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var versionText = Text(root, "version");
        var installer = Text(root, "installer");
        var sha256 = Text(root, "sha256");
        var notes = Text(root, "notes");

        if (!Version.TryParse(versionText, out var version))
        {
            reason = "latest.json has no usable version";
            return null;
        }

        if (!IsNewer(version, current))
        {
            return null;
        }

        if (!IsBareFileName(installer))
        {
            reason = $"latest.json names an installer that is not a file next to it ('{installer}')";
            return null;
        }

        if (!IsSha256(sha256))
        {
            reason = "latest.json has no sha256 for the installer";
            return null;
        }

        return new UpdateInfo(version, Resolve(feed, installer!), sha256!.ToLowerInvariant(), notes);
    }

    /// <summary>True when the file's SHA-256 is the published one.</summary>
    public static async Task<bool> MatchesAsync(string path, string sha256, CancellationToken cancellation = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellation);
        return string.Equals(Convert.ToHexString(hash), sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSha256(string? text) =>
        text is { Length: 64 } && text.All(Uri.IsHexDigit);

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
