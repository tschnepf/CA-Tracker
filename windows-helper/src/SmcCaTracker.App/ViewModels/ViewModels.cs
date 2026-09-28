using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Operations;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ProjectViewModel : ObservableObject
{
    private ProviderSnapshot? snapshot;
    private ProjectMapping? mapping;
    private int attentionCount;
    private bool compared;

    public ProjectViewModel(string provider, string projectId)
    {
        Provider = provider;
        ProjectId = projectId;
    }

    public string Provider { get; }
    public string ProjectId { get; }
    public string Key => $"{Provider}|{ProjectId}";
    public string ProviderLabel => Providers.Label(Provider);

    public ProviderSnapshot? Snapshot
    {
        get => snapshot;
        set
        {
            if (Set(ref snapshot, value))
            {
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public ProjectMapping? Mapping
    {
        get => mapping;
        set
        {
            if (Set(ref mapping, value))
            {
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(HasCaFolder));
            }
        }
    }

    public string Name => !string.IsNullOrWhiteSpace(Snapshot?.ProjectName) ? Snapshot!.ProjectName
        : !string.IsNullOrWhiteSpace(Mapping?.ProjectName) ? Mapping!.ProjectName
        : ProjectId;

    public bool HasCaFolder => !string.IsNullOrWhiteSpace(Mapping?.CaFolder);

    public string Subtitle
    {
        get
        {
            var data = Snapshot is null ? "no item data yet" : $"data {Formatting.When(Snapshot.ScannedAt)}";
            var folder = HasCaFolder ? "" : " · no CA folder";
            return $"{ProviderLabel} · {data}{folder}";
        }
    }

    public int AttentionCount
    {
        get => attentionCount;
        set
        {
            if (Set(ref attentionCount, value))
            {
                OnPropertyChanged(nameof(BadgeText));
                OnPropertyChanged(nameof(HasBadge));
            }
        }
    }

    public bool Compared
    {
        get => compared;
        set
        {
            if (Set(ref compared, value))
            {
                OnPropertyChanged(nameof(HasBadge));
            }
        }
    }

    public bool HasBadge => Compared && AttentionCount > 0;
    public string BadgeText => AttentionCount.ToString();
}

public sealed class RowViewModel : ObservableObject
{
    private bool isChecked;

    public RowViewModel(ComparisonRow row, CompanyIdentity company, FolderLayout folders)
    {
        Row = row;
        CourtText = row.Item is null ? "" : CourtEvaluator.Describe(row.Court, company);
        LocationText = row.Folder is null ? ""
            : row.Folder.Location == FolderLocation.Returned
                ? $"{folders.TypeFolder(row.ItemType)}\\{folders.ReturnedFolder}"
                : folders.TypeFolder(row.ItemType);
    }

    public ComparisonRow Row { get; }

    public bool IsChecked
    {
        get => isChecked;
        set => Set(ref isChecked, value && CanSelect);
    }

    public bool CanSelect => Row.Folder is not null;
    public RowStatus Status => Row.Status;
    public string StatusText => ComparisonRow.StatusLabel(Row.Status);
    public string Number => Row.Item?.Number ?? "—";
    public string Title => Row.Item?.Title ?? "";
    public string ProviderStatus => Row.Item?.Status ?? "";
    public string CourtText { get; }
    public string FolderName => Row.Folder?.Name ?? "—";
    public string LocationText { get; }
    public string Detail => Row.Detail;
    public bool HasFolder => Row.Folder is not null;
    public string? SourceUrl => Row.Item?.SourceUrl;

    public bool HasLink => Uri.TryCreate(SourceUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    public string ToolTipText => string.IsNullOrEmpty(Detail) ? StatusText : $"{StatusText}. {Detail}";
}

public sealed class OperationRowViewModel
{
    public OperationRowViewModel(PlannedOperation operation, OperationResult? result)
    {
        Operation = operation;
        Glyph = operation.Kind == OperationKind.CreateFolder ? "+" : "→";
        Title = operation.Title;
        FromText = operation.SourcePath is null ? "" : $"From  {operation.SourcePath}";
        ToText = operation.Kind == OperationKind.CreateFolder ? $"Create  {operation.DestinationPath}" : $"To      {operation.DestinationPath}";
        Warning = result is null ? operation.Warning : null;
        Blocked = result is null && operation.IsBlocked ? $"Will be skipped: {operation.BlockedReason}" : null;
        if (result is not null)
        {
            Outcome = result.Outcome switch
            {
                OperationOutcome.Done => $"Done — {result.Message}",
                OperationOutcome.Skipped => $"Skipped — {result.Message}",
                _ => $"Failed — {result.Message}"
            };
            OutcomeKind = result.Outcome.ToString();
        }
    }

    public PlannedOperation Operation { get; }
    public string Glyph { get; }
    public string Title { get; }
    public string FromText { get; }
    public string ToText { get; }
    public string? Warning { get; }
    public string? Blocked { get; }
    public string? Outcome { get; }
    public string OutcomeKind { get; } = "";
    public bool HasFrom => FromText.Length > 0;
    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public bool HasBlocked => !string.IsNullOrEmpty(Blocked);
    public bool HasOutcome => !string.IsNullOrEmpty(Outcome);
}

public static class Formatting
{
    public static string When(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return local.Date == DateTime.Today
            ? $"today {local:h:mm tt}"
            : local.Date == DateTime.Today.AddDays(-1)
                ? $"yesterday {local:h:mm tt}"
                : $"{local:MMM d, h:mm tt}";
    }
}
