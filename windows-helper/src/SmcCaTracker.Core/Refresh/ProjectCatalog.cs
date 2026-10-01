using System.Text.Json;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Refresh;

/// <summary>A Helix or Procore project seen during a refresh, offered when connecting a tracker project.</summary>
public sealed class CatalogProject
{
    public string Provider { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string? AccountId { get; set; }
    public string? AccountName { get; set; }
    public string Name { get; set; } = "";
    public string? Host { get; set; }
    public DateTimeOffset SeenAt { get; set; }

    public string Key => $"{Provider}|{AccountId}|{ProjectId}";

    public ProviderConnection ToConnection() => new()
    {
        Provider = Provider,
        ProjectId = ProjectId,
        AccountId = AccountId,
        ProjectName = Name,
        Host = Host
    };
}

/// <summary>The list of projects the user can see, as read during a refresh.</summary>
public sealed class CatalogMessage
{
    public string Provider { get; set; } = "";
    public List<CatalogProject> Projects { get; set; } = new();
}

public sealed class ProjectCatalogStore
{
    private const int MaxProjects = 5000;
    private readonly AppPaths paths;

    public ProjectCatalogStore(AppPaths paths)
    {
        this.paths = paths;
    }

    public IReadOnlyList<CatalogProject> Load()
    {
        try
        {
            var json = LocalFiles.ReadAllTextWithRetry(paths.CatalogFile);
            return json is null
                ? Array.Empty<CatalogProject>()
                : JsonSerializer.Deserialize<List<CatalogProject>>(json, TrackerJson.Options) ?? new List<CatalogProject>();
        }
        catch (JsonException)
        {
            return Array.Empty<CatalogProject>();
        }
    }

    public void Merge(CatalogMessage? message, DateTimeOffset now)
    {
        if (message is null || !Providers.All.Contains(message.Provider))
        {
            return;
        }

        var byKey = Load().ToDictionary(project => project.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var project in message.Projects.Take(MaxProjects))
        {
            if (string.IsNullOrWhiteSpace(project.ProjectId) || project.ProjectId.Length > 120 || string.IsNullOrWhiteSpace(project.Name))
            {
                continue;
            }

            project.Provider = message.Provider;
            project.SeenAt = now;
            byKey[project.Key] = project;
        }

        var merged = byKey.Values
            .OrderByDescending(project => project.SeenAt)
            .Take(MaxProjects)
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        LocalFiles.WriteAllTextAtomic(paths.CatalogFile, JsonSerializer.Serialize(merged, TrackerJson.Options));
    }

    /// <summary>The catalog plus any project that has sent a snapshot, newest first per project.</summary>
    public IReadOnlyList<CatalogProject> WithSnapshots(IEnumerable<ProviderSnapshot> snapshots)
    {
        var byKey = Load()
            .GroupBy(project => $"{project.Provider}|{project.ProjectId}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(project => project.SeenAt).First(), StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            var key = $"{snapshot.Provider}|{snapshot.ProjectId}";
            if (!byKey.ContainsKey(key))
            {
                byKey[key] = new CatalogProject
                {
                    Provider = snapshot.Provider,
                    ProjectId = snapshot.ProjectId,
                    AccountId = snapshot.AccountId,
                    Name = snapshot.ProjectName,
                    SeenAt = snapshot.ScannedAt
                };
            }
        }

        return byKey.Values.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
