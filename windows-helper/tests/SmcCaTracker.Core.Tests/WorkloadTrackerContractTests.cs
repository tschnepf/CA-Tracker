using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SmcCaTracker.ContractGen;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.Tests;

/// <summary>
/// The pinned Workload Tracker schema, read as JSON Schema so request bodies can be checked against it.
/// OpenAPI 3.0's <c>nullable</c> becomes a null alternative, and objects refuse properties the schema
/// does not list, so a misspelt or extra field fails here instead of being ignored by the tracker.
/// </summary>
internal static class PinnedSchema
{
    private static readonly ConcurrentDictionary<string, JsonSchema> Built = new();

    public static string WindowsHelper { get; } = ContractFiles.FindWindowsHelper(AppContext.BaseDirectory)
        ?? throw new InvalidOperationException("Could not find windows-helper from the test output folder.");

    public static JsonObject Document { get; } =
        JsonNode.Parse(File.ReadAllText(ContractFiles.PinnedSchema(WindowsHelper)))!.AsObject();

    public static IReadOnlyList<string> Problems(string component, string json)
    {
        var schema = Built.GetOrAdd(component, name => JsonSchema.FromText(AsJsonSchema(name).ToJsonString()));
        using var instance = JsonDocument.Parse(json);
        var result = schema.Evaluate(instance.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
            RequireFormatValidation = true
        });
        if (result.IsValid)
        {
            return [];
        }

        return (result.Details ?? [])
            .Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"))
            .DefaultIfEmpty("invalid")
            .ToList();
    }

    private static JsonObject AsJsonSchema(string component)
    {
        var defs = new JsonObject();
        foreach (var (name, node) in Document["components"]!["schemas"]!.AsObject())
        {
            defs[name] = Convert(node!.DeepClone());
        }

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$defs"] = defs,
            ["$ref"] = $"#/$defs/{component}"
        };
    }

    private static JsonNode Convert(JsonNode node)
    {
        switch (node)
        {
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    array[index] = array[index] is { } item ? Convert(item.DeepClone()) : null;
                }

                return array;
            case JsonObject obj:
                foreach (var key in obj.Select(entry => entry.Key).ToList())
                {
                    if (key == "$ref")
                    {
                        obj[key] = obj[key]!.GetValue<string>().Replace("#/components/schemas/", "#/$defs/", StringComparison.Ordinal);
                    }
                    else if (obj[key] is { } child && key != "enum" && key != "required")
                    {
                        obj[key] = Convert(child.DeepClone());
                    }
                }

                if (obj["properties"] is JsonObject && obj["additionalProperties"] is null)
                {
                    obj["additionalProperties"] = false;
                }

                if (obj["nullable"]?.GetValue<bool>() == true)
                {
                    obj.Remove("nullable");
                    if (obj["type"] is JsonValue type)
                    {
                        obj["type"] = new JsonArray(type.GetValue<string>(), "null");
                    }
                    else
                    {
                        return new JsonObject { ["anyOf"] = new JsonArray(obj, new JsonObject { ["type"] = "null" }) };
                    }
                }

                return obj;
            default:
                return node;
        }
    }
}

public class WorkloadTrackerContractTests
{
    private static IEnumerable<string> SampleNames() =>
        Directory.GetFiles(ContractFiles.Samples(PinnedSchema.WindowsHelper), "*.json").Select(Path.GetFileName).OfType<string>().Order();

    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var name in SampleNames())
        {
            data.Add(name);
        }

        return data;
    }

    private static string Sample(string name) =>
        File.ReadAllText(Path.Combine(ContractFiles.Samples(PinnedSchema.WindowsHelper), name));

    private static string ComponentFor(string sample) =>
        sample.StartsWith("observation-", StringComparison.Ordinal) ? "CaObservationPartRequest"
        : sample.StartsWith("assignments-", StringComparison.Ordinal) ? "CaAssignmentBatchRequest"
        : throw new InvalidOperationException($"No component for sample {sample}.");

    [Fact]
    public void TheGeneratedDtosMatchThePinnedSchema()
    {
        var pinned = File.ReadAllText(ContractFiles.PinnedSchema(PinnedSchema.WindowsHelper));
        var committed = File.ReadAllText(ContractFiles.GeneratedDtos(PinnedSchema.WindowsHelper)).ReplaceLineEndings("\n");

        Assert.True(
            ContractGenerator.GenerateCSharp(pinned) == committed,
            "Contract.g.cs is out of date: run SmcCaTracker.ContractGen generate (see contract/README.md).");
    }

    [Fact]
    public void TheGeneralReadsThatArePinnedStillHaveTheFieldsTheAppReads()
    {
        // Project search and a project's team are read field by field (their rows are large and loosely
        // typed); these are the fields WorkloadTrackerApi reads. A rename in the tracker fails here.
        var paths = PinnedSchema.Document["paths"]!.AsObject();
        Assert.NotNull(paths["/api/projects/search/"]?["post"]);
        Assert.Null(paths["/api/projects/search/"]?["get"]);
        Assert.NotNull(paths["/api/assignments/"]?["get"]);
        Assert.Null(paths["/api/assignments/"]?["post"]);

        var schemas = PinnedSchema.Document["components"]!["schemas"]!.AsObject();
        Assert.Subset(
            schemas["Assignment"]!["properties"]!.AsObject().Select(entry => entry.Key).ToHashSet(),
            new HashSet<string> { "person", "personName", "personDepartmentName", "roleName" });
        Assert.Subset(
            schemas["ProjectSearchResponse"]!["properties"]!.AsObject().Select(entry => entry.Key).ToHashSet(),
            new HashSet<string> { "results", "next" });
        Assert.Subset(
            schemas["PaginatedAssignmentList"]!["properties"]!.AsObject().Select(entry => entry.Key).ToHashSet(),
            new HashSet<string> { "results", "next" });
    }

    [Fact]
    public void ThePinnedSchemaHasTheCaEndpointsAndSaysWhereItCameFrom()
    {
        var paths = PinnedSchema.Document["paths"]!.AsObject();
        Assert.NotNull(paths["/api/ca/observations/"]?["post"]);
        Assert.NotNull(paths["/api/ca/assignments/"]?["post"]);
        Assert.NotNull(paths["/api/ca/assignments/"]?["get"]);
        Assert.False(string.IsNullOrWhiteSpace(PinnedSchema.Document["info"]?["x-source"]?["commit"]?.GetValue<string>()));
        Assert.Equal(PinnedContract.TrackerCommit, PinnedSchema.Document["info"]!["x-source"]!["commit"]!.GetValue<string>());
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void TheSampleBodiesMatchThePinnedSchema(string sample)
    {
        Assert.Empty(PinnedSchema.Problems(ComponentFor(sample), Sample(sample)));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void TheSampleBodiesRoundTripThroughTheDtos(string sample)
    {
        var json = Sample(sample);
        var type = ComponentFor(sample) == "CaObservationPartRequest" ? typeof(CaObservationPartRequest) : typeof(CaAssignmentBatchRequest);

        var dto = JsonSerializer.Deserialize(json, type, ContractJson.Options);
        var written = JsonSerializer.Serialize(dto, type, ContractJson.Options);

        Assert.True(
            JsonNode.DeepEquals(Canonical(JsonNode.Parse(json)), Canonical(JsonNode.Parse(written))),
            $"{sample} changed on the way through the DTOs:\n{written}");
        Assert.Empty(PinnedSchema.Problems(ComponentFor(sample), written));
    }

    [Theory]
    [InlineData("an unknown field", "$.extra", "1")]
    [InlineData("a missing field", "$.collector", null)]
    [InlineData("an unknown scope tag", "$.items[0].scopeTag", "\"core_and_shell\"")]
    [InlineData("a null where none is allowed", "$.items[0].title", "null")]
    [InlineData("a day in the wrong form", "$.items[0].dueDate", "\"10/09/2026\"")]
    [InlineData("a part number of zero", "$.part", "0")]
    public void ThePinnedSchemaRefusesWhatTheTrackerWouldRefuse(string what, string path, string? value)
    {
        var body = JsonNode.Parse(Sample("observation-procore-items.json"))!.AsObject();
        var segments = path[2..].Split('.');
        JsonObject target = body;
        foreach (var segment in segments[..^1])
        {
            target = segment.EndsWith("[0]", StringComparison.Ordinal)
                ? target[segment[..^3]]![0]!.AsObject()
                : target[segment]!.AsObject();
        }

        if (value is null)
        {
            target.Remove(segments[^1]);
        }
        else
        {
            target[segments[^1]] = JsonNode.Parse(value);
        }

        Assert.True(PinnedSchema.Problems("CaObservationPartRequest", body.ToJsonString()).Count > 0, $"{what} was accepted");
    }

    [Fact]
    public void ThePinnedSchemaRefusesMoreThan500ItemsInAPart()
    {
        var body = JsonNode.Parse(Sample("observation-procore-items.json"))!.AsObject();
        var item = body["items"]![0]!;
        body["items"] = new JsonArray(Enumerable.Range(0, 501).Select(_ => item.DeepClone()).ToArray());

        Assert.NotEmpty(PinnedSchema.Problems("CaObservationPartRequest", body.ToJsonString()));
    }

    [Fact]
    public void TheSamplesUseThisBuildsSchemaVersion()
    {
        foreach (var sample in SampleNames().Where(name => name.StartsWith("observation-", StringComparison.Ordinal)))
        {
            Assert.Equal(ObservationBuilder.SchemaVersion, JsonNode.Parse(Sample(sample))!["schemaVersion"]!.GetValue<string>());
        }
    }

    [Fact]
    public void ACompleteObservationIsSplitIntoPartsThatMatchTheSchema()
    {
        var snapshot = Snapshot(rfis: 700, submittals: 503);
        var project = ObservationBuilder.ProviderProject(snapshot, new ProviderConnection { Provider = "procore", ProjectId = "42", Host = "US02.procore.com" }, "Acme");
        var bindings = new List<CaBindingAssertionRequest>
        {
            new() { TrackerProjectId = 7, Scope = CaBindingScopeEnum.BaseBuild, IncludeUnlabeled = true }
        };
        var id = Guid.NewGuid();

        var parts = ObservationBuilder.Complete(snapshot, project, bindings, new ObservationSender(Guid.NewGuid(), "3.4.0"), id);

        Assert.Equal([500, 500, 203], parts.Select(part => part.Items.Count));
        Assert.All(parts, part =>
        {
            Assert.Equal(id, part.ObservationId);
            Assert.Equal(3, part.TotalParts);
            Assert.Equal("us02.procore.com", part.ProviderProject.Host);
            Assert.Equal([700, 503], part.Coverage.Select(entry => entry.ItemCount));
            Assert.All(part.Coverage, entry => Assert.True(entry.FullSet));
            Assert.Empty(PinnedSchema.Problems("CaObservationPartRequest", JsonSerializer.Serialize(part, ContractJson.Options)));
        });
        Assert.Equal(1203, parts.SelectMany(part => part.Items).Select(item => (item.ItemType, item.ProviderItemId)).Distinct().Count());
    }

    [Fact]
    public void APartIsClosedBeforeFourMegabytesWhateverItsItemCount()
    {
        // Items as large as the contract allows: 50 holders with long names, companies and emails each.
        var snapshot = Snapshot(rfis: 600, submittals: 0);
        foreach (var item in snapshot.Items)
        {
            item.Title = new string('T', 500);
            item.SourceUrl = $"https://app.procore.com/42/project/rfi/show/{item.ItemId}?{new string('q', 1900)}";
            item.Holders = Enumerable.Range(0, 50).Select(n => new SnapshotHolder
            {
                Name = $"{n} {new string('N', 190)}",
                Company = new string('C', 200),
                CompanyId = new string('I', 64),
                ProviderUserId = $"{n}-{new string('U', 60)}",
                Email = $"{n}{new string('e', 230)}@example.com",
                State = "current",
                Since = snapshot.ScannedAt
            }).ToList();
        }

        var project = ObservationBuilder.ProviderProject(snapshot, new ProviderConnection { Provider = "procore", ProjectId = "42" }, "Acme");
        var parts = ObservationBuilder.Complete(snapshot, project, [], new ObservationSender(Guid.NewGuid(), "3.5.0"), Guid.NewGuid());
        var bodies = parts.Select(part => JsonSerializer.SerializeToUtf8Bytes(part, ContractJson.Options)).ToList();

        Assert.True(parts.Count > 2, $"{parts.Count} parts");
        Assert.All(bodies, body => Assert.InRange(body.Length, 1, ObservationBuilder.MaxPartBytes));
        Assert.All(parts, part => Assert.Equal(parts.Count, part.TotalParts));
        Assert.Equal(600, parts.Sum(part => part.Items.Count));
        Assert.Empty(PinnedSchema.Problems("CaObservationPartRequest", System.Text.Encoding.UTF8.GetString(bodies[0])));
    }

    [Fact]
    public void ItemsAreCutToTheContractAndBadHoldersAreLeftOut()
    {
        var item = new SnapshotItem
        {
            ItemType = "RFI",
            ItemId = "1",
            Number = "BB-001",
            Title = new string('x', 499) + "😀",
            Status = "Open",
            Holders =
            [
                new SnapshotHolder { Name = "Pat Example", Company = "Acme", State = "current" },
                new SnapshotHolder { Name = "", State = "current" },
                new SnapshotHolder { Name = "Lee Example", State = "past" }
            ],
            SourceUrl = "http://app.procore.com/1/project/rfi/show/1"
        };

        var built = ObservationBuilder.Item(item);

        Assert.Equal("rfi", built.ItemType);
        Assert.Equal(499, built.Title.Length);
        Assert.Equal(CaItemScopeTagEnum.BaseBuild, built.ScopeTag);
        Assert.Equal(["Pat Example"], built.Holders.Select(holder => holder.Name));
        Assert.Null(built.SourceUrl);
    }

    [Theory]
    [InlineData("BB-RFI-001", "", "", CaItemScopeTagEnum.BaseBuild)]
    [InlineData("017", "Tenant fit out: level 3 ceiling grid", "", CaItemScopeTagEnum.TenantFitOut)]
    [InlineData("018", "Ceiling grid", "TFO package", CaItemScopeTagEnum.TenantFitOut)]
    [InlineData("019", "Ceiling grid", "", CaItemScopeTagEnum.Unlabeled)]
    public void TheScopeTagFollowsTheAppsBaseBuildAndFitOutDetection(string number, string title, string contract, string expected)
    {
        Assert.Equal(expected, ObservationBuilder.ScopeTag(new SnapshotItem { Number = number, Title = title, Contract = contract }));
    }

    [Fact]
    public void DueDatesThatWerePlainDaysAreSentAsDays()
    {
        var helixDay = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        var localMidnight = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Unspecified);
        var procoreDay = new DateTimeOffset(localMidnight, TimeZoneInfo.Local.GetUtcOffset(localMidnight)).ToUniversalTime();
        var instant = new DateTimeOffset(2026, 10, 9, 17, 30, 15, TimeSpan.FromHours(-4));

        Assert.Equal("2026-10-09", ObservationBuilder.ProviderDate(helixDay));
        Assert.Equal("2026-10-10", ObservationBuilder.ProviderDate(procoreDay));
        Assert.Equal("2026-10-09T21:30:15Z", ObservationBuilder.ProviderDate(instant));
        Assert.Null(ObservationBuilder.ProviderDate(null));
    }

    [Fact]
    public void ACatalogObservationCarriesCountsOnly()
    {
        var snapshot = Snapshot(rfis: 3, submittals: 0);
        var project = ObservationBuilder.ProviderProject(snapshot, null, null);

        var catalog = ObservationBuilder.Catalog(snapshot, project, new ObservationSender(Guid.NewGuid(), "3.4.0"), Guid.NewGuid());

        Assert.Equal(CaObservationKindEnum.Catalog, catalog.ObservationKind);
        Assert.Empty(catalog.Items);
        Assert.Equal([3, 0], catalog.Coverage.Select(entry => entry.ItemCount));
        Assert.All(catalog.Coverage, entry => Assert.False(entry.FullSet));
        Assert.Empty(PinnedSchema.Problems("CaObservationPartRequest", JsonSerializer.Serialize(catalog, ContractJson.Options)));
    }

    [Fact]
    public void ExportedFixturesAreAnonymizedAndMatchTheSchema()
    {
        var tracked = Snapshot(rfis: 4, submittals: 2, provider: "helix", projectId: "H-77", projectName: "Riverside Data Center");
        var untracked = Snapshot(rfis: 2, submittals: 1, provider: "procore", projectId: "562949953", projectName: "Harbor Logistics");
        var projects = new List<TrackerProject>
        {
            new() { Name = "Riverside", Connection = new ProviderConnection { Provider = "helix", ProjectId = "H-77" } }
        };

        var files = FixtureExport.Build([untracked, tracked], projects, "3.4.0");
        var again = FixtureExport.Build([untracked, tracked], projects, "3.4.0");

        Assert.Equal(["observation-export-helix-1-part-1-of-1.json", "observation-export-procore-1-catalog.json"], files.Select(file => file.Name));
        Assert.Equal(files, again);
        foreach (var file in files)
        {
            Assert.Empty(PinnedSchema.Problems("CaObservationPartRequest", file.Json));
            foreach (var secret in new[] { "Pat Example", "pat@acme.example", "Acme Mechanical", "Leak at the riser", "Riverside", "Harbor", "H-77", "562949953", "acme-user", "vendor-9" })
            {
                Assert.DoesNotContain(secret, file.Json, StringComparison.OrdinalIgnoreCase);
            }
        }

        var items = JsonSerializer.Deserialize<CaObservationPartRequest>(files[0].Json, ContractJson.Options)!;
        Assert.Equal(6, items.Items.Count);
        Assert.All(items.Items, item => Assert.Equal($"Item {item.Number}", item.Title));
        Assert.Equal("Person 1", items.Collector.Name);
        Assert.Contains(items.Items.SelectMany(item => item.Holders), holder => holder.Name == "Person 1" && holder.Company == "Company A");
    }

    [Fact]
    public void EmailsAndProviderIdsAreReplacedToo()
    {
        var part = JsonSerializer.Deserialize<CaObservationPartRequest>(Sample("observation-procore-items.json"), ContractJson.Options)!;
        part = part with
        {
            Collector = part.Collector with { Email = "pat@acme.example", ProviderUserId = "8812345" },
            Items = part.Items.Select(item => item with
            {
                Holders = item.Holders.Select(holder => holder with { Email = "lee@vendor.example", ProviderUserId = "99001" }).ToList()
            }).ToList()
        };

        var json = JsonSerializer.Serialize(new FixtureAnonymizer().Observation(part), ContractJson.Options);

        foreach (var secret in new[] { "acme.example", "vendor.example", "8812345", "99001" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        Assert.Contains("person-1@example.invalid", json, StringComparison.Ordinal);
        Assert.Empty(PinnedSchema.Problems("CaObservationPartRequest", json));
    }

    [Fact]
    public void LargeProjectsAreSampledAndThenNotSentAsAFullSet()
    {
        var snapshot = Snapshot(rfis: 650, submittals: 50);
        var projects = new List<TrackerProject> { new() { Connection = new ProviderConnection { Provider = "procore", ProjectId = snapshot.ProjectId } } };

        var files = FixtureExport.Build([snapshot], projects, "3.4.0", maxItemsPerProject: 600);

        var parts = files.Select(file => JsonSerializer.Deserialize<CaObservationPartRequest>(file.Json, ContractJson.Options)!).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal(600, parts.Sum(part => part.Items.Count));
        Assert.All(parts, part => Assert.All(part.Coverage, entry => Assert.False(entry.FullSet)));
        Assert.Equal([650, 50], parts[0].Coverage.Select(entry => entry.ItemCount));
    }

    private static ProviderSnapshot Snapshot(int rfis, int submittals, string provider = "procore", string projectId = "42", string projectName = "Tower")
    {
        var scanned = new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.Zero);
        var items = new List<SnapshotItem>();
        for (var n = 1; n <= rfis + submittals; n++)
        {
            var type = n <= rfis ? "rfi" : "submittal";
            items.Add(new SnapshotItem
            {
                ItemType = type,
                ItemId = $"{n}",
                Number = n % 3 == 0 ? $"BB-{n:D3}" : $"{n:D3}",
                Revision = type == "submittal" ? "1" : null,
                Title = "Leak at the riser",
                Status = n % 2 == 0 ? "Closed" : "Open",
                Closed = n % 2 == 0,
                Contract = "Acme Mechanical",
                CreatedAt = scanned.AddDays(-n),
                DueAt = n % 4 == 0 ? null : scanned.Date.AddDays(n % 9),
                SourceUrl = provider == "procore" ? $"https://app.procore.com/{projectId}/project/rfi/show/{n}" : $"https://project.helixteam.com/Projects/{projectId}/RFI",
                Holders =
                [
                    new SnapshotHolder { Name = "Pat Example", Company = "Acme Mechanical", CompanyId = "vendor-9", State = "current", Since = scanned.AddDays(-1) },
                    new SnapshotHolder { Name = "Robin Sample", Company = "SMC", CompanyId = "smc-1", State = "upcoming" }
                ]
            });
        }

        return new ProviderSnapshot
        {
            Provider = provider,
            AccountId = provider == "procore" ? "acme-user" : null,
            ProjectId = projectId,
            ProjectName = projectName,
            ItemTypes = ["rfi", "submittal"],
            Complete = true,
            ScannedAt = scanned,
            User = new SnapshotUser { FullName = "Pat Example", Company = "Acme Mechanical", CompanyId = "vendor-9" },
            Items = items,
            Coverage = new Dictionary<string, TypeCoverage>
            {
                ["rfi"] = new() { ScannedAt = scanned, Complete = true },
                ["submittal"] = new() { ScannedAt = scanned.AddMinutes(-1), Complete = true }
            }
        };
    }

    /// <summary>Nulls dropped (an absent optional field reads back as null) and date-times compared as instants.</summary>
    private static JsonNode? Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    if (value is not null)
                    {
                        copy[key] = Canonical(value);
                    }
                }

                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(Canonical).ToArray());
            case JsonValue value when value.TryGetValue<string>(out var text)
                && text.Contains('T')
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant):
                return JsonValue.Create(instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            default:
                return node?.DeepClone();
        }
    }
}
