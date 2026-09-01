(function installHelixActivityPageBridge() {
  "use strict";

  const SOURCE = "helix-activity-extension";
  const TO_PAGE = "to-page";
  const FROM_PAGE = "from-page";
  const PROGRESS = "progress";
  const ALLOWED_OPERATIONS = new Set(["bootstrap", "scan", "openItem", "prepareItemDownload"]);

  if (!window.HelixBridgeCore || window.__HELIX_ACTIVITY_PAGE_BRIDGE_INSTALLED) {
    return;
  }
  window.__HELIX_ACTIVITY_PAGE_BRIDGE_INSTALLED = true;

  function currentNonce() {
    return window.__HELIX_ACTIVITY_BRIDGE_NONCE;
  }

  function send(payload) {
    window.postMessage(Object.assign({
      source: SOURCE,
      direction: FROM_PAGE,
      nonce: currentNonce()
    }, payload), window.location.origin);
  }

  function errorMessage(error) {
    return error instanceof Error && error.message
      ? error.message.slice(0, 500)
      : "The Helix read-only bridge encountered an error.";
  }

  window.addEventListener("message", async (event) => {
    if (event.source !== window || event.origin !== window.location.origin) {
      return;
    }
    const message = event.data;
    if (!message
      || message.source !== SOURCE
      || message.direction !== TO_PAGE
      || message.nonce !== currentNonce()
      || !ALLOWED_OPERATIONS.has(message.operation)
      || typeof message.requestId !== "string") {
      return;
    }

    try {
      let result;
      if (message.operation === "bootstrap") {
        result = window.HelixBridgeCore.getContext(window);
      } else if (message.operation === "scan") {
        result = await window.HelixBridgeCore.scan(window, (update) => {
          send({ kind: PROGRESS, requestId: message.requestId, update });
        });
      } else if (message.operation === "openItem" || message.operation === "prepareItemDownload") {
        const itemId = message.payload && message.payload.itemId;
        if (typeof itemId !== "string" || !itemId) {
          throw new Error("A valid item identifier is required.");
        }
        const itemType = message.payload && message.payload.itemType;
        const projectId = message.payload && message.payload.projectId;
        result = message.operation === "openItem"
          ? await window.HelixBridgeCore.openItem(window, itemId, itemType, projectId)
          : await window.HelixBridgeCore.prepareItemDownload(window, itemId, itemType, projectId);
      } else {
        throw new Error("The requested operation is not supported.");
      }
      send({ kind: "response", requestId: message.requestId, ok: true, result });
    } catch (error) {
      send({ kind: "response", requestId: message.requestId, ok: false, error: errorMessage(error) });
    }
  });
})();
