"use strict";

const fs = require("node:fs");
const path = require("node:path");

// The extension is developed in its own repository, but part of its validation
// checks that it still matches the Task Hub server (Keycloak realm, .env
// examples, web packaging). Those files live in the AnotherTaskManager repo.
//
// Lookup order:
//   1. TASKHUB_REPO_ROOT environment variable
//   2. A sibling checkout: ../AnotherTaskManager
//   3. The legacy in-repo layout: extensions/HelixHelper inside AnotherTaskManager
// Returns null when none is found so callers can skip the server checks.
function isTaskHubRepository(candidate) {
  return Boolean(candidate)
    && fs.existsSync(path.join(candidate, "tools", "keycloak", "realm-export", "taskhub-realm.json"));
}

function findTaskHubRepository(extensionRoot) {
  const candidates = [
    process.env.TASKHUB_REPO_ROOT && path.resolve(process.env.TASKHUB_REPO_ROOT),
    path.resolve(extensionRoot, "..", "AnotherTaskManager"),
    path.resolve(extensionRoot, "..", "..")
  ];
  if (process.env.TASKHUB_REPO_ROOT && !isTaskHubRepository(candidates[0])) {
    throw new Error(`TASKHUB_REPO_ROOT does not point at an AnotherTaskManager checkout: ${candidates[0]}`);
  }
  return candidates.find(isTaskHubRepository) || null;
}

module.exports = { findTaskHubRepository };
