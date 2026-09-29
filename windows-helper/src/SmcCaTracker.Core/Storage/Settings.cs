using System.Text.Json;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.Core.Storage;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public sealed class TrackerSettings
{
    public const string DefaultExtensionId = "kcngamkbiboiljpfbjpmjoekkfpbidbk";

    public int Version { get; set; } = 2;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public FolderLayout Folders { get; set; } = new();
    public ReturnedRule Returned { get; set; } = new();
    public BrowserSettings Browser { get; set; } = new();

    /// <summary>Projects defined in the app. Each has a CA folder and optionally one Helix or Procore project.</summary>
    public List<TrackerProject> Projects { get; set; } = new();

    public List<string> AllowedExtensionIds { get; set; } = new() { DefaultExtensionId };

    public TrackerProject? FindProject(string id) =>
        Projects.FirstOrDefault(project => string.Equals(project.Id, id, StringComparison.Ordinal));

    public TrackerProject? FindByConnection(string provider, string projectId) =>
        Projects.FirstOrDefault(project => project.Connection is not null
            && string.Equals(project.Connection.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(project.Connection.ProjectId, projectId, StringComparison.Ordinal));
}

/// <summary>How the app opens Chrome when it asks the extension to refresh a project.</summary>
public sealed class BrowserSettings
{
    /// <summary>Full path to chrome.exe; empty means find it automatically.</summary>
    public string ChromePath { get; set; } = "";

    /// <summary>Chrome profile folder name such as "Default" or "Profile 1"; empty means Chrome's last used profile.</summary>
    public string ProfileDirectory { get; set; } = "";

    /// <summary>Close the tab the app opened once the refresh has finished.</summary>
    public bool CloseTabWhenDone { get; set; } = true;

    /// <summary>How long to wait for Chrome before reporting that a refresh did not finish.</summary>
    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>A project as the app defines it: a CA folder, optionally connected to a Helix or Procore project.</summary>
public sealed class TrackerProject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>The project's CA folder, e.g. Z:\Shared\Projects\CMH04\CA.</summary>
    public string CaFolder { get; set; } = "";

    public ProviderConnection? Connection { get; set; }

    /// <summary>Company whose court defines "returned"; empty means the logged-in user's company.</summary>
    public string? CompanyId { get; set; }
    public string? CompanyName { get; set; }

    /// <summary>Per-project folder names; empty means use the default layout.</summary>
    public FolderLayout? FoldersOverride { get; set; }

    public FolderLayout EffectiveFolders(FolderLayout defaults) => FoldersOverride ?? defaults;

    /// <summary>A sensible project name from a CA folder path: the parent's name when the folder is called "CA".</summary>
    public static string SuggestName(string caFolder)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(caFolder ?? "");
        var name = Path.GetFileName(trimmed);
        if (string.Equals(name, "CA", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? "");
            if (!string.IsNullOrWhiteSpace(parent))
            {
                return parent;
            }
        }

        return string.IsNullOrWhiteSpace(name) ? "New project" : name;
    }
}

/// <summary>The Helix or Procore project a tracker project is connected to.</summary>
public sealed class ProviderConnection
{
    public string Provider { get; set; } = "";
    public string ProjectId { get; set; } = "";

    /// <summary>Procore company id (not used for Helix).</summary>
    public string? AccountId { get; set; }

    public string ProjectName { get; set; } = "";

    /// <summary>Procore host such as app.procore.com or us02.procore.com (not used for Helix).</summary>
    public string? Host { get; set; }

    public string Label => string.IsNullOrWhiteSpace(ProjectName)
        ? $"{Providers.Label(Provider)} project {ProjectId}"
        : $"{Providers.Label(Provider)} · {ProjectName}";
}

/// <summary>Folder names inside a project's CA folder. Defaults: RFIs, Submittals, ~Returned.</summary>
public sealed class FolderLayout
{
    public string RfiFolder { get; set; } = "RFIs";
    public string SubmittalFolder { get; set; } = "Submittals";
    public string ReturnedFolder { get; set; } = "~Returned";

    public string TypeFolder(string itemType) =>
        string.Equals(itemType, ItemTypes.Submittal, StringComparison.OrdinalIgnoreCase) ? SubmittalFolder : RfiFolder;

    public FolderLayout Clone() => new()
    {
        RfiFolder = RfiFolder,
        SubmittalFolder = SubmittalFolder,
        ReturnedFolder = ReturnedFolder
    };

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        void Check(string label, string value)
        {
            if (!FolderNames.IsValid(value))
            {
                problems.Add($"{label} folder name \"{value}\" is not a valid Windows folder name.");
            }
        }

        Check("RFI", RfiFolder);
        Check("Submittal", SubmittalFolder);
        Check("Returned", ReturnedFolder);
        if (string.Equals(RfiFolder.Trim(), SubmittalFolder.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("The RFI and Submittal folders must have different names.");
        }

        return problems;
    }
}

/// <summary>
/// When an item counts as returned. The default is "no one from the selected company holds it
/// any more", which includes closed items; the status lists refine it as the team defines it further.
/// </summary>
public sealed class ReturnedRule
{
    public bool WhenClosed { get; set; } = true;
    public bool WhenNoCompanyHolder { get; set; } = true;
    public bool WhenOnlyComingUp { get; set; }
    public List<string> AlwaysReturnedStatuses { get; set; } = new();
    public List<string> NeverReturnedStatuses { get; set; } = new();
}

public static class FolderNames
{
    private static readonly string[] Reserved =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    // Windows-invalid characters, listed explicitly so validation is the same on every OS.
    private static readonly char[] Invalid = "<>:\"/\\|?*".ToCharArray();

    public static bool IsValid(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var trimmed = name.Trim();
        if (trimmed != name || trimmed is "." or ".." || trimmed.EndsWith('.'))
        {
            return false;
        }

        if (trimmed.IndexOfAny(Invalid) >= 0 || trimmed.Any(character => character < 32))
        {
            return false;
        }

        var stem = trimmed.Split('.')[0];
        return !Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class SettingsStore
{
    private readonly AppPaths paths;

    public SettingsStore(AppPaths paths)
    {
        this.paths = paths;
    }

    public TrackerSettings Load()
    {
        try
        {
            var json = LocalFiles.ReadAllTextWithRetry(paths.SettingsFile);
            if (json is null)
            {
                return new TrackerSettings();
            }

            var settings = JsonSerializer.Deserialize<TrackerSettings>(json, TrackerJson.Options) ?? new TrackerSettings();
            if (settings.Version < 2)
            {
                settings.Projects = MigrateVersion1Projects(json);
                settings.Version = 2;
                Normalize(settings);
                try
                {
                    Save(settings);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The migration simply runs again next time.
                }
            }

            return Normalize(settings);
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection and start from defaults.
            try
            {
                File.Copy(paths.SettingsFile, $"{paths.SettingsFile}.{DateTime.Now:yyyyMMddHHmmss}.bad", overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }

            return new TrackerSettings();
        }
    }

    /// <summary>
    /// Version 1 stored one entry per Helix/Procore project with an optional CA folder. Version 2 stores
    /// app-owned projects; each old entry with a CA folder becomes a project connected to that provider.
    /// </summary>
    internal static List<TrackerProject> MigrateVersion1Projects(string json)
    {
        var projects = new List<TrackerProject>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("projects", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return projects;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            string? Text(string name) =>
                entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            var caFolder = Text("caFolder");
            var provider = Text("provider");
            var projectId = Text("projectId");
            if (string.IsNullOrWhiteSpace(caFolder))
            {
                continue;
            }

            var projectName = Text("projectName") ?? "";
            FolderLayout? folders = null;
            if (entry.TryGetProperty("foldersOverride", out var foldersElement) && foldersElement.ValueKind == JsonValueKind.Object)
            {
                folders = foldersElement.Deserialize<FolderLayout>(TrackerJson.Options);
            }

            projects.Add(new TrackerProject
            {
                Name = string.IsNullOrWhiteSpace(projectName) ? TrackerProject.SuggestName(caFolder) : projectName,
                CaFolder = caFolder,
                Connection = !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(projectId) && Providers.All.Contains(provider)
                    ? new ProviderConnection { Provider = provider, ProjectId = projectId, ProjectName = projectName }
                    : null,
                CompanyId = Text("companyId"),
                CompanyName = Text("companyName"),
                FoldersOverride = folders
            });
        }

        return projects;
    }

    /// <summary>Repairs missing sections so a hand-edited file cannot cause null references.</summary>
    internal static TrackerSettings Normalize(TrackerSettings settings)
    {
        settings.Folders ??= new FolderLayout();
        settings.Returned ??= new ReturnedRule();
        settings.Returned.AlwaysReturnedStatuses ??= new List<string>();
        settings.Returned.NeverReturnedStatuses ??= new List<string>();
        settings.Browser ??= new BrowserSettings();
        settings.Projects = (settings.Projects ?? new List<TrackerProject>())
            .Where(project => project is not null && !string.IsNullOrWhiteSpace(project.Id))
            .ToList();
        foreach (var project in settings.Projects)
        {
            project.Name ??= "";
            project.CaFolder ??= "";
        }
        settings.AllowedExtensionIds ??= new List<string>();
        if (settings.AllowedExtensionIds.Count == 0)
        {
            settings.AllowedExtensionIds.Add(TrackerSettings.DefaultExtensionId);
        }

        return settings;
    }

    public void Save(TrackerSettings settings)
    {
        LocalFiles.WriteAllTextAtomic(paths.SettingsFile, JsonSerializer.Serialize(settings, TrackerJson.Options));
    }
}
