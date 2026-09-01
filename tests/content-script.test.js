"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const ActivityCore = require("../activity-core.js");
const TaskHubMapper = require("../integrations/taskhub/mapper.js");

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
  const uiState = {
    loading: [],
    progress: [],
    contexts: [],
    panel: [],
    widths: [],
    viewStates: [],
    taskHubStatuses: [],
    cacheWarnings: []
  };
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
    setTaskHubStatus(value) { uiState.taskHubStatuses.push(value); },
    setTaskHubSyncing(value) { uiState.taskHubSyncing = value; },
    setTaskHubSyncResult(value) { uiState.taskHubSyncResult = value; },
    setTaskHubSyncError(value) { uiState.taskHubSyncError = value; },
    setCacheWarning(value) { uiState.cacheWarnings.push(value); },
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
  const runtimeMessages = [];
  const selectedDirectories = new Map();
  const pickerCalls = [];
  const broadcastChannels = new Map();

  class BroadcastChannelMock {
    constructor(name) {
      this.name = name;
      this.onmessage = null;
      const entries = broadcastChannels.get(name) || new Set();
      entries.add(this);
      broadcastChannels.set(name, entries);
    }
    close() {
      const entries = broadcastChannels.get(this.name);
      if (entries) entries.delete(this);
    }
    static emit(name, data) {
      (broadcastChannels.get(name) || []).forEach((channel) => {
        if (typeof channel.onmessage === "function") channel.onmessage({ data });
      });
    }
  }

  function fileDirectory(files) {
    return {
      async getFileHandle(filename, handleOptions = {}) {
        if (!files.has(filename) && !handleOptions.create) {
          const error = new Error("Not found");
          error.name = "NotFoundError";
          throw error;
        }
        if (!files.has(filename)) files.set(filename, null);
        return {
          async createWritable() {
            let value;
            return {
              async write(blob) { value = blob; },
              async close() { files.set(filename, value); }
            };
          }
        };
      }
    };
  }

  const directoryHandle = {
    async getDirectoryHandle(directoryName, handleOptions = {}) {
      if (!selectedDirectories.has(directoryName) && !handleOptions.create) {
        const error = new Error("Not found");
        error.name = "NotFoundError";
        throw error;
      }
      if (!selectedDirectories.has(directoryName)) {
        selectedDirectories.set(directoryName, new Map());
      }
      return fileDirectory(selectedDirectories.get(directoryName));
    }
  };

  const windowObject = {
    location: {
      origin,
      hash: "",
      pathname: "/Projects/project-dynamic/RFI",
      search: "",
      assign(value) { assignedLocations.push(value); }
    },
    history: { replaceState() {} },
    __HELIX_ACTIVITY_CONTENT_STARTED: false,
    async showDirectoryPicker(pickerOptions) {
      pickerCalls.push(pickerOptions);
      if (options.directoryPickerError) throw options.directoryPickerError;
      return directoryHandle;
    },
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
            : message.operation === "prepareItemDownload"
              ? (typeof options.prepareItemDownloadResult === "function"
                  ? options.prepareItemDownloadResult(message.payload)
                  : {
                      prepared: true,
                      report: {
                        kind: "client",
                        url: `${origin}/Reports/clientReportViewer?rid=report-1&helixActivityDownload=1`
                      },
                      attachments: []
                    })
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
        runtimeMessages.push(message.type);
        if (message.type === "HELIX_ACTIVITY_INJECT_BRIDGE") {
          setImmediate(() => callback({ ok: true, result: { injected: true } }));
          return;
        }
        if (message.type === "HELIX_ACTIVITY_STORAGE_GET") {
          setImmediate(() => callback({ ok: true, result: storageData[message.key] || null }));
          return;
        }
        if (message.type === "HELIX_ACTIVITY_STORAGE_SET") {
          storageData[message.key] = message.value;
          setImmediate(() => callback({ ok: true, result: true }));
          return;
        }
        if (message.type === "HELIX_ACTIVITY_CACHE_WRITE") {
          if (!options.cacheWriteFailure) storageData[message.key] = message.value;
          setImmediate(() => callback({
            ok: true,
            result: options.cacheWriteFailure
              ? { cached: false, reason: options.cacheWriteFailure }
              : { cached: true, reason: null, bytesInUse: 1000, budgetBytes: 5 * 1024 * 1024, entryCount: 1 }
          }));
          return;
        }
        if (message.type === "HELIX_ACTIVITY_STORAGE_REMOVE") {
          delete storageData[message.key];
          setImmediate(() => callback({ ok: true, result: true }));
          return;
        }
        if (message.type === "HELIX_TASKHUB_STATUS") {
          setImmediate(() => callback({ ok: true, result: options.taskHubStatus || { connected: false } }));
          return;
        }
        if (message.type === "HELIX_TASKHUB_SYNC") {
          uiState.taskHubSnapshot = message.snapshot;
          setImmediate(() => callback({
            ok: true,
            result: { created: message.snapshot.items.length, updated: 0, archived: 0 }
          }));
          return;
        }
        if (message.type === "HELIX_ACTIVITY_OPEN_OPTIONS") {
          uiState.openedOptions = true;
          setImmediate(() => callback({ ok: true, result: true }));
          return;
        }
        if (message.type === "HELIX_ACTIVITY_DOWNLOAD_PACKAGE") {
          uiState.downloadPackage = message.package;
          setImmediate(() => {
            callback({ ok: true, result: { queued: true } });
            BroadcastChannelMock.emit(message.channelName, {
              type: "helix-report-ready",
              filename: message.package.report.filename || "RFI 0001 - Details.pdf",
              blob: { kind: "report" }
            });
          });
          return;
        }
        if (message.type === "HELIX_ACTIVITY_REPORT_SAVED"
          || message.type === "HELIX_ACTIVITY_REPORT_FAILED") {
          setImmediate(() => callback({ ok: true, result: { finished: true } }));
          return;
        }
        setImmediate(() => callback({ ok: false, error: "Unexpected message" }));
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
    HelixTaskHubMapper: TaskHubMapper,
    setTimeout,
    clearTimeout,
    setImmediate,
    Promise,
    Error,
    URL,
    BroadcastChannel: BroadcastChannelMock,
    fetch: async (url) => ({
      ok: true,
      status: 200,
      async blob() { return { kind: "attachment", url }; }
    })
  });

  return {
    ui,
    uiState,
    operations,
    storageData,
    assignedLocations,
    runtimeMessages,
    selectedDirectories,
    pickerCalls,
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
  await waitFor(() => Boolean(harness.storageData["helix-activity-cache-v4:contact-dynamic"]), "scan was not cached");
  const cached = harness.storageData["helix-activity-cache-v4:contact-dynamic"];
  assert.equal(cached.version, 4);
  assert.equal(cached.activities["project-dynamic:rfi"].results[0].id, "rfi-dynamic");

  const button = { disabled: false, textContent: "Open in Helix" };
  await harness.callbacks.onOpenItem({ id: "rfi-dynamic", itemType: "rfi", projectId: "project-dynamic" }, button);
  assert.equal(harness.operations.at(-1), "openItem");
  assert.equal(harness.uiState.panel.includes(false), false);
});

test("content script creates an item-named folder inside the selected destination", async () => {
  const harness = createHarness({
    prepareItemDownloadResult: () => ({
      prepared: true,
      report: {
        kind: "client",
        filename: "RFI 0001 - Details.pdf",
        url: `${origin}/Reports/clientReportViewer?rid=report-1&helixActivityDownload=1`
      },
      attachments: [{
        filename: "Review markup.pdf",
        uploadedAt: "2026-08-03T18:45:00.000Z",
        url: "https://storage.googleapis.com/example/signed-file"
      }]
    })
  });
  await waitFor(() => harness.uiState.contexts.length > 0, "bootstrap did not finish");
  const button = { disabled: false, textContent: "Download package" };
  const browseButton = { disabled: false, hidden: true, textContent: "Browse folder" };
  const item = {
    id: "rfi-dynamic",
    itemType: "rfi",
    projectId: "project-dynamic",
    projectName: "CMH04 - Hospital Expansion",
    number: "P000210",
    title: "CMH04 - Overhead Coiling Doors - Overhead Coiling - SD"
  };
  await harness.callbacks.onDownloadItem(item, button, browseButton);

  assert.equal(harness.operations.at(-1), "prepareItemDownload");
  assert.ok(harness.uiState.downloadPackage);
  assert.equal(harness.pickerCalls.length, 1);
  assert.equal(harness.pickerCalls[0].mode, "readwrite");
  assert.equal(harness.pickerCalls[0].startIn, "downloads");
  assert.match(harness.pickerCalls[0].id, /^helix_project-dynamic_[0-9a-f]{8}$/);
  const expectedFolder = "P000210 - Overhead Coiling Doors - Overhead Coiling - SD";
  assert.deepEqual(Array.from(harness.selectedDirectories.keys()), [expectedFolder]);
  assert.deepEqual(Array.from(harness.selectedDirectories.get(expectedFolder).keys()).sort(), [
    "2026-08-03 Review markup.pdf",
    "RFI 0001 - Details.pdf",
  ]);
  assert.equal(harness.runtimeMessages.includes("HELIX_ACTIVITY_REPORT_SAVED"), true);
  assert.equal(button.textContent, "Downloaded 2 files");
  assert.equal(button.disabled, false);
  assert.equal(browseButton.hidden, false);
  assert.equal(browseButton.disabled, false);

  await harness.callbacks.onBrowseDownloadedFolder(item, browseButton);
  assert.equal(harness.pickerCalls.length, 2);
  assert.match(harness.pickerCalls[1].id, /^helix_browse_[0-9a-f]{8}$/);
  assert.equal(harness.pickerCalls[1].mode, "read");
  assert.equal(typeof harness.pickerCalls[1].startIn.getFileHandle, "function");
  assert.equal(browseButton.textContent, "Browse folder");
});

test("package downloads prefix only PDF attachments that have a Helix upload date", async () => {
  const harness = createHarness({
    prepareItemDownloadResult: () => ({
      prepared: true,
      report: {
        kind: "client",
        filename: "RFI 0001 - Details.pdf",
        url: `${origin}/Reports/clientReportViewer?rid=report-1&helixActivityDownload=1`
      },
      attachments: [
        {
          filename: "Drawing.PDF",
          uploadedAt: "2026-08-04T01:15:00.000Z",
          url: "https://storage.googleapis.com/example/drawing"
        },
        {
          filename: "2026-08-04 Existing date.pdf",
          uploadedAt: "2026-08-04T10:00:00.000Z",
          url: "https://storage.googleapis.com/example/existing"
        },
        {
          filename: "Unknown date.pdf",
          uploadedAt: null,
          url: "https://storage.googleapis.com/example/unknown"
        },
        {
          filename: "Checklist.docx",
          uploadedAt: "2026-08-04T10:00:00.000Z",
          url: "https://storage.googleapis.com/example/checklist"
        }
      ]
    })
  });
  await waitFor(() => harness.uiState.contexts.length > 0, "bootstrap did not finish");
  const button = { disabled: false, textContent: "Download package" };

  await harness.callbacks.onDownloadItem({
    id: "rfi-dynamic",
    itemType: "rfi",
    projectId: "project-dynamic",
    projectName: "Dynamic Project",
    number: "0001",
    title: "Dynamic Project - Coordination"
  }, button);

  const files = harness.selectedDirectories.get("0001 - Coordination");
  assert.deepEqual(Array.from(files.keys()).sort(), [
    "2026-08-04 Drawing.PDF",
    "2026-08-04 Existing date.pdf",
    "Checklist.docx",
    "RFI 0001 - Details.pdf",
    "Unknown date.pdf"
  ]);
});

test("folder naming matches an embedded project code and removes repeated dash separators", async () => {
  const harness = createHarness();
  await waitFor(() => harness.uiState.contexts.length > 0, "bootstrap did not finish");
  const button = { disabled: false, textContent: "Download package" };

  await harness.callbacks.onDownloadItem({
    id: "submittal-cmh02",
    itemType: "submittal",
    projectId: "project-cmh02",
    projectName: "Aligned CMH02",
    number: "P000211",
    title: "CMH02 -  - Overhead Coiling Doors -- Electrical"
  }, button);

  assert.deepEqual(Array.from(harness.selectedDirectories.keys()), [
    "P000211 - Overhead Coiling Doors - Electrical"
  ]);
});

test("folder pickers remember one starting location per project across RFIs and Submittals", async () => {
  const harness = createHarness();
  await waitFor(() => harness.uiState.contexts.length > 0, "bootstrap did not finish");
  const download = (item) => harness.callbacks.onDownloadItem(
    item,
    { disabled: false, textContent: "Download package" }
  );

  await download({
    id: "rfi-one",
    itemType: "rfi",
    projectId: "project-shared",
    projectName: "Shared Project",
    number: "0001",
    title: "First item"
  });
  await download({
    id: "submittal-one",
    itemType: "submittal",
    projectId: "project-shared",
    projectName: "Shared Project",
    number: "0002",
    title: "Second item"
  });
  await download({
    id: "rfi-other",
    itemType: "rfi",
    projectId: "project-other",
    projectName: "Other Project",
    number: "0003",
    title: "Third item"
  });

  assert.equal(harness.pickerCalls.length, 3);
  assert.equal(harness.pickerCalls[0].id, harness.pickerCalls[1].id);
  assert.notEqual(harness.pickerCalls[0].id, harness.pickerCalls[2].id);
  assert.equal(harness.pickerCalls.every((call) => call.startIn === "downloads"), true);
});

test("cancelling the destination prompt does not read or download the selected item", async () => {
  const cancellation = new Error("The user aborted a request.");
  cancellation.name = "AbortError";
  const harness = createHarness({ directoryPickerError: cancellation });
  await waitFor(() => harness.uiState.contexts.length > 0, "bootstrap did not finish");
  const button = { disabled: false, textContent: "Download package" };

  await harness.callbacks.onDownloadItem({
    id: "rfi-dynamic",
    itemType: "rfi",
    projectId: "project-dynamic"
  }, button);

  assert.equal(harness.pickerCalls.length, 1);
  assert.equal(harness.operations.includes("prepareItemDownload"), false);
  assert.equal(harness.runtimeMessages.includes("HELIX_ACTIVITY_DOWNLOAD_PACKAGE"), false);
  assert.equal(harness.uiState.openFailure, undefined);
  assert.equal(button.textContent, "Download package");
  assert.equal(button.disabled, false);
});

test("refresh replaces the queue and retains only three recent activity contexts", async () => {
  const cacheKey = "helix-activity-cache-v4:contact-dynamic";
  const activity = (scannedAt, id) => ({ scannedAt, warnings: [], results: [{ id }] });
  const harness = createHarness({
    storageData: {
      [cacheKey]: {
        version: 4,
        storedAt: Date.now(),
        queue: {
          scannedAt: "2026-07-20T00:00:00.000Z",
          warnings: [],
          openQueue: [{ id: "stale-queue-item" }],
          projects: [],
          people: [],
          currentPersonKey: null
        },
        activities: {
          "project-a:rfi": activity("2026-07-20T00:00:00.000Z", "a"),
          "project-b:rfi": activity("2026-07-19T00:00:00.000Z", "b"),
          "project-c:rfi": activity("2026-07-18T00:00:00.000Z", "c"),
          "project-d:rfi": activity("2026-07-17T00:00:00.000Z", "d")
        }
      }
    }
  });

  await waitFor(() => harness.uiState.data && harness.uiState.data.cached, "cached index did not render");
  harness.callbacks.onRefresh();
  await waitFor(() => {
    const cache = harness.storageData[cacheKey];
    return cache && cache.queue.scannedAt === "2026-07-21T00:00:00.000Z";
  }, "refreshed cache was not saved");

  const cache = harness.storageData[cacheKey];
  assert.deepEqual(cache.queue.openQueue, []);
  assert.deepEqual(Object.keys(cache.activities).sort(), [
    "project-a:rfi",
    "project-b:rfi",
    "project-dynamic:rfi"
  ]);
});

test("a cache quota failure remains separate from scan completeness and does not stop Task Hub sync", async () => {
  const harness = createHarness({
    cacheWriteFailure: "quota",
    taskHubStatus: {
      connected: true,
      autoSync: true,
      user: { email: "dynamic@example.com" }
    }
  });
  await waitFor(() => harness.uiState.contexts.length > 0, "bootstrap did not finish");

  harness.callbacks.onOpen();
  await waitFor(() => Boolean(harness.uiState.taskHubSnapshot), "Task Hub sync did not run");
  assert.equal(harness.uiState.taskHubSnapshot.complete, true);
  assert.deepEqual(harness.uiState.data.warnings, []);
  assert.match(harness.uiState.cacheWarnings.at(-1), /Chrome could not save/);
  assert.ok(
    harness.runtimeMessages.indexOf("HELIX_ACTIVITY_CACHE_WRITE")
      < harness.runtimeMessages.indexOf("HELIX_TASKHUB_SYNC")
  );
});

test("content script reuses a fresh person-scoped cache without rebuilding on panel open", async () => {
  const cacheKey = "helix-activity-cache-v4:contact-dynamic";
  const harness = createHarness({
    storageData: {
      [cacheKey]: {
        version: 4,
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

test("cross-project package download keeps the selected directory handle in the current tab", async () => {
  const harness = createHarness({
    prepareItemDownloadResult: () => ({
      prepared: true,
      report: {
        kind: "client",
        filename: "Submittal P000210 - Details.pdf",
        url: `${origin}/Reports/clientReportViewer?rid=report-2&helixActivityDownload=1`
      },
      attachments: []
    })
  });
  await waitFor(() => harness.uiState.contexts.length > 0, "bootstrap did not finish");
  const button = { disabled: false, textContent: "Download package" };
  await harness.callbacks.onDownloadItem({
    id: "submittal-other",
    itemType: "submittal",
    projectId: "project-other",
    projectName: "Other Project",
    number: "P000210",
    title: "Other Project - Cooling door package"
  }, button);

  assert.equal(harness.storageData["helix-activity-navigation-v1:contact-dynamic"], undefined);
  assert.deepEqual(harness.assignedLocations, []);
  const files = harness.selectedDirectories.get("P000210 - Cooling door package");
  assert.equal(files.has("Submittal P000210 - Details.pdf"), true);
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
