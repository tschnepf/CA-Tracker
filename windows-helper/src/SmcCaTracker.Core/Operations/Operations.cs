using System.Text.Json;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Operations;

public enum OperationKind
{
    CreateFolder,
    MoveFolder,
    MoveFile
}

/// <summary>One change shown in the preview. Nothing touches the disk until the plan is executed.</summary>
public sealed class PlannedOperation
{
    public OperationKind Kind { get; init; }
    public string? SourcePath { get; init; }
    public string DestinationPath { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Warning { get; init; }
    public string? BlockedReason { get; init; }
    public bool IsBlocked => BlockedReason is not null;

    /// <summary>
    /// A folder that must exist when this step runs (it was moved or created by an earlier step); otherwise the
    /// step is skipped, so nothing is made in the wrong place when an earlier step didn't happen.
    /// </summary>
    public string? RequiresPath { get; init; }
}

public sealed class OperationPlan
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public IReadOnlyList<PlannedOperation> Operations { get; init; } = Array.Empty<PlannedOperation>();

    public int RunnableCount => Operations.Count(operation => !operation.IsBlocked);
    public int BlockedCount => Operations.Count(operation => operation.IsBlocked);
    public bool HasRunnable => RunnableCount > 0;
}

public static class OperationPlanner
{
    /// <summary>
    /// Plans moving active item folders into a returned folder: the nearest ~Returned going up from the
    /// folder (e.g. Submittals\Elec\~Returned for Submittals\Elec\BB\P000210), keeping the BB/TFO
    /// subfolder when that returned folder has one. Without any, the item type's ~Returned is created.
    /// </summary>
    public static OperationPlan PlanMarkReturned(IEnumerable<ComparisonRow> rows, ProjectComparison comparison)
    {
        var operations = new List<PlannedOperation>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var creating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var returnedName = FolderLayout.DisplayName(comparison.Folders.ReturnedFolder);
        foreach (var row in rows.Where(row => row.Folder is not null)
                     .OrderBy(row => row.ItemType)
                     .ThenBy(row => row.SortNumber, Comparer<string>.Create(ItemNumber.CompareNatural)))
        {
            var scan = comparison.For(row.ItemType).Scan;
            var folder = row.Folder!;
            var destinationParent = ReturnedDestination(folder, scan, comparison.Folders);
            var destination = Path.Combine(destinationParent, folder.Name);
            // Checked first, so a move that can't happen doesn't leave an empty returned folder behind.
            var problem = MoveProblem(folder, FolderLocation.Active, destination, targets);
            if (problem is null && !Directory.Exists(destinationParent) && creating.Add(destinationParent))
            {
                operations.Add(new PlannedOperation
                {
                    Kind = OperationKind.CreateFolder,
                    DestinationPath = destinationParent,
                    Title = comparison.Folders.ReturnedIsShared
                        ? $"Create the {returnedName} folder"
                        : $"Create {Display(scan.TypeFolderPath, destinationParent)}",
                    BlockedReason = CreateProblem(destinationParent, scan)
                });
            }

            operations.Add(new PlannedOperation
            {
                Kind = OperationKind.MoveFolder,
                SourcePath = folder.FullPath,
                DestinationPath = destination,
                Title = $"Move {folder.Name} to {Display(scan.TypeFolderPath, destinationParent)}",
                Warning = row.Item is null
                    ? "This folder was not matched to a provider item."
                    : row.IsReturned ? null : $"The provider still shows this as: {CourtEvaluator.Describe(row.Court, comparison.Company)}.",
                BlockedReason = problem
            });
        }

        return new OperationPlan
        {
            Title = "Mark as returned",
            Description = "These folders will be moved into their returned folder.",
            Operations = operations
        };
    }

    /// <summary>
    /// Plans moving folders out of a returned folder, back to where the returned folder sits (keeping
    /// a BB/TFO subfolder when the active side has one too).
    /// </summary>
    public static OperationPlan PlanMoveBack(IEnumerable<ComparisonRow> rows, ProjectComparison comparison)
    {
        var operations = new List<PlannedOperation>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(row => row.Folder is not null)
                     .OrderBy(row => row.SortNumber, Comparer<string>.Create(ItemNumber.CompareNatural)))
        {
            var scan = comparison.For(row.ItemType).Scan;
            var folder = row.Folder!;
            var destinationParent = ActiveDestination(folder, scan);
            var destination = Path.Combine(destinationParent, folder.Name);
            operations.Add(new PlannedOperation
            {
                Kind = OperationKind.MoveFolder,
                SourcePath = folder.FullPath,
                DestinationPath = destination,
                Title = $"Move {folder.Name} back to {Display(scan.TypeFolderPath, destinationParent)}",
                BlockedReason = MoveProblem(folder, FolderLocation.Returned, destination, targets)
            });
        }

        return new OperationPlan
        {
            Title = "Move back to active",
            Description = "These folders will be moved out of the returned folder.",
            Operations = operations
        };
    }

    /// <summary>
    /// Plans bringing back the folders of items that are back in your company's court while their folder sits in
    /// a returned folder: each folder moves back to the active folder (as <see cref="PlanMoveBack"/>), and gets a
    /// new revision folder for the new round. A folder with no revision folders yet ("R0", "R1", "Rev 2", …) has
    /// what it holds moved into a new R0 (the first round) and an empty R1 made; otherwise the next number after
    /// the highest is made (R1 → R2). Hidden and system files (desktop.ini, Thumbs.db) stay where they are. Every
    /// step after the move needs the folder to have arrived, so nothing is made elsewhere if the move doesn't happen.
    /// </summary>
    public static OperationPlan PlanReopen(IEnumerable<ComparisonRow> rows, ProjectComparison comparison)
    {
        var operations = new List<PlannedOperation>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(row => row.Folder is not null)
                     .OrderBy(row => row.SortNumber, Comparer<string>.Create(ItemNumber.CompareNatural)))
        {
            var scan = comparison.For(row.ItemType).Scan;
            var folder = row.Folder!;
            var destinationParent = ActiveDestination(folder, scan);
            var destination = Path.Combine(destinationParent, folder.Name);
            var problem = MoveProblem(folder, FolderLocation.Returned, destination, targets);
            operations.Add(new PlannedOperation
            {
                Kind = OperationKind.MoveFolder,
                SourcePath = folder.FullPath,
                DestinationPath = destination,
                Title = $"Move {folder.Name} back to {Display(scan.TypeFolderPath, destinationParent)}",
                BlockedReason = problem
            });

            // Later steps act on the folder where it will be, once moved back.
            var skipped = problem is null ? null : "Skipped with the move above.";
            var (revisions, contents) = ReadItemFolder(folder.FullPath);
            if (revisions.Count == 0)
            {
                var first = Path.Combine(destination, "R0");
                operations.Add(new PlannedOperation
                {
                    Kind = OperationKind.CreateFolder,
                    DestinationPath = first,
                    Title = $"Create R0 in {folder.Name}, for what it holds now",
                    RequiresPath = destination,
                    BlockedReason = skipped
                });
                foreach (var (name, isFile) in contents)
                {
                    operations.Add(new PlannedOperation
                    {
                        Kind = isFile ? OperationKind.MoveFile : OperationKind.MoveFolder,
                        SourcePath = Path.Combine(destination, name),
                        DestinationPath = Path.Combine(first, name),
                        Title = $"Move {name} into R0",
                        RequiresPath = first,
                        BlockedReason = skipped
                    });
                }

                operations.Add(new PlannedOperation
                {
                    Kind = OperationKind.CreateFolder,
                    DestinationPath = Path.Combine(destination, "R1"),
                    Title = $"Create R1 in {folder.Name}, for the new round",
                    RequiresPath = destination,
                    BlockedReason = skipped
                });
            }
            else
            {
                var next = revisions.Max() + 1;
                operations.Add(new PlannedOperation
                {
                    Kind = OperationKind.CreateFolder,
                    DestinationPath = Path.Combine(destination, $"R{next}"),
                    Title = $"Create R{next} in {folder.Name}, for the new round (the last was R{next - 1})",
                    RequiresPath = destination,
                    BlockedReason = skipped
                });
            }
        }

        return new OperationPlan
        {
            Title = "Back with you: new revision",
            Description = "These items are back with your company, but their folders are in the returned folder. Each folder moves back and gets a new R folder for the new round.",
            Operations = operations
        };
    }

    /// <summary>An item folder's revision folders (their numbers) and the rest of what it holds (hidden and system files left out).</summary>
    private static (List<int> Revisions, List<(string Name, bool IsFile)> Contents) ReadItemFolder(string path)
    {
        var revisions = new List<int>();
        var contents = new List<(string, bool)>();
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry is DirectoryInfo && Downloads.DownloadNaming.RevisionOfFolder(entry.Name) is { } revision)
                {
                    revisions.Add(revision);
                }
                else if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                {
                    contents.Add((entry.Name, entry is FileInfo));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return (revisions, contents);
    }

    /// <summary>The folder a returned item folder goes into. See <see cref="PlanMarkReturned"/>.</summary>
    public static string ReturnedDestination(FolderEntry folder, TypeFolderScan scan, FolderLayout layout)
    {
        if (layout.ReturnedIsShared || string.IsNullOrEmpty(scan.TypeFolderPath))
        {
            return scan.ReturnedFolderPath;
        }

        var returnedName = Path.GetFileName(Path.TrimEndingDirectorySeparator(scan.ReturnedFolderPath));
        var phase = Phases.Detect(folder.DetectionText);
        var level = folder.ParentPath;
        while (level.Length > 0 && FolderScanner.IsInside(level, scan.TypeFolderPath))
        {
            // "~Returned", or one with a note such as "~Returned (Base Build)".
            var candidate = FolderScanner.FindReturnedChild(level, returnedName, phase);
            if (candidate is not null)
            {
                // Keep the BB/TFO (and any other grouping) folders inside ~Returned, so the folder
                // stays with its part; the plan creates them there if they're missing.
                var relative = Path.GetRelativePath(level, folder.ParentPath);
                return relative == "." ? candidate : Path.Combine(candidate, relative);
            }

            if (FolderScanner.SamePath(level, scan.TypeFolderPath))
            {
                break;
            }

            level = Path.GetDirectoryName(level) ?? "";
        }

        return scan.ReturnedFolderPath;
    }

    /// <summary>The folder a returned item folder moves back to. See <see cref="PlanMoveBack"/>.</summary>
    public static string ActiveDestination(FolderEntry folder, TypeFolderScan scan)
    {
        if (string.IsNullOrEmpty(scan.TypeFolderPath))
        {
            return folder.ParentPath;
        }

        var returned = folder.ReturnedFolder ?? scan.ReturnedFolderPath;
        var anchor = FolderScanner.IsInside(returned, scan.TypeFolderPath) && !FolderScanner.SamePath(returned, scan.TypeFolderPath)
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(returned)) ?? scan.TypeFolderPath
            : scan.TypeFolderPath;
        var relative = FolderScanner.IsInside(folder.ParentPath, returned) ? Path.GetRelativePath(returned, folder.ParentPath) : ".";
        if (relative != "." && Directory.Exists(Path.Combine(anchor, relative)))
        {
            return Path.Combine(anchor, relative);
        }

        return anchor;
    }

    private static string Display(string typePath, string path) =>
        typePath.Length > 0 && FolderScanner.IsInside(path, typePath)
            ? Path.Combine(Path.GetFileName(Path.TrimEndingDirectorySeparator(typePath)), Path.GetRelativePath(typePath, path)).TrimEnd('.', Path.DirectorySeparatorChar)
            : path;

    /// <summary>Plans creating missing RFI/Submittal folders in a project's CA folder.</summary>
    public static OperationPlan PlanCreateTypeFolders(ProjectComparison comparison)
    {
        var operations = new List<PlannedOperation>();
        foreach (var type in comparison.Types.Values)
        {
            if (!Directory.Exists(type.Scan.TypeFolderPath) && !string.IsNullOrEmpty(type.Scan.TypeFolderPath))
            {
                var caExists = Directory.Exists(Path.GetDirectoryName(type.Scan.TypeFolderPath));
                operations.Add(new PlannedOperation
                {
                    Kind = OperationKind.CreateFolder,
                    DestinationPath = type.Scan.TypeFolderPath,
                    Title = $"Create the {Path.GetFileName(type.Scan.TypeFolderPath)} folder",
                    BlockedReason = caExists ? null : "The CA folder cannot be found."
                });
            }
        }

        return new OperationPlan
        {
            Title = "Create missing folders",
            Description = "These folders will be created in the project's CA folder.",
            Operations = operations
        };
    }

    /// <summary>
    /// Why a missing returned folder can't be created. Folders missing on the way inside the item type's
    /// folder (~Returned\Elec\BB) are created with it; the type's folder itself (for a returned folder
    /// outside it, the folder it goes in) has to be there already, so nothing is created on a drive that
    /// is offline or in a CA folder that has moved.
    /// </summary>
    private static string? CreateProblem(string folder, TypeFolderScan scan)
    {
        var root = !string.IsNullOrEmpty(scan.TypeFolderPath) && FolderScanner.IsInside(folder, scan.TypeFolderPath)
            ? scan.TypeFolderPath
            : Path.GetDirectoryName(folder);
        var existing = Path.GetDirectoryName(folder);
        while (existing is not null && !Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
        }

        return root is not null && existing is not null && FolderScanner.IsInside(existing, root)
            ? null
            : $"{root ?? folder} does not exist.";
    }

    private static string? MoveProblem(
        FolderEntry folder,
        FolderLocation expectedLocation,
        string destination,
        HashSet<string> targets)
    {
        if (folder.Location != expectedLocation)
        {
            return expectedLocation == FolderLocation.Active
                ? "This folder is already in the returned folder."
                : "This folder is not in the returned folder.";
        }

        if (!Directory.Exists(folder.FullPath))
        {
            return "This folder no longer exists. Rescan and try again.";
        }

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            return "A folder with the same name is already at the destination.";
        }

        if (!targets.Add(destination))
        {
            return "Another selected folder is moving to the same place.";
        }

        return null;
    }

    internal static bool SamePath(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }
}

public enum OperationOutcome
{
    Done,
    Skipped,
    Failed
}

public sealed record OperationResult(PlannedOperation Operation, OperationOutcome Outcome, string Message);

/// <summary>Carries out a confirmed plan, re-checking each step just before it runs.</summary>
public sealed class OperationExecutor
{
    private readonly AppPaths paths;

    public OperationExecutor(AppPaths paths)
    {
        this.paths = paths;
    }

    public IReadOnlyList<OperationResult> Execute(OperationPlan plan)
    {
        var batchId = Guid.NewGuid().ToString("N");
        var results = new List<OperationResult>();
        var failedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in plan.Operations)
        {
            var result = Run(operation, failedFolders);
            if (result.Outcome != OperationOutcome.Done && operation.Kind == OperationKind.CreateFolder)
            {
                failedFolders.Add(operation.DestinationPath);
            }

            results.Add(result);
            Journal(batchId, plan, result);
        }

        return results;
    }

    private static OperationResult Run(PlannedOperation operation, HashSet<string> failedFolders)
    {
        if (operation.IsBlocked)
        {
            return new OperationResult(operation, OperationOutcome.Skipped, operation.BlockedReason!);
        }

        if (operation.RequiresPath is { } required && !Directory.Exists(required))
        {
            return new OperationResult(operation, OperationOutcome.Skipped, "An earlier step didn't happen, so this one was skipped.");
        }

        try
        {
            if (operation.Kind == OperationKind.CreateFolder)
            {
                if (Directory.Exists(operation.DestinationPath))
                {
                    return new OperationResult(operation, OperationOutcome.Done, "Folder already existed.");
                }

                Directory.CreateDirectory(operation.DestinationPath);
                return new OperationResult(operation, OperationOutcome.Done, "Created.");
            }

            var parent = Path.GetDirectoryName(operation.DestinationPath);
            if (parent is not null && failedFolders.Contains(parent))
            {
                return new OperationResult(operation, OperationOutcome.Skipped, "The destination folder could not be created.");
            }

            if (operation.Kind == OperationKind.MoveFile)
            {
                if (operation.SourcePath is null || !File.Exists(operation.SourcePath))
                {
                    return new OperationResult(operation, OperationOutcome.Failed, "The file no longer exists.");
                }

                if (File.Exists(operation.DestinationPath) || Directory.Exists(operation.DestinationPath))
                {
                    return new OperationResult(operation, OperationOutcome.Failed, "Something with the same name appeared at the destination.");
                }

                if (parent is null || !Directory.Exists(parent))
                {
                    return new OperationResult(operation, OperationOutcome.Failed, "The destination folder does not exist.");
                }

                File.Move(operation.SourcePath, operation.DestinationPath);
                return new OperationResult(operation, OperationOutcome.Done, "Moved.");
            }

            if (operation.SourcePath is null || !Directory.Exists(operation.SourcePath))
            {
                return new OperationResult(operation, OperationOutcome.Failed, "The folder no longer exists.");
            }

            if (Directory.Exists(operation.DestinationPath) || File.Exists(operation.DestinationPath))
            {
                return new OperationResult(operation, OperationOutcome.Failed, "Something with the same name appeared at the destination.");
            }

            if (parent is null || !Directory.Exists(parent))
            {
                return new OperationResult(operation, OperationOutcome.Failed, "The destination folder does not exist.");
            }

            Directory.Move(operation.SourcePath, operation.DestinationPath);
            return new OperationResult(operation, OperationOutcome.Done, "Moved.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new OperationResult(operation, OperationOutcome.Failed, error.Message);
        }
    }

    private void Journal(string batchId, OperationPlan plan, OperationResult result)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            var line = JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.Now,
                batch = batchId,
                plan = plan.Title,
                kind = result.Operation.Kind.ToString(),
                source = result.Operation.SourcePath,
                destination = result.Operation.DestinationPath,
                outcome = result.Outcome.ToString(),
                message = result.Message
            });
            File.AppendAllText(paths.JournalFile, line + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The journal is a record, not a requirement for the move itself.
        }
    }
}
