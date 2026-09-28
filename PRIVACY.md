# TaskHub Helper privacy

TaskHub Helper activates only on its declared Helix and Procore application routes. The two provider
adapters share rendering, filtering, cache limits, and Task Hub authentication but do not share
provider credentials or page bridges.

My Helix Activity reads Helix data only through the active Helix browser tab and processes it locally in Chrome. When you select **Open in Helix**, it reads a fresh copy of that requested item so Helix's own detail viewer receives a complete record; this copy is not added to the extension's persistent cache. When you explicitly select **Download package**, it reads the requested item's current attachment metadata and available Details-report metadata, then asks Helix for permission-checked, short-lived attachment URLs.

It reads the current Helix directory identity and current-project activity needed for **My activity**. For **Open queue**, it also reads active project assignments from Helix's project directory, followed by open RFI and Submittal metadata within those assigned projects. This information is used only to render the local sidebar and its project/type filters.

The extension does not collect analytics or inspect Helix credentials or authentication storage. A
**Download package** request prompts the user to choose a destination folder, reads Helix's protected
attachment URLs, and opens an inactive, same-origin Helix report tab long enough to render the official
Details PDF. The extension captures Helix's client-rendered or paginated PDF save operation and closes
the temporary report tab after success or failure. The files are written into an automatically named item folder inside the selected parent
folder. The generated folder handle remains only in page memory so the user can select **Browse
folder** after a successful download; it is discarded when that card or page closes. The URLs, handles,
and file contents are not stored in extension storage or sent to Task
Hub. The extension provides Chrome with a project-specific picker identifier so Chrome can remember a
separate last-used starting location for each project. Other Helix data is not transmitted unless the
user explicitly connects a self-hosted Task Hub.

When Task Hub is connected, a fresh scan can send the current selected-person **In court** queue to
that configured server. Each item includes its Helix item/project identifiers and names, RFI or
Submittal type and number, title, status, responsible text, created/modified/due dates, selected
current assignee names/company/person identifiers, the derived court-start date and its derivation
source, and a link back to the Helix item. Comments, attachment contents, activity history, cookies,
and Helix login tokens are not sent. A scan containing Helix read warnings is marked incomplete so it
cannot archive missing Task Hub tasks.

Connecting is optional and requires a runtime Chrome host permission for the chosen Task Hub origin.
Non-local servers must use HTTPS. Sign-in uses the deployment's TaskHub ID provider through OIDC
Authorization Code + PKCE, so the extension never receives the user's password. Revocable access and
refresh tokens are stored in Chrome extension-local storage, restricted to trusted extension
contexts, rotated automatically, and revoked when possible before being removed on disconnect.

Successful scans are saved as a sanitized cache in Chrome's extension-local storage, scoped to the logged-in contact. Queue data is reused across supported pages and activity data is cached per project and feature. A cache is treated as fresh for 15 minutes; **Refresh** replaces it immediately. Cross-page opening stores a two-minute navigation handoff containing only the item, type, and project identifiers; it is consumed on the matching destination page. Package downloads remain in the current tab so the selected folder handle never needs to be persisted. Chrome may retain older cache entries until they are replaced, extension storage is cleared, or the extension is uninstalled. Removing or disabling the extension stops all processing.

For Procore, the content script reads the company/project identifiers and RFI or Submittal rows that
Procore has rendered visibly in its current grid. It uses this data locally so the sidebar works
standalone. It does not read Procore cookies, authentication headers, browser local/session storage,
network traffic, or private API tokens. Without Task Hub, visible item data stays in page memory.

When the user explicitly selects **Discover companies & projects**, the extension opens Procore's
normal company and project selectors, switches through the invited companies shown there, and stores
a sanitized catalog in Chrome extension-local storage. The catalog contains company and project IDs
and names, safe Procore project-home links, discovery timestamps/status, and resumable scan progress.
It does not contain RFI/Submittal records, comments, attachments, cookies, or credentials. The scan
restores the original company and page after completion or cancellation. The local catalog remains
until replaced by another discovery scan, extension storage is cleared, or the extension is removed.

If Task Hub is connected, the extension may send a sanitized visible-page snapshot containing the
company/project/item identifiers and names, item type/number/title/status, due date, company name,
observation time, and the Procore item link. Visible snapshots are always marked incomplete, so items
missing from a filtered or paged Procore grid cannot archive Task Hub tasks.

An additional Procore API connection is optional. Task Hub performs Procore Authorization Code OAuth
on the user's behalf; the Procore client secret and encrypted refresh token remain on the Task Hub
server and are never returned to Chrome.

Task Hub requests only the connected user's identity, accessible active projects, and open RFI and
Submittal records filtered to that user's Ball In Court. Sanitized Procore queue data returned to the
extension includes company/project/item identifiers and names, item type/number/title/status,
responsible names, created/modified/due dates, assignment identity/name/company, and an item link.
The extension caches that API-rendered queue for 15 minutes under a provider/company/project/user-scoped key,
within the same 24-hour and 5 MB bounds as Helix data. It does not receive attachments, comments,
Procore access tokens, or refresh tokens.

To preserve the panel when Procore opens a legacy project detail URL that omits the company ID, the
extension stores a bounded mapping of up to 200 TaskHub-user/project/company identifiers learned from
modern Procore URLs and API results. It contains no record titles or content and is removed for that
user on Procore disconnect, or entirely on Task Hub disconnect.

Disconnecting Procore deletes Task Hub's stored encrypted refresh token. Because the current backend
does not call an upstream Procore revocation endpoint, administrators or users should also revoke the
app grant in Procore when immediate upstream invalidation is required. Disconnecting does not erase
the separately managed activity cache; use **Clear cached activity data** when that local copy should
also be removed.

The extension also stores the sidebar width, whether it was open or hidden, the selected My activity/Open queue page, and the active search, sort, status, project, person, item-type, activity, timing, and due-date filters. Filter state is scoped to the logged-in contact and can include search text plus Helix project and person identifiers, but not complete Helix records.

Disconnecting Task Hub stops future syncs and removes the local Task Hub URL and login tokens. Local
Helix and visible-page Procore activity continue to work. Disconnecting does not delete provider tasks
already synced to the self-hosted Task Hub.
