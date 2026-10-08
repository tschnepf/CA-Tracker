using System.Security.Cryptography;
using System.Text;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Links;

/// <summary>
/// Names that keep one CA Tracker running per data folder: the mutex a running copy holds, and the pipe it
/// listens on for a second start (a clicked link, or the app started again) to hand over. A copy run with
/// <c>--data-folder</c> has names of its own, so it neither blocks nor receives the usual copy's links.
/// </summary>
public static class InstanceNames
{
    /// <summary>
    /// <c>Local\SMC-CA-Tracker</c> (per Windows session), or with the data folder's hash for a copy run
    /// with <c>--data-folder</c>. Unchanged from earlier versions, so an old and a new copy still exclude each other.
    /// </summary>
    public static string Mutex(AppPaths paths) =>
        paths.IsDefault ? @"Local\SMC-CA-Tracker" : $@"Local\SMC-CA-Tracker-{FolderKey(paths)}";

    /// <summary>
    /// The pipe the running copy listens on. Pipe names are shared by everyone on the PC, so the name holds
    /// the Windows user (a hash of their SID) and session as the mutex's "Local\" does, plus the data
    /// folder's hash for a <c>--data-folder</c> copy. The pipe itself only lets the same user connect.
    /// </summary>
    public static string Pipe(AppPaths paths, string userSid, int sessionId)
    {
        var user = Hash(userSid)[..16];
        var name = $"SMC-CA-Tracker-{sessionId}-{user}";
        return paths.IsDefault ? name : $"{name}-{FolderKey(paths)}";
    }

    private static string FolderKey(AppPaths paths) => Hash(paths.Root.ToLowerInvariant())[..16];

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
