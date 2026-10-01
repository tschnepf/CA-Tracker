using System.Security.Cryptography;
using System.Text;
using SmcCaTracker.Core.Procore;

namespace SmcCaTracker.App.Services;

/// <summary>Encrypts secrets with Windows DPAPI: only this Windows user on this PC can read them back.</summary>
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SMC-CA-Tracker/procore");

    public string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        {
            // Saved by another Windows user or PC: it has to be entered again.
            return null;
        }
    }
}
