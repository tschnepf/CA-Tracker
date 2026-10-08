using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Downloads;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Operations;

namespace SmcCaTracker.App.ViewModels;

/// <summary>What to download and where it would go by default.</summary>
/// <param name="Provider">Where the item is: <see cref="Providers.Helix"/> or <see cref="Providers.Procore"/>.</param>
/// <param name="ExistingFolder">The item's matched folder, if it already has one.</param>
/// <param name="ParentFolder">Where a new "&lt;number&gt; - &lt;name&gt;" folder goes (the project's RFI or Submittal folder).</param>
/// <param name="MoveBackTo">
/// For an item back with you while its folder is in Returned (a new round): where that folder goes when it moves
/// back out of Returned. Its new files go in a new R folder (see <see cref="NewRound"/>).
/// </param>
/// <param name="AccountId">Procore: the project's company, which Procore needs to answer for it.</param>
/// <param name="Host">Procore: the server the project is on ("app.procore.com", "us02.procore.com").</param>
public sealed record DownloadRequest(
    string Provider,
    string ItemType,
    string ItemId,
    string ProjectId,
    string Number,
    string Title,
    string ProjectName,
    string? ExistingFolder,
    string? ParentFolder,
    string? MoveBackTo = null,
    string? AccountId = null,
    string? Host = null)
{
    /// <summary>"Helix" or "Procore", for what the windows say.</summary>
    public string ProviderLabel => Providers.Label(Provider);
}

/// <summary>One file offered for download.</summary>
public sealed class DownloadFileViewModel : ObservableObject
{
    private bool isChecked = true;
    private bool canChange = true;
    private string statusText = "";

    public DownloadFileViewModel(string key, string name, DateTimeOffset? uploadedAt, string targetName, bool isReport)
    {
        Key = key;
        Name = name;
        UploadedAt = uploadedAt;
        TargetName = targetName;
        IsReport = isReport;
    }

    /// <summary>"report" or the attachment's id in Helix or Procore.</summary>
    public string Key { get; }
    public string Name { get; }
    public DateTimeOffset? UploadedAt { get; }
    public bool IsReport { get; }
    public string TargetName { get; }
    /// <summary>When it was uploaded; a day alone for Procore's dates that have no time (a response's returned date).</summary>
    public string UploadedText => IsReport ? "Created by Helix now"
        : UploadedAt?.ToLocalTime() is not { } local ? ""
        : local.TimeOfDay == TimeSpan.Zero ? local.ToString("MMM d, yyyy")
        : local.ToString("MMM d, yyyy h:mm tt");
    public string NameToolTip => $"Saved as {TargetName}";

    /// <summary>Short-lived download link from Helix or Procore (attachments only).</summary>
    public string? Url { get; set; }

    /// <summary>Whether it starts checked ("New files only" goes back to this).</summary>
    public bool CheckedByDefault { get; set; }

    public bool IsChecked
    {
        get => isChecked;
        set => Set(ref isChecked, value);
    }

    public bool CanChange
    {
        get => canChange;
        set => Set(ref canChange, value);
    }

    public string StatusText
    {
        get => statusText;
        set => Set(ref statusText, value);
    }
}

public enum DownloadItemState
{
    /// <summary>Not read yet (waiting its turn).</summary>
    Waiting,
    Reading,
    Ready,
    Failed,
    Downloading,

    /// <summary>Downloaded (some files may have failed; they stay checked to try again).</summary>
    Done
}

/// <summary>How an item's status chip is coloured.</summary>
public enum DownloadChip
{
    Neutral,
    Accent,
    Success,
    Warning
}

/// <summary>One item in the download window: where it goes, its files and where it has got to.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private DownloadItemState state = DownloadItemState.Waiting;
    private string statusText;
    private DownloadChip chip = DownloadChip.Neutral;
    private string message = "";
    private bool isExpanded;
    private string? destination;
    private bool inRevisionFolder;
    private bool hasSaved;
    private bool updatingChecks;

    // The item's folder, where it is now (it moves when a new round is brought out of Returned).
    private string? itemFolder;

    // A new round (back with you while the folder is in Returned): which R folder, and what moves first.
    private int roundNumber;
    private string roundText = "";
    private bool moveOutOfReturned = true;
    private bool moveLooseIntoR0 = true;
    private bool movedBack;
    private bool folderChosen;
    private int looseCount;
    private bool looseHasFolders;

    public DownloadItemViewModel(DownloadRequest request, bool verifying = false)
    {
        Request = request;
        Verifying = verifying;
        statusText = $"Waiting to read from {request.ProviderLabel}";
        FolderName = DownloadNaming.FolderName(request.Number, request.Title, request.ProjectName);
        Revision = DownloadNaming.Revision(request.Title, request.Number);
        Heading = $"{(request.ItemType == ItemTypes.Submittal ? "Submittal" : "RFI")} {request.Number} · {request.Title}";
        itemFolder = request.ExistingFolder;
        if (IsNewRound)
        {
            roundNumber = NewRound.SuggestNumber(NewRound.Read(itemFolder!).Revisions, Revision);
            roundText = roundNumber.ToString(CultureInfo.InvariantCulture);
            ReadLoose();
            Destination = RoundDestination();
            return;
        }

        Destination = request.ExistingFolder is not null
            ? WithRevision(request.ExistingFolder)
            : string.IsNullOrWhiteSpace(request.ParentFolder) ? null : WithRevision(Path.Combine(request.ParentFolder, FolderName));
    }

    public DownloadRequest Request { get; }

    /// <summary>Checking the item's folder against Helix or Procore: every missing file starts checked (not just new PDFs).</summary>
    public bool Verifying { get; }

    /// <summary>Files Helix or Procore has that aren't in the folder (as last compared).</summary>
    public int MissingCount { get; private set; }

    /// <summary>The folder exists and has every file Helix or Procore has for the item.</summary>
    public bool IsComplete { get; private set; }

    public string FolderName { get; }
    public int? Revision { get; }
    public string Heading { get; }
    public ObservableCollection<DownloadFileViewModel> Files { get; } = new();
    public HelixReport? Report { get; private set; }
    public DateTimeOffset ReadAt { get; private set; }

    /// <summary>Completes when the current read of the item's files has finished (read or failed).</summary>
    public TaskCompletionSource ReadDone { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DownloadItemState State
    {
        get => state;
        private set
        {
            if (Set(ref state, value))
            {
                OnPropertyChanged(nameof(CanRetry));
                OnPropertyChanged(nameof(CanChangeFolder));
            }
        }
    }

    /// <summary>The status chip: "3 new files", "Up to date", "Reading from Procore…", "Saved 3 files"…</summary>
    public string StatusText
    {
        get => statusText;
        private set => Set(ref statusText, value);
    }

    public DownloadChip Chip
    {
        get => chip;
        private set => Set(ref chip, value);
    }

    /// <summary>A line under the heading: why reading failed, the revision subfolder, a download problem.</summary>
    public string Message
    {
        get => message;
        set
        {
            if (Set(ref message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => Message.Length > 0;

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    public bool CanRetry => State == DownloadItemState.Failed;
    public bool CanChangeFolder => State is not (DownloadItemState.Downloading or DownloadItemState.Reading);

    /// <summary>Where its files go (a revision subfolder for a revised item whose folder exists). Null: choose one.</summary>
    public string? Destination
    {
        get => destination;
        private set
        {
            if (Set(ref destination, value))
            {
                OnPropertyChanged(nameof(DestinationText));
                OnPropertyChanged(nameof(DestinationNote));
            }
        }
    }

    public string DestinationText => Destination ?? "No CA folder: choose where to save it with Change";

    public string DestinationNote =>
        Destination is null ? ""
        : IsNewRound ? $"Back with you: the new files go in {Path.GetFileName(Destination)}"
        : inRevisionFolder ? $"Revision {Revision}: its files go in the revision subfolder"
        : Directory.Exists(Destination) ? "Existing folder; files there are never replaced"
        : "New folder";

    // ---- A new round ---------------------------------------------------------------------------

    /// <summary>
    /// Back with you while its folder is in Returned: a new round, whose files go in an R folder (the next number;
    /// it can be changed). The folder can move out of Returned first, and the first round's files into R0.
    /// </summary>
    public bool IsNewRound => Request.MoveBackTo is not null && itemFolder is not null && !Verifying && !folderChosen;

    /// <summary>The R folder's number as typed ("1" for R1).</summary>
    public string RoundText
    {
        get => roundText;
        set
        {
            if (Set(ref roundText, value ?? "")
                && int.TryParse(roundText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= 99)
            {
                roundNumber = number;
                RoundChanged();
            }
        }
    }

    /// <summary>Whether the R folder is there already or will be made.</summary>
    public string RoundNote => !IsNewRound ? ""
        : Directory.Exists(DownloadNaming.RevisionFolder(itemFolder!, roundNumber)) ? "already in the folder: the new files are added to it"
        : "a new folder";

    /// <summary>Move the folder out of Returned before saving (checked to begin with).</summary>
    public bool MoveOutOfReturned
    {
        get => moveOutOfReturned;
        set
        {
            if (Set(ref moveOutOfReturned, value))
            {
                RoundChanged();
            }
        }
    }

    public bool CanMoveOutOfReturned => IsNewRound && !movedBack;

    public string MoveOutOfReturnedText => $"Move the folder out of Returned, back to {ShortPath(Request.MoveBackTo)}";

    public string MoveBackToolTip => Request.MoveBackTo ?? "";

    /// <summary>Move what the folder holds outside R folders (the first round's files) into R0 (checked to begin with).</summary>
    public bool MoveLooseIntoR0
    {
        get => moveLooseIntoR0;
        set
        {
            if (Set(ref moveLooseIntoR0, value))
            {
                RoundChanged();
            }
        }
    }

    /// <summary>Offered while files sit loose in the folder, unless the new files go in R0 themselves.</summary>
    public bool CanMoveLooseIntoR0 => IsNewRound && looseCount > 0 && roundNumber > 0;

    public string MoveLooseIntoR0Text =>
        $"Move the {looseCount} {(looseHasFolders ? looseCount == 1 ? "item" : "items" : looseCount == 1 ? "file" : "files")} already in the folder into R0";

    /// <summary>Something moves before the files are saved, so Download shows a preview first.</summary>
    public bool HasRoundMoves => (CanMoveOutOfReturned && MoveOutOfReturned) || (CanMoveLooseIntoR0 && MoveLooseIntoR0);

    /// <summary>Its folder is to move out of Returned first and hasn't yet, so nothing can be saved into it.</summary>
    public bool WaitsForMove => CanMoveOutOfReturned && MoveOutOfReturned;

    /// <summary>The moves to preview before its files are saved; null when nothing moves.</summary>
    public OperationPlan? RoundPlan() => HasRoundMoves
        ? NewRound.Plan(itemFolder!, WaitsForMove ? Request.MoveBackTo : null, CanMoveLooseIntoR0 && MoveLooseIntoR0, roundNumber).Plan
        : null;

    /// <summary>After the moves ran: the folder is wherever it ended up, and what's loose in it is read again.</summary>
    public void RoundDone()
    {
        if (WaitsForMove)
        {
            var moved = MovedFolder();
            if (Directory.Exists(moved) && !Directory.Exists(itemFolder))
            {
                itemFolder = moved;
                movedBack = true;
            }
            else
            {
                moveOutOfReturned = false;
                OnPropertyChanged(nameof(MoveOutOfReturned));
                Message = "Its folder couldn't move out of Returned, so its files go in the R folder where it is.";
            }
        }

        ReadLoose();
        RoundChanged();
    }

    private string MovedFolder() => Path.Combine(Request.MoveBackTo!, Path.GetFileName(Path.TrimEndingDirectorySeparator(itemFolder!)));

    /// <summary>The R folder in the item's folder, where the folder will be (moved out of Returned when that's checked).</summary>
    private string RoundDestination() => Path.Combine(WaitsForMove ? MovedFolder() : itemFolder!,
        Path.GetFileName(DownloadNaming.RevisionFolder(itemFolder!, roundNumber)));

    private void ReadLoose()
    {
        var (_, loose) = NewRound.Read(itemFolder!);
        looseCount = loose.Count;
        looseHasFolders = loose.Any(entry => !entry.IsFile);
    }

    private void RoundChanged()
    {
        if (IsNewRound)
        {
            Destination = RoundDestination();
        }

        foreach (var name in new[]
                 {
                     nameof(IsNewRound), nameof(RoundNote), nameof(CanMoveOutOfReturned), nameof(CanMoveLooseIntoR0),
                     nameof(MoveLooseIntoR0Text), nameof(HasRoundMoves), nameof(DestinationNote)
                 })
        {
            OnPropertyChanged(name);
        }

        ApplyChoices(resetChecks: false);
    }

    /// <summary>"Elec\BB" for a long path: its last two folders.</summary>
    private static string ShortPath(string? path)
    {
        var parts = Path.TrimEndingDirectorySeparator(path ?? "").Split(Path.DirectorySeparatorChar);
        return parts.Length >= 2 ? Path.Combine(parts[^2], parts[^1]) : path ?? "";
    }

    /// <summary>Files were saved into <see cref="Destination"/>, so Open folder is offered.</summary>
    public bool HasSaved
    {
        get => hasSaved;
        private set => Set(ref hasSaved, value);
    }

    public int CheckedCount => Files.Count(file => file.IsChecked);

    /// <summary>The item's box: checked when all its files are, empty when none are, in between otherwise.</summary>
    public bool? IsIncluded
    {
        get
        {
            var checkedCount = CheckedCount;
            return checkedCount == 0 ? false : checkedCount == Files.Count ? true : null;
        }
        set
        {
            if (!CanChangeFiles)
            {
                return;
            }

            updatingChecks = true;
            foreach (var file in Files)
            {
                file.IsChecked = value == true;
            }

            updatingChecks = false;
            ChecksChanged();
        }
    }

    public bool CanChangeFiles => State is DownloadItemState.Ready or DownloadItemState.Done;

    /// <summary>Raised when a file is checked or unchecked, so the window can update its totals.</summary>
    public event EventHandler? CheckedChanged;

    public void BeginReading()
    {
        if (ReadDone.Task.IsCompleted)
        {
            ReadDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        Message = "";
        State = DownloadItemState.Reading;
        SetChip($"Reading from {Request.ProviderLabel}…", DownloadChip.Neutral);
        OnPropertyChanged(nameof(CanChangeFiles));
    }

    /// <summary>Lists the files Helix or Procore offers and checks the new ones (see <see cref="DownloadChoices"/>).</summary>
    public void Load(ItemFiles files)
    {
        foreach (var file in Files)
        {
            file.PropertyChanged -= OnFileChanged;
        }

        Files.Clear();
        Report = files.Report;
        ReadAt = files.ReadAt;
        if (files.Report is { } report)
        {
            Files.Add(new DownloadFileViewModel("report", $"{report.Name} (Details PDF)", null,
                DownloadNaming.SafeFileName(report.Filename, "Details.pdf"), isReport: true));
        }

        foreach (var attachment in files.Attachments)
        {
            Files.Add(new DownloadFileViewModel(attachment.Id, attachment.Filename, attachment.UploadedAt,
                DownloadNaming.AttachmentFileName(attachment.Filename, attachment.UploadedAt?.ToLocalTime()), isReport: false)
            {
                Url = attachment.Url
            });
        }

        foreach (var file in Files)
        {
            file.PropertyChanged += OnFileChanged;
        }

        State = DownloadItemState.Ready;
        OnPropertyChanged(nameof(CanChangeFiles));
        Message = files.Report is null && files.ReportError is { } reportError ? $"{reportError} The attachments can still be downloaded." : "";
        ApplyChoices(resetChecks: true);
        // Items with something new open (and a new round, to show its R folder); ones with nothing new stay folded away.
        IsExpanded = CheckedCount > 0 || IsNewRound;
    }

    /// <summary>New download links (they expire after a few minutes) for the files already listed.</summary>
    public void RefreshLinks(ItemFiles files)
    {
        var fresh = files.Attachments.ToDictionary(attachment => attachment.Id);
        foreach (var file in Files.Where(file => !file.IsReport))
        {
            file.Url = fresh.TryGetValue(file.Key, out var attachment) ? attachment.Url : null;
        }

        ReadAt = files.ReadAt;
    }

    public void Fail(string why)
    {
        State = DownloadItemState.Failed;
        SetChip("Couldn't read its files", DownloadChip.Warning);
        Message = why;
        OnPropertyChanged(nameof(CanChangeFiles));
    }

    /// <summary>Marks files already in the folder; with <paramref name="resetChecks"/> also checks the new ones again.</summary>
    public void ApplyChoices(bool resetChecks)
    {
        if (State is not (DownloadItemState.Ready or DownloadItemState.Done))
        {
            return;
        }

        var keys = DownloadFolders.ExistingFileKeys(itemFolder, Destination, inRevisionFolder);
        var folderExists = FolderAlreadyExists;
        var facts = Files.Select(file => new DownloadFileFacts(file.Name, file.TargetName, file.IsReport)).ToList();
        var choices = Verifying
            ? DownloadChoices.ChooseMissing(facts, keys, Destination)
            : DownloadChoices.Choose(facts, keys, Destination, folderExists);
        MissingCount = choices.Count(choice => !choice.AlreadySaved);
        IsComplete = folderExists && MissingCount == 0;
        updatingChecks = true;
        for (var index = 0; index < Files.Count; index++)
        {
            var file = Files[index];
            file.StatusText = choices[index].Note;
            file.CheckedByDefault = choices[index].Checked;
            if (resetChecks)
            {
                file.IsChecked = choices[index].Checked;
            }
        }

        updatingChecks = false;
        if (State == DownloadItemState.Ready && Verifying)
        {
            SetChip(DownloadChoices.MissingSummary(choices, folderExists, Request.ProviderLabel), IsComplete ? DownloadChip.Success : DownloadChip.Warning);
        }
        else if (State == DownloadItemState.Ready)
        {
            SetChip(DownloadChoices.Summary(choices, folderExists, Request.ProviderLabel),
                choices.Any(choice => choice.Checked) || !folderExists ? DownloadChip.Accent : DownloadChip.Neutral);
        }

        ChecksChanged();
    }

    /// <summary>A folder chosen with Change: the item's own folder is used; any other gets a new item folder inside it.</summary>
    public void ChooseFolder(string chosen)
    {
        // A folder chosen by hand is used as it is: the new round's R folder and moves no longer apply.
        var wasNewRound = IsNewRound;
        folderChosen = true;
        if (wasNewRound)
        {
            RoundChanged();
        }

        var chosenName = Path.GetFileName(Path.TrimEndingDirectorySeparator(chosen));
        Destination = WithRevision(string.Equals(chosenName, FolderName, StringComparison.OrdinalIgnoreCase)
            || chosenName.StartsWith($"{Request.Number} ", StringComparison.OrdinalIgnoreCase)
                ? chosen
                : Path.Combine(chosen, FolderName));
        Message = Message.StartsWith("Choose where", StringComparison.Ordinal) ? "" : Message;
        ApplyChoices(resetChecks: false);
    }

    public void BeginDownload()
    {
        State = DownloadItemState.Downloading;
        SetChip("Downloading…", DownloadChip.Accent);
        Message = "";
        OnPropertyChanged(nameof(CanChangeFiles));
        foreach (var file in Files)
        {
            file.CanChange = false;
        }
    }

    public void FinishDownload(int saved, int failed, string? problem)
    {
        State = DownloadItemState.Done;
        OnPropertyChanged(nameof(CanChangeFiles));
        foreach (var file in Files)
        {
            file.CanChange = true;
        }

        HasSaved = HasSaved || saved > 0;
        SetChip(failed == 0
                ? $"Saved {saved} {(saved == 1 ? "file" : "files")}"
                : saved == 0 ? $"{failed} failed" : $"{saved} saved · {failed} failed",
            failed == 0 ? DownloadChip.Success : DownloadChip.Warning);
        Message = problem ?? (failed > 0 ? "Failed files stay checked: select Download to try them again." : "");
        OnPropertyChanged(nameof(DestinationNote));
        ChecksChanged();
    }

    private bool FolderAlreadyExists => inRevisionFolder
        ? Directory.Exists(Destination)
        : (itemFolder is not null && Directory.Exists(itemFolder))
            || (Destination is not null && Directory.Exists(Destination));

    private string WithRevision(string itemFolder)
    {
        (var folder, inRevisionFolder) = DownloadFolders.Destination(itemFolder, Revision);
        return folder;
    }

    private void SetChip(string text, DownloadChip kind)
    {
        StatusText = text;
        Chip = kind;
    }

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadFileViewModel.IsChecked) && !updatingChecks)
        {
            ChecksChanged();
        }
    }

    private void ChecksChanged()
    {
        OnPropertyChanged(nameof(IsIncluded));
        OnPropertyChanged(nameof(CheckedCount));
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }
}
