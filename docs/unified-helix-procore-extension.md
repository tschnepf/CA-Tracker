# Unified Helix and Procore browser extension

## Product goal

Ship one Manifest V3 Chrome extension, branded **SMC-CA-Tracker** (originally TaskHub Helper), that activates only on
supported Helix or Procore application pages. The shared shell owns presentation, local
preferences, bounded caching, TaskHub authentication, and normalized work-item behavior.
Provider adapters own site detection, context discovery, record retrieval, deep links, and
provider-specific downloads.

The existing Helix behavior is a compatibility requirement. Procore's default mode is standalone:
it reads only work-item cells already rendered in the visible Procore grid. Optional broader scans
use Procore's supported OAuth and REST APIs. Neither mode reads browser cookies, copies bearer tokens
from page traffic, or depends on private Procore JavaScript interfaces.

## Runtime shape

```text
SMC-CA-Tracker extension
  provider-core.js              host and route classification
  activity-core.js              normalized filtering and assignment rules
  ui.js                         one sidebar, provider-aware labels and actions
  content-script.js             existing Helix fixed-operation page bridge client
  procore-core/content-script   visible-grid extraction plus optional API upgrade
  integrations/taskhub/*        optional TaskHub OIDC and normalized sync transport

TaskHub backend
  tasks/external_sync.py       provider-neutral task/project synchronization
  helix_integration             compatibility API for existing extension releases
  procore_integration           OAuth, Procore REST client, and queue mapping
```

## Provider boundary

Every adapter returns the same normalized context and index shape. Provider-specific fields
remain under `sourceMetadata` and never control shared UI behavior directly.

```json
{
  "provider": "helix | procore",
  "accountId": "provider account or company id",
  "projectId": "provider project id",
  "projectName": "display name",
  "currentPersonKey": "provider-scoped person id",
  "user": {
    "id": "provider user id",
    "fullName": "display name",
    "company": "display company"
  }
}
```

Normalized work items contain `provider`, `itemType`, `itemId`, `projectId`, `number`,
`title`, `status`, `dates`, `queueAssignments`, `sourceUrl`, `attachments`, and
`sourceMetadata`. Cache keys include provider and account identity so records from two
providers or Procore companies cannot collide.

## Authentication and trust boundaries

- TaskHub is optional for both provider adapters. Its OIDC flow uses the pinned extension identity
  and Authorization Code + PKCE.
- Procore Authorization Code OAuth starts in the extension but the TaskHub backend performs
  the token exchange. The Procore client secret and refresh token never enter the extension.
- Stored Procore refresh tokens use TaskHub field encryption and are scoped to the connected
  TaskHub user.
- Each adapter sends only normalized selected work items when TaskHub is connected. A Procore
  visible-page snapshot is always incomplete and therefore cannot archive missing tasks.
- When the optional Procore OAuth connection is enabled, TaskHub can instead read and synchronize
  records server-side and return only the normalized queue to Chrome.
- The Helix bridge retains its fixed operation allowlist and read-only guarantees.

## Procore data flow

1. A Procore content script reads company/project identifiers plus the RFI and Submittal cells
   rendered in the current grid, then normalizes them locally.
2. The shared sidebar displays and filters that visible queue without requiring TaskHub.
3. If TaskHub is connected, it can receive a sanitized incomplete snapshot; records missing from
   the current grid are never archived.
4. If the optional Procore OAuth connection is also enabled, TaskHub refreshes its server-held token,
   calls documented REST endpoints, and returns a broader normalized queue.
5. Only a complete API scan can reconcile missing TaskHub tasks. Warning-bearing API scans skip that
   archival step.

Initial API coverage:

- `GET /rest/v1.0/me`
- `GET /rest/v1.1/projects`
- `GET /rest/v1.0/projects/{project_id}/rfis`
- `GET /rest/v1.1/projects/{project_id}/submittals`

Webhook-triggered refresh, RFI reply expansion, and deeper submittal workflow activity are
planned follow-on capabilities rather than part of the initial queue release.

## Compatibility and rollout

The first unified release keeps the existing extension public key so Chrome treats it as an
upgrade. Existing Helix cache, view-state, and TaskHub tokens remain readable. The new Procore
cache and route-context keys are provider-scoped; compatibility-sensitive `helix-*` authentication
and UI keys intentionally remain in place so an update does not sign users out or reset Helix.

The rollout sequence is:

1. Introduce provider-neutral contracts and keep all Helix tests green.
2. Add backend `procore` source support and encrypted OAuth connection storage.
3. Add mocked Procore API contract tests and normalized queue mapping.
4. Add the Procore adapter and provider-aware options/sidebar UI.
5. Validate against a Procore developer sandbox before enabling production configuration.
6. Package one stable-ID extension and document administrator permissions and rollback.

## Product decisions

- Default Procore mode is standalone visible-page access, so it has no external service dependency
  and stays within what Procore already rendered for the signed-in user.
- Optional API mode is user-authorized access so results match the person using the extension and
  Procore remains the permission authority.
- A future Developer Managed Service Account can use the same backend client for unattended
  company-wide synchronization, but is not allowed to silently broaden a user's view.
- A Procore embedded side-panel component can reuse the same hosted UI later; it is not
  required for the single-extension experience.
