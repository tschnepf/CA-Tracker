# SMC CA Tracker

A Windows app that tracks each project's RFIs and Submittals against its CA folder on the Egnyte
drive. The app does everything itself:

- **Projects** are defined in the app by choosing a CA folder, split into Base Build / Tenant Fit Out
  when needed.
- **Connect** links a project to one Helix or Procore project.
- **Refresh** opens that project in the app's own built-in browser window, reads its RFIs and
  Submittals (read-only), and shows when it was last refreshed. You sign in to Helix or Procore in
  that window once; it remembers you.
- The list compares the items with the CA folder. Folders for returned items can be moved into
  `~Returned`, always after a preview you can cancel.

No Chrome extension or other browser add-in is needed.

## Repository

| Path | What |
|---|---|
| `windows-helper/src/SmcCaTracker.App` | The WPF app (built-in browser, windows, view models) |
| `windows-helper/src/SmcCaTracker.App/Scripts` | Read-only scan scripts the built-in browser runs inside Helix and Procore pages |
| `windows-helper/src/SmcCaTracker.Core` | Folder matching, comparison, returned rule, previews and folder moves, settings, Procore item parsing, sending to Workload Tracker |
| `windows-helper/src/SmcCaTracker.Data` | The app's SQLite database (`tracker.db`), including the Workload Tracker outbox |
| `windows-helper/tests` | .NET tests, and Node tests for the scan scripts |
| `windows-helper/installer`, `windows-helper/company` | The installer and the company settings file |
| `windows-helper/contract`, `windows-helper/tools` | The pinned Workload Tracker schema, sample bodies and the tool that generates the DTOs from it |

```powershell
powershell -ExecutionPolicy Bypass -File windows-helper\build.ps1   # test and build into windows-helper\dist
npm test                                                            # scan-script tests (Node 22+)
```

See [`windows-helper/README.md`](windows-helper/README.md) for setup and how everything works.

## Privacy

The app reads Helix and Procore only through the pages you are signed in to in its browser window,
using the same requests those pages make, so it sees only what you can see. For Procore that includes
listing the companies you belong to and their projects, so projects hosted by other companies are
found without anything being installed in their Procore accounts. It stores item numbers, titles, statuses, dates and who
currently holds each item in `%LOCALAPPDATA%\SMC-CA-Tracker` on your PC. It never reads files,
comments or passwords. It sends nothing to any other server unless you connect it to your company's
Workload Tracker (Settings → Connections): it then sends that tracker the items it reads (numbers,
titles, statuses, due dates and who holds each item; never files, attachments or comments), and only
counts for projects you haven't set up in the app. Your Helix/Procore sign-in is kept in
the browser window's profile folder, `%LOCALAPPDATA%\SMC-CA-Tracker\browser`; **Settings → Sign out
of the built-in browser** deletes it.
