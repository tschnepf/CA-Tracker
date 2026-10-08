using System.Globalization;
using System.Text.Json;

namespace SmcCaTracker.App.Services;

/// <summary>A Helix item's Details report, as offered for download.</summary>
public sealed record HelixReport(string Name, string Kind, string Filename, string Url);

/// <summary>
/// An item's attachment: its id, file name, date and short-lived download link. Helix gives the upload date;
/// Procore gives none, so a Procore file carries the date of what it came with (question, answer, response).
/// </summary>
public sealed record ItemAttachment(string Id, string Filename, DateTimeOffset? UploadedAt, string Url);

/// <summary>What Helix or Procore offers for one item, and when that was read (the links expire). Only Helix has a Details report.</summary>
public sealed record ItemFiles(HelixReport? Report, string? ReportError, IReadOnlyList<ItemAttachment> Attachments, DateTimeOffset ReadAt);

/// <summary>
/// A page in a browser of the Download or Open window that lists an item's files: Helix's or Procore's. One
/// request at a time. When the site asks you to sign in, <see cref="SignInChanged"/> tells the window to show
/// the page; the request carries on by itself once you are in. Read-only. Use from the screen's thread.
/// </summary>
public interface IItemFilesSession
{
    /// <summary>True when the site shows its sign-in (show the page), false once it is past it.</summary>
    event Action<bool>? SignInChanged;

    Task StartAsync();

    /// <summary>Lists the item's files with fresh download links. Procore needs the company and server; Helix ignores them.</summary>
    Task<ItemFiles> ReadItemAsync(string itemId, string itemType, string projectId, string? accountId, string? host);
}

/// <summary>Reads the attachments a page posted ({ attachments: [{ id, filename, uploadedAt, url }] }; Helix and Procore alike).</summary>
public static class ItemFilesMessages
{
    public static IReadOnlyList<ItemAttachment> Attachments(JsonElement answer)
    {
        var attachments = new List<ItemAttachment>();
        if (!answer.TryGetProperty("attachments", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return attachments;
        }

        foreach (var element in list.EnumerateArray())
        {
            var id = Text(element, "id");
            var url = Text(element, "url");
            // Only https download links.
            if (id is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }

            DateTimeOffset? uploaded = DateTimeOffset.TryParse(Text(element, "uploadedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed
                : null;
            attachments.Add(new ItemAttachment(id, Text(element, "filename") ?? "Attachment", uploaded, url!));
        }

        return attachments;
    }

    public static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
