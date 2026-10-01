using System.Text.Json;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Downloads;

/// <summary>Adds each downloaded file to journal.jsonl, next to the folder moves.</summary>
public static class DownloadJournal
{
    public static void Record(AppPaths paths, string item, string destination, string outcome, string message)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now,
                plan = $"Download {item}",
                kind = "DownloadFile",
                source = (string?)null,
                destination,
                outcome,
                message
            });
            File.AppendAllText(paths.JournalFile, line + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The journal is a record, not a requirement for the download.
        }
    }
}
