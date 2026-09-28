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

    public int Version { get; set; } = 1;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public FolderLayout Folders { get; set; } = new();
    public ReturnedRule Returned { get; set; } = new();
    public List<ProjectMapping> Projects { get; set; } = new();
    public List<string> AllowedExtensionIds { get; set; } = new() { DefaultExtensionId };

    public ProjectMapping? FindMapping(string provider, string projectId) =>
        Projects.FirstOrDefault(mapping =>
            string.Equals(mapping.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(mapping.ProjectId, projectId, StringComparison.Ordinal));

    public ProjectMapping GetOrAddMapping(string provider, string projectId, string projectName)
    {
        var mapping = FindMapping(provider, projectId);
        if (mapping is null)
        {
            mapping = new ProjectMapping { Provider = provider, ProjectId = projectId, ProjectName = projectName };
            Projects.Add(mapping);
        }
        else if (!string.IsNullOrWhiteSpace(projectName))
        {
            mapping.ProjectName = projectName;
        }

        return mapping;
    }
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

public sealed class ProjectMapping
{
    public string Provider { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string ProjectName { get; set; } = "";

    /// <summary>The project's CA folder, e.g. Z:\Shared\Projects\CMH04\CA.</summary>
    public string? CaFolder { get; set; }

    /// <summary>Company whose court defines "returned"; empty means the logged-in user's company.</summary>
    public string? CompanyId { get; set; }
    public string? CompanyName { get; set; }

    /// <summary>Per-project folder names; empty means use the default layout.</summary>
    public FolderLayout? FoldersOverride { get; set; }

    public FolderLayout EffectiveFolders(FolderLayout defaults) => FoldersOverride ?? defaults;
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

    /// <summary>Repairs missing sections so a hand-edited file cannot cause null references.</summary>
    internal static TrackerSettings Normalize(TrackerSettings settings)
    {
        settings.Folders ??= new FolderLayout();
        settings.Returned ??= new ReturnedRule();
        settings.Returned.AlwaysReturnedStatuses ??= new List<string>();
        settings.Returned.NeverReturnedStatuses ??= new List<string>();
        settings.Projects = (settings.Projects ?? new List<ProjectMapping>()).Where(mapping => mapping is not null).ToList();
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
