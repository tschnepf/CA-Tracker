# CA Tracker company server

Lets everyone's CA Tracker share what they read from Helix and Procore and the discipline choices for
each project, and keeps one company-wide database of every RFI and Submittal and whose court it is in.
Optional: without it, each CA Tracker works on its own.

| File | |
|---|---|
| [PROTOCOL.md](PROTOCOL.md) | How the app and the server talk, and what each side does |
| [schema.sql](schema.sql) | The PostgreSQL tables (plain SQL; notes for other databases at the top) |
| [schema-sqlite.sql](schema-sqlite.sql) | The same tables for SQLite (a pilot) |
| [ca_tracker_server.py](ca_tracker_server.py) | The server: Python 3.8+, nothing else to install for SQLite |
| [test_server.py](test_server.py) | Its tests, against SQLite and PostgreSQL |

## Try it (SQLite, a few minutes)

```
python ca_tracker_server.py init-db   --db sqlite:ca-tracker.db
python ca_tracker_server.py add-token --db sqlite:ca-tracker.db --person "Pat Engineer"
python ca_tracker_server.py serve     --db sqlite:ca-tracker.db --port 8080
```

`add-token` prints the access token once. In CA Tracker: Settings → Connections → Company server →
address `http://<that PC>:8080` and the token → Save and connect.

## Run it for the company (PostgreSQL)

1. Create a database, or use the workload tracker's (all tables start with `ca_`):
   `psql -d workload -f schema.sql` (the server also creates them when it starts).
2. `pip install "psycopg[binary]"`
3. Set the database address for the service: `CA_TRACKER_DB=postgresql://ca_tracker:<password>@dbhost/workload`
4. `python ca_tracker_server.py serve --host 0.0.0.0 --port 8080`, as a service:
   - Windows: NSSM or a scheduled task "At startup" running that command;
   - Linux: a systemd unit with `Restart=always`.
5. HTTPS: put it behind the company's reverse proxy (IIS with URL Rewrite/ARR, or nginx) with the
   company certificate, or pass `--cert server.pem --key server-key.pem`.
6. Give each person a token: `add-token --person "Name" [--ref <workload tracker id>]`. `list-tokens`
   shows them; `revoke-token --id N` stops one. A `--read-only` token can read everything but not send
   (for dashboards).
7. Set the address for everyone in `ca-tracker-company.json` (see windows-helper/company):
   `"centralServer": "https://ca.example.com"`. People then only enter their token.

Back up the database like any other. If it is restored from a backup, the apps notice (they have read
further than the server has) and send everything they have again.

## Reading the data

```sql
-- Open RFIs and Submittals and who has them, oldest first.
SELECT project_name, item_type, number, title, holder, holder_since, due_at
FROM ca_items_in_court
ORDER BY holder_since;

-- How many open items each person has.
SELECT holder, COUNT(*) AS open_items, MIN(due_at) AS next_due
FROM ca_items_in_court
GROUP BY holder
ORDER BY open_items DESC;

-- Overdue items.
SELECT project_name, number, title, holder, due_at
FROM ca_items_in_court
WHERE due_at < CURRENT_TIMESTAMP
ORDER BY due_at;

-- Who tracks which project, and when each was last read.
SELECT p.user_name, p.name, p.provider, MAX(i.read_at) AS last_read
FROM ca_tracked_projects p
LEFT JOIN ca_items i ON i.provider = p.provider AND i.provider_project_id = p.provider_project_id
WHERE p.deleted_at IS NULL
GROUP BY p.user_name, p.name, p.provider;
```

Read these tables, but don't change them: the server keeps them in step with `ca_changes`.

## Tests

```
python -m unittest test_server -v
CA_TRACKER_TEST_PSQL="-h localhost -U postgres -d ca_tracker_test" python -m unittest test_server -v
```

The second line also runs every test against PostgreSQL (an empty database you can drop; it uses
`psql`, so no Python driver is needed).
