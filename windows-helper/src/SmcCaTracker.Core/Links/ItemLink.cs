using System.Globalization;
using System.Text;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.Core.Links;

/// <summary>What a link from Workload Tracker asks CA Tracker to do with its item, as its buttons do.</summary>
public static class ItemLinkActions
{
    public const string Details = "details";
    public const string Open = "open";
    public const string Folder = "folder";
    public const string Newest = "newest";
    public const string Download = "download";
    public const string Verify = "verify";
    public const string Discipline = "discipline";

    public static readonly IReadOnlyList<string> All = [Details, Open, Folder, Newest, Download, Verify, Discipline];
}

/// <summary>
/// A link from Workload Tracker's web list to one item in CA Tracker:
/// <c>smc-ca-tracker://item?provider=helix&amp;host=…&amp;project=…&amp;type=rfi&amp;id=…&amp;number=…&amp;action=details</c>.
/// Windows starts the app with the link when one is clicked. A link is only ever read: every value must
/// fit its allowlist or the link is refused, and what it asks for runs through the app's own buttons
/// (nothing that changes a folder happens without their usual windows).
/// </summary>
public sealed record ItemLink(string Provider, string Host, string ProjectId, string ItemType, string ItemId, string Number, string Action)
{
    public const string Scheme = "smc-ca-tracker";

    private const string Prefix = Scheme + "://item";
    private const int MaxLength = 2048;
    private const int MaxIdLength = 64;
    private const int MaxNumberLength = 64;
    private const int MaxParameters = 16;

    private static readonly string[] Required = ["provider", "host", "project", "type", "id", "number", "action"];

    /// <summary>"RFI 000303" / "Submittal 03 30 00-1", for messages.</summary>
    public string ItemLabel => $"{ItemTypes.Singular(ItemType)} {Number}";

    /// <summary>The link as Workload Tracker writes it.</summary>
    public string ToUri() => string.Create(CultureInfo.InvariantCulture,
        $"{Prefix}?provider={Provider}&host={Host}&project={Uri.EscapeDataString(ProjectId)}&type={ItemType}" +
        $"&id={Uri.EscapeDataString(ItemId)}&number={Uri.EscapeDataString(Number)}&action={Action}");

    /// <summary>
    /// Reads a link. Null unless it is exactly an item link: the scheme and "item", each of the seven values
    /// once, a provider, item type and action from their lists, project and item ids of at most 64 letters,
    /// digits and <c>. _ : -</c>, a plain host name, and an item number of at most 64 characters with no
    /// control characters. Other parameters are ignored, so Workload Tracker can add some later.
    /// </summary>
    public static ItemLink? Parse(string? text)
    {
        if (text is null || text.Length > MaxLength || !text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // "smc-ca-tracker://item?…", or "…item/?…" as some browsers write it; nothing else, and no fragment.
        var rest = text[Prefix.Length..];
        if (rest.StartsWith("/?", StringComparison.Ordinal))
        {
            rest = rest[1..];
        }

        if (!rest.StartsWith('?') || rest.Contains('#'))
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var pairs = rest[1..].Split('&');
        if (pairs.Length > MaxParameters)
        {
            return null;
        }

        foreach (var pair in pairs)
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                return null;
            }

            var name = pair[..equals];
            if (!Required.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            if (Decode(pair[(equals + 1)..]) is not { } value || !values.TryAdd(name, value))
            {
                return null; // badly escaped, or named twice
            }
        }

        if (Required.Any(name => !values.ContainsKey(name)))
        {
            return null;
        }

        var provider = values["provider"].ToLowerInvariant();
        var itemType = values["type"].ToLowerInvariant();
        var action = values["action"].ToLowerInvariant();
        var host = values["host"].ToLowerInvariant();
        var number = values["number"].Trim();
        if (!Providers.All.Contains(provider, StringComparer.Ordinal)
            || !ItemTypes.All.Contains(itemType, StringComparer.Ordinal)
            || !ItemLinkActions.All.Contains(action, StringComparer.Ordinal)
            || !IsHostName(host)
            || !IsId(values["project"])
            || !IsId(values["id"])
            || number.Length is 0 or > MaxNumberLength
            || !IsPlainText(number))
        {
            return null;
        }

        return new ItemLink(provider, host, values["project"], itemType, values["id"], number, action);
    }

    /// <summary>The link among the program's arguments (Windows passes it as one argument), if one is there.</summary>
    public static string? FindInArguments(IReadOnlyList<string> args) =>
        args.FirstOrDefault(arg => arg.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>Percent-decoding of a query value ("+" is a space); null when it isn't valid UTF-8 or decodes to another escape.</summary>
    private static string? Decode(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length
                    || !byte.TryParse(value.AsSpan(index + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var octet))
                {
                    return null;
                }

                bytes.Add(octet);
                index += 2;
            }
            else if (character == '+')
            {
                bytes.Add((byte)' ');
            }
            else if (character < 0x80)
            {
                bytes.Add((byte)character);
            }
            else
            {
                return null; // a browser escapes anything beyond ASCII
            }
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>A Helix or Procore project id or item id: 1 to 64 of A–Z, a–z, 0–9 and <c>. _ : -</c>.</summary>
    private static bool IsId(string value) =>
        value.Length is > 0 and <= MaxIdLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    /// <summary>A plain DNS name such as project.helixteam.com: labels of letters, digits and inner hyphens.</summary>
    private static bool IsHostName(string value)
    {
        if (value.Length is 0 or > 253)
        {
            return false;
        }

        var labels = value.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is > 0 and <= 63
            && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')
            && label[0] != '-' && label[^1] != '-');
    }

    /// <summary>No control characters, and none of the invisible ones that reorder or hide text.</summary>
    private static bool IsPlainText(string value) =>
        value.All(character => !char.IsControl(character)
            && CharUnicodeInfo.GetUnicodeCategory(character) is not (UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned));
}
