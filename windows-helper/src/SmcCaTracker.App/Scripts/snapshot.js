(function attachCaTrackerSnapshot(root, factory) {
  const api = factory(root.HelixActivityCore);
  root.CaTrackerSnapshot = api;
  if (typeof module === "object" && module.exports) {
    module.exports = factory(require("./activity-core.js"));
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function createCaTrackerSnapshot(Core) {
  "use strict";

  // Snapshots describe what a provider currently shows for one project so the CA Tracker app
  // can compare it with that project's CA folder. The app's built-in browser runs this inside
  // the Helix or Procore page. Snapshots contain item identity, status, and who currently holds
  // each item — never files, comments, or credentials.
  const SCHEMA_VERSION = 1;
  const MAX_ITEMS = 5000;
  const ITEM_TYPES = ["rfi", "submittal"];

  function validDate(value) {
    if (!value) return null;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }

  // The earliest of the due, requested-review, and contractual-review dates.
  function earliestDue(item) {
    const dates = item && item.dates || {};
    const candidates = [
      ["due", validDate(dates.due)],
      ["requested_review", validDate(dates.requestedReview)],
      ["contractual_review", validDate(dates.contractualReview)]
    ].filter((entry) => entry[1]);
    candidates.sort((left, right) => left[1].localeCompare(right[1]));
    if (candidates.length) return { dueAt: candidates[0][1], source: candidates[0][0] };
    return { dueAt: null, source: null };
  }

  // The project's Helix RFI or Submittal list, where the item can be found by its number.
  function itemUrl(item) {
    const route = item.itemType === "submittal" ? "Submittals" : "RFI";
    return `https://project.helixteam.com/Projects/${encodeURIComponent(item.projectId)}/${route}`;
  }

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

  // The same person can turn up with a directory id, a contact id or only a name (a creator without a
  // contact, a reviewer with one). Ids decide when both have one; the name only when neither id can.
  function samePerson(a, b) {
    if (a.id && b.id && String(a.id) === String(b.id)) return true;
    if (a.contactId && b.contactId) return String(a.contactId) === String(b.contactId);
    if (a.id && b.id) return false;
    return text(a.fullName).toLocaleLowerCase() === text(b.fullName).toLocaleLowerCase();
  }

  function collectPeople(items, user) {
    const people = [];
    function remember(identity) {
      if (!identityKey(identity) || !identity.fullName) return;
      const known = people.find((person) => samePerson(person, identity));
      if (!known) {
        people.push(Object.assign({}, identity));
        return;
      }
      // Keep what this sighting adds (a contact id the first one lacked, say).
      for (const field of ["id", "contactId", "companyId", "company"]) {
        if (!known[field] && identity[field]) known[field] = identity[field];
      }
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
    return people;
  }

  // "Waiting on Chris Contractor" (or "Waiting on A, B and C"): the names it gives.
  function waitingOnNames(value) {
    const match = text(value, 400).match(/waiting\s+on\s*:?\s*(.+)$/i);
    if (!match) return [];
    return match[1].replace(/[.!]+\s*$/, "").split(/\s*(?:,|;|\band\b|&)\s*/i)
      .map((name) => text(name, 160)).filter(Boolean).slice(0, 5);
  }

  // Everyone who currently holds (or will next hold) the item, using the same In court /
  // Coming up rules as the Open queue. The app filters these by company.
  function holdersFor(item, people) {
    if (isClosed(item)) return [];
    const holders = [];
    const seen = new Set();
    for (const person of people) {
      const state = Core.classifyOpenQueue(item, person);
      if (!state) continue;
      const holder = {
        name: text(person.fullName, 160),
        companyId: person.companyId ? text(person.companyId, 80) : null,
        company: person.company ? text(person.company, 160) : null,
        state: state.code === "current" ? "current" : "upcoming",
        since: null
      };
      const key = `${holder.name.toLocaleLowerCase()}|${holder.companyId || ""}`;
      if (seen.has(key)) continue;
      seen.add(key);
      if (holder.state === "current") holder.since = courtSince(item, person, state);
      holders.push(holder);
    }
    // Waiting on someone none of the known people matched: they hold it (by name only).
    if (!holders.some((holder) => holder.state === "current")) {
      for (const name of waitingOnNames(item && item.responsible)) {
        const key = `${name.toLocaleLowerCase()}|`;
        if (seen.has(key)) continue;
        seen.add(key);
        holders.push({ name, companyId: null, company: null, state: "current", since: null });
      }
    }
    return holders;
  }

  // When the item reached the person who has it now. Preferably worked out from Helix's saved
  // versions during the scan (item.courtSince); otherwise from the review workflow or review date.
  // Null when neither says (rather than guessing the scan time).
  function courtSince(item, person, state) {
    if (item && item.courtSince) return isoDate(item.courtSince);
    if (typeof Core.courtDate !== "function") return null;
    const entered = Core.courtDate(item, { state, personKey: Core.personKey(person) }, null);
    return entered && entered.source !== "observed" ? isoDate(entered.value) : null;
  }

  // Procore's "Ball In Court" column lists the people holding an item, separated by commas.
  function procoreHolders(value) {
    const seen = new Set();
    return String(value || "").split(/[,;]\s*/).map((name) => text(name, 160)).filter((name) => {
      const key = name.toLocaleLowerCase();
      if (!name || seen.has(key)) return false;
      seen.add(key);
      return true;
    }).slice(0, 20).map((name) => ({ name, companyId: null, company: null, state: "current", since: null }));
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
    if (!Core) throw new Error("Helix activity helpers are unavailable.");
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
      const due = earliestDue(item);
      return {
        itemType: item.itemType,
        itemId: text(item.id, 120),
        number: text(item.number, 120),
        title: text(item.title, 400),
        status: text(item.status, 120),
        statusRank: Number.isFinite(item.statusRank) ? item.statusRank : null,
        closed: isClosed(item),
        responsible: text(item.responsible, 400),
        contract: text(item.contract, 300),
        holders: holdersFor(item, people),
        attachmentCount: Number(item.attachmentCount) || 0,
        createdAt: isoDate(item.dates && item.dates.created),
        modifiedAt: isoDate(item.dates && item.dates.modified),
        dueAt: due.dueAt,
        sourceUrl: itemUrl(Object.assign({}, item, { projectId: context.projectId }))
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

  // Procore reads only see the rows rendered in the current grid, so these
  // snapshots are always partial: the app must never treat a missing row as removed.
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
        responsible: text(row.ball_in_court, 400),
        contract: "",
        holders: procoreHolders(row.ball_in_court),
        attachmentCount: 0,
        createdAt: isoDate(row.source_created_at),
        modifiedAt: isoDate(row.source_modified_at),
        dueAt: isoDate(row.due_at),
        sourceUrl: row.source_url ? text(row.source_url, 1000) : null
      });
    }
    return Array.from(byProject.values());
  }

  // One Procore page during an app-requested refresh: always returns a snapshot for the requested
  // project and item type, even when the grid shows no rows, so the app can record the refresh.
  function buildProcoreProjectSnapshot(visibleQueue, request) {
    if (!request || !request.projectId) throw new Error("A Procore refresh request is required.");
    const itemType = Array.isArray(request.itemTypes) && request.itemTypes[0] === "submittal" ? "submittal" : "rfi";
    const projectId = text(request.projectId, 120);
    const matching = buildProcoreSnapshots(visibleQueue).find((snapshot) => snapshot.projectId === projectId);
    const items = matching ? matching.items.filter((item) => item.itemType === itemType) : [];
    return {
      schemaVersion: SCHEMA_VERSION,
      provider: "procore",
      accountId: request.accountId ? text(request.accountId, 80) : (matching && matching.accountId) || null,
      projectId,
      projectName: text(request.projectName || (matching && matching.projectName) || `Procore Project ${projectId}`, 300),
      projectUrl: null,
      itemTypes: [itemType],
      complete: false,
      scannedAt: (matching && matching.scannedAt) || new Date().toISOString(),
      user: { fullName: "", companyId: null, company: null },
      items
    };
  }

  // Projects the user can open, offered by the app when connecting a project.
  function buildHelixCatalog(scanResult) {
    const projects = scanResult && Array.isArray(scanResult.projects) ? scanResult.projects : [];
    return {
      provider: "helix",
      projects: projects.slice(0, MAX_ITEMS).filter((project) => project && project.id && project.name).map((project) => ({
        projectId: text(project.id, 120),
        accountId: null,
        accountName: null,
        name: text(project.name, 300),
        host: "project.helixteam.com"
      }))
    };
  }

  function buildProcoreCatalog(visibleQueue, discoveryCatalog, host) {
    const projects = new Map();
    const safeHost = /^(?:app|[a-z]{2}\d{2})\.procore\.com$/i.test(String(host || "")) ? String(host).toLowerCase() : null;
    const companies = discoveryCatalog && Array.isArray(discoveryCatalog.companies) ? discoveryCatalog.companies : [];
    for (const company of companies) {
      for (const project of Array.isArray(company && company.projects) ? company.projects : []) {
        if (!project || !project.id || !project.name) continue;
        projects.set(`${company.id}|${project.id}`, {
          projectId: text(project.id, 120),
          accountId: text(company.id, 80),
          accountName: text(company.name, 240),
          name: text(project.name, 300),
          host: safeHost
        });
      }
    }
    for (const row of visibleQueue && Array.isArray(visibleQueue.items) ? visibleQueue.items : []) {
      if (!row || !row.project_id || !row.account_id) continue;
      const key = `${row.account_id}|${row.project_id}`;
      if (projects.has(key)) continue;
      projects.set(key, {
        projectId: text(row.project_id, 120),
        accountId: text(row.account_id, 80),
        accountName: null,
        name: text(row.project_name || `Procore Project ${row.project_id}`, 300),
        host: safeHost
      });
    }
    return { provider: "procore", projects: Array.from(projects.values()).slice(0, MAX_ITEMS) };
  }

  return Object.freeze({
    buildProcoreProjectSnapshot,
    buildHelixCatalog,
    buildProcoreCatalog,
    SCHEMA_VERSION,
    MAX_ITEMS,
    buildHelixSnapshot,
    buildProcoreSnapshots,
    holdersFor,
    isClosed
  });
});
