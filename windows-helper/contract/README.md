# Workload Tracker contract

CA Tracker sends scans and item assignments to Workload Tracker. Workload Tracker owns the contract:
its serializers in `backend/ca_tracker/serializers.py` define the endpoints, and its committed
`backend/openapi.json` is the schema. This folder pins the part of that schema the desktop uses.

| File | What it is |
| --- | --- |
| `openapi.json` | The `/api/ca/` paths of Workload Tracker's schema, the two general reads the desktop makes (`POST /api/projects/search/` to bind a project, `GET /api/assignments/` for a project's team), and every component they use. `info.x-source.commit` names the tracker commit it came from. Never edit by hand. |
| `samples/` | Request bodies the tracker's own tests also use (`backend/ca_tracker/tests/fixtures/`). Keep the two copies identical. |
| `../src/SmcCaTracker.Core/WorkloadTracker/Contract.g.cs` | DTOs generated from `openapi.json`. Never edit by hand. |

## Checks

- `dotnet run --project windows-helper/tools/SmcCaTracker.ContractGen -- check` fails when
  `Contract.g.cs` differs from what `openapi.json` generates. CI runs it.
- `WorkloadTrackerContractTests` validates every sample and every observation the app builds against
  `openapi.json`, and round-trips the samples through the DTOs. Objects are checked strictly: a
  field the schema does not list fails the test.
- Project search and team rows are large and loosely typed, so the app reads only the fields it uses
  (`WorkloadTrackerApi.ReadProjectChoices` and `ReadTeamPage`); a test checks those fields are still in
  the pinned schema. The pinned general operations are listed in `ContractGenerator.PinnedOperations`.
- An item's four CA Tracker fields (`discipline`, `disciplineSource`, `folderStatus`, `folderPath`) are
  optional, and the tracker keeps what it has when they are left out but clears a value sent as null.
  `ContractJson` writes them as a set: all four for an item the app compared (it always has a folder
  status), none for one it didn't.

## When the tracker's endpoints change

Pin the new schema in the same change that alters the tracker, then regenerate:

```powershell
dotnet run --project windows-helper/tools/SmcCaTracker.ContractGen -- pin C:\Source\workload-tracker\backend\openapi.json <tracker commit>
```

`pin` also regenerates `Contract.g.cs`. Copy any new or changed sample bodies from the tracker's
fixtures into `samples/`. A change an older desktop could not send or read also bumps
`SCHEMA_VERSION` in the tracker's `ca_tracker/contract.py` and `ObservationBuilder.SchemaVersion` here.

## Fixtures from real data

Settings → General → Troubleshooting → Developer tools → **Export Workload Tracker fixtures** writes
anonymized observations of the app's own projects to
`%LOCALAPPDATA%\SMC-CA-Tracker\exports\workload-tracker-fixtures-<time>\`. People, companies, emails,
titles, provider ids, project names and links are replaced; numbers, revisions, statuses, dates and
counts are kept. Read them before copying them into the tracker's `backend/ca_tracker/tests/fixtures/`.
