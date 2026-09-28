using System.Text;
using System.Text.Json;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.Core.Storage;

/// <summary>
/// Stores the latest provider snapshot per project in %LOCALAPPDATA%\SMC-CA-Tracker\snapshots.
/// Written by the native messaging host, read by the app.
/// </summary>
public sealed class SnapshotStore
{
    public const int MaxItems = 5000;
    private readonly AppPaths paths;

    public SnapshotStore(AppPaths paths)
    {
        this.paths = paths;
    }

    public string Directory => paths.SnapshotsDirectory;

    public static string FileNameFor(string provider, string projectId)
    {
        var builder = new StringBuilder();
        foreach (var character in projectId)
        {
            builder.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_');
        }

        return $"{provider.ToLowerInvariant()}_{builder}.json";
    }

    public string PathFor(string provider, string projectId) =>
        Path.Combine(paths.SnapshotsDirectory, FileNameFor(provider, projectId));

    public static void Validate(ProviderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Providers.All.Contains(snapshot.Provider))
        {
            throw new InvalidDataException("Unsupported snapshot provider.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.ProjectId) || snapshot.ProjectId.Length > 120)
        {
            throw new InvalidDataException("Snapshots require a project identifier.");
        }

        if (snapshot.ItemTypes.Count == 0 || snapshot.ItemTypes.Any(type => !ItemTypes.All.Contains(type)))
        {
            throw new InvalidDataException("Snapshots must cover RFIs, Submittals, or both.");
        }

        if (snapshot.Items.Count > MaxItems)
        {
            throw new InvalidDataException("The snapshot contains too many items.");
        }

        if (snapshot.Items.Any(item => !snapshot.ItemTypes.Contains(item.ItemType) || string.IsNullOrWhiteSpace(item.ItemId)))
        {
            throw new InvalidDataException("Every snapshot item needs an id and a covered item type.");
        }
    }

    /// <summary>
    /// Combines a new scan with what was stored before. A complete scan replaces the item
    /// types it covers; a partial scan (Procore's visible grid) only adds or updates rows,
    /// because a row missing from a filtered grid has not necessarily gone away.
    /// </summary>
    public static ProviderSnapshot Merge(ProviderSnapshot? existing, ProviderSnapshot incoming)
    {
        var coveredTypes = incoming.ItemTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new ProviderSnapshot
        {
            SchemaVersion = incoming.SchemaVersion,
            Provider = incoming.Provider,
            AccountId = incoming.AccountId ?? existing?.AccountId,
            ProjectId = incoming.ProjectId,
            ProjectName = string.IsNullOrWhiteSpace(incoming.ProjectName) ? existing?.ProjectName ?? incoming.ProjectId : incoming.ProjectName,
            ProjectUrl = incoming.ProjectUrl ?? existing?.ProjectUrl,
            User = string.IsNullOrWhiteSpace(incoming.User.FullName) && existing is not null ? existing.User : incoming.User,
            Coverage = existing is null
                ? new Dictionary<string, TypeCoverage>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, TypeCoverage>(existing.Coverage, StringComparer.OrdinalIgnoreCase)
        };

        var items = new List<SnapshotItem>();
        var partialByKey = new Dictionary<string, SnapshotItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing?.Items ?? new List<SnapshotItem>())
        {
            if (!coveredTypes.Contains(item.ItemType, StringComparer.OrdinalIgnoreCase))
            {
                items.Add(item);
            }
            else if (!incoming.Complete)
            {
                partialByKey[$"{item.ItemType}|{item.ItemId}"] = item;
            }
        }

        if (incoming.Complete)
        {
            items.AddRange(incoming.Items);
        }
        else
        {
            foreach (var item in incoming.Items)
            {
                partialByKey[$"{item.ItemType}|{item.ItemId}"] = item;
            }

            items.AddRange(partialByKey.Values);
        }

        foreach (var type in coveredTypes)
        {
            result.Coverage[type] = new TypeCoverage { ScannedAt = incoming.ScannedAt, Complete = incoming.Complete };
        }

        result.Items = items;
        result.ItemTypes = (existing?.ItemTypes ?? new List<string>())
            .Concat(coveredTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.Complete = result.ItemTypes.All(type => result.Coverage.TryGetValue(type, out var c) && c.Complete);
        result.ScannedAt = existing is null || incoming.ScannedAt > existing.ScannedAt ? incoming.ScannedAt : existing.ScannedAt;
        return result;
    }

    public ProviderSnapshot Save(ProviderSnapshot incoming)
    {
        Validate(incoming);
        var merged = Merge(Load(incoming.Provider, incoming.ProjectId), incoming);
        LocalFiles.WriteAllTextAtomic(
            PathFor(merged.Provider, merged.ProjectId),
            JsonSerializer.Serialize(merged, TrackerJson.Options));
        return merged;
    }

    public ProviderSnapshot? Load(string provider, string projectId) => LoadFile(PathFor(provider, projectId));

    public IReadOnlyList<ProviderSnapshot> LoadAll()
    {
        if (!System.IO.Directory.Exists(paths.SnapshotsDirectory))
        {
            return Array.Empty<ProviderSnapshot>();
        }

        return System.IO.Directory.EnumerateFiles(paths.SnapshotsDirectory, "*.json")
            .Select(LoadFile)
            .Where(snapshot => snapshot is not null)
            .Select(snapshot => snapshot!)
            .ToList();
    }

    private static ProviderSnapshot? LoadFile(string path)
    {
        try
        {
            var json = LocalFiles.ReadAllTextWithRetry(path);
            return json is null ? null : JsonSerializer.Deserialize<ProviderSnapshot>(json, TrackerJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
