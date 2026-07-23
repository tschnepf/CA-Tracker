(function startHelixActivityExtension() {
  "use strict";

  if (window.top !== window || window.__HELIX_ACTIVITY_CONTENT_STARTED) {
    return;
  }
  window.__HELIX_ACTIVITY_CONTENT_STARTED = true;

  const SOURCE = "helix-activity-extension";
  const TO_PAGE = "to-page";
  const FROM_PAGE = "from-page";
  const CACHE_VERSION = 3;
  const CACHE_TTL_MS = 15 * 60 * 1000;
  const CACHE_PREFIX = "helix-activity-cache-v3:";
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
    if (!key || !currentActivityKey) return;
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
      .slice(0, 20);
    const prunedActivities = Object.fromEntries(recentActivityKeys.map((activityKey) => [activityKey, activities[activityKey]]));
    await storageSet(key, {
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
    });
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
      await saveCachedIndex(index);
      try {
        await syncTaskHub(index, forceTaskHubSync === true);
      } catch (_error) {
        // Helix remains fully usable when the optional Task Hub connection fails.
      }
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
