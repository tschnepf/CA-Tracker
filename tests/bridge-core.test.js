"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const Bridge = require("../bridge-core.js");

const projectId = "project-current";
const user = {
  _id: "directory-current",
  fullName: "Current User",
  refContact: { _id: "contact-current" },
  refCompany: { _id: "company-current", name: "Example Co" }
};
const other = { _id: "directory-other", fullName: "Other User" };

function mockWindow(datasets, itemType) {
  const calls = [];
  const opened = [];
  const collectionName = itemType === "submittal"
    ? "HelixProjectSubmittalPackages"
    : "HelixProjectRFIs";
  const master = {
    async readFromCollection(collection, query) {
      calls.push({ collection, query: JSON.parse(JSON.stringify(query)) });
      let values = datasets[collection] || [];
      const filter = query.filter || {};
      if (filter["refProject._id"]) {
        values = values.filter((record) => record.refProject
          && String(record.refProject._id) === String(filter["refProject._id"]));
      }
      if (filter["refContact._id"]) {
        values = values.filter((record) => record.refContact
          && String(record.refContact._id) === String(filter["refContact._id"]));
      }
      if (filter.isActive === true) values = values.filter((record) => record.isActive !== false);
      if (filter["status.rank"] && filter["status.rank"].$lt) {
        values = values.filter((record) => !record.status || typeof record.status.rank !== "number"
          || record.status.rank < filter["status.rank"].$lt);
      }
      return values.slice(query.skip, query.skip + query.limit);
    },
    async loadDetailView(_event, record) {
      opened.push(record._id);
    }
  };
  return {
    location: {
      href: `https://project.helixteam.com/Projects/${projectId}/${itemType === "submittal" ? "Submittals" : itemType === "overview" ? "Overview" : "RFI"}`
    },
    UserInfo: { directoryInfo: user, projectInfo: { name: "Current Project" } },
    Master: master,
    gridNamespace: {
      collectionName,
      detailEditColumns: [{ module: "workflowRequired" }],
      baseGrid_onManipulateDataOnRead(records) {
        return records.map((record) => {
          record.workflow = record.workflow || {};
          record.workflow.workflowRequired = record.workflow.workflowRequired || [];
          record.workflow.workflowOptional = record.workflow.workflowOptional || [];
          record.workflow.refDirectoriesNotify = record.workflow.refDirectoriesNotify || [];
          return record;
        });
      }
    },
    calls,
    opened
  };
}

test("getContext derives project and user identity from the current Helix page", () => {
  const context = Bridge.getContext(mockWindow({}));
  assert.deepEqual(context, {
    projectId,
    projectName: "Current Project",
    contactId: "contact-current",
    itemType: "rfi",
    itemLabel: "RFI",
    itemLabelPlural: "RFIs",
    user: { id: user._id, contactId: "contact-current", fullName: user.fullName, companyId: "company-current", company: "Example Co" }
  });
});

test("Overview scans both current-project RFIs and Submittals", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectDirectory: [{
      _id: "directory-current",
      refContact: { _id: "contact-current" },
      refProject: { _id: projectId, name: "Current Project" },
      isActive: true
    }],
    HelixProjectRFIs: [{
      _id: "rfi-overview",
      refProject: { _id: projectId },
      number: "0007",
      title: "Overview RFI",
      status: { name: "Review", rank: 800 }
    }],
    HelixProjectSubmittalPackages: [{
      _id: "submittal-overview",
      refProject: { _id: projectId },
      number: "0008",
      description: "Overview Submittal",
      status: { name: "Review", rank: 800 }
    }],
    HelixProjectComments: [],
    HelixProjectAttachments: [],
    HelixProjectHistories: []
  }, "overview");

  const result = await Bridge.scan(windowObject);
  assert.equal(result.context.itemType, "overview");
  assert.equal(result.context.itemLabelPlural, "Items");
  assert.deepEqual(result.items.map((item) => [item.itemType, item.id]), [
    ["rfi", "rfi-overview"],
    ["submittal", "submittal-overview"]
  ]);
});

test("scan uses fixed read collections, filters to the current project/user, and sanitizes results", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectRFIs: [
      { _id: "rfi-current", refProject: { _id: projectId }, title: "Current", number: "1", status: { name: "Review" }, refDirectoryCreator: user, _kmd: { ect: "2026-01-01" } },
      { _id: "rfi-other-project", refProject: { _id: "project-other" }, title: "Other", number: "2", status: { name: "Review" } }
    ],
    HelixProjectSubmittalPackages: [{
      _id: "submittal-other-project",
      refProject: { _id: "project-other" },
      number: "0008",
      description: "Other project submittal",
      status: { name: "Review", rank: 800 },
      workflow: { workflowRequired: [{ refDirectoryAssigned: user, isReviewed: false }] }
    }],
    HelixProjectDirectory: [
      { _id: "directory-current", refContact: { _id: "contact-current" }, refProject: { _id: projectId, name: "Current Project" }, isActive: true },
      { _id: "directory-empty-project", refContact: { _id: "contact-current" }, refProject: { _id: "project-empty", name: "Empty Project" }, isActive: true },
      { _id: "directory-other-project", refContact: { _id: "contact-current" }, refProject: { _id: "project-other", name: "Other Project" }, isActive: true },
      { _id: "directory-inactive", refContact: { _id: "contact-current" }, refProject: { _id: "project-inactive", name: "Inactive Project" }, isActive: false },
      { _id: "directory-someone-else", refContact: { _id: "contact-other" }, refProject: { _id: "project-not-mine", name: "Not Mine" }, isActive: true }
    ],
    HelixProjectComments: [
      { _id: "comment-current", idItem: "rfi-current", idProject: projectId, refDirectory: user, comment: "Mine", _kmd: { ect: "2026-01-02" } },
      { _id: "comment-other", idItem: "rfi-current", idProject: projectId, refDirectory: other, comment: "Not mine" }
    ],
    HelixProjectAttachments: [
      { _id: "file-current", idItem: "rfi-current", idProject: projectId, refDirectoryCreator: user, filename: "mine.pdf" },
      { _id: "file-other", idItem: "rfi-current", idProject: projectId, refDirectoryCreator: other, filename: "other.pdf" }
    ],
    HelixProjectHistories: [
      { _id: "history-current", originalId: "rfi-current", nameEditor: user.fullName, _kmd: { lmt: "2026-01-03" } },
      { _id: "history-other", originalId: "rfi-current", nameEditor: other.fullName }
    ]
  });

  const result = await Bridge.scan(windowObject);
  assert.deepEqual(result.rfis.map((record) => record.id), ["rfi-current"]);
  assert.deepEqual(result.comments.map((record) => record.id), ["comment-current"]);
  assert.deepEqual(result.attachments.map((record) => record.id), ["file-current"]);
  assert.deepEqual(result.histories.map((record) => record.id), ["history-current"]);
  assert.deepEqual(new Set(windowObject.calls.map((call) => call.collection)), new Set([
    Bridge.COLLECTIONS.rfis,
    Bridge.COLLECTIONS.submittals,
    Bridge.COLLECTIONS.directories,
    Bridge.COLLECTIONS.comments,
    Bridge.COLLECTIONS.attachments,
    Bridge.COLLECTIONS.histories
  ]));

  const rfiCall = windowObject.calls.find((call) => call.collection === Bridge.COLLECTIONS.rfis);
  assert.equal(rfiCall.query.filter["refProject._id"], projectId);
  const commentCall = windowObject.calls.find((call) => call.collection === Bridge.COLLECTIONS.comments);
  assert.equal(commentCall.query.filter["refDirectory._id"], user._id);
  const directoryCall = windowObject.calls.find((call) => call.collection === Bridge.COLLECTIONS.directories);
  assert.deepEqual(directoryCall.query.filter, { "refContact._id": "contact-current", isActive: true });
  const otherProjectRfiCall = windowObject.calls.find((call) => call.collection === Bridge.COLLECTIONS.rfis
    && call.query.filter["refProject._id"] === "project-other");
  assert.ok(otherProjectRfiCall, "Expected an individual open-RFI read for the assigned project");
  assert.deepEqual(result.projects, [
    { id: projectId, name: "Current Project" },
    { id: "project-empty", name: "Empty Project" },
    { id: "project-other", name: "Other Project" }
  ]);
  assert.deepEqual(result.queueItems.map((record) => [record.itemType, record.projectName]), [
    ["rfi", "Current Project"],
    ["rfi", "Other Project"],
    ["submittal", "Other Project"]
  ]);
});

test("readAll paginates and rejects collections outside the allowlist", async () => {
  let callCount = 0;
  const master = {
    async readFromCollection() {
      callCount += 1;
      return callCount === 1 ? Array.from({ length: 250 }, (_, index) => ({ index })) : [{ index: 250 }];
    }
  };
  const records = await Bridge.readAll(master, Bridge.COLLECTIONS.rfis, { filter: {} });
  assert.equal(records.length, 251);
  assert.equal(callCount, 2);
  await assert.rejects(() => Bridge.readAll(master, "UnknownCollection", { filter: {} }), /not permitted/);
});

test("scan limits all collection reads to three concurrent requests", async () => {
  Bridge._test.resetCache();
  const directories = Array.from({ length: 6 }, (_value, index) => ({
    _id: `directory-${index}`,
    refContact: { _id: "contact-current" },
    refProject: { _id: index === 0 ? projectId : `project-${index}`, name: `Project ${index}` },
    isActive: true
  }));
  const windowObject = mockWindow({
    HelixProjectRFIs: [{
      _id: "rfi-current",
      refProject: { _id: projectId },
      title: "Current",
      status: { name: "Review", rank: 800 }
    }],
    HelixProjectSubmittalPackages: [],
    HelixProjectDirectory: directories,
    HelixProjectComments: [],
    HelixProjectAttachments: [],
    HelixProjectHistories: []
  });
  const readFromCollection = windowObject.Master.readFromCollection;
  let activeReads = 0;
  let maximumReads = 0;
  windowObject.Master.readFromCollection = async (...args) => {
    activeReads += 1;
    maximumReads = Math.max(maximumReads, activeReads);
    await new Promise((resolve) => setTimeout(resolve, 4));
    try {
      return await readFromCollection(...args);
    } finally {
      activeReads -= 1;
    }
  };

  await Bridge.scan(windowObject);
  assert.ok(maximumReads > 1, "Expected the scan to retain bounded parallel reads");
  assert.ok(maximumReads <= 3, `Observed ${maximumReads} simultaneous Helix reads`);
});

test("openItem opens current-page records and returns a safe route for a scanned cross-project item", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectRFIs: [
      { _id: "rfi-current", refProject: { _id: projectId }, title: "Current", status: { name: "Review" } },
      { _id: "rfi-other", refProject: { _id: "project-other" }, title: "Other", status: { name: "Review" } }
    ],
    HelixProjectSubmittalPackages: [],
    HelixProjectDirectory: [
      { _id: "directory-current", refContact: { _id: "contact-current" }, refProject: { _id: projectId, name: "Current Project" }, isActive: true },
      { _id: "directory-other", refContact: { _id: "contact-current" }, refProject: { _id: "project-other", name: "Other Project" }, isActive: true }
    ],
    HelixProjectComments: [],
    HelixProjectAttachments: [],
    HelixProjectHistories: []
  });
  await Bridge.scan(windowObject);
  await Bridge.openItem(windowObject, "rfi-current");
  assert.deepEqual(windowObject.opened, ["rfi-current"]);
  const navigation = await Bridge.openItem(windowObject, "rfi-other", "rfi", "project-other");
  assert.equal(navigation.opened, false);
  assert.equal(navigation.navigateTo, "https://project.helixteam.com/Projects/project-other/RFI");
  assert.deepEqual(windowObject.opened, ["rfi-current"]);
  assert.ok(Bridge._test.navigationDescriptors().every((descriptor) => {
    assert.deepEqual(Object.keys(descriptor).sort(), ["itemId", "itemType", "projectId"]);
    return true;
  }));
  await assert.rejects(() => Bridge.openItem(windowObject, "unknown-rfi"), /could not be found/);
});

test("openItem can safely look up and open a current-page item after cross-page navigation", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectRFIs: [{
      _id: "rfi-destination",
      refProject: { _id: projectId },
      title: "Destination",
      status: { name: "Review" }
    }]
  });

  const result = await Bridge.openItem(windowObject, "rfi-destination", "rfi", projectId);
  assert.equal(result.opened, true);
  assert.deepEqual(windowObject.opened, ["rfi-destination"]);
  const lookup = windowObject.calls.find((call) => call.collection === Bridge.COLLECTIONS.rfis);
  assert.deepEqual(lookup.query.filter, { _id: "rfi-destination", "refProject._id": projectId });
});

test("openItem fetches a complete record and applies Helix's native grid normalization", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectRFIs: [{
      _id: "rfi-complete",
      refProject: { _id: projectId },
      title: "Complete detail",
      response: "This field is not part of the activity scan projection.",
      status: { name: "Review" },
      workflow: { workflowRequired: [{ refDirectoryAssigned: user }] }
    }],
    HelixProjectDirectory: [],
    HelixProjectSubmittalPackages: [],
    HelixProjectComments: [],
    HelixProjectAttachments: [],
    HelixProjectHistories: []
  });
  const readFromCollection = windowObject.Master.readFromCollection;
  windowObject.Master.readFromCollection = async (collection, query) => {
    const records = await readFromCollection(collection, query);
    if (!query.fields) return records;
    return records.map((record) => {
      const projected = Object.assign({}, record);
      delete projected.response;
      return projected;
    });
  };
  let normalizedCount = 0;
  let openedRecord = null;
  windowObject.gridNamespace.baseGrid_onManipulateDataOnRead = (records) => records.map((record) => {
    normalizedCount += 1;
    record.workflow = record.workflow || {};
    record.workflow.workflowRequired = record.workflow.workflowRequired || [];
    record.workflow.workflowOptional = record.workflow.workflowOptional || [];
    record.workflow.refDirectoriesNotify = record.workflow.refDirectoriesNotify || [];
    return record;
  });
  windowObject.Master.loadDetailView = async (_event, record) => { openedRecord = record; };

  await Bridge.scan(windowObject);
  await Bridge.openItem(windowObject, "rfi-complete", "rfi", projectId);

  const detailRead = windowObject.calls.find((call) => call.collection === Bridge.COLLECTIONS.rfis
    && call.query.filter && call.query.filter._id === "rfi-complete" && !call.query.fields);
  assert.ok(detailRead, "Expected a fresh, unprojected detail-record read");
  assert.equal(normalizedCount, 1);
  assert.equal(openedRecord.response, "This field is not part of the activity scan projection.");
  assert.equal(openedRecord.workflow.workflowRequired.length, 1);
  assert.deepEqual(openedRecord.workflow.workflowOptional, []);
  assert.deepEqual(openedRecord.workflow.refDirectoriesNotify, []);
});

test("openItem closes another open Helix detail viewer before opening the requested item", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectRFIs: [
      { _id: "rfi-first", refProject: { _id: projectId }, title: "First", status: { name: "Review" } },
      { _id: "rfi-second", refProject: { _id: projectId }, title: "Second", status: { name: "Review" } }
    ]
  });
  const events = [];
  let detailVisible = false;
  const widget = {
    wrapper: { is: () => detailVisible },
    close() {
      events.push("close:rfi-first");
      detailVisible = false;
      windowObject.Master.detailStack = [];
    }
  };
  windowObject.$ = (selector) => ({
    data: (name) => selector === "#detailWindow" && name === "kendoWindow" ? widget : null
  });
  windowObject.document = { querySelectorAll: () => [] };
  windowObject.Master.loadDetailView = async (_event, record) => {
    events.push(`open:${record._id}`);
    detailVisible = true;
    windowObject.Master.detailStack = [{ id: record._id }];
  };

  await Bridge.openItem(windowObject, "rfi-first", "rfi", projectId);
  await Bridge.openItem(windowObject, "rfi-second", "rfi", projectId);

  assert.deepEqual(events, ["open:rfi-first", "close:rfi-first", "open:rfi-second"]);
});

test("openItem leaves the requested item open and does not reload it", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectRFIs: [
      { _id: "rfi-open", refProject: { _id: projectId }, title: "Open", status: { name: "Review" } }
    ]
  });
  let detailVisible = true;
  let closeCount = 0;
  windowObject.Master.detailStack = [{ id: "rfi-open" }];
  windowObject.$ = () => ({
    data: () => ({
      wrapper: { is: () => detailVisible },
      close() {
        closeCount += 1;
        detailVisible = false;
      }
    })
  });
  windowObject.document = { querySelectorAll: () => [] };

  const result = await Bridge.openItem(windowObject, "rfi-open", "rfi", projectId);

  assert.equal(result.alreadyOpen, true);
  assert.equal(closeCount, 0);
  assert.deepEqual(windowObject.opened, []);
});

test("openItem reports unsaved changes instead of timing out or replacing the open item", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectRFIs: [
      { _id: "rfi-first", refProject: { _id: projectId }, title: "First", status: { name: "Review" } },
      { _id: "rfi-second", refProject: { _id: projectId }, title: "Second", status: { name: "Review" } }
    ]
  });
  let closeCount = 0;
  windowObject.Master.hasChanges = true;
  windowObject.Master.detailStack = [{ id: "rfi-first" }];
  windowObject.$ = () => ({
    data: () => ({
      wrapper: { is: () => true },
      close() { closeCount += 1; }
    })
  });
  windowObject.document = { querySelectorAll: () => [] };

  await assert.rejects(
    () => Bridge.openItem(windowObject, "rfi-second", "rfi", projectId),
    /unsaved changes/
  );
  assert.equal(closeCount, 1);
  assert.deepEqual(windowObject.opened, []);
});

test("Submittals use their fixed package collection and preserve searchable register metadata", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectSubmittalPackages: [{
      _id: "submittal-current",
      refProject: { _id: projectId },
      number: "000501",
      description: "Air handler shop drawings",
      status: { name: "Review", rank: 800 },
      refContractSupplier: { displayName: "503 - Mechanical" },
      refsSubmittalRegisters: [{ number: "000880", isActive: true }, { number: "old", isActive: false }],
      refDirectoryCreator: other,
      nameEditor: user.fullName,
      workflow: { workflowRequired: [{ refDirectoryAssigned: user, refDirectoryReviewed: true, dateReviewed: "2026-01-15" }] },
      _kmd: { ect: "2026-01-01", lmt: "2026-02-01" }
    }],
    HelixProjectDirectory: [{
      _id: "directory-current",
      refContact: { _id: "contact-current" },
      refProject: { _id: projectId, name: "Current Project" },
      isActive: true
    }],
    HelixProjectComments: [{
      _id: "sub-comment",
      idItem: "submittal-current",
      idProject: projectId,
      refDirectory: user,
      comment: "Reviewed package"
    }],
    HelixProjectAttachments: [],
    HelixProjectHistories: []
  }, "submittal");

  const result = await Bridge.scan(windowObject);
  assert.equal(result.context.itemType, "submittal");
  assert.equal(result.context.itemLabelPlural, "Submittals");
  assert.equal(result.items.length, 1);
  assert.equal(result.rfis, undefined);
  assert.equal(result.items[0].title, "Air handler shop drawings");
  assert.equal(result.items[0].contract, "503 - Mechanical");
  assert.deepEqual(result.items[0].registers, ["000880"]);
  assert.equal(result.items[0].workflow.required[0].assigned.companyId, "company-current");
  assert.equal(result.items[0].workflow.required[0].reviewedAt, "2026-01-15T00:00:00.000Z");
  assert.deepEqual(result.comments.map((record) => record.itemId), ["submittal-current"]);
  assert.ok(windowObject.calls.some((call) => call.collection === Bridge.COLLECTIONS.submittals));
  assert.ok(windowObject.calls.some((call) => call.collection === Bridge.COLLECTIONS.rfis));

  await Bridge.openItem(windowObject, "submittal-current");
  assert.deepEqual(windowObject.opened, ["submittal-current"]);
});

test("history batches stay below Helix's read URL length guard", async () => {
  Bridge._test.resetCache();
  const rfis = Array.from({ length: 20 }, (_value, index) => ({
    _id: String(index).padStart(24, "a"),
    refProject: { _id: projectId },
    title: `RFI ${index}`,
    status: { name: "Review" }
  }));
  const windowObject = mockWindow({
    HelixProjectRFIs: rfis,
    HelixProjectDirectory: [{
      _id: "directory-current",
      refContact: { _id: "contact-current" },
      refProject: { _id: projectId, name: "Current Project" },
      isActive: true
    }],
    HelixProjectComments: [],
    HelixProjectAttachments: [],
    HelixProjectHistories: []
  });
  await Bridge.scan(windowObject);
  const historyCall = windowObject.calls.find((call) => call.collection === Bridge.COLLECTIONS.histories);
  assert.ok(historyCall);
  const fullUrl = `https://helix-api.helixteam.com/appData/app/HelixProjectHistories?query=${JSON.stringify(historyCall.query)}`;
  assert.ok(fullUrl.length < 2048, `History query was ${fullUrl.length} characters`);
});
