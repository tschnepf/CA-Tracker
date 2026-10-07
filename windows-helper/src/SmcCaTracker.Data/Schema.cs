namespace SmcCaTracker.Data;

/// <summary>
/// Creates and upgrades the database tables. Each version's statements run once, in order, inside a
/// transaction; the version reached is kept in <c>schema_info</c>. Types and syntax are kept to what
/// SQLite and PostgreSQL both accept (TEXT, INTEGER, ON CONFLICT … DO UPDATE).
/// </summary>
internal static class Schema
{
    public const int CurrentVersion = 4;

    private static readonly string[][] Versions =
    {
        // Version 1
        new[]
        {
            """
            CREATE TABLE IF NOT EXISTS preferences (
                pref_key TEXT NOT NULL PRIMARY KEY,
                pref_value TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS projects (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                ca_folder TEXT NOT NULL,
                provider TEXT NULL,
                provider_project_id TEXT NULL,
                provider_account_id TEXT NULL,
                provider_project_name TEXT NULL,
                provider_host TEXT NULL,
                company_id TEXT NULL,
                company_name TEXT NULL,
                rfi_folder TEXT NULL,
                submittal_folder TEXT NULL,
                returned_folder TEXT NULL,
                phase TEXT NOT NULL,
                filter_items INTEGER NOT NULL,
                filter_folders INTEGER NOT NULL,
                include_unlabeled INTEGER NOT NULL,
                sort_order INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                deleted_at TEXT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS discipline_choices (
                project_id TEXT NOT NULL,
                item_type TEXT NOT NULL,
                target_kind TEXT NOT NULL,
                target TEXT NOT NULL,
                discipline TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (project_id, item_type, target_kind, target)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS provider_projects (
                provider TEXT NOT NULL,
                account_id TEXT NOT NULL,
                project_id TEXT NOT NULL,
                account_name TEXT NULL,
                name TEXT NOT NULL,
                host TEXT NULL,
                seen_at TEXT NOT NULL,
                PRIMARY KEY (provider, account_id, project_id)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS provider_snapshots (
                provider TEXT NOT NULL,
                project_id TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                account_id TEXT NULL,
                project_name TEXT NOT NULL,
                project_url TEXT NULL,
                complete INTEGER NOT NULL,
                scanned_at TEXT NOT NULL,
                user_full_name TEXT NOT NULL,
                user_company_id TEXT NULL,
                user_company TEXT NULL,
                PRIMARY KEY (provider, project_id)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS snapshot_coverage (
                provider TEXT NOT NULL,
                project_id TEXT NOT NULL,
                item_type TEXT NOT NULL,
                scanned_at TEXT NOT NULL,
                complete INTEGER NOT NULL,
                PRIMARY KEY (provider, project_id, item_type)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS items (
                provider TEXT NOT NULL,
                project_id TEXT NOT NULL,
                item_type TEXT NOT NULL,
                item_id TEXT NOT NULL,
                number TEXT NOT NULL,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                status_rank INTEGER NULL,
                closed INTEGER NOT NULL,
                responsible TEXT NOT NULL,
                contract TEXT NOT NULL,
                attachment_count INTEGER NOT NULL,
                created_at TEXT NULL,
                modified_at TEXT NULL,
                due_at TEXT NULL,
                source_url TEXT NULL,
                content_hash TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (provider, project_id, item_type, item_id)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS item_holders (
                provider TEXT NOT NULL,
                project_id TEXT NOT NULL,
                item_type TEXT NOT NULL,
                item_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                name TEXT NOT NULL,
                company_id TEXT NULL,
                company TEXT NULL,
                state TEXT NOT NULL,
                since TEXT NULL,
                PRIMARY KEY (provider, project_id, item_type, item_id, ordinal)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS folder_scans (
                project_id TEXT NOT NULL,
                item_type TEXT NOT NULL,
                signature TEXT NOT NULL,
                type_folder_path TEXT NOT NULL,
                type_folder_exists INTEGER NOT NULL,
                returned_folder_path TEXT NOT NULL,
                returned_folder_exists INTEGER NOT NULL,
                returned_shared INTEGER NOT NULL,
                error TEXT NULL,
                scanned_at TEXT NOT NULL,
                PRIMARY KEY (project_id, item_type)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS folder_entries (
                project_id TEXT NOT NULL,
                item_type TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                name TEXT NOT NULL,
                full_path TEXT NOT NULL,
                location TEXT NOT NULL,
                last_write_ticks BIGINT NOT NULL,
                groups_path TEXT NOT NULL,
                returned_folder TEXT NULL,
                relative_parent TEXT NOT NULL,
                PRIMARY KEY (project_id, item_type, ordinal)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS folder_containers (
                project_id TEXT NOT NULL,
                item_type TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                path TEXT NOT NULL,
                last_write_ticks BIGINT NOT NULL,
                folder_exists INTEGER NOT NULL,
                PRIMARY KEY (project_id, item_type, ordinal)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS accounts (
                provider TEXT NOT NULL PRIMARY KEY,
                user_name TEXT NULL,
                company TEXT NULL,
                signed_in_at TEXT NOT NULL,
                needs_sign_in INTEGER NOT NULL
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS sync_runs (
                id TEXT NOT NULL PRIMARY KEY,
                provider TEXT NOT NULL,
                project_id TEXT NOT NULL,
                run_mode TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT NULL,
                status TEXT NOT NULL,
                item_count INTEGER NOT NULL,
                changed_count INTEGER NOT NULL,
                error TEXT NULL
            );
            """,
            "CREATE INDEX IF NOT EXISTS sync_runs_project ON sync_runs (provider, project_id, started_at);",
            """
            CREATE TABLE IF NOT EXISTS change_log (
                -- Numbers only ever go up (AUTOINCREMENT), even after old rows are removed: the central
                -- server ignores numbers it has already had from this PC. In PostgreSQL: BIGINT GENERATED ALWAYS AS IDENTITY.
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                entity TEXT NOT NULL,
                entity_key TEXT NOT NULL,
                operation TEXT NOT NULL,
                changed_at TEXT NOT NULL,
                payload TEXT NULL,
                synced_at TEXT NULL
            );
            """,
            "CREATE INDEX IF NOT EXISTS change_log_unsynced ON change_log (synced_at, seq);"
        },

        // Version 2: Procore submittal revisions, kept apart from the number.
        new[]
        {
            "ALTER TABLE items ADD COLUMN revision TEXT NULL;"
        },

        // Version 3: Workload Tracker. People's provider ids and emails; the outbox of what scans changed,
        // one row per provider project (merged, so it stays small); the retired company server's change log goes.
        new[]
        {
            "ALTER TABLE item_holders ADD COLUMN provider_user_id TEXT NULL;",
            "ALTER TABLE item_holders ADD COLUMN email TEXT NULL;",
            "ALTER TABLE provider_snapshots ADD COLUMN user_provider_id TEXT NULL;",
            "ALTER TABLE provider_snapshots ADD COLUMN user_email TEXT NULL;",
            "ALTER TABLE accounts ADD COLUMN provider_user_id TEXT NULL;",
            "ALTER TABLE accounts ADD COLUMN email TEXT NULL;",
            """
            CREATE TABLE IF NOT EXISTS wt_outbox (
                provider TEXT NOT NULL,
                project_id TEXT NOT NULL,
                account TEXT NOT NULL,
                full_set INTEGER NOT NULL,
                changed TEXT NOT NULL,
                removed TEXT NOT NULL,
                verified TEXT NOT NULL,
                version BIGINT NOT NULL,
                updated_at TEXT NOT NULL,
                attempts INTEGER NOT NULL,
                next_attempt_at TEXT NULL,
                last_error TEXT NULL,
                PRIMARY KEY (provider, project_id)
            );
            """,
            "DROP INDEX IF EXISTS change_log_unsynced;",
            "DROP TABLE IF EXISTS change_log;",
            "DELETE FROM preferences WHERE pref_key = 'central';",
            "DELETE FROM schema_info WHERE meta_key IN ('central_cursor', 'central_client_id', 'central_server', 'central_send_everything');"
        },

        // Version 4: Workload Tracker item assignments, as read back from the tracker plus changes made
        // here that are still to be sent (pending). Keyed like the tracker: project, item lineage, person.
        new[]
        {
            """
            CREATE TABLE IF NOT EXISTS wt_assignments (
                tracker_project_id INTEGER NOT NULL,
                provider TEXT NOT NULL,
                host TEXT NOT NULL,
                provider_project_id TEXT NOT NULL,
                item_type TEXT NOT NULL,
                number TEXT NOT NULL,
                person_id INTEGER NOT NULL,
                person_name TEXT NOT NULL,
                person_active INTEGER NOT NULL,
                active INTEGER NOT NULL,
                assigned_at TEXT NOT NULL,
                assigned_by_name TEXT NULL,
                completed_at TEXT NULL,
                pending INTEGER NOT NULL,
                PRIMARY KEY (tracker_project_id, provider, provider_project_id, item_type, number, person_id)
            );
            """
        }
    };

    public static void Migrate(SqlitePersistence database)
    {
        database.Execute("CREATE TABLE IF NOT EXISTS schema_info (meta_key TEXT NOT NULL PRIMARY KEY, meta_value TEXT NOT NULL);");
        var current = int.TryParse(database.GetMeta("schema_version"), out var version) ? version : 0;
        for (var index = current; index < Versions.Length; index++)
        {
            using var transaction = database.BeginTransaction();
            foreach (var statement in Versions[index])
            {
                database.Execute(statement, transaction);
            }

            database.Execute(
                "INSERT INTO schema_info (meta_key, meta_value) VALUES ('schema_version', $version) " +
                "ON CONFLICT (meta_key) DO UPDATE SET meta_value = excluded.meta_value;",
                transaction,
                ("$version", (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            transaction.Commit();
        }
    }
}
