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
const REPORT_DOWNLOAD_TTL_MS = 3 * 60 * 1000;
const CONTENT_STORAGE_KEYS = [
  /^helix-activity-navigation-v1:/,
  /^helix-activity-view-state-v1:/,
  /^helix-activity-ui-preferences-v1$/
];
const taskHubClient = HelixTaskHubClient.create({ chrome, fetch });
let activityCacheOperations = Promise.resolve();
const pendingReportDownloads = new Map();
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

function safeDownloadFilename(value, fallback) {
  const parts = String(value || "").split(/[\\/]/);
  let cleaned = String(parts[parts.length - 1] || "")
    .replace(/[<>:"/\\|?*\u0000-\u001f]/g, "-")
    .replace(/\s+/g, " ")
    .replace(/[. ]+$/g, "")
    .trim()
    .slice(0, 180);
  if (/^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(cleaned)) cleaned = `_${cleaned}`;
  return cleaned || fallback;
}

function allowedReportDownload(value) {
  try {
    const url = new URL(String(value));
    return url.origin === HELIX_ORIGIN
      && /^\/Reports\/(?:Display|betaViewer|clientReportViewer)$/i.test(url.pathname)
      && url.searchParams.get("helixActivityDownload") === "1";
  } catch (_error) {
    return false;
  }
}

function createBackgroundTab(url) {
  return new Promise((resolve, reject) => {
    chrome.tabs.create({ url, active: false }, (tab) => {
      if (chrome.runtime.lastError) reject(new Error(chrome.runtime.lastError.message));
      else if (!tab || !Number.isInteger(tab.id)) reject(new Error("Chrome did not create the report tab."));
      else resolve(tab);
    });
  });
}

function removeReportTab(tabId) {
  if (!Number.isInteger(tabId)) return;
  chrome.tabs.remove(tabId, () => {
    void chrome.runtime.lastError;
  });
}

function notifyReportFailure(pending, error) {
  if (!pending || !Number.isInteger(pending.sourceTabId) || typeof chrome.tabs.sendMessage !== "function") return;
  chrome.tabs.sendMessage(pending.sourceTabId, {
    type: "HELIX_ACTIVITY_REPORT_ERROR",
    channelName: pending.channelName,
    error: error && error.message ? error.message : String(error || "Helix could not export the Details PDF.")
  }, () => {
    void chrome.runtime.lastError;
  });
}

async function queuePackageDownload(packageData, sender) {
  if (!isAllowedTab(sender.tab) || !packageData || typeof packageData !== "object") {
    throw new Error("Package downloads must start from a supported Helix page.");
  }
  if (!packageData.report || !allowedReportDownload(packageData.report.url)) {
    throw new Error("The Helix Details report URL is invalid.");
  }
  if (typeof packageData.channelName !== "string"
    || !/^helix-activity-report-[a-zA-Z0-9_-]{16,160}$/.test(packageData.channelName)) {
    throw new Error("The report transfer channel is invalid.");
  }

  const reportTab = await createBackgroundTab(packageData.report.url);
  const pending = {
    tabId: reportTab.id,
    sourceTabId: sender.tab.id,
    kind: String(packageData.report.kind || "client"),
    channelName: packageData.channelName,
    filename: safeDownloadFilename(packageData.report.filename, "Details.pdf"),
    exporting: false
  };
  pendingReportDownloads.set(reportTab.id, pending);
  const cleanupTimer = setTimeout(() => {
    const current = pendingReportDownloads.get(reportTab.id);
    if (current && current === pending) {
      pendingReportDownloads.delete(reportTab.id);
      removeReportTab(reportTab.id);
      notifyReportFailure(pending, new Error("The Helix Details report did not finish in time."));
    }
  }, REPORT_DOWNLOAD_TTL_MS);
  if (cleanupTimer && typeof cleanupTimer.unref === "function") cleanupTimer.unref();
  return {
    queued: true,
    reportName: String(packageData.report.name || "Details")
  };
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

async function startReportExport(tabId) {
  const pending = pendingReportDownloads.get(tabId);
  if (!pending || pending.exporting) return;
  pending.exporting = true;
  try {
    const results = await chrome.scripting.executeScript({
      target: { tabId },
      world: "MAIN",
      args: [pending.kind, pending.channelName, pending.filename],
      func: async (kind, channelName, fallbackFilename) => {
        const channel = new BroadcastChannel(channelName);
        const send = (message) => {
          channel.postMessage(message);
          setTimeout(() => channel.close(), 50);
        };
        const sendError = (error) => send({
          type: "helix-report-error",
          error: error && error.message ? error.message : String(error || "The report export failed.")
        });
        const sendBlob = async (source, filename) => {
          try {
            let blob = source instanceof Blob ? source : null;
            if (!blob) {
              if (!source) throw new Error("Helix returned an empty PDF export.");
              const response = await fetch(source);
              if (!response.ok) throw new Error(`Helix returned ${response.status} while creating the report.`);
              blob = await response.blob();
            }
            send({
              type: "helix-report-ready",
              blob,
              filename: String(filename || fallbackFilename || "Details.pdf")
            });
          } catch (error) {
            sendError(error);
          }
        };
        const waitFor = async (predicate, timeoutMs) => {
          const deadline = Date.now() + timeoutMs;
          while (Date.now() < deadline) {
            try {
              const value = predicate();
              if (value) return value;
            } catch (_error) {
              // The report initializes several globals asynchronously.
            }
            await new Promise((resolve) => setTimeout(resolve, 250));
          }
          throw new Error("The Helix Details report did not finish loading.");
        };

        try {
          if (kind === "legacy") {
            const viewer = await waitFor(() => {
              const jquery = window.jQuery || window.$;
              return typeof jquery === "function"
                ? jquery("#reportViewer1").data("telerik_ReportViewer")
                : null;
            }, 120000);
            await waitFor(() => document.querySelector(".trv-page-container, .trv-pages-area .trv-page-wrapper"), 120000);
            const originalOpen = window.open;
            window.open = (url) => {
              window.open = originalOpen;
              void sendBlob(new URL(String(url), window.location.href).href, fallbackFilename);
              return window;
            };
            viewer.exportReport("PDF");
            return { started: true, kind };
          }

          const exportButton = await waitFor(() => {
            const loadingMask = document.getElementById("loadingMask");
            const loading = loadingMask && window.getComputedStyle(loadingMask).display !== "none";
            const button = document.getElementById("export")
              || Array.from(document.querySelectorAll("button, a")).find((element) => /^(download|export)$/i.test(element.textContent.trim()));
            if (loading || !button || button.disabled) return null;
            const jquery = window.jQuery || window.$;
            if (kind === "client" && jquery && typeof jquery._data === "function") {
              const events = jquery._data(button, "events");
              if (!events || !Array.isArray(events.click) || events.click.length === 0) return null;
            }
            return button;
          }, 120000);
          const kendo = window.kendo;
          const pdf = kendo && kendo.drawing && kendo.drawing.pdf;
          const originalKendoSaveAs = kendo && kendo.saveAs;
          const originalPdfSaveAs = pdf && pdf.saveAs;
          const exportPdf = kendo && kendo.drawing && kendo.drawing.exportPDF;
          if (!kendo || (typeof originalKendoSaveAs !== "function"
            && !(typeof originalPdfSaveAs === "function" && typeof exportPdf === "function"))) {
            throw new Error("Helix's PDF export service is unavailable.");
          }
          let delivered = false;
          const restore = () => {
            if (typeof originalKendoSaveAs === "function") kendo.saveAs = originalKendoSaveAs;
            if (pdf && typeof originalPdfSaveAs === "function") pdf.saveAs = originalPdfSaveAs;
          };
          const deliver = (source, filename) => {
            if (delivered) return;
            delivered = true;
            restore();
            void sendBlob(source, filename || fallbackFilename);
          };
          if (typeof originalKendoSaveAs === "function") {
            kendo.saveAs = (options) => {
              const settings = options && typeof options === "object" ? options : {};
              deliver(settings.dataURI || settings.dataUrl || settings.blob, settings.fileName);
            };
          }
          if (pdf && typeof originalPdfSaveAs === "function" && typeof exportPdf === "function") {
            pdf.saveAs = (group, filename) => {
              Promise.resolve(exportPdf(group))
                .then((dataUri) => deliver(dataUri, filename))
                .catch((error) => {
                  restore();
                  sendError(error);
                });
            };
          }
          const needsDraftConfirmation = Boolean(document.querySelector(".missingRequired"));
          exportButton.click();

          if (needsDraftConfirmation) {
            const draftDeadline = Date.now() + 5000;
            while (Date.now() < draftDeadline) {
              const draftButton = Array.from(document.querySelectorAll("button, a")).find((element) => (
                /download draft/i.test(element.textContent || "")
              ));
              if (draftButton) {
                draftButton.click();
                break;
              }
              await new Promise((resolve) => setTimeout(resolve, 100));
            }
          }
          return { started: true, kind };
        } catch (error) {
          sendError(error);
          throw error;
        }
      }
    });
    const result = results && results[0] && results[0].result;
    if (!result || result.started !== true) {
      throw new Error("The report export did not start.");
    }
  } catch (error) {
    pendingReportDownloads.delete(tabId);
    removeReportTab(tabId);
    notifyReportFailure(pending, error);
  }
}

chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo && changeInfo.status === "complete" && pendingReportDownloads.has(tabId)) {
    void startReportExport(tabId);
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  pendingReportDownloads.delete(tabId);
});

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

  if (message.type === "HELIX_ACTIVITY_DOWNLOAD_PACKAGE") {
    return queuePackageDownload(Object.assign({}, message.package, {
      channelName: message.channelName
    }), sender);
  }

  if (message.type === "HELIX_ACTIVITY_REPORT_SAVED"
    || message.type === "HELIX_ACTIVITY_REPORT_FAILED") {
    if (!isAllowedTab(sender.tab) || typeof message.channelName !== "string") {
      throw new Error("Only the originating Helix tab can finish a report transfer.");
    }
    const pending = Array.from(pendingReportDownloads.values()).find((entry) => (
      entry.sourceTabId === sender.tab.id && entry.channelName === message.channelName
    ));
    if (!pending) return { finished: false };
    pendingReportDownloads.delete(pending.tabId);
    if (message.type === "HELIX_ACTIVITY_REPORT_SAVED") {
      removeReportTab(pending.tabId);
    } else {
      removeReportTab(pending.tabId);
    }
    return { finished: true };
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
