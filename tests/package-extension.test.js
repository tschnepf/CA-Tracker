"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..");
const { findTaskHubRepository } = require("../scripts/taskhub-repository.js");

const repositoryRoot = findTaskHubRepository(root);
const packageSource = fs.readFileSync(path.join(root, "scripts", "package-extension.ps1"), "utf8");
const portablePackageSource = fs.readFileSync(
  path.join(root, "scripts", "package-extension.js"),
  "utf8"
);
const readme = fs.readFileSync(path.join(root, "README.md"), "utf8");
const manifest = JSON.parse(fs.readFileSync(path.join(root, "manifest.json"), "utf8"));
const packageMetadata = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));

test("packaging preserves a stable unpacked folder and stable-name archive", () => {
  assert.equal(manifest.version, "2.3.0");
  assert.equal(packageMetadata.version, manifest.version);
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
  assert.match(packageSource, /'integrations'/);
  assert.match(packageSource, /'options'/);
  assert.match(packageSource, /-Recurse/);
  assert.match(portablePackageSource, /"integrations", "options"/);
  assert.match(portablePackageSource, /"procore-content-script\.js"/);
  assert.match(portablePackageSource, /helix-my-activity-\$\{manifest\.version\}\.zip/);
  assert.match(readme, /npm run package/);
});

test("web packaging publishes no-cache metadata from the same manifest as the ZIP", {
  skip: repositoryRoot ? false : "no AnotherTaskManager checkout found (set TASKHUB_REPO_ROOT)"
}, () => {
  const webDockerfile = fs.readFileSync(path.join(repositoryRoot, "infra", "web", "Dockerfile"), "utf8");
  const nginxConfiguration = fs.readFileSync(path.join(repositoryRoot, "infra", "web", "nginx.conf"), "utf8");
  assert.match(
    webDockerfile,
    /dist\/helix-my-activity\/manifest\.json \/usr\/share\/nginx\/html\/browser-extensions\/helix-my-activity\.json/
  );
  assert.match(nginxConfiguration, /location = \/browser-extensions\/helix-my-activity\.json/);
  assert.match(nginxConfiguration, /Cache-Control "private, no-cache"/);
});
