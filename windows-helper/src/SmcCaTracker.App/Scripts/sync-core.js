(function attachCaTrackerSync(root, factory) {
  const api = factory(root.HelixBridgeCore, root.CaTrackerSnapshot);
  root.CaTrackerSync = api;
  if (typeof module === "object" && module.exports) {
    module.exports = factory(require("./bridge-core.js"), require("./snapshot.js"));
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function createCaTrackerSync(Bridge, Snapshot) {
  "use strict";

  // Background sync for CA Tracker. The app keeps one hidden Helix page open and asks it to read
  // each tracked project in turn. The first read of a project reads every item; later reads ask
  // Helix only for items changed since the newest change seen (with a few minutes' overlap), and
  // merge them into what this page already holds. A full read runs again after fullAfterMs, so items
  // deleted in Helix drop out. Read-only: nothing in Helix is changed.

  const OVERLAP_MS = 5 * 60 * 1000;
  const DEFAULT_FULL_AFTER_MS = 12 * 60 * 60 * 1000;
  const cache = new Map();

  function newer(left, right) {
    if (!left) return right || null;
    if (!right) return left;
    return right > left ? right : left;
  }

  async function syncProject(windowObject, request) {
    if (!Bridge || !Snapshot) {
      throw new Error("The Helix sync helpers are unavailable.");
    }
    const projectId = String(request && request.projectId || "");
    if (!projectId) {
      throw new Error("A Helix project is required.");
    }
    const context = Bridge.syncContext(windowObject, projectId, request.projectName);
    const now = typeof request.now === "number" ? request.now : Date.now();
    const fullAfterMs = Number(request.fullAfterMs) > 0 ? Number(request.fullAfterMs) : DEFAULT_FULL_AFTER_MS;
    const previous = cache.get(projectId) || null;
    let mode = request.full || !previous || !previous.latest || now - previous.fullAt > fullAfterMs ? "full" : "changes";

    let rows = null;
    let present = null;
    if (mode === "changes") {
      const since = new Date(Date.parse(previous.latest) - OVERLAP_MS).toISOString();
      try {
        rows = await Bridge.readProjectItems(windowObject, { projectId, projectName: context.projectName, since });
      } catch (_error) {
        // Helix didn't accept the changed-since filter: read everything instead.
        mode = "full";
      }
    }
    if (mode === "changes") {
      // Items whose court history couldn't be read last time: read them again.
      const retry = Array.from(previous.retryCourt || []).map((key) => key.slice(key.indexOf(":") + 1));
      if (retry.length > 0) {
        try {
          const again = await Bridge.readProjectItems(windowObject, { projectId, projectName: context.projectName, ids: retry });
          const seen = new Set(rows.map((row) => `${row.itemType}:${row.id}`));
          rows = rows.concat(again.filter((row) => !seen.has(`${row.itemType}:${row.id}`)));
        } catch (_error) {
          // Tried again next time.
        }
      }
      // Which items still exist in this project, to drop deleted or moved ones.
      try {
        present = await Bridge.readProjectItemIds(windowObject, projectId);
      } catch (_error) {
        present = null;
      }
    }
    if (mode === "full") {
      rows = await Bridge.readProjectItems(windowObject, { projectId, projectName: context.projectName });
    }

    const entry = mode === "full"
      ? { items: new Map(), latest: null, fullAt: now, retryCourt: new Set() }
      : { items: new Map(previous.items), latest: previous.latest, fullAt: previous.fullAt, retryCourt: new Set(previous.retryCourt || []) };
    let changedCount = 0;
    for (const row of rows) {
      const key = `${row.itemType}:${row.id}`;
      const signature = JSON.stringify(row.item);
      const before = previous ? previous.items.get(key) : null;
      if (!before || before.signature !== signature) {
        changedCount += 1;
      }
      entry.items.set(key, { item: row.item, signature });
      entry.latest = newer(entry.latest, row.modifiedAt);
      if (row.courtUnknown) entry.retryCourt.add(key);
      else entry.retryCourt.delete(key);
    }
    if (present) {
      const still = { rfi: new Set(present.rfi || []), submittal: new Set(present.submittal || []) };
      for (const key of Array.from(entry.items.keys())) {
        const itemType = key.slice(0, key.indexOf(":"));
        const id = key.slice(key.indexOf(":") + 1);
        if (!still[itemType] || !still[itemType].has(id)) {
          entry.items.delete(key);
          entry.retryCourt.delete(key);
          changedCount += 1;
        }
      }
    }
    if (mode === "full" && previous) {
      // Items that are gone count as changes too.
      for (const key of previous.items.keys()) {
        if (!entry.items.has(key)) changedCount += 1;
      }
    }
    cache.set(projectId, entry);

    const items = Array.from(entry.items.values()).map((value) => value.item);
    const snapshot = Snapshot.buildHelixSnapshot({
      context,
      items,
      warnings: [],
      scannedAt: new Date(now).toISOString()
    });
    return {
      snapshot,
      mode,
      itemCount: items.length,
      changedCount,
      user: { fullName: context.user.fullName, company: context.user.company || null }
    };
  }

  // Whether this page can sync yet: the signed-in user and Helix's data service are loaded.
  function readiness(windowObject) {
    const doc = windowObject && windowObject.document;
    if (doc && doc.querySelector && doc.querySelector('input[type="password"]')) {
      return { state: "signedOut" };
    }
    const user = Bridge.currentUser(windowObject);
    if (!user) {
      return { state: "loading" };
    }
    try {
      Bridge.syncContext(windowObject, "check", "check");
      // resolveMaster is reached through listAssignedProjects/readProjectItems; check it directly here.
      if (!(windowObject.Master && typeof windowObject.Master.readFromCollection === "function")
        && !(windowObject.featureScriptHandler && typeof windowObject.featureScriptHandler.getCurrentMasterObject === "function")) {
        return { state: "loading" };
      }
    } catch (_error) {
      return { state: "loading" };
    }
    return { state: "ready", user: { fullName: user.fullName, company: user.company || null } };
  }

  // An anonymized copy of a project's items as this page holds them, for building test cases from real
  // projects: people become "Person 1", "Person 2"…, companies "Company A"…, titles "Item <number>",
  // and ids are renumbered. The same person keeps the same label everywhere (including in "waiting on"
  // text), so whose court an item is in can still be worked out — and checked.
  function exportSample(windowObject, projectId) {
    const entry = cache.get(String(projectId));
    if (!entry) throw new Error("Sync this project first, then export the sample.");
    const context = Bridge.syncContext(windowObject, projectId, "Sample project");
    const people = new Map();
    const companies = new Map();
    const ids = new Map();
    const personLabel = (name) => {
      const key = String(name || "").trim().toLowerCase();
      if (!key) return name;
      if (!people.has(key)) people.set(key, `Person ${people.size + 1}`);
      return people.get(key);
    };
    const companyLabel = (name) => {
      const key = String(name || "").trim().toLowerCase();
      if (!key) return name;
      if (!companies.has(key)) companies.set(key, `Company ${String.fromCharCode(65 + (companies.size % 26))}${companies.size >= 26 ? companies.size : ""}`);
      return companies.get(key);
    };
    const idLabel = (prefix, value) => {
      if (!value) return value;
      const key = `${prefix}:${value}`;
      if (!ids.has(key)) ids.set(key, `${prefix}-${ids.size + 1}`);
      return ids.get(key);
    };
    const identity = (value) => value && typeof value === "object"
      ? Object.assign({}, value, {
          id: idLabel("directory", value.id),
          contactId: idLabel("contact", value.contactId),
          fullName: personLabel(value.fullName),
          companyId: idLabel("company", value.companyId),
          company: companyLabel(value.company)
        })
      : value;
    // Learn every name first so "waiting on" text can be rewritten with the same labels.
    const items = Array.from(entry.items.values()).map((value) => value.item);
    const user = identity(context.user);
    for (const item of items) {
      [item.creator, item.responseEditor, item.acceptedBy, item.rejectedBy].forEach((who) => who && personLabel(who.fullName));
      const workflow = item.workflow || {};
      [].concat(workflow.required || [], workflow.optional || []).forEach((step) => step && step.assigned && personLabel(step.assigned.fullName));
    }
    const rewriteText = (text) => {
      let result = String(text || "");
      for (const [key, label] of people.entries()) {
        if (key.length < 3) continue;
        result = result.replace(new RegExp(key.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi"), label);
      }
      for (const [key, label] of companies.entries()) {
        if (key.length < 3) continue;
        result = result.replace(new RegExp(key.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi"), label);
      }
      return result;
    };
    const step = (value) => value && Object.assign({}, value, {
      assigned: identity(value.assigned),
      reviewed: value.reviewed && typeof value.reviewed === "object" ? identity(value.reviewed) : value.reviewed
    });
    const anonymized = items.map((item) => Object.assign({}, item, {
      id: idLabel("item", item.id),
      projectId: "project-1",
      projectName: "Sample project",
      title: `Item ${item.number}`,
      contract: item.contract ? companyLabel(item.contract) : item.contract,
      responsible: rewriteText(item.responsible),
      creator: identity(item.creator),
      responseEditor: identity(item.responseEditor),
      acceptedBy: identity(item.acceptedBy),
      rejectedBy: identity(item.rejectedBy),
      lastEditorName: item.lastEditorName ? personLabel(item.lastEditorName) : item.lastEditorName,
      registers: [],
      searchTerms: [],
      workflow: item.workflow && {
        required: (item.workflow.required || []).map(step),
        optional: (item.workflow.optional || []).map(step),
        notify: (item.workflow.notify || []).map(identity)
      }
    }));
    const sampleContext = Object.assign({}, context, { projectId: "project-1", projectName: "Sample project", user });
    const snapshot = Snapshot.buildHelixSnapshot({ context: sampleContext, items: anonymized, warnings: [], scannedAt: new Date().toISOString() });
    return {
      kind: "ca-tracker-helix-sample",
      version: 1,
      exportedAt: new Date().toISOString(),
      user,
      items: anonymized,
      // What CA Tracker currently works out; correct any that are wrong to make a test case.
      expected: snapshot.items.map((item) => ({ itemId: item.itemId, number: item.number, status: item.status, holders: item.holders }))
    };
  }

  function forget(projectId) {
    if (projectId) cache.delete(String(projectId));
    else cache.clear();
  }

  return Object.freeze({
    syncProject,
    readiness,
    exportSample,
    forget,
    _test: Object.freeze({ cache, OVERLAP_MS })
  });
});
