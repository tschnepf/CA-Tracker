"use strict";

// Checks whose court each item is in against anonymized samples from real Helix projects
// (exported by CA Tracker and corrected by hand where it was wrong). Samples are read from
// tests/Scripts/fixtures/*.json and, kept out of the repo because they come from live projects, from
// the folder named by CA_TRACKER_TEST_DATA (default C:\Local\CA-Tracker-Test-Data) when it exists.

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Snapshot = require("../../src/SmcCaTracker.App/Scripts/snapshot.js");

const folders = [path.join(__dirname, "fixtures"), process.env.CA_TRACKER_TEST_DATA || "C:\\Local\\CA-Tracker-Test-Data"]
  .filter((folder) => fs.existsSync(folder));
const samples = folders.flatMap((folder) => fs.readdirSync(folder)
  .filter((name) => name.endsWith(".json"))
  .map((name) => path.join(folder, name)));

for (const file of samples) {
  test(`whose court: ${path.basename(file)}`, () => {
    const sample = JSON.parse(fs.readFileSync(file, "utf8"));
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
