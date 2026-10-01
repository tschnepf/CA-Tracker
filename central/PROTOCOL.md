# CA Tracker sync protocol (version 1)

Each CA Tracker keeps everything it needs in its own database, so it works fully on its own. If the
company has a CA Tracker server, every copy of the app also:

1. **sends** its change log: the projects on its list (with their discipline choices) and the Helix and
   Procore RFIs and Submittals it reads;
2. **reads** everyone's changes since the last ones it read, and takes in what applies to it.

The server puts every change in one order (`seq`), keeps the latest version of each project and item,
and keeps readable tables up to date ([schema.sql](schema.sql)) for reports and other apps.

`ca_tracker_server.py` in this folder is a complete server; another system (such as the company
workload tracker) can implement the same four endpoints instead.

## Basics

- Base address: `{server}/api/ca-tracker/v1/`, for example `https://ca.example.com/api/ca-tracker/v1/changes`.
- HTTPS. JSON in UTF-8 both ways.
- Every request except `health` carries `Authorization: Bearer <access token>`. The server keeps only a
  hash of each token. Unknown or revoked token: `401`. A read-only token sending changes: `403`.
- Errors are JSON: `{ "error": "what's wrong" }`.
- Times are ISO 8601 with an offset or `Z`. The app writes .NET's round-trip format, which has 7 decimal
  places (`2026-09-30T17:00:00.1234567+00:00`); servers must accept any number of decimals.
- Fields that a side doesn't know are ignored, so either side can add fields within version 1.

## Endpoints

### `GET health`

No token needed. `200 { "ok": true, "protocol": 1 }`.

### `GET hello`

Checks the token. `200 { "name": "CA Tracker server", "protocol": 1, "user": "Pat Engineer", "canWrite": true }`
(`user` is who the token was issued to; the app shows it in Settings → Connections).

### `POST changes`

```json
{
  "client": "0f8fad5bd9cb469fa16570867728950e",
  "user": "Pat Engineer",
  "app": "3.0.0",
  "changes": [
    { "seq": 41, "entity": "project", "key": "6c1d…", "operation": "update",
      "changedAt": "2026-09-30T17:00:00.1234567+00:00", "payload": { "id": "6c1d…", "name": "CMH04 BB", … } },
    { "seq": 42, "entity": "item", "key": "helix|5f2a…|rfi|65a1…", "operation": "delete",
      "changedAt": "2026-09-30T17:00:00.1234567+00:00", "payload": null }
  ]
}
```

- `client`: the app install's id (one per Windows user and PC; letters, digits, `-`, `_`; up to 64).
- `seq`: the app's own change number. It only ever goes up for a client.
- `entity` / `key` / `payload`: see [Projects and items](#projects-and-items).
- `operation`: `insert` or `update` (both mean "this is the latest version") or `delete`.
- `changedAt`: when the app made the change or read the item from Helix/Procore.
- At most 5,000 changes per request (the app sends 500).

Answer `200`:

```json
{ "accepted": 2, "stored": 1, "unchanged": 0, "skipped": [ { "seq": 42, "reason": "…" } ], "last": 1290 }
```

What a server must do:

- **Accept retries.** Remember the highest `seq` accepted from each client and ignore changes at or below
  it: the app sends a batch again when it didn't get the answer.
- **Not store repeats.** A change identical to the latest version of that project/item (same payload, or
  a delete of something already deleted) is answered as accepted but not stored again. Several people
  reading the same Helix project send the same items; only real changes reach everyone else.
  (The reference server compares SHA-256 hashes of the payload with sorted keys.)
- **Not store older reads.** An item change whose `changedAt` is before that of the item's latest
  version is answered as accepted but not stored: a PC sending everything it has (the first time it
  shares, or after a server restore) re-sends reads that may be older than someone else's.
- **Skip what it can't use, but accept it.** A change with an unknown entity, a bad key or no payload is
  listed in `skipped` and counted as received, so the app doesn't send it forever.
- Answer `200` only once the batch is saved: the app marks the batch sent when it gets the answer.

### `GET changes?after={seq}&limit={n}`

Everyone's stored changes after `seq` (the server's numbering), oldest first, at most `limit`
(default 500, up to 1,000):

```json
{
  "changes": [
    { "seq": 1289, "client": "0f8f…", "user": "Pat Engineer", "entity": "item",
      "key": "helix|5f2a…|rfi|65a1…", "operation": "update",
      "changedAt": "2026-09-30T17:00:00.123456Z", "payload": { "itemType": "rfi", … } }
  ],
  "last": 1289
}
```

- `last`: the `seq` to ask after next time (the last one returned, or `after` when there are none). The
  app keeps reading pages until a page has fewer than `limit` changes.
- The client's own changes are included; the app skips them (it compares `client`).
- If `after` is greater than the newest `seq` the server has, the server was reset or restored from a
  backup: answer `409 { "error": "…", "last": <newest> }`. The app then sends everything it has again
  and reads from 0.

## Projects and items

### Projects: `entity: "project"`, `key`: the project's id

The payload is a tracker project as the app keeps it (camelCase):

| Field | Meaning |
|---|---|
| `id` | The project's id (32 hex characters) |
| `name` | The name on that person's list |
| `caFolder` | The CA folder on that person's PC |
| `connection` | `{ provider: "helix" \| "procore", projectId, projectName, accountId, host }` or null |
| `companyId`, `companyName` | The company in Helix/Procore |
| `foldersOverride` | `{ rfiFolder, submittalFolder, returnedFolder }` when this project's folders are named differently, or null |
| `scope` | `{ phase: "Whole" \| "BaseBuild" \| "TenantFitOut", filterItems, filterFolders, includeUnlabeled }` |
| `disciplines` | Discipline choices: `"rfi\|<folder name, lower case>": "Electrical"` for a CA folder, `"item\|rfi\|<item id, lower case>": "Mechanical"` for an item; `""` means "detect it from the name again" |

A project is one person's list entry: several people can track the same Helix or Procore project, each
under their own id. `delete` means it was removed from that person's list.

### Items: `entity: "item"`, `key`: `provider|projectId|itemType|itemId`

For example `helix|5f2a…|submittal|65a1…`. `provider` is `helix` or `procore`, `projectId` is the
Helix/Procore project's id, `itemType` is `rfi` or `submittal`. The payload:

| Field | Meaning |
|---|---|
| `itemType`, `itemId` | As in the key |
| `number`, `title`, `status` | As shown in Helix/Procore (Procore Submittal numbers include ` Rev N`) |
| `statusRank` | The status's order in the workflow, when known |
| `closed` | Closed or answered |
| `responsible`, `contract` | As shown in Helix/Procore |
| `holders` | Whose court it is in: `[{ name, companyId, company, state: "current" \| "upcoming" \| …, since }]` |
| `attachmentCount` | Number of attachments |
| `createdAt`, `modifiedAt`, `dueAt` | Times, or null |
| `sourceUrl` | The item's page in Helix/Procore |

`delete` means the item is no longer in Helix/Procore (or moved to another project).

## What the app takes in

The server shares everything; each copy of the app decides what applies to it. Each share first reads
everyone's new changes, then sends its own.

- **Items** are taken in for the Helix/Procore projects on this person's list. Only the most recently
  read version of each item counts (by `changedAt`), and this PC's own are skipped. If this PC has read that project since the other
  person read the item (its last complete read started after the change's `changedAt`), what this PC
  has is at least as new and the change is skipped. Items waiting to be sent from here also win.
- **Projects**: a change for a project on this person's list (the same id, or the same Helix/Procore
  project, whatever the other person called it) shares its choices:
  - Discipline choices are combined: choices this PC doesn't have are added, and a different
    discipline for the same folder or item replaces the one here. An empty choice (`""`) means "detect
    it again from the name"; it is shared like any other choice.
  - The folder names (`foldersOverride`) and the Base Build/TI `scope` are taken.
  - When this PC has changed that project and not sent it yet, or sent a change of it after the
    received one, only the missing discipline choices are added: what was decided here stays.
  - The project's name and CA folder always stay as they are on this PC, and someone removing a project
    from their list doesn't remove it from anyone else's.
- Changes taken in are saved without going back into the change log, so they aren't sent back.
- The first time an app shares with a server, it reads everyone's changes from 0 first and then sends
  every project and item it has (with what it just took in), items dated when they were read. After a
  `409` it queues everything it has first (so what was decided here since the server's backup isn't
  undone by older changes), then reads from 0 and sends.

## Building it into another system

The tables in [schema.sql](schema.sql) all start with `ca_`, so they can live in an existing PostgreSQL
database (such as the workload tracker's). Either point the reference server at that database, or
implement the endpoints above in that system:

1. `ca_tokens`: issue tokens (store `sha256(token)`, show the token once).
2. `POST changes`: in one transaction, per change in `seq` order: skip at or below
   `ca_clients.last_client_seq`; skip repeats of `ca_current` and item reads older than its
   `changed_at`; insert into `ca_changes`; upsert `ca_current`; update the readable tables; then save
   the new `last_client_seq`.
3. `GET changes`: `SELECT … FROM ca_changes WHERE seq > ? ORDER BY seq LIMIT ?`.

The readable tables are what reports should use: `ca_items` (every RFI and Submittal last read by
anyone, with `read_at` and `read_by`), `ca_item_holders`, the `ca_items_in_court` view (open items and
who has them now), `ca_tracked_projects` (who tracks what, and where their CA folder is),
`ca_discipline_choices` and `ca_provider_projects`.
