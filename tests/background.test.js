"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.resolve(__dirname, "..", "background.js"), "utf8");

function loadBackground() {
  let listener = null;
  const injections = [];
  const chrome = {
    runtime: {
      onMessage: {
        addListener(value) {
          listener = value;
        }
      }
    },
    scripting: {
      async executeScript(options) {
        injections.push(options);
        return [];
      }
    }
  };
  vm.runInNewContext(source, { chrome, URL, Number, Error });
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

test("background rejects non-Helix pages and malformed nonces before injection", () => {
  const { listener, injections } = loadBackground();
  let response = null;
  const keepAlive = listener({ type: "HELIX_ACTIVITY_INJECT_BRIDGE", nonce: "short" }, {
    tab: { id: 7, url: "https://example.com/Projects/project-1/RFI" }
  }, (value) => { response = value; });

  assert.equal(keepAlive, false);
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
