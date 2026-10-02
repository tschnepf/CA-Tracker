"""
Tests for the CA Tracker reference server, run against SQLite and (when available) PostgreSQL.

  python -m unittest test_server -v

PostgreSQL: set CA_TRACKER_TEST_PSQL to psql's connection arguments for an empty test database, e.g.
  CA_TRACKER_TEST_PSQL="-h localhost -U postgres -d ca_tracker_test"
The tests then reach PostgreSQL through the psql command (so no Python driver is needed to run them):
the server's own SQL runs unchanged, with values written into it as SQL literals.
"""

import csv
import datetime as dt
import io
import http.client
import json
import os
import shlex
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ca_tracker_server as server  # noqa: E402


class PsqlDriver:
    """Test-only PostgreSQL driver: one psql process, statements sent on its input."""

    kind = "postgresql"
    END = "<<ca-end>>"
    NULL = "<<ca-null>>"

    def __init__(self, args):
        self.proc = subprocess.Popen(["psql", "-X", "-q", "-t", "--csv", "-v", "ON_ERROR_STOP=0", *args],
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                     text=True, encoding="utf-8")
        self._send(f"\\pset null '{self.NULL}'", raw=True)
        self._send("SET client_min_messages = warning")
        self._send("SET TIME ZONE 'UTC'")

    @staticmethod
    def literal(value):
        if value is None:
            return "NULL"
        if isinstance(value, bool):
            return "TRUE" if value else "FALSE"
        if isinstance(value, int):
            return str(value)
        if isinstance(value, dt.datetime):
            return f"'{value.isoformat()}'::timestamptz"
        return "'" + str(value).replace("\x00", "").replace("'", "''") + "'"

    def _send(self, sql, raw=False):
        self.proc.stdin.write(sql + ("\n" if raw else ";\n") + f"\\echo {self.END}\n")
        self.proc.stdin.flush()
        lines = []
        while True:
            line = self.proc.stdout.readline()
            if line == "":
                raise RuntimeError("psql stopped: " + "".join(lines))
            if line.rstrip("\n") == self.END:
                break
            lines.append(line)
        errors = [line for line in lines if line.startswith(("ERROR:", "FATAL:")) or (line.startswith("psql:") and "ERROR:" in line)]
        if errors:
            raise RuntimeError("".join(errors).strip())
        return lines

    def query(self, sql, params):
        pieces = sql.split("?")
        if len(pieces) - 1 != len(params):
            raise ValueError(f"{len(pieces) - 1} placeholders but {len(params)} values: {sql}")
        statement = pieces[0] + "".join(self.literal(value) + piece for value, piece in zip(params, pieces[1:]))
        lines = self._send(statement)
        rows = csv.reader(io.StringIO("".join(lines)))
        return [tuple(None if cell == self.NULL else cell for cell in row) for row in rows if row]

    def begin(self):
        self._send("BEGIN")

    def commit(self):
        self._send("COMMIT")

    def rollback(self):
        self._send("ROLLBACK")

    def script(self, sql):
        self._send(sql)

    def close(self):
        self.proc.stdin.close()
        self.proc.wait(timeout=10)
        self.proc.stdout.close()


T0 = "2026-09-30T17:00:00.1234567Z"   # how .NET writes times ("O" format, 7 decimals)


def project(project_id="p1", helix="h1", disciplines=None, name="CMH04 BB"):
    return {
        "id": project_id, "name": name, "caFolder": r"Z:\Shared\CMH04\CA",
        "connection": {"provider": "helix", "projectId": helix, "accountId": None, "projectName": "CMH04", "host": None},
        "companyId": None, "companyName": "SMC", "foldersOverride": None,
        "scope": {"phase": "BaseBuild", "filterItems": False, "filterFolders": True, "includeUnlabeled": False, "isSplit": True},
        "disciplines": disciplines if disciplines is not None else {"submittal|p000210 - doors": "Electrical", "item|rfi|a": "Mechanical"},
    }


def item(item_id="a", number="RFI-001", holder="Pat Engineer", closed=False, status="Review"):
    return {
        "itemType": "rfi", "itemId": item_id, "number": number, "title": f"Title {number}", "status": status,
        "statusRank": 800, "closed": closed, "responsible": holder, "contract": "",
        "holders": [{"name": holder, "companyId": None, "company": "SMC", "state": "current", "since": "2026-09-20T00:00:00+00:00"},
                    {"name": "Next Person", "companyId": None, "company": "GC", "state": "upcoming", "since": None}],
        "attachmentCount": 2, "createdAt": "2026-09-01T08:00:00-07:00", "modifiedAt": None,
        "dueAt": "2026-10-03T00:00:00+00:00", "sourceUrl": "https://helix.example/rfi/a",
    }


def change(seq, entity, key, payload=None, operation=None, changed_at=T0):
    return {"seq": seq, "entity": entity, "key": key, "operation": operation or ("delete" if payload is None else "update"),
            "changedAt": changed_at, "payload": payload}


class ServerTests:
    """The tests; mixed into one class per database below."""

    def make_database(self):
        raise NotImplementedError

    def setUp(self):
        self.db = self.make_database()
        self.db.create_schema()
        self.service = server.Service(self.db)
        _, self.token = self.service.add_token("Pat Engineer")
        self.writer = self.service.authenticate(self.token)

    def tearDown(self):
        self.db.close()

    def rows(self, sql, *params):
        return self.db.run(lambda tx: tx.all(sql, *params))

    def send(self, client, *changes, user="Pat Engineer"):
        return self.service.accept_changes(self.writer, {"client": client, "user": user, "app": "3.0.0", "changes": list(changes)})

    # ---- Tests ----

    def test_schema_can_be_applied_again(self):
        self.db.create_schema()
        self.assertEqual(1, len(self.rows("SELECT version FROM ca_schema")))

    def test_tokens(self):
        self.assertEqual("Pat Engineer", self.writer["person"])
        self.assertTrue(self.writer["can_write"])
        self.assertIsNone(self.service.authenticate("cat_wrong"))
        token_id, reader = self.service.add_token("Read Only", ref="emp-42", can_write=False)
        self.assertFalse(self.service.authenticate(reader)["can_write"])
        self.assertTrue(self.service.revoke_token(token_id))
        self.assertIsNone(self.service.authenticate(reader))

    def test_changes_are_shared_and_readable(self):
        result = self.send("pc-a", change(1, "project", "p1", project()), change(2, "item", "helix|h1|rfi|a", item()),
                           change(3, "item", "helix|h1|rfi|b", item("b", "RFI-002", holder="Other Person")))
        self.assertEqual((3, 3, []), (result["accepted"], result["stored"], result["skipped"]))

        page = self.service.read_changes(0, 500)
        self.assertEqual([1, 2, 3], [entry["seq"] for entry in page["changes"]])
        self.assertEqual(3, page["last"])
        first = page["changes"][0]
        self.assertEqual(("pc-a", "Pat Engineer", "project", "p1", "update"), (first["client"], first["user"], first["entity"], first["key"], first["operation"]))
        self.assertEqual("CMH04 BB", first["payload"]["name"])
        self.assertEqual("2026-09-30T17:00:00.123456Z", page["changes"][1]["changedAt"])

        self.assertEqual([("p1", "helix", "h1", "BaseBuild")],
                         [tuple(row) for row in self.rows("SELECT project_key, provider, provider_project_id, phase FROM ca_tracked_projects")])
        self.assertEqual([("rfi", "item", "a", "Mechanical"), ("submittal", "folder", "p000210 - doors", "Electrical")],
                         [tuple(row) for row in self.rows("SELECT item_type, target_kind, target, discipline FROM ca_discipline_choices ORDER BY item_type")])
        self.assertEqual([("CMH04",)], [tuple(row) for row in self.rows("SELECT name FROM ca_provider_projects")])
        items = self.rows("SELECT item_id, number, attachment_count, read_by FROM ca_items ORDER BY item_id")
        self.assertEqual([("a", "RFI-001", 2, "Pat Engineer"), ("b", "RFI-002", 2, "Pat Engineer")],
                         [(row[0], row[1], int(row[2]), row[3]) for row in items])
        self.assertEqual(server.iso("2026-09-01T15:00:00Z"), server.iso(self.rows("SELECT created_at FROM ca_items WHERE item_id = 'a'")[0][0]))
        court = self.rows("SELECT item_id, holder, project_name FROM ca_items_in_court ORDER BY item_id")
        self.assertEqual([("a", "Pat Engineer", "CMH04"), ("b", "Other Person", "CMH04")], [tuple(row) for row in court])

    def test_sending_again_after_a_lost_answer_adds_nothing(self):
        batch = [change(1, "project", "p1", project()), change(2, "item", "helix|h1|rfi|a", item())]
        self.send("pc-a", *batch)
        again = self.send("pc-a", *batch)
        self.assertEqual(0, again["stored"])
        self.assertEqual(2, len(self.service.read_changes(0, 500)["changes"]))

    def test_the_same_item_read_by_someone_else_is_not_stored_again(self):
        self.send("pc-a", change(1, "item", "helix|h1|rfi|a", item()))
        result = self.send("pc-b", change(7, "item", "helix|h1|rfi|a", item()), user="Other Person")
        self.assertEqual((0, 1), (result["stored"], result["unchanged"]))
        changed = dict(item(), status="Answered")
        result = self.send("pc-b", change(8, "item", "helix|h1|rfi|a", changed), user="Other Person")
        self.assertEqual(1, result["stored"])
        self.assertEqual([("Answered", "Other Person")], [tuple(row) for row in self.rows("SELECT status, read_by FROM ca_items")])

    def test_an_older_read_of_an_item_is_not_stored(self):
        self.send("pc-a", change(1, "item", "helix|h1|rfi|a", dict(item(), status="Answered"), changed_at="2026-09-30T12:00:00Z"))
        # Another PC sends everything it has, including a read from the morning.
        result = self.send("pc-b", change(1, "item", "helix|h1|rfi|a", item(), changed_at="2026-09-30T08:00:00Z"))
        self.assertEqual((0, 1), (result["stored"], result["unchanged"]))
        self.assertEqual([("Answered",)], [tuple(row) for row in self.rows("SELECT status FROM ca_items")])
        # A newer read is stored, and so is a removal read later.
        self.assertEqual(1, self.send("pc-b", change(2, "item", "helix|h1|rfi|a", item(), changed_at="2026-09-30T13:00:00Z"))["stored"])
        self.assertEqual(0, self.send("pc-a", change(2, "item", "helix|h1|rfi|a", None, changed_at="2026-09-30T12:30:00Z"))["stored"])
        self.assertEqual(1, self.send("pc-a", change(3, "item", "helix|h1|rfi|a", None, changed_at="2026-09-30T14:00:00Z"))["stored"])

    def test_the_same_version_read_again_later_keeps_an_older_read_out(self):
        # x reads it at 08:00 (Open); y reads the same at 18:00; z's read from 12:00 (Stale) arrives last.
        self.send("pc-x", change(1, "item", "helix|h1|rfi|a", item(status="Open"), changed_at="2026-09-30T08:00:00Z"))
        later = self.send("pc-y", change(1, "item", "helix|h1|rfi|a", item(status="Open"), changed_at="2026-09-30T18:00:00Z"),
                          user="Person Y")
        self.assertEqual((0, 1), (later["stored"], later["unchanged"]))
        stale = self.send("pc-z", change(1, "item", "helix|h1|rfi|a", item(status="Stale"), changed_at="2026-09-30T12:00:00Z"))
        self.assertEqual((0, 1), (stale["stored"], stale["unchanged"]))
        status, read_at, read_by = self.rows("SELECT status, read_at, read_by FROM ca_items")[0]
        self.assertEqual(("Open", "2026-09-30T18:00:00Z", "Person Y"), (status, server.iso(read_at), read_by))
        self.assertEqual("2026-09-30T18:00:00Z", server.iso(self.rows("SELECT changed_at FROM ca_current")[0][0]))
        # Only one change was recorded for the two identical reads.
        self.assertEqual(1, len(self.service.read_changes(0, 500)["changes"]))

    def test_an_item_change_without_changed_at_is_skipped(self):
        missing = change(1, "item", "helix|h1|rfi|a", item())
        del missing["changedAt"]
        result = self.send("pc-a", missing, change(2, "item", "helix|h1|rfi|b", item("b", "RFI-002"), changed_at="not a time"),
                           change(3, "item", "helix|h1|rfi|c", item("c", "RFI-003")))
        self.assertEqual(1, result["stored"])
        self.assertEqual([1, 2], sorted(entry["seq"] for entry in result["skipped"]))
        self.assertIn("changedAt", result["skipped"][0]["reason"])
        self.assertEqual([("c",)], [tuple(row) for row in self.rows("SELECT item_id FROM ca_items")])

    def test_a_seq_must_be_a_real_whole_number(self):
        result = self.send("pc-a", change(True, "item", "helix|h1|rfi|a", item()), change(2 ** 63, "item", "helix|h1|rfi|b", item("b")),
                           change(0, "item", "helix|h1|rfi|c", item("c")), change(-4, "item", "helix|h1|rfi|d", item("d")),
                           change(5, "item", "helix|h1|rfi|e", item("e")))
        self.assertEqual(1, result["stored"])
        self.assertEqual([None, None, None, None], [entry["seq"] for entry in result["skipped"]])
        self.assertEqual([("e",)], [tuple(row) for row in self.rows("SELECT item_id FROM ca_items")])

    def test_item_removed_from_helix(self):
        self.send("pc-a", change(1, "item", "helix|h1|rfi|a", item()))
        self.send("pc-a", change(2, "item", "helix|h1|rfi|a", None))
        self.assertIsNotNone(self.rows("SELECT deleted_at FROM ca_items")[0][0])
        self.assertEqual([], self.rows("SELECT * FROM ca_item_holders"))
        self.assertEqual([], self.rows("SELECT * FROM ca_items_in_court"))
        # Deleting what is already deleted isn't another change.
        self.assertEqual(0, self.send("pc-b", change(1, "item", "helix|h1|rfi|a", None))["stored"])
        # It comes back when someone reads it again.
        self.send("pc-a", change(3, "item", "helix|h1|rfi|a", item()))
        self.assertIsNone(self.rows("SELECT deleted_at FROM ca_items")[0][0])

    def test_closed_items_are_not_in_court(self):
        self.send("pc-a", change(1, "item", "helix|h1|rfi|a", item(closed=True)))
        self.assertEqual([], self.rows("SELECT * FROM ca_items_in_court"))

    def test_project_removed_from_a_list(self):
        self.send("pc-a", change(1, "project", "p1", project()))
        self.send("pc-a", change(2, "project", "p1", None))
        self.assertIsNotNone(self.rows("SELECT deleted_at FROM ca_tracked_projects")[0][0])
        self.assertEqual([], self.rows("SELECT * FROM ca_discipline_choices"))
        self.send("pc-a", change(3, "project", "p1", project(disciplines={})))
        self.assertIsNone(self.rows("SELECT deleted_at FROM ca_tracked_projects")[0][0])

    def test_unusable_changes_are_skipped_once(self):
        result = self.send("pc-a",
                           change(1, "folder", "x", {"a": 1}),
                           change(2, "item", "helix|h1|a", item()),
                           change(3, "project", "p" * 65, project()),
                           change(4, "item", "helix|h1|rfi|a", "not an object"),
                           {"seq": "5"},
                           change(6, "item", "helix|h1|rfi|a", item()))
        self.assertEqual(1, result["stored"])
        self.assertEqual([None, 1, 2, 3, 4], sorted((entry["seq"] for entry in result["skipped"]), key=lambda seq: seq or 0))
        again = self.send("pc-a", change(1, "folder", "x", {"a": 1}))
        self.assertEqual([], again["skipped"])

    def test_a_change_the_database_refuses_is_skipped_and_the_rest_kept(self):
        single_holder = dict(item(), holders=[{"name": "A", "state": "current", "since": None, "company": None}])
        breaking = dict(item("c", "RFI-003"), statusRank=2 ** 40)  # too big for INTEGER in PostgreSQL; SQLite takes it
        result = self.send("pc-a", change(1, "item", "helix|h1|rfi|a", single_holder), change(2, "item", "helix|h1|rfi|c", breaking),
                           change(3, "item", "helix|h1|rfi|b", item("b", "RFI-002")))
        stored_ids = sorted(row[0] for row in self.rows("SELECT item_id FROM ca_items"))
        if self.db.kind == "postgresql":
            self.assertEqual(["a", "b"], stored_ids)
            self.assertEqual([2], [entry["seq"] for entry in result["skipped"]])
            self.assertEqual([1, 3], [int(row[0]) for row in self.rows("SELECT client_seq FROM ca_changes ORDER BY seq")])
        else:
            self.assertEqual(["a", "b", "c"], stored_ids)

    def test_reading_in_pages(self):
        seq = 0
        for batch in range(3):
            changes = []
            for _ in range(400):
                seq += 1
                changes.append(change(seq, "item", f"helix|h1|rfi|{seq}", item(str(seq), f"RFI-{seq}")))
            self.assertEqual(400, self.send("pc-a", *changes)["stored"])
        cursor, pages = 0, []
        while True:
            page = self.service.read_changes(cursor, 500)
            pages.append(len(page["changes"]))
            if page["last"] == cursor:
                break
            cursor = page["last"]
        self.assertEqual([500, 500, 200, 0], pages)
        self.assertEqual(1200, cursor)

    def test_a_cursor_past_the_newest_change_means_the_server_was_reset(self):
        self.send("pc-a", change(1, "project", "p1", project()))
        with self.assertRaises(server.Conflict) as caught:
            self.service.read_changes(5, 500)
        self.assertEqual(1, caught.exception.extra["last"])

    # ---- Over HTTP, as the app sends and reads ----

    def start_http(self):
        httpd = server.make_server(self.service, "127.0.0.1", 0)
        thread = threading.Thread(target=httpd.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(lambda: (httpd.shutdown(), httpd.server_close()))
        return f"http://127.0.0.1:{httpd.server_address[1]}{server.API}"

    @staticmethod
    def call(url, token=None, body=None, raw=None):
        data = raw if raw is not None else (json.dumps(body).encode("utf-8") if body is not None else None)
        request = urllib.request.Request(url, data=data, method="POST" if data is not None else "GET")
        if token:
            request.add_header("Authorization", f"Bearer {token}")
        if data is not None:
            request.add_header("Content-Type", "application/json; charset=utf-8")
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status, json.loads(response.read())
        except urllib.error.HTTPError as error:
            return error.code, json.loads(error.read())

    def test_http(self):
        base = self.start_http()
        self.assertEqual((200, {"ok": True, "protocol": 1}), self.call(f"{base}/health"))
        self.assertEqual(401, self.call(f"{base}/hello")[0])
        self.assertEqual(401, self.call(f"{base}/hello", token="cat_wrong")[0])
        status, hello = self.call(f"{base}/hello", token=self.token)
        self.assertEqual((200, "Pat Engineer", True), (status, hello["user"], hello["canWrite"]))

        _, reader = self.service.add_token("Viewer", can_write=False)
        self.assertEqual(403, self.call(f"{base}/changes", reader, {"client": "pc-v", "changes": []})[0])
        self.assertEqual(400, self.call(f"{base}/changes", self.token, raw=b"{not json")[0])
        self.assertEqual(400, self.call(f"{base}/changes", self.token, {"client": "bad id!", "changes": []})[0])
        self.assertEqual(400, self.call(f"{base}/changes?after=x", self.token)[0])
        self.assertEqual(404, self.call(f"{base}/nothing", self.token)[0])

        # Exactly what the app's CentralSync.ChangesBody writes.
        body = {"client": "0f8fad5bd9cb469fa16570867728950e", "user": "Pat Engineer", "app": "3.0.0", "changes": [
            {"seq": 41, "entity": "project", "key": "p1", "operation": "insert", "changedAt": T0, "payload": project()},
            {"seq": 42, "entity": "item", "key": "helix|h1|rfi|a", "operation": "update", "changedAt": T0, "payload": item()},
            {"seq": 43, "entity": "item", "key": "helix|h1|rfi|z", "operation": "delete", "changedAt": T0, "payload": None}]}
        status, answer = self.call(f"{base}/changes", self.token, body)
        self.assertEqual((200, 2), (status, answer["stored"]))  # deleting something never stored isn't a change

        status, page = self.call(f"{base}/changes?after=0&limit=500", reader)
        self.assertEqual(200, status)
        # What the app's CentralSync.ParsePage reads.
        self.assertEqual(2, page["last"])
        for entry in page["changes"]:
            self.assertEqual({"seq", "client", "user", "entity", "key", "operation", "changedAt", "payload"}, set(entry))
            self.assertIsInstance(entry["payload"], dict)
        self.assertEqual(item(), page["changes"][1]["payload"])
        self.assertEqual(409, self.call(f"{base}/changes?after=99", reader)[0])

    def test_an_answer_sent_before_the_body_was_read_does_not_break_the_next_request(self):
        base = self.start_http()
        host, port = urllib.parse.urlparse(base).hostname, urllib.parse.urlparse(base).port
        _, reader = self.service.add_token("Viewer", can_write=False)
        body = json.dumps({"client": "pc-v", "changes": [change(1, "item", "helix|h1|rfi|a", item())] * 50}).encode("utf-8")
        connection = http.client.HTTPConnection(host, port, timeout=10)
        self.addCleanup(connection.close)
        # Refused before the body is read (403) ...
        connection.request("POST", f"{server.API}/changes", body=body,
                           headers={"Authorization": f"Bearer {reader}", "Content-Type": "application/json"})
        response = connection.getresponse()
        self.assertEqual(403, response.status)
        self.assertIn("error", json.loads(response.read()))
        # ... and the same keep-alive connection still gets a proper answer to the next request.
        connection.request("GET", f"{server.API}/hello", headers={"Authorization": f"Bearer {reader}"})
        response = connection.getresponse()
        self.assertEqual(200, response.status)
        self.assertEqual("Viewer", json.loads(response.read())["user"])
        # The same after a 401, and a Content-Length that isn't a number is a 400, not a crash.
        connection.request("POST", f"{server.API}/changes", body=body, headers={"Authorization": "Bearer cat_wrong"})
        response = connection.getresponse()
        self.assertEqual(401, response.status)
        response.read()
        connection.request("POST", f"{server.API}/changes", body=b"{}",
                           headers={"Authorization": f"Bearer {self.token}", "Content-Length": "lots"})
        response = connection.getresponse()
        self.assertEqual(400, response.status)
        self.assertIn("Content-Length", json.loads(response.read())["error"])


class SqliteServerTests(ServerTests, unittest.TestCase):
    def make_database(self):
        folder = tempfile.mkdtemp(prefix="ca-tracker-test-")
        self.addCleanup(shutil.rmtree, folder, True)
        return server.Database.open(f"sqlite:{os.path.join(folder, 'central.db')}")


PSQL_ARGS = os.environ.get("CA_TRACKER_TEST_PSQL")


@unittest.skipUnless(PSQL_ARGS and shutil.which("psql"), "set CA_TRACKER_TEST_PSQL to test with PostgreSQL")
class PostgresServerTests(ServerTests, unittest.TestCase):
    def make_database(self):
        driver = PsqlDriver(shlex.split(PSQL_ARGS))
        driver.script("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public")
        return server.Database(driver)


class ValueTests(unittest.TestCase):
    def test_times(self):
        cases = {
            "2026-09-30T17:00:00.1234567Z": "2026-09-30T17:00:00.123456Z",
            "2026-09-30T10:00:00-07:00": "2026-09-30T17:00:00Z",
            "2026-09-30 17:00:00.5+00": "2026-09-30T17:00:00.500000Z",
            "2026-09-30": "2026-09-30T00:00:00Z",
        }
        for value, expected in cases.items():
            self.assertEqual(expected, server.iso(value), value)
        self.assertIsNone(server.parse_time("soon"))
        self.assertIsNone(server.parse_time("2026-13-40"))

    def test_discipline_keys_split_like_the_app(self):
        self.assertEqual(("rfi", "folder", "p000210 - doors"), server.split_discipline_key("rfi|p000210 - doors"))
        self.assertEqual(("rfi", "item", "abc"), server.split_discipline_key("item|rfi|abc"))
        self.assertEqual(("", "", ""), server.split_discipline_key("nothing"))


if __name__ == "__main__":
    unittest.main()
