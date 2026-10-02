"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const Sync = require("../../src/SmcCaTracker.App/Scripts/sync-core.js");

const user = {
  _id: "directory-home",
  fullName: "Current User",
  refContact: { _id: "contact-current" },
  refCompany: { _id: "company-current", name: "Example Co" }
};

// A Helix page (any page: the background sync never opens a project) whose data service answers
// the filters the sync uses: project, changed-since and history ids.
function helixPage(datasets) {
  const calls = [];
  return {
    location: { href: "https://project.helixteam.com/" },
    document: { querySelector: () => null },
    UserInfo: { directoryInfo: user },
    Master: {
      async readFromCollection(collection, query) {
        calls.push({ collection, filter: JSON.parse(JSON.stringify(query.filter || {})) });
        let values = datasets[collection] || [];
        const filter = query.filter || {};
        if (filter["refProject._id"]) {
          values = values.filter((record) => record.refProject && record.refProject._id === filter["refProject._id"]);
        }
        if (filter["_kmd.lmt"] && filter["_kmd.lmt"].$gte) {
          values = values.filter((record) => record._kmd && record._kmd.lmt >= filter["_kmd.lmt"].$gte);
        }
        if (filter.originalId && filter.originalId.$in) {
          values = values.filter((record) => filter.originalId.$in.includes(record.originalId));
        }
        return values.slice(query.skip, query.skip + query.limit);
      }
    },
    calls
  };
}

function rfi(id, overrides) {
  return Object.assign({
    _id: id,
    refProject: { _id: "p1" },
    number: id.toUpperCase(),
    title: `Title ${id}`,
    status: { name: "Review", rank: 800 },
    statusMessage: "Current User",
    _kmd: { lmt: "2026-09-01T00:00:00.000Z" }
  }, overrides || {});
}

test("first sync reads everything; later syncs ask only for changes and merge them", async () => {
  Sync.forget();
  const datasets = {
    HelixProjectRFIs: [rfi("a"), rfi("b", { _kmd: { lmt: "2026-09-05T00:00:00.000Z" } }), rfi("other", { refProject: { _id: "p2" } })],
    HelixProjectSubmittalPackages: [],
    HelixProjectHistories: []
  };
  const page = helixPage(datasets);

  const first = await Sync.syncProject(page, { projectId: "p1", projectName: "Project One", now: Date.parse("2026-09-10T00:00:00Z") });
  assert.equal(first.mode, "full");
  assert.equal(first.itemCount, 2);
  assert.deepEqual(first.snapshot.items.map((item) => item.itemId).sort(), ["a", "b"]);
  assert.equal(first.snapshot.projectName, "Project One");
  assert.ok(first.snapshot.items.every((item) => item.holders.some((holder) => holder.name === "Current User")));
  assert.ok(!page.calls.some((call) => call.filter["_kmd.lmt"]), "the first read is a full read");

  // "a" is sent on to someone else; nothing else changes.
  datasets.HelixProjectRFIs[0] = rfi("a", { statusMessage: "Other Person", _kmd: { lmt: "2026-09-09T00:00:00.000Z" } });
  page.calls.length = 0;
  const second = await Sync.syncProject(page, { projectId: "p1", projectName: "Project One", now: Date.parse("2026-09-10T00:10:00Z") });
  assert.equal(second.mode, "changes");
  assert.equal(second.changedCount, 1);
  assert.equal(second.itemCount, 2, "unchanged items are kept from the first read");
  const asked = page.calls.find((call) => call.collection === "HelixProjectRFIs");
  assert.ok(asked.filter["_kmd.lmt"].$gte < "2026-09-05T00:00:00.000Z", "asks with some overlap before the newest change seen");
  const a = second.snapshot.items.find((item) => item.itemId === "a");
  assert.equal(a.holders.some((holder) => holder.name === "Current User" && holder.state === "current"), false);
});

test("a full read runs again after the full-read interval", async () => {
  Sync.forget();
  const datasets = {
    HelixProjectRFIs: [rfi("a"), rfi("b")],
    HelixProjectSubmittalPackages: [],
    HelixProjectHistories: []
  };
  const page = helixPage(datasets);
  await Sync.syncProject(page, { projectId: "p1", now: 0 + Date.parse("2026-09-10T00:00:00Z") });
  datasets.HelixProjectRFIs = [rfi("a")];

  const soon = await Sync.syncProject(page, { projectId: "p1", now: Date.parse("2026-09-10T01:00:00Z"), fullAfterMs: 6 * 3600 * 1000 });
  assert.equal(soon.mode, "changes");
  assert.equal(soon.itemCount, 1, "the id check drops the deleted item");

  const later = await Sync.syncProject(page, { projectId: "p1", now: Date.parse("2026-09-10T07:00:00Z"), fullAfterMs: 6 * 3600 * 1000 });
  assert.equal(later.mode, "full");
  assert.equal(later.itemCount, 1);
  assert.equal(later.changedCount, 0);
});

test("falls back to a full read when Helix rejects the changed-since filter", async () => {
  Sync.forget();
  const datasets = { HelixProjectRFIs: [rfi("a")], HelixProjectSubmittalPackages: [], HelixProjectHistories: [] };
  const page = helixPage(datasets);
  await Sync.syncProject(page, { projectId: "p1", now: Date.parse("2026-09-10T00:00:00Z") });
  const original = page.Master.readFromCollection;
  page.Master.readFromCollection = async (collection, query) => {
    if (query.filter && query.filter["_kmd.lmt"]) throw new Error("Unsupported query");
    return original(collection, query);
  };
  const result = await Sync.syncProject(page, { projectId: "p1", now: Date.parse("2026-09-10T00:30:00Z") });
  assert.equal(result.mode, "full");
  assert.equal(result.itemCount, 1);
});

test("readiness reports sign-in forms, loading pages and ready pages", () => {
  const page = helixPage({});
  assert.equal(Sync.readiness(page).state, "ready");
  assert.equal(Sync.readiness(Object.assign({}, page, { UserInfo: {} })).state, "loading");
  assert.equal(Sync.readiness(Object.assign({}, page, { document: { querySelector: () => ({}) } })).state, "signedOut");
  // A sign-in page that asks for the email first (no password box yet) counts as signed out.
  const emailFirst = { document: { querySelector: (selector) => (/email/.test(selector) ? {} : null), querySelectorAll: () => [] } };
  assert.equal(Sync.readiness(emailFirst).state, "signedOut");
  const signInButton = { document: { querySelector: () => null, querySelectorAll: () => [{ innerText: "Sign in with Microsoft" }] } };
  assert.equal(Sync.readiness(signInButton).state, "signedOut");
  const loading = Sync.readiness({ location: { pathname: "/Home" }, document: { title: "Helix", querySelector: () => null, querySelectorAll: () => [] } });
  assert.equal(loading.state, "loading");
  assert.equal(loading.reason, "no-user-info");
  assert.equal(loading.path, "/Home");
});

test("a changes-only read drops items deleted or moved to another project", async () => {
  Sync.forget();
  const datasets = { HelixProjectRFIs: [rfi("a"), rfi("b")], HelixProjectSubmittalPackages: [], HelixProjectHistories: [] };
  const page = helixPage(datasets);
  await Sync.syncProject(page, { projectId: "p1", now: Date.parse("2026-09-10T00:00:00Z") });
  // "b" moves to another project; "a" is deleted.
  datasets.HelixProjectRFIs = [rfi("b", { refProject: { _id: "p2" }, _kmd: { lmt: "2026-09-10T00:05:00.000Z" } })];
  const result = await Sync.syncProject(page, { projectId: "p1", now: Date.parse("2026-09-10T00:10:00Z") });
  assert.equal(result.mode, "changes");
  assert.equal(result.itemCount, 0);
  assert.equal(result.changedCount, 2);
});

test("items whose court history couldn't be read are read again on the next sync", async () => {
  Sync.forget();
  const datasets = {
    HelixProjectRFIs: [rfi("a")],
    HelixProjectSubmittalPackages: [],
    HelixProjectHistories: [
      { originalId: "a", status: { name: "Review", rank: 800 }, statusMessage: "Current User", _kmd: { lmt: "2026-08-30T00:00:00.000Z" } }
    ]
  };
  const page = helixPage(datasets);
  const original = page.Master.readFromCollection;
  let historyDown = true;
  page.Master.readFromCollection = async (collection, query) => {
    if (collection === "HelixProjectHistories" && historyDown) throw new Error("timeout");
    return original(collection, query);
  };
  const first = await Sync.syncProject(page, { projectId: "p1", now: Date.parse("2026-09-10T00:00:00Z") });
  assert.equal(first.snapshot.items[0].holders[0].since, null);

  historyDown = false;
  const second = await Sync.syncProject(page, { projectId: "p1", now: Date.parse("2026-09-10T00:10:00Z") });
  assert.equal(second.mode, "changes");
  assert.equal(second.snapshot.items[0].holders[0].since, "2026-08-30T00:00:00.000Z");
});

test("a sample export hides names, companies and titles but keeps whose court each item is in", async () => {
  Sync.forget();
  const datasets = {
    HelixProjectRFIs: [rfi("a", {
      title: "Secret title",
      statusMessage: "Waiting on Current User at Example Co",
      workflow: { workflowRequired: [{ refDirectoryAssigned: { _id: "d2", fullName: "Other User", refContact: { _id: "c2" } } }] }
    })],
    HelixProjectSubmittalPackages: [],
    HelixProjectHistories: []
  };
  const page = helixPage(datasets);
  await Sync.syncProject(page, { projectId: "p1", now: Date.parse("2026-09-10T00:00:00Z") });
  const sample = Sync.exportSample(page, "p1");
  const text = JSON.stringify(sample);
  assert.ok(!text.includes("Current User"));
  assert.ok(!text.includes("Other User"));
  assert.ok(!text.includes("Secret title"));
  assert.ok(!text.includes("Example Co"));
  assert.equal(sample.items[0].responsible, `Waiting on ${sample.user.fullName} at Company A`);
  const holders = sample.expected[0].holders.map((holder) => [holder.name, holder.state]);
  assert.deepEqual(holders[0], [sample.user.fullName, "current"]);
  assert.ok(holders.every(([name]) => /^Person \d+$/.test(name)));
});

test("items read again go in batches of 20, and a batch that fails is reported", async () => {
  Sync.forget();
  const datasets = { HelixProjectRFIs: [rfi("a")], HelixProjectSubmittalPackages: [], HelixProjectHistories: [] };
  const page = helixPage(datasets);
  await Sync.syncProject(page, { projectId: "p1", projectName: "Project One", now: Date.parse("2026-09-10T00:00:00Z") });

  // 45 items whose court history couldn't be read last time; Helix refuses the batch holding "r44".
  const ids = Array.from({ length: 45 }, (_, index) => `r${index}`);
  Sync._test.cache.get("p1").retryCourt = new Set(ids.map((id) => `rfi:${id}`));
  const read = page.Master.readFromCollection;
  const asked = [];
  page.Master.readFromCollection = async (collection, query) => {
    const wanted = query.filter && query.filter._id && query.filter._id.$in;
    if (wanted && collection === "HelixProjectRFIs") asked.push(wanted.length);
    if (wanted && wanted.includes("r44")) throw new Error("Helix refused the read");
    return read(collection, query);
  };
  const progress = [];
  const result = await Sync.syncProject(page, {
    projectId: "p1",
    projectName: "Project One",
    now: Date.parse("2026-09-10T00:10:00Z"),
    onProgress: (message) => progress.push(message)
  });

  assert.equal(result.mode, "changes");
  assert.deepEqual(asked, [20, 20, 5]);
  assert.equal(result.warnings.length, 1);
  assert.match(result.warnings[0], /Helix refused the read/);
  assert.equal(progress.filter((message) => message.phase === "warning").length, 1);
});
