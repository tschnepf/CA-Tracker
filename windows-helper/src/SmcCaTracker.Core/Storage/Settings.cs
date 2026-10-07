using System.Text.Json;
using SmcCaTracker.Core.Comparison;
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
    public int Version { get; set; } = 2;
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public FolderLayout Folders { get; set; } = new();
    public ReturnedRule Returned { get; set; } = new();
    public BrowserSettings Browser { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();

    /// <summary>The Workload Tracker connection (optional): sends this PC's scans to the company's tracker.</summary>
    public WorkloadTrackerSettings WorkloadTracker { get; set; } = new();

    /// <summary>Procore: projects you chose not to sync, and when your projects were last listed.</summary>
    public SmcCaTracker.Core.Procore.ProcoreSettings Procore { get; set; } = new();

    /// <summary>Projects defined in the app. Each has a CA folder and optionally one Helix or Procore project.</summary>
    public List<TrackerProject> Projects { get; set; } = new();

    public TrackerProject? FindProject(string id) =>
        Projects.FirstOrDefault(project => string.Equals(project.Id, id, StringComparison.Ordinal));

    public TrackerProject? FindByConnection(string provider, string projectId) =>
        Projects.FirstOrDefault(project => project.Connection is not null
            && string.Equals(project.Connection.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(project.Connection.ProjectId, projectId, StringComparison.Ordinal));
}

/// <summary>
/// The connection to Workload Tracker (decision D2). Without an address and a token the app works on
/// its own, as it always has.
/// </summary>
public sealed class WorkloadTrackerSettings
{
    /// <summary>The tracker's https address, typed in by the person.</summary>
    public string Address { get; set; } = "";

    /// <summary>This installation's id, sent with every call: a copied token shows up with another one.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The CA Tracker token from the tracker, encrypted for this Windows user.</summary>
    public string TokenProtected { get; set; } = "";

    /// <summary>The tracker's session for the token (for Disconnect) and when it ends.</summary>
    public int? SessionId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Who the token belongs to, from the tracker's /api/auth/me/: tags the outbox and shows "Connected as".</summary>
    public string Account { get; set; } = "";
    public string? AccountName { get; set; }
    public int? PersonId { get; set; }
    public string? PersonName { get; set; }

    /// <summary>
    /// Per tracker project entry (<see cref="TrackerProject.Id"/>): the Workload Tracker project its items go
    /// to, chosen in Edit project (decision D3). Kept when disconnecting, as this PC's choice; forgotten
    /// when connecting to a different tracker, whose project ids mean other projects.
    /// </summary>
    public Dictionary<string, WorkloadBinding> Bindings { get; set; } = new(StringComparer.Ordinal);

    public bool IsPaired => Address.Length > 0 && TokenProtected.Length > 0 && Account.Length > 0;
}

/// <summary>An existing Workload Tracker project a local project entry is bound to.</summary>
public sealed class WorkloadBinding
{
    public int ProjectId { get; set; }
    public string? ProjectNumber { get; set; }
    public string ProjectName { get; set; } = "";

    public const string Bound = "bound";
    public const string Conflict = "conflict";
    public const string Rejected = "rejected";

    /// <summary>
    /// What the tracker answered when the binding was last sent: <see cref="Bound"/>, <see cref="Conflict"/>
    /// (someone else bound the Helix or Procore project differently) or <see cref="Rejected"/> (this
    /// account can't see the project). Null until sent.
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>On a conflict: the project the tracker uses, and who chose it.</summary>
    public int? TheirProjectId { get; set; }
    public string? TheirProjectNumber { get; set; }
    public string? TheirProjectName { get; set; }
    public string? TheirAssertedBy { get; set; }

    public string Label => LabelOf(ProjectNumber, ProjectName);

    public string? TheirLabel => TheirProjectName is null ? null : LabelOf(TheirProjectNumber, TheirProjectName);

    public static string LabelOf(string? number, string name) => string.IsNullOrWhiteSpace(number) ? name : $"{number} · {name}";

    public WorkloadBinding Copy() => (WorkloadBinding)MemberwiseClone();
}

/// <summary>Keeping Helix items up to date in the background.</summary>
public sealed class SyncSettings
{
    /// <summary>Re-read tracked Helix projects in the background while the app is open (only changed items are read).</summary>
    public bool AutoSync { get; set; } = true;

    /// <summary>Minutes between background reads.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Hours after which a project's items are all read again, to catch items removed in Helix.</summary>
    public int FullReadHours { get; set; } = 12;
}

/// <summary>How the built-in browser window behaves during a refresh.</summary>
public sealed class BrowserSettings
{
    /// <summary>Close the browser window once the refresh has finished.</summary>
    public bool CloseWindowWhenDone { get; set; } = true;

    /// <summary>How long each page may take to open and be read before the refresh is stopped.</summary>
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

    /// <summary>Base Build / Tenant Fit Out: which part of the job this project covers.</summary>
    public PhaseScope Scope { get; set; } = new();

    /// <summary>Disciplines chosen by hand, keyed by item type and folder name (Disciplines.Key) or, for items without a folder yet, by item (Disciplines.ItemKey).</summary>
    public Dictionary<string, string> Disciplines { get; set; } = new();

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

/// <summary>
/// Where item folders live. Defaults: CA\RFIs, CA\Submittals, each with a ~Returned subfolder.
/// The RFI and Submittal values are relative to the CA folder and the returned value is relative to
/// each of those, unless a value is a full path (chosen with Browse when a project is laid out differently).
/// </summary>
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

    /// <summary>The last part of a folder setting, for labels: "~Returned" or the name at the end of a full path.</summary>
    public static string DisplayName(string value)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(value ?? ""));
        return string.IsNullOrEmpty(name) ? value ?? "" : name;
    }

    /// <summary>True when the returned folder is a full path, so RFIs and Submittals share it.</summary>
    public bool ReturnedIsShared => Path.IsPathFullyQualified(ReturnedFolder ?? "");

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        void Check(string label, string value)
        {
            if (!FolderNames.IsValidSetting(value))
            {
                problems.Add($"{label} folder \"{value}\" is not a valid Windows folder name or path.");
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

    /// <summary>
    /// A folder setting: a single name ("RFIs"), a relative path ("03 CA\RFIs"), or a full path
    /// ("Z:\Projects\CMH04\Submittals" or "\\server\share\CA"). ".." is not allowed.
    /// </summary>
    public static bool IsValidSetting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() != value)
        {
            return false;
        }

        var rest = value.Replace('/', '\\');
        if (rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':')
        {
            if (rest.Length < 3 || rest[2] != '\\')
            {
                return false;
            }

            rest = rest[3..];
        }
        else if (rest.StartsWith(@"\\", StringComparison.Ordinal))
        {
            rest = rest[2..];
        }

        var parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts.All(part => part != ".." && IsValid(part));
    }
}

public sealed class SettingsStore
{
    private readonly AppPaths paths;

    public SettingsStore(AppPaths paths)
    {
        this.paths = paths;
    }

    /// <summary>Reads settings.json; the defaults when it is missing or can't be read (see <see cref="TryLoad"/>).</summary>
    public TrackerSettings Load()
    {
        TryLoad(out var settings);
        return settings;
    }

    /// <summary>
    /// Reads settings.json. False when the file is there but isn't valid JSON: <paramref name="settings"/>
    /// are then the defaults, the file is left as it is and a copy is kept as settings.json.*.bad, so a
    /// caller that would save over it (the one-time import) can stop instead of saving no projects.
    /// </summary>
    public bool TryLoad(out TrackerSettings settings)
    {
        settings = LoadCore(out var readable);
        return readable;
    }

    private TrackerSettings LoadCore(out bool readable)
    {
        readable = true;
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
            readable = false;
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
        settings.Sync ??= new SyncSettings();
        settings.Procore ??= new SmcCaTracker.Core.Procore.ProcoreSettings();
        settings.Procore.SkippedProjectIds ??= new List<string>();
        settings.Procore.DiscoverEveryHours = Math.Clamp(settings.Procore.DiscoverEveryHours, 1, 168);
        settings.WorkloadTracker ??= new WorkloadTrackerSettings();
        settings.WorkloadTracker.Address ??= "";
        settings.WorkloadTracker.ClientId ??= "";
        settings.WorkloadTracker.TokenProtected ??= "";
        settings.WorkloadTracker.Account ??= "";
        settings.Sync.IntervalMinutes = Math.Clamp(settings.Sync.IntervalMinutes, 5, 240);
        settings.Sync.FullReadHours = Math.Clamp(settings.Sync.FullReadHours, 1, 168);
        settings.Projects = (settings.Projects ?? new List<TrackerProject>())
            .Where(project => project is not null && !string.IsNullOrWhiteSpace(project.Id))
            .ToList();
        foreach (var project in settings.Projects)
        {
            project.Name ??= "";
            project.CaFolder ??= "";
            project.Scope ??= new PhaseScope();
            project.Disciplines = (project.Disciplines ?? new Dictionary<string, string>())
                .GroupBy(entry => entry.Key.ToLowerInvariant())
                .ToDictionary(group => group.Key, group => group.Last().Value);
        }

        // Bindings of projects that were removed go with them.
        var projectIds = settings.Projects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        settings.WorkloadTracker.Bindings = new Dictionary<string, WorkloadBinding>(
            (settings.WorkloadTracker.Bindings ?? new Dictionary<string, WorkloadBinding>())
                .Where(pair => pair.Value is { ProjectId: > 0 } && projectIds.Contains(pair.Key)),
            StringComparer.Ordinal);
        foreach (var binding in settings.WorkloadTracker.Bindings.Values)
        {
            binding.ProjectName ??= "";
        }

        return settings;
    }

    public void Save(TrackerSettings settings)
    {
        LocalFiles.WriteAllTextAtomic(paths.SettingsFile, JsonSerializer.Serialize(settings, TrackerJson.Options));
    }
}
