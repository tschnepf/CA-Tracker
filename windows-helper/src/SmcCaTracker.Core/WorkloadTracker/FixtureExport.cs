using System.Globalization;
using System.Text.Json;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>
/// Anonymized observations built from the app's own data, as test fixtures for Workload Tracker's
/// CA Tracker endpoints. Projects with a tracker entry become item observations (a sample of their
/// items when large); the rest become catalog observations. People, companies, emails, titles,
/// provider ids, project names and links are replaced; numbers, revisions, statuses, dates, scope
/// tags, holder states and counts are kept.
/// </summary>
public static class FixtureExport
{
    public const int DefaultMaxItemsPerProject = 600;

    public sealed record FixtureFile(string Name, string Json);

    public static IReadOnlyList<FixtureFile> Build(
        IReadOnlyList<ProviderSnapshot> snapshots,
        IReadOnlyList<TrackerProject> projects,
        string appVersion,
        int maxItemsPerProject = DefaultMaxItemsPerProject)
    {
        var anonymizer = new FixtureAnonymizer();
        var sender = new ObservationSender(new Guid("00000000-0000-4000-8000-000000000001"), appVersion);
        var files = new List<FixtureFile>();
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);

        var ordered = snapshots
            .OrderBy(snapshot => snapshot.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(snapshot => snapshot.ProjectId, StringComparer.Ordinal);
        foreach (var snapshot in ordered)
        {
            var connection = projects
                .Select(project => project.Connection)
                .FirstOrDefault(candidate => candidate is not null
                    && string.Equals(candidate.Provider, snapshot.Provider, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.ProjectId, snapshot.ProjectId, StringComparison.Ordinal));
            var project = ObservationBuilder.ProviderProject(snapshot, connection, company: null);
            var number = counters[project.Provider] = counters.GetValueOrDefault(project.Provider) + 1;
            var observationId = new Guid($"00000000-0000-4000-8000-{files.Count + 1:D12}");
            var stem = $"observation-export-{project.Provider}-{number}";

            if (connection is null)
            {
                var catalog = anonymizer.Observation(ObservationBuilder.Catalog(snapshot, project, sender, observationId));
                files.Add(new FixtureFile($"{stem}-catalog.json", Serialize(catalog)));
                continue;
            }

            var sample = Sample(snapshot, maxItemsPerProject);
            var sampled = sample.Items.Count < snapshot.Items.Count;
            var coverage = ObservationBuilder.Coverage(snapshot, fullSet: !sampled);
            var parts = ObservationBuilder.Complete(sample, project, [], sender, observationId);
            foreach (var part in parts)
            {
                var anonymized = anonymizer.Observation(part with { Coverage = coverage });
                files.Add(new FixtureFile($"{stem}-part-{part.Part}-of-{part.TotalParts}.json", Serialize(anonymized)));
            }
        }

        return files;
    }

    /// <summary>Up to <paramref name="max"/> items: open ones first, then the newest closed ones.</summary>
    private static ProviderSnapshot Sample(ProviderSnapshot snapshot, int max)
    {
        if (snapshot.Items.Count <= max)
        {
            return snapshot;
        }

        var items = snapshot.Items
            .OrderBy(item => item.Closed)
            .ThenByDescending(item => item.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .Take(max)
            .ToList();
        return new ProviderSnapshot
        {
            SchemaVersion = snapshot.SchemaVersion,
            Provider = snapshot.Provider,
            AccountId = snapshot.AccountId,
            ProjectId = snapshot.ProjectId,
            ProjectName = snapshot.ProjectName,
            ProjectUrl = snapshot.ProjectUrl,
            ItemTypes = snapshot.ItemTypes,
            Complete = snapshot.Complete,
            ScannedAt = snapshot.ScannedAt,
            User = snapshot.User,
            Items = items,
            Coverage = snapshot.Coverage
        };
    }

    private static string Serialize(CaObservationPartRequest part) =>
        JsonSerializer.Serialize(part, ContractJson.Indented) + "\n";
}

/// <summary>
/// Replaces identifying values with labels that are stable within one export: the same person, company or
/// id always gets the same label, numbered in the order first met.
/// </summary>
internal sealed class FixtureAnonymizer
{
    private readonly Dictionary<string, string> people = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> companies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> counters = new(StringComparer.Ordinal);

    public CaObservationPartRequest Observation(CaObservationPartRequest part)
    {
        var project = part.ProviderProject;
        var projectId = Id("project", $"{project.Provider}:{project.ProjectId}")!;
        return part with
        {
            ProviderProject = project with
            {
                AccountId = Id("account", project.AccountId),
                ProjectId = projectId,
                Name = $"Project {projectId["project-".Length..]}",
                Company = Company(project.Company)
            },
            Collector = part.Collector with
            {
                ProviderUserId = Id("user", part.Collector.ProviderUserId),
                Email = Email(part.Collector.Email, part.Collector.Name),
                Name = Person(part.Collector.Name),
                Company = Company(part.Collector.Company),
                CompanyId = Id("company", part.Collector.CompanyId)
            },
            Items = part.Items.Select(item => Item(item, project, projectId)).ToList(),
            Removed = part.Removed
                .Select(removed => removed with { ProviderItemId = ItemId(project, removed.ItemType, removed.ProviderItemId) })
                .ToList()
        };
    }

    private CaItemRequest Item(CaItemRequest item, CaProviderProjectRequest project, string projectId)
    {
        var itemId = ItemId(project, item.ItemType, item.ProviderItemId);
        return item with
        {
            ProviderItemId = itemId,
            Title = $"Item {item.Number}".Trim(),
            SourceUrl = item.SourceUrl is null ? null : Link(project, projectId, item.ItemType, itemId),
            Holders = item.Holders
                .Select(holder => holder with
                {
                    Email = Email(holder.Email, holder.Name),
                    Name = Person(holder.Name),
                    Company = Company(holder.Company),
                    CompanyId = Id("company", holder.CompanyId),
                    ProviderUserId = Id("user", holder.ProviderUserId)
                })
                .ToList()
        };
    }

    private string ItemId(CaProviderProjectRequest project, string itemType, string providerItemId) =>
        Id("item", $"{project.Provider}:{project.ProjectId}:{itemType}:{providerItemId}")!;

    private static string Link(CaProviderProjectRequest project, string projectId, string itemType, string itemId) =>
        project.Provider == CaProviderEnum.Procore
            ? itemType == CaItemTypeEnum.Submittal
                ? $"https://{project.Host}/{projectId}/project/submittal_logs/{itemId}"
                : $"https://{project.Host}/{projectId}/project/rfi/show/{itemId}"
            : $"https://{project.Host}/Projects/{projectId}/{(itemType == CaItemTypeEnum.Submittal ? "Submittals" : "RFI")}";

    private string Person(string name)
    {
        var key = name.Trim();
        if (key.Length == 0)
        {
            return "";
        }

        if (!people.TryGetValue(key, out var label))
        {
            label = $"Person {people.Count + 1}";
            people[key] = label;
        }

        return label;
    }

    /// <summary>An address that cannot be delivered to, tied to the person's label.</summary>
    private string? Email(string? email, string name) =>
        string.IsNullOrWhiteSpace(email)
            ? null
            : $"{(Person(name) is { Length: > 0 } label ? label : Person(email)).ToLowerInvariant().Replace(' ', '-')}@example.invalid";

    private string? Company(string? name)
    {
        var key = name?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (!companies.TryGetValue(key, out var label))
        {
            label = $"Company {Letters(companies.Count)}";
            companies[key] = label;
        }

        return label;
    }

    private string? Id(string prefix, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var key = $"{prefix}:{value}";
        if (!ids.TryGetValue(key, out var label))
        {
            var next = counters[prefix] = counters.GetValueOrDefault(prefix) + 1;
            label = $"{prefix}-{next.ToString(CultureInfo.InvariantCulture)}";
            ids[key] = label;
        }

        return label;
    }

    /// <summary>A, B, … Z, AA, AB, … like spreadsheet columns.</summary>
    private static string Letters(int index)
    {
        var letters = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            letters = (char)('A' + (n - 1) % 26) + letters;
        }

        return letters;
    }
}
