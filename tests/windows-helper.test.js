"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const Snapshot = require("../integrations/windows-helper/snapshot.js");
const HelperClient = require("../integrations/windows-helper/client.js");

const root = path.resolve(__dirname, "..");

const us = { id: "d-me", contactId: "c-me", fullName: "Pat Reviewer", companyId: "co-smc", company: "SMC" };
const coworker = { id: "d-cw", contactId: "c-cw", fullName: "Sam Coworker", companyId: "co-smc", company: "SMC" };
const architect = { id: "d-ar", contactId: "c-ar", fullName: "Alex Architect", companyId: "co-arch", company: "Arch" };

function item(overrides) {
  return Object.assign({
    id: "i1",
    projectId: "p1",
    itemType: "submittal",
    number: "P000210",
    title: "Overhead Coiling Doors",
    status: "Review",
    statusRank: 800,
    responsible: "",
    attachmentCount: 2,
    dates: { created: "2026-08-01T00:00:00Z", modified: "2026-09-01T00:00:00Z" },
    workflow: { required: [], optional: [], notify: [] }
  }, overrides);
}

function scan(items, contextOverrides) {
  return {
    context: Object.assign({
      projectId: "p1",
      projectName: "Aligned CMH04",
      itemType: "overview",
      user: us
    }, contextOverrides),
    items,
    warnings: [],
    scannedAt: "2026-09-28T20:00:00Z"
  };
}

test("Helix snapshots list every current-project item with company-aware holders", () => {
  const items = [
    item({ id: "held", workflow: { required: [{ assigned: coworker }, { assigned: architect }] } }),
    item({ id: "returned", workflow: { required: [{ assigned: us, reviewedAt: "2026-09-02T00:00:00Z" }, { assigned: architect }] } }),
    item({ id: "later", workflow: { required: [{ assigned: architect }, { assigned: us }] } }),
    item({ id: "closed", status: "Complete", statusRank: 1400, workflow: { required: [{ assigned: us }] } }),
    item({ id: "rfi", itemType: "rfi", number: "RFI-012", status: "Review", responsible: "Waiting on Pat Reviewer" })
  ];
  const snapshot = Snapshot.buildHelixSnapshot(scan(items));
  assert.equal(snapshot.provider, "helix");
  assert.equal(snapshot.projectId, "p1");
  assert.deepEqual(snapshot.itemTypes, ["rfi", "submittal"]);
  assert.equal(snapshot.complete, true);
  assert.equal(snapshot.user.companyId, "co-smc");
  const byId = new Map(snapshot.items.map((entry) => [entry.itemId, entry]));

  assert.deepEqual(byId.get("held").holders.map((h) => [h.name, h.companyId, h.state]), [
    ["Sam Coworker", "co-smc", "current"],
    ["Alex Architect", "co-arch", "upcoming"]
  ]);
  assert.deepEqual(byId.get("returned").holders.map((h) => [h.companyId, h.state]), [["co-arch", "current"]]);
  assert.deepEqual(byId.get("later").holders.map((h) => [h.companyId, h.state]), [
    ["co-smc", "upcoming"],
    ["co-arch", "current"]
  ]);
  assert.equal(byId.get("closed").closed, true);
  assert.deepEqual(byId.get("closed").holders, []);
  assert.deepEqual(byId.get("rfi").holders.map((h) => [h.name, h.state]), [["Pat Reviewer", "current"]]);
  assert.match(byId.get("rfi").sourceUrl, /\/Projects\/p1\/RFI#helix-activity-open=rfi$/);
  assert.equal(byId.get("held").attachmentCount, 2);
});

test("Helix snapshots only claim the item types the scan fully read", () => {
  const rfiPage = Snapshot.buildHelixSnapshot(scan([item({ itemType: "rfi" })], { itemType: "rfi" }));
  assert.deepEqual(rfiPage.itemTypes, ["rfi"]);
  assert.equal(rfiPage.items.length, 1);

  const partial = scan([item({ id: "a", itemType: "rfi" }), item({ id: "b" })]);
  partial.warnings = [{ collection: "current-project Submittals", message: "denied" }];
  const snapshot = Snapshot.buildHelixSnapshot(partial);
  assert.deepEqual(snapshot.itemTypes, ["rfi"]);
  assert.deepEqual(snapshot.items.map((entry) => entry.itemId), ["a"]);
});

test("Helix snapshots exclude items from other projects and contain no workflow internals", () => {
  const snapshot = Snapshot.buildHelixSnapshot(scan([
    item({ id: "mine" }),
    item({ id: "other", projectId: "p2" })
  ]));
  assert.deepEqual(snapshot.items.map((entry) => entry.itemId), ["mine"]);
  const serialized = JSON.stringify(snapshot);
  assert.doesNotMatch(serialized, /workflow|contactId|c-me/);
});

test("Procore snapshots are grouped by project and always partial", () => {
  const snapshots = Snapshot.buildProcoreSnapshots({
    scanned_at: "2026-09-28T20:00:00Z",
    items: [
      { item_id: "11", item_type: "rfi", project_id: "900", project_name: "Tower", number: "12", title: "Slab edge", status: "Open", account_id: "5", source_url: "https://app.procore.com/900/project/rfi/show/11" },
      { item_id: "12", item_type: "submittal", project_id: "900", project_name: "Tower", number: "03 30 00-1", title: "Concrete", status: "Closed" },
      { item_id: "13", item_type: "rfi", project_id: "901", project_name: "Garage", number: "1", title: "Ramp", status: "Draft" }
    ]
  });
  assert.equal(snapshots.length, 2);
  const tower = snapshots.find((entry) => entry.projectId === "900");
  assert.equal(tower.complete, false);
  assert.equal(tower.provider, "procore");
  assert.deepEqual(tower.itemTypes, ["rfi", "submittal"]);
  assert.deepEqual(tower.items.map((entry) => [entry.number, entry.closed]), [["12", false], ["03 30 00-1", true]]);
});

function fakeChrome(options) {
  const sent = [];
  return {
    sent,
    chrome: {
      permissions: {
        contains(_request, callback) { callback(options.permission === true); }
      },
      runtime: {
        lastError: null,
        sendNativeMessage: options.native === false ? undefined : function sendNativeMessage(host, message, callback) {
          sent.push({ host, message });
          if (options.missing) {
            this.lastError = { message: "Specified native messaging host not found." };
            callback(undefined);
            this.lastError = null;
            return;
          }
          callback({ ok: true, version: "1.0.0" });
        }
      }
    }
  };
}

const minimalSnapshot = { provider: "helix", projectId: "p1", itemTypes: ["rfi"], items: [] };

test("helper client stays quiet when the optional permission is not granted", async () => {
  const fake = fakeChrome({ permission: false });
  const client = HelperClient.create({ chrome: fake.chrome });
  assert.deepEqual(await client.status(), { permission: false, installed: false });
  assert.deepEqual(await client.sendSnapshot(minimalSnapshot), { sent: false, reason: "not_enabled" });
  assert.equal(fake.sent.length, 0);
});

test("helper client reports a missing native host without throwing", async () => {
  const fake = fakeChrome({ permission: true, missing: true });
  const client = HelperClient.create({ chrome: fake.chrome });
  const status = await client.status();
  assert.equal(status.permission, true);
  assert.equal(status.installed, false);
  assert.match(status.error, /not installed or not registered/);
  const result = await client.sendSnapshot(minimalSnapshot);
  assert.equal(result.sent, false);
  assert.equal(result.reason, "unavailable");
});

test("helper client sends snapshots to the fixed native host", async () => {
  const fake = fakeChrome({ permission: true });
  const client = HelperClient.create({ chrome: fake.chrome });
  assert.deepEqual(await client.status(), { permission: true, installed: true, version: "1.0.0" });
  const result = await client.sendSnapshot(minimalSnapshot);
  assert.equal(result.sent, true);
  assert.equal(fake.sent[1].host, "com.smc.catracker");
  assert.equal(fake.sent[1].message.type, "snapshot");
  await assert.rejects(() => client.sendSnapshot({ provider: "egnyte", projectId: "x", items: [], itemTypes: [] }));
});

test("the native messaging permission is optional and the helper host name is shared", () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(root, "manifest.json"), "utf8"));
  assert.equal(manifest.permissions.includes("nativeMessaging"), false);
  assert.deepEqual(manifest.optional_permissions, ["nativeMessaging"]);
  assert.equal(Snapshot.HOST_NAME, HelperClient.HOST_NAME);
  for (const entry of manifest.content_scripts) {
    const scripts = entry.js;
    assert.ok(scripts.indexOf("integrations/windows-helper/snapshot.js") > scripts.indexOf("activity-core.js"));
    assert.ok(scripts.indexOf("integrations/windows-helper/snapshot.js") > scripts.indexOf("integrations/taskhub/mapper.js"));
  }
  const hostManifest = JSON.parse(fs.readFileSync(
    path.join(root, "windows-helper", "native-host", "com.smc.catracker.json"),
    "utf8"
  ));
  assert.equal(hostManifest.name, HelperClient.HOST_NAME);
  assert.deepEqual(hostManifest.allowed_origins, ["chrome-extension://kcngamkbiboiljpfbjpmjoekkfpbidbk/"]);
});
