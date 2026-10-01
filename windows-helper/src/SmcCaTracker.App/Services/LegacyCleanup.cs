using System.IO;
using Microsoft.Win32;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Services;

/// <summary>
/// Earlier versions of the app connected to a Chrome extension through a "native messaging" registration
/// for the current Windows user. The app no longer uses Chrome, so this removes that registration
/// and its files from %LOCALAPPDATA%\SMC-CA-Tracker. It never touches CA folders.
/// </summary>
public static class LegacyCleanup
{
    private const string HostName = "com.smc.catracker";

    private static readonly string[] BrowserKeys =
    {
        $@"Software\Google\Chrome\NativeMessagingHosts\{HostName}",
        $@"Software\Microsoft\Edge\NativeMessagingHosts\{HostName}"
    };

    public static void Run(AppPaths paths)
    {
        foreach (var keyPath in BrowserKeys)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
            }
        }

        foreach (var name in new[] { $"{HostName}.json", "native-host.log", "extension-contact.json" })
        {
            LocalFiles.TryDelete(Path.Combine(paths.Root, name));
        }
    }
}
