-- CA Tracker central database: SQLite (for a pilot or a small team; use schema.sql with PostgreSQL
-- for the company). The same tables as schema.sql, in SQLite's types: times are ISO 8601 text in
-- UTC, flags are 0/1.

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS ca_schema (
    version     INTEGER NOT NULL PRIMARY KEY,
    applied_at  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ca_tokens (
    token_id     INTEGER PRIMARY KEY AUTOINCREMENT,
    token_hash   TEXT NOT NULL UNIQUE,
    person       TEXT NOT NULL,
    person_ref   TEXT,
    can_write    INTEGER NOT NULL DEFAULT 1,
    created_at   TEXT NOT NULL,
    revoked_at   TEXT
);

CREATE TABLE IF NOT EXISTS ca_clients (
    client_id        TEXT PRIMARY KEY,
    token_id         INTEGER REFERENCES ca_tokens (token_id),
    user_name        TEXT,
    app_version      TEXT,
    last_client_seq  INTEGER NOT NULL DEFAULT 0,
    first_seen_at    TEXT NOT NULL,
    last_seen_at     TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ca_changes (
    seq           INTEGER PRIMARY KEY AUTOINCREMENT,
    client_id     TEXT NOT NULL REFERENCES ca_clients (client_id),
    client_seq    INTEGER NOT NULL,
    user_name     TEXT,
    entity        TEXT NOT NULL CHECK (entity IN ('project', 'item')),
    entity_key    TEXT NOT NULL,
    operation     TEXT NOT NULL CHECK (operation IN ('insert', 'update', 'delete')),
    changed_at    TEXT NOT NULL,
    received_at   TEXT NOT NULL,
    payload       TEXT,
    payload_hash  TEXT,
    UNIQUE (client_id, client_seq)
);
CREATE INDEX IF NOT EXISTS ca_changes_entity_key ON ca_changes (entity, entity_key);

CREATE TABLE IF NOT EXISTS ca_current (
    entity        TEXT NOT NULL,
    entity_key    TEXT NOT NULL,
    payload       TEXT,
    payload_hash  TEXT,
    deleted       INTEGER NOT NULL,
    last_seq      INTEGER NOT NULL,
    changed_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL,
    updated_by    TEXT,
    PRIMARY KEY (entity, entity_key)
);

CREATE TABLE IF NOT EXISTS ca_tracked_projects (
    project_key            TEXT PRIMARY KEY,
    client_id              TEXT,
    user_name              TEXT,
    name                   TEXT NOT NULL,
    ca_folder              TEXT,
    provider               TEXT,
    provider_project_id    TEXT,
    provider_project_name  TEXT,
    company_name           TEXT,
    phase                  TEXT,
    updated_at             TEXT NOT NULL,
    deleted_at             TEXT
);
CREATE INDEX IF NOT EXISTS ca_tracked_projects_provider ON ca_tracked_projects (provider, provider_project_id);

CREATE TABLE IF NOT EXISTS ca_discipline_choices (
    project_key  TEXT NOT NULL REFERENCES ca_tracked_projects (project_key) ON DELETE CASCADE,
    item_type    TEXT NOT NULL,
    target_kind  TEXT NOT NULL,
    target       TEXT NOT NULL,
    discipline   TEXT NOT NULL,
    PRIMARY KEY (project_key, item_type, target_kind, target)
);

CREATE TABLE IF NOT EXISTS ca_provider_projects (
    provider             TEXT NOT NULL,
    provider_project_id  TEXT NOT NULL,
    name                 TEXT NOT NULL,
    updated_at           TEXT NOT NULL,
    PRIMARY KEY (provider, provider_project_id)
);

CREATE TABLE IF NOT EXISTS ca_items (
    provider             TEXT NOT NULL,
    provider_project_id  TEXT NOT NULL,
    item_type            TEXT NOT NULL,
    item_id              TEXT NOT NULL,
    number               TEXT NOT NULL,
    title                TEXT NOT NULL,
    status               TEXT NOT NULL,
    status_rank          INTEGER,
    closed               INTEGER NOT NULL,
    responsible          TEXT NOT NULL,
    contract             TEXT NOT NULL,
    attachment_count     INTEGER NOT NULL,
    created_at           TEXT,
    modified_at          TEXT,
    due_at               TEXT,
    source_url           TEXT,
    read_at              TEXT NOT NULL,
    read_by              TEXT,
    deleted_at           TEXT,
    PRIMARY KEY (provider, provider_project_id, item_type, item_id)
);

CREATE TABLE IF NOT EXISTS ca_item_holders (
    provider             TEXT NOT NULL,
    provider_project_id  TEXT NOT NULL,
    item_type            TEXT NOT NULL,
    item_id              TEXT NOT NULL,
    ordinal              INTEGER NOT NULL,
    name                 TEXT NOT NULL,
    company              TEXT,
    state                TEXT NOT NULL,
    since                TEXT,
    PRIMARY KEY (provider, provider_project_id, item_type, item_id, ordinal),
    FOREIGN KEY (provider, provider_project_id, item_type, item_id)
        REFERENCES ca_items (provider, provider_project_id, item_type, item_id) ON DELETE CASCADE
);

CREATE VIEW IF NOT EXISTS ca_items_in_court AS
SELECT i.provider,
       i.provider_project_id,
       p.name AS project_name,
       i.item_type,
       i.item_id,
       i.number,
       i.title,
       i.status,
       i.due_at,
       h.name AS holder,
       h.company AS holder_company,
       h.since AS holder_since,
       i.read_at
FROM ca_items i
JOIN ca_item_holders h
  ON h.provider = i.provider AND h.provider_project_id = i.provider_project_id
 AND h.item_type = i.item_type AND h.item_id = i.item_id AND h.state = 'current'
LEFT JOIN ca_provider_projects p
  ON p.provider = i.provider AND p.provider_project_id = i.provider_project_id
WHERE i.deleted_at IS NULL AND i.closed = 0;

INSERT INTO ca_schema (version, applied_at)
SELECT 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
WHERE NOT EXISTS (SELECT 1 FROM ca_schema WHERE version = 1);
