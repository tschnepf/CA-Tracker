# TaskHub Helper for Helix and Procore

A dependency-free Chrome Manifest V3 extension with one stable extension ID and two site adapters. It activates **My Helix Activity** on supported Helix pages and **My Procore Activity** on supported Procore company/project tool pages. Shared UI, filtering, bounded caching, Task Hub login, and sync infrastructure are reused; provider-specific data access remains isolated.

Helix uses the existing read-only page bridge. Procore works standalone by reading the work-item rows
rendered on the current Procore page. An optional Task Hub connection can sync those sanitized rows,
and an optional server-side Procore OAuth connection enables broader official REST API scans. The
extension never reads Procore cookies, authentication storage, headers, or private page tokens.
Existing Helix Helper installations upgrade in place because the manifest key, extension ID, legacy
storage keys, and stable package paths are preserved.

## Repository

This is the **SMC-CA-Tracker** repository. The extension was extracted from
`extensions/HelixHelper` in the AnotherTaskManager (Task Hub) repository with its
git history preserved, so it can be developed and released independently.

```sh
npm run check     # validate the manifest and run the unit tests
npm run package   # check, then build dist/helix-my-activity.zip
```

Some validation checks confirm the extension still matches the Task Hub server
configuration (Keycloak realm export, `.env` examples, and web packaging). Those
files live in AnotherTaskManager. They are found automatically when that repository
is checked out next to this one (for example `C:\Source\AnotherTaskManager`), or
you can point at it explicitly:

```powershell
$env:TASKHUB_REPO_ROOT = "C:\Source\AnotherTaskManager"; npm run check
```

When no server checkout is found, those checks are skipped with a notice and the
rest of the suite still runs. The design notes are in
[`docs/unified-helix-procore-extension.md`](docs/unified-helix-procore-extension.md).

## What it finds in Helix

The sidebar combines the current project's records with the current user's comments, uploads, and item history. A result can match because the logged-in user:

- created the RFI or Submittal;
- authored the current or a historical RFI response;
- commented on it;
- uploaded an attachment;
- edited it;
- was assigned to, reviewed, advanced/accepted, or rejected it; or
- is configured to be notified when it completes.

Search, status filters, activity filters, and sorting all run locally after the index is built. On Overview, **My activity** can show RFIs, Submittals, or both. Submittal search also includes linked register numbers.

## Sidebar layout

- Drag the sidebar's left edge to resize it from 360 to 1,200 pixels. Use Left/Right Arrow on the resize handle for keyboard resizing, or double-click the handle to reset it.
- Wider sizes automatically reorganize item cards into compact multi-column rows so more information fits horizontally with less vertical scrolling.
- **Hide** moves the sidebar out of the way without clearing its index or stopping an in-progress scan. The launcher restores it immediately.
- The chosen width, open/hidden state, My activity/Open queue page, and all active search, sort, status, project, person, item-type, activity, timing, and due-date filters are stored locally and restored on supported Helix pages, including after Chrome restarts.

## Download packages

Select **Download package** on an RFI or Submittal card to download Helix's available **Details** PDF
and every current attachment without opening the detail viewer. Chrome first asks you to choose the
parent destination. Inside it, the extension creates a folder named `<item number> - <item name>`—for
example, `P000210 - Overhead Coiling Doors - Overhead Coiling - SD`. A matching leading project name
or project code is removed from the item name using alternative matches. For example, a Helix project
named `Aligned CMH02` removes either `Aligned CMH02 -` or `CMH02 -` from the beginning of the item
name. Repeated separators such as `--`, `- -`, and `-  -` are collapsed to one dash. Downloaded PDF
attachments are prefixed with their Helix upload date, for example `2026-08-03 Review markup.pdf`.
Other files preserve their Helix filenames, unsafe filename characters are sanitized, and a number is
added instead of overwriting an existing file. The generated Details PDF keeps its report filename
because it has no Helix upload date. Items from another assigned project can be downloaded without
navigating away.

The parent-folder picker has separate history for each Helix project, shared by that project's RFIs
and Submittals. On the first CMH04 download, select the CMH04 folder. Chrome then opens future CMH04
package pickers at that remembered starting location. Selecting a different folder updates the
remembered location for CMH04 only; other projects keep their own locations. Before a project has a
remembered location, its picker starts in Downloads.

After a package finishes, that card shows **Browse folder**. Selecting it reopens Chrome's native
directory chooser at the newly created item folder. This is browser-only browsing; it does not launch
Finder or Windows Explorer. The action remains available while that card and Helix page stay open.

The Details PDF is rendered by Helix in an inactive report tab. The extension starts the report's PDF
export, writes it to the generated item folder, and closes that temporary tab when the download
finishes. Deleted and archived attachments are not included; this matches Helix's default **Show
Archives: No** view. The selected parent-folder handle is used only for that click and is never saved in
extension storage; Chrome maintains the project-specific picker location.

Both Helix Details export implementations are supported: standard client-rendered PDFs and paginated
Kendo PDFs. The package is successful only after the official **Details (RFI)** or **Details
(Submittal)** printed copy has been saved alongside the attachments. If Helix cannot export that PDF,
the temporary report tab closes and the item card reports the failure instead of opening the report in
Chrome.

## Open queue

The **Open queue** view provides a separate work list alongside **My activity**. It first reads Helix's active **My Projects** assignments for the current contact, then reads open RFIs and Submittals within each assigned project. This means the project selector includes assigned projects even when they currently have no open items. It lets you combine:

- any number of projects;
- RFI, Submittal, or both item types;
- **Only me**, **All people in my company**, **All people**, or any custom combination of people found in open workflows;
- **In court now** (the default), **Current + future**, or **Future only** timing;
- any combination of **Overdue**, **Due today**, and **Next N days**, with an editable 1–365 day window;
- status and text filters; and
- priority, project, type, item number, or title sorting.

Each queue card shows its project and item type. Selecting **Open in Helix** keeps the sidebar open. If another Helix detail viewer is already open, the extension closes it through Helix's normal close flow before opening the requested item. The extension then reads a fresh, complete copy of the requested record and applies Helix's native page normalization before opening the detail viewer, so sections such as Review and Notification Workflow receive the same model shape as items opened from Helix's grid. When the item is in another project or feature, the extension navigates to the correct RFI or Submittals page and then opens that item's existing Helix detail viewer automatically. The navigation handoff expires after two minutes and never changes the record.

The people filter defaults to **Only me**, the exact logged-in contact. **All people in my company** selects the current user and every coworker found in open workflows by their Helix company identity. **All people** includes every person found in those workflows, and individual names can still be combined into a custom selection. The selected scope is restored after refreshes and Chrome restarts; company and all-people scopes automatically include newly discovered matching people.

Queue cards show each selected responsible person once, with their current timing such as **Tim Schnepf — in court**. A duplicate Responsible value from Helix is suppressed when it identifies the same person.

Due filtering uses the browser's local calendar day and checks all available Helix Due Date, Requested Review Date, and Contractual Review Date values. The selected ranges use OR logic, so **Overdue + Today + Next 7 days** includes any item matching at least one range. **Next N days** begins tomorrow; today remains a separate option. With no ranges selected, due dates are unrestricted. If an item has multiple applicable dates, any matching date includes it, and the earliest applicable date is shown as its target.

Queue classifications are:

- **In court** means the selected person owns the first unreviewed required workflow step while the item is in Review. It also includes a nonterminal item when Helix's Responsible message explicitly names that person.
- **Coming up** means the selected person has a later unreviewed required workflow step, or the item has not yet reached Review.
- Complete and voided items are excluded.

The launcher shows the open count for the selected people, projects, and types so pending work is visible without opening the panel.

Successful results are considered fresh in Chrome's extension-local storage for 15 minutes and are scoped to the logged-in contact. Each refresh replaces the cached open queue and retains only the three most recently accessed project/feature views. Cache entries expire after 24 hours and the extension enforces a 5 MB total cache budget without requesting unlimited storage. Use **Refresh** whenever an immediate rebuild is needed, or open extension settings to inspect and clear only the local Helix cache.

## Procore activity

On a supported Procore tool URL, the same panel immediately displays the RFI and Submittal rows
rendered in Procore's current grid, including **My Open Items**. This standalone mode needs neither a
Task Hub account nor a separate Procore authorization. Project, type, status, text, and due-date
filtering run locally through the shared activity UI. **Open in Procore** follows the displayed item's
Procore link. Only currently rendered rows are available in the item queue; change Procore's own
filters or paging to change that visible set.

Select **Discover companies & projects** to build a browser-local catalog of the companies in
Procore's company chooser and the accessible active projects in each company's project picker. The
scan switches through those companies one at a time, reports progress in the sidebar, survives a
normal Procore page reload, and restores the original company and page when it finishes or is
cancelled. The project filter then lists every discovered project, including projects with no items
in the current queue, using a **Company — Project** label. Re-run discovery whenever invitations or
project access changes. Discovery reads only the visible company and project selectors; it does not
read cookies, browser session storage, network traffic, or private Procore API credentials.

If broader scanning is wanted, connect Task Hub and then use the optional Procore OAuth connection.
The current project is used when present in the URL; a company-level API scan reads accessible active
projects, up to the configured safety limit. Official API mode displays open RFIs and Submittals where
the connected Procore user is currently in **Ball In Court**. Helix-only package downloads remain
hidden on Procore cards until an official API-backed package workflow is implemented.

To enable optional API-backed scans:

1. Connect Task Hub in extension settings.
2. Select **Connect Procore** and approve the Procore OAuth prompt.
3. Open a modern Procore company/project tool URL and select **My Procore Activity**.
4. Leave automatic sync enabled, or use **Sync** in the panel.

Task Hub calls `GET /rest/v1.0/me`, `GET /rest/v1.1/projects`, the v1.0 project RFI list, and the v1.1
project Submittal list. API warnings or truncated project scans make a result incomplete so missing
tasks are not archived. The current implementation uses user-delegated OAuth; it does not yet run a
company-wide unattended DMSA sync or subscribe to webhooks.

## Task Hub connection

Helix and Procore both work without Task Hub. Connect it only to send the current **In court now**
selection to a self-hosted Task Hub:

1. Open the extension's **Task Hub** settings from either sidebar or Chrome's extension details.
2. Enter the public HTTPS URL of the Task Hub deployment and select **Sign in with TaskHub ID**.
   Chrome opens the deployment's Keycloak login using OIDC Authorization Code + PKCE. Plain HTTP is
   accepted only for `localhost` or `127.0.0.1` development.
3. Approve Chrome's host permission for that one server.
4. Leave automatic sync enabled, or disable it and use **Sync** in either sidebar.
5. Choose **Only me**, a company scope, all people, or a custom set in the Open queue person filter.
   A fresh scan sends only the selected people whose assignment is currently **In court**.

Task Hub creates a **CA** subsection under **Work**, groups the RFIs and Submittals by provider project,
and shows the source added date, the best available date the item entered the selected person's court,
and the earliest available due/review date. Repeated syncs update the same tasks. A complete scan
archives synced items that are no longer in the selected current queue; a scan with Helix read
warnings never archives missing items. A visible-page Procore snapshot is always incomplete for the
same reason: records absent from a filtered or paged grid must never be treated as completed.

The extension never receives the TaskHub ID password. It stores revocable access/refresh tokens in
extension-local storage restricted to trusted extension contexts, rotates them automatically, and
revokes the refresh token when possible before removing it on disconnect. The optional connection
never changes Helix records. See [PRIVACY.md](PRIVACY.md) for the exact fields sent.

## CA Tracker Windows helper (optional)

The companion Windows app in [`windows-helper/`](windows-helper/README.md) compares each project's
Helix or Procore items with that project's Egnyte CA folder (default `CA\RFIs`, `CA\Submittals`,
each with a `~Returned` subfolder). It can move folders for returned items into `~Returned`, always
after a preview you can cancel.

The extension works exactly as before without it. To connect them:

1. Build and run the helper, then select **Settings → Register with Chrome** in the app.
2. In this extension's settings, select **Enable Windows helper**. This asks Chrome for the optional
   `nativeMessaging` permission, so updating the extension never disables it or adds a prompt.
3. After each fresh Helix scan, the extension sends the helper the current project's item list,
   including who holds each item and at which company. Procore sends its visible rows, marked
   partial.

See [PRIVACY.md](PRIVACY.md) for exactly what is sent.

## Install in Chrome

For a deployed Task Hub, open **Settings → Browser Extensions**, download the TaskHub Helper ZIP, extract it
into a permanent folder, and use that folder in the steps below. Task Hub also shows whether the
current account has connected or synced the extension.

For local development, run the portable packaging command once to validate the extension and create
the same ZIP plus the permanent unpacked folder:

```bash
npm run package
```

The existing Windows PowerShell entry point remains available:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package-extension.ps1
```

1. Open `chrome://extensions`.
2. Enable **Developer mode**.
3. Select **Load unpacked**.
4. Choose this folder once: `extensions\HelixHelper\dist\helix-my-activity` inside this repository.
5. Open a supported Helix or Procore tool page and refresh it once.
6. Select the matching **My Helix Activity** or **My Procore Activity** launcher.

Each person installs the same folder/package. The extension automatically chooses its adapter from the active site; no manual provider switch is required.

For every future update, run the same packaging command. It replaces the contents of the permanent `dist\helix-my-activity` folder without changing its path. Then open `chrome://extensions` and select **Reload** on the existing extension. Do not select **Load unpacked** again.

The script also produces `dist\helix-my-activity.zip` for a stable-name distribution package and a versioned archive for release history. A recipient should always extract `helix-my-activity.zip` over the contents of one permanent folder, then select **Reload** in Chrome.

If older versions were loaded from separate folders, remove those duplicates from `chrome://extensions` after the permanent folder is installed.

## Read-only and privacy guarantees

- The extension has only `scripting`, local `storage`, and OIDC `identity` permissions. Its content
  scripts are limited to supported Helix routes and Procore `/webclients/host/` or legacy
  `/{project_id}/project/` routes. Access to a Task Hub host is optional and requested only when
  connecting.
- It calls Helix's existing `readFromCollection` and protected-download helpers through a fixed
  operation bridge.
- It contains no Helix save, upload, or delete implementation. Network reads used by an explicit
  package download are limited to Helix-provided HTTPS attachment and report URLs.
- The bridge cannot accept arbitrary collection names or arbitrary queries from the content script.
- It does not read cookies, authentication tokens, or local files. Attachment URLs are resolved only
  after an explicit **Download package** selection, used to write the requested files into the
  user-selected folder, and are not cached.
- It stores a sanitized, bounded local cache in Chrome's extension-local storage. When Task Hub is
  connected, the selected current-assignment subset is sent only to that configured host. Without a
  connection, the cache is not sent anywhere.
- Sidebar width and open/hidden preferences are stored locally; they contain no Helix record data.
- **Open in Helix** opens Helix's existing detail viewer. For another project or item type, a short-lived local handoff carries only the item, type, and project identifiers to the destination page so it can open the same read-only detail viewer.
- Procore credentials never enter page or content-script storage. Task Hub stores the refresh token
  encrypted with `TASKHUB_FIELD_ENCRYPTION_KEY`; the extension receives only sanitized work items.

Helix itself remains responsible for access control. The extension uses the same active project-directory assignments as Helix's **My Projects** page and can only read records the logged-in user is already permitted to read.

## Development and verification

No dependency installation is required. With Node.js 18 or later:

```powershell
node scripts/validate-extension.js
node --test tests/*.test.js
```

Or run both through the package script:

```powershell
npm run check
```

For a visual preview without Helix, open `test/ui-preview.html` in a browser.

## Compatibility notes

The extension targets the current Helix Overview, RFI, and Submittals implementations and their `UserInfo.directoryInfo`, `Master.readFromCollection`, and `Master.loadDetailView` page interfaces. If Helix changes those internal interfaces, the sidebar reports an error instead of falling back to credential extraction or direct API calls.

The Procore adapter targets modern `/webclients/host/companies/.../tools/...` pages and legacy
`/{project_id}/project/...` detail routes. A modern route establishes a bounded, user-scoped mapping
from project ID to company ID so the panel can remain available after **Open in Procore** reaches a
legacy detail URL. If no safe mapping exists yet, the panel asks the user to open a modern company or
project tool once. API failures are reported without falling back to DOM scraping or browser-session
credential extraction.
