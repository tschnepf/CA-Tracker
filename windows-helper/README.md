# SMC CA Tracker (Windows app)

The CA Tracker app is where projects live. Each project is a CA folder on the Egnyte drive,
optionally connected to one Helix or Procore project. **Refresh** opens that project in Chrome,
where the TaskHub Helper extension reads its RFIs and Submittals and hands them back. The app then
compares them with the CA folder and can move folders for returned items into `~Returned`. It never
changes a folder without first showing a preview you can cancel.

```
CA Tracker app ──Refresh──▶ opens Chrome at the project page (with a one-time refresh id)
      ▲                                  │
      │                 TaskHub Helper extension claims the id, scans the page
      │                                  │
      └── %LOCALAPPDATA%\SMC-CA-Tracker ◀── SmcCaTracker.NativeHost.exe (Chrome native messaging)
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
3. In Chrome, reload the TaskHub Helper extension, open its settings, and select **Enable Windows helper**.
   The footer of the app then shows when the extension last connected.
4. **New project…**: choose the project's CA folder (e.g. `Z:\Shared\Projects\CMH04\CA`). The name
   defaults to the folder above `CA`.
5. **Connect…**: pick the Helix or Procore project, or paste its link:
   - Helix: the project's Overview page, e.g. `https://project.helixteam.com/Projects/<id>/Overview`
   - Procore: the project's RFIs tool, e.g. `https://app.procore.com/webclients/host/companies/<company>/projects/<project>/tools/rfis`

   The list fills in with every project Chrome has seen: all your assigned Helix projects after you
   open any Helix project, and Procore projects you open or discover.
6. **Refresh.**

If you move the `dist` folder, register with Chrome again.

## Refresh

**Refresh** always rechecks the CA folder. For a connected project it also:

1. Records a one-time refresh request (a random id) in `%LOCALAPPDATA%\SMC-CA-Tracker\refresh`.
2. Opens Chrome at the project's page with that id in the link:
   - Helix: the Overview page, which reads every RFI and Submittal in one scan.
   - Procore: the RFIs tool, then the Submittals tool.
3. The extension asks the app's native host to claim the id. Only ids the app created are accepted,
   so a link someone sends you cannot start a refresh.
4. The extension runs a fresh scan and sends the items back. The tab closes when the refresh
   finishes; you can turn this off in Settings.

The project header shows **when the Helix/Procore data was last refreshed** and **when the CA folder
was last checked**. While a refresh runs it shows progress, and you can cancel it. If Chrome doesn't
report back within 3 minutes (changeable in Settings), the app says so. The usual causes are not
being signed in to Helix or Procore in Chrome, or the extension's Windows helper being off.

Browsing a connected project in Chrome also updates its data, even without pressing Refresh.

Chrome opens in its last-used profile. If you use several Chrome profiles, set the profile folder
(e.g. `Profile 1`) in **Settings → Chrome**.

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
it is closed. The company defaults to the one on your Helix login and can be changed per project in
**Edit project**.
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

Creating, editing, connecting or removing a project, and changing settings, only update the app's
own settings file. They don't change anything in the CA folder.

## Files

| Path | What |
|---|---|
| `%LOCALAPPDATA%\SMC-CA-Tracker\settings.json` | Projects (CA folder + connection), theme, folder names, returned rule, Chrome options |
| `%LOCALAPPDATA%\SMC-CA-Tracker\snapshots\*.json` | Latest item list per project from the extension |
| `%LOCALAPPDATA%\SMC-CA-Tracker\refresh\*.json` | Refresh requests (cleaned up after a day) |
| `%LOCALAPPDATA%\SMC-CA-Tracker\catalog.json` | Helix/Procore projects Chrome has seen, offered in Connect |
| `%LOCALAPPDATA%\SMC-CA-Tracker\journal.jsonl` | Every folder change that was applied |
| `%LOCALAPPDATA%\SMC-CA-Tracker\native-host.log` | Errors from the Chrome bridge, if any |
