using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmcCaTracker.Core.Storage;

/// <summary>Where the helper keeps its own data (never inside a CA folder).</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
    }

    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SMC-CA-Tracker"));

    public string Root { get; }
    public string SnapshotsDirectory => Path.Combine(Root, "snapshots");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string JournalFile => Path.Combine(Root, "journal.jsonl");
    public string HostManifestFile => Path.Combine(Root, "com.smc.catracker.json");
    public string HostLogFile => Path.Combine(Root, "native-host.log");
    public string RefreshDirectory => Path.Combine(Root, "refresh");
    public string CatalogFile => Path.Combine(Root, "catalog.json");
    public string ContactFile => Path.Combine(Root, "extension-contact.json");
}

public static class TrackerJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public static class TrackerVersion
{
    public static string Current =>
        typeof(TrackerVersion).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
}

/// <summary>Small helpers for the helper's own files in %LOCALAPPDATA%.</summary>
public static class LocalFiles
{
    public static void WriteAllTextAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < 8)
            {
                // The app or the native host may be reading the file at this moment.
                Thread.Sleep(60);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }
    }

    public static string? ReadAllTextWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                // Share read/write/delete so a reader never blocks the other process's atomic replace.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (Exception error) when ((error is IOException or UnauthorizedAccessException) && attempt < 5)
            {
                Thread.Sleep(80);
            }
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
