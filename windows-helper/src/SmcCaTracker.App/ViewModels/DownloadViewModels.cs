using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using SmcCaTracker.App.Services;
using SmcCaTracker.Core.Downloads;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.App.ViewModels;

/// <summary>What to download and where it would go by default.</summary>
/// <param name="ExistingFolder">The item's matched folder, if it already has one.</param>
/// <param name="ParentFolder">Where a new "&lt;number&gt; - &lt;name&gt;" folder goes (the project's RFI or Submittal folder).</param>
public sealed record DownloadRequest(
    string ItemType,
    string ItemId,
    string ProjectId,
    string Number,
    string Title,
    string ProjectName,
    string? ExistingFolder,
    string? ParentFolder);

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

    /// <summary>"report" or the Helix attachment id.</summary>
    public string Key { get; }
    public string Name { get; }
    public DateTimeOffset? UploadedAt { get; }
    public bool IsReport { get; }
    public string TargetName { get; }
    public string UploadedText => IsReport ? "Created by Helix now" : UploadedAt?.ToLocalTime().ToString("MMM d, yyyy h:mm tt") ?? "";
    public string NameToolTip => $"Saved as {TargetName}";

    /// <summary>Short-lived download link from Helix (attachments only).</summary>
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
    private string statusText = "Waiting to read from Helix";
    private DownloadChip chip = DownloadChip.Neutral;
    private string message = "";
    private bool isExpanded;
    private string? destination;
    private bool inRevisionFolder;
    private bool hasSaved;
    private bool updatingChecks;

    public DownloadItemViewModel(DownloadRequest request, bool verifying = false)
    {
        Request = request;
        Verifying = verifying;
        FolderName = DownloadNaming.FolderName(request.Number, request.Title, request.ProjectName);
        Revision = DownloadNaming.Revision(request.Title, request.Number);
        Heading = $"{(request.ItemType == ItemTypes.Submittal ? "Submittal" : "RFI")} {request.Number} · {request.Title}";
        Destination = request.ExistingFolder is not null
            ? WithRevision(request.ExistingFolder)
            : string.IsNullOrWhiteSpace(request.ParentFolder) ? null : WithRevision(Path.Combine(request.ParentFolder, FolderName));
    }

    public DownloadRequest Request { get; }

    /// <summary>Checking the item's folder against Helix: every missing file starts checked (not just new PDFs).</summary>
    public bool Verifying { get; }

    /// <summary>Files Helix has that aren't in the folder (as last compared).</summary>
    public int MissingCount { get; private set; }

    /// <summary>The folder exists and has every file Helix has for the item.</summary>
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

    /// <summary>The status chip: "3 new files", "Up to date", "Reading from Helix…", "Saved 3 files"…</summary>
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
        : inRevisionFolder ? $"Revision {Revision}: its files go in the revision subfolder"
        : Directory.Exists(Destination) ? "Existing folder; files there are never replaced"
        : "New folder";

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
        SetChip("Reading from Helix…", DownloadChip.Neutral);
        OnPropertyChanged(nameof(CanChangeFiles));
    }

    /// <summary>Lists the files Helix offers and checks the new ones (see <see cref="DownloadChoices"/>).</summary>
    public void Load(HelixItemFiles files)
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
        // Items with something new open; ones with nothing new stay folded away.
        IsExpanded = CheckedCount > 0;
    }

    /// <summary>New download links (they expire after a few minutes) for the files already listed.</summary>
    public void RefreshLinks(HelixItemFiles files)
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

        var keys = DownloadFolders.ExistingFileKeys(Request.ExistingFolder, Destination, inRevisionFolder);
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
            SetChip(DownloadChoices.MissingSummary(choices, folderExists), IsComplete ? DownloadChip.Success : DownloadChip.Warning);
        }
        else if (State == DownloadItemState.Ready)
        {
            SetChip(DownloadChoices.Summary(choices, folderExists), choices.Any(choice => choice.Checked) || !folderExists ? DownloadChip.Accent : DownloadChip.Neutral);
        }

        ChecksChanged();
    }

    /// <summary>A folder chosen with Change: the item's own folder is used; any other gets a new item folder inside it.</summary>
    public void ChooseFolder(string chosen)
    {
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
        : (Request.ExistingFolder is not null && Directory.Exists(Request.ExistingFolder))
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
