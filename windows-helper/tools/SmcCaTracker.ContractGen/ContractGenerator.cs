using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmcCaTracker.ContractGen;

/// <summary>
/// Pins the part of Workload Tracker's OpenAPI schema the desktop uses, and generates the desktop's
/// DTOs from the pinned copy. Workload Tracker owns the contract (decision D1); the desktop conforms.
/// </summary>
public static class ContractGenerator
{
    /// <summary>Path prefixes of the tracker endpoints the desktop calls. Add the existing reads when a slice starts using them.</summary>
    public static readonly IReadOnlyList<string> PinnedPathPrefixes =
    [
        "/api/ca/",
        "/api/auth/me/",
        "/api/auth/sessions/{session_id}/revoke/"
    ];

    /// <summary>
    /// Single operations of the tracker's general API the desktop reads, as "METHOD /path/" (exact path).
    /// Only that method is pinned: project search for binding (D3) and a project's team for assigning (D4).
    /// </summary>
    public static readonly IReadOnlyList<string> PinnedOperations =
    [
        "POST /api/projects/search/",
        "GET /api/assignments/"
    ];

    public const string Namespace = "SmcCaTracker.Core.WorkloadTracker.Contract";

    private const string ComponentPrefix = "#/components/schemas/";

    private static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The CA paths of the tracker's schema and every component they use, with the tracker commit.</summary>
    public static string Pin(string trackerSchemaJson, string trackerCommit)
    {
        var source = JsonNode.Parse(trackerSchemaJson)?.AsObject()
            ?? throw new InvalidDataException("The tracker schema is empty.");

        var paths = new JsonObject();
        foreach (var (path, item) in source["paths"]!.AsObject().OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (PinnedPathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
            {
                paths[path] = item!.DeepClone();
                continue;
            }

            var methods = PinnedOperations
                .Select(operation => operation.Split(' ', 2))
                .Where(parts => parts[1] == path)
                .Select(parts => parts[0].ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);
            if (methods.Count > 0)
            {
                var pinnedItem = new JsonObject();
                foreach (var (key, value) in item!.AsObject())
                {
                    // Path-level keys (parameters, summary) and the pinned methods; other methods stay out.
                    if (methods.Contains(key) || key is not ("get" or "put" or "post" or "delete" or "options" or "head" or "patch" or "trace"))
                    {
                        pinnedItem[key] = value!.DeepClone();
                    }
                }

                paths[path] = pinnedItem;
            }
        }

        foreach (var operation in PinnedOperations)
        {
            var parts = operation.Split(' ', 2);
            if (paths[parts[1]]?[parts[0].ToLowerInvariant()] is null)
            {
                throw new InvalidDataException($"The tracker schema lacks {operation}.");
            }
        }

        if (paths.Count == 0)
        {
            throw new InvalidDataException("The tracker schema has no CA Tracker paths.");
        }

        var all = source["components"]!["schemas"]!.AsObject();
        var needed = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Queue<JsonNode>([paths]);
        while (pending.TryDequeue(out var node))
        {
            foreach (var name in References(node))
            {
                if (needed.Add(name))
                {
                    pending.Enqueue(all[name] ?? throw new InvalidDataException($"The tracker schema lacks component {name}."));
                }
            }
        }

        var schemas = new JsonObject();
        foreach (var name in needed)
        {
            schemas[name] = all[name]!.DeepClone();
        }

        var components = new JsonObject { ["schemas"] = schemas };
        if (source["components"]!["securitySchemes"] is { } security)
        {
            components["securitySchemes"] = security.DeepClone();
        }

        var pinned = new JsonObject
        {
            ["openapi"] = source["openapi"]!.DeepClone(),
            ["info"] = new JsonObject
            {
                ["title"] = "Workload Tracker API (CA Tracker subset)",
                ["version"] = source["info"]?["version"]?.DeepClone(),
                ["x-source"] = new JsonObject
                {
                    ["repository"] = "workload-tracker",
                    ["file"] = "backend/openapi.json",
                    ["commit"] = trackerCommit
                }
            },
            ["paths"] = paths,
            ["components"] = components
        };
        return pinned.ToJsonString(Output) + "\n";
    }

    /// <summary>C# records for every component of the pinned schema; string enums become constants.</summary>
    public static string GenerateCSharp(string pinnedJson)
    {
        var root = JsonNode.Parse(pinnedJson)?.AsObject()
            ?? throw new InvalidDataException("The pinned schema is empty.");
        var schemas = root["components"]!["schemas"]!.AsObject();
        var commit = root["info"]?["x-source"]?["commit"]?.GetValue<string>() ?? "unknown";

        var code = new StringBuilder();
        code.Append($$"""
            // <auto-generated>
            // Generated by tools/SmcCaTracker.ContractGen from contract/openapi.json.
            // Pinned from workload-tracker commit {{commit}}.
            // Do not edit: change the tracker's serializers, pin the new schema and regenerate
            // (see contract/README.md).
            // </auto-generated>
            #nullable enable
            using System.Text.Json;
            using System.Text.Json.Serialization;

            namespace {{Namespace}};

            public static class PinnedContract
            {
                public const string TrackerCommit = "{{commit}}";
            }

            """);

        foreach (var (name, node) in schemas.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var schema = node!.AsObject();
            if (IsEnum(schema))
            {
                if (name != "NullEnum")
                {
                    WriteEnum(code, name, schema);
                }
            }
            else if (schema["properties"] is JsonObject)
            {
                WriteRecord(code, name, schema, schemas);
            }
            else
            {
                throw new NotSupportedException($"Component {name} is neither a string enum nor an object.");
            }
        }

        return code.ToString().ReplaceLineEndings("\n");
    }

    private static void WriteEnum(StringBuilder code, string name, JsonObject schema)
    {
        var values = schema["enum"]!.AsArray().Select(value => value!.GetValue<string>()).ToList();
        code.Append('\n');
        WriteSummary(code, schema, "");
        code.Append($"public static class {name}\n{{\n");
        foreach (var value in values)
        {
            code.Append($"    public const string {Pascal(value)} = \"{value}\";\n");
        }

        code.Append($"\n    public static readonly IReadOnlyList<string> All = [{string.Join(", ", values.Select(Pascal))}];\n}}\n");
    }

    private static void WriteRecord(StringBuilder code, string name, JsonObject schema, JsonObject schemas)
    {
        var required = schema["required"]?.AsArray().Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        code.Append('\n');
        WriteSummary(code, schema, "");
        code.Append($"public sealed record {name}\n{{\n");
        var first = true;
        foreach (var (property, node) in schema["properties"]!.AsObject())
        {
            var definition = node!.AsObject();
            var type = TypeOf(definition, schemas, out var nullable);
            var isRequired = required.Contains(property);
            var member = Pascal(property);
            if (member == name)
            {
                member += "Value";
            }

            if (!first)
            {
                code.Append('\n');
            }

            first = false;
            WriteSummary(code, definition, "    ");
            code.Append($"    [JsonPropertyName(\"{property}\")]\n");
            if (!isRequired && !nullable)
            {
                // Optional but not nullable: leave it out rather than send null.
                code.Append("    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]\n");
            }

            var optionalMark = nullable || !isRequired ? "?" : "";
            var requiredMark = isRequired ? "required " : "";
            code.Append($"    public {requiredMark}{type}{optionalMark} {member} {{ get; init; }}\n");
        }

        code.Append("}\n");
    }

    private static string TypeOf(JsonObject definition, JsonObject schemas, out bool nullable)
    {
        nullable = definition["nullable"]?.GetValue<bool>() == true;

        if (definition["$ref"] is { } reference)
        {
            return Referenced(reference.GetValue<string>(), schemas);
        }

        if (definition["allOf"] is JsonArray { Count: 1 } allOf && allOf[0]?["$ref"] is { } wrapped)
        {
            return Referenced(wrapped.GetValue<string>(), schemas);
        }

        if (definition["oneOf"] is JsonArray oneOf)
        {
            var choices = oneOf.Select(choice => choice?["$ref"]?.GetValue<string>()).ToList();
            if (choices.Remove(ComponentPrefix + "NullEnum"))
            {
                nullable = true;
            }

            if (choices is [{ } single])
            {
                return Referenced(single, schemas);
            }

            throw new NotSupportedException($"Unsupported oneOf: {definition.ToJsonString()}");
        }

        var format = definition["format"]?.GetValue<string>();
        return definition["type"]?.GetValue<string>() switch
        {
            "string" when format == "date-time" => "DateTimeOffset",
            "string" when format == "uuid" => "Guid",
            "string" => "string",
            "integer" when format == "int64" => "long",
            "integer" => "int",
            "number" => "double",
            "boolean" => "bool",
            "array" => $"List<{TypeOf(definition["items"]!.AsObject(), schemas, out _)}>",
            "object" when definition["additionalProperties"] is not null => "Dictionary<string, JsonElement>",
            _ => "JsonElement"
        };
    }

    private static string Referenced(string reference, JsonObject schemas)
    {
        if (!reference.StartsWith(ComponentPrefix, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported reference {reference}.");
        }

        var name = reference[ComponentPrefix.Length..];
        var target = schemas[name]?.AsObject() ?? throw new InvalidDataException($"Missing component {name}.");
        return IsEnum(target) ? "string" : name;
    }

    private static bool IsEnum(JsonObject schema) => schema["enum"] is JsonArray;

    private static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "$ref" && value?.GetValue<string>() is { } reference && reference.StartsWith(ComponentPrefix, StringComparison.Ordinal))
                    {
                        yield return reference[ComponentPrefix.Length..];
                    }
                    else
                    {
                        foreach (var name in References(value))
                        {
                            yield return name;
                        }
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var name in References(item))
                    {
                        yield return name;
                    }
                }

                break;
        }
    }

    private static void WriteSummary(StringBuilder code, JsonObject schema, string indent)
    {
        var description = schema["description"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(description))
        {
            return;
        }

        // drf-spectacular appends "* `value` - Label" lines for choices; the constants already say that.
        var bullets = description.IndexOf("\n\n* `", StringComparison.Ordinal);
        var text = (bullets >= 0 ? description[..bullets] : description).Trim();
        if (text.StartsWith("* `", StringComparison.Ordinal) || text.Length == 0)
        {
            return;
        }

        var escaped = string.Join(' ', text.Split((char[])['\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        code.Append($"{indent}/// <summary>{escaped}</summary>\n");
    }

    private static string Pascal(string value)
    {
        var builder = new StringBuilder();
        var upper = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                upper = true;
                continue;
            }

            builder.Append(upper ? char.ToUpper(character, CultureInfo.InvariantCulture) : character);
            upper = false;
        }

        var result = builder.ToString();
        return result.Length > 0 && char.IsDigit(result[0]) ? "_" + result : result;
    }
}
