using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Services;

/// <summary>A newer version published to the company's update location.</summary>
public sealed record UpdateInfo(Version Version, string Installer, string? Notes);

/// <summary>
/// Checks the company's update location (Settings from ca-tracker-company.json → updateFeed: a folder such
/// as \\server\apps\ca-tracker, or an https address) for <c>latest.json</c>:
/// <code>{ "version": "3.1.0", "installer": "SMC-CA-Tracker-Setup-3.1.0.exe", "notes": "…" }</code>
/// and installs a newer version when asked: the installer runs quietly, closes the app and starts it again.
/// </summary>
public sealed class UpdateService
{
    private readonly HttpClient http;

    public UpdateService(HttpClient http)
    {
        this.http = http;
    }

    public static Version Current => Version.TryParse(TrackerVersion.Current, out var version) ? version : new Version(0, 0, 0);

    /// <summary>The newer version on offer, or null (none, or the location can't be reached right now).</summary>
    public async Task<UpdateInfo?> CheckAsync(string feed)
    {
        try
        {
            var json = await ReadTextAsync(Combine(feed, "latest.json"));
            if (json is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var versionText = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
            var installer = root.TryGetProperty("installer", out var installerElement) ? installerElement.GetString() : null;
            if (!Version.TryParse(versionText, out var version) || string.IsNullOrWhiteSpace(installer) || version <= Current)
            {
                return null;
            }

            var notes = root.TryGetProperty("notes", out var notesElement) ? notesElement.GetString() : null;
            return new UpdateInfo(version, Combine(feed, installer), notes);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or TaskCanceledException
            or InvalidOperationException or UriFormatException or ArgumentException or NotSupportedException)
        {
            AppLog.Warn("update", $"Couldn't check {feed} for updates: {error.Message}");
            return null;
        }
    }

    /// <summary>Copies the installer to a temporary folder and runs it quietly; the caller then closes the app.</summary>
    public async Task<string?> StartInstallAsync(UpdateInfo update)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "SMC-CA-Tracker-update");
            Directory.CreateDirectory(folder);
            var name = Path.GetFileName(IsWeb(update.Installer) ? Uri.UnescapeDataString(new Uri(update.Installer).AbsolutePath) : update.Installer);
            var local = Path.Combine(folder, string.IsNullOrWhiteSpace(name) ? "SMC-CA-Tracker-Setup.exe" : name);
            if (IsWeb(update.Installer))
            {
                await using var source = await http.GetStreamAsync(update.Installer);
                await using var target = File.Create(local);
                await source.CopyToAsync(target);
            }
            else
            {
                File.Copy(update.Installer, local, overwrite: true);
            }

            AppLog.Info("update", $"Installing version {update.Version} from {update.Installer}.");
            Process.Start(new ProcessStartInfo(local, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or System.ComponentModel.Win32Exception
            or UriFormatException or InvalidOperationException or TaskCanceledException or ArgumentException or NotSupportedException)
        {
            AppLog.Error("update", "Starting the update failed", error);
            return error.Message;
        }
    }

    private static bool IsWeb(string location) =>
        location.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    private static string Combine(string feed, string name) =>
        IsWeb(name) || Path.IsPathFullyQualified(name) ? name
        : IsWeb(feed) ? $"{feed.TrimEnd('/')}/{Uri.EscapeDataString(name)}"
        : Path.Combine(feed, name);

    private async Task<string?> ReadTextAsync(string location)
    {
        if (IsWeb(location))
        {
            using var response = await http.GetAsync(location);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
        }

        return File.Exists(location) ? await File.ReadAllTextAsync(location) : null;
    }
}
