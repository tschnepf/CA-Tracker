"use strict";

importScripts("integrations/taskhub/client.js");

const INJECT_MESSAGE = "HELIX_ACTIVITY_INJECT_BRIDGE";
const HELIX_ORIGIN = "https://project.helixteam.com";
const ACTIVITY_PATH = /^\/Projects\/[^/]+\/(?:Overview|RFI|Submittals)(?:\/|$)/i;
const ACTIVITY_CACHE_PREFIX = "helix-activity-cache-";
const CURRENT_ACTIVITY_CACHE_PREFIX = "helix-activity-cache-v4:";
const ACTIVITY_CACHE_VERSION = 4;
const ACTIVITY_CACHE_RETENTION_MS = 24 * 60 * 60 * 1000;
const MAX_ACTIVITY_CONTEXTS = 3;
const MAX_ACTIVITY_CACHE_ENTRY_BYTES = 2 * 1024 * 1024;
const MAX_ACTIVITY_CACHE_TOTAL_BYTES = 5 * 1024 * 1024;
const CONTENT_STORAGE_KEYS = [
  /^helix-activity-navigation-v1:/,
  /^helix-activity-view-state-v1:/,
  /^helix-activity-ui-preferences-v1$/
];
const taskHubClient = HelixTaskHubClient.create({ chrome, fetch });
let activityCacheOperations = Promise.resolve();
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

function isExtensionOptionsPage(sender) {
  if (!sender || sender.id !== chrome.runtime.id || typeof sender.url !== "string") {
    return false;
  }
  try {
    const senderUrl = new URL(sender.url);
    const expectedUrl = new URL(chrome.runtime.getURL("options/options.html"));
    return senderUrl.protocol === expectedUrl.protocol
      && senderUrl.host === expectedUrl.host
      && senderUrl.pathname === expectedUrl.pathname
      && !senderUrl.search
      && !senderUrl.hash;
  } catch (_error) {
    return false;
  }
}

function isValidNonce(value) {
  return typeof value === "string" && /^[a-zA-Z0-9_-]{16,128}$/.test(value);
}

function isAllowedContentStorageKey(value) {
  return typeof value === "string" && CONTENT_STORAGE_KEYS.some((pattern) => pattern.test(value));
}

function isCurrentActivityCacheKey(value) {
  return typeof value === "string"
    && value.startsWith(CURRENT_ACTIVITY_CACHE_PREFIX)
    && value.length > CURRENT_ACTIVITY_CACHE_PREFIX.length;
}

function storageGet(key) {
  return new Promise((resolve) => {
    chrome.storage.local.get(key, (value) => {
      if (chrome.runtime.lastError) resolve(null);
      else resolve(value && Object.prototype.hasOwnProperty.call(value, key) ? value[key] : null);
    });
  });
}

function storageGetAll() {
  return new Promise((resolve, reject) => {
    chrome.storage.local.get(null, (value) => {
      if (chrome.runtime.lastError) reject(new Error(chrome.runtime.lastError.message));
      else resolve(value && typeof value === "object" ? value : {});
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

function storageRemove(keys) {
  const values = Array.isArray(keys) ? keys : [keys];
  if (values.length === 0) return Promise.resolve();
  return new Promise((resolve, reject) => chrome.storage.local.remove(values, () => {
    if (chrome.runtime.lastError) reject(new Error(chrome.runtime.lastError.message));
    else resolve();
  }));
}

function storageBytes(keys) {
  const values = Array.isArray(keys) ? keys : [keys];
  if (values.length === 0) return Promise.resolve(0);
  return new Promise((resolve, reject) => {
    chrome.storage.local.getBytesInUse(values, (value) => {
      if (chrome.runtime.lastError) reject(new Error(chrome.runtime.lastError.message));
      else resolve(Number(value) || 0);
    });
  });
}

function serializedBytes(key, value) {
  return new TextEncoder().encode(JSON.stringify({ [key]: value })).byteLength;
}

function activityTimestamp(value) {
  const timestamp = Number(value && value.storedAt);
  return Number.isFinite(timestamp) && timestamp > 0 ? timestamp : 0;
}

function normalizeActivityCache(value) {
  if (!value || typeof value !== "object"
    || value.version !== ACTIVITY_CACHE_VERSION
    || !activityTimestamp(value)
    || !value.queue
    || typeof value.queue !== "object"
    || !value.activities
    || typeof value.activities !== "object"
    || Array.isArray(value.activities)) {
    return null;
  }
  const activityKeys = Object.keys(value.activities)
    .filter((key) => value.activities[key] && typeof value.activities[key] === "object")
    .sort((left, right) => {
      const leftTime = Date.parse(value.activities[left].scannedAt || "") || 0;
      const rightTime = Date.parse(value.activities[right].scannedAt || "") || 0;
      return rightTime - leftTime;
    })
    .slice(0, MAX_ACTIVITY_CONTEXTS);
  return {
    version: ACTIVITY_CACHE_VERSION,
    storedAt: activityTimestamp(value),
    queue: value.queue,
    activities: Object.fromEntries(activityKeys.map((key) => [key, value.activities[key]]))
  };
}

function activityCacheEntries(values) {
  return Object.entries(values)
    .filter(([key]) => key.startsWith(ACTIVITY_CACHE_PREFIX))
    .map(([key, value]) => ({ key, value, storedAt: activityTimestamp(value) }));
}

async function cleanupActivityCache(nowValue) {
  const now = Number.isFinite(Number(nowValue)) ? Number(nowValue) : Date.now();
  const values = await storageGetAll();
  const removable = activityCacheEntries(values)
    .filter(({ key, value, storedAt }) => !isCurrentActivityCacheKey(key)
      || !normalizeActivityCache(value)
      || now - storedAt > ACTIVITY_CACHE_RETENTION_MS)
    .map(({ key }) => key);
  await storageRemove(removable);
  return removable;
}

async function activityCacheStatus() {
  await cleanupActivityCache();
  const values = await storageGetAll();
  const keys = activityCacheEntries(values)
    .filter(({ key }) => isCurrentActivityCacheKey(key))
    .map(({ key }) => key);
  return {
    bytesInUse: await storageBytes(keys),
    budgetBytes: MAX_ACTIVITY_CACHE_TOTAL_BYTES,
    entryCount: keys.length
  };
}

function isQuotaError(error) {
  return /quota|QUOTA_BYTES/i.test(error instanceof Error ? error.message : String(error || ""));
}

function queueActivityCacheOperation(operation) {
  const next = activityCacheOperations.catch(() => {}).then(operation);
  activityCacheOperations = next.catch(() => {});
  return next;
}

async function writeActivityCache(key, value) {
  if (!isCurrentActivityCacheKey(key)) {
    throw new Error("The activity cache key is invalid.");
  }
  const candidate = normalizeActivityCache(value);
  if (!candidate) {
    throw new Error("The activity cache value is invalid.");
  }

  await cleanupActivityCache();
  const candidateBytes = serializedBytes(key, candidate);
  if (candidateBytes > MAX_ACTIVITY_CACHE_ENTRY_BYTES) {
    await storageRemove(key);
    return Object.assign(await activityCacheStatus(), {
      cached: false,
      reason: "too_large"
    });
  }

  const values = await storageGetAll();
  const entries = activityCacheEntries(values)
    .filter((entry) => isCurrentActivityCacheKey(entry.key));
  const existingKeys = entries.map((entry) => entry.key);
  let totalBytes = await storageBytes(existingKeys);
  if (existingKeys.includes(key)) totalBytes -= await storageBytes(key);

  const oldestFirst = entries
    .filter((entry) => entry.key !== key)
    .sort((left, right) => left.storedAt - right.storedAt);
  while (totalBytes + candidateBytes > MAX_ACTIVITY_CACHE_TOTAL_BYTES && oldestFirst.length) {
    const oldest = oldestFirst.shift();
    totalBytes -= await storageBytes(oldest.key);
    await storageRemove(oldest.key);
  }

  try {
    await storageSet(key, candidate);
  } catch (error) {
    if (!isQuotaError(error)) {
      await storageRemove(key);
      return Object.assign(await activityCacheStatus(), {
        cached: false,
        reason: "storage_error"
      });
    }
    const remaining = activityCacheEntries(await storageGetAll())
      .filter((entry) => isCurrentActivityCacheKey(entry.key) && entry.key !== key)
      .sort((left, right) => left.storedAt - right.storedAt);
    await storageRemove(remaining.map((entry) => entry.key));
    try {
      await storageSet(key, candidate);
    } catch (_retryError) {
      await storageRemove(key);
      return Object.assign(await activityCacheStatus(), {
        cached: false,
        reason: "quota"
      });
    }
  }

  return Object.assign(await activityCacheStatus(), {
    cached: true,
    reason: null
  });
}

async function clearActivityCache() {
  const values = await storageGetAll();
  const keys = activityCacheEntries(values).map(({ key }) => key);
  const bytesCleared = await storageBytes(keys);
  await storageRemove(keys);
  return {
    bytesCleared,
    entriesCleared: keys.length,
    bytesInUse: 0,
    budgetBytes: MAX_ACTIVITY_CACHE_TOTAL_BYTES,
    entryCount: 0
  };
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

  if (message.type === "HELIX_ACTIVITY_STORAGE_GET") {
    if (!isAllowedTab(sender.tab)
      || (!isAllowedContentStorageKey(message.key) && !isCurrentActivityCacheKey(message.key))) {
      throw new Error("Storage access is limited to Helix activity data.");
    }
    return storageGet(message.key);
  }

  if (message.type === "HELIX_ACTIVITY_STORAGE_SET"
    || message.type === "HELIX_ACTIVITY_STORAGE_REMOVE") {
    if (!isAllowedTab(sender.tab) || !isAllowedContentStorageKey(message.key)) {
      throw new Error("Storage access is limited to Helix activity data.");
    }
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

  if (message.type === "HELIX_ACTIVITY_CACHE_WRITE") {
    if (!isAllowedTab(sender.tab)) {
      throw new Error("Activity cache writes must start from a supported Helix page.");
    }
    return queueActivityCacheOperation(() => writeActivityCache(message.key, message.value));
  }

  if (message.type === "HELIX_ACTIVITY_CACHE_STATUS") {
    if (!isExtensionOptionsPage(sender)) {
      throw new Error("Activity cache status is available only from extension settings.");
    }
    return queueActivityCacheOperation(() => activityCacheStatus());
  }

  if (message.type === "HELIX_ACTIVITY_CACHE_CLEAR") {
    if (!isExtensionOptionsPage(sender)) {
      throw new Error("Activity cache can be cleared only from extension settings.");
    }
    return queueActivityCacheOperation(() => clearActivityCache());
  }

  if (message.type === "HELIX_ACTIVITY_OPEN_OPTIONS") {
    if (!isAllowedTab(sender.tab)) throw new Error("Open settings from a supported Helix page.");
    await chrome.runtime.openOptionsPage();
    return true;
  }

  if (message.type === "HELIX_TASKHUB_STATUS") {
    if (!isAllowedTab(sender.tab) && !isExtensionOptionsPage(sender)) {
      throw new Error("Task Hub status is available only to this extension.");
    }
    return taskHubClient.status();
  }

  if (message.type === "HELIX_TASKHUB_OIDC_LOGIN") {
    if (!isExtensionOptionsPage(sender)) throw new Error("Task Hub login must start from extension settings.");
    return taskHubClient.login({
      baseUrl: message.baseUrl
    });
  }

  if (message.type === "HELIX_TASKHUB_LOGOUT") {
    if (!isExtensionOptionsPage(sender)) throw new Error("Task Hub logout must start from extension settings.");
    return taskHubClient.logout();
  }

  if (message.type === "HELIX_TASKHUB_UPDATE_SETTINGS") {
    if (!isExtensionOptionsPage(sender)) throw new Error("Task Hub settings must be changed from extension settings.");
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

void queueActivityCacheOperation(() => cleanupActivityCache()).catch(() => {
  // Cache cleanup is best-effort and must never prevent the service worker from starting.
});
