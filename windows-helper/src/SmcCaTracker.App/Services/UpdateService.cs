using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.Updates;

namespace SmcCaTracker.App.Services;

/// <summary>
/// Checks the company's update location (Settings from ca-tracker-company.json → updateFeed: a folder such
/// as \\server\apps\ca-tracker, or an https address) for <c>latest.json</c>:
/// <code>{ "version": "3.1.0", "installer": "SMC-CA-Tracker-Setup-3.1.0.exe", "sha256": "…", "notes": "…" }</code>
/// and installs a newer version when asked: the installer (which must sit next to latest.json and match the
/// published SHA-256) runs quietly, closes the app and starts it again.
/// </summary>
public sealed class UpdateService
{
    private readonly HttpClient http;

    public UpdateService(HttpClient http)
    {
        this.http = http;
    }

    public static Version Current => Version.TryParse(TrackerVersion.Current, out var version) ? version : new Version(0, 0, 0);

    /// <summary>The newer version on offer, or null (none, or the location can't be reached or trusted right now).</summary>
    public async Task<UpdateInfo?> CheckAsync(string feed)
    {
        try
        {
            var json = await ReadTextAsync(UpdateFeed.Resolve(feed, "latest.json"));
            if (json is null)
            {
                return null;
            }

            var update = UpdateFeed.Parse(feed, json, Current, out var reason);
            if (reason is not null)
            {
                AppLog.Warn("update", $"Ignoring {feed}: {reason}.");
            }

            return update;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or TaskCanceledException
            or InvalidOperationException or UriFormatException or ArgumentException or NotSupportedException)
        {
            AppLog.Warn("update", $"Couldn't check {feed} for updates: {error.Message}");
            return null;
        }
    }

    /// <summary>
    /// Copies the installer to a temporary folder, checks its SHA-256 against latest.json and runs it quietly;
    /// the caller then closes the app. Returns the problem when it couldn't start.
    /// </summary>
    public async Task<string?> StartInstallAsync(UpdateInfo update)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "SMC-CA-Tracker-update");
            Directory.CreateDirectory(folder);
            var name = Path.GetFileName(UpdateFeed.IsWeb(update.Installer) ? Uri.UnescapeDataString(new Uri(update.Installer).AbsolutePath) : update.Installer);
            if (!UpdateFeed.IsBareFileName(name))
            {
                return "The update feed names an installer that isn't a file next to latest.json.";
            }

            var local = Path.Combine(folder, name);
            if (UpdateFeed.IsWeb(update.Installer))
            {
                await using var source = await http.GetStreamAsync(update.Installer);
                await using var target = File.Create(local);
                await source.CopyToAsync(target);
            }
            else if (UpdateFeed.IsInsecureWeb(update.Installer))
            {
                return "The update feed must be a folder or an https address.";
            }
            else
            {
                // Opening a file on a share waits on the server: off the screen's thread, so the window
                // doesn't freeze while a slow or offline server answers.
                await Task.Run(async () =>
                {
                    await using var source = new FileStream(update.Installer, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                    await using var target = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                    await source.CopyToAsync(target);
                });
            }

            if (!await UpdateFeed.MatchesAsync(local, update.Sha256))
            {
                File.Delete(local);
                AppLog.Error("update", $"The installer at {update.Installer} does not match the sha256 in latest.json; not running it.");
                return "The downloaded installer doesn't match the published checksum, so it was not run. Tell whoever publishes updates.";
            }

            AppLog.Info("update", $"Installing version {update.Version} from {update.Installer} (sha256 {update.Sha256}).");
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

    private async Task<string?> ReadTextAsync(string location)
    {
        if (UpdateFeed.IsWeb(location))
        {
            using var response = await http.GetAsync(location);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
        }

        return await Task.Run(() => File.Exists(location) ? File.ReadAllText(location) : null);
    }
}
