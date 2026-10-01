"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const Bridge = require("../../src/SmcCaTracker.App/Scripts/bridge-core.js");

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

test("prepareItemDownload resolves the official Details report and current protected attachments", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectRFIs: [{
      _id: "rfi-download",
      refProject: { _id: projectId },
      displayNumber: "P000210",
      title: "Cooling door coordination",
      status: { name: "Review", rank: 800 }
    }],
    HelixProjectAttachments: [{
      _id: "attachment-current",
      idItem: "rfi-download",
      idProject: projectId,
      description: "Review markup.pdf",
      isActive: true,
      isCurrent: true,
      _kmd: { ect: "2026-08-03T18:45:00.000Z" }
    }],
    HelixProjectReports: [{
      _id: "details-rfis",
      reportName: "Details (RFIs)",
      functionName: "DetailView",
      featureName: "RFIs",
      isActive: true,
      isV4: true,
      isClientReport: true,
      settingsParams: [{
        propName: "itemID",
        remoteProp: "DetailEdit.loadedModel._id"
      }]
    }]
  }, "rfi");
  const protectedCalls = [];
  windowObject.Master.callCustomEndpoint = async (...args) => {
    protectedCalls.push(args);
    return { downloadURL: "https://storage.googleapis.com/example/signed-file" };
  };

  const result = await Bridge.prepareItemDownload(
    windowObject,
    "rfi-download",
    "rfi",
    projectId
  );
  assert.equal(result.prepared, true);
  assert.equal(result.report.kind, "client");
  assert.match(result.report.url, /\/Reports\/clientReportViewer\?/);
  assert.match(result.report.url, /rid=details-rfis/);
  assert.match(result.report.url, /itemID=rfi-download/);
  assert.match(result.report.url, /helixActivityDownload=1/);
  assert.deepEqual(result.attachments, [{
    id: "attachment-current",
    filename: "Review markup.pdf",
    uploadedAt: "2026-08-03T18:45:00.000Z",
    url: "https://storage.googleapis.com/example/signed-file"
  }]);
  assert.deepEqual(protectedCalls, [["getDownloadURL", {
    collectionName: "HelixProjectAttachments",
    _id: "attachment-current",
    propName: "file",
    preferThumbnail: true,
    idProject: projectId
  }]]);
  const attachmentRead = windowObject.calls.find((call) => (
    call.collection === Bridge.COLLECTIONS.attachments
      && call.query.filter && call.query.filter.idItem === "rfi-download"
  ));
  assert.ok(attachmentRead.query.fields.includes("_kmd"));
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

test("listAssignedProjects reads the signed-in user's projects from any Helix page", async () => {
  const page = mockWindow({
    HelixProjectDirectory: [
      { _id: "d1", refContact: { _id: "contact-current" }, refProject: { _id: "p2", name: "Tower" }, isActive: true },
      { _id: "d2", refContact: { _id: "contact-current" }, refProject: { _id: "p1", name: "Aligned CMH04" }, isActive: true },
      { _id: "d3", refContact: { _id: "contact-current" }, refProject: { _id: "p3", name: "Old" }, isActive: false },
      { _id: "d4", refContact: { _id: "someone-else" }, refProject: { _id: "p4", name: "Not mine" }, isActive: true }
    ]
  }, "overview");
  page.location.href = "https://project.helixteam.com/Home";
  const result = await Bridge.listAssignedProjects(page);
  assert.equal(result.user.fullName, "Current User");
  assert.equal(result.user.company, "Example Co");
  assert.deepEqual(result.projects, [{ id: "p1", name: "Aligned CMH04" }, { id: "p2", name: "Tower" }]);
  assert.equal(page.calls[0].collection, "HelixProjectDirectory");

  assert.equal(Bridge.currentUser({ location: page.location }), null);
  await assert.rejects(() => Bridge.listAssignedProjects({ location: page.location }), /still loading/);
});

test("court start is when the item's court last changed to what it is now", () => {
  const { courtStartFromHistory } = Bridge._test;
  const step = (id, reviewed) => ({ refDirectoryAssigned: { _id: id, fullName: id }, dateReviewed: reviewed || null });
  const version = (at, status, message, required) => ({
    _kmd: { lmt: at }, status: { name: status }, statusMessage: message, workflow: { workflowRequired: required }
  });
  const now = version("2026-09-20T10:00:00Z", "Review", "", [step("arch", "2026-09-10T09:00:00Z"), step("smc")]);
  const history = [
    version("2026-09-01T08:00:00Z", "Open", "Waiting on submitter", [step("arch"), step("smc")]),
    version("2026-09-05T08:00:00Z", "Review", "", [step("arch"), step("smc")]),
    version("2026-09-10T09:00:00Z", "Review", "", [step("arch", "2026-09-10T09:00:00Z"), step("smc")]),
    version("2026-09-15T12:00:00Z", "Review", "", [step("arch", "2026-09-10T09:00:00Z"), step("smc")])
  ];
  // Reached SMC's step on Sep 10; the Sep 15 and Sep 20 saves didn't change the court.
  assert.equal(courtStartFromHistory(now, history), "2026-09-10T09:00:00.000Z");
  // Back in an earlier court after a change: only the latest unbroken run counts.
  const bounced = version("2026-09-25T10:00:00Z", "Open", "Waiting on submitter", [step("arch"), step("smc")]);
  assert.equal(courtStartFromHistory(bounced, history), "2026-09-25T10:00:00.000Z");
  assert.equal(courtStartFromHistory(now, []), null, "no history means no date");
});

test("prepareItemDownload still lists attachments when Helix offers no Details report", async () => {
  const page = mockWindow({
    HelixProjectAttachments: [{ _id: "a1", idItem: "s1", filename: "Drawings - R2.pdf", isActive: true, isCurrent: true, _kmd: { ect: "2026-09-25T15:00:00Z" } }],
    HelixProjectReports: []
  }, "submittal");
  page.Master.readFromCollection = (original => async (collection, query) => {
    if (collection === "HelixProjectSubmittalPackages") return [{ _id: "s1", refProject: { _id: projectId }, number: "000641" }];
    return original(collection, query);
  })(page.Master.readFromCollection);
  page.Master.callCustomEndpoint = async () => ({ downloadURL: "https://files.example/a1" });
  const result = await Bridge.prepareItemDownload(page, "s1", "submittal", projectId);
  assert.equal(result.report, null);
  assert.match(result.reportError, /Details \(Submittals\)/);
  assert.deepEqual(result.attachments.map((attachment) => attachment.filename), ["Drawings - R2.pdf"]);
});

test("scanProjectItems reads only the page's project items, with court dates, in scan's shape", async () => {
  Bridge._test.resetCache();
  const windowObject = mockWindow({
    HelixProjectRFIs: [
      {
        _id: "rfi-a",
        refProject: { _id: projectId },
        number: "0001",
        title: "Waiting on me",
        status: { name: "Review", rank: 800 },
        statusMessage: "Current User",
        _kmd: { lmt: "2026-09-10T00:00:00.000Z" }
      },
      { _id: "rfi-other-project", refProject: { _id: "elsewhere" }, number: "9", status: { name: "Review", rank: 800 } }
    ],
    HelixProjectHistories: [
      { originalId: "rfi-a", status: { name: "Open", rank: 100 }, statusMessage: "Other User", _kmd: { lmt: "2026-09-01T00:00:00.000Z" } },
      { originalId: "rfi-a", status: { name: "Review", rank: 800 }, statusMessage: "Current User", _kmd: { lmt: "2026-09-05T00:00:00.000Z" } }
    ]
  }, "rfi");

  const result = await Bridge.scanProjectItems(windowObject);
  assert.deepEqual(result.items.map((item) => item.id), ["rfi-a"]);
  assert.equal(result.items[0].courtSince, "2026-09-05T00:00:00.000Z");
  assert.equal(result.context.user.fullName, "Current User");
  assert.deepEqual(result.queueItems, []);
  assert.ok(!windowObject.calls.some((call) => ["HelixProjectComments", "HelixProjectAttachments", "HelixProjectDirectory"]
    .includes(call.collection)), "no cross-project, comment or upload reads");
});

test("itemCourtState changes when the item is sent to someone else", async () => {
  const record = {
    _id: "rfi-a",
    refProject: { _id: projectId },
    status: { name: "Review", rank: 800 },
    statusMessage: "Current User",
    _kmd: { lmt: "2026-09-10T00:00:00.000Z" }
  };
  const windowObject = mockWindow({ HelixProjectRFIs: [record] }, "rfi");
  const before = await Bridge.itemCourtState(windowObject, "rfi-a", "rfi", projectId);
  record.statusMessage = "Other User";
  record._kmd = { lmt: "2026-09-11T00:00:00.000Z" };
  const after = await Bridge.itemCourtState(windowObject, "rfi-a", "rfi", projectId);
  assert.notEqual(before.signature, after.signature);
  assert.equal(after.modifiedAt, "2026-09-11T00:00:00.000Z");
  await assert.rejects(() => Bridge.itemCourtState(windowObject, "missing", "rfi", projectId), /no longer lists/);
});
