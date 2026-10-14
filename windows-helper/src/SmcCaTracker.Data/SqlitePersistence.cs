using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using SmcCaTracker.Core.Comparison;
using SmcCaTracker.Core.Data;
using SmcCaTracker.Core.Matching;
using SmcCaTracker.Core.Models;
using SmcCaTracker.Core.Refresh;
using SmcCaTracker.Core.Storage;
using SmcCaTracker.Core.WorkloadTracker;

namespace SmcCaTracker.Data;

/// <summary>
/// The app's local database (%LOCALAPPDATA%\SMC-CA-Tracker\tracker.db, SQLite). The tables are plain
/// and portable — text ids, ISO-8601 text dates, integer flags, no SQLite-only types — so the same
/// shape can live in a central database (e.g. PostgreSQL) later. While the app is paired with Workload
/// Tracker, <c>wt_outbox</c> keeps what each project's scans changed until it is sent, <c>wt_assignments</c> and
/// <c>wt_completions</c> what was read back, and <c>wt_item_facts</c> the facts last sent per item. Not thread-safe:
/// <see cref="TrackerStore"/> calls it from one thread at a time.
/// </summary>
public sealed class SqlitePersistence : ITrackerPersistence
{
    private readonly SqliteConnection connection;

    /// <summary>The transaction of a <see cref="RunBatch"/>, which every save inside it joins.</summary>
    private SqliteTransaction? ambient;

    public SqlitePersistence(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            Execute("PRAGMA busy_timeout = 5000;");
            Execute("PRAGMA journal_mode = WAL;");
            Execute("PRAGMA synchronous = NORMAL;");
            Schema.Migrate(this);
        }
        catch
        {
            // Don't keep the file locked when it can't be used.
            connection.Dispose();
            throw;
        }
    }

    // ---- Small helpers ------------------------------------------------------------------------

    internal SqliteCommand Command(string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction ?? ambient;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    internal int Execute(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        return command.ExecuteNonQuery();
    }

    internal SqliteTransaction BeginTransaction() => connection.BeginTransaction();

    /// <summary>
    /// A save's transaction: its own, or the batch's when inside <see cref="RunBatch"/> (then only the
    /// batch commits).
    /// </summary>
    private sealed class WriteScope : IDisposable
    {
        private readonly bool owns;
        private readonly string? savepoint;
        private bool committed;

        public WriteScope(SqlitePersistence database)
        {
            owns = database.ambient is null;
            Transaction = database.ambient ?? database.BeginTransaction();
            if (!owns)
            {
                // Inside a batch: a save that fails part-way is undone on its own (the batch may go on).
                savepoint = $"save_{Guid.NewGuid():N}";
                Transaction.Save(savepoint);
            }
        }

        public SqliteTransaction Transaction { get; }

        public void Commit()
        {
            committed = true;
            if (owns)
            {
                Transaction.Commit();
            }
            else
            {
                Transaction.Release(savepoint!);
            }
        }

        public void Dispose()
        {
            if (owns)
            {
                Transaction.Dispose();
            }
            else if (!committed)
            {
                Transaction.Rollback(savepoint!);
            }
        }
    }

    public void RunBatch(Action work)
    {
        if (ambient is not null)
        {
            work();
            return;
        }

        using var transaction = BeginTransaction();
        ambient = transaction;
        try
        {
            work();
            transaction.Commit();
        }
        finally
        {
            ambient = null;
        }
    }

    private static long Flag(bool value) => value ? 1 : 0;

    // Stored in UTC so text order is time order (and portable).
    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Iso(DateTimeOffset? value) => value is { } actual ? Iso(actual) : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? Number(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, TrackerJson.Options);

    /// <summary>
    /// An item as stored, for its content hash: dates in UTC, as the database keeps
    /// them, so an item read back from the database hashes the same as when it was saved (Helix gives
    /// local times such as -07:00, which would otherwise log every item again on each save).
    /// </summary>
    private static string ItemJson(SnapshotItem item) => JsonSerializer.Serialize(item, StoredJson);

    private static readonly JsonSerializerOptions StoredJson = CreateStoredJson();

    private static JsonSerializerOptions CreateStoredJson()
    {
        var options = new JsonSerializerOptions(TrackerJson.Options);
        options.Converters.Insert(0, new UtcDates());
        return options;
    }

    private sealed class UtcDates : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime());
    }

    private static T? FromJson<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, TrackerJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- Meta and preferences -----------------------------------------------------------------

    public string? GetMeta(string key)
    {
        using var command = Command("SELECT meta_value FROM schema_info WHERE meta_key = $key;", null, ("$key", key));
        return command.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value) =>
        Execute(
            "INSERT INTO schema_info (meta_key, meta_value) VALUES ($key, $value) ON CONFLICT (meta_key) DO UPDATE SET meta_value = excluded.meta_value;",
            null,
            ("$key", key), ("$value", value));

    private string? GetPreference(string key)
    {
        using var command = Command("SELECT pref_value FROM preferences WHERE pref_key = $key;", null, ("$key", key));
        return command.ExecuteScalar() as string;
    }

    private void SetPreference(string key, string value, SqliteTransaction transaction) =>
        Execute(
            "INSERT INTO preferences (pref_key, pref_value, updated_at) VALUES ($key, $value, $at) " +
            "ON CONFLICT (pref_key) DO UPDATE SET pref_value = excluded.pref_value, updated_at = excluded.updated_at;",
            transaction,
            ("$key", key), ("$value", value), ("$at", Iso(DateTimeOffset.Now)));

    // ---- Loading ------------------------------------------------------------------------------

    public PersistedState Load() => new()
    {
        Settings = LoadSettings(),
        Snapshots = LoadSnapshots(),
        FolderScans = LoadFolderScans(),
        Catalog = LoadCatalog(),
        Accounts = LoadAccounts(),
        View = FromJson<ViewState>(GetPreference("view")) ?? new ViewState(),
        RecentSyncRuns = LoadSyncRuns(200),
        Outbox = LoadOutbox(),
        Assignments = LoadAssignments(),
        Completions = LoadCompletions(),
        SentFacts = LoadSentFacts()
    };

    private TrackerSettings LoadSettings()
    {
        var settings = new TrackerSettings
        {
            Theme = Enum.TryParse<ThemePreference>(GetPreference("theme"), out var theme) ? theme : ThemePreference.System,
            Folders = FromJson<FolderLayout>(GetPreference("folders")) ?? new FolderLayout(),
            Returned = FromJson<ReturnedRule>(GetPreference("returned")) ?? new ReturnedRule(),
            Browser = FromJson<BrowserSettings>(GetPreference("browser")) ?? new BrowserSettings(),
            Sync = FromJson<SyncSettings>(GetPreference("sync")) ?? new SyncSettings(),
            Procore = FromJson<SmcCaTracker.Core.Procore.ProcoreSettings>(GetPreference("procore")) ?? new SmcCaTracker.Core.Procore.ProcoreSettings(),
            WorkloadTracker = FromJson<WorkloadTrackerSettings>(GetPreference("workloadTracker")) ?? new WorkloadTrackerSettings()
        };

        var byId = new Dictionary<string, TrackerProject>(StringComparer.Ordinal);
        using (var command = Command(
                   "SELECT id, name, ca_folder, provider, provider_project_id, provider_account_id, provider_project_name, provider_host, " +
                   "company_id, company_name, rfi_folder, submittal_folder, returned_folder, phase, filter_items, filter_folders, include_unlabeled " +
                   "FROM projects WHERE deleted_at IS NULL ORDER BY sort_order, name;",
                   null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var provider = Text(reader, 3);
                var providerProjectId = Text(reader, 4);
                var rfiFolder = Text(reader, 10);
                var submittalFolder = Text(reader, 11);
                var returnedFolder = Text(reader, 12);
                var project = new TrackerProject
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    CaFolder = reader.GetString(2),
                    Connection = string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerProjectId)
                        ? null
                        : new ProviderConnection
                        {
                            Provider = provider,
                            ProjectId = providerProjectId,
                            AccountId = Text(reader, 5),
                            ProjectName = Text(reader, 6) ?? "",
                            Host = Text(reader, 7)
                        },
                    CompanyId = Text(reader, 8),
                    CompanyName = Text(reader, 9),
                    FoldersOverride = rfiFolder is null && submittalFolder is null && returnedFolder is null
                        ? null
                        : new FolderLayout
                        {
                            RfiFolder = rfiFolder ?? new FolderLayout().RfiFolder,
                            SubmittalFolder = submittalFolder ?? new FolderLayout().SubmittalFolder,
                            ReturnedFolder = returnedFolder ?? new FolderLayout().ReturnedFolder
                        },
                    Scope = new PhaseScope
                    {
                        Phase = Enum.TryParse<ProjectPhase>(reader.GetString(13), out var phase) ? phase : ProjectPhase.Whole,
                        FilterItems = reader.GetInt64(14) != 0,
                        FilterFolders = reader.GetInt64(15) != 0,
                        IncludeUnlabeled = reader.GetInt64(16) != 0
                    }
                };
                settings.Projects.Add(project);
                byId[project.Id] = project;
            }
        }

        using (var command = Command("SELECT project_id, item_type, target_kind, target, discipline FROM discipline_choices;", null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!byId.TryGetValue(reader.GetString(0), out var project))
                {
                    continue;
                }

                var itemType = reader.GetString(1);
                var target = reader.GetString(3);
                var key = reader.GetString(2) == "item" ? Disciplines.ItemKey(itemType, target) : Disciplines.Key(itemType, target);
                project.Disciplines[key] = reader.GetString(4);
            }
        }

        return settings;
    }

    private List<ProviderSnapshot> LoadSnapshots()
    {
        var byKey = new Dictionary<string, ProviderSnapshot>(StringComparer.OrdinalIgnoreCase);
        static string Key(string provider, string projectId) => $"{provider}|{projectId}";

        using (var command = Command(
                   "SELECT provider, project_id, schema_version, account_id, project_name, project_url, complete, scanned_at, " +
                   "user_full_name, user_company_id, user_company, user_provider_id, user_email FROM provider_snapshots;",
                   null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var snapshot = new ProviderSnapshot
                {
                    Provider = reader.GetString(0),
                    ProjectId = reader.GetString(1),
                    SchemaVersion = (int)reader.GetInt64(2),
                    AccountId = Text(reader, 3),
                    ProjectName = reader.GetString(4),
                    ProjectUrl = Text(reader, 5),
                    Complete = reader.GetInt64(6) != 0,
                    ScannedAt = ParseDate(reader.GetString(7)) ?? DateTimeOffset.MinValue,
                    User = new SnapshotUser
                    {
                        FullName = reader.GetString(8),
                        CompanyId = Text(reader, 9),
                        Company = Text(reader, 10),
                        ProviderUserId = Text(reader, 11),
                        Email = Text(reader, 12)
                    }
                };
                byKey[Key(snapshot.Provider, snapshot.ProjectId)] = snapshot;
            }
        }

        using (var command = Command("SELECT provider, project_id, item_type, scanned_at, complete FROM snapshot_coverage;", null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (byKey.TryGetValue(Key(reader.GetString(0), reader.GetString(1)), out var snapshot))
                {
                    var itemType = reader.GetString(2);
                    snapshot.Coverage[itemType] = new TypeCoverage
                    {
                        ScannedAt = ParseDate(reader.GetString(3)) ?? DateTimeOffset.MinValue,
                        Complete = reader.GetInt64(4) != 0
                    };
                    if (!snapshot.ItemTypes.Contains(itemType, StringComparer.OrdinalIgnoreCase))
                    {
                        snapshot.ItemTypes.Add(itemType);
                    }
                }
            }
        }

        var items = new Dictionary<string, SnapshotItem>(StringComparer.Ordinal);
        using (var command = Command(
                   "SELECT provider, project_id, item_type, item_id, number, title, status, status_rank, closed, responsible, contract, " +
                   "attachment_count, created_at, modified_at, due_at, source_url, revision FROM items;",
                   null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!byKey.TryGetValue(Key(reader.GetString(0), reader.GetString(1)), out var snapshot))
                {
                    continue;
                }

                var rank = Number(reader, 7);
                var item = new SnapshotItem
                {
                    ItemType = reader.GetString(2),
                    ItemId = reader.GetString(3),
                    Number = reader.GetString(4),
                    Title = reader.GetString(5),
                    Status = reader.GetString(6),
                    StatusRank = rank is null ? null : (int)rank.Value,
                    Closed = reader.GetInt64(8) != 0,
                    Responsible = reader.GetString(9),
                    Contract = reader.GetString(10),
                    AttachmentCount = (int)reader.GetInt64(11),
                    CreatedAt = ParseDate(Text(reader, 12)),
                    ModifiedAt = ParseDate(Text(reader, 13)),
                    DueAt = ParseDate(Text(reader, 14)),
                    SourceUrl = Text(reader, 15),
                    Revision = Text(reader, 16)
                };
                snapshot.Items.Add(item);
                items[$"{snapshot.Provider}|{snapshot.ProjectId}|{item.ItemType}|{item.ItemId}"] = item;
            }
        }

        using (var command = Command(
                   "SELECT provider, project_id, item_type, item_id, name, company_id, company, state, since, provider_user_id, email " +
                   "FROM item_holders ORDER BY ordinal;",
                   null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = $"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}";
                if (items.TryGetValue(key, out var item))
                {
                    item.Holders.Add(new SnapshotHolder
                    {
                        Name = reader.GetString(4),
                        CompanyId = Text(reader, 5),
                        Company = Text(reader, 6),
                        State = reader.GetString(7),
                        Since = ParseDate(Text(reader, 8)),
                        ProviderUserId = Text(reader, 9),
                        Email = Text(reader, 10)
                    });
                }
            }
        }

        return byKey.Values.ToList();
    }

    private List<FolderScanRecord> LoadFolderScans()
    {
        var scans = new Dictionary<string, (string ProjectId, string ItemType, string Signature, string TypePath, bool TypeExists,
            string ReturnedPath, bool ReturnedExists, bool Shared, string? Error, DateTimeOffset ScannedAt)>(StringComparer.Ordinal);
        var entries = new Dictionary<string, List<FolderEntry>>(StringComparer.Ordinal);
        var containers = new Dictionary<string, List<FolderStamp>>(StringComparer.Ordinal);

        using (var command = Command(
                   "SELECT project_id, item_type, signature, type_folder_path, type_folder_exists, returned_folder_path, " +
                   "returned_folder_exists, returned_shared, error, scanned_at FROM folder_scans;",
                   null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var key = $"{reader.GetString(0)}|{reader.GetString(1)}";
                scans[key] = (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0,
                    reader.GetString(5), reader.GetInt64(6) != 0, reader.GetInt64(7) != 0, Text(reader, 8),
                    ParseDate(reader.GetString(9)) ?? DateTimeOffset.MinValue);
                entries[key] = new List<FolderEntry>();
                containers[key] = new List<FolderStamp>();
            }
        }

        using (var command = Command(
                   "SELECT project_id, item_type, name, full_path, location, last_write_ticks, groups_path, returned_folder, relative_parent " +
                   "FROM folder_entries ORDER BY project_id, item_type, ordinal;",
                   null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!entries.TryGetValue($"{reader.GetString(0)}|{reader.GetString(1)}", out var list))
                {
                    continue;
                }

                list.Add(new FolderEntry(
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4) == "returned" ? FolderLocation.Returned : FolderLocation.Active,
                    new DateTime(reader.GetInt64(5), DateTimeKind.Utc))
                {
                    Groups = reader.GetString(6).Split('\\', StringSplitOptions.RemoveEmptyEntries),
                    ReturnedFolder = Text(reader, 7),
                    RelativeParent = reader.GetString(8)
                });
            }
        }

        using (var command = Command(
                   "SELECT project_id, item_type, path, last_write_ticks, folder_exists FROM folder_containers ORDER BY project_id, item_type, ordinal;",
                   null))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (containers.TryGetValue($"{reader.GetString(0)}|{reader.GetString(1)}", out var list))
                {
                    list.Add(new FolderStamp(reader.GetString(2), new DateTime(reader.GetInt64(3), DateTimeKind.Utc), reader.GetInt64(4) != 0));
                }
            }
        }

        return scans.Select(pair => new FolderScanRecord(pair.Value.ProjectId, new TypeFolderScan
        {
            ItemType = pair.Value.ItemType,
            Signature = pair.Value.Signature,
            TypeFolderPath = pair.Value.TypePath,
            TypeFolderExists = pair.Value.TypeExists,
            ReturnedFolderPath = pair.Value.ReturnedPath,
            ReturnedFolderExists = pair.Value.ReturnedExists,
            ReturnedShared = pair.Value.Shared,
            Error = pair.Value.Error,
            ScannedAt = pair.Value.ScannedAt,
            Folders = entries[pair.Key],
            Containers = containers[pair.Key]
        })).ToList();
    }

    private List<CatalogProject> LoadCatalog()
    {
        var projects = new List<CatalogProject>();
        using var command = Command("SELECT provider, account_id, project_id, account_name, name, host, seen_at FROM provider_projects ORDER BY name;", null);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var accountId = reader.GetString(1);
            projects.Add(new CatalogProject
            {
                Provider = reader.GetString(0),
                AccountId = accountId.Length == 0 ? null : accountId,
                ProjectId = reader.GetString(2),
                AccountName = Text(reader, 3),
                Name = reader.GetString(4),
                Host = Text(reader, 5),
                SeenAt = ParseDate(reader.GetString(6)) ?? DateTimeOffset.MinValue
            });
        }

        return projects;
    }

    private List<ProviderAccount> LoadAccounts()
    {
        var accounts = new List<ProviderAccount>();
        using var command = Command("SELECT provider, user_name, company, signed_in_at, needs_sign_in, provider_user_id, email FROM accounts;", null);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            accounts.Add(new ProviderAccount
            {
                Provider = reader.GetString(0),
                UserName = Text(reader, 1),
                Company = Text(reader, 2),
                SignedInAt = ParseDate(reader.GetString(3)) ?? DateTimeOffset.MinValue,
                NeedsSignIn = reader.GetInt64(4) != 0,
                ProviderUserId = Text(reader, 5),
                Email = Text(reader, 6)
            });
        }

        return accounts;
    }

    // The sync runs worth keeping: the latest $limit of all, plus each project's latest complete run of
    // each mode (a Procore project's last full read and last count check decide how its next read goes).
    private const string KeptSyncRuns =
        "WITH ranked AS (SELECT id, status, ROW_NUMBER() OVER (ORDER BY started_at DESC) AS recent, " +
        "ROW_NUMBER() OVER (PARTITION BY provider, project_id, run_mode, status ORDER BY started_at DESC) AS latest " +
        "FROM sync_runs) SELECT id FROM ranked WHERE recent <= $limit OR (latest = 1 AND status = 'complete')";

    private List<SyncRun> LoadSyncRuns(int limit)
    {
        var runs = new List<SyncRun>();
        using var command = Command(
            "SELECT id, provider, project_id, run_mode, started_at, finished_at, status, item_count, changed_count, error " +
            $"FROM sync_runs WHERE id IN ({KeptSyncRuns}) ORDER BY started_at DESC;",
            null,
            ("$limit", limit));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            runs.Add(new SyncRun
            {
                Id = reader.GetString(0),
                Provider = reader.GetString(1),
                ProjectId = reader.GetString(2),
                Mode = reader.GetString(3),
                StartedAt = ParseDate(reader.GetString(4)) ?? DateTimeOffset.MinValue,
                FinishedAt = ParseDate(Text(reader, 5)),
                Status = reader.GetString(6),
                ItemCount = (int)reader.GetInt64(7),
                ChangedCount = (int)reader.GetInt64(8),
                Error = Text(reader, 9)
            });
        }

        runs.Reverse();
        return runs;
    }

    // ---- Saving -------------------------------------------------------------------------------

    public void SaveSettings(TrackerSettings settings)
    {
        var now = Iso(DateTimeOffset.Now);
        using var scope = new WriteScope(this);
        var transaction = scope.Transaction;
        SetPreference("theme", settings.Theme.ToString(), transaction);
        SetPreference("folders", Json(settings.Folders), transaction);
        SetPreference("returned", Json(settings.Returned), transaction);
        SetPreference("browser", Json(settings.Browser), transaction);
        SetPreference("sync", Json(settings.Sync), transaction);
        SetPreference("procore", Json(settings.Procore), transaction);
        SetPreference("workloadTracker", Json(settings.WorkloadTracker), transaction);

        var stored = new Dictionary<string, (string Hash, bool Deleted)>(StringComparer.Ordinal);
        using (var command = Command("SELECT id, content_hash, deleted_at FROM projects;", transaction))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                stored[reader.GetString(0)] = (reader.GetString(1), !reader.IsDBNull(2));
            }
        }

        for (var index = 0; index < settings.Projects.Count; index++)
        {
            var project = settings.Projects[index];
            var payload = Json(project);
            var hash = Hash(payload);
            if (stored.TryGetValue(project.Id, out var previous) && previous.Hash == hash && !previous.Deleted)
            {
                Execute("UPDATE projects SET sort_order = $order WHERE id = $id AND sort_order <> $order;", transaction,
                    ("$order", index), ("$id", project.Id));
                continue;
            }

            var folders = project.FoldersOverride;
            Execute(
                "INSERT INTO projects (id, name, ca_folder, provider, provider_project_id, provider_account_id, provider_project_name, " +
                "provider_host, company_id, company_name, rfi_folder, submittal_folder, returned_folder, phase, filter_items, filter_folders, " +
                "include_unlabeled, sort_order, content_hash, created_at, updated_at, deleted_at) VALUES ($id, $name, $ca, $provider, $providerProject, " +
                "$account, $providerName, $host, $companyId, $companyName, $rfi, $submittal, $returned, $phase, $filterItems, $filterFolders, " +
                "$unlabeled, $order, $hash, $now, $now, NULL) ON CONFLICT (id) DO UPDATE SET name = excluded.name, ca_folder = excluded.ca_folder, " +
                "provider = excluded.provider, provider_project_id = excluded.provider_project_id, provider_account_id = excluded.provider_account_id, " +
                "provider_project_name = excluded.provider_project_name, provider_host = excluded.provider_host, company_id = excluded.company_id, " +
                "company_name = excluded.company_name, rfi_folder = excluded.rfi_folder, submittal_folder = excluded.submittal_folder, " +
                "returned_folder = excluded.returned_folder, phase = excluded.phase, filter_items = excluded.filter_items, " +
                "filter_folders = excluded.filter_folders, include_unlabeled = excluded.include_unlabeled, sort_order = excluded.sort_order, " +
                "content_hash = excluded.content_hash, updated_at = excluded.updated_at, deleted_at = NULL;",
                transaction,
                ("$id", project.Id), ("$name", project.Name ?? ""), ("$ca", project.CaFolder ?? ""),
                ("$provider", project.Connection?.Provider), ("$providerProject", project.Connection?.ProjectId),
                ("$account", project.Connection?.AccountId), ("$providerName", project.Connection?.ProjectName),
                ("$host", project.Connection?.Host), ("$companyId", project.CompanyId), ("$companyName", project.CompanyName),
                ("$rfi", folders?.RfiFolder), ("$submittal", folders?.SubmittalFolder), ("$returned", folders?.ReturnedFolder),
                ("$phase", (project.Scope ?? new PhaseScope()).Phase.ToString()),
                ("$filterItems", Flag(project.Scope?.FilterItems == true)), ("$filterFolders", Flag(project.Scope?.FilterFolders == true)),
                ("$unlabeled", Flag(project.Scope?.IncludeUnlabeled == true)), ("$order", index), ("$hash", hash), ("$now", now));

            Execute("DELETE FROM discipline_choices WHERE project_id = $id;", transaction, ("$id", project.Id));
            foreach (var (key, code) in project.Disciplines ?? new Dictionary<string, string>())
            {
                var (itemType, kind, target) = SplitDisciplineKey(key);
                if (itemType.Length == 0 || target.Length == 0)
                {
                    continue;
                }

                Execute(
                    "INSERT INTO discipline_choices (project_id, item_type, target_kind, target, discipline, updated_at) " +
                    "VALUES ($project, $type, $kind, $target, $discipline, $now) ON CONFLICT (project_id, item_type, target_kind, target) " +
                    "DO UPDATE SET discipline = excluded.discipline, updated_at = excluded.updated_at;",
                    transaction,
                    ("$project", project.Id), ("$type", itemType), ("$kind", kind), ("$target", target), ("$discipline", code), ("$now", now));
            }

        }

        var kept = settings.Projects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, previous) in stored)
        {
            if (previous.Deleted || kept.Contains(id))
            {
                continue;
            }

            Execute("UPDATE projects SET deleted_at = $now, updated_at = $now WHERE id = $id;", transaction, ("$now", now), ("$id", id));
            Execute("DELETE FROM discipline_choices WHERE project_id = $id;", transaction, ("$id", id));
            Execute("DELETE FROM folder_entries WHERE project_id = $id;", transaction, ("$id", id));
            Execute("DELETE FROM folder_containers WHERE project_id = $id;", transaction, ("$id", id));
            Execute("DELETE FROM folder_scans WHERE project_id = $id;", transaction, ("$id", id));
        }

        scope.Commit();
    }

    /// <summary>"rfi|p000210 - doors" → (rfi, folder, p000210 - doors); "item|rfi|abc" → (rfi, item, abc).</summary>
    internal static (string ItemType, string Kind, string Target) SplitDisciplineKey(string key)
    {
        if (key.StartsWith("item|", StringComparison.OrdinalIgnoreCase))
        {
            var rest = key["item|".Length..];
            var bar = rest.IndexOf('|');
            return bar <= 0 ? ("", "", "") : (rest[..bar], "item", rest[(bar + 1)..]);
        }

        var separator = key.IndexOf('|');
        return separator <= 0 ? ("", "", "") : (key[..separator], "folder", key[(separator + 1)..]);
    }

    public void SaveSnapshot(ProviderSnapshot snapshot)
    {
        var now = Iso(DateTimeOffset.Now);
        using var scope = new WriteScope(this);
        var transaction = scope.Transaction;
        Execute(
            "INSERT INTO provider_snapshots (provider, project_id, schema_version, account_id, project_name, project_url, complete, scanned_at, " +
            "user_full_name, user_company_id, user_company, user_provider_id, user_email) VALUES ($provider, $project, $version, $account, $name, " +
            "$url, $complete, $scanned, $user, $companyId, $company, $userId, $userEmail) ON CONFLICT (provider, project_id) DO UPDATE SET " +
            "schema_version = excluded.schema_version, user_provider_id = excluded.user_provider_id, user_email = excluded.user_email, " +
            "account_id = excluded.account_id, project_name = excluded.project_name, project_url = excluded.project_url, " +
            "complete = excluded.complete, scanned_at = excluded.scanned_at, user_full_name = excluded.user_full_name, " +
            "user_company_id = excluded.user_company_id, user_company = excluded.user_company;",
            transaction,
            ("$provider", snapshot.Provider), ("$project", snapshot.ProjectId), ("$version", snapshot.SchemaVersion),
            ("$account", snapshot.AccountId), ("$name", snapshot.ProjectName ?? ""), ("$url", snapshot.ProjectUrl),
            ("$complete", Flag(snapshot.Complete)), ("$scanned", Iso(snapshot.ScannedAt)),
            ("$user", snapshot.User?.FullName ?? ""), ("$companyId", snapshot.User?.CompanyId), ("$company", snapshot.User?.Company),
            ("$userId", snapshot.User?.ProviderUserId), ("$userEmail", snapshot.User?.Email));

        Execute("DELETE FROM snapshot_coverage WHERE provider = $provider AND project_id = $project;", transaction,
            ("$provider", snapshot.Provider), ("$project", snapshot.ProjectId));
        foreach (var itemType in snapshot.ItemTypes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var coverage = snapshot.CoverageFor(itemType);
            Execute(
                "INSERT INTO snapshot_coverage (provider, project_id, item_type, scanned_at, complete) VALUES ($provider, $project, $type, $scanned, $complete);",
                transaction,
                ("$provider", snapshot.Provider), ("$project", snapshot.ProjectId), ("$type", itemType),
                ("$scanned", Iso(coverage?.ScannedAt ?? snapshot.ScannedAt)), ("$complete", Flag(coverage?.Complete ?? snapshot.Complete)));
        }

        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = Command("SELECT item_type, item_id, content_hash FROM items WHERE provider = $provider AND project_id = $project;",
                   transaction, ("$provider", snapshot.Provider), ("$project", snapshot.ProjectId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                stored[$"{reader.GetString(0)}|{reader.GetString(1)}"] = reader.GetString(2);
            }
        }

        using var upsertItem = Command(
            "INSERT INTO items (provider, project_id, item_type, item_id, number, title, status, status_rank, closed, responsible, contract, " +
            "attachment_count, created_at, modified_at, due_at, source_url, revision, content_hash, first_seen_at, updated_at) VALUES ($provider, $project, " +
            "$type, $id, $number, $title, $status, $rank, $closed, $responsible, $contract, $attachments, $created, $modified, $due, $url, $revision, $hash, " +
            "$now, $now) ON CONFLICT (provider, project_id, item_type, item_id) DO UPDATE SET number = excluded.number, title = excluded.title, " +
            "status = excluded.status, status_rank = excluded.status_rank, closed = excluded.closed, responsible = excluded.responsible, " +
            "contract = excluded.contract, attachment_count = excluded.attachment_count, created_at = excluded.created_at, " +
            "modified_at = excluded.modified_at, due_at = excluded.due_at, source_url = excluded.source_url, revision = excluded.revision, " +
            "content_hash = excluded.content_hash, " +
            "updated_at = excluded.updated_at;",
            transaction,
            ("$provider", null), ("$project", null), ("$type", null), ("$id", null), ("$number", null), ("$title", null), ("$status", null),
            ("$rank", null), ("$closed", null), ("$responsible", null), ("$contract", null), ("$attachments", null), ("$created", null),
            ("$modified", null), ("$due", null), ("$url", null), ("$revision", null), ("$hash", null), ("$now", null));
        using var deleteHolders = Command(
            "DELETE FROM item_holders WHERE provider = $provider AND project_id = $project AND item_type = $type AND item_id = $id;",
            transaction, ("$provider", null), ("$project", null), ("$type", null), ("$id", null));
        using var insertHolder = Command(
            "INSERT INTO item_holders (provider, project_id, item_type, item_id, ordinal, name, company_id, company, state, since, " +
            "provider_user_id, email) VALUES ($provider, $project, $type, $id, $ordinal, $name, $companyId, $company, $state, $since, " +
            "$userId, $email);",
            transaction,
            ("$provider", null), ("$project", null), ("$type", null), ("$id", null), ("$ordinal", null), ("$name", null),
            ("$companyId", null), ("$company", null), ("$state", null), ("$since", null), ("$userId", null), ("$email", null));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            var key = $"{item.ItemType}|{item.ItemId}";
            if (!seen.Add(key))
            {
                continue;
            }

            var payload = ItemJson(item);
            var hash = Hash(payload);
            if (stored.TryGetValue(key, out var previousHash) && previousHash == hash)
            {
                continue;
            }

            Set(upsertItem, ("$provider", snapshot.Provider), ("$project", snapshot.ProjectId), ("$type", item.ItemType), ("$id", item.ItemId),
                ("$number", item.Number ?? ""), ("$title", item.Title ?? ""), ("$status", item.Status ?? ""), ("$rank", item.StatusRank),
                ("$closed", Flag(item.Closed)), ("$responsible", item.Responsible ?? ""), ("$contract", item.Contract ?? ""),
                ("$attachments", item.AttachmentCount), ("$created", Iso(item.CreatedAt)), ("$modified", Iso(item.ModifiedAt)),
                ("$due", Iso(item.DueAt)), ("$url", item.SourceUrl), ("$revision", item.Revision), ("$hash", hash), ("$now", now));
            upsertItem.ExecuteNonQuery();

            Set(deleteHolders, ("$provider", snapshot.Provider), ("$project", snapshot.ProjectId), ("$type", item.ItemType), ("$id", item.ItemId));
            deleteHolders.ExecuteNonQuery();
            for (var ordinal = 0; ordinal < item.Holders.Count; ordinal++)
            {
                var holder = item.Holders[ordinal];
                Set(insertHolder, ("$provider", snapshot.Provider), ("$project", snapshot.ProjectId), ("$type", item.ItemType),
                    ("$id", item.ItemId), ("$ordinal", ordinal), ("$name", holder.Name ?? ""), ("$companyId", holder.CompanyId),
                    ("$company", holder.Company), ("$state", holder.State ?? ""), ("$since", Iso(holder.Since)),
                    ("$userId", holder.ProviderUserId), ("$email", holder.Email));
                insertHolder.ExecuteNonQuery();
            }

        }

        foreach (var key in stored.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            var bar = key.IndexOf('|');
            var itemType = key[..bar];
            var itemId = key[(bar + 1)..];
            var parameters = new (string, object?)[]
            {
                ("$provider", snapshot.Provider), ("$project", snapshot.ProjectId), ("$type", itemType), ("$id", itemId)
            };
            Execute("DELETE FROM item_holders WHERE provider = $provider AND project_id = $project AND item_type = $type AND item_id = $id;", transaction, parameters);
            Execute("DELETE FROM items WHERE provider = $provider AND project_id = $project AND item_type = $type AND item_id = $id;", transaction, parameters);
        }

        scope.Commit();
    }

    private static void Set(SqliteCommand command, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            command.Parameters[name].Value = value ?? DBNull.Value;
        }
    }

    public void SaveFolderScan(string projectId, TypeFolderScan scan)
    {
        using var scope = new WriteScope(this);
        var transaction = scope.Transaction;
        Execute(
            "INSERT INTO folder_scans (project_id, item_type, signature, type_folder_path, type_folder_exists, returned_folder_path, " +
            "returned_folder_exists, returned_shared, error, scanned_at) VALUES ($project, $type, $signature, $typePath, $typeExists, " +
            "$returnedPath, $returnedExists, $shared, $error, $scanned) ON CONFLICT (project_id, item_type) DO UPDATE SET " +
            "signature = excluded.signature, type_folder_path = excluded.type_folder_path, type_folder_exists = excluded.type_folder_exists, " +
            "returned_folder_path = excluded.returned_folder_path, returned_folder_exists = excluded.returned_folder_exists, " +
            "returned_shared = excluded.returned_shared, error = excluded.error, scanned_at = excluded.scanned_at;",
            transaction,
            ("$project", projectId), ("$type", scan.ItemType), ("$signature", scan.Signature), ("$typePath", scan.TypeFolderPath ?? ""),
            ("$typeExists", Flag(scan.TypeFolderExists)), ("$returnedPath", scan.ReturnedFolderPath ?? ""),
            ("$returnedExists", Flag(scan.ReturnedFolderExists)), ("$shared", Flag(scan.ReturnedShared)), ("$error", scan.Error),
            ("$scanned", Iso(scan.ScannedAt)));

        var key = new (string, object?)[] { ("$project", projectId), ("$type", scan.ItemType) };
        Execute("DELETE FROM folder_entries WHERE project_id = $project AND item_type = $type;", transaction, key);
        Execute("DELETE FROM folder_containers WHERE project_id = $project AND item_type = $type;", transaction, key);

        using var insertEntry = Command(
            "INSERT INTO folder_entries (project_id, item_type, ordinal, name, full_path, location, last_write_ticks, groups_path, " +
            "returned_folder, relative_parent) VALUES ($project, $type, $ordinal, $name, $path, $location, $ticks, $groups, $returned, $parent);",
            transaction,
            ("$project", projectId), ("$type", scan.ItemType), ("$ordinal", null), ("$name", null), ("$path", null), ("$location", null),
            ("$ticks", null), ("$groups", null), ("$returned", null), ("$parent", null));
        for (var ordinal = 0; ordinal < scan.Folders.Count; ordinal++)
        {
            var folder = scan.Folders[ordinal];
            Set(insertEntry, ("$ordinal", ordinal), ("$name", folder.Name), ("$path", folder.FullPath),
                ("$location", folder.Location == FolderLocation.Returned ? "returned" : "active"), ("$ticks", folder.LastWriteTimeUtc.Ticks),
                ("$groups", string.Join("\\", folder.Groups)), ("$returned", folder.ReturnedFolder), ("$parent", folder.RelativeParent ?? ""));
            insertEntry.ExecuteNonQuery();
        }

        using var insertContainer = Command(
            "INSERT INTO folder_containers (project_id, item_type, ordinal, path, last_write_ticks, folder_exists) " +
            "VALUES ($project, $type, $ordinal, $path, $ticks, $exists);",
            transaction,
            ("$project", projectId), ("$type", scan.ItemType), ("$ordinal", null), ("$path", null), ("$ticks", null), ("$exists", null));
        for (var ordinal = 0; ordinal < scan.Containers.Count; ordinal++)
        {
            var stamp = scan.Containers[ordinal];
            Set(insertContainer, ("$ordinal", ordinal), ("$path", stamp.Path), ("$ticks", stamp.LastWriteTimeUtc.Ticks), ("$exists", Flag(stamp.Exists)));
            insertContainer.ExecuteNonQuery();
        }

        scope.Commit();
    }

    public void SaveCatalog(IReadOnlyList<CatalogProject> projects)
    {
        using var scope = new WriteScope(this);
        var transaction = scope.Transaction;
        Execute("DELETE FROM provider_projects;", transaction);
        using var insert = Command(
            "INSERT INTO provider_projects (provider, account_id, project_id, account_name, name, host, seen_at) " +
            "VALUES ($provider, $account, $project, $accountName, $name, $host, $seen) ON CONFLICT (provider, account_id, project_id) " +
            "DO UPDATE SET account_name = excluded.account_name, name = excluded.name, host = excluded.host, seen_at = excluded.seen_at;",
            transaction,
            ("$provider", null), ("$account", null), ("$project", null), ("$accountName", null), ("$name", null), ("$host", null), ("$seen", null));
        foreach (var project in projects)
        {
            Set(insert, ("$provider", project.Provider), ("$account", project.AccountId ?? ""), ("$project", project.ProjectId),
                ("$accountName", project.AccountName), ("$name", project.Name ?? ""), ("$host", project.Host), ("$seen", Iso(project.SeenAt)));
            insert.ExecuteNonQuery();
        }

        scope.Commit();
    }

    public void SaveAccounts(IReadOnlyList<ProviderAccount> accounts)
    {
        using var scope = new WriteScope(this);
        var transaction = scope.Transaction;
        Execute("DELETE FROM accounts;", transaction);
        foreach (var account in accounts)
        {
            Execute(
                "INSERT INTO accounts (provider, user_name, company, signed_in_at, needs_sign_in, provider_user_id, email) " +
                "VALUES ($provider, $user, $company, $at, $needs, $userId, $email) " +
                "ON CONFLICT (provider) DO UPDATE SET user_name = excluded.user_name, company = excluded.company, " +
                "signed_in_at = excluded.signed_in_at, needs_sign_in = excluded.needs_sign_in, " +
                "provider_user_id = excluded.provider_user_id, email = excluded.email;",
                transaction,
                ("$provider", account.Provider.ToLowerInvariant()), ("$user", account.UserName), ("$company", account.Company),
                ("$at", Iso(account.SignedInAt)), ("$needs", Flag(account.NeedsSignIn)),
                ("$userId", account.ProviderUserId), ("$email", account.Email));
        }

        scope.Commit();
    }

    public void SaveViewState(ViewState view)
    {
        using var scope = new WriteScope(this);
        var transaction = scope.Transaction;
        SetPreference("view", Json(view), transaction);
        scope.Commit();
    }

    public void RecordSyncRun(SyncRun run)
    {
        using var scope = new WriteScope(this);
        var transaction = scope.Transaction;
        Execute(
            "INSERT INTO sync_runs (id, provider, project_id, run_mode, started_at, finished_at, status, item_count, changed_count, error) " +
            "VALUES ($id, $provider, $project, $mode, $started, $finished, $status, $items, $changed, $error) ON CONFLICT (id) DO UPDATE SET " +
            "finished_at = excluded.finished_at, status = excluded.status, item_count = excluded.item_count, " +
            "changed_count = excluded.changed_count, error = excluded.error;",
            transaction,
            ("$id", run.Id), ("$provider", run.Provider), ("$project", run.ProjectId), ("$mode", run.Mode), ("$started", Iso(run.StartedAt)),
            ("$finished", Iso(run.FinishedAt)), ("$status", run.Status), ("$items", run.ItemCount), ("$changed", run.ChangedCount),
            ("$error", run.Error));
        Execute($"DELETE FROM sync_runs WHERE id NOT IN ({KeptSyncRuns});", transaction, ("$limit", 2000));
        scope.Commit();
    }

    // ---- Workload Tracker outbox -------------------------------------------------------------

    private List<OutboxEntry> LoadOutbox()
    {
        var entries = new List<OutboxEntry>();
        using var command = Command(
            "SELECT provider, project_id, account, full_set, changed, removed, verified, version, updated_at, attempts, next_attempt_at, " +
            "last_error FROM wt_outbox;",
            null);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new OutboxEntry
            {
                Provider = reader.GetString(0),
                ProjectId = reader.GetString(1),
                Account = reader.GetString(2),
                FullSet = reader.GetInt64(3) != 0,
                Changed = new HashSet<string>(FromJson<List<string>>(reader.GetString(4)) ?? new List<string>(), StringComparer.Ordinal),
                Removed = new HashSet<string>(FromJson<List<string>>(reader.GetString(5)) ?? new List<string>(), StringComparer.Ordinal),
                Verified = new HashSet<string>(FromJson<List<string>>(reader.GetString(6)) ?? new List<string>(), StringComparer.OrdinalIgnoreCase),
                Version = reader.GetInt64(7),
                UpdatedAt = ParseDate(reader.GetString(8)) ?? DateTimeOffset.MinValue,
                Attempts = (int)reader.GetInt64(9),
                NextAttemptAt = ParseDate(Text(reader, 10)),
                LastError = Text(reader, 11)
            });
        }

        return entries;
    }

    public void SaveOutboxEntry(OutboxEntry entry)
    {
        using var scope = new WriteScope(this);
        Execute(
            "INSERT INTO wt_outbox (provider, project_id, account, full_set, changed, removed, verified, version, updated_at, attempts, " +
            "next_attempt_at, last_error) VALUES ($provider, $project, $account, $full, $changed, $removed, $verified, $version, $updated, " +
            "$attempts, $next, $error) ON CONFLICT (provider, project_id) DO UPDATE SET account = excluded.account, " +
            "full_set = excluded.full_set, changed = excluded.changed, removed = excluded.removed, verified = excluded.verified, " +
            "version = excluded.version, updated_at = excluded.updated_at, attempts = excluded.attempts, " +
            "next_attempt_at = excluded.next_attempt_at, last_error = excluded.last_error;",
            scope.Transaction,
            ("$provider", entry.Provider.ToLowerInvariant()), ("$project", entry.ProjectId), ("$account", entry.Account),
            ("$full", Flag(entry.FullSet)), ("$changed", Json(entry.Changed.Order(StringComparer.Ordinal).ToList())),
            ("$removed", Json(entry.Removed.Order(StringComparer.Ordinal).ToList())),
            ("$verified", Json(entry.Verified.Order(StringComparer.OrdinalIgnoreCase).ToList())), ("$version", entry.Version),
            ("$updated", Iso(entry.UpdatedAt)), ("$attempts", entry.Attempts), ("$next", Iso(entry.NextAttemptAt)), ("$error", entry.LastError));
        scope.Commit();
    }

    public void DeleteOutboxEntry(string provider, string projectId)
    {
        using var scope = new WriteScope(this);
        Execute("DELETE FROM wt_outbox WHERE provider = $provider AND project_id = $project;", scope.Transaction,
            ("$provider", provider.ToLowerInvariant()), ("$project", projectId));
        scope.Commit();
    }

    public void ClearOutbox()
    {
        using var scope = new WriteScope(this);
        Execute("DELETE FROM wt_outbox;", scope.Transaction);
        scope.Commit();
    }

    // ---- Workload Tracker item assignments ---------------------------------------------------

    private List<WorkloadAssignment> LoadAssignments()
    {
        var rows = new List<WorkloadAssignment>();
        using var command = Command(
            "SELECT tracker_project_id, provider, host, provider_project_id, item_type, number, person_id, person_name, person_active, " +
            "active, assigned_at, assigned_by_name, completed_at, pending FROM wt_assignments;",
            null);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new WorkloadAssignment
            {
                TrackerProjectId = (int)reader.GetInt64(0),
                Provider = reader.GetString(1),
                Host = reader.GetString(2),
                ProviderProjectId = reader.GetString(3),
                ItemType = reader.GetString(4),
                Number = reader.GetString(5),
                PersonId = (int)reader.GetInt64(6),
                PersonName = reader.GetString(7),
                PersonActive = reader.GetInt64(8) != 0,
                Active = reader.GetInt64(9) != 0,
                AssignedAt = ParseDate(reader.GetString(10)) ?? DateTimeOffset.MinValue,
                AssignedByName = Text(reader, 11),
                CompletedAt = ParseDate(Text(reader, 12)),
                Pending = reader.GetInt64(13) != 0
            });
        }

        return rows;
    }

    public void ReplaceAssignments(int trackerProjectId, IReadOnlyList<WorkloadAssignment> assignments)
    {
        using var scope = new WriteScope(this);
        Execute("DELETE FROM wt_assignments WHERE tracker_project_id = $project;", scope.Transaction, ("$project", trackerProjectId));
        foreach (var row in assignments)
        {
            Execute(
                "INSERT OR REPLACE INTO wt_assignments (tracker_project_id, provider, host, provider_project_id, item_type, number, person_id, " +
                "person_name, person_active, active, assigned_at, assigned_by_name, completed_at, pending) VALUES ($project, $provider, $host, " +
                "$providerProject, $type, $number, $person, $name, $personActive, $active, $assigned, $by, $completed, $pending);",
                scope.Transaction,
                ("$project", trackerProjectId), ("$provider", row.Provider.ToLowerInvariant()), ("$host", row.Host),
                ("$providerProject", row.ProviderProjectId), ("$type", row.ItemType.ToLowerInvariant()), ("$number", row.Number),
                ("$person", row.PersonId), ("$name", row.PersonName), ("$personActive", Flag(row.PersonActive)), ("$active", Flag(row.Active)),
                ("$assigned", Iso(row.AssignedAt)), ("$by", row.AssignedByName), ("$completed", Iso(row.CompletedAt)), ("$pending", Flag(row.Pending)));
        }

        scope.Commit();
    }

    public void ClearAssignments()
    {
        using var scope = new WriteScope(this);
        Execute("DELETE FROM wt_assignments;", scope.Transaction);
        scope.Commit();
    }

    // ---- Workload Tracker "marked complete" marks ---------------------------------------------

    private List<WorkloadCompletion> LoadCompletions()
    {
        var rows = new List<WorkloadCompletion>();
        using var command = Command(
            "SELECT tracker_project_id, provider, host, provider_project_id, item_type, provider_item_id, number, revision, person_id, " +
            "person_name, marked_by_name, marked_at FROM wt_completions;",
            null);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new WorkloadCompletion
            {
                TrackerProjectId = (int)reader.GetInt64(0),
                Provider = reader.GetString(1),
                Host = reader.GetString(2),
                ProviderProjectId = reader.GetString(3),
                ItemType = reader.GetString(4),
                ProviderItemId = reader.GetString(5),
                Number = reader.GetString(6),
                Revision = Text(reader, 7),
                PersonId = (int)reader.GetInt64(8),
                PersonName = reader.GetString(9),
                MarkedByName = Text(reader, 10),
                MarkedAt = ParseDate(reader.GetString(11)) ?? DateTimeOffset.MinValue
            });
        }

        return rows;
    }

    public void ReplaceCompletions(int trackerProjectId, IReadOnlyList<WorkloadCompletion> completions)
    {
        using var scope = new WriteScope(this);
        Execute("DELETE FROM wt_completions WHERE tracker_project_id = $project;", scope.Transaction, ("$project", trackerProjectId));
        foreach (var row in completions)
        {
            Execute(
                "INSERT OR REPLACE INTO wt_completions (tracker_project_id, provider, host, provider_project_id, item_type, provider_item_id, " +
                "number, revision, person_id, person_name, marked_by_name, marked_at) VALUES ($project, $provider, $host, $providerProject, " +
                "$type, $item, $number, $revision, $person, $name, $by, $marked);",
                scope.Transaction,
                ("$project", trackerProjectId), ("$provider", row.Provider.ToLowerInvariant()), ("$host", row.Host),
                ("$providerProject", row.ProviderProjectId), ("$type", row.ItemType.ToLowerInvariant()), ("$item", row.ProviderItemId),
                ("$number", row.Number), ("$revision", row.Revision), ("$person", row.PersonId), ("$name", row.PersonName),
                ("$by", row.MarkedByName), ("$marked", Iso(row.MarkedAt)));
        }

        scope.Commit();
    }

    public void ClearCompletions()
    {
        using var scope = new WriteScope(this);
        Execute("DELETE FROM wt_completions;", scope.Transaction);
        scope.Commit();
    }

    // ---- Workload Tracker: facts sent about items ---------------------------------------------

    private List<SentItemFacts> LoadSentFacts()
    {
        var rows = new List<SentItemFacts>();
        using var command = Command("SELECT provider, project_id, item_type, item_id, fingerprint FROM wt_item_facts;", null);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SentItemFacts(reader.GetString(0), reader.GetString(1), OutboxEntry.ItemKey(reader.GetString(2), reader.GetString(3)),
                reader.GetString(4)));
        }

        return rows;
    }

    public void SaveSentFacts(string provider, string projectId, IReadOnlyList<(string ItemKey, string Fingerprint)> sent, IReadOnlyList<string> forgotten)
    {
        using var scope = new WriteScope(this);
        using var upsert = Command(
            "INSERT INTO wt_item_facts (provider, project_id, item_type, item_id, fingerprint) VALUES ($provider, $project, $type, $id, $print) " +
            "ON CONFLICT (provider, project_id, item_type, item_id) DO UPDATE SET fingerprint = excluded.fingerprint;",
            scope.Transaction, ("$provider", provider.ToLowerInvariant()), ("$project", projectId), ("$type", null), ("$id", null), ("$print", null));
        foreach (var (key, fingerprint) in sent)
        {
            var (itemType, itemId) = OutboxEntry.SplitKey(key);
            Set(upsert, ("$type", itemType), ("$id", itemId), ("$print", fingerprint));
            upsert.ExecuteNonQuery();
        }

        using var delete = Command(
            "DELETE FROM wt_item_facts WHERE provider = $provider AND project_id = $project AND item_type = $type AND item_id = $id;",
            scope.Transaction, ("$provider", provider.ToLowerInvariant()), ("$project", projectId), ("$type", null), ("$id", null));
        foreach (var key in forgotten)
        {
            var (itemType, itemId) = OutboxEntry.SplitKey(key);
            Set(delete, ("$type", itemType), ("$id", itemId));
            delete.ExecuteNonQuery();
        }

        scope.Commit();
    }

    public void ClearSentFacts()
    {
        using var scope = new WriteScope(this);
        Execute("DELETE FROM wt_item_facts;", scope.Transaction);
        scope.Commit();
    }

    public void Dispose()
    {
        try
        {
            Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException)
        {
        }

        connection.Dispose();
    }
}
