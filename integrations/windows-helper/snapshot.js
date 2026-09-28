(function attachWindowsHelperSnapshot(root, factory) {
  const api = factory(root.HelixActivityCore, root.HelixTaskHubMapper);
  root.TaskHubWindowsHelperSnapshot = api;
  if (typeof module === "object" && module.exports) {
    module.exports = factory(require("../../activity-core.js"), require("../taskhub/mapper.js"));
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function createWindowsHelperSnapshot(Core, Mapper) {
  "use strict";

  // Snapshots describe what a provider currently shows for one project so the optional
  // CA Tracker Windows helper can compare it with that project's CA folder. They contain
  // item identity, status, and who currently holds each item — never files, comments,
  // or credentials.
  const HOST_NAME = "com.smc.catracker";
  const SCHEMA_VERSION = 1;
  const MAX_ITEMS = 5000;
  const ITEM_TYPES = ["rfi", "submittal"];

  function text(value, limit) {
    return String(value === null || value === undefined ? "" : value).trim().slice(0, limit || 300);
  }

  function isoDate(value) {
    if (!value) return null;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }

  function isClosed(item) {
    const status = text(item && item.status).toLocaleLowerCase();
    const rank = item && Number.isFinite(item.statusRank) ? item.statusRank : null;
    return (rank !== null && rank >= 1300) || status === "complete" || status === "voided";
  }

  function identityKey(identity) {
    if (!identity) return null;
    if (identity.contactId) return `contact:${identity.contactId}`;
    if (identity.id) return `directory:${identity.id}`;
    if (identity.fullName) return `name:${text(identity.fullName).toLocaleLowerCase()}`;
    return null;
  }

  function collectPeople(items, user) {
    const people = new Map();
    function remember(identity) {
      const key = identityKey(identity);
      if (!key || !identity.fullName) return;
      if (!people.has(key)) people.set(key, identity);
    }
    remember(user);
    for (const item of items) {
      const workflow = item && item.workflow || {};
      for (const entry of [].concat(workflow.required || [], workflow.optional || [])) {
        remember(entry && entry.assigned);
      }
      remember(item && item.creator);
      remember(item && item.responseEditor);
    }
    return Array.from(people.values());
  }

  // Everyone who currently holds (or will next hold) the item, using the same In court /
  // Coming up rules as the Open queue. The helper filters these by company.
  function holdersFor(item, people) {
    if (isClosed(item)) return [];
    const holders = [];
    const seen = new Set();
    for (const person of people) {
      const state = Core.classifyOpenQueue(item, person);
      if (!state) continue;
      const key = identityKey(person);
      if (seen.has(key)) continue;
      seen.add(key);
      holders.push({
        name: text(person.fullName, 160),
        companyId: person.companyId ? text(person.companyId, 80) : null,
        company: person.company ? text(person.company, 160) : null,
        state: state.code === "current" ? "current" : "upcoming"
      });
    }
    return holders;
  }

  function coveredHelixTypes(scanResult) {
    const context = scanResult.context || {};
    const types = context.itemType === "overview" || !ITEM_TYPES.includes(context.itemType)
      ? ITEM_TYPES.slice()
      : [context.itemType];
    const warnings = Array.isArray(scanResult.warnings) ? scanResult.warnings : [];
    return types.filter((itemType) => {
      const plural = itemType === "rfi" ? "RFIs" : "Submittals";
      return !warnings.some((warning) => warning
        && String(warning.collection || "") === `current-project ${plural}`);
    });
  }

  function buildHelixSnapshot(scanResult) {
    if (!Core || !Mapper) throw new Error("Helix activity helpers are unavailable.");
    if (!scanResult || !scanResult.context || !scanResult.context.projectId) {
      throw new Error("A Helix project scan is required.");
    }
    const context = scanResult.context;
    const user = context.user || {};
    const itemTypes = coveredHelixTypes(scanResult);
    const scanned = Array.isArray(scanResult.items) ? scanResult.items : [];
    const projectItems = scanned.filter((item) => item && item.id
      && itemTypes.includes(item.itemType)
      && String(item.projectId || context.projectId) === String(context.projectId));
    const people = collectPeople(projectItems, user);
    const items = projectItems.slice(0, MAX_ITEMS).map((item) => {
      const due = Mapper.earliestDue(item);
      return {
        itemType: item.itemType,
        itemId: text(item.id, 120),
        number: text(item.number, 120),
        title: text(item.title, 400),
        status: text(item.status, 120),
        statusRank: Number.isFinite(item.statusRank) ? item.statusRank : null,
        closed: isClosed(item),
        responsible: text(item.responsible, 400),
        holders: holdersFor(item, people),
        attachmentCount: Number(item.attachmentCount) || 0,
        createdAt: isoDate(item.dates && item.dates.created),
        modifiedAt: isoDate(item.dates && item.dates.modified),
        dueAt: due.dueAt,
        sourceUrl: Mapper.itemUrl(Object.assign({}, item, { projectId: context.projectId }))
      };
    });
    return {
      schemaVersion: SCHEMA_VERSION,
      provider: "helix",
      accountId: null,
      projectId: text(context.projectId, 120),
      projectName: text(context.projectName || context.projectId, 300),
      projectUrl: `https://project.helixteam.com/Projects/${encodeURIComponent(context.projectId)}/Overview`,
      itemTypes,
      complete: itemTypes.length > 0 && projectItems.length <= MAX_ITEMS,
      scannedAt: isoDate(scanResult.scannedAt) || new Date().toISOString(),
      user: {
        fullName: text(user.fullName, 160),
        companyId: user.companyId ? text(user.companyId, 80) : null,
        company: user.company ? text(user.company, 160) : null
      },
      items
    };
  }

  // Procore's standalone mode only sees the rows rendered in the current grid, so these
  // snapshots are always partial: the helper must never treat a missing row as removed.
  function buildProcoreSnapshots(visibleQueue) {
    const source = visibleQueue && Array.isArray(visibleQueue.items) ? visibleQueue.items : [];
    const byProject = new Map();
    for (const row of source.slice(0, MAX_ITEMS)) {
      if (!row || !row.project_id || !row.item_id) continue;
      const itemType = row.item_type === "submittal" ? "submittal" : "rfi";
      const projectId = text(row.project_id, 120);
      if (!byProject.has(projectId)) {
        byProject.set(projectId, {
          schemaVersion: SCHEMA_VERSION,
          provider: "procore",
          accountId: row.account_id ? text(row.account_id, 80) : null,
          projectId,
          projectName: text(row.project_name || `Procore Project ${projectId}`, 300),
          projectUrl: null,
          itemTypes: [],
          complete: false,
          scannedAt: isoDate(visibleQueue.scanned_at) || new Date().toISOString(),
          user: { fullName: "", companyId: null, company: null },
          items: []
        });
      }
      const snapshot = byProject.get(projectId);
      if (!snapshot.itemTypes.includes(itemType)) snapshot.itemTypes.push(itemType);
      const status = text(row.status, 120);
      snapshot.items.push({
        itemType,
        itemId: text(row.item_id, 120),
        number: text(row.number, 120),
        title: text(row.title, 400),
        status,
        statusRank: null,
        closed: /^(closed|void(ed)?)$/i.test(status),
        responsible: "",
        holders: [],
        attachmentCount: 0,
        createdAt: isoDate(row.source_created_at),
        modifiedAt: isoDate(row.source_modified_at),
        dueAt: isoDate(row.due_at),
        sourceUrl: row.source_url ? text(row.source_url, 1000) : null
      });
    }
    return Array.from(byProject.values());
  }

  return Object.freeze({
    HOST_NAME,
    SCHEMA_VERSION,
    MAX_ITEMS,
    buildHelixSnapshot,
    buildProcoreSnapshots,
    holdersFor,
    isClosed
  });
});
