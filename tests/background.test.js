"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { webcrypto } = require("node:crypto");
const TaskHubClient = require("../integrations/taskhub/client.js");

const source = fs.readFileSync(path.resolve(__dirname, "..", "background.js"), "utf8");

function loadBackground() {
  let listener = null;
  const injections = [];
  const storageData = {};
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
          callback({ [key]: storageData[key] });
        },
        set(value, callback) {
          Object.assign(storageData, value);
          if (callback) callback();
        },
        remove(keys, callback) {
          (Array.isArray(keys) ? keys : [keys]).forEach((key) => delete storageData[key]);
          if (callback) callback();
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
  return { listener, injections };
}

function send(listener, message, sender) {
  return new Promise((resolve) => {
    const keepAlive = listener(message, sender, resolve);
    assert.equal(keepAlive, true);
  });
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
  const sender = {
    id: "test-extension-id",
    url: "chrome-extension://test-extension-id/options/options.html",
    tab: { id: 99, url: "chrome-extension://test-extension-id/options/options.html" }
  };

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
