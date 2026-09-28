# SMC CA Tracker (Windows helper)

A small Windows app that compares what the TaskHub Helper extension sees in Helix or Procore
with each project's CA folder on the Egnyte drive, and moves item folders into a returned folder.
It never changes a folder without first showing a preview you can cancel.

```
TaskHub Helper (Chrome) ──native messaging──▶ SmcCaTracker.NativeHost.exe ──▶ %LOCALAPPDATA%\SMC-CA-Tracker\snapshots
                                                                                      │
                                              SmcCaTracker.exe (the app) ◀────────────┘  compares with  Z:\...\CA
```

## Build

Requires the .NET 10 SDK on Windows.

```powershell
powershell -ExecutionPolicy Bypass -File windows-helper\build.ps1
```

This runs the tests and publishes `SmcCaTracker.exe` and `SmcCaTracker.NativeHost.exe` into
`windows-helper\dist`. Use `-SelfContained` to build a copy for PCs without .NET 10 installed.
Close the app before rebuilding, because Windows locks a running exe.

## First run

1. Run `windows-helper\dist\SmcCaTracker.exe`.
2. **Settings → Register with Chrome.** This writes a small file to `%LOCALAPPDATA%\SMC-CA-Tracker`
   and a registry key under `HKEY_CURRENT_USER` (and Edge's equivalent). No admin rights are needed.
3. In Chrome, open the TaskHub Helper extension's settings and select **Enable Windows helper**.
   Chrome should then show **Connected**.
4. Open a project's **Overview** page in Helix, which lists every RFI and Submittal. An RFI or
   Submittals page sends only that type. The project appears in the app within a second or two.
5. Select the project and **Choose CA folder…** (e.g. `Z:\Shared\Projects\CMH04\CA`).

If you move the `dist` folder, register with Chrome again.

## Folder layout

Default, per CA folder (changeable in Settings, globally or per project):

```
CA\
  RFIs\
    RFI-012 - Slab edge\
    ~Returned\
      RFI-004 - Door hardware\
  Submittals\
    P000210 - Overhead Coiling Doors\
    ~Returned\
```

Folders are matched to items by the number at the start of the name (the text before ` - `).
If that doesn't match exactly, a looser match ignores `RFI`/`SUB` prefixes, punctuation and leading
zeros, but only when exactly one item fits. Only folder names and dates are read, so Egnyte
online-only files are not downloaded.

## What "returned" means

By default an item is returned when **no one from the selected company holds it**, including when
it is closed. The company defaults to the one on your Helix login and can be changed per project.
"Holds" uses the extension's own In court and Coming up rules. Settings can also:

- count items where your company only has a later step (coming up) as returned
- stop counting closed items
- list statuses that always, or never, count as returned

Procore's visible grid does not say who holds an item, so Procore items count as returned only
when they are closed (or match a status list). Folders missing from a partial Procore view are
shown as "Not in last Procore view", never as missing.

## Results

| Result | Meaning |
|---|---|
| Ready to move to returned | Returned per the rule, but the folder is still active |
| Back in court — folder is in returned | In `~Returned`, but the item is back with your company |
| More than one folder / Folder matches several items | Needs a person to sort out; excluded from suggestions |
| Not found in provider | A complete Helix scan has no item with this number |
| No folder | The item exists but has no folder yet |
| Open / Returned | Everything lines up |

## Changes always need a preview

**Mark ticked as returned…**, **Move ticked back to active…** and **Create missing folders…** all
open a preview listing every folder that would be created or moved, with its full From and To paths.
Anything that can't be done safely (the destination already exists, the folder has moved, two folders
would land in the same place) is shown in red and skipped. Cancel is the default button. After you
apply, each step is re-checked just before it runs. A results window shows what happened, and every
step is appended to `%LOCALAPPDATA%\SMC-CA-Tracker\journal.jsonl`.

Choosing a CA folder or changing settings only updates the app's own settings file. They don't
change anything in the CA folder.

## Files

| Path | What |
|---|---|
| `%LOCALAPPDATA%\SMC-CA-Tracker\settings.json` | Theme, folder names, returned rule, project → CA folder mappings |
| `%LOCALAPPDATA%\SMC-CA-Tracker\snapshots\*.json` | Latest item list per project from the extension |
| `%LOCALAPPDATA%\SMC-CA-Tracker\journal.jsonl` | Every folder change that was applied |
| `%LOCALAPPDATA%\SMC-CA-Tracker\native-host.log` | Errors from the Chrome bridge, if any |
