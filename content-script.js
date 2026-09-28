(function startHelixActivityExtension() {
  "use strict";

  if (window.top !== window || window.__HELIX_ACTIVITY_CONTENT_STARTED) {
    return;
  }
  window.__HELIX_ACTIVITY_CONTENT_STARTED = true;

  const SOURCE = "helix-activity-extension";
  const TO_PAGE = "to-page";
  const FROM_PAGE = "from-page";
  const CACHE_VERSION = 4;
  const CACHE_TTL_MS = 15 * 60 * 1000;
  const CACHE_PREFIX = "helix-activity-cache-v4:";
  const MAX_ACTIVITY_CONTEXTS = 3;
  const NAVIGATION_VERSION = 1;
  const NAVIGATION_TTL_MS = 2 * 60 * 1000;
  const NAVIGATION_PREFIX = "helix-activity-navigation-v1:";
  const UI_PREFERENCES_KEY = "helix-activity-ui-preferences-v1";
  const VIEW_STATE_VERSION = 1;
  const VIEW_STATE_PREFIX = "helix-activity-view-state-v1:";
  const nonce = crypto.randomUUID().replace(/-/g, "_");
  const pending = new Map();
  let requestSequence = 0;
  let context = null;
  let hasScanned = false;
  let scanPromise = null;
  let restoringNavigation = false;
  let uiPreferences = null;
  let uiPreferencesPromise = null;
  let viewStateWritePromise = Promise.resolve();
  let activeViewState = null;
  const downloadedDirectoryHandles = new WeakMap();
  const activeReportTransfers = new Map();

  function runtimeMessage(message) {
    return new Promise((resolve) => {
      chrome.runtime.sendMessage(message, (response) => {
        if (chrome.runtime.lastError) {
          resolve({ ok: false, error: chrome.runtime.lastError.message });
          return;
        }
        resolve(response);
      });
    });
  }

  function safeFileName(value, fallback) {
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

  function safeDirectoryName(value, fallback) {
    let cleaned = String(value || "")
      .replace(/[<>:"/\\|?*\u0000-\u001f]/g, "-")
      .replace(/(?:\s*[-–—]\s*){2,}/g, " - ")
      .replace(/\s+/g, " ")
      .replace(/[. ]+$/g, "")
      .trim()
      .slice(0, 180);
    if (/^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/i.test(cleaned)) cleaned = `_${cleaned}`;
    return cleaned || fallback;
  }

  function escapeRegularExpression(value) {
    return String(value).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  }

  function titleWithoutProjectPrefix(title, projectName) {
    const value = String(title || "").trim();
    const project = String(projectName || "").trim();
    if (!value) return value;
    const cleanSeparators = (text) => String(text || "")
      .replace(/(?:\s*[-–—]\s*){2,}/g, " - ")
      .replace(/^(?:\s*[-–—:|]\s*)+|(?:\s*[-–—:|]\s*)+$/g, "")
      .replace(/\s+/g, " ")
      .trim();
    if (!project) return cleanSeparators(value);
    const projectCodes = project
      .split(/[^a-zA-Z0-9._-]+/)
      .filter((candidate) => /[a-zA-Z]/.test(candidate) && /\d/.test(candidate));
    const candidates = [
      project,
      project.split(/\s+(?:-|–|—|\||:)\s+/)[0],
      ...projectCodes
    ]
      .map((candidate) => String(candidate || "").trim())
      .filter((candidate, index, values) => candidate && values.indexOf(candidate) === index)
      .sort((left, right) => right.length - left.length);
    const match = candidates.find((candidate) => new RegExp(
      `^${escapeRegularExpression(candidate)}(?:(?:\\s*[-–—:|]\\s*)+|\\s+)`,
      "i"
    ).test(value));
    const withoutPrefix = match
      ? value.replace(new RegExp(
        `^${escapeRegularExpression(match)}(?:(?:\\s*[-–—:|]\\s*)+|\\s+)`,
        "i"
      ), "").trim()
      : value;
    return cleanSeparators(withoutPrefix);
  }

  function packageFolderName(item) {
    const number = String(item && (item.number || item.id) || "Helix item").trim();
    const title = titleWithoutProjectPrefix(item && item.title, item && item.projectName);
    return safeDirectoryName(title ? `${number} - ${title}` : number, "Helix item");
  }

  function projectFolderPickerId(item) {
    const projectId = String(item && item.projectId || "default-project");
    let hash = 2166136261;
    for (let index = 0; index < projectId.length; index += 1) {
      hash ^= projectId.charCodeAt(index);
      hash = Math.imul(hash, 16777619);
    }
    const readable = projectId.replace(/[^a-zA-Z0-9_-]/g, "_").slice(0, 15) || "project";
    return `helix_${readable}_${(hash >>> 0).toString(16).padStart(8, "0")}`;
  }

  function browseFolderPickerId(item) {
    return `helix_browse_${projectFolderPickerId(item).slice(-8)}`;
  }

  function numberedFileName(filename, number) {
    if (number <= 1) return filename;
    const dot = filename.lastIndexOf(".");
    return dot > 0
      ? `${filename.slice(0, dot)} (${number})${filename.slice(dot)}`
      : `${filename} (${number})`;
  }

  function attachmentPackageFileName(attachment) {
    const filename = String(attachment && attachment.filename || "");
    if (!/\.pdf$/i.test(filename)) return filename;
    const dateMatch = String(attachment && attachment.uploadedAt || "")
      .match(/^(\d{4}-\d{2}-\d{2})(?:T|$)/);
    if (!dateMatch) return filename;
    const datePrefix = dateMatch[1];
    const nextCharacter = filename.slice(datePrefix.length, datePrefix.length + 1);
    if (filename.startsWith(datePrefix) && (!nextCharacter || /[\s_-]/.test(nextCharacter))) {
      return filename;
    }
    return `${datePrefix} ${filename}`;
  }

  async function fileAlreadyExists(directoryHandle, filename) {
    try {
      await directoryHandle.getFileHandle(filename);
      return true;
    } catch (error) {
      if (error && error.name === "NotFoundError") return false;
      throw error;
    }
  }

  async function writePackageFile(directoryHandle, requestedName, fallback, blob, usedNames) {
    const baseName = safeFileName(requestedName, fallback);
    let sequence = 1;
    let filename = numberedFileName(baseName, sequence);
    while (usedNames.has(filename.toLocaleLowerCase()) || await fileAlreadyExists(directoryHandle, filename)) {
      sequence += 1;
      filename = numberedFileName(baseName, sequence);
    }
    usedNames.add(filename.toLocaleLowerCase());
    const fileHandle = await directoryHandle.getFileHandle(filename, { create: true });
    const writable = await fileHandle.createWritable();
    try {
      await writable.write(blob);
    } finally {
      await writable.close();
    }
    return filename;
  }

  async function downloadAttachment(attachment) {
    let url;
    try {
      url = new URL(String(attachment && attachment.url));
    } catch (_error) {
      throw new Error("Helix returned an invalid attachment URL.");
    }
    if (url.protocol !== "https:") throw new Error("Helix returned an unsafe attachment URL.");
    const response = await fetch(url.href);
    if (!response.ok) {
      throw new Error(`Helix returned ${response.status} while downloading ${attachment.filename || "an attachment"}.`);
    }
    return response.blob();
  }

  function createReportTransfer(channelName) {
    const channel = new BroadcastChannel(channelName);
    let timer;
    let rejectTransfer;
    const close = () => {
      clearTimeout(timer);
      channel.close();
      activeReportTransfers.delete(channelName);
    };
    const promise = new Promise((resolve, reject) => {
      rejectTransfer = reject;
      timer = setTimeout(() => {
        close();
        reject(new Error("The Helix Details report did not finish in time. The temporary report tab was closed."));
      }, 165000);
      channel.onmessage = (event) => {
        const message = event && event.data;
        if (!message || !["helix-report-ready", "helix-report-error"].includes(message.type)) return;
        close();
        if (message.type === "helix-report-error") {
          reject(new Error(message.error || "Helix could not create the Details report."));
        } else if (!message.blob) {
          reject(new Error("Helix returned an empty Details report."));
        } else {
          resolve({ blob: message.blob, filename: message.filename });
        }
      };
    });
    void promise.catch(() => {});
    const fail = (error) => {
      close();
      rejectTransfer(error instanceof Error ? error : new Error(String(error || "The report transfer failed.")));
    };
    activeReportTransfers.set(channelName, { fail });
    return {
      promise,
      cancel: fail
    };
  }

  if (chrome.runtime.onMessage && typeof chrome.runtime.onMessage.addListener === "function") {
    chrome.runtime.onMessage.addListener((message) => {
      if (!message || message.type !== "HELIX_ACTIVITY_REPORT_ERROR") return false;
      const transfer = activeReportTransfers.get(message.channelName);
      if (transfer) transfer.fail(new Error(message.error || "Helix could not export the Details PDF."));
      return false;
    });
  }

  async function storageGet(key) {
    const response = await runtimeMessage({ type: "HELIX_ACTIVITY_STORAGE_GET", key });
    return response && response.ok ? response.result : null;
  }

  async function storageSet(key, value) {
    const response = await runtimeMessage({ type: "HELIX_ACTIVITY_STORAGE_SET", key, value });
    if (!response || !response.ok) throw new Error(response && response.error || "Unable to save extension data.");
  }

  async function storageRemove(key) {
    await runtimeMessage({ type: "HELIX_ACTIVITY_STORAGE_REMOVE", key });
  }

  function currentCacheKey() {
    if (!context || !context.user) return null;
    const personId = context.contactId || context.user.contactId || context.user.id;
    return personId ? `${CACHE_PREFIX}${String(personId)}` : null;
  }

  function activityCacheKey() {
    return context ? `${context.projectId}:${context.itemType}` : null;
  }

  function pendingNavigationKey() {
    if (!context || !context.user) return null;
    const personId = context.contactId || context.user.contactId || context.user.id;
    return personId ? `${NAVIGATION_PREFIX}${String(personId)}` : null;
  }

  function currentViewStateKey() {
    if (!context || !context.user) return null;
    const personId = context.contactId || context.user.contactId || context.user.id;
    return personId ? `${VIEW_STATE_PREFIX}${String(personId)}` : null;
  }

  async function loadUiPreferences() {
    if (uiPreferences) return uiPreferences;
    if (!uiPreferencesPromise) {
      uiPreferencesPromise = storageGet(UI_PREFERENCES_KEY).then((value) => {
        uiPreferences = value && typeof value === "object" ? Object.assign({}, value) : {};
        return uiPreferences;
      });
    }
    return uiPreferencesPromise;
  }

  async function saveUiPreferences(update) {
    const current = await loadUiPreferences();
    Object.assign(current, update);
    await storageSet(UI_PREFERENCES_KEY, Object.assign({}, current));
  }

  async function loadViewState() {
    const key = currentViewStateKey();
    if (!key) return null;
    const value = await storageGet(key);
    return value && value.version === VIEW_STATE_VERSION && value.state
      ? value.state
      : null;
  }

  async function saveViewState(state) {
    const key = currentViewStateKey();
    if (!key || !state || typeof state !== "object") return;
    const storedValue = {
      version: VIEW_STATE_VERSION,
      storedAt: Date.now(),
      state
    };
    viewStateWritePromise = viewStateWritePromise.then(() => storageSet(key, storedValue));
    await viewStateWritePromise;
  }

  async function loadCachedIndex() {
    const key = currentCacheKey();
    if (!key) return null;
    const cache = await storageGet(key);
    if (!cache || cache.version !== CACHE_VERSION || !cache.queue) return null;
    const activity = cache.activities && cache.activities[activityCacheKey()];
    return {
      fresh: Date.now() - Number(cache.storedAt || 0) < CACHE_TTL_MS,
      index: {
        context,
        scannedAt: cache.queue.scannedAt || activity && activity.scannedAt || null,
        warnings: [
          ...(Array.isArray(cache.queue.warnings) ? cache.queue.warnings : []),
          ...(activity && Array.isArray(activity.warnings) ? activity.warnings : [])
        ],
        results: activity && Array.isArray(activity.results) ? activity.results : [],
        openQueue: Array.isArray(cache.queue.openQueue) ? cache.queue.openQueue : [],
        projects: Array.isArray(cache.queue.projects) ? cache.queue.projects : [],
        people: Array.isArray(cache.queue.people) ? cache.queue.people : [],
        currentPersonKey: cache.queue.currentPersonKey || null,
        cached: true
      }
    };
  }

  async function saveCachedIndex(index) {
    const key = currentCacheKey();
    const currentActivityKey = activityCacheKey();
    if (!key || !currentActivityKey) return { cached: false, reason: "storage_error" };
    const previous = await storageGet(key);
    const activities = previous && previous.version === CACHE_VERSION && previous.activities
      ? Object.assign({}, previous.activities)
      : {};
    activities[currentActivityKey] = {
      scannedAt: index.scannedAt,
      warnings: index.warnings,
      results: index.results
    };
    const recentActivityKeys = Object.keys(activities)
      .sort((a, b) => String(activities[b].scannedAt || "").localeCompare(String(activities[a].scannedAt || "")))
      .slice(0, MAX_ACTIVITY_CONTEXTS);
    const prunedActivities = Object.fromEntries(recentActivityKeys.map((activityKey) => [activityKey, activities[activityKey]]));
    const response = await runtimeMessage({
      type: "HELIX_ACTIVITY_CACHE_WRITE",
      key,
      value: {
        version: CACHE_VERSION,
        storedAt: Date.now(),
        queue: {
          scannedAt: index.scannedAt,
          warnings: index.warnings,
          openQueue: index.openQueue,
          projects: index.projects,
          people: index.people,
          currentPersonKey: index.currentPersonKey
        },
        activities: prunedActivities
      }
    });
    return response && response.ok
      ? response.result
      : { cached: false, reason: "storage_error" };
  }

  function cacheWarningMessage(result) {
    if (!result || result.cached !== false) return null;
    if (result.reason === "too_large") {
      return "These Helix results are too large to cache. They remain available until this page closes.";
    }
    if (result.reason === "quota") {
      return "Chrome could not save the local Helix cache. Task Hub sync continued; clear cached data in settings if this repeats.";
    }
    return "The local Helix cache could not be saved. Current results and Task Hub sync are still available.";
  }

  const ui = HelixActivityUI.mount({
    onPanelChange: (open) => { void saveUiPreferences({ panelOpen: Boolean(open) }); },
    onWidthChange: (width) => { void saveUiPreferences({ sidebarWidth: Number(width) }); },
    onViewStateChange: (state) => {
      activeViewState = state;
      void saveViewState(state);
    },
    onOpen: () => {
      if (!restoringNavigation && !hasScanned && !scanPromise) {
        void runScan().catch(() => {});
      }
    },
    onRefresh: () => { void runScan(true).catch(() => {}); },
    onTaskHubSettings: () => { void runtimeMessage({ type: "HELIX_ACTIVITY_OPEN_OPTIONS" }); },
    onTaskHubSync: () => { void runScan(true, true).catch(() => {}); },
    onOpenItem: async (item, button) => {
      try {
        button.disabled = true;
        button.textContent = "Opening…";
        const result = await rpc("openItem", {
          itemId: item.id,
          itemType: item.itemType,
          projectId: item.projectId
        }, null, 30000);
        if (result && result.navigateTo) {
          const navigationKey = pendingNavigationKey();
          if (!navigationKey) throw new Error("Unable to preserve the selected item across Helix pages.");
          await storageSet(navigationKey, {
            version: NAVIGATION_VERSION,
            createdAt: Date.now(),
            itemId: String(item.id),
            itemType: String(item.itemType),
            projectId: String(item.projectId)
          });
          window.location.assign(result.navigateTo);
          return;
        }
        button.textContent = "Opened";
      } catch (error) {
        ui.notifyOpenFailure(button, error.message);
      } finally {
        button.disabled = false;
      }
    },
    onDownloadItem: async (item, button, browseFolderButton) => {
      const originalLabel = button.textContent;
      let channelName = null;
      let reportTransfer = null;
      try {
        button.disabled = true;
        if (browseFolderButton) browseFolderButton.disabled = true;
        if (typeof window.showDirectoryPicker !== "function") {
          throw new Error("This Chrome version does not support choosing a download folder.");
        }
        button.textContent = "Choose folder…";
        const parentDirectoryHandle = await window.showDirectoryPicker({
          id: projectFolderPickerId(item),
          mode: "readwrite",
          startIn: "downloads"
        });
        button.textContent = "Preparing…";
        const result = await rpc("prepareItemDownload", {
          itemId: item.id,
          itemType: item.itemType,
          projectId: item.projectId
        }, null, 120000);
        const directoryHandle = await parentDirectoryHandle.getDirectoryHandle(
          packageFolderName(item),
          { create: true }
        );
        channelName = `helix-activity-report-${crypto.randomUUID().replace(/-/g, "_")}`;
        reportTransfer = createReportTransfer(channelName);
        const queued = await runtimeMessage({
          type: "HELIX_ACTIVITY_DOWNLOAD_PACKAGE",
          channelName,
          package: { report: result.report }
        });
        if (!queued || !queued.ok) {
          throw new Error(queued && queued.error || "Chrome could not start the Helix report.");
        }

        const usedNames = new Set();
        const attachments = Array.isArray(result.attachments) ? result.attachments : [];
        for (let index = 0; index < attachments.length; index += 1) {
          button.textContent = `Downloading ${index + 1}/${attachments.length + 1}…`;
          const blob = await downloadAttachment(attachments[index]);
          await writePackageFile(
            directoryHandle,
            attachmentPackageFileName(attachments[index]),
            `Attachment ${index + 1}`,
            blob,
            usedNames
          );
        }

        button.textContent = `Downloading ${attachments.length + 1}/${attachments.length + 1}…`;
        const reportFile = await reportTransfer.promise;
        reportTransfer = null;
        await writePackageFile(
          directoryHandle,
          reportFile.filename || result.report.filename,
          "Details.pdf",
          reportFile.blob,
          usedNames
        );
        await runtimeMessage({ type: "HELIX_ACTIVITY_REPORT_SAVED", channelName });
        if (browseFolderButton) {
          downloadedDirectoryHandles.set(browseFolderButton, directoryHandle);
          browseFolderButton.hidden = false;
        }
        button.textContent = `Downloaded ${attachments.length + 1} file${attachments.length === 0 ? "" : "s"}`;
      } catch (error) {
        if (reportTransfer) reportTransfer.cancel(error);
        if (channelName) {
          await runtimeMessage({ type: "HELIX_ACTIVITY_REPORT_FAILED", channelName });
        }
        if (error && error.name === "AbortError") {
          button.textContent = originalLabel;
        } else {
          ui.notifyOpenFailure(button, error.message);
        }
      } finally {
        button.disabled = false;
        if (browseFolderButton) browseFolderButton.disabled = false;
      }
    },
    onBrowseDownloadedFolder: async (item, button) => {
      const directoryHandle = downloadedDirectoryHandles.get(button);
      if (!directoryHandle) {
        ui.notifyOpenFailure(button, "Download the package before browsing its folder.");
        return;
      }
      const originalLabel = button.textContent;
      try {
        button.disabled = true;
        button.textContent = "Opening…";
        await window.showDirectoryPicker({
          id: browseFolderPickerId(item),
          mode: "read",
          startIn: directoryHandle
        });
      } catch (error) {
        if (!error || error.name !== "AbortError") {
          ui.notifyOpenFailure(button, error && error.message || "Chrome could not browse the downloaded folder.");
        }
      } finally {
        button.textContent = originalLabel;
        button.disabled = false;
      }
    }
  });

  async function taskHubStatus() {
    const response = await runtimeMessage({ type: "HELIX_TASKHUB_STATUS" });
    if (!response || !response.ok) return { connected: false };
    return response.result || { connected: false };
  }

  async function syncTaskHub(index, force) {
    const status = await taskHubStatus();
    ui.setTaskHubStatus(status);
    if (!status.connected || (!force && status.autoSync === false)) return null;
    const snapshot = HelixTaskHubMapper.buildSnapshot(index, activeViewState);
    ui.setTaskHubSyncing(snapshot.items.length);
    const response = await runtimeMessage({ type: "HELIX_TASKHUB_SYNC", snapshot });
    if (!response || !response.ok) {
      const error = new Error(response && response.error || "Task Hub sync failed.");
      ui.setTaskHubSyncError(error.message);
      throw error;
    }
    ui.setTaskHubSyncResult(response.result, snapshot.items.length);
    return response.result;
  }

  async function sendWindowsHelperSnapshot(raw) {
    // Optional: hands the full current-project item list to the CA Tracker Windows helper.
    // Without the helper permission or install this resolves quietly.
    try {
      if (typeof TaskHubWindowsHelperSnapshot === "undefined") return null;
      const snapshot = TaskHubWindowsHelperSnapshot.buildHelixSnapshot(raw);
      if (!snapshot.itemTypes.length) return null;
      const response = await runtimeMessage({ type: "WINDOWS_HELPER_SNAPSHOT", snapshot });
      return response && response.ok ? response.result : null;
    } catch (_error) {
      return null;
    }
  }

  function rpc(operation, payload, onProgress, timeoutMs) {
    const requestId = `activity-${Date.now()}-${requestSequence += 1}`;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(requestId);
        reject(new Error("Helix took too long to respond. Please try again."));
      }, timeoutMs || 15000);
      pending.set(requestId, { resolve, reject, timer, onProgress });
      window.postMessage({
        source: SOURCE,
        direction: TO_PAGE,
        nonce,
        requestId,
        operation,
        payload: payload || null
      }, window.location.origin);
    });
  }

  window.addEventListener("message", (event) => {
    if (event.source !== window || event.origin !== window.location.origin) return;
    const message = event.data;
    if (!message || message.source !== SOURCE || message.direction !== FROM_PAGE || message.nonce !== nonce) return;
    const request = pending.get(message.requestId);
    if (!request) return;
    if (message.kind === "progress") {
      if (typeof request.onProgress === "function") request.onProgress(message.update);
      return;
    }
    clearTimeout(request.timer);
    pending.delete(message.requestId);
    if (message.ok) request.resolve(message.result);
    else request.reject(new Error(message.error || "Helix returned an unknown error."));
  });

  async function bootstrapWithRetry() {
    let lastError = null;
    for (let attempt = 0; attempt < 24; attempt += 1) {
      try {
        context = await rpc("bootstrap", null, null, 5000);
        ui.setContext(context);
        return context;
      } catch (error) {
        lastError = error;
        await new Promise((resolve) => setTimeout(resolve, 500));
      }
    }
    throw lastError || new Error("Helix did not finish loading the current user.");
  }

  async function runScan(force, forceTaskHubSync) {
    if (scanPromise && !force) return scanPromise;
    scanPromise = (async () => {
      ui.setLoading("Confirming the current Helix login…");
      if (!context) await bootstrapWithRetry();
      const raw = await rpc("scan", null, (update) => ui.setProgress(update), 180000);
      context = raw.context;
      ui.setContext(context);
      const index = HelixActivityCore.buildActivityIndex(raw);
      ui.setData(index);
      hasScanned = true;
      let cacheResult;
      try {
        cacheResult = await saveCachedIndex(index);
      } catch (_error) {
        cacheResult = { cached: false, reason: "storage_error" };
      }
      if (typeof ui.setCacheWarning === "function") {
        ui.setCacheWarning(cacheWarningMessage(cacheResult));
      }
      try {
        await syncTaskHub(index, forceTaskHubSync === true);
      } catch (_error) {
        // Helix remains fully usable when the optional Task Hub connection fails.
      }
      await sendWindowsHelperSnapshot(raw);
      return index;
    })().catch((error) => {
      ui.setError(error.message);
      throw error;
    }).finally(() => {
      scanPromise = null;
    });
    return scanPromise;
  }

  async function resumePendingNavigation() {
    const navigationKey = pendingNavigationKey();
    if (!navigationKey) return false;
    const intent = await storageGet(navigationKey);
    if (!intent) return false;
    const expired = intent.version !== NAVIGATION_VERSION
      || Date.now() - Number(intent.createdAt || 0) > NAVIGATION_TTL_MS;
    if (expired) {
      await storageRemove(navigationKey);
      return false;
    }
    if (String(intent.projectId) !== String(context.projectId)
      || String(intent.itemType) !== String(context.itemType)) return false;

    await storageRemove(navigationKey);
    restoringNavigation = true;
    ui.setPanelOpen(true);
    restoringNavigation = false;
    try {
      await rpc("openItem", {
        itemId: String(intent.itemId),
        itemType: String(intent.itemType),
        projectId: String(intent.projectId)
      }, null, 30000);
      return true;
    } catch (error) {
      ui.setError(`The selected item could not be opened: ${error.message}`);
      return false;
    }
  }

  async function resumeTaskHubNavigation() {
    const match = String(window.location.hash || "").match(/^#helix-activity-open=([^&]+)$/);
    if (!match) return false;
    let itemId;
    try {
      itemId = decodeURIComponent(match[1]);
    } catch (_error) {
      return false;
    }
    if (!itemId || itemId.length > 255 || !/^[a-zA-Z0-9_-]+$/.test(itemId)) {
      return false;
    }
    if (window.history && typeof window.history.replaceState === "function") {
      window.history.replaceState(null, "", `${window.location.pathname}${window.location.search || ""}`);
    }
    ui.setPanelOpen(true);
    try {
      await rpc("openItem", {
        itemId,
        itemType: context.itemType,
        projectId: context.projectId
      }, null, 30000);
      return true;
    } catch (error) {
      ui.setError(`The Task Hub item could not be opened: ${error.message}`);
      return false;
    }
  }

  (async () => {
    try {
      const injected = await runtimeMessage({ type: "HELIX_ACTIVITY_INJECT_BRIDGE", nonce });
      if (!injected || !injected.ok) {
        throw new Error(injected && injected.error ? injected.error : "Unable to initialize the read-only bridge.");
      }
      await bootstrapWithRetry();
      const preferences = await loadUiPreferences();
      if (preferences.sidebarWidth) ui.setWidth(preferences.sidebarWidth);
      const savedViewState = await loadViewState();
      activeViewState = savedViewState;
      if (savedViewState && typeof ui.setViewState === "function") ui.setViewState(savedViewState);
      const cached = await loadCachedIndex();
      if (cached) {
        ui.setData(cached.index);
        hasScanned = cached.fresh;
      }
      ui.setTaskHubStatus(await taskHubStatus());
      const resumedTaskHubNavigation = await resumeTaskHubNavigation();
      const resumedNavigation = resumedTaskHubNavigation || await resumePendingNavigation();
      if (!resumedNavigation && preferences.panelOpen === true) ui.setPanelOpen(true);
    } catch (error) {
      ui.setError(error.message);
    }
  })();
})();
