"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { spawnSync } = require("node:child_process");

const root = path.resolve(__dirname, "..");

if (process.platform === "win32") {
  const packaged = spawnSync(
    "powershell.exe",
    ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path.join(__dirname, "package-extension.ps1")],
    { cwd: root, stdio: "inherit" }
  );
  process.exit(packaged.status === null ? 1 : packaged.status);
}

const manifest = JSON.parse(fs.readFileSync(path.join(root, "manifest.json"), "utf8"));
const dist = path.join(root, "dist");
const install = path.join(dist, "helix-my-activity");
const versionedArchive = path.join(dist, `helix-my-activity-${manifest.version}.zip`);
const currentArchive = path.join(dist, "helix-my-activity.zip");
const distPrefix = `${path.resolve(dist)}${path.sep}`;

if (!path.resolve(install).startsWith(distPrefix)) {
  throw new Error("The unpacked install folder must remain inside the extension dist directory.");
}

const files = [
  "manifest.json",
  "background.js",
  "bridge-core.js",
  "page-bridge.js",
  "activity-core.js",
  "ui.js",
  "content-script.js",
  "README.md",
  "PRIVACY.md"
];
const directories = ["integrations", "options"];

fs.rmSync(install, { recursive: true, force: true });
fs.mkdirSync(install, { recursive: true });
files.forEach((file) => fs.copyFileSync(path.join(root, file), path.join(install, file)));
directories.forEach((directory) => {
  fs.cpSync(path.join(root, directory), path.join(install, directory), { recursive: true });
});

fs.rmSync(versionedArchive, { force: true });
fs.rmSync(currentArchive, { force: true });
const zipped = spawnSync("zip", ["-q", "-r", versionedArchive, "."], {
  cwd: install,
  encoding: "utf8"
});
if (zipped.status !== 0) {
  throw new Error(zipped.stderr || "The zip command failed while packaging the extension.");
}
fs.copyFileSync(versionedArchive, currentArchive);

const sha256 = crypto
  .createHash("sha256")
  .update(fs.readFileSync(versionedArchive))
  .digest("hex");
console.log([
  `Install folder: ${install}`,
  `Current archive: ${currentArchive}`,
  `Versioned archive: ${versionedArchive}`,
  `Version: ${manifest.version}`,
  `SHA256: ${sha256}`
].join("\n"));
