using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Diagnostics;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Procore;
using SmcCaTracker.Core.Storage;

namespace SmcCaTracker.Core.Central;

public sealed record CentralSyncResult(int Sent, int Received, string? Problem);

/// <summary>A change received from the central server (another PC's, or this PC's own coming back).</summary>
public sealed record RemoteChange(
    long Seq,
    string? Client,
    string? User,
    string Entity,
    string Key,
    string Operation,
    DateTimeOffset ChangedAt,
    JsonElement? Payload)
{
    public bool IsDelete => string.Equals(Operation, "delete", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Shares data through the company's CA Tracker server (protocol: central/PROTOCOL.md). Sends this PC's
/// change log (its projects and the Helix/Procore items it read) and takes in everyone else's:
/// <list type="bullet">
/// <item>Items of the Helix/Procore projects tracked on this PC, unless this PC has read that project
/// since the other person did (then what it has is at least as new).</item>
/// <item>For projects tracked on this PC (the same Helix/Procore project, whatever the other person
/// called it): discipline choices are combined (a different choice for the same folder or item replaces
/// the older one), and the folder names and Base Build/TI scope are taken. The CA folder and the
/// project's name stay as they are here, and someone removing a project from their list doesn't remove it
/// from anyone else's.</item>
/// </list>
/// Changes made here that are waiting to be sent, or were sent after a received change, win over it.
/// Off unless a server and an access token are set.
/// </summary>
public sealed class CentralSync
{
    public const string ApiRoot = "api/ca-tracker/v1";
    public const int Batch = 500;
    internal const string CursorKey = "central_cursor";
    internal const string ClientKey = "central_client_id";
    internal const string ServerKey = "central_server";
    internal const string BaselineKey = "central_send_everything";

    private readonly TrackerStore store;
    private readonly HttpClient http;
    private readonly ISecretProtector protector;
    private int running;

    public CentralSync(TrackerStore store, HttpClient http, ISecretProtector protector)
    {
        this.store = store;
        this.http = http;
        this.protector = protector;
    }

    /// <summary>The server set by the company settings file (takes precedence over the user's).</summary>
    public string? CompanyServer { get; set; }

    public bool IsCompanyServer => !string.IsNullOrWhiteSpace(CompanyServer);

    public string Server => NormalizeServer(IsCompanyServer ? CompanyServer : store.Settings.Central.Server);

    public bool HasToken => Token is not null;

    public bool IsConfigured => Server.Length > 0 && HasToken;

    public bool IsRunning => Volatile.Read(ref running) == 1;

    public DateTimeOffset? LastSyncAt { get; private set; }
    public string? LastProblem { get; private set; }
    public int LastSent { get; private set; }
    public int LastReceived { get; private set; }

    /// <summary>Who the server says the token belongs to (from the last connection test).</summary>
    public string? ConnectedAs { get; private set; }

    private string? Token => protector.Unprotect(store.Settings.Central.TokenProtected) is { Length: > 0 } token ? token : null;

    /// <summary>"https://ca.company.com/" → "https://ca.company.com"; anything that isn't an http(s) address → "".</summary>
    public static string NormalizeServer(string? server)
    {
        var text = (server ?? "").Trim().TrimEnd('/');
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? text
            : "";
    }

    /// <summary>Saves the server address (when the company settings don't set it) and, if given, a new token (encrypted).</summary>
    public void Configure(string? server, string? token)
    {
        var settings = store.SettingsCopy();
        if (!IsCompanyServer)
        {
            settings.Central.Server = NormalizeServer(server);
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            settings.Central.TokenProtected = protector.Protect(token.Trim());
        }

        store.SaveSettings(settings);
        ConnectedAs = null;
        LastProblem = null;
    }

    /// <summary>Forgets the access token: nothing more is shared until a new one is entered.</summary>
    public void Disconnect()
    {
        var settings = store.SettingsCopy();
        settings.Central.TokenProtected = "";
        store.SaveSettings(settings);
        ConnectedAs = null;
        LastProblem = null;
    }

    /// <summary>Checks the server and token (GET hello). Null when they work; otherwise what's wrong.</summary>
    public async Task<string?> TestAsync(CancellationToken cancellationToken = default)
    {
        var server = Server;
        var token = Token;
        if (server.Length == 0 || token is null)
        {
            return "Enter the server's address and your access token.";
        }

        try
        {
            using var request = Request(HttpMethod.Get, server, "hello", token);
            using var response = await http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            ConnectedAs = document.RootElement.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.String ? user.GetString() : null;
            LastProblem = null;
            return null;
        }
        catch (Exception error) when (IsConnectionProblem(error))
        {
            LastProblem = Describe(error);
            return LastProblem;
        }
    }

    /// <summary>Takes in everyone else's changes, then sends what changed here. Does nothing while already running.</summary>
    public async Task<CentralSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var server = Server;
        var token = Token;
        if (server.Length == 0 || token is null)
        {
            return new CentralSyncResult(0, 0, "No central server is set up.");
        }

        if (Interlocked.Exchange(ref running, 1) == 1)
        {
            return new CentralSyncResult(0, 0, null);
        }

        try
        {
            using var timing = AppLog.Time("central.sync", server, 5000);
            var sent = 0;
            var received = 0;
            // Read first, then send: changes made here are still waiting while everyone else's are taken
            // in, so they aren't overwritten (and the server orders them after what was read).
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var client = await PrepareAsync(server);
                var (count, reset) = await ReceiveAsync(server, token, client, cancellationToken);
                received += count;
                if (reset)
                {
                    continue;
                }

                await QueueEverythingIfFirstShareAsync();
                sent += await SendAsync(server, token, client, cancellationToken);
                break;
            }

            LastSyncAt = DateTimeOffset.Now;
            LastProblem = null;
            LastSent = sent;
            LastReceived = received;
            return new CentralSyncResult(sent, received, null);
        }
        catch (Exception error)
        {
            // Never let a background share bring up an error box: it is reported in Settings and the log.
            if (IsConnectionProblem(error))
            {
                LastProblem = Describe(error);
                AppLog.Warn("central", $"Sharing with {server} failed: {error.Message}");
            }
            else
            {
                LastProblem = $"Sharing failed: {error.Message}";
                AppLog.Error("central", $"Sharing with {server} failed", error);
            }

            return new CentralSyncResult(0, 0, LastProblem);
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private static bool IsConnectionProblem(Exception error) =>
        error is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException
            or KeyNotFoundException or FormatException or ObjectDisposedException;

    private static string Describe(Exception error) => error switch
    {
        OperationCanceledException => "The central server didn't answer in time.",
        JsonException or KeyNotFoundException or FormatException => "The central server's answer couldn't be read.",
        _ => error.Message
    };

    /// <summary>
    /// This PC's id for the server. The first time a server is used, everyone's changes are read from
    /// the start, and then everything here is sent (see <see cref="QueueEverythingIfFirstShareAsync"/>).
    /// </summary>
    private Task<string> PrepareAsync(string server) =>
        store.OnDatabaseAsync(database =>
        {
            var client = database.GetMeta(ClientKey);
            if (string.IsNullOrEmpty(client))
            {
                client = Guid.NewGuid().ToString("N");
                database.SetMeta(ClientKey, client);
            }

            if (!string.Equals(database.GetMeta(ServerKey), server, StringComparison.OrdinalIgnoreCase))
            {
                database.RunBatch(() =>
                {
                    database.SetMeta(CursorKey, "0");
                    database.SetMeta(ServerKey, server);
                    database.SetMeta(BaselineKey, "1");
                });
                AppLog.Info("central", $"First time sharing with {server}: reading everyone's changes, then sending everything here.");
            }

            return client;
        });

    /// <summary>
    /// After the first read from a new server: queues every project and item as they now are (with
    /// what was just taken in from others), replacing what was waiting.
    /// </summary>
    private Task QueueEverythingIfFirstShareAsync() =>
        store.OnDatabaseAsync(database =>
        {
            if (database.GetMeta(BaselineKey) != "1")
            {
                return false;
            }

            database.RunBatch(() =>
            {
                database.QueueEverything();
                database.SetMeta(BaselineKey, "");
            });
            return true;
        });

    private static string Endpoint(string server, string path) => $"{server}/{ApiRoot}/{path}";

    private static HttpRequestMessage Request(HttpMethod method, string server, string path, string token)
    {
        var request = new HttpRequestMessage(method, Endpoint(server, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The central server didn't accept your access token. Enter a new one in Settings → Connections.",
            HttpStatusCode.Forbidden => "Your access token isn't allowed to share CA Tracker data. Ask the server's administrator.",
            HttpStatusCode.NotFound => "That address doesn't have a CA Tracker server (check it in Settings → Connections).",
            _ => $"The central server answered {(int)response.StatusCode} {response.ReasonPhrase}."
        };
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                message = $"{message} ({error.GetString()})";
            }
        }
        catch (JsonException)
        {
        }

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    /// <summary>Sends the change log in batches; each batch the server accepts is marked sent.</summary>
    private async Task<int> SendAsync(string server, string token, string client, CancellationToken cancellationToken)
    {
        var sent = 0;
        var user = store.Account(Providers.Helix)?.UserName ?? store.Account(Providers.Procore)?.UserName ?? Environment.UserName;
        while (true)
        {
            var pending = await store.OnDatabaseAsync(database => database.PendingChanges(Batch));
            if (pending.Count == 0)
            {
                return sent;
            }

            using var request = Request(HttpMethod.Post, server, "changes", token);
            request.Content = new ByteArrayContent(ChangesBody(client, user, pending));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var response = await http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);

            var last = pending[^1].Seq;
            await store.OnDatabaseAsync(database =>
            {
                database.MarkChangesSent(last);
                return true;
            });
            sent += pending.Count;
            if (pending.Count < Batch)
            {
                return sent;
            }
        }
    }

    /// <summary>The POST body: the payloads go in as the JSON they already are.</summary>
    internal static byte[] ChangesBody(string client, string user, IReadOnlyList<ChangeRecord> changes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("client", client);
            writer.WriteString("user", user);
            writer.WriteString("app", TrackerVersion.Current);
            writer.WriteStartArray("changes");
            foreach (var change in changes)
            {
                writer.WriteStartObject();
                writer.WriteNumber("seq", change.Seq);
                writer.WriteString("entity", change.Entity);
                writer.WriteString("key", change.Key);
                writer.WriteString("operation", change.Operation);
                writer.WriteString("changedAt", change.ChangedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                writer.WritePropertyName("payload");
                if (string.IsNullOrWhiteSpace(change.Payload))
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteRawValue(change.Payload);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Takes in everyone's changes after the last ones received, a page at a time. Reset is true when the
    /// server has fewer changes than this PC has read (it was reset or restored from a backup): everything
    /// here is then queued to send again, and the caller reads again from the start.
    /// </summary>
    private async Task<(int Received, bool Reset)> ReceiveAsync(string server, string token, string client, CancellationToken cancellationToken)
    {
        var cursorText = await store.OnDatabaseAsync(database => database.GetMeta(CursorKey));
        var cursor = long.TryParse(cursorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        var received = 0;
        var state = new ApplyState();
        while (true)
        {
            using var request = Request(HttpMethod.Get, server, $"changes?after={cursor.ToString(CultureInfo.InvariantCulture)}&limit={Batch}", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Conflict && cursor > 0)
            {
                // Everything here is queued first, so what was decided here since the server's backup
                // isn't overwritten by older changes read back from it; then everything is read again.
                AppLog.Warn("central", $"{server} has fewer changes than this PC has read (reset or restored): sending everything again.");
                await store.OnDatabaseAsync(database =>
                {
                    database.RunBatch(() =>
                    {
                        database.QueueEverything();
                        database.SetMeta(CursorKey, "0");
                        database.SetMeta(BaselineKey, "");
                    });
                    return true;
                });
                return (received, true);
            }

            await EnsureSuccessAsync(response, cancellationToken);
            var (changes, last) = ParsePage(await response.Content.ReadAsStringAsync(cancellationToken), cursor);

            if (changes.Count > 0)
            {
                // What's waiting to be sent from here wins (the server orders it after these).
                var pendingKeys = await store.OnDatabaseAsync(database => database.PendingKeys());
                received += Apply(store, changes, client, pendingKeys, state);
            }

            if (last <= cursor)
            {
                return (received, false);
            }

            cursor = last;
            var saved = cursor.ToString(CultureInfo.InvariantCulture);
            await store.OnDatabaseAsync(database =>
            {
                database.SetMeta(CursorKey, saved);
                return true;
            });

            if (changes.Count < Batch)
            {
                return (received, false);
            }
        }
    }

    /// <summary>A page of GET changes: <c>{ "changes": [ … ], "last": 123 }</c>.</summary>
    public static (IReadOnlyList<RemoteChange> Changes, long Last) ParsePage(string json, long after)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var changes = new List<RemoteChange>();
        foreach (var element in root.GetProperty("changes").EnumerateArray())
        {
            var payload = element.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object
                ? payloadElement.Clone()
                : (JsonElement?)null;
            var changedAt = element.TryGetProperty("changedAt", out var changedElement) && changedElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(changedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;
            changes.Add(new RemoteChange(
                element.GetProperty("seq").GetInt64(),
                Text(element, "client"),
                Text(element, "user"),
                Text(element, "entity") ?? "",
                Text(element, "key") ?? "",
                Text(element, "operation") ?? "",
                changedAt,
                payload));
        }

        var last = root.TryGetProperty("last", out var lastElement) && lastElement.ValueKind == JsonValueKind.Number && lastElement.TryGetInt64(out var lastSeq)
            ? lastSeq
            : changes.Count > 0 ? changes.Max(change => change.Seq) : after;
        return (changes, Math.Max(last, after));
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>What earlier pages of the same read showed, so a later page can't undo a newer change.</summary>
    public sealed class ApplyState
    {
        /// <summary>Items taken in so far: when that version was read.</summary>
        public Dictionary<string, DateTimeOffset> ItemReadAt { get; } = new(StringComparer.Ordinal);

        /// <summary>This PC's own project changes seen so far: the newest server number of each.</summary>
        public Dictionary<string, long> OwnProjectSeq { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ItemBatch
    {
        public List<SnapshotItem> Upserts { get; } = new();
        public List<(string ItemType, string ItemId)> Deletes { get; } = new();
        public DateTimeOffset AsOf { get; set; } = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Puts other people's changes into this PC's data (see the class summary for what is taken).
    /// <paramref name="pendingKeys"/> are this PC's unsent changes ("project|id", "item|key"). Returns how many were applied.
    /// </summary>
    public static int Apply(
        TrackerStore store,
        IReadOnlyList<RemoteChange> changes,
        string client,
        IReadOnlySet<string> pendingKeys,
        ApplyState? state = null)
    {
        state ??= new ApplyState();
        var ordered = changes.OrderBy(change => change.Seq).ToList();
        bool IsOwn(RemoteChange change) => string.Equals(change.Client, client, StringComparison.Ordinal);

        // Items: only the most recently read version of each counts (a PC sending everything it has
        // re-sends old reads with new numbers); when that is this PC's own, it's already here.
        // Projects: everyone's changes are combined in order; this PC's own latest change of each of
        // its projects marks what came before it as older than what this PC sent.
        var newestItems = new Dictionary<string, RemoteChange>(StringComparer.Ordinal);
        var ownProjectSeq = state.OwnProjectSeq;
        foreach (var change in ordered)
        {
            if (change.Entity == "item")
            {
                if (!newestItems.TryGetValue(change.Key, out var other) || change.ChangedAt >= other.ChangedAt)
                {
                    newestItems[change.Key] = change;
                }
            }
            else if (change.Entity == "project" && IsOwn(change))
            {
                ownProjectSeq[change.Key] = change.Seq;
            }
        }

        var settings = store.SettingsCopy();
        var projectsChanged = false;
        var applied = 0;
        foreach (var change in ordered.Where(change => change.Entity == "project" && !IsOwn(change)))
        {
            // Someone removing a project from their list leaves everyone else's alone.
            if (change.IsDelete || change.Payload is not { } payload)
            {
                continue;
            }

            TrackerProject? remote;
            try
            {
                remote = payload.Deserialize<TrackerProject>(TrackerJson.Options);
            }
            catch (JsonException)
            {
                continue;
            }

            if (remote is null)
            {
                continue;
            }

            foreach (var local in settings.Projects.Where(local => SameProject(local, remote)))
            {
                // What this PC changed and hasn't sent, or sent after this change, isn't overwritten
                // (choices this PC doesn't have are still added).
                var overwrite = !pendingKeys.Contains($"project|{local.Id}")
                    && !(ownProjectSeq.TryGetValue(local.Id, out var ownSeq) && ownSeq > change.Seq);
                if (TakeSharedChoices(remote, local, overwrite))
                {
                    projectsChanged = true;
                    applied++;
                }
            }
        }

        var items = new Dictionary<(string Provider, string ProjectId), ItemBatch>();
        foreach (var change in newestItems.Values.OrderBy(change => change.Seq))
        {
            if (IsOwn(change) || pendingKeys.Contains($"item|{change.Key}")
                || (state.ItemReadAt.TryGetValue(change.Key, out var takenAt) && takenAt > change.ChangedAt))
            {
                continue;
            }

            // provider|projectId|itemType|itemId
            var parts = change.Key.Split('|', 4);
            if (parts.Length != 4 || !IsTracked(settings, parts[0], parts[1]))
            {
                continue;
            }

            var lastRead = store.LastSyncRun(parts[0], parts[1], completeOnly: true);
            if (lastRead is not null && lastRead.StartedAt >= change.ChangedAt)
            {
                // Read here since the other person did: what this PC has is at least as new.
                continue;
            }

            var target = (parts[0].ToLowerInvariant(), parts[1]);
            if (!items.TryGetValue(target, out var batch))
            {
                batch = new ItemBatch();
                items[target] = batch;
            }

            if (change.IsDelete)
            {
                batch.Deletes.Add((parts[2], parts[3]));
            }
            else
            {
                SnapshotItem? item;
                try
                {
                    item = change.Payload?.Deserialize<SnapshotItem>(TrackerJson.Options);
                }
                catch (JsonException)
                {
                    item = null;
                }

                if (item is null
                    || !string.Equals(item.ItemType, parts[2], StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(item.ItemId, parts[3], StringComparison.Ordinal))
                {
                    continue;
                }

                batch.Upserts.Add(item);
            }

            if (change.ChangedAt > batch.AsOf)
            {
                batch.AsOf = change.ChangedAt;
            }

            state.ItemReadAt[change.Key] = change.ChangedAt;
            applied++;
        }

        if (projectsChanged)
        {
            store.SaveSettingsFromServer(settings);
        }

        foreach (var ((provider, projectId), batch) in items)
        {
            store.ApplyRemoteItems(provider, projectId, batch.Upserts, batch.Deletes,
                batch.AsOf == DateTimeOffset.MinValue ? null : batch.AsOf);
        }

        if (applied > 0)
        {
            AppLog.Info("central", $"Took in {applied} changes from other people.");
        }

        return applied;
    }

    /// <summary>The same tracker project (copied settings) or the same Helix/Procore project.</summary>
    private static bool SameProject(TrackerProject local, TrackerProject remote) =>
        string.Equals(local.Id, remote.Id, StringComparison.Ordinal)
        || (local.Connection is { } mine && remote.Connection is { } theirs
            && string.Equals(mine.Provider, theirs.Provider, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(mine.ProjectId)
            && string.Equals(mine.ProjectId, theirs.ProjectId, StringComparison.Ordinal));

    private static bool IsTracked(TrackerSettings settings, string provider, string projectId) =>
        settings.Projects.Any(project => project.Connection is { } connection
            && string.Equals(connection.Provider, provider, StringComparison.OrdinalIgnoreCase)
            && string.Equals(connection.ProjectId, projectId, StringComparison.Ordinal));

    /// <summary>
    /// Takes what is shared about a project. Discipline choices are combined: choices this PC doesn't
    /// have are added; with <paramref name="overwrite"/>, a different choice for the same folder or item
    /// replaces this PC's (an empty choice means "detect it again"). The folder names and Base Build/TI
    /// scope are taken too, but only from the same tracker project (same id) or a twin of the same
    /// phase: a Base Build and a Tenant Fit-Out entry for one Helix project share choices, never scope.
    /// True when anything changed.
    /// </summary>
    private static bool TakeSharedChoices(TrackerProject remote, TrackerProject local, bool overwrite)
    {
        var changed = false;
        local.Disciplines ??= new Dictionary<string, string>();
        foreach (var (rawKey, code) in remote.Disciplines ?? new Dictionary<string, string>())
        {
            var key = rawKey.ToLowerInvariant();
            var choice = code ?? "";
            if (local.Disciplines.TryGetValue(key, out var mine)
                && (!overwrite || string.Equals(mine, choice, StringComparison.Ordinal)))
            {
                continue;
            }

            local.Disciplines[key] = choice;
            changed = true;
        }

        if (!overwrite || !SharesScope(remote, local))
        {
            return changed;
        }

        if (IsSafeOverride(remote.FoldersOverride) && !SameJson(remote.FoldersOverride, local.FoldersOverride))
        {
            local.FoldersOverride = remote.FoldersOverride?.Clone();
            changed = true;
        }

        var scope = remote.Scope ?? new PhaseScope();
        if (!SameJson(scope, local.Scope))
        {
            local.Scope = new PhaseScope
            {
                Phase = scope.Phase,
                FilterItems = scope.FilterItems,
                FilterFolders = scope.FilterFolders,
                IncludeUnlabeled = scope.IncludeUnlabeled
            };
            changed = true;
        }

        return changed;
    }

    /// <summary>The same tracker project, or a twin (same Helix/Procore project) tracking the same phase.</summary>
    private static bool SharesScope(TrackerProject remote, TrackerProject local) =>
        string.Equals(local.Id, remote.Id, StringComparison.Ordinal)
        || (remote.Scope?.Phase ?? ProjectPhase.Whole) == (local.Scope?.Phase ?? ProjectPhase.Whole);

    /// <summary>
    /// Folder names from another PC are taken only when they are valid and the RFI and Submittal
    /// folders are plain names inside the CA folder (never a drive or share on the other PC). The
    /// returned folder may be a full path, which is how a shared returned folder is set for everyone.
    /// </summary>
    internal static bool IsSafeOverride(FolderLayout? layout) =>
        layout is null
        || (layout.Validate().Count == 0
            && !Path.IsPathFullyQualified(layout.RfiFolder ?? "")
            && !Path.IsPathFullyQualified(layout.SubmittalFolder ?? "")
            && !(layout.RfiFolder ?? "").StartsWith('\\') && !(layout.RfiFolder ?? "").StartsWith('/')
            && !(layout.SubmittalFolder ?? "").StartsWith('\\') && !(layout.SubmittalFolder ?? "").StartsWith('/'));

    private static bool SameJson<T>(T left, T right) =>
        JsonSerializer.Serialize(left, TrackerJson.Options) == JsonSerializer.Serialize(right, TrackerJson.Options);
}
