(function captureCaTrackerRefresh() {
  "use strict";

  // Runs at document_start, before Helix or Procore scripts can rewrite the URL. An id placed in the
  // link by the CA Tracker app is removed from the address bar and handed to the extension's
  // background worker only; the page never receives it. The native host later accepts it only if
  // the app on this computer created it.
  if (window.top !== window) return;
  const match = String(window.location.hash || "").match(/^#ca-tracker-refresh=([a-f0-9]{32})$/);
  if (!match) return;
  try {
    window.history.replaceState(window.history.state, "", `${window.location.pathname}${window.location.search}`);
  } catch (_error) {
    // Leaving the id in the address bar is harmless.
  }
  try {
    chrome.runtime.sendMessage({ type: "WINDOWS_HELPER_REFRESH_SEEN", id: match[1] }, () => {
      void chrome.runtime.lastError;
    });
  } catch (_error) {
    // The extension was reloaded; the app will report that the refresh did not finish.
  }
})();
