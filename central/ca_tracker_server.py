#!/usr/bin/env python3
"""
CA Tracker central server (reference implementation of central/PROTOCOL.md).

Shares projects, discipline choices and Helix/Procore items between everyone's CA Tracker, and keeps
readable tables (ca_items, ca_item_holders, ca_tracked_projects, ...) up to date for reports and other
apps such as the company workload tracker.

Needs only Python 3.8+. Databases:
  * PostgreSQL:  --db postgresql://user:password@host/database   (pip install "psycopg[binary]")
  * SQLite:      --db sqlite:ca-tracker.db                       (a pilot or a small team)

  python ca_tracker_server.py init-db   --db URL
  python ca_tracker_server.py add-token --db URL --person "Pat Engineer" [--ref ID] [--read-only]
  python ca_tracker_server.py list-tokens  --db URL
  python ca_tracker_server.py revoke-token --db URL --id N
  python ca_tracker_server.py serve     --db URL [--host 0.0.0.0] [--port 8080] [--cert F --key F]

The database address may also come from the CA_TRACKER_DB environment variable. Serve over HTTPS:
with --cert/--key, or behind the company's reverse proxy (IIS, nginx) that terminates TLS.
"""

import argparse
import datetime as dt
import hashlib
import json
import logging
import os
import re
import secrets
import sqlite3
import ssl
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

PROTOCOL = 1
API = "/api/ca-tracker/v1"
MAX_BODY = 32 * 1024 * 1024
MAX_CHANGES = 5000
DEFAULT_LIMIT = 500
MAX_LIMIT = 1000
HERE = os.path.dirname(os.path.abspath(__file__))

log = logging.getLogger("ca-tracker")


class BadRequest(Exception):
    """The request can't be used (answered 400 with the message)."""


class Conflict(Exception):
    """The request doesn't match the server's state (answered 409)."""

    def __init__(self, message, **extra):
        super().__init__(message)
        self.extra = extra


# ---- Values ------------------------------------------------------------------------------------

_TIME = re.compile(
    r"^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?)?\s*(Z|[+-]\d{2}(?::?\d{2})?)?$",
    re.IGNORECASE)


def utc_now():
    return dt.datetime.now(dt.timezone.utc)


def parse_time(value):
    """ISO 8601 text (any precision, Z or +hh[:mm]) or a datetime -> aware UTC datetime; None if it isn't one."""
    if isinstance(value, dt.datetime):
        return (value if value.tzinfo else value.replace(tzinfo=dt.timezone.utc)).astimezone(dt.timezone.utc)
    if not isinstance(value, str):
        return None
    match = _TIME.match(value.strip())
    if not match:
        return None
    year, month, day, hour, minute, second, fraction, zone = match.groups()
    micro = int((fraction or "0")[:6].ljust(6, "0"))
    offset = dt.timedelta(0)
    if zone and zone.upper() != "Z":
        sign = -1 if zone[0] == "-" else 1
        digits = zone[1:].replace(":", "")
        offset = sign * dt.timedelta(hours=int(digits[:2]), minutes=int(digits[2:4] or 0))
    try:
        local = dt.datetime(int(year), int(month), int(day), int(hour or 0), int(minute or 0), int(second or 0), micro,
                            tzinfo=dt.timezone(offset))
    except ValueError:
        return None
    return local.astimezone(dt.timezone.utc)


def iso(value):
    """A time from the database (datetime or text) -> ISO 8601 in UTC, as clients read it."""
    parsed = parse_time(value)
    return parsed.isoformat().replace("+00:00", "Z") if parsed else None


def truthy(value):
    if isinstance(value, str):
        return value.strip().lower() in ("1", "t", "true", "yes")
    return bool(value)


def canonical(payload):
    return json.dumps(payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def sha256(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def text(value, limit=None):
    result = "" if value is None else str(value)
    return result[:limit] if limit else result


def fits(value, limit):
    return isinstance(value, str) and 0 < len(value) <= limit


# ---- Database ----------------------------------------------------------------------------------

class SqliteDriver:
    kind = "sqlite"

    def __init__(self, path):
        self.conn = sqlite3.connect(path, isolation_level=None, check_same_thread=False, timeout=10)
        self.conn.execute("PRAGMA foreign_keys = ON")
        self.conn.execute("PRAGMA journal_mode = WAL")

    @staticmethod
    def _adapt(value):
        if isinstance(value, dt.datetime):
            return iso(value)
        if isinstance(value, bool):
            return 1 if value else 0
        return value

    def begin(self):
        self.conn.execute("BEGIN IMMEDIATE")

    def commit(self):
        self.conn.execute("COMMIT")

    def rollback(self):
        if self.conn.in_transaction:
            self.conn.execute("ROLLBACK")

    def query(self, sql, params):
        cursor = self.conn.execute(sql, [self._adapt(value) for value in params])
        return cursor.fetchall() if cursor.description else []

    def script(self, sql):
        self.conn.executescript(sql)

    def close(self):
        self.conn.close()


class PostgresDriver:
    kind = "postgresql"

    def __init__(self, url):
        try:
            import psycopg  # noqa: F401  (psycopg 3)
        except ImportError:
            raise SystemExit('PostgreSQL needs psycopg: pip install "psycopg[binary]"')
        self.url = url
        self.conn = None
        self._connect()

    def _connect(self):
        import psycopg
        self.conn = psycopg.connect(self.url)

    def begin(self):
        if self.conn is None or self.conn.closed:
            self._connect()

    def commit(self):
        self.conn.commit()

    def rollback(self):
        if self.conn is not None and not self.conn.closed:
            self.conn.rollback()

    def query(self, sql, params):
        cursor = self.conn.execute(sql.replace("?", "%s"), list(params))
        return cursor.fetchall() if cursor.description else []

    def script(self, sql):
        self.begin()
        self.conn.execute(sql)
        self.conn.commit()

    def close(self):
        if self.conn is not None:
            self.conn.close()


class Database:
    """One connection, used by one request at a time (enough for a company polling every couple of minutes)."""

    def __init__(self, driver):
        self.driver = driver
        self.lock = threading.RLock()

    @staticmethod
    def open(url):
        if not url:
            raise SystemExit("Give the database with --db or the CA_TRACKER_DB environment variable.")
        if url.startswith(("postgres://", "postgresql://")):
            return Database(PostgresDriver(url))
        if url.startswith("sqlite:"):
            path = url[len("sqlite:"):]
            if path.startswith("///"):
                path = path[2:]
            return Database(SqliteDriver(path or "ca-tracker.db"))
        raise SystemExit(f"Unknown database address {url!r}: use postgresql://... or sqlite:file.db")

    @property
    def kind(self):
        return self.driver.kind

    def create_schema(self):
        name = "schema.sql" if self.kind == "postgresql" else "schema-sqlite.sql"
        with open(os.path.join(HERE, name), encoding="utf-8") as file:
            sql = file.read()
        with self.lock:
            self.driver.script(sql)

    def run(self, work):
        """Runs work(tx) in one transaction: all of it is saved, or none."""
        with self.lock:
            self.driver.begin()
            try:
                result = work(Transaction(self.driver))
                self.driver.commit()
                return result
            except BaseException:
                try:
                    self.driver.rollback()
                except Exception:  # the connection may be gone; the next request reconnects
                    log.exception("Rolling back failed")
                raise

    def close(self):
        self.driver.close()


class Transaction:
    def __init__(self, driver):
        self.driver = driver
        self._savepoints = 0

    def all(self, sql, *params):
        return self.driver.query(sql, params)

    def one(self, sql, *params):
        rows = self.driver.query(sql, params)
        return rows[0] if rows else None

    def run(self, sql, *params):
        self.driver.query(sql, params)

    def attempt(self, work):
        """Runs work() in a savepoint: if it fails, only its own changes are undone. Returns (ok, error)."""
        self._savepoints += 1
        name = f"change_{self._savepoints}"
        self.run(f"SAVEPOINT {name}")
        try:
            work()
        except Exception as error:  # noqa: BLE001 - reported to the client as skipped
            self.run(f"ROLLBACK TO SAVEPOINT {name}")
            self.run(f"RELEASE SAVEPOINT {name}")
            return False, error
        self.run(f"RELEASE SAVEPOINT {name}")
        return True, None


# ---- The service -------------------------------------------------------------------------------

class Service:
    def __init__(self, db):
        self.db = db

    # Tokens

    def add_token(self, person, ref=None, can_write=True):
        token = "cat_" + secrets.token_urlsafe(32)
        now = utc_now()

        def work(tx):
            tx.run("INSERT INTO ca_tokens (token_hash, person, person_ref, can_write, created_at) VALUES (?, ?, ?, ?, ?)",
                   sha256(token), text(person, 200), ref and text(ref, 200), bool(can_write), now)
            return tx.one("SELECT token_id FROM ca_tokens WHERE token_hash = ?", sha256(token))[0]

        return int(self.db.run(work)), token

    def list_tokens(self):
        return self.db.run(lambda tx: tx.all(
            "SELECT token_id, person, person_ref, can_write, created_at, revoked_at FROM ca_tokens ORDER BY token_id"))

    def revoke_token(self, token_id):
        def work(tx):
            tx.run("UPDATE ca_tokens SET revoked_at = ? WHERE token_id = ? AND revoked_at IS NULL", utc_now(), int(token_id))
            return tx.one("SELECT revoked_at FROM ca_tokens WHERE token_id = ?", int(token_id))

        row = self.db.run(work)
        return row is not None and row[0] is not None

    def authenticate(self, token):
        """The token's {id, person, can_write}, or None when it is unknown or revoked."""
        if not token:
            return None
        row = self.db.run(lambda tx: tx.one(
            "SELECT token_id, person, can_write FROM ca_tokens WHERE token_hash = ? AND revoked_at IS NULL", sha256(token)))
        return None if row is None else {"id": int(row[0]), "person": row[1], "can_write": truthy(row[2])}

    # Changes in

    def accept_changes(self, token, body):
        if not isinstance(body, dict):
            raise BadRequest("The body must be a JSON object.")
        client = body.get("client")
        if not isinstance(client, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", client):
            raise BadRequest("client must be this PC's id (letters, digits, - and _; up to 64).")
        changes = body.get("changes")
        if not isinstance(changes, list):
            raise BadRequest("changes must be a list.")
        if len(changes) > MAX_CHANGES:
            raise BadRequest(f"Send at most {MAX_CHANGES} changes at a time.")
        user = text(body.get("user"), 200) or token["person"]
        app = text(body.get("app"), 40)
        now = utc_now()

        def work(tx):
            row = tx.one("SELECT last_client_seq FROM ca_clients WHERE client_id = ?", client)
            if row is None:
                tx.run("INSERT INTO ca_clients (client_id, token_id, user_name, app_version, last_client_seq, first_seen_at, last_seen_at) "
                       "VALUES (?, ?, ?, ?, 0, ?, ?)", client, token["id"], user, app, now, now)
                last_client_seq = 0
            else:
                last_client_seq = int(row[0])
                tx.run("UPDATE ca_clients SET token_id = ?, user_name = ?, app_version = ?, last_seen_at = ? WHERE client_id = ?",
                       token["id"], user, app, now, client)

            stored = 0
            unchanged = 0
            skipped = []
            ordered = sorted((change for change in changes if isinstance(change, dict) and isinstance(change.get("seq"), int)),
                             key=lambda change: change["seq"])
            skipped.extend({"seq": None, "reason": "not a change with a whole-number seq"}
                           for change in changes if not (isinstance(change, dict) and isinstance(change.get("seq"), int)))
            for change in ordered:
                seq = change["seq"]
                if seq <= last_client_seq:
                    continue  # already accepted (the client is sending again after a lost answer)
                last_client_seq = seq
                problem = self._invalid(change)
                if problem:
                    skipped.append({"seq": seq, "reason": problem})
                    continue
                outcome = {}
                ok, error = tx.attempt(lambda: outcome.update(stored=self._store(tx, client, user, change, now)))
                if not ok:
                    log.warning("Change %s from %s couldn't be stored: %s", seq, client, error)
                    skipped.append({"seq": seq, "reason": "the server couldn't store it"})
                elif outcome.get("stored"):
                    stored += 1
                else:
                    unchanged += 1

            tx.run("UPDATE ca_clients SET last_client_seq = ? WHERE client_id = ?", last_client_seq, client)
            newest = tx.one("SELECT MAX(seq) FROM ca_changes")[0]
            return {"accepted": len(changes), "stored": stored, "unchanged": unchanged, "skipped": skipped,
                    "last": int(newest or 0)}

        return self.db.run(work)

    @staticmethod
    def _invalid(change):
        entity, key, operation = change.get("entity"), change.get("key"), change.get("operation")
        if entity not in ("project", "item"):
            return "entity must be project or item"
        if operation not in ("insert", "update", "delete"):
            return "operation must be insert, update or delete"
        if entity == "project" and not fits(key, 64):
            return "a project's key is its id (up to 64 characters)"
        if entity == "item":
            parts = key.split("|", 3) if isinstance(key, str) else []
            if len(parts) != 4 or not (fits(parts[0], 20) and fits(parts[1], 200) and fits(parts[2], 20) and fits(parts[3], 200)):
                return "an item's key is provider|projectId|itemType|itemId"
        if operation != "delete" and not isinstance(change.get("payload"), dict):
            return "insert and update need the project or item as payload"
        return None

    def _store(self, tx, client, user, change, now):
        """Adds one change unless it repeats the current version. True when stored."""
        entity, key, operation = change["entity"], change["key"], change["operation"]
        deleted = operation == "delete"
        changed_at = parse_time(change.get("changedAt")) or now
        payload_text = None if deleted else canonical(change["payload"])
        payload_hash = None if deleted else sha256(payload_text)

        current = tx.one("SELECT payload_hash, deleted, changed_at FROM ca_current WHERE entity = ? AND entity_key = ?", entity, key)
        if deleted and (current is None or truthy(current[1])):
            return False
        if not deleted and current is not None and not truthy(current[1]) and current[0] == payload_hash:
            return False
        if entity == "item" and current is not None and (parse_time(current[2]) or changed_at) > changed_at:
            return False  # read before the version already here (e.g. a PC sending everything it has again)

        tx.run("INSERT INTO ca_changes (client_id, client_seq, user_name, entity, entity_key, operation, changed_at, received_at, "
               "payload, payload_hash) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
               client, change["seq"], user, entity, key, operation, changed_at, now, payload_text, payload_hash)
        seq = int(tx.one("SELECT seq FROM ca_changes WHERE client_id = ? AND client_seq = ?", client, change["seq"])[0])
        tx.run("INSERT INTO ca_current (entity, entity_key, payload, payload_hash, deleted, last_seq, changed_at, updated_at, updated_by) "
               "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?) ON CONFLICT (entity, entity_key) DO UPDATE SET payload = excluded.payload, "
               "payload_hash = excluded.payload_hash, deleted = excluded.deleted, last_seq = excluded.last_seq, "
               "changed_at = excluded.changed_at, updated_at = excluded.updated_at, updated_by = excluded.updated_by",
               entity, key, payload_text, payload_hash, deleted, seq, changed_at, now, user)

        if entity == "project":
            self._project(tx, key, None if deleted else change["payload"], client, user, now)
        else:
            self._item(tx, key, None if deleted else change["payload"], changed_at, user, now)
        return True

    # Readable tables

    @staticmethod
    def _project(tx, key, project, client, user, now):
        if project is None:
            tx.run("UPDATE ca_tracked_projects SET deleted_at = ?, updated_at = ? WHERE project_key = ?", now, now, key)
            tx.run("DELETE FROM ca_discipline_choices WHERE project_key = ?", key)
            return

        connection = project.get("connection") if isinstance(project.get("connection"), dict) else {}
        provider = text(connection.get("provider")).lower() or None
        provider_project = text(connection.get("projectId")) or None
        if provider and (len(provider) > 20 or not provider_project or len(provider_project) > 200):
            provider = provider_project = None
        scope = project.get("scope") if isinstance(project.get("scope"), dict) else {}
        tx.run("INSERT INTO ca_tracked_projects (project_key, client_id, user_name, name, ca_folder, provider, provider_project_id, "
               "provider_project_name, company_name, phase, updated_at, deleted_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL) "
               "ON CONFLICT (project_key) DO UPDATE SET client_id = excluded.client_id, user_name = excluded.user_name, "
               "name = excluded.name, ca_folder = excluded.ca_folder, provider = excluded.provider, "
               "provider_project_id = excluded.provider_project_id, provider_project_name = excluded.provider_project_name, "
               "company_name = excluded.company_name, phase = excluded.phase, updated_at = excluded.updated_at, deleted_at = NULL",
               key, client, user, text(project.get("name")), text(project.get("caFolder")) or None, provider, provider_project,
               text(connection.get("projectName")) or None, text(project.get("companyName")) or None,
               text(scope.get("phase"), 40) or None, now)

        tx.run("DELETE FROM ca_discipline_choices WHERE project_key = ?", key)
        disciplines = project.get("disciplines") if isinstance(project.get("disciplines"), dict) else {}
        for choice_key, code in disciplines.items():
            item_type, kind, target = split_discipline_key(choice_key)
            if not (fits(item_type, 20) and fits(target, 500) and fits(code, 100)):
                continue
            tx.run("INSERT INTO ca_discipline_choices (project_key, item_type, target_kind, target, discipline) VALUES (?, ?, ?, ?, ?) "
                   "ON CONFLICT (project_key, item_type, target_kind, target) DO UPDATE SET discipline = excluded.discipline",
                   key, item_type, kind, target, code)

        if provider and provider_project:
            name = text(connection.get("projectName")) or text(project.get("name")) or provider_project
            tx.run("INSERT INTO ca_provider_projects (provider, provider_project_id, name, updated_at) VALUES (?, ?, ?, ?) "
                   "ON CONFLICT (provider, provider_project_id) DO UPDATE SET name = excluded.name, updated_at = excluded.updated_at",
                   provider, provider_project, name, now)

    @staticmethod
    def _item(tx, key, item, read_at, user, now):
        provider, project_id, item_type, item_id = key.split("|", 3)
        provider = provider.lower()
        item_type = item_type.lower()
        ids = (provider, project_id, item_type, item_id)
        tx.run("DELETE FROM ca_item_holders WHERE provider = ? AND provider_project_id = ? AND item_type = ? AND item_id = ?", *ids)
        if item is None:
            tx.run("UPDATE ca_items SET deleted_at = ? WHERE provider = ? AND provider_project_id = ? AND item_type = ? AND item_id = ?",
                   now, *ids)
            return

        rank = item.get("statusRank")
        attachments = item.get("attachmentCount")
        tx.run("INSERT INTO ca_items (provider, provider_project_id, item_type, item_id, number, title, status, status_rank, closed, "
               "responsible, contract, attachment_count, created_at, modified_at, due_at, source_url, read_at, read_by, deleted_at) "
               "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, NULL) "
               "ON CONFLICT (provider, provider_project_id, item_type, item_id) DO UPDATE SET number = excluded.number, "
               "title = excluded.title, status = excluded.status, status_rank = excluded.status_rank, closed = excluded.closed, "
               "responsible = excluded.responsible, contract = excluded.contract, attachment_count = excluded.attachment_count, "
               "created_at = excluded.created_at, modified_at = excluded.modified_at, due_at = excluded.due_at, "
               "source_url = excluded.source_url, read_at = excluded.read_at, read_by = excluded.read_by, deleted_at = NULL",
               *ids, text(item.get("number")), text(item.get("title")), text(item.get("status")),
               rank if isinstance(rank, int) and not isinstance(rank, bool) else None, truthy(item.get("closed")),
               text(item.get("responsible")), text(item.get("contract")),
               attachments if isinstance(attachments, int) and not isinstance(attachments, bool) else 0,
               parse_time(item.get("createdAt")), parse_time(item.get("modifiedAt")), parse_time(item.get("dueAt")),
               text(item.get("sourceUrl")) or None, read_at, user)

        holders = item.get("holders") if isinstance(item.get("holders"), list) else []
        for ordinal, holder in enumerate(h for h in holders if isinstance(h, dict)):
            tx.run("INSERT INTO ca_item_holders (provider, provider_project_id, item_type, item_id, ordinal, name, company, state, since) "
                   "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                   *ids, ordinal, text(holder.get("name")), text(holder.get("company")) or None, text(holder.get("state"), 20),
                   parse_time(holder.get("since")))

    # Changes out

    def read_changes(self, after, limit):
        def work(tx):
            newest = int(tx.one("SELECT MAX(seq) FROM ca_changes")[0] or 0)
            if after > newest:
                raise Conflict("This server has fewer changes than you have read: it was reset or restored. Start again from 0.",
                               last=newest)
            rows = tx.all("SELECT seq, client_id, user_name, entity, entity_key, operation, changed_at, payload FROM ca_changes "
                          "WHERE seq > ? ORDER BY seq LIMIT ?", after, limit)
            changes = [{
                "seq": int(row[0]),
                "client": row[1],
                "user": row[2],
                "entity": row[3],
                "key": row[4],
                "operation": row[5],
                "changedAt": iso(row[6]),
                "payload": json.loads(row[7]) if row[7] else None,
            } for row in rows]
            return {"changes": changes, "last": changes[-1]["seq"] if changes else after, "newest": newest}

        return self.db.run(work)


def split_discipline_key(key):
    """"rfi|p000210 - doors" -> (rfi, folder, p000210 - doors); "item|rfi|abc" -> (rfi, item, abc). Same as the app."""
    if not isinstance(key, str):
        return "", "", ""
    if key.lower().startswith("item|"):
        rest = key[len("item|"):]
        bar = rest.find("|")
        return ("", "", "") if bar <= 0 else (rest[:bar], "item", rest[bar + 1:])
    bar = key.find("|")
    return ("", "", "") if bar <= 0 else (key[:bar], "folder", key[bar + 1:])


# ---- HTTP --------------------------------------------------------------------------------------

class Handler(BaseHTTPRequestHandler):
    server_version = "CATrackerServer/1"
    protocol_version = "HTTP/1.1"
    service = None  # set by make_server

    def do_GET(self):  # noqa: N802
        self._route("GET")

    def do_POST(self):  # noqa: N802
        self._route("POST")

    def log_message(self, format, *args):  # noqa: A002 - BaseHTTPRequestHandler's name
        log.info("%s %s", self.address_string(), format % args)

    def _route(self, method):
        try:
            url = urlparse(self.path)
            path = url.path.rstrip("/")
            if method == "GET" and path == f"{API}/health":
                return self._reply(200, {"ok": True, "protocol": PROTOCOL})
            if not path.startswith(API + "/"):
                return self._reply(404, {"error": "Not a CA Tracker address."})

            header = self.headers.get("Authorization", "")
            token = self.service.authenticate(header[7:].strip() if header.lower().startswith("bearer ") else "")
            if token is None:
                return self._reply(401, {"error": "Missing, unknown or revoked access token."})

            name = path[len(API) + 1:]
            if method == "GET" and name == "hello":
                return self._reply(200, {"name": "CA Tracker server", "protocol": PROTOCOL, "user": token["person"],
                                         "canWrite": token["can_write"]})
            if method == "GET" and name == "changes":
                query = parse_qs(url.query)
                after = _whole(query.get("after", ["0"])[0], "after")
                limit = min(max(_whole(query.get("limit", [str(DEFAULT_LIMIT)])[0], "limit"), 1), MAX_LIMIT)
                return self._reply(200, self.service.read_changes(max(after, 0), limit))
            if method == "POST" and name == "changes":
                if not token["can_write"]:
                    return self._reply(403, {"error": "This access token can read but not send changes."})
                return self._reply(200, self.service.accept_changes(token, self._json()))
            return self._reply(404, {"error": "Not a CA Tracker address."})
        except BadRequest as error:
            return self._reply(400, {"error": str(error)})
        except Conflict as error:
            return self._reply(409, {"error": str(error), **error.extra})
        except Exception:  # noqa: BLE001
            log.exception("Request failed: %s %s", method, self.path)
            return self._reply(500, {"error": "The server had a problem; see its log."})

    def _json(self):
        length = int(self.headers.get("Content-Length") or 0)
        if length <= 0:
            raise BadRequest("The request has no body.")
        if length > MAX_BODY:
            raise BadRequest("The request is too large; send fewer changes at a time.")
        try:
            return json.loads(self.rfile.read(length).decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            raise BadRequest(f"The body isn't JSON: {error}")

    def _reply(self, status, body):
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)


def _whole(value, name):
    try:
        return int(value)
    except (TypeError, ValueError):
        raise BadRequest(f"{name} must be a whole number.")


def make_server(service, host="127.0.0.1", port=8080, cert=None, key=None):
    handler = type("BoundHandler", (Handler,), {"service": service})
    server = ThreadingHTTPServer((host, port), handler)
    server.daemon_threads = True
    if cert:
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(cert, key)
        server.socket = context.wrap_socket(server.socket, server_side=True)
    return server


# ---- Command line ------------------------------------------------------------------------------

def main(argv=None):
    parser = argparse.ArgumentParser(description="CA Tracker central server")
    parser.add_argument("command", choices=["init-db", "add-token", "list-tokens", "revoke-token", "serve"])
    parser.add_argument("--db", default=os.environ.get("CA_TRACKER_DB"), help="postgresql://... or sqlite:file.db")
    parser.add_argument("--person", help="add-token: who the token is for")
    parser.add_argument("--ref", help="add-token: their id in another system (e.g. the workload tracker)")
    parser.add_argument("--read-only", action="store_true", help="add-token: may read but not send changes")
    parser.add_argument("--id", type=int, help="revoke-token: the token's number (list-tokens)")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--cert", help="serve: TLS certificate (PEM)")
    parser.add_argument("--key", help="serve: TLS private key (PEM)")
    args = parser.parse_args(argv)
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

    db = Database.open(args.db)
    db.create_schema()
    service = Service(db)
    if args.command == "init-db":
        print(f"The {db.kind} database is ready.")
    elif args.command == "add-token":
        if not args.person:
            parser.error("add-token needs --person")
        token_id, token = service.add_token(args.person, args.ref, not args.read_only)
        print(f"Token {token_id} for {args.person}{' (read only)' if args.read_only else ''}:\n\n  {token}\n\n"
              "Give it to them for CA Tracker > Settings > Connections > Company server. It isn't shown again.")
    elif args.command == "list-tokens":
        for token_id, person, ref, can_write, created, revoked in service.list_tokens():
            state = f"revoked {iso(revoked)}" if revoked else ("read and send" if truthy(can_write) else "read only")
            print(f"{int(token_id):>4}  {person}{f' ({ref})' if ref else ''}  created {iso(created)}  {state}")
    elif args.command == "revoke-token":
        if args.id is None:
            parser.error("revoke-token needs --id")
        print("Revoked." if service.revoke_token(args.id) else "No such token.")
    else:
        server = make_server(service, args.host, args.port, args.cert, args.key)
        scheme = "https" if args.cert else "http"
        log.info("CA Tracker server on %s://%s:%s%s (database: %s)", scheme, args.host, args.port, API, db.kind)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass
        finally:
            server.server_close()
    db.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
