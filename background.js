"use strict";

importScripts(
  "provider-core.js",
  "procore-core.js",
  "integrations/taskhub/client.js",
  "integrations/windows-helper/client.js"
);

const INJECT_MESSAGE = "HELIX_ACTIVITY_INJECT_BRIDGE";
const HELIX_ORIGIN = "https://project.helixteam.com";
const ACTIVITY_PATH = /^\/Projects\/[^/]+\/(?:Overview|RFI|Submittals)(?:\/|$)/i;
const ACTIVITY_CACHE_PREFIX = "helix-activity-cache-";
const CURRENT_ACTIVITY_CACHE_PREFIX = "helix-activity-cache-v4:";
const UNIFIED_ACTIVITY_CACHE_PREFIX = "taskhub-activity-cache-v1:";
const PROCORE_CONTEXT_KEY = "taskhub-procore-project-context-v1";
const PROCORE_DISCOVERY_KEY = "taskhub-procore-discovery-v1";
const MAX_PROCORE_CONTEXTS = 200;
const PROCORE_DISCOVERY_STALE_MS = 15 * 60 * 1000;
const MAX_PROCORE_DISCOVERY_BYTES = 1024 * 1024;
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
const windowsHelperClient = TaskHubWindowsHelperClient.create({ chrome });
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

function isSupportedTab(tab) {
  return Boolean(tab && Number.isInteger(tab.id)
    && typeof tab.url === "string"
    && TaskHubProviderCore.providerForUrl(tab.url));
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
    && ((value.startsWith(CURRENT_ACTIVITY_CACHE_PREFIX)
      && value.length > CURRENT_ACTIVITY_CACHE_PREFIX.length)
      || (value.startsWith(UNIFIED_ACTIVITY_CACHE_PREFIX)
      && value.length > UNIFIED_ACTIVITY_CACHE_PREFIX.length));
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

function isProcoreSender(sender) {
  return isSupportedTab(sender && sender.tab)
    && TaskHubProviderCore.providerForUrl(sender.tab.url) === TaskHubProviderCore.PROVIDERS.PROCORE;
}

async function readProcoreDiscovery(sender) {
  const stored = await storageGet(PROCORE_DISCOVERY_KEY);
  let catalog = TaskHubProcoreCore.normalizeDiscoveryCatalog(stored);
  const startedAt = Date.parse(catalog.scan.startedAt || "") || 0;
  if (catalog.scan.status === "running" && startedAt
    && Date.now() - startedAt > PROCORE_DISCOVERY_STALE_MS) {
    catalog.scan.status = "error";
    catalog.scan.phase = "";
    catalog.scan.error = "The previous discovery scan expired before it could finish.";
    catalog.scan.message = catalog.scan.error;
    catalog.scan.completedAt = new Date().toISOString();
    catalog.updatedAt = catalog.scan.completedAt;
    await storageSet(PROCORE_DISCOVERY_KEY, catalog);
  }
  return {
    catalog,
    isOwner: Boolean(sender && sender.tab && catalog.scan.ownerTabId === sender.tab.id)
  };
}

async function startProcoreDiscovery(message, sender) {
  if (!isProcoreSender(sender)) {
    throw new Error("Company discovery must start from a supported Procore page.");
  }
  const current = await readProcoreDiscovery(sender);
  if (current.catalog.scan.status === "running" && !current.isOwner) {
    throw new Error("Company discovery is already running in another Procore tab.");
  }
  const origin = message.origin && typeof message.origin === "object" ? message.origin : {};
  const originalUrl = String(origin.url || "");
  const safeOriginalUrl = TaskHubProcoreCore.safeSourceUrl(originalUrl)
    || TaskHubProcoreCore.safeProjectHomeUrl(originalUrl);
  if (!safeOriginalUrl) throw new Error("The current Procore page cannot start discovery.");
  const originalCompanyId = String(origin.companyId || "").trim();
  const originalCompanyName = String(origin.companyName || "").replace(/\s+/g, " ").trim().slice(0, 240);
  if (!/^\d+$/.test(originalCompanyId) || !originalCompanyName) {
    throw new Error("The current Procore company could not be identified.");
  }
  const now = new Date().toISOString();
  const catalog = TaskHubProcoreCore.normalizeDiscoveryCatalog({
    version: 1,
    updatedAt: now,
    companies: current.catalog.companies,
    scan: {
      id: `discovery_${Date.now()}_${sender.tab.id}`,
      ownerTabId: sender.tab.id,
      status: "running",
      phase: "enumerate",
      startedAt: now,
      originalUrl: safeOriginalUrl,
      originalCompanyId,
      originalCompanyName,
      pendingCompanies: [],
      completedCompanies: [],
      totalCompanies: 0,
      message: "Reading invited companies…"
    }
  });
  await storageSet(PROCORE_DISCOVERY_KEY, catalog);
  return { catalog, isOwner: true };
}

async function writeProcoreDiscovery(message, sender) {
  if (!isProcoreSender(sender)) {
    throw new Error("Company discovery updates must come from a supported Procore page.");
  }
  const current = await readProcoreDiscovery(sender);
  if (current.catalog.scan.status === "running" && !current.isOwner) {
    throw new Error("Only the Procore tab that started discovery may update it.");
  }
  const catalog = TaskHubProcoreCore.normalizeDiscoveryCatalog(message.catalog);
  if (catalog.scan.status === "running") catalog.scan.ownerTabId = sender.tab.id;
  catalog.updatedAt = new Date().toISOString();
  if (serializedBytes(PROCORE_DISCOVERY_KEY, catalog) > MAX_PROCORE_DISCOVERY_BYTES) {
    throw new Error("The discovered Procore catalog exceeded the local storage safety limit.");
  }
  await storageSet(PROCORE_DISCOVERY_KEY, catalog);
  return { catalog, isOwner: catalog.scan.ownerTabId === sender.tab.id };
}

async function cancelProcoreDiscovery(sender) {
  if (!isProcoreSender(sender)) {
    throw new Error("Company discovery can be cancelled only from a supported Procore page.");
  }
  const current = await readProcoreDiscovery(sender);
  if (current.catalog.scan.status !== "running") return current;
  if (!current.isOwner) throw new Error("Company discovery is running in another Procore tab.");
  current.catalog.scan.cancelRequested = true;
  current.catalog.scan.phase = "restore";
  current.catalog.scan.pendingCompanies = [];
  current.catalog.scan.message = "Cancelling and restoring your original company…";
  current.catalog.updatedAt = new Date().toISOString();
  await storageSet(PROCORE_DISCOVERY_KEY, current.catalog);
  return current;
}

async function failProcoreDiscoveryForTab(tabId) {
  const stored = await storageGet(PROCORE_DISCOVERY_KEY);
  const catalog = TaskHubProcoreCore.normalizeDiscoveryCatalog(stored);
  if (catalog.scan.status !== "running" || catalog.scan.ownerTabId !== tabId) return;
  const now = new Date().toISOString();
  catalog.scan.status = "error";
  catalog.scan.phase = "";
  catalog.scan.completedAt = now;
  catalog.scan.error = "The Procore tab running discovery was closed.";
  catalog.scan.message = catalog.scan.error;
  catalog.updatedAt = now;
  await storageSet(PROCORE_DISCOVERY_KEY, catalog);
}

async function taskHubIdentityKey() {
  const status = await taskHubClient.status();
  return String(status && status.user && (status.user.id || status.user.email) || "").trim();
}

async function rememberProcoreContexts(context, response) {
  const identityKey = await taskHubIdentityKey();
  const companyId = String(context && context.companyId || "").trim();
  if (!identityKey || !/^\d+$/.test(companyId)) return;
  const queue = response && response.queue ? response.queue : response;
  const projectIds = new Set(
    (queue && Array.isArray(queue.items) ? queue.items : [])
      .map((item) => String(item && (item.project_id || item.projectId) || ""))
      .filter((projectId) => /^\d+$/.test(projectId))
  );
  const requestedProject = String(context && context.projectId || "").trim();
  if (/^\d+$/.test(requestedProject)) projectIds.add(requestedProject);
  if (projectIds.size === 0) return;
  const stored = await storageGet(PROCORE_CONTEXT_KEY);
  const values = stored && typeof stored === "object" && !Array.isArray(stored) ? stored : {};
  const storedAt = Date.now();
  projectIds.forEach((projectId) => {
    values[`${encodeURIComponent(identityKey)}:${projectId}`] = { companyId, storedAt };
  });
  const trimmed = Object.fromEntries(Object.entries(values)
    .sort((left, right) => Number(right[1] && right[1].storedAt || 0) - Number(left[1] && left[1].storedAt || 0))
    .slice(0, MAX_PROCORE_CONTEXTS));
  await storageSet(PROCORE_CONTEXT_KEY, trimmed);
}

async function resolveProcoreCompany(projectId) {
  const normalizedProjectId = String(projectId || "").trim();
  if (!/^\d+$/.test(normalizedProjectId)) throw new Error("A numeric Procore project ID is required.");
  const identityKey = await taskHubIdentityKey();
  if (!identityKey) return { companyId: "" };
  const values = await storageGet(PROCORE_CONTEXT_KEY);
  const entry = values && values[`${encodeURIComponent(identityKey)}:${normalizedProjectId}`];
  return { companyId: String(entry && entry.companyId || "") };
}

async function clearProcoreContextsForCurrentIdentity() {
  const identityKey = await taskHubIdentityKey();
  if (!identityKey) return;
  const values = await storageGet(PROCORE_CONTEXT_KEY);
  if (!values || typeof values !== "object") return;
  const prefix = `${encodeURIComponent(identityKey)}:`;
  const retained = Object.fromEntries(
    Object.entries(values).filter(([key]) => !key.startsWith(prefix))
  );
  if (Object.keys(retained).length) await storageSet(PROCORE_CONTEXT_KEY, retained);
  else await storageRemove(PROCORE_CONTEXT_KEY);
}

function activityTimestamp(value) {
  const timestamp = Number(value && value.storedAt);
  return Number.isFinite(timestamp) && timestamp > 0 ? timestamp : 0;
}

function normalizeActivityCache(value) {
  if (value && typeof value === "object"
    && value.version === 1
    && value.provider === "procore"
    && activityTimestamp(value)
    && value.index
    && typeof value.index === "object"
    && Array.isArray(value.index.results)
    && Array.isArray(value.index.openQueue)) {
    return value;
  }
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
    .filter(([key]) => key.startsWith(ACTIVITY_CACHE_PREFIX)
      || key.startsWith(UNIFIED_ACTIVITY_CACHE_PREFIX))
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
  seenRefreshes.delete(tabId);
  void failProcoreDiscoveryForTab(tabId).catch(() => {});
});

// Refresh ids seen by refresh-capture.js at document_start, per tab, until the page's main content
// script takes them. Kept in extension-only session storage so a sleeping service worker keeps them.
const REFRESH_SEEN_PREFIX = "ca-tracker-refresh-tab:";
const REFRESH_SEEN_TTL_MS = 2 * 60 * 1000;
const seenRefreshes = new Map();

function refreshSession() {
  const session = chrome.storage && chrome.storage.session;
  return session && typeof session.get === "function" && typeof session.set === "function" ? session : null;
}

async function rememberRefreshForTab(tabId, id) {
  const entry = { id, at: Date.now() };
  seenRefreshes.set(tabId, entry);
  const session = refreshSession();
  if (session) {
    try {
      await session.set({ [`${REFRESH_SEEN_PREFIX}${tabId}`]: entry });
    } catch (_error) {
      // The in-memory copy is enough while the worker stays awake.
    }
  }
  return true;
}

async function takeRefreshForTab(tabId) {
  const key = `${REFRESH_SEEN_PREFIX}${tabId}`;
  let entry = seenRefreshes.get(tabId) || null;
  seenRefreshes.delete(tabId);
  const session = refreshSession();
  if (session) {
    try {
      const stored = await session.get(key);
      entry = entry || (stored && stored[key]) || null;
      if (typeof session.remove === "function") await session.remove(key);
    } catch (_error) {
      // Fall back to the in-memory copy.
    }
  }
  if (!entry || Date.now() - Number(entry.at) > REFRESH_SEEN_TTL_MS || !/^[a-f0-9]{32}$/.test(String(entry.id))) {
    return null;
  }
  return entry.id;
}

// The native host decides whether an app-requested refresh continues on another page or is done.
// A next page must stay on the same provider, and only the tab that ran the refresh is closed.
function finishWindowsHelperRefresh(result, provider, tabId) {
  const refresh = result && result.refresh;
  if (!refresh) return result;
  const next = typeof refresh.nextUrl === "string"
    && TaskHubProviderCore.providerForUrl(refresh.nextUrl) === provider
    ? refresh.nextUrl
    : null;
  if (refresh.done && refresh.closeTab && Number.isInteger(tabId)) {
    setTimeout(() => removeReportTab(tabId), 1200);
  }
  return Object.assign({}, result, {
    refresh: { done: Boolean(refresh.done), nextUrl: next, status: refresh.status || null, error: refresh.error || null }
  });
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

  if (message.type === "TASKHUB_ACTIVITY_CACHE_READ") {
    if (!isSupportedTab(sender.tab) || !isCurrentActivityCacheKey(message.key)) {
      throw new Error("Activity cache reads must start from a supported provider page.");
    }
    return storageGet(message.key);
  }

  if (message.type === "TASKHUB_ACTIVITY_CACHE_WRITE") {
    if (!isSupportedTab(sender.tab) || !String(message.key || "").startsWith(UNIFIED_ACTIVITY_CACHE_PREFIX)) {
      throw new Error("Activity cache writes must start from a supported provider page.");
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

  if (message.type === "TASKHUB_ACTIVITY_OPEN_OPTIONS") {
    if (!isSupportedTab(sender.tab)) throw new Error("Open settings from a supported provider page.");
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

  if (message.type === "TASKHUB_STATUS") {
    if (!isSupportedTab(sender.tab) && !isExtensionOptionsPage(sender)) {
      throw new Error("Task Hub status is available only to this extension.");
    }
    return taskHubClient.status();
  }

  if (message.type === "PROCORE_DISCOVERY_READ") {
    if (!isProcoreSender(sender)) {
      throw new Error("Company discovery is available only on supported Procore pages.");
    }
    return readProcoreDiscovery(sender);
  }

  if (message.type === "PROCORE_DISCOVERY_START") {
    return startProcoreDiscovery(message, sender);
  }

  if (message.type === "PROCORE_DISCOVERY_WRITE") {
    return writeProcoreDiscovery(message, sender);
  }

  if (message.type === "PROCORE_DISCOVERY_CANCEL") {
    return cancelProcoreDiscovery(sender);
  }

  if (message.type === "HELIX_TASKHUB_OIDC_LOGIN") {
    if (!isExtensionOptionsPage(sender)) throw new Error("Task Hub login must start from extension settings.");
    return taskHubClient.login({
      baseUrl: message.baseUrl
    });
  }

  if (message.type === "HELIX_TASKHUB_LOGOUT") {
    if (!isExtensionOptionsPage(sender)) throw new Error("Task Hub logout must start from extension settings.");
    const result = await taskHubClient.logout();
    await storageRemove(PROCORE_CONTEXT_KEY);
    return result;
  }

  if (message.type === "HELIX_TASKHUB_UPDATE_SETTINGS") {
    if (!isExtensionOptionsPage(sender)) throw new Error("Task Hub settings must be changed from extension settings.");
    return taskHubClient.updateSettings({ autoSync: message.autoSync });
  }

  if (message.type === "HELIX_TASKHUB_SYNC") {
    if (!isAllowedTab(sender.tab)) throw new Error("Task Hub sync must start from a supported Helix page.");
    return taskHubClient.sync(message.snapshot);
  }

  if (message.type === "PROCORE_STATUS") {
    if (!isSupportedTab(sender.tab) && !isExtensionOptionsPage(sender)) {
      throw new Error("Procore status is available only to this extension.");
    }
    return taskHubClient.procoreStatus();
  }

  if (message.type === "PROCORE_CONNECT") {
    if (!isExtensionOptionsPage(sender)) throw new Error("Procore connection must start from extension settings.");
    return taskHubClient.connectProcore();
  }

  if (message.type === "PROCORE_DISCONNECT") {
    if (!isExtensionOptionsPage(sender)) throw new Error("Procore disconnect must start from extension settings.");
    await clearProcoreContextsForCurrentIdentity();
    return taskHubClient.disconnectProcore();
  }

  if (message.type === "PROCORE_CONTEXT_RESOLVE") {
    if (!isSupportedTab(sender.tab)
      || TaskHubProviderCore.providerForUrl(sender.tab.url) !== TaskHubProviderCore.PROVIDERS.PROCORE) {
      throw new Error("Procore context may be resolved only from a supported Procore page.");
    }
    return resolveProcoreCompany(message.projectId);
  }

  if (message.type === "PROCORE_QUEUE" || message.type === "PROCORE_SYNC") {
    if (!isSupportedTab(sender.tab)
      || TaskHubProviderCore.providerForUrl(sender.tab.url) !== TaskHubProviderCore.PROVIDERS.PROCORE) {
      throw new Error("Procore requests must start from a supported Procore page.");
    }
    const result = await (message.type === "PROCORE_SYNC"
      ? taskHubClient.syncProcore(message.context)
      : taskHubClient.procoreQueue(message.context));
    await rememberProcoreContexts(message.context, result);
    return result;
  }

  if (message.type === "PROCORE_SNAPSHOT_SYNC") {
    if (!isSupportedTab(sender.tab)
      || TaskHubProviderCore.providerForUrl(sender.tab.url) !== TaskHubProviderCore.PROVIDERS.PROCORE) {
      throw new Error("Procore snapshot sync must start from a supported Procore page.");
    }
    return taskHubClient.syncProcoreSnapshot(message.snapshot);
  }

  if (message.type === "WINDOWS_HELPER_STATUS") {
    if (!isSupportedTab(sender.tab) && !isExtensionOptionsPage(sender)) {
      throw new Error("Windows helper status is available only to this extension.");
    }
    return windowsHelperClient.status();
  }

  if (message.type === "WINDOWS_HELPER_SNAPSHOT") {
    if (!isSupportedTab(sender.tab)) {
      throw new Error("Windows helper snapshots must start from a supported Helix or Procore page.");
    }
    const provider = TaskHubProviderCore.providerForUrl(sender.tab.url);
    if (!message.snapshot || message.snapshot.provider !== provider) {
      throw new Error("The snapshot provider does not match the sending page.");
    }
    const result = await windowsHelperClient.sendSnapshot(message.snapshot, {
      catalog: message.catalog,
      refresh: message.refresh
    });
    return finishWindowsHelperRefresh(result, provider, sender.tab.id);
  }

  if (message.type === "WINDOWS_HELPER_REFRESH_SEEN") {
    if (!isSupportedTab(sender.tab) || typeof message.id !== "string" || !/^[a-f0-9]{32}$/.test(message.id)) {
      throw new Error("Ignored an invalid CA Tracker refresh link.");
    }
    return rememberRefreshForTab(sender.tab.id, message.id);
  }

  if (message.type === "WINDOWS_HELPER_REFRESH_TAKE") {
    if (!isSupportedTab(sender.tab)) {
      throw new Error("CA Tracker refreshes must open on a supported Helix or Procore page.");
    }
    return takeRefreshForTab(sender.tab.id);
  }

  if (message.type === "WINDOWS_HELPER_REFRESH_CLAIM") {
    if (!isSupportedTab(sender.tab)) {
      throw new Error("CA Tracker refreshes must open on a supported Helix or Procore page.");
    }
    return windowsHelperClient.claimRefresh(message.id, TaskHubProviderCore.providerForUrl(sender.tab.url));
  }

  if (message.type === "WINDOWS_HELPER_REFRESH_FAILED") {
    if (!isSupportedTab(sender.tab)) {
      throw new Error("CA Tracker refreshes must open on a supported Helix or Procore page.");
    }
    return windowsHelperClient.refreshFailed(message.id, message.error);
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
