"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const BridgeCore = require("../bridge-core.js");

const source = fs.readFileSync(path.resolve(__dirname, "..", "page-bridge.js"), "utf8");
const origin = "https://project.helixteam.com";
const nonce = "bridge_nonce_123456789";

function createHarness() {
  const messageListeners = [];
  const posted = [];
  const opened = [];
  const currentUser = {
    _id: "dynamic-user",
    fullName: "Dynamic User",
    refContact: { _id: "dynamic-contact" },
    refCompany: { name: "Dynamic Co" }
  };
  const datasets = {
    HelixProjectDirectory: [{
      _id: "dynamic-user",
      refContact: { _id: "dynamic-contact" },
      refProject: { _id: "dynamic-project", name: "Dynamic Project" },
      isActive: true
    }],
    HelixProjectRFIs: [{
      _id: "rfi-1",
      refProject: { _id: "dynamic-project" },
      number: "0001",
      title: "Dynamic RFI",
      status: { name: "Review" },
      refDirectoryCreator: currentUser,
      _kmd: { ect: "2026-01-01T00:00:00.000Z" }
    }],
    HelixProjectComments: [],
    HelixProjectAttachments: [],
    HelixProjectHistories: []
  };
  const windowObject = {
    location: {
      origin,
      href: `${origin}/Projects/dynamic-project/RFI`
    },
    UserInfo: { directoryInfo: currentUser },
    gridNamespace: {
      collectionName: "HelixProjectRFIs",
      detailEditColumns: [{ module: "workflowRequired" }],
      baseGrid_onManipulateDataOnRead(records) {
        return records.map((record) => {
          record.workflow = record.workflow || {};
          record.workflow.workflowRequired = record.workflow.workflowRequired || [];
          record.workflow.workflowOptional = record.workflow.workflowOptional || [];
          record.workflow.refDirectoriesNotify = record.workflow.refDirectoriesNotify || [];
          return record;
        });
      }
    },
    Master: {
      async readFromCollection(collection, query) {
        const records = datasets[collection] || [];
        return records.slice(query.skip, query.skip + query.limit);
      },
      async loadDetailView(_event, record) {
        opened.push(record._id);
      }
    },
    HelixBridgeCore: BridgeCore,
    __HELIX_ACTIVITY_BRIDGE_NONCE: nonce,
    addEventListener(type, listener) {
      if (type === "message") messageListeners.push(listener);
    },
    postMessage(data, targetOrigin) {
      posted.push({ data, targetOrigin });
      if (typeof windowObject.onPost === "function") windowObject.onPost(data, targetOrigin);
    }
  };

  vm.runInNewContext(source, { window: windowObject, Set, Error });

  function request(operation, payload, requestNonce) {
    const requestId = `request-${posted.length + 1}`;
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => reject(new Error(`No response for ${operation}`)), 2000);
      windowObject.onPost = (data) => {
        if (data.requestId === requestId && data.kind === "response") {
          clearTimeout(timeout);
          resolve(data);
        }
      };
      const event = {
        source: windowObject,
        origin,
        data: {
          source: "helix-activity-extension",
          direction: "to-page",
          nonce: requestNonce || nonce,
          requestId,
          operation,
          payload: payload || null
        }
      };
      messageListeners.forEach((listener) => listener(event));
    });
  }

  return { windowObject, posted, opened, request, messageListeners };
}

test("page bridge bootstraps and scans using the active session identity", async () => {
  BridgeCore._test.resetCache();
  const harness = createHarness();
  const bootstrap = await harness.request("bootstrap");
  assert.equal(bootstrap.ok, true);
  assert.equal(bootstrap.result.user.id, "dynamic-user");
  assert.equal(bootstrap.result.projectId, "dynamic-project");

  const scan = await harness.request("scan");
  assert.equal(scan.ok, true);
  assert.equal(scan.result.context.user.fullName, "Dynamic User");
  assert.deepEqual(Array.from(scan.result.rfis, (rfi) => rfi.id), ["rfi-1"]);
  assert.ok(harness.posted.some((item) => item.data.kind === "progress"));
});

test("page bridge opens a current-page item through the fixed read-only lookup", async () => {
  BridgeCore._test.resetCache();
  const harness = createHarness();
  const opened = await harness.request("openItem", { itemId: "rfi-1" });
  assert.equal(opened.ok, true);
  assert.deepEqual(harness.opened, ["rfi-1"]);

  const rejected = await harness.request("openItem", { itemId: "not-scanned" });
  assert.equal(rejected.ok, false);
  assert.match(rejected.error, /could not be found/);
});

test("page bridge ignores operations outside its allowlist", async () => {
  const harness = createHarness();
  const before = harness.posted.length;
  const event = {
    source: harness.windowObject,
    origin,
    data: {
      source: "helix-activity-extension",
      direction: "to-page",
      nonce,
      requestId: "forbidden-request",
      operation: "arbitraryQuery",
      payload: { collection: "Anything" }
    }
  };
  harness.messageListeners.forEach((listener) => listener(event));
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(harness.posted.length, before);
});
