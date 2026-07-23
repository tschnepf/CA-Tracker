"use strict";

const INJECT_MESSAGE = "HELIX_ACTIVITY_INJECT_BRIDGE";
const HELIX_ORIGIN = "https://project.helixteam.com";
const ACTIVITY_PATH = /^\/Projects\/[^/]+\/(?:Overview|RFI|Submittals)(?:\/|$)/i;

function isAllowedTab(tab) {
  if (!tab || !Number.isInteger(tab.id) || typeof tab.url !== "string") {
    return false;
  }

  try {
    const url = new URL(tab.url);
    return url.origin === HELIX_ORIGIN && ACTIVITY_PATH.test(url.pathname);
  } catch (_error) {
    return false;
  }
}

function isValidNonce(value) {
  return typeof value === "string" && /^[a-zA-Z0-9_-]{16,128}$/.test(value);
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== INJECT_MESSAGE) {
    return false;
  }

  if (!isAllowedTab(sender.tab) || !isValidNonce(message.nonce)) {
    sendResponse({ ok: false, error: "The bridge can only run on a Helix Overview, RFI, or Submittals page." });
    return false;
  }

  (async () => {
    await chrome.scripting.executeScript({
      target: { tabId: sender.tab.id },
      world: "MAIN",
      func: (nonce) => {
        window.__HELIX_ACTIVITY_BRIDGE_NONCE = nonce;
      },
      args: [message.nonce]
    });

    await chrome.scripting.executeScript({
      target: { tabId: sender.tab.id },
      world: "MAIN",
      files: ["bridge-core.js", "page-bridge.js"]
    });

    sendResponse({ ok: true });
  })().catch((error) => {
    sendResponse({
      ok: false,
      error: error instanceof Error ? error.message : "Unable to initialize the Helix bridge."
    });
  });

  return true;
});
