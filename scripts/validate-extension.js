"use strict";

const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const manifestPath = path.join(root, "manifest.json");
const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
const packageMetadata = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
const requiredFiles = [
  manifest.background && manifest.background.service_worker,
  ...manifest.content_scripts.flatMap((entry) => [...(entry.js || []), ...(entry.css || [])]),
  manifest.options_ui && manifest.options_ui.page,
  "bridge-core.js",
  "page-bridge.js",
  "integrations/taskhub/client.js",
  "integrations/taskhub/mapper.js",
  "options/options.js",
  "options/options.css"
].filter(Boolean);

const missing = requiredFiles.filter((file) => !fs.existsSync(path.join(root, file)));
if (missing.length) {
  throw new Error(`Manifest references missing files: ${missing.join(", ")}`);
}
if (manifest.manifest_version !== 3) {
  throw new Error("The extension must use Manifest V3.");
}
if (manifest.version !== packageMetadata.version) {
  throw new Error("manifest.json and package.json must use the same extension version.");
}

console.log(`Validated Manifest V3 extension with ${requiredFiles.length} referenced files.`);
