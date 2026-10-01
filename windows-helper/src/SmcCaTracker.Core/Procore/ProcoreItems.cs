using System.Globalization;
using System.Text.Json;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.Core.Procore;

/// <summary>
/// Turns Procore's REST lists of RFIs and Submittals (/rest/v1.0/projects/{id}/rfis and /submittals),
/// read either through Procore's API or through the signed-in Procore page, into a project's item
/// list. Every RFI and Submittal is read, so the list is complete (unlike reading Procore's grid).
/// Procore's field names differ a little between versions, so each value is looked for under the
/// names Procore has used.
/// </summary>
public static class ProcoreItems
{
    public const int SchemaVersion = 1;

    public static ProviderSnapshot BuildSnapshot(
        string projectId,
        string? companyId,
        string projectName,
        string host,
        IReadOnlyList<string> itemTypes,
        IEnumerable<JsonElement> rfis,
        IEnumerable<JsonElement> submittals,
        DateTimeOffset scannedAt)
    {
        var items = new List<SnapshotItem>();
        if (itemTypes.Contains(ItemTypes.Rfi, StringComparer.OrdinalIgnoreCase))
        {
            items.AddRange(rfis.Select(element => ParseRfi(element, projectId, host)).OfType<SnapshotItem>());
        }

        if (itemTypes.Contains(ItemTypes.Submittal, StringComparer.OrdinalIgnoreCase))
        {
            items.AddRange(submittals.Select(element => ParseSubmittal(element, projectId, host)).OfType<SnapshotItem>());
        }

        var types = itemTypes.Where(type => ItemTypes.All.Contains(type, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var snapshot = new ProviderSnapshot
        {
            SchemaVersion = SchemaVersion,
            Provider = Providers.Procore,
            AccountId = companyId,
            ProjectId = projectId,
            ProjectName = projectName,
            ProjectUrl = $"https://{host}/{projectId}/project/home",
            ItemTypes = types,
            Complete = true,
            ScannedAt = scannedAt,
            Items = items
                .GroupBy(item => $"{item.ItemType}|{item.ItemId}", StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList()
        };
        foreach (var type in types)
        {
            snapshot.Coverage[type] = new TypeCoverage { ScannedAt = scannedAt, Complete = true };
        }

        return snapshot;
    }

    public static SnapshotItem? ParseRfi(JsonElement element, string projectId, string host)
    {
        var id = Id(element);
        if (id is null)
        {
            return null;
        }

        var status = Status(element);
        var holders = Holders(element);
        return new SnapshotItem
        {
            ItemType = ItemTypes.Rfi,
            ItemId = id,
            Number = Text(element, "full_number", "number") ?? id,
            Title = Text(element, "subject", "title", "name") ?? "",
            Status = Capitalize(status),
            Closed = IsClosed(element, status),
            Responsible = string.Join(", ", holders.Select(holder => holder.Name)),
            Holders = holders,
            AttachmentCount = Count(element, "attachments_count", "attachments"),
            CreatedAt = Date(element, "created_at", "initiated_at"),
            ModifiedAt = Date(element, "updated_at"),
            DueAt = Date(element, "due_date"),
            SourceUrl = Link(element) ?? $"https://{host}/{projectId}/project/rfi/show/{id}"
        };
    }

    public static SnapshotItem? ParseSubmittal(JsonElement element, string projectId, string host)
    {
        var id = Id(element);
        if (id is null)
        {
            return null;
        }

        var status = Status(element);
        var holders = Holders(element);
        var number = Text(element, "formatted_number", "number") ?? id;
        var revision = Text(element, "revision");
        if (!string.IsNullOrWhiteSpace(revision) && revision != "0"
            && !number.Contains("rev", StringComparison.OrdinalIgnoreCase))
        {
            // Matches how CA folders name revisions ("… Rev 2"), so downloads go into R2.
            number = $"{number} Rev {revision}";
        }

        return new SnapshotItem
        {
            ItemType = ItemTypes.Submittal,
            ItemId = id,
            Number = number,
            Title = Text(element, "title", "name", "description") ?? "",
            Status = Capitalize(status),
            Closed = IsClosed(element, status),
            Responsible = string.Join(", ", holders.Select(holder => holder.Name)),
            Contract = Name(element, "responsible_contractor", "received_from") ?? "",
            Holders = holders,
            AttachmentCount = Count(element, "attachments_count", "attachments"),
            CreatedAt = Date(element, "created_at"),
            ModifiedAt = Date(element, "updated_at"),
            DueAt = Date(element, "due_date", "required_on_site_date"),
            SourceUrl = Link(element) ?? $"https://{host}/{projectId}/project/submittal_logs/{id}"
        };
    }

    /// <summary>Who has the ball: ball_in_court (a list of people, one person, or text).</summary>
    internal static List<SnapshotHolder> Holders(JsonElement element)
    {
        var holders = new List<SnapshotHolder>();
        if (!TryGet(element, out var value, "ball_in_court", "ball_in_courts"))
        {
            return holders;
        }

        void AddName(string? name, string? company, string? companyId)
        {
            if (string.IsNullOrWhiteSpace(name) || holders.Any(holder => string.Equals(holder.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            holders.Add(new SnapshotHolder { Name = name.Trim(), Company = company, CompanyId = companyId, State = "current" });
        }

        void AddPerson(JsonElement person)
        {
            if (person.ValueKind == JsonValueKind.String)
            {
                AddName(person.GetString(), null, null);
                return;
            }

            if (person.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var companyId = TryGet(person, out var vendor, "vendor", "company") && vendor.ValueKind == JsonValueKind.Object ? Id(vendor) : null;
            AddName(Text(person, "name", "full_name", "login"), Name(person, "vendor", "company") ?? Text(person, "company_name"), companyId);
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var person in value.EnumerateArray())
                {
                    AddPerson(person);
                }

                break;
            case JsonValueKind.Object:
                AddPerson(value);
                break;
            case JsonValueKind.String:
                foreach (var name in (value.GetString() ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    AddName(name, null, null);
                }

                break;
        }

        return holders;
    }

    private static string Status(JsonElement element)
    {
        if (!TryGet(element, out var value, "status"))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Object => Text(value, "name", "status", "label") ?? "",
            _ => ""
        };
    }

    private static bool IsClosed(JsonElement element, string status)
    {
        var text = status.Trim().ToLowerInvariant();
        if (text is "closed" or "void" or "voided" or "closed-revised" || text.StartsWith("closed", StringComparison.Ordinal))
        {
            return true;
        }

        return Date(element, "closed_at") is not null || (TryGet(element, out var deleted, "deleted_at") && deleted.ValueKind == JsonValueKind.String);
    }

    private static string Capitalize(string status) =>
        status.Length == 0 ? "" : char.ToUpperInvariant(status[0]) + status[1..].Replace('_', ' ');

    private static bool TryGet(JsonElement element, out JsonElement value, params string[] names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in names)
            {
                if (element.TryGetProperty(name, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    internal static string? Text(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, out var value, name))
            {
                continue;
            }

            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }

    private static string? Name(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGet(element, out var value, name))
            {
                var text = value.ValueKind == JsonValueKind.Object ? Text(value, "name", "company", "login") : Text(element, name);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static string? Id(JsonElement element) => Text(element, "id");

    private static int Count(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(element, out var value, name))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                return value.GetArrayLength();
            }
        }

        return 0;
    }

    private static DateTimeOffset? Date(JsonElement element, params string[] names)
    {
        var text = Text(element, names);
        if (text is null)
        {
            return null;
        }

        // A plain date ("2026-10-03") is a day in the user's time zone.
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    private static string? Link(JsonElement element)
    {
        var link = Text(element, "link", "url", "html_url");
        return link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.EndsWith("procore.com", StringComparison.OrdinalIgnoreCase)
            ? link
            : null;
    }
}
