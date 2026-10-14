using System.Text.Json;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Diagnostics;

namespace SmcCaTracker.Core.Storage;

/// <summary>
/// Settings your company sets once for everyone, in a file named <c>ca-tracker-company.json</c> that IT
/// ships with the installer (next to the app) or puts in %ProgramData%\SMC-CA-Tracker. Values here are
/// the defaults for everyone; those listed in <see cref="Locked"/> can't be changed in the app.
/// </summary>
public sealed class CompanySettings
{
    public const string FileName = "ca-tracker-company.json";

    /// <summary>Shown in the app ("Settings are managed by …").</summary>
    public string? CompanyName { get; set; }

    /// <summary>Default folder names inside each CA folder.</summary>
    public FolderLayout? Folders { get; set; }

    /// <summary>The company's disciplines (replaces Electrical, Fire, ICT, Mechanical, Plbg, Security).</summary>
    public List<DisciplineDefinition>? Disciplines { get; set; }

    /// <summary>Minutes between background syncs from 7 am to 5 pm, and at other times.</summary>
    public int? SyncWorkHoursMinutes { get; set; }
    public int? SyncOffHoursMinutes { get; set; }

    /// <summary>Where new versions are published: a folder (e.g. \\server\apps\ca-tracker) or an https address holding latest.json.</summary>
    public string? UpdateFeed { get; set; }

    /// <summary>Settings people can't change: "folders", "disciplines", "sync".</summary>
    public List<string> Locked { get; set; } = new();

    public bool IsLocked(string name) => Locked.Contains(name, StringComparer.OrdinalIgnoreCase);
}

public sealed class DisciplineDefinition
{
    public string Code { get; set; } = "";
    public string? Label { get; set; }

    /// <summary>How it is written in folder names, e.g. "Elec".</summary>
    public string? FolderToken { get; set; }

    /// <summary>Other whole words in folder names or titles that mean this discipline.</summary>
    public List<string>? Aliases { get; set; }

    public Discipline ToDiscipline() =>
        new(Code.Trim(), string.IsNullOrWhiteSpace(Label) ? Code.Trim() : Label.Trim(),
            string.IsNullOrWhiteSpace(FolderToken) ? Code.Trim() : FolderToken.Trim(),
            (Aliases ?? new List<string>()).Where(alias => !string.IsNullOrWhiteSpace(alias)).ToList());
}

public static class CompanySettingsLoader
{
    /// <summary>
    /// Finds the company file: next to the app first, then %ProgramData%\SMC-CA-Tracker, then the path in
    /// the CA_TRACKER_COMPANY_SETTINGS environment variable. Null (and no error) when there is none.
    /// </summary>
    public static (CompanySettings? Settings, string? Path, string? Error) Load(string appDirectory)
    {
        var candidates = new List<string>
        {
            Path.Combine(appDirectory, CompanySettings.FileName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SMC-CA-Tracker", CompanySettings.FileName)
        };
        var fromEnvironment = Environment.GetEnvironmentVariable("CA_TRACKER_COMPANY_SETTINGS");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            candidates.Add(fromEnvironment);
        }

        foreach (var path in candidates)
        {
            try
            {
                var json = LocalFiles.ReadAllTextWithRetry(path);
                if (json is null)
                {
                    continue;
                }

                var settings = JsonSerializer.Deserialize<CompanySettings>(json, TrackerJson.Options) ?? new CompanySettings();
                settings.Locked ??= new List<string>();
                AppLog.Info("company", $"Using company settings from {path}.");
                return (settings, path, null);
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                AppLog.Error("company", $"The company settings file {path} could not be read", error);
                return (null, path, error.Message);
            }
        }

        return (null, null, null);
    }

    /// <summary>
    /// Puts the company's values into the settings: always for locked ones; otherwise only where the
    /// user still has the app's default. Returns true when anything changed (so it is saved).
    /// </summary>
    public static bool Apply(CompanySettings company, TrackerSettings settings)
    {
        var changed = false;
        if (company.Disciplines is { Count: > 0 })
        {
            Disciplines.Configure(company.Disciplines.Where(item => !string.IsNullOrWhiteSpace(item.Code)).Select(item => item.ToDiscipline()));
        }

        if (company.Folders is { } folders && folders.Validate().Count == 0)
        {
            var isDefault = settings.Folders.RfiFolder == new FolderLayout().RfiFolder
                && settings.Folders.SubmittalFolder == new FolderLayout().SubmittalFolder
                && settings.Folders.ReturnedFolder == new FolderLayout().ReturnedFolder;
            if ((company.IsLocked("folders") || isDefault) && !SameLayout(settings.Folders, folders))
            {
                settings.Folders = folders.Clone();
                changed = true;
            }
        }

        var defaults = new SyncSettings();
        if (company.SyncWorkHoursMinutes is { } work
            && (company.IsLocked("sync") || settings.Sync.WorkHoursIntervalMinutes == defaults.WorkHoursIntervalMinutes)
            && settings.Sync.WorkHoursIntervalMinutes != Math.Clamp(work, SyncSettings.WorkHoursMinimum, SyncSettings.WorkHoursMaximum))
        {
            settings.Sync.WorkHoursIntervalMinutes = Math.Clamp(work, SyncSettings.WorkHoursMinimum, SyncSettings.WorkHoursMaximum);
            changed = true;
        }

        if (company.SyncOffHoursMinutes is { } off
            && (company.IsLocked("sync") || settings.Sync.OffHoursIntervalMinutes == defaults.OffHoursIntervalMinutes)
            && settings.Sync.OffHoursIntervalMinutes != Math.Clamp(off, SyncSettings.OffHoursMinimum, SyncSettings.OffHoursMaximum))
        {
            settings.Sync.OffHoursIntervalMinutes = Math.Clamp(off, SyncSettings.OffHoursMinimum, SyncSettings.OffHoursMaximum);
            changed = true;
        }

        return changed;
    }

    private static bool SameLayout(FolderLayout left, FolderLayout right) =>
        left.RfiFolder == right.RfiFolder && left.SubmittalFolder == right.SubmittalFolder && left.ReturnedFolder == right.ReturnedFolder;
}
