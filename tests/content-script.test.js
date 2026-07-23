"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const ActivityCore = require("../activity-core.js");

const source = fs.readFileSync(path.resolve(__dirname, "..", "content-script.js"), "utf8");
const origin = "https://project.helixteam.com";

async function waitFor(predicate, message) {
  const deadline = Date.now() + 2000;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 5));
  }
  throw new Error(message || "Timed out waiting for condition");
}

function createHarness(options = {}) {
  const listeners = [];
  const operations = [];
  const uiState = { loading: [], progress: [], contexts: [], panel: [], widths: [], viewStates: [] };
  let uiCallbacks = null;
  const ui = {
    setPanelOpen(value) { uiState.panel.push(value); },
    setWidth(value) { uiState.widths.push(value); },
    setViewState(value) { uiState.viewStates.push(value); },
    setContext(value) { uiState.contexts.push(value); },
    setLoading(value) { uiState.loading.push(value); },
    setProgress(value) { uiState.progress.push(value); },
    setError(value) { uiState.error = value; },
    setData(value) { uiState.data = value; },
    notifyOpenFailure(_button, value) { uiState.openFailure = value; }
  };
  const sessionContext = options.sessionContext || {
    projectId: "project-dynamic",
    projectName: "Dynamic Project",
    itemType: "rfi",
    itemLabel: "RFI",
    itemLabelPlural: "RFIs",
    contactId: "contact-dynamic",
    user: {
      id: "user-dynamic",
      contactId: "contact-dynamic",
      fullName: "Dynamic User",
      company: "Dynamic Co"
    }
  };
  const storageData = Object.assign({}, options.storageData);
  const assignedLocations = [];

  const windowObject = {
    location: {
      origin,
      assign(value) { assignedLocations.push(value); }
    },
    __HELIX_ACTIVITY_CONTENT_STARTED: false,
    addEventListener(type, listener) {
      if (type === "message") listeners.push(listener);
    },
    postMessage(message) {
      if (!message || message.direction !== "to-page") return;
      operations.push(message.operation);
      setImmediate(() => {
        if (message.operation === "scan") {
          dispatch({
            source: message.source,
            direction: "from-page",
            nonce: message.nonce,
            requestId: message.requestId,
            kind: "progress",
            update: { message: "Reading dynamic activity…" }
          });
        }
        const result = message.operation === "bootstrap"
          ? sessionContext
          : message.operation === "scan"
            ? {
                context: sessionContext,
                scannedAt: "2026-07-21T00:00:00.000Z",
                warnings: [],
                rfis: [{
                  id: "rfi-dynamic",
                  number: "0001",
                  title: "Dynamic result",
                  status: "Review",
                  dates: { created: "2026-07-01T00:00:00.000Z", modified: "2026-07-20T00:00:00.000Z" },
                  creator: sessionContext.user,
                  workflow: { required: [], optional: [], notify: [] }
                }],
                comments: [],
                attachments: [],
                histories: []
              }
            : typeof options.openItemResult === "function"
              ? options.openItemResult(message.payload)
              : { opened: true, itemId: message.payload.itemId };
        dispatch({
          source: message.source,
          direction: "from-page",
          nonce: message.nonce,
          requestId: message.requestId,
          kind: "response",
          ok: true,
          result
        });
      });
    }
  };
  windowObject.top = windowObject;

  function dispatch(data) {
    const event = { source: windowObject, origin, data };
    listeners.forEach((listener) => listener(event));
  }

  const chrome = {
    runtime: {
      lastError: null,
      sendMessage(message, callback) {
        assert.equal(message.type, "HELIX_ACTIVITY_INJECT_BRIDGE");
        setImmediate(() => callback({ ok: true }));
      }
    },
    storage: {
      local: {
        get(key, callback) {
          setImmediate(() => callback({ [key]: storageData[key] }));
        },
        set(value, callback) {
          Object.assign(storageData, value);
          setImmediate(() => callback());
        },
        remove(key, callback) {
          delete storageData[key];
          setImmediate(() => callback());
        }
      }
    }
  };

  const HelixActivityUI = {
    mount(callbacks) {
      uiCallbacks = callbacks;
      return ui;
    }
  };

  vm.runInNewContext(source, {
    window: windowObject,
    chrome,
    crypto: { randomUUID: () => "content_nonce_123456789" },
    HelixActivityUI,
    HelixActivityCore: ActivityCore,
    setTimeout,
    clearTimeout,
    setImmediate,
    Promise,
    Error
  });

  return {
    ui,
    uiState,
    operations,
    storageData,
    assignedLocations,
    get callbacks() { return uiCallbacks; }
  };
}

test("content script completes startup, dynamic scan, rendering, and open-detail orchestration", async () => {
  const harness = createHarness();
  await waitFor(() => harness.uiState.contexts.length > 0, "bootstrap did not finish");
  assert.equal(harness.uiState.contexts[0].user.id, "user-dynamic");

  harness.callbacks.onOpen();
  await waitFor(() => Boolean(harness.uiState.data), "scan did not render");
  assert.deepEqual(harness.operations.slice(0, 2), ["bootstrap", "scan"]);
  assert.equal(harness.uiState.data.results.length, 1);
  assert.equal(harness.uiState.data.results[0].id, "rfi-dynamic");
  assert.equal(harness.uiState.error, undefined);
  assert.ok(harness.uiState.progress.length > 0);
  await waitFor(() => Boolean(harness.storageData["helix-activity-cache-v3:contact-dynamic"]), "scan was not cached");
  const cached = harness.storageData["helix-activity-cache-v3:contact-dynamic"];
  assert.equal(cached.version, 3);
  assert.equal(cached.activities["project-dynamic:rfi"].results[0].id, "rfi-dynamic");

  const button = { disabled: false, textContent: "Open in Helix" };
  await harness.callbacks.onOpenItem({ id: "rfi-dynamic", itemType: "rfi", projectId: "project-dynamic" }, button);
  assert.equal(harness.operations.at(-1), "openItem");
  assert.equal(harness.uiState.panel.includes(false), false);
});

test("content script reuses a fresh person-scoped cache without rebuilding on panel open", async () => {
  const cacheKey = "helix-activity-cache-v3:contact-dynamic";
  const harness = createHarness({
    storageData: {
      [cacheKey]: {
        version: 3,
        storedAt: Date.now(),
        queue: {
          scannedAt: "2026-07-21T00:00:00.000Z",
          warnings: [],
          openQueue: [],
          projects: [{ id: "project-dynamic", name: "Dynamic Project" }],
          people: [{ key: "contact:contact-dynamic", fullName: "Dynamic User", isCurrent: true }],
          currentPersonKey: "contact:contact-dynamic"
        },
        activities: {
          "project-dynamic:rfi": {
            scannedAt: "2026-07-21T00:00:00.000Z",
            warnings: [],
            results: [{
              id: "rfi-cached",
              itemType: "rfi",
              projectId: "project-dynamic",
              number: "0002",
              title: "Cached result",
              status: "Review",
              latestActivityAt: "2026-07-20T00:00:00.000Z",
              searchText: "cached result",
              reasons: []
            }]
          }
        }
      }
    }
  });

  await waitFor(() => harness.uiState.data && harness.uiState.data.cached, "cached index did not render");
  harness.callbacks.onOpen();
  await new Promise((resolve) => setTimeout(resolve, 20));
  assert.deepEqual(harness.operations, ["bootstrap"]);
  assert.equal(harness.uiState.data.results[0].id, "rfi-cached");
});

test("cross-page item opening stores a short-lived handoff without closing the panel", async () => {
  const harness = createHarness({
    openItemResult: () => ({
      opened: false,
      navigateTo: "https://project.helixteam.com/Projects/project-other/Submittals"
    })
  });
  await waitFor(() => harness.uiState.contexts.length > 0, "bootstrap did not finish");
  const button = { disabled: false, textContent: "Open in Helix" };
  await harness.callbacks.onOpenItem({
    id: "submittal-other",
    itemType: "submittal",
    projectId: "project-other"
  }, button);

  assert.deepEqual(harness.assignedLocations, [
    "https://project.helixteam.com/Projects/project-other/Submittals"
  ]);
  const handoff = harness.storageData["helix-activity-navigation-v1:contact-dynamic"];
  assert.equal(handoff.itemId, "submittal-other");
  assert.equal(handoff.itemType, "submittal");
  assert.equal(harness.uiState.panel.includes(false), false);
});

test("destination page consumes the handoff, reopens the panel, and opens the item", async () => {
  const navigationKey = "helix-activity-navigation-v1:contact-dynamic";
  const destinationContext = {
    projectId: "project-other",
    projectName: "Other Project",
    itemType: "submittal",
    itemLabel: "Submittal",
    itemLabelPlural: "Submittals",
    contactId: "contact-dynamic",
    user: {
      id: "user-dynamic",
      contactId: "contact-dynamic",
      fullName: "Dynamic User",
      company: "Dynamic Co"
    }
  };
  const harness = createHarness({
    sessionContext: destinationContext,
    storageData: {
      [navigationKey]: {
        version: 1,
        createdAt: Date.now(),
        itemId: "submittal-other",
        itemType: "submittal",
        projectId: "project-other"
      }
    }
  });

  await waitFor(() => harness.uiState.panel.includes(true), "panel did not reopen");
  await waitFor(() => harness.operations.includes("openItem"), "destination item was not opened");
  assert.deepEqual(harness.operations, ["bootstrap", "openItem"]);
  assert.equal(harness.storageData[navigationKey], undefined);
  assert.equal(harness.uiState.panel.includes(false), false);
});

test("content script restores and updates persistent sidebar preferences", async () => {
  const preferencesKey = "helix-activity-ui-preferences-v1";
  const harness = createHarness({
    storageData: {
      [preferencesKey]: { sidebarWidth: 860, panelOpen: true }
    }
  });

  await waitFor(() => harness.uiState.widths.includes(860), "saved sidebar width was not restored");
  await waitFor(() => harness.uiState.panel.includes(true), "saved open state was not restored");
  harness.callbacks.onWidthChange(940);
  harness.callbacks.onPanelChange(false);
  await waitFor(() => {
    const preferences = harness.storageData[preferencesKey];
    return preferences && preferences.sidebarWidth === 940 && preferences.panelOpen === false;
  }, "sidebar preferences were not saved");
});

test("content script restores and updates the person-scoped filter and page state", async () => {
  const viewStateKey = "helix-activity-view-state-v1:contact-dynamic";
  const savedState = {
    view: "queue",
    search: "concrete",
    status: "Review",
    sort: "project",
    reasons: ["commented"],
    projects: ["project-a", "project-b"],
    types: ["submittal"],
    people: ["contact:reviewer"],
    queueTiming: "all",
    dueModes: ["overdue", "next"],
    dueDays: 21
  };
  const harness = createHarness({
    storageData: {
      [viewStateKey]: { version: 1, storedAt: 1, state: savedState }
    }
  });

  await waitFor(() => harness.uiState.viewStates.length === 1, "saved view state was not restored");
  assert.deepEqual(harness.uiState.viewStates[0], savedState);

  const updatedState = Object.assign({}, savedState, {
    view: "activity",
    status: "Pending",
    dueModes: ["today"]
  });
  harness.callbacks.onViewStateChange(updatedState);
  await waitFor(() => {
    const saved = harness.storageData[viewStateKey];
    return saved && saved.state && saved.state.view === "activity";
  }, "updated view state was not saved");
  assert.equal(harness.storageData[viewStateKey].version, 1);
  assert.deepEqual(harness.storageData[viewStateKey].state, updatedState);
});
