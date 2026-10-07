# SMC CA Tracker (Windows app)

The CA Tracker app is where projects live. Each project is a CA folder on the Egnyte drive,
optionally connected to one Helix or Procore project. The app keeps each project's RFIs and
Submittals up to date, compares them with the CA folder, and can move folders for returned items
into `~Returned`. It never changes a folder without first showing a preview you can cancel.

```
                 ┌── background Helix sync (hidden page, changed items only) ◀── Helix
CA Tracker app ──┤── background Procore sync (hidden page, every project)   ◀── Procore
   (in memory)   └── background CA folder index (re-lists only what changed) ◀── Egnyte drive
        │
        └── saved in the background to the database (%LOCALAPPDATA%\SMC-CA-Tracker\tracker.db)
```

No Chrome extension or browser add-in is involved.

## How it stays fast

- **Everything is in memory.** At start the app reads its database once (projects, items, CA folder
  lists, settings). Filtering, sorting, counts and switching projects never wait on the disk, the
  Egnyte drive or Helix. Changes are saved to the database in the background.
- **CA folders are indexed.** Each project's CA folders are listed once and the list is kept. Every
  20 seconds (and whenever you come back to the app) the app checks the folders' dates — far cheaper
  than listing them — and lists a folder again only when something in it changed. Every 15 minutes
  the shown projects are fully re-listed as a safety net, and **Sync from Helix / Procore** always re-lists.
- **Helix syncs in the background.** One hidden page of the CA Tracker browser stays on Helix and
  reads each tracked project through Helix's own data service — no windows. The first read of a
  project in a session reads everything; later reads ask Helix only for items changed since, so a
  sync of many projects takes seconds. Every 12 hours a project is read in full again so items
  deleted in Helix drop out. Tracked projects sync every 15 minutes (Settings → Background sync).
- **The list redraws once** per change, not once per row.
- **A timing log** (Settings → Open the log folder) records how long each step took and marks any
  slow one, so a sluggish step can be found instead of guessed at.

## The database

`tracker.db` is a SQLite database with plain, portable tables (text ids, ISO dates, no SQLite-only types):

| Table | What |
|---|---|
| `projects`, `discipline_choices` | Tracker projects (CA folder, connection, part, folder names) and disciplines set by hand |
| `provider_snapshots`, `snapshot_coverage`, `items`, `item_holders` | Each Helix/Procore project's items and who holds them |
| `provider_projects` | Helix/Procore projects you can see (offered in Connect) |
| `folder_scans`, `folder_entries`, `folder_containers` | The CA folder index |
| `accounts`, `preferences` | Sign-ins shown in Connections; settings and the last view |
| `sync_runs` | Each Helix/Procore read: full or changes only, items, changes, errors |
| `wt_outbox` | While connected to Workload Tracker: for each Helix/Procore project, which items changed or went away since the last send (one row per project, so it stays small). Empty when not connected |

On first start, the JSON files of earlier versions (`settings.json`, `snapshots\`, `catalog.json`,
`accounts.json`, `view.json`) are imported and moved to `imported-json\` as a backup.

## Rolling it out to the company

- **Installer:** `build.ps1 -Installer` (needs [Inno Setup 6](https://jrsoftware.org/isdl.php)) builds
  `installer\output\SMC-CA-Tracker-Setup-<version>.exe`: a per-user install (no administrator rights),
  with the .NET runtime included, Start-menu shortcut, and a check for the WebView2 Runtime.
- **Company settings:** put `company\ca-tracker-company.json` (see `company\README.md`) next to the app —
  the installer ships it — to set folder names, the company's disciplines, the sync
  interval and the update location for everyone, and lock what people shouldn't change.
- **Workload Tracker (optional):** each person enters the tracker's address in Settings → Connections →
  Workload Tracker and presses **Connect…**; they sign in to the tracker in the window that opens and
  press **Connect CA Tracker** on its Profile page. CA Tracker then sends what it reads every minute
  while it is open. See [`contract/README.md`](contract/README.md) for the contract.
- **Updates:** publish each new installer with its `latest.json` to the update location; every CA
  Tracker offers **Update to <version>** in the title bar and installs it quietly.
- **Problems:** the log (Settings → Open the log folder) records timings, errors and crashes.

## Build

Requires the .NET 10 SDK on Windows.

```powershell
powershell -ExecutionPolicy Bypass -File windows-helper\build.ps1
```

This runs the tests and publishes `SmcCaTracker.exe` into `windows-helper\dist`. Use `-SelfContained` to build a copy for PCs without .NET 10 installed.
Close the app before rebuilding, because Windows locks a running exe.

To try a build without touching your real data, run it with its own data folder; it can stay open
beside the installed app. A newer build upgrades the database it opens, and older versions can't use
an upgraded one.

```powershell
windows-helper\dist\SmcCaTracker.exe --data-folder C:\Temp\ca-tracker-test
```

## First run

1. Run `windows-helper\dist\SmcCaTracker.exe`.
2. **Settings → Connections → Sign in to Helix / Sign in to Procore** (or the banner's buttons): opens
   the site in CA Tracker's own browser window. Sign in there once; every sync uses that sign-in. For Helix it also
   lists the projects you're assigned to, so you can pick them in step 4. For Procore, as soon as you're
   signed in CA Tracker finds **every project you can see in every company you belong to** — including
   projects hosted by other companies (GCs, owners) — and syncs them all in the background. No links
   to paste, no switching companies, and nothing to install in anyone's Procore account. Each
   connection shows who you're signed in as and when it last synced.
3. **New project…**: choose the project's CA folder (e.g. `Z:\Shared\Projects\CMH04\CA`). The name
   defaults to the folder above `CA`.
4. **Link…** (beside "Linked to" on the project card): pick the project from the list (Procore projects are already listed after step 2), or paste its link. **Open Helix/Procore to copy a
   link** opens the site in your usual browser so you can find the project:
   - Helix: the project's Overview page, e.g. `https://project.helixteam.com/Projects/<id>/Overview`
   - Procore: the project's RFIs tool, e.g. `https://app.procore.com/webclients/host/companies/<company>/projects/<project>/tools/rfis`
5. **Sync from Helix / Procore** on the project card (F5). If a sign-in has expired, a banner under
   the header says so, with a button to sign in again.

The built-in browser uses the Microsoft Edge WebView2 Runtime, which comes with Windows 11. It saves
the Helix and Procore passwords you sign in with, encrypted for your Windows user (Windows DPAPI, the
same protection Edge uses), and fills them in when a site asks you to sign in again — Procore ends
its sign-in when CA Tracker closes, so after a restart you only click **Sign in**.
**Settings → Connections → Sign out of Helix and Procore** ends both sign-ins (cookies, site storage
and cache are cleared) but keeps the saved usernames and passwords, so signing in again fills them
in. It closes the item panel and the hidden sync pages first, and asks you to close any CA Tracker
browser, download or sign-in window that is still open. **Sign out and forget saved passwords** does
the same and also deletes the usernames and passwords saved in CA Tracker's browser.

## Project list

The list on the left has two sections:

- **With a CA folder**: projects you track. Each compares its Helix/Procore items with its CA folder.
- **No CA folder yet**: every Helix or Procore project CA Tracker has seen (from signing in to Helix,
  from looking through your Procore companies, and from syncs) that isn't tracked yet. Procore
  ones are synced in the background like tracked ones, so their items show without a CA folder; the
  subtitle names the company hosting each one. Select one and choose **Choose CA folder…** to start
  tracking it; it moves up into the first section, already linked.

**Find a project** (Ctrl+F) above the list narrows it by name or hosting company; Esc clears it.

**Right-click** a project, or use **⋯** on the project card, for Favorites, **Archive** (hide it and
stop syncing it, for you only) and, for Procore projects, **Stop syncing** (closed jobs, projects you
don't work on). Those stay in the list, dimmed, marked *sync turned off*; **Turn syncing back on**
undoes it. A project that couldn't be read is marked *⚠ last sync failed* (hover for why — usually no
access to that project's RFIs or Submittals) and the rest still sync.

If a Base Build or Tenant Fit Out project is set to *only include folders whose name says this part*,
folders without BB/TFO in their name are left out, and a note above the list says how many, with an
**Include them…** button.

## The number next to each project

Each project in the list shows how many items match the current filters, e.g. **10**, after a red
**1 overdue** when some of them are overdue. Both count only items that match the filters: the type
(Both, RFIs, Submittals), Show, checked people, due filter and discipline. Filter to one
person and every project shows how many items that person has there. Projects you aren't viewing are
counted in the background from their last sync; a project with nothing matching shows no number.

## Several projects at once

Ctrl-click or Shift-click projects in the list to show all their items together. The header then
describes the project clicked last, and **Sync N projects** syncs each selected project in turn.
The actions below the list (Move to Returned, Move back to active, Set discipline, Download) work on
checked items from any of the shown projects, each against its own CA folder. Their labels say how
many items they act on; the count beside them says when some checked items are hidden by the filters.

- **Both / RFIs / Submittals** choose the item types; Both shows RFIs and Submittals in one list.
- **Group by** adds headings: *Project, then type* (default), *Project*, *RFIs / Submittals*, or
  *Nothing* for one flat list.
- **Sorting** applies within each heading. Click any column heading to sort by it (click again to
  reverse), or use **Sort by** for the common orders (it shows the heading you sorted by, e.g.
  "Title ↑"). Empty values (no due date, no days in court) always go last.
- **Columns**: right-click any column heading to turn columns on or off (remembered). The due date
  starts hidden; hover **Days till due** for it.
- **Column widths** fit the longest text in each column (every row, not just those on screen), so
  titles and names show in full. If the window is too narrow, only Title, Ball in court, Folder and the
  other long-text columns shorten (hover for the full text); anything still too wide scrolls sideways.
  The check box and each row's **Folder**, **Open Online**, **Download** and **Verify** links stay in place (the same
  actions, and Set discipline, are on right-click).

## Items: filter and sort

The main list shows the selected project's RFIs or Submittals from Helix or Procore; the CA folder
is matched alongside. The tabs show how many are open.

- **Show**: *Open items* (not closed), *All items*, or one of the folder views: *Needs attention*,
  *Ready to return*, *No folder* and *In Returned folder*.
- **Ball in court**: who has the item now (Helix "In court", Procore "Ball In Court"). Check as many people
  as you like; *Anyone at <your company>* covers everyone at your company, and **Everyone** clears
  the checks.
- **Due**: *Overdue*, *Due today*, or *Due within # days* (today through # days from now). The due date
  is the earliest of the due, requested review and contractual review dates.
- **Discipline**: from the folder name (shown as "(auto)"), or set by hand.
- **Sort by**: Number, Ball in court (A–Z), Days in court (longest first) or Days till due (soonest
  first). Column headers sort too.

When nothing matches, **Clear filters** goes back to open items, everyone, any date and every discipline.

**Days in court** counts from the day the item reached the person who has it now. Helix doesn't store
that date, so each sync works it out from the item's saved versions in Helix: the first save after
which its status, "waiting on" text and current review step have stayed as they are now. If Helix has
no saved versions for an item, the review workflow is used instead (the day the previous reviewer
finished, or the review date); if neither says, the cell is left empty rather than guessed. Procore's
list doesn't say how long someone has had an item, so it is empty for Procore. Sync again after
updating so the new fields are read.

## Opening an item

**Open Online** on a row (or double-clicking a row that has no folder yet) opens that exact RFI or
Submittal; double-clicking a row with a folder opens the folder. Helix items open in the **item panel** beside the
list (drag the bar between them to resize): Helix has no web address for a single item, so the panel
opens the project's RFI or Submittal list in CA Tracker's browser (already signed in) and then the item
in Helix's detail viewer; you work on it there as in Helix. Opening another item reuses the panel, so it
opens quickly. Procore items open in your usual browser at the item's own page.

While a Helix item is open in the panel, CA Tracker checks it every few seconds. When you send it on
(or answer, close or void it) and Helix saves the change, CA Tracker re-reads that project's RFIs or
Submittals and updates the list by itself — the item leaves your court (and your person filter) without
a sync. The panel's status line says where the item went. Esc closes the panel. Closing the item (or opening another)
re-reads the project once more, so changes you made to other items in the panel are picked up too; the
panel closes at once and that re-read finishes behind it.
Procore items update at the next background sync.

## Test cases from real projects

When CA Tracker gets whose court an item is in wrong, **Settings → General → Troubleshooting →
Developer tools → Export a Helix test sample** saves
an anonymized copy of the selected Helix project's items to `%LOCALAPPDATA%\SMC-CA-Tracker\exports`:
people become "Person 1, 2…", companies "Company A…", titles "Item <number>". The file lists what
CA Tracker works out for each item under `expected`; correct the wrong ones and add the file to
`tests/Scripts/fixtures` — every later change is then checked against it.

## Downloading an RFI or Submittal

**Download** on an item's row (Helix items) opens the download window for that item; **Download…** at
the bottom of the list opens it for every checked item at once. Each item is a section with where its
files go, a status (`3 new files`, `New folder · 6 files`, `Up to date`, `Saved 3 files`…) and its
Details PDF and current attachments, with each file's name and upload date. The items are read from
Helix one after another in the background (one hidden Helix page serves them all); you can check and
uncheck files while the rest are read, and an item with nothing new stays folded away. Its box checks
or unchecks all of its files; **New files only**, **Check all** and **Uncheck all** apply to every item.
Each item's files go into a folder named `<number> - <name>` (e.g. `P000210 - Overhead Coiling Doors`),
with a leading project name or code taken off the name:

- If the item already has a folder, that folder is used. If the item's title names a revision
  (`Rev 2`, `Rev. 2`, `Revision 2` or `R2`), the files go into an `R2` subfolder of it instead,
  reusing an existing `R2`/`R02`/`Rev 2` subfolder. The last revision in the title counts, and `R0`
  counts too. Only that subfolder is checked for files already saved, so a file named like one from
  an earlier revision is still downloaded.
- Otherwise a new folder is made in the project's RFI or Submittal folder.
- **Change…** picks somewhere else (needed for projects without a CA folder).

Nothing is written until you select **Download**, which saves every checked file of every item, item by
item, with a progress bar (an item still being read is downloaded with its new files when its turn
comes). **Stop** finishes the file being saved and stops. A file that fails doesn't stop the rest: it
stays checked, so **Download** tries it again, and the window ends with what was saved where.

**Verify** on a row (or **Verify folders…** at the bottom for the checked items) opens the same window to
check that each item's folder has every file Helix has for it — attachments and the Details PDF, a file
saved with a date in front or as a copy counting as there. Each item says `All 7 files in folder`,
`3 of 7 missing` or `No folder yet`, the missing files start checked (whatever their kind), and the
window sums up how many folders are complete; **Download** then gets just what is missing.

For a folder that already exists, only PDFs not in it yet start checked (items added since); files
already in the folder start unchecked and are never replaced; a checked duplicate is saved as
`name (2).pdf`. PDFs get their upload date in front of the name (`2026-08-03 Review markup.pdf`), and
the Details PDF is created by Helix's own export. Each saved file is added to `journal.jsonl`. Procore
downloads aren't supported yet.

## Settings

**Settings** (top right) replaces the project list with the Settings page (the button then reads
**← Projects**). Its sidebar has:

- **General settings**: appearance (saved as soon as you pick it), default folder names, the returned
  rule, background sync and troubleshooting (the log folder). Other changes apply when you select
  **Save**; **Discard changes** goes back to the saved values. Fields are checked as you type, and
  leaving with unsaved changes asks whether to save them.
- **Connections**: Helix and Procore, each with a status badge (Syncing, Synced, N projects not read,
  Sign-in needed, Not connected), when it last synced, how many projects it has (and how many are
  archived or set to not sync), a progress bar while it syncs, and **Sign in** / **Sync now** buttons;
  **Sign out of Helix and Procore** (saved passwords kept) and **Sign out and forget saved
  passwords**; and the optional **Workload Tracker**: its address, **Connect…** (signs in to the tracker
  in a window and receives a token that only reaches the CA Tracker endpoints, stored encrypted for your
  Windows user), **Send now** and **Disconnect…** (revokes this PC's connection). Once connected, what
  you read from Helix and Procore is sent every minute while CA Tracker is open: items of projects set
  up in the app, counts only for the others. CA folder locations and files stay on this PC.

**← Back to projects** (or **← Projects**) returns to the project list.

## Sync

**Sync all** (top right) reads every project of each signed-in site in the background (no window),
Helix then Procore, except projects set to not sync; with nothing signed in it opens Connections. Its
**▾** syncs one site or opens Connections. For Procore it first looks through every company you belong
to for new projects. A project that can't be read is marked and the rest carry on; while it runs the
button reads **Stop syncing**, which finishes the current project and skips the rest. Progress shows
above the project list and on each Connections card. A connection's last sync is the most recent read
of any of its projects. While the app is open, tracked Helix projects and every Procore project
(except those set to not sync) also sync by themselves every 15 minutes, and your Procore companies
are looked through again for new projects every 6 hours (Settings → Background sync).

## Syncing one project

**Sync from Helix / Procore** on the project card (F5) always re-lists the CA folder. For a linked
project it also reads the project:

- **Helix:** in the background, on the hidden Helix page — no window opens. Syncing one project reads all of the
  project's items; background syncs fetch only items changed since the last read (plus a quick list of
  ids, so deleted or moved items drop out). If Helix needs you to sign in again, the banner and
  Connections say so within a few seconds, and background syncs pause until you sign in again.
- **Procore:** in the background too, reading **every** RFI and Submittal (not just the rows a list
  shows), with your Procore sign-in: a hidden Procore page asks Procore's own REST service
  for the lists, as Procore's web pages do. It sees exactly what you can see in Procore — across every
  company you belong to — and nothing has to be installed in any company's Procore account. A tool
  you can't see on a project (say, Submittals) is left as it was rather than emptied. If Procore
  refuses the page way for your account, the sync falls back to reading the RFIs and Submittals lists
  in the CA Tracker browser window, as before; those reads only see the rows Procore's lists show.
**Settings → Connections → Sign out of Helix and Procore** ends the sign-in (saved passwords stay).

The project card shows **when the Helix/Procore data was last synced** and **when the CA folder was
last rescanned** (a failed sync says why, in full). While a sync runs it shows progress, and you can
cancel it.

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

Folders can also be grouped by discipline and by Base Build / Tenant Fit Out, at any depth:

```
CA\
  Submittals\
    Elec\
      BB\
        P000210 - Overhead Coiling Doors\
      TFO\
      ~Returned\
        P000100 - Switchgear\
    Mech\
```

A folder whose whole name is a discipline (Elec, Electrical, Mech, Fire, ICT, Plbg, Plumbing,
Security) or a part (BB, TFO, Base Build, Tenant Fit Out, …) is looked inside rather than treated as an
item. The folders an item sits in set its discipline and part, so `Elec\BB\P000210 - …` counts as
Electrical and Base Build without either word in its own name. A `~Returned` folder at any level
counts as returned, and so does one with a note after the name, such as `~Returned (Base Build)` or
`~Returned - TFO` (the note also sets the part).

- **Mark as returned** moves a folder into the nearest `~Returned` above it (for `Elec\BB\…` that's
  `Elec\~Returned`), keeping its `BB`/`TFO` folder (`Elec\~Returned\BB`, created if missing and
  shown in the preview) so it stays with its part. If there's no `~Returned` anywhere above, one is
  created in `RFIs`/`Submittals`.
- **Move back** returns it beside that `~Returned`, into `BB`/`TFO` when that exists.
- **Download** puts a new item folder into the matching discipline and part folder (e.g.
  `Submittals\Elec\BB`) when the discipline can be told from the item title; otherwise into
  `Submittals`, and **Change…** picks another.

Folders are matched to items by the number at the start of the name (the text before ` - `).
If that doesn't match exactly, a looser match ignores `RFI`/`SUB` prefixes, punctuation and leading
zeros, but only when exactly one item fits. Only folder names and dates are read, so Egnyte
online-only files are not downloaded.

Procore keeps each revision of a submittal as its own item with the same number. The folder is judged
by the newest revision (shown as `03 30 00-1 Rev 2`), and older revisions aren't listed separately.

## Base Build and Tenant Fit Out

A job split into Base Build (BB) and Tenant Fit Out (TFO) is set up as two projects, for example
"CMH04 BB" and "CMH04 TFO". Each has its own sync, timestamps and list. In **Edit project → Base
Build / Tenant Fit Out**, choose the part, then pick how the two are separated:

- **Separate Helix/Procore projects or separate CA folders:** connect or choose each project's own.
  No box needs to be checked.
- **BB and TFO subfolders** (e.g. `CA\BB\RFIs`): use **Browse** under Folder names.
- **One Helix/Procore project for both:** check *only include items whose title, number or contract
  says this part*.
- **One set of CA folders for both:** check *only include folders whose name says this part*.
- Check *also include items and folders that mention neither* if unlabelled ones belong to this part.

The words recognised are BB, Base Build and Base Building for Base Build, and TFO, Tenant Fit Out,
Fit-Out and Tenant Improvement for Tenant Fit Out. Two projects can share a connection or CA folder
only when they are set to different parts. Choosing a CA folder that another project already uses
offers to create the other part automatically.

## Disciplines

Each item folder has a discipline: **Electrical** (written `Elec` in folder names), **Fire**, **ICT**,
**Mechanical** (`Mech`), **Plbg** or **Security**. It is detected from whole words in the folder name,
for example `P000210 - Elec - Lighting` gives Electrical, and `Electrical`, `Mechanical` and `Plumbing`
are recognised too. Items with no folder yet are detected from their title. Detected disciplines show
in italics.

Use **Set** in the Discipline column, or check folders and choose **Set discipline…**, to pick one by
hand, mark a folder as having none, or go back to detecting it. Choices are saved in the tracker per
folder name, so they follow a folder into `~Returned`. Folders are never renamed. The **Discipline**
filter above the list shows one discipline at a time.

**Presetting before a download.** Items with no folder yet can be checked too. The box in the list's
heading checks every item shown (click again to uncheck them all). Choose **Set discipline…** (or **Set**
on one row) and the choice is saved for those items. **Download** then puts the new
`<number> - <name>` folder inside that discipline's folder (e.g. `Submittals\Mech`, or `Submittals\Mech\BB`
for a Base Build project). If the Submittal or RFI folder is split by discipline but has no folder for
that one yet, a new one (e.g. `Mech`) is suggested; the download window shows the full path before
anything is created. The choice stays with the item after its folder exists.

## What "returned" means

By default an item is returned when **no one from the selected company holds it**, including when
it is closed. The company defaults to the one on your Helix login and can be changed per project in
**Edit project**.
"Holds" uses Helix's review order: whoever the item is with now (In court) or next (Coming up). Settings can also:

- count items where your company only has a later step (coming up) as returned
- stop counting closed items
- list statuses that always, or never, count as returned

Procore's background read gives each item's Ball In Court people with their company, so Procore items
follow the same rule. When Procore is read through its list page instead, the Ball In Court column
has only names, so those items count as returned only when they are closed (or match a status list).
Folders missing from a partial Procore view are shown as "Not in last Procore view", never as missing.

## Results

The **Folder status** column:

| Result | Meaning |
|---|---|
| Ready to return | Returned per the rule, but the folder is still active |
| Back in court — folder is in Returned | In `~Returned`, but the item is back with your company |
| More than one folder / Folder matches several items | Needs a person to sort out; excluded from suggestions |
| Not in Helix/Procore | A complete Helix or Procore read has no item with this number |
| No folder | The item exists but has no folder yet |
| Open / Returned | Everything lines up |

## Changes always need a preview

**Mark checked as returned…**, **Move checked back to active…** and **Create missing folders…** all
open a preview listing every folder that would be created or moved, with its full From and To paths.
Anything that can't be done safely (the destination already exists, the folder has moved, two folders
would land in the same place) is shown in red and skipped. Cancel is the default button. After you
apply, each step is re-checked just before it runs. A results window shows what happened, and every
step is appended to `%LOCALAPPDATA%\SMC-CA-Tracker\journal.jsonl`.

Creating, editing, connecting or removing a project, and changing settings, only update the app's
own settings file. They don't change anything in the CA folder.

## Upgrading from the Chrome extension version

Version 2.0 no longer uses the SMC-CA-Tracker Chrome extension. On first start it removes the
extension's Windows registration (for your user only) and its files in `%LOCALAPPDATA%\SMC-CA-Tracker`.
Your projects, folder settings and disciplines are kept. You can remove the extension from
`chrome://extensions`.

## Files

| Path | What |
|---|---|
| `%LOCALAPPDATA%\SMC-CA-Tracker\tracker.db` | The database: projects, items, CA folder index, settings, last view, sign-ins, sync history, change log (see above) |
| `%LOCALAPPDATA%\SMC-CA-Tracker\logs\` | Timings and errors, one file per day (kept two weeks) |
| `%LOCALAPPDATA%\SMC-CA-Tracker\refresh\*.json` | Procore refresh requests (cleaned up after a day) |
| `%LOCALAPPDATA%\SMC-CA-Tracker\browser\` | The built-in browser's profile (keeps you signed in) |
| `%LOCALAPPDATA%\SMC-CA-Tracker\imported-json\` | The JSON files of earlier versions, after they were imported |
| `%LOCALAPPDATA%\SMC-CA-Tracker\journal.jsonl` | Every folder change that was applied |
