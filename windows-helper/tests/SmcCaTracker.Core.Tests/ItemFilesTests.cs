using System.Text.Json;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Downloads;

namespace SmcCaTracker.Core.Tests;

public sealed class ItemFilesTests
{
    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void ProcoreFilesAreReadLikeHelixFiles()
    {
        // What the Procore page posts for a Submittal: a file of the submittal itself (a timestamp) and a
        // response's file (Procore's returned date has no time).
        var answer = Json("""
            {
              "type": "procore-item-files", "requestId": "abc",
              "attachments": [
                { "id": "6538651529", "filename": "Product data.pdf", "uploadedAt": "2026-10-06T13:58:59Z", "url": "https://storage.procore.com/api/v5/files/1?sig=a" },
                { "id": "6545666580", "filename": "Reviewed.pdf", "uploadedAt": "2026-10-08", "url": "https://storage.procore.com/api/v5/files/2?sig=b" }
              ]
            }
            """);

        var files = ItemFilesMessages.Attachments(answer);

        Assert.Equal(new[] { "6538651529", "6545666580" }, files.Select(file => file.Id));
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 13, 58, 59, TimeSpan.Zero), files[0].UploadedAt);
        // A day alone is that day where you are, so the saved name starts with it.
        Assert.Equal(new DateTime(2026, 10, 8), files[1].UploadedAt!.Value.LocalDateTime);
        Assert.Equal("2026-10-08 Reviewed.pdf", DownloadNaming.AttachmentFileName(files[1].Filename, files[1].UploadedAt?.ToLocalTime()));
    }

    [Fact]
    public void FilesWithoutAnHttpsLinkOrIdAreLeftOut()
    {
        var answer = Json("""
            {
              "attachments": [
                { "id": "1", "filename": "Plain.pdf", "url": "http://storage.procore.com/1" },
                { "filename": "No id.pdf", "url": "https://storage.procore.com/2" },
                { "id": "3", "url": "https://storage.procore.com/3", "uploadedAt": "not a date" }
              ]
            }
            """);

        var file = Assert.Single(ItemFilesMessages.Attachments(answer));

        Assert.Equal("3", file.Id);
        Assert.Equal("Attachment", file.Filename);
        Assert.Null(file.UploadedAt);
        Assert.Empty(ItemFilesMessages.Attachments(Json("""{ "type": "procore-error", "status": 404 }""")));
    }

    [Fact]
    public void EachFileSaysWhoUploadedItAndTheirCompany()
    {
        var answer = Json("""
            {
              "attachments": [
                { "id": "1", "filename": "Review.pdf", "url": "https://storage.procore.com/1",
                  "uploadedBy": { "name": " Juan Cortez ", "company": "Strategic Mission Critical", "note": null } },
                { "id": "2", "filename": "Product data.pdf", "url": "https://storage.procore.com/2",
                  "uploadedBy": { "name": "Peter Leszko", "company": " ", "note": "Peter Leszko created the submittal." } },
                { "id": "3", "filename": "Nobody.pdf", "url": "https://storage.procore.com/3", "uploadedBy": { "name": "  ", "company": "Acme" } },
                { "id": "4", "filename": "Older page.pdf", "url": "https://storage.procore.com/4" }
              ]
            }
            """);

        var files = ItemFilesMessages.Attachments(answer);

        Assert.Equal(new ItemUploader("Juan Cortez", "Strategic Mission Critical", null), files[0].UploadedBy);
        Assert.Equal("Juan Cortez · Strategic Mission Critical", files[0].UploadedBy!.Text);
        // No company: the name alone; the note says how sure that is.
        Assert.Equal("Peter Leszko", files[1].UploadedBy!.Text);
        Assert.Equal("Peter Leszko created the submittal.", files[1].UploadedBy!.Note);
        // No name (or a page from before uploaders were listed): no one.
        Assert.Null(files[2].UploadedBy);
        Assert.Null(files[3].UploadedBy);
    }

    [Fact]
    public void AnItemWithNoFilesSaysWhereItLooked()
    {
        Assert.Equal("No files in Procore", DownloadChoices.Summary(Array.Empty<DownloadFileChoice>(), folderExists: true, "Procore"));
        Assert.Equal("No files in Procore", DownloadChoices.MissingSummary(Array.Empty<DownloadFileChoice>(), folderExists: true, "Procore"));
    }
}
