using SmcCaTracker.Core.Operations;

namespace SmcCaTracker.Core.Downloads;

/// <summary>
/// An item back with you while its folder is in Returned starts a new round: its new files go in a new R folder,
/// R1 the first time and the next number after that. Before they're saved, the folder can move back out of
/// Returned, and what it holds outside R folders (the first round's files) can go into R0. Those moves are
/// planned as steps for the preview; nothing changes until it is confirmed.
/// </summary>
public static class NewRound
{
    /// <summary>An item folder's R folders (their numbers) and the rest of what it holds (hidden and system files left out).</summary>
    public static (IReadOnlyList<int> Revisions, IReadOnlyList<(string Name, bool IsFile)> Loose) Read(string folder)
    {
        var revisions = new List<int>();
        var loose = new List<(string, bool)>();
        try
        {
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (entry is DirectoryInfo && DownloadNaming.RevisionOfFolder(entry.Name) is { } revision)
                {
                    revisions.Add(revision);
                }
                else if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                {
                    loose.Add((entry.Name, entry is FileInfo));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }

        return (revisions, loose);
    }

    /// <summary>
    /// The R folder a new round's files go in to begin with: the next after the highest there, R1 when there are
    /// none, or the revision the item's title names when that is higher.
    /// </summary>
    public static int SuggestNumber(IReadOnlyList<int> revisions, int? titleRevision) =>
        Math.Max(revisions.Count == 0 ? 1 : revisions.Max() + 1, titleRevision ?? 0);

    /// <summary>
    /// The steps before a new round's files are saved: move <paramref name="itemFolder"/> back into
    /// <paramref name="moveBackTo"/> (when given), move what it holds outside R folders into R0 (when
    /// <paramref name="looseIntoR0"/>), and make the R folder (an existing "R2" or "Rev 2" is used as it is).
    /// Steps after the move need the folder to have arrived, so nothing is made elsewhere if it doesn't.
    /// Also gives where the round's files then go.
    /// </summary>
    public static (OperationPlan Plan, string RoundFolder) Plan(string itemFolder, string? moveBackTo, bool looseIntoR0, int number)
    {
        var operations = new List<PlannedOperation>();
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(itemFolder));
        var folder = itemFolder;
        string? skipped = null;
        if (moveBackTo is not null)
        {
            var destination = Path.Combine(moveBackTo, name);
            var problem = !Directory.Exists(itemFolder) ? "This folder no longer exists. Rescan and try again."
                : Directory.Exists(destination) || File.Exists(destination) ? "A folder with the same name is already at the destination."
                : null;
            operations.Add(new PlannedOperation
            {
                Kind = OperationKind.MoveFolder,
                SourcePath = itemFolder,
                DestinationPath = destination,
                Title = $"Move {name} out of Returned, back to {moveBackTo}",
                BlockedReason = problem
            });
            folder = destination;
            skipped = problem is null ? null : "Skipped with the move above.";
        }

        var (_, loose) = Read(itemFolder);
        if (looseIntoR0 && number > 0 && loose.Count > 0)
        {
            var existingFirst = DownloadNaming.RevisionFolder(itemFolder, 0);
            var first = Path.Combine(folder, Path.GetFileName(existingFirst));
            if (!Directory.Exists(existingFirst))
            {
                operations.Add(new PlannedOperation
                {
                    Kind = OperationKind.CreateFolder,
                    DestinationPath = first,
                    Title = $"Create R0 in {name}, for what it holds now",
                    RequiresPath = folder,
                    BlockedReason = skipped
                });
            }

            foreach (var (entry, isFile) in loose)
            {
                var taken = File.Exists(Path.Combine(existingFirst, entry)) || Directory.Exists(Path.Combine(existingFirst, entry));
                operations.Add(new PlannedOperation
                {
                    Kind = isFile ? OperationKind.MoveFile : OperationKind.MoveFolder,
                    SourcePath = Path.Combine(folder, entry),
                    DestinationPath = Path.Combine(first, entry),
                    Title = $"Move {entry} into {Path.GetFileName(first)}",
                    RequiresPath = first,
                    BlockedReason = skipped ?? (taken ? $"{Path.GetFileName(first)} already has one with that name." : null)
                });
            }
        }

        var existingRound = DownloadNaming.RevisionFolder(itemFolder, number);
        var round = Path.Combine(folder, Path.GetFileName(existingRound));
        if (!Directory.Exists(existingRound))
        {
            operations.Add(new PlannedOperation
            {
                Kind = OperationKind.CreateFolder,
                DestinationPath = round,
                Title = $"Create {Path.GetFileName(round)} in {name}, for the new files",
                RequiresPath = folder,
                BlockedReason = skipped
            });
        }

        return (new OperationPlan
        {
            Title = "Back with you: new round",
            Description = "Before the new files are saved, these folders and files move, and the new R folder is made.",
            Operations = operations
        }, round);
    }
}
