"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const packageSource = fs.readFileSync(path.join(root, "scripts", "package-extension.ps1"), "utf8");
const readme = fs.readFileSync(path.join(root, "README.md"), "utf8");

test("packaging preserves a stable unpacked folder and stable-name archive", () => {
  assert.match(packageSource, /\$install = Join-Path \$dist 'helix-my-activity'/);
  assert.match(packageSource, /\$currentArchive = Join-Path \$dist 'helix-my-activity\.zip'/);
  assert.match(packageSource, /InstallFolder = \$install/);
  assert.ok(
    packageSource.lastIndexOf("Remove-Item -LiteralPath $install")
      < packageSource.indexOf("Compress-Archive"),
    "The unpacked install folder must remain after packaging"
  );
  assert.match(readme, /select \*\*Reload\*\* on the existing extension/);
  assert.match(readme, /Do not select \*\*Load unpacked\*\* again/);
});
