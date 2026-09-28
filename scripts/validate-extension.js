"use strict";

const crypto = require("node:crypto");
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
  "provider-core.js",
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
if (!manifest.key) {
  throw new Error("The private package manifest must include a public key for a stable extension ID.");
}
if (!manifest.permissions.includes("identity")) {
  throw new Error("The OIDC extension must declare Chrome's identity permission.");
}
if (manifest.permissions.includes("downloads")) {
  throw new Error("Folder-picked package downloads must not require Chrome's downloads permission.");
}
if (manifest.permissions.includes("unlimitedStorage")) {
  throw new Error("The Helix extension must remain within its bounded local-storage budget.");
}

const publicKey = Buffer.from(manifest.key, "base64");
const extensionHex = crypto.createHash("sha256").update(publicKey).digest("hex").slice(0, 32);
const extensionId = Array.from(
  extensionHex,
  (character) => String.fromCharCode(97 + Number.parseInt(character, 16))
).join("");
const redirectUri = `https://${extensionId}.chromiumapp.org/taskhub`;
const procoreRedirectUri = `https://${extensionId}.chromiumapp.org/procore`;
const repositoryRoot = path.resolve(root, "..", "..");
const realm = JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, "tools", "keycloak", "realm-export", "taskhub-realm.json"),
  "utf8"
));
const oidcClient = (realm.clients || []).find((client) => client.clientId === "taskhub-helix-extension");
if (!oidcClient) {
  throw new Error("Keycloak realm is missing the taskhub-helix-extension client.");
}
if (!Array.isArray(oidcClient.redirectUris) || !oidcClient.redirectUris.includes(redirectUri)) {
  throw new Error(`Keycloak must register the pinned extension redirect URI: ${redirectUri}`);
}
if (
  oidcClient.publicClient !== true
  || oidcClient.standardFlowEnabled !== true
  || oidcClient.directAccessGrantsEnabled !== false
  || !oidcClient.attributes
  || oidcClient.attributes["pkce.code.challenge.method"] !== "S256"
) {
  throw new Error("The Helix Keycloak client must be public, standard-flow-only, and require PKCE S256.");
}
const requiredDefaultScopes = ["profile", "email", "roles", "helix.sync", "taskhub-api-audience"];
if (!requiredDefaultScopes.every((scope) => (oidcClient.defaultClientScopes || []).includes(scope))) {
  throw new Error("The Helix Keycloak client is missing required default scopes.");
}
if (!(oidcClient.optionalClientScopes || []).includes("offline_access")) {
  throw new Error("The Helix Keycloak client must allow the offline_access scope.");
}
if (!(realm.clientScopes || []).some((scope) => scope.name === "helix.sync")) {
  throw new Error("The Keycloak realm is missing the helix.sync client scope.");
}
for (const environmentFile of [".env.example", ".env.unraid.production.example"]) {
  const environment = fs.readFileSync(path.join(repositoryRoot, environmentFile), "utf8");
  if (!environment.includes(`KEYCLOAK_HELIX_REDIRECT_URI=${redirectUri}`)) {
    throw new Error(`${environmentFile} does not match the pinned extension redirect URI.`);
  }
  if (!environment.includes(`PROCORE_OAUTH_REDIRECT_URI=${procoreRedirectUri}`)) {
    throw new Error(`${environmentFile} does not match the pinned Procore OAuth redirect URI.`);
  }
}

const procoreMatches = manifest.content_scripts.flatMap((entry) => entry.matches || []);
if (!procoreMatches.includes("https://*.procore.com/webclients/host/*")) {
  throw new Error("The unified extension must activate on modern Procore tool routes.");
}
if (!procoreMatches.includes("https://*.procore.com/*/project/*")) {
  throw new Error("The unified extension must retain context on regional legacy Procore project routes.");
}

console.log(
  `Validated Manifest V3 extension ${extensionId} with ${requiredFiles.length} referenced files.`
);
