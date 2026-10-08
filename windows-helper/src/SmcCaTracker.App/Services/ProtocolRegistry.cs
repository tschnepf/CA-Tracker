using System.IO;
using System.Security;
using Microsoft.Win32;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Links;

namespace SmcCaTracker.App.Services;

/// <summary>
/// Makes Windows open smc-ca-tracker:// links (Workload Tracker's buttons) with this app, for this Windows
/// user only. The installer registers the installed copy; this fills in a missing registration, or one
/// left by a copy that is gone (see <see cref="ProtocolRegistration.NeedsRegistering"/>).
/// </summary>
public static class ProtocolRegistry
{
    public static void EnsureRegistered(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        try
        {
            string? registered;
            using (var current = Registry.CurrentUser.OpenSubKey($@"{ProtocolRegistration.KeyPath}\shell\open\command"))
            {
                registered = current?.GetValue(null) as string;
            }

            if (!ProtocolRegistration.NeedsRegistering(registered, exePath, File.Exists))
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey(ProtocolRegistration.KeyPath);
            key.SetValue(null, ProtocolRegistration.Description);
            key.SetValue("URL Protocol", "");
            using (var icon = key.CreateSubKey("DefaultIcon"))
            {
                icon.SetValue(null, ProtocolRegistration.IconFor(exePath));
            }

            using (var command = key.CreateSubKey(@"shell\open\command"))
            {
                command.SetValue(null, ProtocolRegistration.CommandFor(exePath));
            }

            AppLog.Info("links", $"Registered {ItemLink.Scheme}:// links to open {exePath}.");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
        {
            AppLog.Warn("links", $"Couldn't register {ItemLink.Scheme}:// links: {error.Message}");
        }
    }
}
