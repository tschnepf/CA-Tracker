"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { webcrypto } = require("node:crypto");
const TaskHubClient = require("../integrations/taskhub/client.js");

const source = fs.readFileSync(path.resolve(__dirname, "..", "background.js"), "utf8");

function loadBackground(options = {}) {
  let listener = null;
  const injections = [];
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
      async executeScript(options) {
        injections.push(options);
        return [];
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
          const isCacheWrite = Object.keys(value).some((key) => key.startsWith("helix-activity-cache-v4:"));
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
    async json() { return {}; }
  });
  vm.runInNewContext(source, {
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
    HelixTaskHubClient: TaskHubClient,
    importScripts() {}
  });
  return { listener, injections, storageData };
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
