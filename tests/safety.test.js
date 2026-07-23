"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const manifest = JSON.parse(fs.readFileSync(path.join(root, "manifest.json"), "utf8"));
const runtimeFiles = [
  "background.js",
  "bridge-core.js",
  "page-bridge.js",
  "activity-core.js",
  "ui.js",
  "content-script.js"
];

test("manifest uses only the required bridge, storage, and OIDC permissions", () => {
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual(manifest.permissions, ["scripting", "storage", "identity"]);
  assert.deepEqual(manifest.host_permissions, ["https://project.helixteam.com/*"]);
  assert.deepEqual(manifest.optional_host_permissions, [
    "https://*/*",
    "http://localhost/*",
    "http://127.0.0.1/*"
  ]);
  assert.equal(manifest.permissions.includes("webRequest"), false);
  assert.equal(manifest.permissions.includes("downloads"), false);
  assert.ok(manifest.key);
});

test("network access is isolated to the optional Task Hub client", () => {
  const client = fs.readFileSync(
    path.join(root, "integrations", "taskhub", "client.js"),
    "utf8"
  );
  assert.match(client, /\bfetchFunction\s*\(/);
  assert.doesNotMatch(client, /project\.helixteam\.com/);
  assert.match(client, /\/integrations\/helix\/sync/);
  assert.match(client, /\/integrations\/helix\/auth\/config/);
  assert.doesNotMatch(client, /\/integrations\/helix\/auth\/login/);
  assert.doesNotMatch(client, /password\s*:/i);
});

test("Task Hub settings contain no direct credential fields", () => {
  const options = fs.readFileSync(path.join(root, "options", "options.html"), "utf8");
  assert.doesNotMatch(options, /type=["']password["']/i);
  assert.doesNotMatch(options, /name=["']email["']/i);
  assert.match(options, /Sign in with TaskHub ID/);
});

test("runtime code contains no Helix write, upload, delete, or generic network primitives", () => {
  const source = runtimeFiles.map((file) => fs.readFileSync(path.join(root, file), "utf8")).join("\n");
  const forbiddenPatterns = [
    /\.saveToCollection\s*\(/,
    /\.removeByIdFromCollection\s*\(/,
    /\.uploadFile\s*\(/,
    /\bfetch\s*\(/,
    /new\s+XMLHttpRequest\s*\(/,
    /\bindexedDB\b/,
    /\blocalStorage\b/,
    /\bsessionStorage\b/
  ];
  forbiddenPatterns.forEach((pattern) => assert.doesNotMatch(source, pattern));
});

test("page bridge exposes only fixed operations", () => {
  const source = fs.readFileSync(path.join(root, "page-bridge.js"), "utf8");
  const match = source.match(/ALLOWED_OPERATIONS\s*=\s*new Set\(\[([^\]]+)\]\)/);
  assert.ok(match, "Expected an explicit operation allowlist");
  const operations = Array.from(match[1].matchAll(/"([^"]+)"/g), (item) => item[1]);
  assert.deepEqual(operations, ["bootstrap", "scan", "openItem"]);
});
