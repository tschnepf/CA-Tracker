"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { webcrypto } = require("node:crypto");
const TaskHubClient = require("../integrations/taskhub/client.js");
const ProviderCore = require("../provider-core.js");
const ProcoreCore = require("../procore-core.js");

const source = fs.readFileSync(path.resolve(__dirname, "..", "background.js"), "utf8");

function loadBackground(options = {}) {
  let listener = null;
  let tabUpdatedListener = null;
  let tabRemovedListener = null;
  const injections = [];
  const createdTabs = [];
  const removedTabs = [];
  let nextTabId = 200;
  const storageData = Object.assign({}, options.storageData);
  let remainingCacheWriteFailures = Number(options.cacheWriteFailures) || 0;
  function requestedKeys(keys) {
    if (keys === null || keys === undefined) return Object.keys(storageData);
    return Array.isArray(keys) ? keys : [keys];
  }
  function bytesForKeys(keys) {
    return requestedKeys(keys).reduce((total, key) => (
      Object.prototype.hasOwnProperty.call(storageData, key)
        ? total + Buffer.byteLength(JSON.stringify({ [key]: storageData[key] }))
        : total
    ), 0);
  }
  const chrome = {
    runtime: {
      id: "test-extension-id",
      lastError: null,
      getURL(value) {
        return `chrome-extension://test-extension-id/${value}`;
      },
      onMessage: {
        addListener(value) {
          listener = value;
        }
      },
      async openOptionsPage() {}
    },
    identity: {
      getRedirectURL(value) {
        return `https://test-extension-id.chromiumapp.org/${value}`;
      }
    },
    scripting: {
      async executeScript(scriptOptions) {
        injections.push(scriptOptions);
        return options.reportExportResult && typeof scriptOptions.func === "function"
          ? [{ result: { started: true, kind: scriptOptions.args[0] } }]
          : [];
      }
    },
    tabs: {
      create(tabOptions, callback) {
        const tab = { id: nextTabId += 1, url: tabOptions.url, active: tabOptions.active };
        createdTabs.push(tab);
        callback(tab);
      },
      remove(tabId, callback) {
        removedTabs.push(tabId);
        if (callback) callback();
      },
      update(_tabId, _options, callback) {
        if (callback) callback();
      },
      onUpdated: {
        addListener(value) { tabUpdatedListener = value; }
      },
      onRemoved: {
        addListener(value) { tabRemovedListener = value; }
      }
    },
    storage: {
      local: {
        get(key, callback) {
          if (key === null || key === undefined) {
            callback(Object.assign({}, storageData));
            return;
          }
          const result = {};
          requestedKeys(key).forEach((storageKey) => {
            if (Object.prototype.hasOwnProperty.call(storageData, storageKey)) {
              result[storageKey] = storageData[storageKey];
            }
          });
          callback(result);
        },
        set(value, callback) {
          const isCacheWrite = Object.keys(value).some((key) => key.startsWith("helix-activity-cache-v4:")
            || key.startsWith("taskhub-activity-cache-v1:"));
          if (isCacheWrite && remainingCacheWriteFailures > 0) {
            remainingCacheWriteFailures -= 1;
            chrome.runtime.lastError = { message: "QUOTA_BYTES quota exceeded" };
            if (callback) callback();
            chrome.runtime.lastError = null;
            return;
          }
          Object.assign(storageData, value);
          if (callback) callback();
        },
        remove(keys, callback) {
          (Array.isArray(keys) ? keys : [keys]).forEach((key) => delete storageData[key]);
          if (callback) callback();
        },
        getBytesInUse(keys, callback) {
          callback(bytesForKeys(keys));
        },
        async setAccessLevel() {}
      }
    }
  };
  const fetchStub = async () => ({
    ok: true,
    status: 200,
    headers: { get: () => "application/json" },
    async json() { return {}; },
    async blob() { return new Blob(["pdf"], { type: "application/pdf" }); }
  });
  vm.runInNewContext(source, Object.assign({
    chrome,
    URL,
    Number,
    Error,
    JSON,
    TextEncoder,
    Uint8Array,
    URLSearchParams,
    btoa,
    crypto: webcrypto,
    fetch: fetchStub,
    Blob,
    setTimeout,
    HelixTaskHubClient: options.taskHubClientModule || TaskHubClient,
    TaskHubProviderCore: ProviderCore,
    TaskHubProcoreCore: ProcoreCore,
    importScripts() {}
  }, options.reportGlobals || {}));
  return {
    listener,
    injections,
    storageData,
    createdTabs,
    removedTabs,
    get tabUpdatedListener() { return tabUpdatedListener; },
    get tabRemovedListener() { return tabRemovedListener; }
  };
}

function send(listener, message, sender) {
  return new Promise((resolve) => {
    const keepAlive = listener(message, sender, resolve);
    assert.equal(keepAlive, true);
  });
}

const helixSender = {
  id: "test-extension-id",
  url: "https://project.helixteam.com/Projects/project-1/RFI",
  tab: { id: 42, url: "https://project.helixteam.com/Projects/project-1/RFI" }
};
const optionsSender = {
  id: "test-extension-id",
  url: "chrome-extension://test-extension-id/options/options.html",
  tab: { id: 99, url: "chrome-extension://test-extension-id/options/options.html" }
};
const procoreSender = {
  id: "test-extension-id",
  url: "https://us02.procore.com/webclients/host/companies/12/projects/34/tools/rfis",
  tab: {
    id: 43,
    url: "https://us02.procore.com/webclients/host/companies/12/projects/34/tools/rfis"
  }
};
const legacyProcoreSender = {
  id: "test-extension-id",
  url: "https://app.procore.com/34/project/rfi/show/99",
  tab: { id: 44, url: "https://app.procore.com/34/project/rfi/show/99" }
};

function cacheValue(storedAt = Date.now(), activityCount = 1, padding = "") {
  const activities = {};
  for (let index = 0; index < activityCount; index += 1) {
    activities[`project-${index}:rfi`] = {
      scannedAt: new Date(storedAt - index * 1000).toISOString(),
      warnings: [],
      results: [{ id: `rfi-${index}` }]
    };
  }
  return {
    version: 4,
    storedAt,
    queue: {
      scannedAt: new Date(storedAt).toISOString(),
      warnings: [],
      openQueue: [],
      projects: [],
      people: [],
      currentPersonKey: null,
      padding
    },
    activities
  };
}

test("background injects only the fixed bridge files into an allowed Helix RFI tab", async () => {
  const { listener, injections } = loadBackground();
  assert.equal(typeof listener, "function");
  const nonce = "valid_nonce_123456789";
  const response = await send(listener, { type: "HELIX_ACTIVITY_INJECT_BRIDGE", nonce }, {
    tab: { id: 42, url: "https://project.helixteam.com/Projects/project-1/RFI" }
  });

  assert.equal(response.ok, true);
  assert.equal(injections.length, 2);
  assert.equal(injections[0].world, "MAIN");
  assert.deepEqual(Array.from(injections[0].target && [injections[0].target.tabId]), [42]);
  assert.deepEqual(Array.from(injections[0].args), [nonce]);
  assert.equal(injections[1].world, "MAIN");
  assert.deepEqual(Array.from(injections[1].files), ["bridge-core.js", "page-bridge.js"]);
});

test("background rejects non-Helix pages and malformed nonces before injection", async () => {
  const { listener, injections } = loadBackground();
  const response = await send(listener, { type: "HELIX_ACTIVITY_INJECT_BRIDGE", nonce: "short" }, {
    tab: { id: 7, url: "https://example.com/Projects/project-1/RFI" }
  });

  assert.equal(response.ok, false);
  assert.equal(injections.length, 0);
});

test("background also allows the fixed Helix Submittals route", async () => {
  const { listener, injections } = loadBackground();
  const response = await send(listener, {
    type: "HELIX_ACTIVITY_INJECT_BRIDGE",
    nonce: "valid_nonce_123456789"
  }, {
    tab: { id: 43, url: "https://project.helixteam.com/Projects/project-1/Submittals" }
  });

  assert.equal(response.ok, true);
  assert.equal(injections.length, 2);
});

test("background also allows the fixed Helix Overview route", async () => {
  const { listener, injections } = loadBackground();
  const response = await send(listener, {
    type: "HELIX_ACTIVITY_INJECT_BRIDGE",
    nonce: "valid_nonce_123456789"
  }, {
    tab: { id: 44, url: "https://project.helixteam.com/Projects/project-1/Overview" }
  });

  assert.equal(response.ok, true);
  assert.equal(injections.length, 2);
});

test("background opens a hidden Details report and closes it after the selected-folder write succeeds", async () => {
  const harness = loadBackground({ reportExportResult: true });
  const channelName = "helix-activity-report-content_nonce_123456789";
  const response = await send(harness.listener, {
    type: "HELIX_ACTIVITY_DOWNLOAD_PACKAGE",
    channelName,
    package: {
      report: {
        name: "Details (RFIs)",
        kind: "client",
        filename: "RFI 0001 - Details.pdf",
        url: "https://project.helixteam.com/Reports/clientReportViewer?rid=report-1&helixActivityDownload=1"
      }
    }
  }, helixSender);

  assert.equal(response.ok, true);
  assert.equal(harness.createdTabs.length, 1);
  assert.equal(harness.createdTabs[0].active, false);

  harness.tabUpdatedListener(harness.createdTabs[0].id, { status: "complete" });
  await new Promise((resolve) => setImmediate(resolve));
  assert.deepEqual(Array.from(harness.injections.at(-1).args), [
    "client",
    channelName,
    "RFI 0001 - Details.pdf"
  ]);

  const finished = await send(harness.listener, {
    type: "HELIX_ACTIVITY_REPORT_SAVED",
    channelName
  }, helixSender);
  assert.equal(finished.ok, true);
  assert.equal(finished.result.finished, true);
  assert.deepEqual(harness.removedTabs, [harness.createdTabs[0].id]);
});

test("Details export capture waits for Helix and handles standard and paginated Kendo PDF saves", async () => {
  const posted = [];
  let exportMode = "standard";
  const originalKendoSaveAs = function originalKendoSaveAs() {};
  const originalPdfSaveAs = function originalPdfSaveAs() {};
  const reportWindow = {
    getComputedStyle() { return { display: "none" }; },
    kendo: {
      saveAs: originalKendoSaveAs,
      drawing: {
        exportPDF: async () => "data:application/pdf;base64,cGRm",
        pdf: { saveAs: originalPdfSaveAs }
      }
    }
  };
  const jquery = function jquery() {};
  jquery._data = () => ({ click: [{}] });
  reportWindow.jQuery = jquery;
  const exportButton = {
    disabled: false,
    textContent: "Download",
    click() {
      if (exportMode === "paginated") {
        reportWindow.kendo.saveAs({
          dataURI: "data:application/pdf;base64,cGRm",
          fileName: "Paginated Details.pdf"
        });
      } else {
        reportWindow.kendo.drawing.pdf.saveAs({}, "Standard Details.pdf");
      }
    }
  };
  const reportDocument = {
    getElementById(id) {
      if (id === "loadingMask") return {};
      if (id === "export") return exportButton;
      return null;
    },
    querySelector() { return null; },
    querySelectorAll() { return []; }
  };
  class BroadcastChannelMock {
    constructor(name) { this.name = name; }
    postMessage(message) { posted.push({ channelName: this.name, message }); }
    close() {}
  }
  const harness = loadBackground({
    reportExportResult: true,
    reportGlobals: {
      window: reportWindow,
      document: reportDocument,
      BroadcastChannel: BroadcastChannelMock
    }
  });
  const channelName = "helix-activity-report-capture_nonce_123456789";
  await send(harness.listener, {
    type: "HELIX_ACTIVITY_DOWNLOAD_PACKAGE",
    channelName,
    package: {
      report: {
        kind: "client",
        filename: "Details.pdf",
        url: "https://project.helixteam.com/Reports/clientReportViewer?rid=report-1&helixActivityDownload=1"
      }
    }
  }, helixSender);
  harness.tabUpdatedListener(harness.createdTabs[0].id, { status: "complete" });
  await new Promise((resolve) => setImmediate(resolve));
  const capture = harness.injections.at(-1).func;

  await capture("client", channelName, "Details.pdf");
  await new Promise((resolve) => setImmediate(resolve));
  exportMode = "paginated";
  await capture("client", `${channelName}_page`, "Details.pdf");
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual(posted.map((entry) => entry.message.type), [
    "helix-report-ready",
    "helix-report-ready"
  ]);
  assert.deepEqual(posted.map((entry) => entry.message.filename), [
    "Standard Details.pdf",
    "Paginated Details.pdf"
  ]);
  assert.equal(reportWindow.kendo.saveAs, originalKendoSaveAs);
  assert.equal(reportWindow.kendo.drawing.pdf.saveAs, originalPdfSaveAs);
});

test("background rejects package downloads with an untrusted report URL or transfer channel", async () => {
  const { listener, createdTabs } = loadBackground();
  const response = await send(listener, {
    type: "HELIX_ACTIVITY_DOWNLOAD_PACKAGE",
    channelName: "bad-channel",
    package: {
      report: {
        kind: "client",
        url: "https://example.com/Reports/clientReportViewer?helixActivityDownload=1"
      }
    }
  }, helixSender);
  assert.equal(response.ok, false);
  assert.equal(createdTabs.length, 0);
});

test("background accepts Task Hub requests from the tab-based extension options page", async () => {
  const { listener } = loadBackground();
  const sender = optionsSender;

  const status = await send(listener, { type: "HELIX_TASKHUB_STATUS" }, sender);
  assert.equal(status.ok, true);
  assert.equal(status.result.connected, false);

  const login = await send(listener, {
    type: "HELIX_TASKHUB_OIDC_LOGIN",
    baseUrl: "https://tasks.example.com"
  }, sender);
  assert.equal(login.ok, false);
  assert.doesNotMatch(login.error, /must start from extension settings/);
});

test("background rejects Task Hub login from Helix and unrelated extension pages", async () => {
  const { listener } = loadBackground();
  const helix = await send(listener, {
    type: "HELIX_TASKHUB_OIDC_LOGIN",
    baseUrl: "https://tasks.example.com"
  }, {
    id: "test-extension-id",
    url: "https://project.helixteam.com/Projects/project-1/RFI",
    tab: { id: 42, url: "https://project.helixteam.com/Projects/project-1/RFI" }
  });
  assert.equal(helix.ok, false);
  assert.match(helix.error, /must start from extension settings/);

  const unrelated = await send(listener, {
    type: "HELIX_TASKHUB_OIDC_LOGIN",
    baseUrl: "https://tasks.example.com"
  }, {
    id: "test-extension-id",
    url: "chrome-extension://test-extension-id/other.html",
    tab: { id: 100, url: "chrome-extension://test-extension-id/other.html" }
  });
  assert.equal(unrelated.ok, false);
  assert.match(unrelated.error, /must start from extension settings/);
});

test("cache status removes obsolete, malformed, and expired activity caches", async () => {
  const now = Date.now();
  const currentKey = "helix-activity-cache-v4:current";
  const { listener, storageData } = loadBackground({
    storageData: {
      [currentKey]: cacheValue(now),
      "helix-activity-cache-v4:expired": cacheValue(now - 25 * 60 * 60 * 1000),
      "helix-activity-cache-v4:malformed": { version: 4, storedAt: now },
      "helix-activity-cache-v3:legacy": { version: 3, storedAt: now },
      "helix-taskhub-settings-v1": { baseUrl: "https://tasks.example.com" }
    }
  });

  const response = await send(listener, { type: "HELIX_ACTIVITY_CACHE_STATUS" }, optionsSender);
  assert.equal(response.ok, true);
  assert.equal(response.result.entryCount, 1);
  assert.equal(response.result.budgetBytes, 5 * 1024 * 1024);
  assert.ok(response.result.bytesInUse > 0);
  assert.ok(storageData[currentKey]);
  assert.equal(storageData["helix-activity-cache-v4:expired"], undefined);
  assert.equal(storageData["helix-activity-cache-v4:malformed"], undefined);
  assert.equal(storageData["helix-activity-cache-v3:legacy"], undefined);
  assert.deepEqual(storageData["helix-taskhub-settings-v1"], { baseUrl: "https://tasks.example.com" });
});

test("cache writes retain only the three newest activity contexts", async () => {
  const { listener, storageData } = loadBackground();
  const key = "helix-activity-cache-v4:person";
  const response = await send(listener, {
    type: "HELIX_ACTIVITY_CACHE_WRITE",
    key,
    value: cacheValue(Date.now(), 6)
  }, helixSender);

  assert.equal(response.ok, true);
  assert.equal(response.result.cached, true);
  assert.deepEqual(Object.keys(storageData[key].activities), [
    "project-0:rfi",
    "project-1:rfi",
    "project-2:rfi"
  ]);
});

test("cache writes evict the oldest identities before exceeding the five-megabyte budget", async () => {
  const now = Date.now();
  const padding = "x".repeat(1_700_000);
  const { listener, storageData } = loadBackground({
    storageData: {
      "helix-activity-cache-v4:oldest": cacheValue(now - 3000, 1, padding),
      "helix-activity-cache-v4:middle": cacheValue(now - 2000, 1, padding),
      "helix-activity-cache-v4:newest": cacheValue(now - 1000, 1, padding)
    }
  });

  const response = await send(listener, {
    type: "HELIX_ACTIVITY_CACHE_WRITE",
    key: "helix-activity-cache-v4:current",
    value: cacheValue(now, 1, "y".repeat(1_000_000))
  }, helixSender);

  assert.equal(response.ok, true);
  assert.equal(response.result.cached, true);
  assert.equal(storageData["helix-activity-cache-v4:oldest"], undefined);
  assert.ok(response.result.bytesInUse <= 5 * 1024 * 1024);
});

test("concurrent cache writes are serialized under the shared cache budget", async () => {
  const { listener } = loadBackground();
  const padding = "x".repeat(1_800_000);
  const responses = await Promise.all(["one", "two", "three"].map((person, index) => send(listener, {
    type: "HELIX_ACTIVITY_CACHE_WRITE",
    key: `helix-activity-cache-v4:${person}`,
    value: cacheValue(Date.now() + index, 1, padding)
  }, helixSender)));
  assert.ok(responses.every((response) => response.ok && response.result.cached));

  const status = await send(listener, { type: "HELIX_ACTIVITY_CACHE_STATUS" }, optionsSender);
  assert.equal(status.ok, true);
  assert.ok(status.result.bytesInUse <= 5 * 1024 * 1024);
  assert.equal(status.result.entryCount, 2);
});

test("an oversized person cache is skipped and its stale entry is removed", async () => {
  const key = "helix-activity-cache-v4:large";
  const { listener, storageData } = loadBackground({
    storageData: { [key]: cacheValue(Date.now() - 1000) }
  });
  const response = await send(listener, {
    type: "HELIX_ACTIVITY_CACHE_WRITE",
    key,
    value: cacheValue(Date.now(), 1, "x".repeat(2 * 1024 * 1024))
  }, helixSender);

  assert.equal(response.ok, true);
  assert.equal(response.result.cached, false);
  assert.equal(response.result.reason, "too_large");
  assert.equal(storageData[key], undefined);
});

test("a quota error evicts older caches and retries exactly once", async () => {
  const key = "helix-activity-cache-v4:current";
  const { listener, storageData } = loadBackground({
    cacheWriteFailures: 1,
    storageData: {
      "helix-activity-cache-v4:older": cacheValue(Date.now() - 1000)
    }
  });
  const response = await send(listener, {
    type: "HELIX_ACTIVITY_CACHE_WRITE",
    key,
    value: cacheValue()
  }, helixSender);

  assert.equal(response.ok, true);
  assert.equal(response.result.cached, true);
  assert.equal(storageData["helix-activity-cache-v4:older"], undefined);
  assert.ok(storageData[key]);
});

test("a repeated quota error returns a nonfatal result and removes the failed entry", async () => {
  const key = "helix-activity-cache-v4:current";
  const { listener, storageData } = loadBackground({ cacheWriteFailures: 2 });
  const response = await send(listener, {
    type: "HELIX_ACTIVITY_CACHE_WRITE",
    key,
    value: cacheValue()
  }, helixSender);

  assert.equal(response.ok, true);
  assert.equal(response.result.cached, false);
  assert.equal(response.result.reason, "quota");
  assert.equal(storageData[key], undefined);
});

test("clearing the activity cache preserves Task Hub credentials and UI settings", async () => {
  const preserved = {
    "helix-taskhub-auth-v1": { accessToken: "token" },
    "helix-taskhub-settings-v1": { baseUrl: "https://tasks.example.com" },
    "helix-activity-ui-preferences-v1": { panelOpen: true },
    "helix-activity-view-state-v1:person": { state: { view: "queue" } },
    "helix-activity-navigation-v1:person": { itemId: "rfi-1" }
  };
  const { listener, storageData } = loadBackground({
    storageData: Object.assign({
      "helix-activity-cache-v4:person": cacheValue(),
      "helix-activity-cache-v3:legacy": { version: 3, storedAt: Date.now() }
    }, preserved)
  });
  const response = await send(listener, { type: "HELIX_ACTIVITY_CACHE_CLEAR" }, optionsSender);

  assert.equal(response.ok, true);
  assert.equal(response.result.entriesCleared, 1);
  assert.ok(response.result.bytesCleared > 0);
  assert.equal(storageData["helix-activity-cache-v3:legacy"], undefined);
  assert.deepEqual(
    Object.fromEntries(Object.keys(preserved).map((key) => [key, storageData[key]])),
    preserved
  );
});

test("cache management messages enforce their trusted senders", async () => {
  const { listener } = loadBackground();
  const status = await send(listener, { type: "HELIX_ACTIVITY_CACHE_STATUS" }, helixSender);
  assert.equal(status.ok, false);
  assert.match(status.error, /extension settings/);

  const clear = await send(listener, { type: "HELIX_ACTIVITY_CACHE_CLEAR" }, {
    id: "test-extension-id",
    url: "chrome-extension://test-extension-id/other.html",
    tab: { id: 100, url: "chrome-extension://test-extension-id/other.html" }
  });
  assert.equal(clear.ok, false);
  assert.match(clear.error, /extension settings/);

  const write = await send(listener, {
    type: "HELIX_ACTIVITY_CACHE_WRITE",
    key: "helix-activity-cache-v4:person",
    value: cacheValue()
  }, optionsSender);
  assert.equal(write.ok, false);
  assert.match(write.error, /supported Helix page/);
});

test("background routes Procore requests only from supported Procore tabs", async () => {
  const calls = [];
  const fakeClient = {
    create() {
      return {
        restrictCredentialStorage: async () => {},
        status: async () => ({ user: { id: "taskhub-user" } }),
        procoreQueue: async (context) => {
          calls.push(["queue", context]);
          return { items: [{ project_id: "34" }] };
        },
        syncProcore: async (context) => { calls.push(["sync", context]); return { created: 0 }; },
        syncProcoreSnapshot: async (snapshot) => { calls.push(["snapshot", snapshot]); return { created: 1 }; }
      };
    }
  };
  const { listener } = loadBackground({ taskHubClientModule: fakeClient });

  const accepted = await send(listener, {
    type: "PROCORE_QUEUE",
    context: { companyId: "12", projectId: "34" }
  }, procoreSender);
  const rejected = await send(listener, {
    type: "PROCORE_SYNC",
    context: { companyId: "12", projectId: "34" }
  }, helixSender);
  const snapshot = await send(listener, {
    type: "PROCORE_SNAPSHOT_SYNC",
    snapshot: { schema_version: 1, company_id: "12", items: [] }
  }, procoreSender);

  assert.equal(accepted.ok, true);
  assert.equal(rejected.ok, false);
  assert.equal(snapshot.ok, true);
  assert.deepEqual(calls, [
    ["queue", { companyId: "12", projectId: "34" }],
    ["snapshot", { schema_version: 1, company_id: "12", items: [] }]
  ]);

  const resolved = await send(listener, {
    type: "PROCORE_CONTEXT_RESOLVE",
    projectId: "34"
  }, legacyProcoreSender);
  assert.equal(resolved.result.companyId, "12");
});

test("unified Procore caches share the bounded activity storage budget", async () => {
  const { listener, storageData } = loadBackground();
  const key = "taskhub-activity-cache-v1:procore:12:person";
  const value = {
    version: 1,
    provider: "procore",
    storedAt: Date.now(),
    index: { results: [], openQueue: [] }
  };

  const response = await send(listener, {
    type: "TASKHUB_ACTIVITY_CACHE_WRITE",
    key,
    value
  }, procoreSender);
  const read = await send(listener, {
    type: "TASKHUB_ACTIVITY_CACHE_READ",
    key
  }, procoreSender);

  assert.equal(response.ok, true);
  assert.deepEqual(storageData[key], value);
  assert.deepEqual(read.result, value);
});

test("standalone Procore discovery is locally persisted and owned by its starting tab", async () => {
  const { listener, storageData } = loadBackground();
  const started = await send(listener, {
    type: "PROCORE_DISCOVERY_START",
    origin: {
      url: "https://us02.procore.com/webclients/host/companies/12/projects/34/tools/rfis",
      companyId: "12",
      companyName: "Example Company"
    }
  }, procoreSender);

  assert.equal(started.ok, true);
  assert.equal(started.result.isOwner, true);
  assert.equal(started.result.catalog.scan.status, "running");
  assert.equal(started.result.catalog.scan.ownerTabId, 43);

  const catalog = started.result.catalog;
  catalog.companies = [{
    id: "12",
    name: "Example Company",
    projects: [{
      id: "34",
      name: "Airport",
      url: "https://us02.procore.com/34/project/home"
    }]
  }];
  catalog.scan.phase = "scan";
  catalog.scan.pendingCompanies = ["Other Company"];
  const updated = await send(listener, {
    type: "PROCORE_DISCOVERY_WRITE",
    catalog
  }, procoreSender);
  assert.equal(updated.ok, true);
  assert.equal(storageData["taskhub-procore-discovery-v1"].companies[0].projects[0].id, "34");

  const read = await send(listener, { type: "PROCORE_DISCOVERY_READ" }, procoreSender);
  assert.equal(read.ok, true);
  assert.equal(read.result.isOwner, true);
  assert.equal(read.result.catalog.scan.pendingCompanies[0], "Other Company");

  const otherTab = {
    id: "test-extension-id",
    url: procoreSender.url,
    tab: { id: 77, url: procoreSender.url }
  };
  const rejected = await send(listener, {
    type: "PROCORE_DISCOVERY_WRITE",
    catalog
  }, otherTab);
  assert.equal(rejected.ok, false);
  assert.match(rejected.error, /started discovery/);
});

test("standalone Procore discovery rejects unsafe catalog project links", async () => {
  const { listener, storageData } = loadBackground();
  const started = await send(listener, {
    type: "PROCORE_DISCOVERY_START",
    origin: {
      url: "https://us02.procore.com/webclients/host/companies/12/tools/opentasks",
      companyId: "12",
      companyName: "Example Company"
    }
  }, procoreSender);
  const catalog = started.result.catalog;
  catalog.companies = [{
    id: "12",
    name: "Example Company",
    projects: [{ id: "34", name: "Unsafe", url: "javascript:alert(1)" }]
  }];
  const updated = await send(listener, { type: "PROCORE_DISCOVERY_WRITE", catalog }, procoreSender);
  assert.equal(updated.ok, true);
  assert.equal(storageData["taskhub-procore-discovery-v1"].companies[0].projects.length, 0);
});

test("cancelling Procore discovery retains the catalog and requests origin restoration", async () => {
  const { listener } = loadBackground();
  await send(listener, {
    type: "PROCORE_DISCOVERY_START",
    origin: {
      url: "https://us02.procore.com/webclients/host/companies/12/tools/opentasks",
      companyId: "12",
      companyName: "Example Company"
    }
  }, procoreSender);
  const cancelled = await send(listener, { type: "PROCORE_DISCOVERY_CANCEL" }, procoreSender);
  assert.equal(cancelled.ok, true);
  assert.equal(cancelled.result.catalog.scan.status, "running");
  assert.equal(cancelled.result.catalog.scan.phase, "restore");
  assert.equal(cancelled.result.catalog.scan.cancelRequested, true);
  assert.deepEqual(Array.from(cancelled.result.catalog.scan.pendingCompanies), []);
});

test("closing the owner tab marks an active Procore discovery scan as failed", async () => {
  const harness = loadBackground();
  await send(harness.listener, {
    type: "PROCORE_DISCOVERY_START",
    origin: {
      url: "https://us02.procore.com/webclients/host/companies/12/tools/opentasks",
      companyId: "12",
      companyName: "Example Company"
    }
  }, procoreSender);
  harness.tabRemovedListener(43);
  await new Promise((resolve) => setImmediate(resolve));
  const catalog = harness.storageData["taskhub-procore-discovery-v1"];
  assert.equal(catalog.scan.status, "error");
  assert.match(catalog.scan.error, /tab.*closed/i);
});
