namespace SmcCaTracker.ContractGen;

/// <summary>Where the pinned schema and the generated DTOs live, relative to windows-helper.</summary>
public static class ContractFiles
{
    public static string? FindWindowsHelper(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SmcCaTracker.slnx")))
            {
                return directory.FullName;
            }

            var nested = Path.Combine(directory.FullName, "windows-helper");
            if (File.Exists(Path.Combine(nested, "SmcCaTracker.slnx")))
            {
                return nested;
            }
        }

        return null;
    }

    public static string ContractDirectory(string windowsHelper) => Path.Combine(windowsHelper, "contract");

    public static string PinnedSchema(string windowsHelper) => Path.Combine(ContractDirectory(windowsHelper), "openapi.json");

    public static string Samples(string windowsHelper) => Path.Combine(ContractDirectory(windowsHelper), "samples");

    public static string GeneratedDtos(string windowsHelper) =>
        Path.Combine(windowsHelper, "src", "SmcCaTracker.Core", "WorkloadTracker", "Contract.g.cs");
}
