# Privacy

My Helix Activity reads Helix data only through the active Helix browser tab and processes it locally in Chrome. When you select **Open in Helix**, it reads a fresh copy of that requested item so Helix's own detail viewer receives a complete record; this copy is not added to the extension's persistent cache.

It reads the current Helix directory identity and current-project activity needed for **My activity**. For **Open queue**, it also reads active project assignments from Helix's project directory, followed by open RFI and Submittal metadata within those assigned projects. This information is used only to render the local sidebar and its project/type filters.

The extension does not collect analytics, transmit data to another service, inspect credentials or authentication storage, or download attachment contents.

Successful scans are saved as a sanitized cache in Chrome's extension-local storage, scoped to the logged-in contact. Queue data is reused across supported pages and activity data is cached per project and feature. A cache is treated as fresh for 15 minutes; **Refresh** replaces it immediately. Cross-page item opening also stores a two-minute navigation handoff containing only item, type, and project identifiers; it is consumed on the matching destination page. Chrome may retain older cache entries until they are replaced, extension storage is cleared, or the extension is uninstalled. Removing or disabling the extension stops all processing.

The extension also stores the sidebar width, whether it was open or hidden, the selected My activity/Open queue page, and the active search, sort, status, project, person, item-type, activity, timing, and due-date filters. Filter state is scoped to the logged-in contact and can include search text plus Helix project and person identifiers, but not complete Helix records.
