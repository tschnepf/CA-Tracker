"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const Mapper = require("../integrations/taskhub/mapper.js");

function currentItem(overrides) {
  return Object.assign({
    id: "rfi-123",
    itemType: "rfi",
    projectId: "project-1",
    projectName: "Central Library",
    number: "0123",
    title: "Confirm storefront dimensions",
    status: "Review",
    responsible: "Alex Example is responsible",
    dates: {
      created: "2026-07-01T08:00:00Z",
      modified: "2026-07-22T15:30:00Z",
      due: "2026-07-30T07:00:00Z",
      requestedReview: "2026-07-28T07:00:00Z",
      review: "2026-07-20T10:00:00Z"
    },
    workflow: {
      required: [
        {
          assigned: { contactId: "reviewer-before", fullName: "Reviewer Before" },
          reviewedAt: "2026-07-22T15:30:00Z"
        },
        {
          assigned: { contactId: "alex", fullName: "Alex Example" },
          reviewedAt: null
        }
      ]
    },
    queueState: { code: "current", position: 2, dueAt: "2026-07-30T07:00:00Z" },
    queueAssignments: [
      {
        personKey: "contact:alex",
        person: { fullName: "Alex Example", company: "Example Co" },
        state: { code: "current", position: 2 }
      }
    ]
  }, overrides || {});
}

test("snapshot includes only selected people currently in court with all requested dates", () => {
  const upcoming = currentItem({
    id: "submittal-upcoming",
    itemType: "submittal",
    queueState: { code: "upcoming", position: 3 },
    queueAssignments: [{
      personKey: "contact:alex",
      person: { fullName: "Alex Example", company: "Example Co" },
      state: { code: "upcoming", position: 3 }
    }]
  });
  const index = {
    scannedAt: "2026-07-23T12:00:00Z",
    warnings: [],
    currentPersonKey: "contact:alex",
    people: [
      { key: "contact:alex", fullName: "Alex Example" },
      { key: "contact:pat", fullName: "Pat Example" }
    ],
    openQueue: [currentItem(), upcoming]
  };

  const snapshot = Mapper.buildSnapshot(index, { people: ["contact:alex"] });

  assert.equal(snapshot.complete, true);
  assert.deepEqual(snapshot.selected_people, ["contact:alex"]);
  assert.equal(snapshot.items.length, 1);
  assert.equal(snapshot.items[0].item_id, "rfi-123");
  assert.equal(snapshot.items[0].helix_created_at, "2026-07-01T08:00:00.000Z");
  assert.equal(snapshot.items[0].assignments[0].court_started_at, "2026-07-22T15:30:00.000Z");
  assert.equal(snapshot.items[0].assignments[0].court_date_source, "helix_workflow");
  assert.equal(snapshot.items[0].due_at, "2026-07-28T07:00:00.000Z");
  assert.equal(snapshot.items[0].due_date_source, "requested_review");
  assert.equal(
    snapshot.items[0].source_url,
    "https://project.helixteam.com/Projects/project-1/RFI#helix-activity-open=rfi-123"
  );
});

test("warnings make a snapshot incomplete so missing tasks are not archived", () => {
  const index = {
    scannedAt: "2026-07-23T12:00:00Z",
    warnings: ["One assigned project could not be read"],
    currentPersonKey: "contact:alex",
    people: [{ key: "contact:alex", fullName: "Alex Example" }],
    openQueue: []
  };

  const snapshot = Mapper.buildSnapshot(index, null);

  assert.equal(snapshot.complete, false);
  assert.deepEqual(snapshot.items, []);
  assert.deepEqual(snapshot.selected_people, ["contact:alex"]);
});

test("missing Helix due fields stay empty instead of using a modified-date fallback", () => {
  const item = currentItem({
    dates: {
      created: "2026-07-01T08:00:00Z",
      modified: "2026-07-22T15:30:00Z",
      review: "2026-07-20T10:00:00Z"
    },
    queueState: {
      code: "current",
      position: 2,
      dueAt: "2026-07-22T15:30:00Z"
    }
  });

  assert.deepEqual(Mapper.earliestDue(item), { dueAt: null, source: null });
});

test("custom people selection excludes unselected current assignments", () => {
  const patItem = currentItem({
    id: "submittal-456",
    itemType: "submittal",
    projectId: "project-2",
    projectName: "Clinic",
    queueAssignments: [{
      personKey: "contact:pat",
      person: { fullName: "Pat Example", company: "Example Co" },
      state: { code: "current", position: 1 }
    }],
    workflow: {
      required: [{
        assigned: { contactId: "pat", fullName: "Pat Example" },
        reviewedAt: null
      }]
    }
  });
  const index = {
    scannedAt: "2026-07-23T12:00:00Z",
    warnings: [],
    currentPersonKey: "contact:alex",
    people: [
      { key: "contact:alex", fullName: "Alex Example" },
      { key: "contact:pat", fullName: "Pat Example" }
    ],
    openQueue: [currentItem(), patItem]
  };

  const snapshot = Mapper.buildSnapshot(index, { people: ["contact:pat"] });

  assert.deepEqual(snapshot.selected_people, ["contact:pat"]);
  assert.deepEqual(snapshot.items.map((item) => item.item_id), ["submittal-456"]);
  assert.match(snapshot.items[0].source_url, /\/Submittals#/);
});
