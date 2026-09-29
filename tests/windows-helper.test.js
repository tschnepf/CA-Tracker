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
  const capture = manifest.content_scripts.find((entry) => entry.js.includes("refresh-capture.js"));
  assert.equal(capture.run_at, "document_start");
  assert.deepEqual(capture.js, ["refresh-capture.js"]);
  for (const entry of manifest.content_scripts.filter((candidate) => candidate !== capture)) {
    assert.ok(capture.matches.every((pattern) => pattern) && entry.matches.every((pattern) => capture.matches.includes(pattern)));
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

test("refresh ids are taken from the URL only when they match the app's format", () => {
  function fakeWindow(hash) {
    const calls = [];
    return {
      calls,
      location: { hash, pathname: "/Projects/p1/Overview", search: "" },
      history: { state: null, replaceState(state, title, url) { calls.push(url); } }
    };
  }
  const good = fakeWindow(`#ca-tracker-refresh=${"a1".repeat(16)}`);
  assert.equal(Snapshot.takeRefreshId(good), "a1".repeat(16));
  assert.deepEqual(good.calls, ["/Projects/p1/Overview"]);
  const bad = fakeWindow("#ca-tracker-refresh=../../etc");
  assert.equal(Snapshot.takeRefreshId(bad), null);
  assert.deepEqual(bad.calls, []);
  assert.equal(Snapshot.takeRefreshId(fakeWindow("")), null);
});

test("Procore refresh snapshots cover the requested project and type even when the grid is empty", () => {
  const request = { projectId: "900", accountId: "5", projectName: "Tower", itemTypes: ["submittal"] };
  const empty = Snapshot.buildProcoreProjectSnapshot({ items: [] }, request);
  assert.equal(empty.projectId, "900");
  assert.equal(empty.projectName, "Tower");
  assert.deepEqual(empty.itemTypes, ["submittal"]);
  assert.deepEqual(empty.items, []);
  assert.equal(empty.complete, false);

  const withRows = Snapshot.buildProcoreProjectSnapshot({
    items: [
      { item_id: "1", item_type: "submittal", project_id: "900", number: "S-1", title: "Doors", status: "Open" },
      { item_id: "2", item_type: "rfi", project_id: "900", number: "4", title: "Slab", status: "Open" },
      { item_id: "3", item_type: "submittal", project_id: "901", number: "S-9", title: "Other", status: "Open" }
    ]
  }, request);
  assert.deepEqual(withRows.items.map((item) => item.itemId), ["1"]);
});

test("project catalogs list Helix assignments and Procore discovery results", () => {
  const helix = Snapshot.buildHelixCatalog({ projects: [{ id: "p1", name: "Aligned CMH04" }, { id: "", name: "x" }] });
  assert.deepEqual(helix, {
    provider: "helix",
    projects: [{ projectId: "p1", accountId: null, accountName: null, name: "Aligned CMH04", host: "project.helixteam.com" }]
  });
  const procore = Snapshot.buildProcoreCatalog(
    { items: [{ project_id: "34", account_id: "12", project_name: "Garage" }] },
    { companies: [{ id: "12", name: "SMC", projects: [{ id: "33", name: "Tower" }] }] },
    "us02.procore.com"
  );
  assert.deepEqual(procore.projects.map((project) => [project.accountId, project.projectId, project.name, project.host]), [
    ["12", "33", "Tower", "us02.procore.com"],
    ["12", "34", "Garage", "us02.procore.com"]
  ]);
  assert.equal(Snapshot.buildProcoreCatalog({ items: [] }, null, "evil.example.com").projects.length, 0);
});

test("helper client claims refreshes and forwards catalog and refresh details", async () => {
  const fake = fakeChrome({ permission: true });
  fake.chrome.runtime.sendNativeMessage = function sendNativeMessage(host, message, callback) {
    fake.sent.push({ host, message });
    if (message.type === "claimRefresh") callback({ ok: true, request: { id: message.id, projectId: "p1", step: 0 } });
    else callback({ ok: true, version: "1.1.0", refresh: { done: true } });
  };
  const client = HelperClient.create({ chrome: fake.chrome });
  const id = "b2".repeat(16);
  assert.deepEqual(await client.claimRefresh(id, "helix"), { id, projectId: "p1", step: 0 });
  await assert.rejects(() => client.claimRefresh("not-an-id", "helix"));
  const result = await client.sendSnapshot(minimalSnapshot, {
    catalog: { provider: "helix", projects: [] },
    refresh: { id, step: "0" }
  });
  assert.deepEqual(result.refresh, { done: true });
  const sentSnapshot = fake.sent.find((entry) => entry.message.type === "snapshot").message;
  assert.deepEqual(sentSnapshot.refresh, { id, step: 0 });
  assert.deepEqual(sentSnapshot.catalog, { provider: "helix", projects: [] });

  await client.sendSnapshot(minimalSnapshot, { catalog: { provider: "procore", projects: [] }, refresh: { id: "bad" } });
  const last = fake.sent[fake.sent.length - 1].message;
  assert.equal(last.catalog, undefined);
  assert.equal(last.refresh, undefined);
});

test("helper client refuses to claim refreshes without the optional permission", async () => {
  const client = HelperClient.create({ chrome: fakeChrome({ permission: false }).chrome });
  await assert.rejects(() => client.claimRefresh("c3".repeat(16), "helix"), /Enable the Windows helper/);
  assert.equal(await client.refreshFailed("c3".repeat(16), "x"), false);
});

test("the document_start capture hands refresh ids to the background worker, not the page", () => {
  const source = fs.readFileSync(path.join(root, "refresh-capture.js"), "utf8");
  const sent = [];
  const replaced = [];
  const sandbox = {
    window: {
      top: null,
      location: { hash: `#ca-tracker-refresh=${"e5".repeat(16)}`, pathname: "/Projects/p1/Overview", search: "" },
      history: { state: null, replaceState(state, title, url) { replaced.push(url); } }
    },
    chrome: { runtime: { lastError: null, sendMessage(message, callback) { sent.push(message); callback(); } } }
  };
  sandbox.window.top = sandbox.window;
  require("node:vm").runInNewContext(source, sandbox);
  assert.equal(JSON.stringify(sent), JSON.stringify([{ type: "WINDOWS_HELPER_REFRESH_SEEN", id: "e5".repeat(16) }]));
  assert.deepEqual(replaced, ["/Projects/p1/Overview"]);
});
