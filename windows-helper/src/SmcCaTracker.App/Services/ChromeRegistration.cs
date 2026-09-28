using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Services;

public sealed record RegistrationStatus(bool IsRegistered, string Detail);

/// <summary>
/// Registers the native messaging host for the current Windows user only (HKCU), so no
/// administrator rights are needed. Only the listed extension IDs may start the host.
/// </summary>
public sealed class ChromeRegistration
{
    public const string HostName = "com.smc.catracker";
    public const string HostExecutable = "SmcCaTracker.NativeHost.exe";

    private static readonly string[] BrowserKeys =
    {
        $@"Software\Google\Chrome\NativeMessagingHosts\{HostName}",
        $@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}"
    };

    private readonly AppPaths paths;

    public ChromeRegistration(AppPaths paths)
    {
        this.paths = paths;
    }

    public static string HostExecutablePath => Path.Combine(AppContext.BaseDirectory, HostExecutable);

    public RegistrationStatus GetStatus()
    {
        try
        {
            return ReadStatus();
        }
        catch (Exception error)
        {
            return new RegistrationStatus(false, $"The Chrome registration could not be read: {error.Message}");
        }
    }

    private RegistrationStatus ReadStatus()
    {
        using var key = Registry.CurrentUser.OpenSubKey(BrowserKeys[0]);
        var manifestPath = key?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return new RegistrationStatus(false, "Not registered with Chrome.");
        }

        if (!File.Exists(manifestPath))
        {
            return new RegistrationStatus(false, $"Registered, but {manifestPath} is missing. Register again.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var hostPath = document.RootElement.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(hostPath) || !File.Exists(hostPath))
            {
                return new RegistrationStatus(false, "Registered, but the helper program has moved. Register again.");
            }

            if (!string.Equals(Path.GetFullPath(hostPath), Path.GetFullPath(HostExecutablePath), StringComparison.OrdinalIgnoreCase))
            {
                return new RegistrationStatus(true, $"Registered to a different copy: {hostPath}");
            }

            return new RegistrationStatus(true, "Registered with Chrome for this Windows user.");
        }
        catch (Exception error) when (error is JsonException or IOException or KeyNotFoundException)
        {
            return new RegistrationStatus(false, $"The registration file could not be read: {error.Message}");
        }
    }

    public void Register(IEnumerable<string> extensionIds)
    {
        var origins = extensionIds
            .Select(id => id.Trim())
            .Where(id => id.Length == 32 && id.All(character => character is >= 'a' and <= 'p'))
            .Distinct()
            .Select(id => $"chrome-extension://{id}/")
            .ToList();
        if (origins.Count == 0)
        {
            throw new InvalidOperationException("At least one valid Chrome extension ID is required.");
        }

        if (!File.Exists(HostExecutablePath))
        {
            throw new FileNotFoundException($"{HostExecutable} was not found next to the app. Rebuild with build.ps1.", HostExecutablePath);
        }

        var manifest = new
        {
            name = HostName,
            description = "SMC CA Tracker Windows helper",
            path = HostExecutablePath,
            type = "stdio",
            allowed_origins = origins
        };
        LocalFiles.WriteAllTextAtomic(paths.HostManifestFile, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var keyPath in BrowserKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(null, paths.HostManifestFile);
        }
    }

    public void Unregister()
    {
        foreach (var keyPath in BrowserKeys)
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }

        LocalFiles.TryDelete(paths.HostManifestFile);
    }
}
