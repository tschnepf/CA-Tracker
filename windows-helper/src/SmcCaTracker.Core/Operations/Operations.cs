using System.Text.Json;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Operations;

public enum OperationKind
{
    CreateFolder,
    MoveFolder
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
    /// <summary>Plans moving active item folders into the item type's returned folder.</summary>
    public static OperationPlan PlanMarkReturned(IEnumerable<ComparisonRow> rows, ProjectComparison comparison)
    {
        var operations = new List<PlannedOperation>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in rows.Where(row => row.Folder is not null).GroupBy(row => row.ItemType))
        {
            var scan = comparison.For(group.Key).Scan;
            var returnedName = comparison.Folders.ReturnedFolder;
            var returnedExists = Directory.Exists(scan.ReturnedFolderPath);
            if (!returnedExists)
            {
                operations.Add(new PlannedOperation
                {
                    Kind = OperationKind.CreateFolder,
                    DestinationPath = scan.ReturnedFolderPath,
                    Title = $"Create the {returnedName} folder in {ItemTypes.Plural(group.Key)}",
                    BlockedReason = Directory.Exists(scan.TypeFolderPath) ? null : $"{scan.TypeFolderPath} does not exist."
                });
            }

            foreach (var row in group.OrderBy(row => row.SortNumber, Comparer<string>.Create(ItemNumber.CompareNatural)))
            {
                var folder = row.Folder!;
                var destination = Path.Combine(scan.ReturnedFolderPath, folder.Name);
                operations.Add(new PlannedOperation
                {
                    Kind = OperationKind.MoveFolder,
                    SourcePath = folder.FullPath,
                    DestinationPath = destination,
                    Title = $"Move {folder.Name} to {returnedName}",
                    Warning = row.Item is null
                        ? "This folder was not matched to a provider item."
                        : row.IsReturned ? null : $"The provider still shows this as: {CourtEvaluator.Describe(row.Court, comparison.Company)}.",
                    BlockedReason = MoveProblem(folder, FolderLocation.Active, scan.TypeFolderPath, destination, targets)
                });
            }
        }

        var plan = new OperationPlan
        {
            Title = "Mark as returned",
            Description = "These folders will be moved into their returned folder.",
            Operations = operations
        };
        return plan;
    }

    /// <summary>Plans moving folders out of the returned folder back into the item type folder.</summary>
    public static OperationPlan PlanMoveBack(IEnumerable<ComparisonRow> rows, ProjectComparison comparison)
    {
        var operations = new List<PlannedOperation>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(row => row.Folder is not null)
                     .OrderBy(row => row.SortNumber, Comparer<string>.Create(ItemNumber.CompareNatural)))
        {
            var scan = comparison.For(row.ItemType).Scan;
            var folder = row.Folder!;
            var destination = Path.Combine(scan.TypeFolderPath, folder.Name);
            operations.Add(new PlannedOperation
            {
                Kind = OperationKind.MoveFolder,
                SourcePath = folder.FullPath,
                DestinationPath = destination,
                Title = $"Move {folder.Name} back to {ItemTypes.Plural(row.ItemType)}",
                BlockedReason = MoveProblem(folder, FolderLocation.Returned, scan.ReturnedFolderPath, destination, targets)
            });
        }

        return new OperationPlan
        {
            Title = "Move back to active",
            Description = "These folders will be moved out of the returned folder.",
            Operations = operations
        };
    }

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

    private static string? MoveProblem(
        FolderEntry folder,
        FolderLocation expectedLocation,
        string expectedParent,
        string destination,
        HashSet<string> targets)
    {
        if (folder.Location != expectedLocation)
        {
            return expectedLocation == FolderLocation.Active
                ? "This folder is already in the returned folder."
                : "This folder is not in the returned folder.";
        }

        if (!SamePath(Path.GetDirectoryName(folder.FullPath), expectedParent))
        {
            return "This folder is not where the tracker expects it.";
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
