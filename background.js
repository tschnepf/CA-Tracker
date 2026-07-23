"use strict";

importScripts("integrations/taskhub/client.js");

const INJECT_MESSAGE = "HELIX_ACTIVITY_INJECT_BRIDGE";
const HELIX_ORIGIN = "https://project.helixteam.com";
const ACTIVITY_PATH = /^\/Projects\/[^/]+\/(?:Overview|RFI|Submittals)(?:\/|$)/i;
const CONTENT_STORAGE_KEYS = [
  /^helix-activity-cache-v3:/,
  /^helix-activity-navigation-v1:/,
  /^helix-activity-view-state-v1:/,
  /^helix-activity-ui-preferences-v1$/
];
const taskHubClient = HelixTaskHubClient.create({ chrome, fetch });
void taskHubClient.restrictCredentialStorage();

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

function isExtensionPage(sender) {
  return Boolean(sender && sender.id === chrome.runtime.id && !sender.tab);
}

function isValidNonce(value) {
  return typeof value === "string" && /^[a-zA-Z0-9_-]{16,128}$/.test(value);
}

function isAllowedContentStorageKey(value) {
  return typeof value === "string" && CONTENT_STORAGE_KEYS.some((pattern) => pattern.test(value));
}

function storageGet(key) {
  return new Promise((resolve) => {
    chrome.storage.local.get(key, (value) => {
      if (chrome.runtime.lastError) resolve(null);
      else resolve(value && Object.prototype.hasOwnProperty.call(value, key) ? value[key] : null);
    });
  });
}

function storageSet(key, value) {
  return new Promise((resolve, reject) => {
    chrome.storage.local.set({ [key]: value }, () => {
      if (chrome.runtime.lastError) reject(new Error(chrome.runtime.lastError.message));
      else resolve();
    });
  });
}

function storageRemove(key) {
  return new Promise((resolve) => chrome.storage.local.remove(key, () => resolve()));
}

async function injectBridge(message, sender) {
  if (!isAllowedTab(sender.tab) || !isValidNonce(message.nonce)) {
    throw new Error("The bridge can only run on a Helix Overview, RFI, or Submittals page.");
  }
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
  return { injected: true };
}

async function handleMessage(message, sender) {
  if (!message || typeof message.type !== "string") {
    throw new Error("Invalid extension message.");
  }

  if (message.type === INJECT_MESSAGE) {
    return injectBridge(message, sender);
  }

  if (message.type === "HELIX_ACTIVITY_STORAGE_GET"
    || message.type === "HELIX_ACTIVITY_STORAGE_SET"
    || message.type === "HELIX_ACTIVITY_STORAGE_REMOVE") {
    if (!isAllowedTab(sender.tab) || !isAllowedContentStorageKey(message.key)) {
      throw new Error("Storage access is limited to Helix activity data.");
    }
    if (message.type === "HELIX_ACTIVITY_STORAGE_GET") return storageGet(message.key);
    if (message.type === "HELIX_ACTIVITY_STORAGE_REMOVE") {
      await storageRemove(message.key);
      return true;
    }
    const serialized = JSON.stringify(message.value);
    if (serialized.length > 5 * 1024 * 1024) {
      throw new Error("Helix activity data exceeded the local storage safety limit.");
    }
    await storageSet(message.key, message.value);
    return true;
  }

  if (message.type === "HELIX_ACTIVITY_OPEN_OPTIONS") {
    if (!isAllowedTab(sender.tab)) throw new Error("Open settings from a supported Helix page.");
    await chrome.runtime.openOptionsPage();
    return true;
  }

  if (message.type === "HELIX_TASKHUB_STATUS") {
    if (!isAllowedTab(sender.tab) && !isExtensionPage(sender)) {
      throw new Error("Task Hub status is available only to this extension.");
    }
    return taskHubClient.status();
  }

  if (message.type === "HELIX_TASKHUB_LOGIN") {
    if (!isExtensionPage(sender)) throw new Error("Task Hub login must start from extension settings.");
    return taskHubClient.login({
      baseUrl: message.baseUrl,
      email: message.email,
      password: message.password
    });
  }

  if (message.type === "HELIX_TASKHUB_LOGOUT") {
    if (!isExtensionPage(sender)) throw new Error("Task Hub logout must start from extension settings.");
    return taskHubClient.logout();
  }

  if (message.type === "HELIX_TASKHUB_UPDATE_SETTINGS") {
    if (!isExtensionPage(sender)) throw new Error("Task Hub settings must be changed from extension settings.");
    return taskHubClient.updateSettings({ autoSync: message.autoSync });
  }

  if (message.type === "HELIX_TASKHUB_SYNC") {
    if (!isAllowedTab(sender.tab)) throw new Error("Task Hub sync must start from a supported Helix page.");
    return taskHubClient.sync(message.snapshot);
  }

  throw new Error("Unsupported extension message.");
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  handleMessage(message, sender)
    .then((result) => sendResponse({ ok: true, result }))
    .catch((error) => sendResponse({
      ok: false,
      error: error instanceof Error ? error.message : "The extension request failed."
    }));
  return true;
});
