"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const Snapshot = require("../../src/SmcCaTracker.App/Scripts/snapshot.js");

const scripts = path.resolve(__dirname, "../../src/SmcCaTracker.App/Scripts");

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
  assert.equal(byId.get("rfi").sourceUrl, "https://project.helixteam.com/Projects/p1/RFI");
  assert.equal(byId.get("held").attachmentCount, 2);
  assert.equal(Snapshot.buildHelixSnapshot(scan([item({ contract: "TFO - Level 3" })])).items[0].contract, "TFO - Level 3");
});

test("Helix snapshot due dates use the earliest due or review date and never fall back to modified", () => {
  const snapshot = Snapshot.buildHelixSnapshot(scan([
    item({ id: "review", dates: { due: "2026-10-09T00:00:00Z", requestedReview: "2026-10-02T00:00:00Z", contractualReview: "2026-10-05T00:00:00Z", modified: "2026-09-01T00:00:00Z" } }),
    item({ id: "due", dates: { due: "2026-10-01T00:00:00Z", requestedReview: "not a date" } }),
    item({ id: "none", dates: { modified: "2026-09-01T00:00:00Z" } })
  ]));
  const byId = new Map(snapshot.items.map((entry) => [entry.itemId, entry]));
  assert.equal(byId.get("review").dueAt, "2026-10-02T00:00:00.000Z");
  assert.equal(byId.get("due").dueAt, "2026-10-01T00:00:00.000Z");
  assert.equal(byId.get("none").dueAt, null);
  assert.equal(byId.get("review").sourceUrl, "https://project.helixteam.com/Projects/p1/Submittals");
});

test("the built-in browser can load every scan script into a plain page", () => {
  const page = { window: {} };
  page.window.window = page.window;
  const context = vm.createContext(page.window);
  for (const file of ["bridge-core.js", "activity-core.js", "procore-core.js", "snapshot.js"]) {
    vm.runInContext(fs.readFileSync(path.join(scripts, file), "utf8"), context, { filename: file });
  }
  assert.equal(typeof context.HelixBridgeCore.scan, "function");
  assert.equal(typeof context.CaTrackerSnapshot.buildHelixSnapshot, "function");
  assert.equal(typeof context.CaTrackerSnapshot.buildHelixCatalog, "function");
  assert.equal(typeof context.CaTrackerProcoreCore.visibleQueueFromRows, "function");
  assert.equal(typeof context.CaTrackerSnapshot.buildProcoreProjectSnapshot, "function");
  const raw = scan([item({ id: "held", workflow: { required: [{ assigned: us }] } })]);
  const fromPage = context.CaTrackerSnapshot.buildHelixSnapshot(raw);
  assert.deepEqual(JSON.parse(JSON.stringify(fromPage)), JSON.parse(JSON.stringify(Snapshot.buildHelixSnapshot(raw))));
  const catalog = context.CaTrackerSnapshot.buildHelixCatalog({ projects: [{ id: "p1", name: "Aligned CMH04" }] });
  assert.equal(catalog.projects.length, 1);
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

test("current holders carry the date the item reached them when Helix records it", () => {
  const snapshot = Snapshot.buildHelixSnapshot(scan([
    item({ id: "second", workflow: { required: [{ assigned: architect, reviewedAt: "2026-09-02T15:00:00Z" }, { assigned: us }] } }),
    item({ id: "first", workflow: { required: [{ assigned: coworker }] } })
  ]));
  const byId = new Map(snapshot.items.map((entry) => [entry.itemId, entry]));
  const second = byId.get("second").holders.find((holder) => holder.state === "current");
  assert.equal(second.name, "Pat Reviewer");
  assert.equal(second.since, "2026-09-02T15:00:00.000Z");
  assert.equal(byId.get("first").holders[0].since, null, "no recorded date means no guess");
});

test("Procore rows keep the Ball In Court people and a due date from any due column", () => {
  const ProcoreCore = require("../../src/SmcCaTracker.App/Scripts/procore-core.js");
  const page = "https://app.procore.com/webclients/host/companies/12/projects/34/tools/rfis";
  const queue = ProcoreCore.visibleQueueFromRows(page, [{
    number: { text: "7", href: "" },
    subject: { text: "Slab edge", href: "https://app.procore.com/webclients/host/companies/12/projects/34/tools/rfis/901" },
    ball_in_court: { text: "Alex Architect, Sam Coworker", href: "" },
    due_date_formatted: { text: "2026-10-05", href: "" },
    status: { text: "Open", href: "" }
  }], "2026-09-29T15:00:00Z");
  // The link is on the subject: the number comes from its own column, not from the title.
  assert.equal(queue.items[0].number, "7");
  assert.equal(queue.items[0].title, "Slab edge");
  const snapshot = Snapshot.buildProcoreProjectSnapshot(queue, { projectId: "34", accountId: "12", itemTypes: ["rfi"] });
  const entry = snapshot.items[0];
  assert.deepEqual(entry.holders.map((holder) => [holder.name, holder.state]), [["Alex Architect", "current"], ["Sam Coworker", "current"]]);
  assert.equal(entry.responsible, "Alex Architect, Sam Coworker");
  assert.equal(entry.dueAt, "2026-10-05T00:00:00.000Z");
});

test("a court start worked out from Helix history wins over the workflow estimate", () => {
  const snapshot = Snapshot.buildHelixSnapshot(scan([
    item({ id: "h", courtSince: "2026-09-12T08:00:00Z", workflow: { required: [{ assigned: architect, reviewedAt: "2026-09-02T15:00:00Z" }, { assigned: us }] } })
  ]));
  const current = snapshot.items[0].holders.find((holder) => holder.state === "current");
  assert.equal(current.since, "2026-09-12T08:00:00.000Z");
});
