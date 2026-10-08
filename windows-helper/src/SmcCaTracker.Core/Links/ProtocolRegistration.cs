namespace SmcCaTracker.Core.Links;

/// <summary>
/// Windows' registration of <c>smc-ca-tracker://</c> links for the person (HKCU\Software\Classes). The
/// installer writes it; the app writes it again at startup when it is missing or names a program that is
/// no longer there (a moved or reinstalled copy), but leaves another working copy's registration alone.
/// </summary>
public static class ProtocolRegistration
{
    /// <summary>The key under HKEY_CURRENT_USER.</summary>
    public const string KeyPath = @"Software\Classes\" + ItemLink.Scheme;

    /// <summary>The key's default value.</summary>
    public const string Description = "URL:SMC CA Tracker";

    /// <summary>What shell\open\command runs: the app, with the link as its one argument.</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>DefaultIcon: the app's own icon.</summary>
    public static string IconFor(string exePath) => $"\"{exePath}\",0";

    /// <summary>
    /// Whether to (re)register this program, given the command registered now (null when there is none).
    /// Yes when nothing is registered or the registered program is gone; no when it names this program or
    /// another one that is still there.
    /// </summary>
    public static bool NeedsRegistering(string? registeredCommand, string exePath, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        if (ProgramOf(registeredCommand) is not { } registered)
        {
            return true;
        }

        return !string.Equals(registered, exePath, StringComparison.OrdinalIgnoreCase) && !fileExists(registered);
    }

    /// <summary>The program a command runs: the first quoted part, or everything up to the first space.</summary>
    public static string? ProgramOf(string? command)
    {
        var text = (command ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            var quoted = end < 0 ? text[1..] : text[1..end];
            return quoted.Trim().Length == 0 ? null : quoted.Trim();
        }

        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}
