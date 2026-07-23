"use strict";

const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const manifestPath = path.join(root, "manifest.json");
const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
const requiredFiles = [
  manifest.background && manifest.background.service_worker,
  ...manifest.content_scripts.flatMap((entry) => [...(entry.js || []), ...(entry.css || [])]),
  "bridge-core.js",
  "page-bridge.js"
].filter(Boolean);

const missing = requiredFiles.filter((file) => !fs.existsSync(path.join(root, file)));
if (missing.length) {
  throw new Error(`Manifest references missing files: ${missing.join(", ")}`);
}
if (manifest.manifest_version !== 3) {
  throw new Error("The extension must use Manifest V3.");
}

console.log(`Validated Manifest V3 extension with ${requiredFiles.length} referenced files.`);
