using System.Globalization;
using System.Text.Json;
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

    /// <summary>
    /// A part is closed before its body reaches this size, whatever its item count: the tracker refuses
    /// bodies over 8 MB, and an item with many holders can be tens of kilobytes (decisions section 8).
    /// </summary>
    public const int MaxPartBytes = 4 * 1024 * 1024;

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
        int partSize = MaxItemsPerPart,
        ProviderAccount? account = null,
        IReadOnlyDictionary<string, ItemFacts>? facts = null)
    {
        var types = ItemTypesOf(snapshot);
        var items = snapshot.Items.Where(item => types.Contains(TypeOf(item)));
        return Build(snapshot, project, bindings, sender, observationId, items, [], Coverage(snapshot, fullSet: true), account, partSize, facts);
    }

    /// <summary>
    /// What an outbox entry names: the items that changed and those that went away, or every item when
    /// it is a full set. Item content is read from the app's data as it is now, with the app's own facts
    /// about each item (<paramref name="facts"/>, by <see cref="OutboxEntry.ItemKey"/>) where it has them.
    /// </summary>
    public static IReadOnlyList<CaObservationPartRequest> FromOutbox(
        OutboxEntry entry,
        ProviderSnapshot snapshot,
        CaProviderProjectRequest project,
        IReadOnlyList<CaBindingAssertionRequest> bindings,
        ObservationSender sender,
        Guid observationId,
        ProviderAccount? account = null,
        int partSize = MaxItemsPerPart,
        IReadOnlyDictionary<string, ItemFacts>? facts = null)
    {
        var types = ItemTypesOf(snapshot);
        var items = snapshot.Items.Where(item => types.Contains(TypeOf(item))
            && (entry.FullSet || entry.Changed.Contains(OutboxEntry.ItemKey(item.ItemType, item.ItemId))));
        // Removals only count for a type a scan verified; the tracker refuses them otherwise.
        var removed = entry.Removed
            .Select(OutboxEntry.SplitKey)
            .Where(key => types.Contains(key.ItemType) && entry.Verified.Contains(key.ItemType))
            .OrderBy(key => key.ItemType, StringComparer.Ordinal)
            .ThenBy(key => key.ItemId, StringComparer.Ordinal)
            .Select(key => new CaRemovedItemRequest { ItemType = key.ItemType, ProviderItemId = Text(key.ItemId, 64) })
            .ToList();
        var coverage = Coverage(snapshot, entry.FullSet, itemType => entry.Verified.Contains(itemType));
        return Build(snapshot, project, bindings, sender, observationId, items, removed, coverage, account, partSize, facts);
    }

    /// <summary>Counts only, for a project that is neither bound nor tracked, so admins can see it exists.</summary>
    public static CaObservationPartRequest Catalog(
        ProviderSnapshot snapshot,
        CaProviderProjectRequest project,
        ObservationSender sender,
        Guid observationId,
        ProviderAccount? account = null) =>
        Envelope(snapshot, project, sender, observationId, CaObservationKindEnum.Catalog, account) with
        {
            Coverage = Coverage(snapshot, fullSet: false)
        };

    private static List<CaObservationPartRequest> Build(
        ProviderSnapshot snapshot,
        CaProviderProjectRequest project,
        IReadOnlyList<CaBindingAssertionRequest> bindings,
        ObservationSender sender,
        Guid observationId,
        IEnumerable<SnapshotItem> source,
        List<CaRemovedItemRequest> removed,
        List<CaCoverageRequest> coverage,
        ProviderAccount? account,
        int partSize,
        IReadOnlyDictionary<string, ItemFacts>? facts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(partSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(partSize, MaxItemsPerPart);

        var items = source
            .OrderBy(TypeOf, StringComparer.Ordinal)
            .ThenBy(item => item.ItemId, StringComparer.Ordinal)
            .Select(item => Item(item, facts?.GetValueOrDefault(OutboxEntry.ItemKey(item.ItemType, item.ItemId))))
            .ToList();
        var removedChunks = removed.Chunk(MaxRemovedPerPart).ToList();
        var envelope = Envelope(snapshot, project, sender, observationId, CaObservationKindEnum.Items, account);

        // What a part holds besides its items: the envelope, and at most the largest batch of removals.
        var fixedBytes = Size(envelope with { Part = MaxPartsPerObservation, TotalParts = MaxPartsPerObservation, Bindings = bindings.ToList(), Coverage = coverage })
            + removedChunks.Select(chunk => Size(chunk)).DefaultIfEmpty(0).Max();
        var itemChunks = ChunkItems(items, partSize, MaxPartBytes - fixedBytes);
        var total = Math.Max(1, Math.Max(itemChunks.Count, removedChunks.Count));
        return Enumerable.Range(0, total)
            .Select(index => envelope with
            {
                Part = index + 1,
                TotalParts = total,
                Bindings = bindings.ToList(),
                Coverage = coverage,
                Items = index < itemChunks.Count ? itemChunks[index].ToList() : [],
                Removed = index < removedChunks.Count ? removedChunks[index].ToList() : []
            })
            .ToList();
    }

    private const int MaxRemovedPerPart = 5000;

    private const int MaxPartsPerObservation = 200;

    /// <summary>Consecutive items, at most <paramref name="count"/> and about <paramref name="bytes"/> per chunk (never empty).</summary>
    private static List<List<CaItemRequest>> ChunkItems(List<CaItemRequest> items, int count, int bytes)
    {
        var chunks = new List<List<CaItemRequest>>();
        var current = new List<CaItemRequest>();
        var size = 0;
        foreach (var item in items)
        {
            var itemBytes = Size(item) + 1; // and its comma
            if (current.Count > 0 && (current.Count >= count || size + itemBytes > bytes))
            {
                chunks.Add(current);
                current = new List<CaItemRequest>();
                size = 0;
            }

            current.Add(item);
            size += itemBytes;
        }

        if (current.Count > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }

    private static int Size<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options).Length;

    /// <summary>The provider project as the tracker keys it: provider, host and project id.</summary>
    public static CaProviderProjectRequest ProviderProject(ProviderSnapshot snapshot, ProviderConnection? connection, string? company)
    {
        var procore = string.Equals(snapshot.Provider, Providers.Procore, StringComparison.OrdinalIgnoreCase);
        var name = string.IsNullOrWhiteSpace(snapshot.ProjectName) ? connection?.ProjectName ?? "" : snapshot.ProjectName;
        return new CaProviderProjectRequest
        {
            Provider = procore ? CaProviderEnum.Procore : CaProviderEnum.Helix,
            Host = Host(snapshot.Provider, connection?.Host),
            AccountId = procore ? Optional(snapshot.AccountId ?? connection?.AccountId, 64) : null,
            ProjectId = Text(snapshot.ProjectId, 64),
            Name = Text(name, 300),
            Company = Optional(company, 200)
        };
    }

    /// <summary>Per item type: when it was scanned, whether that scan was complete, and how many items there are.</summary>
    public static List<CaCoverageRequest> Coverage(ProviderSnapshot snapshot, bool fullSet, Func<string, bool>? presenceVerified = null) =>
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
                    // A complete read verified presence; an outbox entry also knows of scans that saw items leave.
                    PresenceVerified = presenceVerified?.Invoke(type) ?? complete,
                    FullSet = fullSet,
                    ItemCount = items.Count,
                    OpenCount = items.Count(item => !item.Closed)
                };
            })
            .ToList();

    /// <summary>
    /// One item. With <paramref name="facts"/> it carries the app's discipline and folder status (nulls
    /// clear the tracker's values); without, those four fields are left out and the tracker keeps what it
    /// has (see <see cref="ContractJson"/>).
    /// </summary>
    public static CaItemRequest Item(SnapshotItem item, ItemFacts? facts = null) => new()
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
        Holders = item.Holders.Select(Holder).OfType<CaHolderRequest>().Take(50).ToList(),
        Discipline = facts?.Discipline,
        DisciplineSource = facts?.DisciplineSource,
        FolderStatus = facts?.FolderStatus,
        FolderPath = facts?.FolderPath
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
        string kind,
        ProviderAccount? account) => new()
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
        Collector = Collector(snapshot.User, account),
        Items = [],
        Removed = []
    };

    /// <summary>Who scanned: the scan's own user (Helix), else the site's signed-in account (Procore scans carry none).</summary>
    private static CaCollectorRequest Collector(SnapshotUser? user, ProviderAccount? account)
    {
        var scanned = !string.IsNullOrWhiteSpace(user?.FullName);
        return new CaCollectorRequest
        {
            Name = Text(scanned ? user!.FullName : account?.UserName, 200),
            Company = Optional(scanned ? user!.Company : account?.Company, 200),
            CompanyId = Optional(user?.CompanyId, 64),
            ProviderUserId = Optional(user?.ProviderUserId ?? account?.ProviderUserId, 64),
            Email = Optional(user?.Email ?? account?.Email, 254)
        };
    }

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
            ProviderUserId = Optional(holder.ProviderUserId, 64),
            Email = Optional(holder.Email, 254),
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

    /// <summary>The host the tracker keys a provider project by: the Procore instance, or Helix's one host.</summary>
    public static string Host(string provider, string? connectionHost) =>
        string.Equals(provider, Providers.Procore, StringComparison.OrdinalIgnoreCase)
            ? Text(string.IsNullOrWhiteSpace(connectionHost) ? ProviderLinks.DefaultProcoreHost : connectionHost, 255).ToLowerInvariant()
            : ProviderLinks.HelixHost;

    /// <summary>
    /// An item as assignments name it (decision D4): provider project, type and number, the same
    /// values its observations carry. Null for an item the tracker isn't sent.
    /// </summary>
    public static WorkloadItemRef? ItemRef(int trackerProjectId, ProviderConnection connection, SnapshotItem item)
    {
        var type = TypeOf(item);
        var number = Text(item.Number, 64);
        if (!SentItemTypes.Contains(type) || number.Length == 0)
        {
            return null;
        }

        var procore = string.Equals(connection.Provider, Providers.Procore, StringComparison.OrdinalIgnoreCase);
        return new WorkloadItemRef(
            trackerProjectId,
            procore ? CaProviderEnum.Procore : CaProviderEnum.Helix,
            Host(connection.Provider, connection.Host),
            Text(connection.ProjectId, 64),
            type,
            number);
    }

    /// <summary>
    /// An item revision as Workload Tracker keys it (<see cref="WorkloadCompletion.ItemKey"/>), from the
    /// values its observations carry, so marks read back match the items they were made on.
    /// </summary>
    public static string RevisionKey(ProviderConnection connection, SnapshotItem item) =>
        WorkloadCompletion.ItemKeyOf(
            string.Equals(connection.Provider, Providers.Procore, StringComparison.OrdinalIgnoreCase) ? CaProviderEnum.Procore : CaProviderEnum.Helix,
            Text(connection.ProjectId, 64),
            TypeOf(item),
            Text(item.ItemId, 64));

    /// <summary>The revision as sent: null when there is none.</summary>
    public static string? RevisionOf(SnapshotItem item) => Optional(item.Revision, 32);

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
