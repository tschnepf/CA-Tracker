# My Helix Activity

A dependency-free Chrome Manifest V3 extension that finds RFIs and Submittals associated with the user currently logged into Helix. It works from project Overview, RFI, and Submittals pages. It does not contain a user ID or project ID; both are read from the active Helix login and page.

## What it finds

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

Successful results are cached in Chrome's extension-local storage for 15 minutes, scoped to the logged-in contact. The queue is reused across supported Helix project pages, while activity results are cached per project and feature. Use **Refresh** whenever an immediate rebuild is needed.

## Install in Chrome

Run the packaging script once to create the permanent unpacked folder:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package-extension.ps1
```

1. Open `chrome://extensions`.
2. Enable **Developer mode**.
3. Select **Load unpacked**.
4. Choose this folder once: `C:\Users\tim\source\HelixHelper\dist\helix-my-activity`.
5. Open a Helix project Overview, RFI, or Submittals page and refresh it once.
6. Select the **My Helix Activity** launcher in the lower-right corner.

Each person installs the same folder/package. The extension automatically uses that person's active Helix login, current project, and current supported feature page.

For every future update, run the same packaging command. It replaces the contents of the permanent `dist\helix-my-activity` folder without changing its path. Then open `chrome://extensions` and select **Reload** on the existing extension. Do not select **Load unpacked** again.

The script also produces `dist\helix-my-activity.zip` for a stable-name distribution package and a versioned archive for release history. A recipient should always extract `helix-my-activity.zip` over the contents of one permanent folder, then select **Reload** in Chrome.

If older versions were loaded from separate folders, remove those duplicates from `chrome://extensions` after the permanent folder is installed.

## Read-only and privacy guarantees

- The extension has only `scripting` and local `storage` permissions, plus access to `project.helixteam.com`.
- It calls Helix's existing `readFromCollection` helper through a fixed operation bridge.
- It contains no save, upload, delete, raw `fetch`, or `XMLHttpRequest` implementation.
- The bridge cannot accept arbitrary collection names or arbitrary queries from the content script.
- It does not read cookies, authentication tokens, attachment contents, or local files.
- It stores a sanitized 15-minute cache in Chrome's extension-local storage. The cache is not sent anywhere else and is removed when extension storage is cleared or the extension is uninstalled.
- Sidebar width and open/hidden preferences are stored locally; they contain no Helix record data.
- **Open in Helix** opens Helix's existing detail viewer. For another project or item type, a short-lived local handoff carries only the item, type, and project identifiers to the destination page so it can open the same read-only detail viewer.

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
