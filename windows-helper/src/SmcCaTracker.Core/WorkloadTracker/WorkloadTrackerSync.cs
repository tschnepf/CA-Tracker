using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker.Contract;

namespace SmcCaTracker.Core.WorkloadTracker;

/// <summary>
/// Pairs the app with Workload Tracker and sends the outbox (decisions D2, D7 and D8). Projects set
/// up in the app send their items, with the tracker project each is bound to (D3); every other project
/// seen sends a catalog entry (counts only). Item assignments (D4) are sent and read back here too.
/// Unpaired, nothing here runs and the app works on its own.
/// </summary>
public sealed class WorkloadTrackerSync
{
    /// <summary>A scan that changed nothing still tells the tracker it happened, but not more often than this.</summary>
    public static readonly TimeSpan FreshnessInterval = TimeSpan.FromMinutes(30);

    /// <summary>A catalog entry is sent again only when its counts change or after this long.</summary>
    public static readonly TimeSpan CatalogInterval = TimeSpan.FromHours(6);

    /// <summary>A bound project's assignments are read back at most this often (an ETag makes an unchanged read cheap).</summary>
    public static readonly TimeSpan AssignmentReadInterval = TimeSpan.FromMinutes(5);

    /// <summary>A project's team is fetched again after this long.</summary>
    public static readonly TimeSpan TeamCacheAge = TimeSpan.FromMinutes(10);

    /// <summary>The tracker's largest batch of assignment changes (MAX_ASSIGNMENTS in its contract.py).</summary>
    public const int MaxAssignmentsPerBatch = 500;

    private const int MaxTeamPages = 50;

    private static readonly TimeSpan QuarantineAfterRefusal = TimeSpan.FromHours(24);

    private readonly TrackerStore store;
    private readonly WorkloadTrackerApi api;
    private readonly ISecretProtector protector;
    private readonly Func<DateTimeOffset> clock;
    private readonly SemaphoreSlim running = new(1, 1);
    private readonly Dictionary<string, (long Version, Guid ObservationId)> pendingIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> freshnessSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTimeOffset At, string Counts)> catalogSent = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> partSizes = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? resendAt;
    private DateTimeOffset? assignmentsRefusedUntil;

    // Read and written from the UI's calls and the sending thread.
    private readonly object cacheGate = new();
    private readonly Dictionary<int, (DateTimeOffset At, string? ETag)> assignmentReads = new();
    private readonly Dictionary<int, (DateTimeOffset At, IReadOnlyList<TeamMember> Members)> teams = new();

    public WorkloadTrackerSync(TrackerStore store, WorkloadTrackerApi api, ISecretProtector protector, Func<DateTimeOffset>? clock = null)
    {
        this.store = store;
        this.api = api;
        this.protector = protector;
        this.clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>Raised (on the sending thread) after a send round, a connect or a disconnect.</summary>
    public event EventHandler? StatusChanged;

    public bool IsPaired => store.Settings.WorkloadTracker.IsPaired;

    /// <summary>The token was refused (expired, revoked or copied): connect again.</summary>
    public bool NeedsSignIn { get; private set; }

    /// <summary>The tracker wants a newer version of this app.</summary>
    public bool NeedsUpdate { get; private set; }

    public string? LastProblem { get; private set; }
    public DateTimeOffset? LastSentAt { get; private set; }

    /// <summary>Projects with something waiting to be sent.</summary>
    public int PendingCount => store.Outbox().Count;

    /// <summary>Assignments changed here and not sent yet.</summary>
    public int PendingAssignmentCount => store.PendingAssignments().Count;

    /// <summary>The Workload Tracker person this PC is connected as (for "Assigned to me"), when the account has one.</summary>
    public int? PersonId => IsPaired ? store.Settings.WorkloadTracker.PersonId : null;

    /// <summary>
    /// Pairs with the tracker at <paramref name="address"/> using a token from its Profile page. Checks
    /// the token, remembers who it belongs to and queues every project for its first send. Returns null
    /// when it worked, otherwise what went wrong.
    /// </summary>
    public async Task<string?> ConnectAsync(string address, string token, int? sessionId, DateTimeOffset? expiresAt, CancellationToken cancel = default)
    {
        var normalized = WorkloadTrackerApi.NormalizeAddress(address);
        if (normalized is null)
        {
            return "Enter the Workload Tracker address, starting with https://.";
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return "No token was received from Workload Tracker.";
        }

        var settings = store.SettingsCopy();
        var sync = settings.WorkloadTracker;
        if (!Guid.TryParse(sync.ClientId, out _))
        {
            sync.ClientId = Guid.NewGuid().ToString("D");
        }

        var me = await api.GetMeAsync(normalized, token.Trim(), sync.ClientId, cancel).ConfigureAwait(false);
        var identity = WorkloadTrackerApi.ReadIdentity(me);
        if (identity is null)
        {
            return me.NetworkError is not null
                ? $"Couldn't reach Workload Tracker: {me.NetworkError}"
                : $"Workload Tracker didn't accept the token ({me.Detail ?? $"HTTP {me.Status}"}).";
        }

        var account = $"{new Uri(normalized).Authority}#{identity.UserId}";
        var accountChanged = !string.Equals(sync.Account, account, StringComparison.Ordinal);
        if (sync.Address.Length > 0 && !string.Equals(sync.Address, normalized, StringComparison.OrdinalIgnoreCase))
        {
            // Another tracker: its project ids name other projects.
            sync.Bindings.Clear();
        }

        sync.Address = normalized;
        sync.TokenProtected = protector.Protect(token.Trim());
        sync.SessionId = sessionId;
        sync.ExpiresAt = expiresAt;
        sync.Account = account;
        sync.AccountName = identity.UserName;
        sync.PersonId = identity.PersonId;
        sync.PersonName = identity.PersonName;
        store.SaveSettings(settings);

        NeedsSignIn = false;
        NeedsUpdate = false;
        LastProblem = null;
        if (accountChanged)
        {
            store.ClearOutbox();
            store.ClearAssignments();
            ForgetReads();
        }

        // First contact: everything this PC holds goes once, so the tracker fills straight away.
        store.QueueFullSets();
        AppLog.Info("workload", $"Connected to {normalized} as {identity.UserName}.");
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>Stops sending: revokes this PC's session when the tracker can be reached, forgets the token and empties the outbox.</summary>
    public async Task DisconnectAsync(CancellationToken cancel = default)
    {
        var settings = store.SettingsCopy();
        var sync = settings.WorkloadTracker;
        var token = sync.TokenProtected.Length > 0 ? protector.Unprotect(sync.TokenProtected) : null;
        if (token is not null && sync.SessionId is { } sessionId && sync.Address.Length > 0)
        {
            var result = await api.RevokeSessionAsync(sync.Address, token, sync.ClientId, sessionId, cancel).ConfigureAwait(false);
            if (!result.Ok)
            {
                AppLog.Warn("workload", $"Couldn't revoke the Workload Tracker session ({result.NetworkError ?? $"HTTP {result.Status}"}).");
            }
        }

        sync.TokenProtected = "";
        sync.SessionId = null;
        sync.ExpiresAt = null;
        sync.Account = "";
        sync.AccountName = null;
        sync.PersonId = null;
        sync.PersonName = null;
        store.SaveSettings(settings);
        store.ClearOutbox();
        store.ClearAssignments();
        ForgetReads();
        NeedsSignIn = false;
        NeedsUpdate = false;
        LastProblem = null;
        pendingIds.Clear();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Sends what is due in the outbox, then assignment changes, then reads back bound projects'
    /// assignments when due (or now, with <paramref name="readAssignments"/>). Safe to call often: one
    /// round runs at a time.
    /// </summary>
    public async Task SendAsync(CancellationToken cancel = default, bool readAssignments = false)
    {
        if (!await running.WaitAsync(0, cancel).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var sync = store.Settings.WorkloadTracker;
            if (!sync.IsPaired || NeedsSignIn || NeedsUpdate)
            {
                return;
            }

            var token = protector.Unprotect(sync.TokenProtected);
            if (string.IsNullOrEmpty(token))
            {
                NeedsSignIn = true;
                LastProblem = "The Workload Tracker token can't be read on this PC. Connect again.";
                return;
            }

            if (resendAt is { } due && clock() >= due)
            {
                resendAt = null;
                store.QueueFullSets();
            }

            var going = true;
            foreach (var entry in store.Outbox().OrderBy(entry => entry.UpdatedAt))
            {
                if (entry.NextAttemptAt is { } next && next > clock())
                {
                    continue;
                }

                if (!await SendEntryAsync(entry, sync, token, cancel).ConfigureAwait(false))
                {
                    going = false;
                    break;
                }
            }

            // Assignments after the observations, so a binding made in the same round is in place.
            if (going && await SendAssignmentsAsync(sync, token, cancel).ConfigureAwait(false))
            {
                await ReadAssignmentsAsync(sync, token, readAssignments, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            running.Release();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Sends one project's entry. False when sending should stop for now (signed out, tracker down).</summary>
    private async Task<bool> SendEntryAsync(OutboxEntry entry, WorkloadTrackerSettings sync, string token, CancellationToken cancel)
    {
        var key = $"{entry.Provider}|{entry.ProjectId}";
        var snapshot = store.Snapshot(entry.Provider, entry.ProjectId);
        if (snapshot is null)
        {
            store.CompleteOutbox(entry.Provider, entry.ProjectId, entry.Version);
            return true;
        }

        var connection = store.Settings.FindByConnection(entry.Provider, entry.ProjectId)?.Connection;
        var company = store.Catalog().FirstOrDefault(project =>
            string.Equals(project.Provider, entry.Provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(project.ProjectId, entry.ProjectId, StringComparison.Ordinal))?.AccountName;
        var project = ObservationBuilder.ProviderProject(snapshot, connection, company);
        var account = store.Account(entry.Provider);
        var sender = new ObservationSender(Guid.Parse(sync.ClientId), TrackerVersion.Current);
        var now = clock();

        if (connection is null)
        {
            // Not set up in this app: a catalog entry, so admins can see the project exists.
            var counts = string.Join("/", ObservationBuilder.Coverage(snapshot, fullSet: false).Select(coverage => $"{coverage.ItemType}:{coverage.ItemCount}:{coverage.OpenCount}"));
            if (catalogSent.TryGetValue(key, out var lastCatalog) && lastCatalog.Counts == counts && now - lastCatalog.At < CatalogInterval)
            {
                store.CompleteOutbox(entry.Provider, entry.ProjectId, entry.Version);
                return true;
            }

            var catalog = ObservationBuilder.Catalog(snapshot, project, sender, Guid.NewGuid(), account);
            var result = await api.PostObservationAsync(sync.Address, token, sync.ClientId, catalog, cancel).ConfigureAwait(false);
            if (!result.Ok)
            {
                return Handle(result, entry, key);
            }

            catalogSent[key] = (now, counts);
            Sent(entry, result);
            return true;
        }

        var freshnessOnly = !entry.FullSet && entry.Changed.Count == 0 && entry.Removed.Count == 0;
        if (freshnessOnly && freshnessSent.TryGetValue(key, out var lastFresh) && now - lastFresh < FreshnessInterval)
        {
            return true; // kept for later: a later scan or the next interval sends it
        }

        // The same observation id while the entry is unchanged, so a retry after a failed part completes it.
        if (!pendingIds.TryGetValue(key, out var pending) || pending.Version != entry.Version)
        {
            pending = (entry.Version, Guid.NewGuid());
            pendingIds[key] = pending;
        }

        var partSize = partSizes.TryGetValue(key, out var size) ? size : ObservationBuilder.MaxItemsPerPart;
        var bindings = BindingsFor(store.Settings, entry.Provider, entry.ProjectId);
        var parts = ObservationBuilder.FromOutbox(entry, snapshot, project, bindings, sender, pending.ObservationId, account, partSize);
        ApiResult? last = null;
        foreach (var part in parts)
        {
            last = await api.PostObservationAsync(sync.Address, token, sync.ClientId, part, cancel).ConfigureAwait(false);
            if (!last.Ok)
            {
                return Handle(last, entry, key);
            }
        }

        pendingIds.Remove(key);
        freshnessSent[key] = now;
        Sent(entry, last);
        if (bindings.Count > 0)
        {
            RecordBindingResults(entry.Provider, entry.ProjectId, last);
        }

        return true;
    }

    // ---- Bindings (decision D3) -----------------------------------------------------------------

    /// <summary>
    /// The bindings this PC asserts for a Helix or Procore project: one per local project entry linked
    /// to it and bound, each scope once. The tracker takes a whole-project binding or scoped ones, not both.
    /// </summary>
    public static List<CaBindingAssertionRequest> BindingsFor(TrackerSettings settings, string provider, string projectId)
    {
        var bindings = new List<CaBindingAssertionRequest>();
        foreach (var project in LinkedProjects(settings, provider, projectId))
        {
            if (!settings.WorkloadTracker.Bindings.TryGetValue(project.Id, out var binding))
            {
                continue;
            }

            var scope = BindingScope(project);
            if (bindings.Any(existing => existing.Scope == scope))
            {
                continue;
            }

            bindings.Add(new CaBindingAssertionRequest
            {
                TrackerProjectId = binding.ProjectId,
                Scope = scope,
                IncludeUnlabeled = scope == CaBindingScopeEnum.Whole || project.Scope.IncludeUnlabeled
            });
        }

        if (bindings.Count > 1)
        {
            bindings.RemoveAll(binding => binding.Scope == CaBindingScopeEnum.Whole);
        }

        return bindings;
    }

    /// <summary>
    /// The part of the provider project a local entry covers: all of it, unless the entry is Base
    /// Build or Tenant Fit Out and picks its items out of a project that holds both.
    /// </summary>
    public static string BindingScope(TrackerProject project) =>
        !project.Scope.IsSplit || !project.Scope.FilterItems
            ? CaBindingScopeEnum.Whole
            : project.Scope.Phase == ProjectPhase.BaseBuild ? CaBindingScopeEnum.BaseBuild : CaBindingScopeEnum.TenantFitOut;

    /// <summary>
    /// Binds a local project entry to an existing tracker project, or unbinds it (null). A new binding
    /// sends the entry's items again in full, so the tracker files them under the project at once.
    /// Unbinding stops this PC asserting it; the tracker keeps the binding until someone changes it.
    /// </summary>
    public void SetBinding(string localProjectId, TrackerProjectChoice? choice)
    {
        ProviderConnection? connection = null;
        var rebound = store.UpdateSettings(settings =>
        {
            var project = settings.FindProject(localProjectId);
            if (project is null)
            {
                return false;
            }

            connection = project.Connection;
            var bindings = settings.WorkloadTracker.Bindings;
            if (choice is null)
            {
                return bindings.Remove(localProjectId);
            }

            if (bindings.TryGetValue(localProjectId, out var kept) && kept.ProjectId == choice.Id
                && kept.ProjectNumber == choice.Number && kept.ProjectName == choice.Name)
            {
                return false;
            }

            bindings[localProjectId] = new WorkloadBinding { ProjectId = choice.Id, ProjectNumber = choice.Number, ProjectName = choice.Name };
            return true;
        });

        if (rebound && choice is not null && connection is not null)
        {
            store.QueueFullSets([(connection.Provider, connection.ProjectId)]);
            AppLog.Info("workload", $"Bound {localProjectId} to tracker project {choice.Id}.");
        }
    }

    /// <summary>On a conflict, takes the binding the tracker uses (someone else's choice) for this entry.</summary>
    public bool UseTheirBinding(string localProjectId)
    {
        if (!store.Settings.WorkloadTracker.Bindings.TryGetValue(localProjectId, out var binding)
            || binding.TheirProjectId is not { } theirs || binding.TheirProjectName is null)
        {
            return false;
        }

        SetBinding(localProjectId, new TrackerProjectChoice(theirs, binding.TheirProjectNumber, binding.TheirProjectName, null, null));
        return true;
    }

    private static IEnumerable<TrackerProject> LinkedProjects(TrackerSettings settings, string provider, string projectId) =>
        settings.Projects.Where(project => project.Connection is not null
            && string.Equals(project.Connection.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(project.Connection.ProjectId, projectId, StringComparison.Ordinal));

    /// <summary>Keeps what the tracker said about each binding sent, so Edit project can show a conflict.</summary>
    private void RecordBindingResults(string provider, string projectId, ApiResult? result)
    {
        if (result?.TryRead<CaObservationResult>() is not { Bindings: { Count: > 0 } answers })
        {
            return;
        }

        store.UpdateSettings(settings =>
        {
            var changed = false;
            foreach (var project in LinkedProjects(settings, provider, projectId))
            {
                if (!settings.WorkloadTracker.Bindings.TryGetValue(project.Id, out var binding))
                {
                    continue;
                }

                var scope = BindingScope(project);
                var answer = answers.FirstOrDefault(entry => entry.Scope == scope && entry.AssertedProjectId == binding.ProjectId);
                if (answer is null)
                {
                    continue;
                }

                var outcome = answer.Outcome switch
                {
                    CaBindingOutcomeEnum.Conflict => WorkloadBinding.Conflict,
                    CaBindingOutcomeEnum.Rejected => WorkloadBinding.Rejected,
                    _ => WorkloadBinding.Bound
                };
                var theirs = outcome == WorkloadBinding.Conflict ? answer.Effective : null;
                var number = binding.ProjectNumber;
                var name = binding.ProjectName;
                if (outcome == WorkloadBinding.Bound && answer.Effective is { } effective && effective.TrackerProjectId == binding.ProjectId)
                {
                    // Keep the label current when the project is renamed in the tracker.
                    number = effective.ProjectNumber;
                    name = effective.ProjectName;
                }

                if (binding.Outcome == outcome && binding.TheirProjectId == theirs?.TrackerProjectId
                    && binding.TheirAssertedBy == theirs?.AssertedByName && binding.ProjectNumber == number && binding.ProjectName == name)
                {
                    continue;
                }

                if (outcome != WorkloadBinding.Bound)
                {
                    AppLog.Warn("workload", $"Binding of {project.Name} to tracker project {binding.ProjectId}: {answer.Outcome}.");
                }

                binding.Outcome = outcome;
                binding.TheirProjectId = theirs?.TrackerProjectId;
                binding.TheirProjectNumber = theirs?.ProjectNumber;
                binding.TheirProjectName = theirs?.ProjectName;
                binding.TheirAssertedBy = theirs?.AssertedByName;
                binding.ProjectNumber = number;
                binding.ProjectName = name;
                changed = true;
            }

            return changed;
        });
    }

    // ---- Project search and teams ---------------------------------------------------------------

    /// <summary>Running tracker projects matching <paramref name="query"/>, for binding. Problem says why there are none.</summary>
    public async Task<(IReadOnlyList<TrackerProjectChoice> Projects, bool More, string? Problem)> SearchProjectsAsync(
        string query, int page = 1, CancellationToken cancel = default)
    {
        if (Credentials() is not { } credentials)
        {
            return ([], false, NotConnectedText);
        }

        var result = await api.SearchProjectsAsync(credentials.Sync.Address, credentials.Token, credentials.Sync.ClientId, query, page, cancel)
            .ConfigureAwait(false);
        if (!result.Ok)
        {
            return ([], false, Explain(result));
        }

        var (projects, more) = WorkloadTrackerApi.ReadProjectChoices(result);
        return (projects, more, null);
    }

    /// <summary>
    /// The people on a tracker project's team (its assignments, every page), who can be assigned its
    /// items. Kept for <see cref="TeamCacheAge"/>; <paramref name="refresh"/> fetches it again.
    /// </summary>
    public async Task<(IReadOnlyList<TeamMember> Members, string? Problem)> TeamAsync(int trackerProjectId, bool refresh = false, CancellationToken cancel = default)
    {
        (DateTimeOffset At, IReadOnlyList<TeamMember> Members) cached;
        bool found;
        lock (cacheGate)
        {
            found = teams.TryGetValue(trackerProjectId, out cached);
        }

        if (found && !refresh && clock() - cached.At < TeamCacheAge)
        {
            return (cached.Members, null);
        }

        if (Credentials() is not { } credentials)
        {
            return (found ? cached.Members : [], NotConnectedText);
        }

        var people = new Dictionary<int, (string Name, string? Department, List<string> Roles)>();
        for (var page = 1; page <= MaxTeamPages; page++)
        {
            var result = await api.GetTeamPageAsync(credentials.Sync.Address, credentials.Token, credentials.Sync.ClientId, trackerProjectId, page, cancel)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                return (found ? cached.Members : [], Explain(result));
            }

            var (members, more) = WorkloadTrackerApi.ReadTeamPage(result);
            foreach (var member in members)
            {
                // Someone with several roles on the project is listed once, with each role.
                if (!people.TryGetValue(member.PersonId, out var person))
                {
                    person = (member.Name, member.Department, new List<string>());
                    people[member.PersonId] = person;
                }

                if (!string.IsNullOrWhiteSpace(member.Role) && !person.Roles.Contains(member.Role, StringComparer.OrdinalIgnoreCase))
                {
                    person.Roles.Add(member.Role);
                }
            }

            if (!more)
            {
                break;
            }
        }

        var team = people
            .Select(pair => new TeamMember(pair.Key, pair.Value.Name, pair.Value.Department,
                pair.Value.Roles.Count == 0 ? null : string.Join(", ", pair.Value.Roles)))
            .OrderBy(member => member.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        lock (cacheGate)
        {
            teams[trackerProjectId] = (clock(), team);
        }

        return (team, null);
    }

    // ---- Item assignments (decision D4) ---------------------------------------------------------

    /// <summary>
    /// Assigns people to items and unassigns others. Kept here at once (shown straight away) and sent
    /// with the next round; the latest change wins on the tracker.
    /// </summary>
    public void ChangeAssignments(IReadOnlyList<WorkloadItemRef> items, IReadOnlyList<TeamMember> assign, IReadOnlyList<int> unassign)
    {
        // Milliseconds only: the tracker keeps microseconds, and a finer time here would never match its copy.
        var now = clock().ToUniversalTime();
        now = new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        var by = store.Settings.WorkloadTracker.PersonName ?? store.Settings.WorkloadTracker.AccountName;
        var changes = new List<WorkloadAssignment>();
        foreach (var item in items)
        {
            changes.AddRange(assign.Select(person => Row(item, person.PersonId, person.Name, active: true)));
            changes.AddRange(unassign.Select(personId => Row(item, personId, "", active: false)));
        }

        store.ChangeAssignments(changes);

        WorkloadAssignment Row(WorkloadItemRef item, int personId, string name, bool active) => new()
        {
            TrackerProjectId = item.TrackerProjectId,
            Provider = item.Provider,
            Host = item.Host,
            ProviderProjectId = item.ProviderProjectId,
            ItemType = item.ItemType,
            Number = item.Number,
            PersonId = personId,
            PersonName = name,
            Active = active,
            AssignedAt = now,
            AssignedByName = by
        };
    }

    /// <summary>Sends assignment changes made here. False when the round should stop (signed out, tracker down).</summary>
    private async Task<bool> SendAssignmentsAsync(WorkloadTrackerSettings sync, string token, CancellationToken cancel)
    {
        if (assignmentsRefusedUntil is { } until && clock() < until)
        {
            return true;
        }

        foreach (var batch in store.PendingAssignments().Chunk(MaxAssignmentsPerBatch))
        {
            var request = new CaAssignmentBatchRequest
            {
                Assignments = batch.Select(row => new CaAssignmentWriteRequest
                {
                    TrackerProjectId = row.TrackerProjectId,
                    ProviderProject = new CaProviderProjectRefRequest { Provider = row.Provider, Host = row.Host, ProjectId = row.ProviderProjectId },
                    ItemType = row.ItemType,
                    Number = row.Number,
                    PersonId = row.PersonId,
                    Active = row.Active,
                    AssignedAt = row.AssignedAt
                }).ToList()
            };
            var result = await api.PostAssignmentsAsync(sync.Address, token, sync.ClientId, request, cancel).ConfigureAwait(false);
            if (!result.Ok)
            {
                return HandleAssignmentFailure(result);
            }

            if (result.TryRead<CaAssignmentBatchResult>() is not { } answer)
            {
                LastProblem = "Workload Tracker's answer about assignments couldn't be read.";
                return false;
            }

            var outcomes = new List<(WorkloadAssignment Sent, WorkloadAssignment? Tracker)>();
            foreach (var row in answer.Results.Where(row => row.Index >= 0 && row.Index < batch.Length))
            {
                var sent = batch[row.Index];
                if (row.Outcome == CaAssignmentOutcomeEnum.Rejected)
                {
                    LastProblem = $"Couldn't {(sent.Active ? "assign" : "unassign")} {Named(sent)} on {sent.ItemType.ToUpperInvariant()} {sent.Number}: {RejectionText(row.Reason)}";
                    AppLog.Warn("workload", $"Assignment refused ({row.Reason}): tracker project {sent.TrackerProjectId}, {sent.LineageKey}, person {sent.PersonId}.");
                    lock (cacheGate)
                    {
                        // Read the project in full next time, to show what the tracker has instead.
                        assignmentReads.Remove(sent.TrackerProjectId);
                    }
                }

                outcomes.Add((sent, row.Outcome == CaAssignmentOutcomeEnum.Rejected || row.Assignment is null ? null : FromContract(row.Assignment)));
            }

            store.CompleteAssignments(outcomes);
        }

        return true;
    }

    /// <summary>Reads back each bound tracker project's assignments, when due or when asked.</summary>
    private async Task ReadAssignmentsAsync(WorkloadTrackerSettings sync, string token, bool now, CancellationToken cancel)
    {
        foreach (var trackerProjectId in sync.Bindings.Values.Select(binding => binding.ProjectId).Distinct().ToList())
        {
            (DateTimeOffset At, string? ETag) last;
            bool read;
            lock (cacheGate)
            {
                read = assignmentReads.TryGetValue(trackerProjectId, out last);
            }

            if (read && !now && clock() - last.At < AssignmentReadInterval)
            {
                continue;
            }

            var result = await api.GetAssignmentsAsync(sync.Address, token, sync.ClientId, trackerProjectId, read ? last.ETag : null, cancel)
                .ConfigureAwait(false);
            if (result.Status == 304)
            {
                Remember(trackerProjectId, last.ETag);
                continue;
            }

            if (result.Status == 404)
            {
                // Not a project this account can see (any more): nothing to show for it.
                store.ReplaceAssignments(trackerProjectId, []);
                Remember(trackerProjectId, null);
                continue;
            }

            if (!result.Ok)
            {
                HandleAssignmentFailure(result);
                return;
            }

            if (result.TryRead<CaProjectAssignments>() is { } body)
            {
                store.ReplaceAssignments(trackerProjectId, body.Assignments.Select(FromContract).ToList());
                Remember(trackerProjectId, result.ETag);
            }
        }

        void Remember(int trackerProjectId, string? etag)
        {
            lock (cacheGate)
            {
                assignmentReads[trackerProjectId] = (clock(), etag);
            }
        }
    }

    private bool HandleAssignmentFailure(ApiResult result)
    {
        var detail = result.Detail ?? result.NetworkError ?? $"HTTP {result.Status}";
        switch (result.Status)
        {
            case 401:
            case 403:
                NeedsSignIn = true;
                LastProblem = result.Status == 401
                    ? "Workload Tracker needs you to connect again."
                    : $"Workload Tracker refused this PC's token: {detail} Connect again.";
                return false;
            case 400 when result.Error?.Code == CaErrorCodeEnum.UnsupportedSchemaVersion:
                NeedsUpdate = true;
                LastProblem = detail;
                return false;
            case 400:
                // Retrying the same changes won't help until the app changes.
                AppLog.Warn("workload", $"Workload Tracker refused assignment changes: {result.Body}");
                assignmentsRefusedUntil = clock() + QuarantineAfterRefusal;
                LastProblem = $"Workload Tracker refused the assignment changes: {detail}";
                return false;
            case 0:
                LastProblem = $"Can't reach Workload Tracker: {detail}";
                return false;
            default:
                LastProblem = detail;
                return false;
        }
    }

    private static WorkloadAssignment FromContract(CaAssignment row) => new()
    {
        TrackerProjectId = row.TrackerProjectId,
        Provider = row.ProviderProject.Provider,
        Host = row.ProviderProject.Host,
        ProviderProjectId = row.ProviderProject.ProjectId,
        ItemType = row.ItemType,
        Number = row.Number,
        PersonId = row.PersonId,
        PersonName = row.PersonName,
        PersonActive = row.PersonActive,
        Active = row.Active,
        AssignedAt = row.AssignedAt,
        AssignedByName = row.AssignedByName,
        CompletedAt = row.CompletedAt
    };

    private static string Named(WorkloadAssignment row) => row.PersonName.Length > 0 ? row.PersonName : $"person {row.PersonId}";

    private static string RejectionText(string? reason) => reason switch
    {
        CaAssignmentRejectionEnum.ProjectNotVisible => "your Workload Tracker account can't see that project.",
        CaAssignmentRejectionEnum.ProjectNotBound => "the project isn't bound to this Helix or Procore project in Workload Tracker (check Edit project).",
        CaAssignmentRejectionEnum.PersonNotFound => "that person isn't in Workload Tracker.",
        CaAssignmentRejectionEnum.PersonInactive => "that person has left.",
        _ => "Workload Tracker refused it."
    };

    // ---- Shared -----------------------------------------------------------------------------------

    private const string NotConnectedText = "Connect to Workload Tracker first (Settings → Workload Tracker).";

    /// <summary>The connection and token, when paired and usable.</summary>
    private (WorkloadTrackerSettings Sync, string Token)? Credentials()
    {
        var sync = store.Settings.WorkloadTracker;
        if (!sync.IsPaired || NeedsSignIn)
        {
            return null;
        }

        var token = protector.Unprotect(sync.TokenProtected);
        return string.IsNullOrEmpty(token) ? null : (sync, token);
    }

    /// <summary>What a failed read means, in words; a refused token means connecting again.</summary>
    private string Explain(ApiResult result)
    {
        switch (result.Status)
        {
            case 0:
                return $"Can't reach Workload Tracker: {result.NetworkError}";
            case 401:
                NeedsSignIn = true;
                LastProblem = "Workload Tracker needs you to connect again.";
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return LastProblem;
            default:
                return result.Detail ?? $"Workload Tracker answered HTTP {result.Status}.";
        }
    }

    private void ForgetReads()
    {
        lock (cacheGate)
        {
            assignmentReads.Clear();
            teams.Clear();
        }
    }

    private void Sent(OutboxEntry entry, ApiResult? result)
    {
        store.CompleteOutbox(entry.Provider, entry.ProjectId, entry.Version);
        LastSentAt = clock();
        LastProblem = null;
        if (result?.TryRead<CaObservationResult>() is { ResendAll: true } observation && resendAt is null)
        {
            // The tracker lost track (a restore, a missed change): send everything again, after a delay
            // the tracker staggers across desktops.
            resendAt = clock().AddSeconds(observation.ResendAfterSeconds ?? 0);
            AppLog.Info("workload", $"Workload Tracker asked for everything again in {observation.ResendAfterSeconds ?? 0} s.");
        }
    }

    /// <summary>What a failed call means for this project and for the round. True to go on with other projects.</summary>
    private bool Handle(ApiResult result, OutboxEntry entry, string key)
    {
        var detail = result.Detail ?? result.NetworkError ?? $"HTTP {result.Status}";
        var code = result.Error?.Code;
        switch (result.Status)
        {
            case 0:
                Defer(entry, Backoff(entry), $"Can't reach Workload Tracker: {detail}");
                return false;
            case 401:
            case 403:
                NeedsSignIn = true;
                LastProblem = result.Status == 401
                    ? "Workload Tracker needs you to connect again."
                    : $"Workload Tracker refused this PC's token: {detail} Connect again.";
                return false;
            case 400 when code == CaErrorCodeEnum.UnsupportedSchemaVersion:
                NeedsUpdate = true;
                LastProblem = detail;
                return false;
            case 400:
                // A body the tracker won't take: retrying won't help until the app changes.
                AppLog.Warn("workload", $"Workload Tracker refused {key}: {result.Body}");
                Defer(entry, clock() + QuarantineAfterRefusal, $"Refused: {detail}");
                return true;
            case 409:
                pendingIds.Remove(key);
                Defer(entry, clock().AddMinutes(1), detail);
                return true;
            case 413:
                partSizes[key] = Math.Max(25, (partSizes.TryGetValue(key, out var size) ? size : ObservationBuilder.MaxItemsPerPart) / 2);
                pendingIds.Remove(key);
                Defer(entry, clock().AddSeconds(10), "Too large; sending smaller parts.");
                return true;
            default:
                // 429, 503 (sync turned off) and server errors: wait and try again.
                Defer(entry, Backoff(entry), detail);
                return result.Status < 500 && result.Status != 429;
        }
    }

    private void Defer(OutboxEntry entry, DateTimeOffset next, string error)
    {
        store.DeferOutbox(entry.Provider, entry.ProjectId, next, error);
        LastProblem = error;
    }

    /// <summary>Doubling waits from a minute up to an hour, spread a little so desktops don't retry together.</summary>
    private DateTimeOffset Backoff(OutboxEntry entry)
    {
        var minutes = Math.Min(60, Math.Pow(2, Math.Min(entry.Attempts, 6)));
        return clock() + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(Random.Shared.Next(0, 30));
    }
}
