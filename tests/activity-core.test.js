"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const Core = require("../activity-core.js");

const alice = { id: "directory-alice", fullName: "Alice Example" };
const bob = { id: "directory-bob", fullName: "Bob Example" };

test("company people scope uses the current person's Helix company identity", () => {
  const people = [
    { key: "alice", companyId: "company-a", company: "Architects Inc" },
    { key: "coworker", companyId: "company-a", company: "Architects Inc" },
    { key: "other", companyId: "company-b", company: "Architects Inc" },
    { key: "unknown", company: "Architects Inc" }
  ];
  assert.deepEqual(Core.companyPersonKeys(people, "alice"), ["alice", "coworker"]);
  assert.deepEqual(Core.companyPersonKeys(people, "missing"), []);
});

function baseRfi(overrides) {
  return Object.assign({
    id: "rfi-1",
    projectId: "project-1",
    number: "000101",
    title: "Roof curb detail",
    status: "Review",
    contract: "Roofing",
    dates: { created: "2026-01-01T12:00:00.000Z", modified: "2026-02-01T12:00:00.000Z" },
    creator: bob,
    responseEditor: bob,
    acceptedBy: null,
    rejectedBy: null,
    lastEditorName: "Bob Example",
    workflow: { required: [], optional: [], notify: [] }
  }, overrides || {});
}

test("buildActivityIndex attributes every supported activity type to the current user", () => {
  const scan = {
    context: { projectId: "project-1", user: alice },
    scannedAt: "2026-03-01T00:00:00.000Z",
    warnings: [],
    rfis: [baseRfi({
      creator: alice,
      responseEditor: alice,
      acceptedBy: alice,
      rejectedBy: alice,
      lastEditorName: alice.fullName,
      workflow: {
        required: [{ assigned: alice, reviewed: true }],
        optional: [],
        notify: [alice]
      }
    })],
    comments: [{ id: "comment-1", rfiId: "rfi-1", author: alice, text: "Reviewed", createdAt: "2026-02-02T00:00:00.000Z" }],
    attachments: [{ id: "file-1", rfiId: "rfi-1", uploader: alice, filename: "detail.pdf", createdAt: "2026-02-03T00:00:00.000Z" }],
    histories: [{
      id: "history-1",
      rfiId: "rfi-1",
      editorName: alice.fullName,
      responseEditor: alice,
      acceptedBy: alice,
      rejectedBy: alice,
      modifiedAt: "2026-02-04T00:00:00.000Z"
    }]
  };

  const index = Core.buildActivityIndex(scan);
  assert.equal(index.results.length, 1);
  assert.deepEqual(index.results[0].reasons.map((reason) => reason.code), [
    "created", "responded", "commented", "uploaded", "edited", "assigned", "reviewed", "accepted", "rejected", "notified"
  ]);
});

test("the current identity is dynamic and other users' activity is excluded", () => {
  const scanForAlice = {
    context: { projectId: "project-1", user: alice },
    rfis: [baseRfi()],
    comments: [{ id: "comment-bob", rfiId: "rfi-1", author: bob, text: "Bob comment" }],
    attachments: [{ id: "file-bob", rfiId: "rfi-1", uploader: bob, filename: "bob.pdf" }],
    histories: [{ id: "history-bob", rfiId: "rfi-1", editorName: bob.fullName }]
  };
  assert.equal(Core.buildActivityIndex(scanForAlice).results.length, 0);

  const scanForBob = Object.assign({}, scanForAlice, { context: { projectId: "project-1", user: bob } });
  const bobIndex = Core.buildActivityIndex(scanForBob);
  assert.equal(bobIndex.results.length, 1);
  assert.ok(bobIndex.results[0].reasons.some((reason) => reason.code === "commented"));
});

test("historical response ownership survives a different current response editor", () => {
  const index = Core.buildActivityIndex({
    context: { projectId: "project-1", user: alice },
    rfis: [baseRfi({ responseEditor: bob })],
    comments: [],
    attachments: [],
    histories: [{
      id: "history-response",
      rfiId: "rfi-1",
      editorName: bob.fullName,
      responseEditor: alice,
      modifiedAt: "2026-01-15T00:00:00.000Z"
    }]
  });
  assert.equal(index.results.length, 1);
  assert.ok(index.results[0].reasons.some((reason) => reason.code === "responded"));
});

test("Submittal activity uses generic item IDs and includes register numbers in local search", () => {
  const index = Core.buildActivityIndex({
    context: {
      projectId: "project-1",
      itemType: "submittal",
      itemLabel: "Submittal",
      itemLabelPlural: "Submittals",
      user: alice
    },
    items: [{
      id: "submittal-1",
      itemType: "submittal",
      projectId: "project-1",
      number: "000501",
      title: "Air handler shop drawings",
      status: "Review",
      contract: "Mechanical",
      searchTerms: ["000880"],
      dates: { created: "2026-01-01T00:00:00.000Z", modified: "2026-02-01T00:00:00.000Z" },
      creator: bob,
      lastEditorName: bob.fullName,
      workflow: { required: [{ assigned: alice, reviewed: true }], optional: [], notify: [] }
    }],
    comments: [{ id: "comment-submittal", itemId: "submittal-1", author: alice, text: "Reviewed" }],
    attachments: [{ id: "file-submittal", itemId: "submittal-1", uploader: alice, filename: "review.pdf" }],
    histories: []
  });

  assert.equal(index.results.length, 1);
  assert.deepEqual(index.results[0].reasons.map((reason) => reason.code), ["commented", "uploaded", "assigned", "reviewed"]);
  assert.deepEqual(Core.filterResults(index.results, { search: "000880" }).map((item) => item.id), ["submittal-1"]);
});

test("open queue mirrors required-review order for the exact person rather than their company", () => {
  const queueUser = { id: "alice", contactId: "contact-alice", fullName: "Alice Example", companyId: "company-a", company: "Architects Inc" };
  const colleague = { id: "colleague", contactId: "contact-colleague", fullName: "Company Colleague", companyId: "company-a", company: "Architects Inc" };
  const otherCompany = { id: "other", fullName: "Other Reviewer", companyId: "company-b", company: "Engineers LLC" };
  const base = {
    id: "submittal-queue",
    number: "000600",
    title: "Queue test",
    status: "Review",
    statusRank: 800,
    dates: { requestedReview: "2026-08-15T00:00:00.000Z" },
    workflow: { required: [], optional: [], notify: [] }
  };

  assert.equal(Core.classifyOpenQueue(Object.assign({}, base, {
    workflow: { required: [{ assigned: colleague, reviewed: false }], optional: [], notify: [] }
  }), queueUser), null);

  const current = Core.classifyOpenQueue(Object.assign({}, base, {
    workflow: { required: [{ assigned: queueUser, reviewed: false }], optional: [], notify: [] }
  }), queueUser);
  assert.equal(current.code, "current");
  assert.equal(current.label, "In court");

  const upcoming = Core.classifyOpenQueue(Object.assign({}, base, {
    workflow: {
      required: [
        { assigned: otherCompany, reviewed: false },
        { assigned: queueUser, reviewed: false }
      ],
      optional: [],
      notify: []
    }
  }), queueUser);
  assert.equal(upcoming.code, "upcoming");
  assert.match(upcoming.detail, /1 required review ahead/);

  const beforeReview = Core.classifyOpenQueue(Object.assign({}, base, {
    status: "Pending",
    statusRank: 600,
    workflow: { required: [{ assigned: queueUser, reviewed: false }], optional: [], notify: [] }
  }), queueUser);
  assert.equal(beforeReview.code, "upcoming");

  assert.equal(Core.classifyOpenQueue(Object.assign({}, base, {
    status: "Complete",
    statusRank: 1300,
    workflow: { required: [{ assigned: colleague, reviewed: false }], optional: [], notify: [] }
  }), queueUser), null);
});

test("open queue defaults to the logged-in person while allowing coworkers to be selected", () => {
  const queueUser = { id: "alice", contactId: "contact-alice", fullName: "Alice Example", companyId: "company-a", company: "Architects Inc" };
  const index = Core.buildActivityIndex({
    context: {
      projectId: "project-1",
      itemType: "submittal",
      itemLabel: "Submittal",
      itemLabelPlural: "Submittals",
      user: queueUser
    },
    items: [{
      id: "submittal-company",
      number: "000601",
      title: "Company assignment",
      status: "Review",
      statusRank: 800,
      dates: { due: "2026-08-10T00:00:00.000Z" },
      creator: bob,
      lastEditorName: bob.fullName,
      workflow: {
        required: [{
          assigned: { id: "colleague", contactId: "contact-colleague", fullName: "Colleague", companyId: "company-a", company: "Architects Inc" },
          reviewed: false,
          reviewedAt: null
        }],
        optional: [],
        notify: []
      }
    }],
    comments: [],
    attachments: [],
    histories: []
  });

  assert.equal(index.results.length, 0);
  assert.equal(index.openQueue.length, 1);
  assert.equal(index.openQueue[0].queueState.code, "current");
  assert.deepEqual(Core.filterOpenQueue(index.openQueue, {
    search: "company assignment",
    people: [index.currentPersonKey]
  }), []);
  const colleague = index.people.find((person) => person.fullName === "Colleague");
  assert.ok(colleague);
  assert.deepEqual(Core.filterOpenQueue(index.openQueue, { people: [colleague.key] }).map((item) => item.id), ["submittal-company"]);
});

test("filters combine search, status, activity reason, and sorting", () => {
  const items = [
    { number: "00010", itemType: "rfi", title: "Zulu", status: "Review", searchText: "00010 zulu roofing commented", latestActivityAt: "2026-02-01", reasons: [{ code: "commented" }] },
    { number: "0002", itemType: "submittal", title: "Alpha", status: "Complete", searchText: "0002 alpha concrete uploaded file", latestActivityAt: "2026-03-01", reasons: [{ code: "uploaded" }] }
  ];
  assert.deepEqual(Core.filterResults(items, { search: "roof", status: "Review", reasons: ["commented"] }).map((item) => item.number), ["00010"]);
  assert.deepEqual(Core.filterResults(items, { sort: "number" }).map((item) => item.number), ["0002", "00010"]);
  assert.deepEqual(Core.filterResults(items, { reasons: ["uploaded"] }).map((item) => item.number), ["0002"]);
  assert.deepEqual(Core.filterResults(items, { types: ["rfi"] }).map((item) => item.number), ["00010"]);
});

test("open queue filters combine any selected projects and item types", () => {
  const items = [
    { id: "a-rfi", projectId: "a", projectName: "Alpha", itemType: "rfi", number: "2", status: "Review", searchText: "alpha rfi", queueState: { code: "current" } },
    { id: "a-sub", projectId: "a", projectName: "Alpha", itemType: "submittal", number: "1", status: "Review", searchText: "alpha submittal", queueState: { code: "upcoming" } },
    { id: "b-rfi", projectId: "b", projectName: "Beta", itemType: "rfi", number: "3", status: "Pending", searchText: "beta rfi", queueState: { code: "upcoming" } }
  ];

  assert.deepEqual(Core.filterOpenQueue(items, {
    projects: ["a"],
    types: ["submittal"]
  }).map((item) => item.id), ["a-sub"]);
  assert.deepEqual(Core.filterOpenQueue(items, {
    projects: ["a", "b"],
    types: ["rfi"]
  }).map((item) => item.id), ["a-rfi", "b-rfi"]);
  assert.deepEqual(Core.filterOpenQueue(items, { projects: [], types: ["rfi"] }), []);
  assert.deepEqual(Core.filterOpenQueue(items, { states: ["current"] }).map((item) => item.id), ["a-rfi"]);
  assert.deepEqual(Core.filterOpenQueue(items, { states: ["upcoming"] }).map((item) => item.id), ["a-sub", "b-rfi"]);
  assert.deepEqual(Core.filterOpenQueue(items, { states: ["current", "upcoming"] }).map((item) => item.id), ["a-rfi", "a-sub", "b-rfi"]);
  assert.deepEqual(Core.filterOpenQueue(items, { sort: "project" }).map((item) => item.id), ["a-rfi", "a-sub", "b-rfi"]);
  assert.deepEqual(Core.filterOpenQueue(items, { sort: "type" }).map((item) => item.id), ["a-rfi", "b-rfi", "a-sub"]);
});

test("queue responsibility labels list each selected person only once", () => {
  const item = {
    responsible: "Tim Schnepf",
    queueState: { code: "current", label: "In court", detail: "Tim Schnepf" },
    queuePeople: [{
      fullName: "Tim Schnepf",
      state: { code: "current", detail: "Tim Schnepf" }
    }]
  };
  assert.deepEqual(Core.queueReasonLabels(item), ["Tim Schnepf — in court"]);

  assert.deepEqual(Core.queueReasonLabels({
    responsible: "Ready for Tim Schnepf",
    queueState: { code: "current", label: "In court", detail: "Required review is ready" },
    queuePeople: item.queuePeople
  }), ["Tim Schnepf — in court", "Required review is ready"]);
});

test("queue due filters use due, requested review, and contractual review calendar dates", () => {
  const now = new Date(2026, 6, 21, 12, 0, 0);
  const date = (day) => new Date(2026, 6, day, 9, 0, 0).toISOString();
  const items = [
    { id: "overdue-due", number: "1", queueState: { code: "current" }, dates: { due: date(20) } },
    { id: "today-requested", number: "2", queueState: { code: "current" }, dates: { requestedReview: date(21) } },
    { id: "today-contractual", number: "3", queueState: { code: "current" }, dates: { contractualReview: date(21) } },
    { id: "future-due", number: "4", queueState: { code: "current" }, dates: { due: date(22) } },
    { id: "no-due-date", number: "5", queueState: { code: "current" }, dates: {} },
    { id: "mixed-overdue", number: "6", queueState: { code: "current" }, dates: { due: date(20), requestedReview: date(22) } },
    { id: "future-eight", number: "7", queueState: { code: "current" }, dates: { due: date(29) } }
  ];
  const ids = (mode) => Core.filterOpenQueue(items, { due: mode, now }).map((item) => item.id).sort();

  assert.deepEqual(ids("today"), ["today-contractual", "today-requested"]);
  assert.deepEqual(ids("overdue"), ["mixed-overdue", "overdue-due"]);
  assert.deepEqual(ids("today-or-overdue"), ["mixed-overdue", "overdue-due", "today-contractual", "today-requested"]);
  assert.equal(Core.filterOpenQueue(items, { due: "all", now }).length, items.length);

  const combinedIds = (modes, days) => Core.filterOpenQueue(items, {
    dueModes: modes,
    dueDays: days,
    now
  }).map((item) => item.id).sort();
  assert.deepEqual(combinedIds(["next"], 7), ["future-due", "mixed-overdue"]);
  assert.deepEqual(combinedIds(["overdue", "today", "next"], 7), [
    "future-due",
    "mixed-overdue",
    "overdue-due",
    "today-contractual",
    "today-requested"
  ]);
  assert.deepEqual(combinedIds(["next"], 8), ["future-due", "future-eight", "mixed-overdue"]);
  assert.equal(combinedIds([], 7).length, items.length);
});

test("assigned projects remain available even when they have no open queue items", () => {
  const index = Core.buildActivityIndex({
    context: {
      projectId: "a",
      itemType: "rfi",
      itemLabel: "RFI",
      itemLabelPlural: "RFIs",
      user: { id: "alice", fullName: "Alice Example", companyId: "company-a" }
    },
    items: [],
    queueItems: [],
    projects: [
      { id: "a", name: "Alpha" },
      { id: "b", name: "Beta — no open items" }
    ],
    comments: [],
    attachments: [],
    histories: []
  });

  assert.deepEqual(index.projects, [
    { id: "a", name: "Alpha" },
    { id: "b", name: "Beta — no open items" }
  ]);
  assert.deepEqual(index.openQueue, []);
});
