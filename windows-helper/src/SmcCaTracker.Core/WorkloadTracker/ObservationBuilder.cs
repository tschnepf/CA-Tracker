using System.Globalization;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>The desktop sending observations: its client id and app version.</summary>
public sealed record ObservationSender(Guid ClientId, string AppVersion);

/// <summary>
/// Builds Workload Tracker observations (decision D7) from what the app holds for one Helix or
/// Procore project: the lean item fields only, cut to the contract's lengths and split into parts.
/// </summary>
public static class ObservationBuilder
{
    /// <summary>The contract version this build sends; SCHEMA_VERSION in the tracker's ca_tracker/contract.py.</summary>
    public const string SchemaVersion = "1.0";

    public const int MaxItemsPerPart = 500;

    private static readonly IReadOnlyList<string> SentItemTypes = [CaItemTypeEnum.Rfi, CaItemTypeEnum.Submittal];

    /// <summary>
    /// Every item the app holds for the project, as one observation in parts (first contact, resendAll, a
    /// collapsed outbox). <c>fullSet</c> is set, so the tracker drops items the app no longer has where the
    /// last scan verified presence.
    /// </summary>
    public static IReadOnlyList<CaObservationPartRequest> Complete(
        ProviderSnapshot snapshot,
        CaProviderProjectRequest project,
        IReadOnlyList<CaBindingAssertionRequest> bindings,
        ObservationSender sender,
        Guid observationId,
        int partSize = MaxItemsPerPart)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partSize, MaxItemsPerPart);

        var types = ItemTypesOf(snapshot);
        var items = snapshot.Items
            .Where(item => types.Contains(TypeOf(item)))
            .OrderBy(item => TypeOf(item), StringComparer.Ordinal)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .Select(Item)
            .ToList();
        var coverage = Coverage(snapshot, fullSet: true);
        var chunks = items.Chunk(partSize).ToList();
        if (chunks.Count == 0)
        {
            chunks.Add([]);
        }

        return chunks
            .Select((chunk, index) => Envelope(snapshot, project, sender, observationId, CaObservationKindEnum.Items) with
            {
                Part = index + 1,
                TotalParts = chunks.Count,
                Bindings = bindings.ToList(),
                Coverage = coverage,
                Items = chunk.ToList()
            })
            .ToList();
    }

    /// <summary>Counts only, for a project that is neither bound nor tracked, so admins can see it exists.</summary>
    public static CaObservationPartRequest Catalog(
        ProviderSnapshot snapshot,
        CaProviderProjectRequest project,
        ObservationSender sender,
        Guid observationId) =>
        Envelope(snapshot, project, sender, observationId, CaObservationKindEnum.Catalog) with
        {
            Coverage = Coverage(snapshot, fullSet: false)
        };

    /// <summary>The provider project as the tracker keys it: provider, host and project id.</summary>
    public static CaProviderProjectRequest ProviderProject(ProviderSnapshot snapshot, ProviderConnection? connection, string? company)
    {
        var procore = string.Equals(snapshot.Provider, Providers.Procore, StringComparison.OrdinalIgnoreCase);
        var name = string.IsNullOrWhiteSpace(snapshot.ProjectName) ? connection?.ProjectName ?? "" : snapshot.ProjectName;
        return new CaProviderProjectRequest
        {
            Provider = procore ? CaProviderEnum.Procore : CaProviderEnum.Helix,
            Host = procore
                ? Text(string.IsNullOrWhiteSpace(connection?.Host) ? ProviderLinks.DefaultProcoreHost : connection!.Host!, 255).ToLowerInvariant()
                : ProviderLinks.HelixHost,
            AccountId = procore ? Optional(snapshot.AccountId ?? connection?.AccountId, 64) : null,
            ProjectId = Text(snapshot.ProjectId, 64),
            Name = Text(name, 300),
            Company = Optional(company, 200)
        };
    }

    /// <summary>Per item type: when it was scanned, whether that scan was complete, and how many items there are.</summary>
    public static List<CaCoverageRequest> Coverage(ProviderSnapshot snapshot, bool fullSet) =>
        ItemTypesOf(snapshot)
            .Select(type =>
            {
                var coverage = snapshot.Coverage.FirstOrDefault(entry => string.Equals(entry.Key, type, StringComparison.OrdinalIgnoreCase)).Value;
                var complete = coverage?.Complete ?? snapshot.Complete;
                var items = snapshot.Items.Where(item => TypeOf(item) == type).ToList();
                return new CaCoverageRequest
                {
                    ItemType = type,
                    ScannedAt = coverage?.ScannedAt ?? snapshot.ScannedAt,
                    DetailsComplete = complete,
                    PresenceVerified = complete,
                    FullSet = fullSet,
                    ItemCount = items.Count,
                    OpenCount = items.Count(item => !item.Closed)
                };
            })
            .ToList();

    public static CaItemRequest Item(SnapshotItem item) => new()
    {
        ItemType = TypeOf(item),
        ProviderItemId = Text(item.ItemId, 64),
        Number = Text(item.Number, 64),
        Revision = Optional(item.Revision, 32),
        Title = Text(item.Title, 500),
        Status = Text(item.Status, 100),
        Closed = item.Closed,
        DueDate = ProviderDate(item.DueAt),
        CreatedDate = Timestamp(item.CreatedAt),
        ScopeTag = ScopeTag(item),
        SourceUrl = SourceUrl(item.SourceUrl),
        Holders = item.Holders.Select(Holder).OfType<CaHolderRequest>().Take(50).ToList()
    };

    /// <summary>Base Build / Tenant Fit Out, detected the way the app filters a split job's items.</summary>
    public static string ScopeTag(SnapshotItem item) =>
        Phases.Detect($"{item.Number} {item.Title} {item.Contract}") switch
        {
            ProjectPhase.BaseBuild => CaItemScopeTagEnum.BaseBuild,
            ProjectPhase.TenantFitOut => CaItemScopeTagEnum.TenantFitOut,
            _ => CaItemScopeTagEnum.Unlabeled
        };

    /// <summary>
    /// A due date as the contract wants it. The app stores dates as instants, so a provider's plain day
    /// arrives as midnight: UTC midnight from Helix, local midnight from Procore. Those go as plain days so
    /// the tracker does not move them across a time zone; anything else goes as a date-time.
    /// </summary>
    public static string? ProviderDate(DateTimeOffset? value)
    {
        if (value is not { } date)
        {
            return null;
        }

        if (date.UtcDateTime.TimeOfDay == TimeSpan.Zero)
        {
            return date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var local = date.ToLocalTime();
        return local.TimeOfDay == TimeSpan.Zero
            ? local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : Timestamp(date);
    }

    private static string? Timestamp(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static CaObservationPartRequest Envelope(
        ProviderSnapshot snapshot,
        CaProviderProjectRequest project,
        ObservationSender sender,
        Guid observationId,
        string kind) => new()
    {
        SchemaVersion = SchemaVersion,
        AppVersion = Text(sender.AppVersion, 32),
        ClientId = sender.ClientId,
        ObservationId = observationId,
        Part = 1,
        TotalParts = 1,
        ObservationKind = kind,
        ProviderProject = project,
        Bindings = [],
        ScannedAt = snapshot.ScannedAt,
        Coverage = [],
        Collector = new CaCollectorRequest
        {
            Name = Text(snapshot.User.FullName, 200),
            Company = Optional(snapshot.User.Company, 200),
            CompanyId = Optional(snapshot.User.CompanyId, 64)
        },
        Items = [],
        Removed = []
    };

    private static CaHolderRequest? Holder(SnapshotHolder holder)
    {
        var state = holder.State?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(holder.Name) || !CaHolderStateEnum.All.Contains(state))
        {
            return null;
        }

        return new CaHolderRequest
        {
            Name = Text(holder.Name, 200),
            Company = Optional(holder.Company, 200),
            CompanyId = Optional(holder.CompanyId, 64),
            State = state!,
            Since = holder.Since
        };
    }

    private static List<string> ItemTypesOf(ProviderSnapshot snapshot)
    {
        var present = snapshot.ItemTypes.Concat(snapshot.Items.Select(item => item.ItemType))
            .Select(type => type.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
        return SentItemTypes.Where(present.Contains).ToList();
    }

    private static string TypeOf(SnapshotItem item) => item.ItemType.Trim().ToLowerInvariant();

    private static string? SourceUrl(string? url) =>
        url is { Length: <= 2048 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? url
            : null;

    private static string Text(string? value, int maxLength)
    {
        var text = (value ?? "").Trim();
        if (text.Length <= maxLength)
        {
            return text;
        }

        // Never cut between the two halves of a character outside the BMP.
        var cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..cut];
    }

    private static string? Optional(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : Text(value, maxLength);
}
