using System.Text.Json;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.Core.Storage;

/// <summary>A site the built-in browser has been signed in to.</summary>
public sealed class ProviderAccount
{
    public string Provider { get; set; } = "";
    public DateTimeOffset SignedInAt { get; set; }
    public string? UserName { get; set; }
    public string? Company { get; set; }

    /// <summary>The signed-in person's id on the site (Procore user id, Helix contact id), when known.</summary>
    public string? ProviderUserId { get; set; }

    /// <summary>The signed-in person's email on the site, when known.</summary>
    public string? Email { get; set; }

    /// <summary>The last background read found the sign-in expired (Connect again to fix it).</summary>
    public bool NeedsSignIn { get; set; }
}

/// <summary>
/// Remembers when the built-in browser was last seen signed in to Helix or Procore, for the
/// Connect to Helix / Connect to Procore buttons. The sign-in itself lives in the browser profile;
/// this is only what the app shows. Signing out clears it.
/// </summary>
public sealed class AccountStore
{
    private readonly AppPaths paths;

    public AccountStore(AppPaths paths)
    {
        this.paths = paths;
    }

    public ProviderAccount? Get(string provider) =>
        Load().FirstOrDefault(account => string.Equals(account.Provider, provider, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ProviderAccount> Load()
    {
        try
        {
            var json = LocalFiles.ReadAllTextWithRetry(paths.AccountsFile);
            return json is null
                ? Array.Empty<ProviderAccount>()
                : JsonSerializer.Deserialize<List<ProviderAccount>>(json, TrackerJson.Options) ?? new List<ProviderAccount>();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return Array.Empty<ProviderAccount>();
        }
    }

    /// <summary>Records a sign-in. A missing name keeps the one seen before.</summary>
    public void Record(string provider, string? userName, string? company, DateTimeOffset now)
    {
        if (!Providers.All.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        provider = provider.ToLowerInvariant();

        var accounts = Load()
            .Where(account => !string.Equals(account.Provider, provider, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var previous = Get(provider);
        accounts.Add(new ProviderAccount
        {
            Provider = provider,
            SignedInAt = now,
            UserName = string.IsNullOrWhiteSpace(userName) ? previous?.UserName : userName.Trim(),
            Company = string.IsNullOrWhiteSpace(company) ? previous?.Company : company.Trim()
        });
        try
        {
            LocalFiles.WriteAllTextAtomic(paths.AccountsFile, JsonSerializer.Serialize(accounts, TrackerJson.Options));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Clear() => LocalFiles.TryDelete(paths.AccountsFile);
}
