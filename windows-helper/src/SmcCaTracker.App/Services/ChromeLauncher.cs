using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.Services;

/// <summary>Opens a page in the user's Chrome so the SMC-CA-Tracker extension can refresh it.</summary>
public static class ChromeLauncher
{
    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";

    public static string? FindChrome(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(AppPathsKey);
                if (key?.GetValue(null) is string path && File.Exists(path))
                {
                    return path;
                }
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        return candidates
            .Where(folder => !string.IsNullOrEmpty(folder))
            .Select(folder => Path.Combine(folder, "Google", "Chrome", "Application", "chrome.exe"))
            .FirstOrDefault(File.Exists);
    }

    public static void Open(string url, BrowserSettings browser)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Only https links can be opened.");
        }

        var chrome = FindChrome(browser.ChromePath)
            ?? throw new FileNotFoundException("Chrome could not be found. Set its location in Settings → Chrome.");
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
        if (!string.IsNullOrWhiteSpace(browser.ProfileDirectory))
        {
            start.ArgumentList.Add($"--profile-directory={browser.ProfileDirectory.Trim()}");
        }

        start.ArgumentList.Add(uri.AbsoluteUri);
        using var _ = Process.Start(start);
    }
}
