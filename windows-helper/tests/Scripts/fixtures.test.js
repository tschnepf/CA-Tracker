"use strict";

// Checks whose court each item is in against anonymized samples from real Helix projects
// (tests/Scripts/fixtures/*.json, exported by CA Tracker and corrected by hand where it was wrong).

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Snapshot = require("../../src/SmcCaTracker.App/Scripts/snapshot.js");

const folder = path.join(__dirname, "fixtures");
const samples = fs.readdirSync(folder).filter((name) => name.endsWith(".json"));

for (const name of samples) {
  test(`whose court: ${name}`, () => {
    const sample = JSON.parse(fs.readFileSync(path.join(folder, name), "utf8"));
    const snapshot = Snapshot.buildHelixSnapshot({
      context: { projectId: "project-1", projectName: "Sample project", itemType: "overview", user: sample.user },
      items: sample.items,
      warnings: [],
      scannedAt: sample.exportedAt
    });
    for (const expected of sample.expected) {
      const actual = snapshot.items.find((item) => item.itemId === expected.itemId);
      assert.ok(actual, `${expected.number} is missing`);
      assert.deepEqual(
        actual.holders.map((holder) => [holder.name, holder.state]),
        expected.holders.map((holder) => [holder.name, holder.state]),
        `${expected.number}: whose court`);
    }
  });
}

test("fixtures folder is readable", () => {
  assert.ok(Array.isArray(samples));
});
