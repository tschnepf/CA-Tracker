(function attachHelixActivityCore(root, factory) {
  const api = factory();
  root.HelixActivityCore = api;
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function createHelixActivityCore() {
  "use strict";

  const REASON_DEFINITIONS = Object.freeze([
    { code: "created", label: "Created" },
    { code: "responded", label: "Responded" },
    { code: "commented", label: "Commented" },
    { code: "uploaded", label: "Uploaded file" },
    { code: "edited", label: "Edited" },
    { code: "assigned", label: "Assigned" },
    { code: "reviewed", label: "Reviewed" },
    { code: "accepted", label: "Advanced/accepted" },
    { code: "rejected", label: "Rejected" },
    { code: "notified", label: "Notify on completion" }
  ]);
  const REASON_ORDER = new Map(REASON_DEFINITIONS.map((item, index) => [item.code, index]));
  const REASON_LABELS = new Map(REASON_DEFINITIONS.map((item) => [item.code, item.label]));

  function normalizeName(value) {
    return String(value || "").trim().toLocaleLowerCase();
  }

  function identityMatches(identity, user) {
    if (!identity || !user) {
      return false;
    }
    if (identity.contactId && user.contactId) {
      return String(identity.contactId) === String(user.contactId);
    }
    if (identity.id && user.id) {
      return String(identity.id) === String(user.id);
    }
    return Boolean(identity.fullName && user.fullName
      && normalizeName(identity.fullName) === normalizeName(user.fullName));
  }

  function editorMatches(name, user) {
    return Boolean(name && user && user.fullName && normalizeName(name) === normalizeName(user.fullName));
  }

  function workflowEntryReviewed(entry) {
    return Boolean(entry && (entry.reviewedAt || entry.reviewed === true
      || (entry.reviewed && typeof entry.reviewed === "object")));
  }

  function responsibleTextMatches(value, user) {
    const text = normalizeName(value);
    if (!text || !user) {
      return false;
    }
    return Boolean(user.fullName && text.includes(normalizeName(user.fullName)));
  }

  function personKey(identity) {
    if (!identity || typeof identity !== "object") return null;
    if (identity.contactId) return `contact:${String(identity.contactId)}`;
    if (identity.fullName) return `name:${normalizeName(identity.fullName)}`;
    if (identity.id) return `directory:${String(identity.id)}`;
    return null;
  }

  function personSummary(identity, isCurrent) {
    const key = personKey(identity);
    if (!key) return null;
    return {
      key,
      id: identity.id ? String(identity.id) : null,
      contactId: identity.contactId ? String(identity.contactId) : null,
      fullName: identity.fullName || "Unknown person",
      companyId: identity.companyId ? String(identity.companyId) : null,
      company: identity.company || null,
      isCurrent: isCurrent === true
    };
  }

  function companyPersonKeys(people, currentPersonKey) {
    const values = Array.isArray(people) ? people : [];
    const current = values.find((person) => person && String(person.key) === String(currentPersonKey));
    if (!current) return [];
    const companyId = current.companyId ? String(current.companyId) : "";
    const companyName = normalizeName(current.company);
    if (!companyId && !companyName) return [];
    return values.filter((person) => {
      if (!person || !person.key) return false;
      if (companyId) return Boolean(person.companyId && String(person.companyId) === companyId);
      return normalizeName(person.company) === companyName;
    }).map((person) => String(person.key));
  }

  function normalizeDate(value) {
    if (!value) {
      return null;
    }
    const date = value instanceof Date ? value : new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }

  function latestDate(values) {
    let latest = null;
    for (const value of values) {
      const iso = normalizeDate(value);
      if (iso && (!latest || iso > latest)) {
        latest = iso;
      }
    }
    return latest;
  }

  function groupByItem(records) {
    const grouped = new Map();
    for (const record of Array.isArray(records) ? records : []) {
      const itemId = record && (record.itemId || record.rfiId);
      if (!itemId) {
        continue;
      }
      const key = String(itemId);
      if (!grouped.has(key)) {
        grouped.set(key, []);
      }
      grouped.get(key).push(record);
    }
    return grouped;
  }

  function event(kind, timestamp, detail, sourceId) {
    return {
      kind,
      timestamp: normalizeDate(timestamp),
      detail: detail || null,
      sourceId: sourceId ? String(sourceId) : null
    };
  }

  function addWorkflowEvents(events, rfi, user) {
    const workflow = rfi.workflow || {};
    const groups = [
      ...(Array.isArray(workflow.required) ? workflow.required : []),
      ...(Array.isArray(workflow.optional) ? workflow.optional : [])
    ];

    groups.forEach((entry, index) => {
      if (identityMatches(entry && entry.assigned, user)) {
        events.push(event("assigned", rfi.dates && rfi.dates.modified, "Workflow assignment", `workflow-${index}`));
        const reviewed = entry.reviewed;
        if (reviewed === true || identityMatches(reviewed, user)) {
          events.push(event("reviewed", rfi.dates && rfi.dates.modified, "Workflow review", `review-${index}`));
        }
      } else if (identityMatches(entry && entry.reviewed, user)) {
        events.push(event("reviewed", rfi.dates && rfi.dates.modified, "Workflow review", `review-${index}`));
      }
    });

    const notify = Array.isArray(workflow.notify) ? workflow.notify : [];
    notify.forEach((identity, index) => {
      if (identityMatches(identity, user)) {
        events.push(event("notified", rfi.dates && rfi.dates.modified, "Completion notification", `notify-${index}`));
      }
    });
  }

  function deduplicateEvents(events) {
    const seen = new Set();
    return events.filter((item) => {
      const key = [item.kind, item.sourceId || "", item.timestamp || "", item.detail || ""].join("|");
      if (seen.has(key)) {
        return false;
      }
      seen.add(key);
      return true;
    });
  }

  function aggregateReasons(events) {
    const groups = new Map();
    for (const item of events) {
      if (!groups.has(item.kind)) {
        groups.set(item.kind, []);
      }
      groups.get(item.kind).push(item);
    }
    return Array.from(groups.entries())
      .map(([code, items]) => ({
        code,
        label: REASON_LABELS.get(code) || code,
        count: items.length,
        lastAt: latestDate(items.map((item) => item.timestamp))
      }))
      .sort((a, b) => (REASON_ORDER.get(a.code) ?? 99) - (REASON_ORDER.get(b.code) ?? 99));
  }

  function queueDueAt(item) {
    const dates = item && item.dates ? item.dates : {};
    const dueDates = queueDueDates(item).sort((a, b) => a.getTime() - b.getTime());
    return normalizeDate(dueDates[0] || dates.review || dates.modified || dates.created);
  }

  function queueDueDates(item) {
    const dates = item && item.dates ? item.dates : {};
    const seen = new Set();
    return [dates.due, dates.requestedReview, dates.contractualReview]
      .map((value) => {
        if (!value) return null;
        const date = value instanceof Date ? new Date(value.getTime()) : new Date(value);
        return Number.isNaN(date.getTime()) ? null : date;
      })
      .filter((date) => {
        if (!date) return false;
        const key = date.getTime();
        if (seen.has(key)) return false;
        seen.add(key);
        return true;
      });
  }

  function matchesDueFilter(item, filter, nowValue) {
    const value = filter && typeof filter === "object" && !Array.isArray(filter)
      ? filter
      : { modes: filter && filter !== "all" ? [filter] : [] };
    const modes = new Set(Array.isArray(value.modes) ? value.modes.map(String) : []);
    if (modes.has("today-or-overdue")) {
      modes.add("today");
      modes.add("overdue");
    }
    if (modes.size === 0) return true;
    const now = nowValue ? new Date(nowValue) : new Date();
    if (Number.isNaN(now.getTime())) return false;
    const todayStart = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const tomorrowStart = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
    const requestedDays = Number(value.days);
    const futureDays = Number.isFinite(requestedDays) ? Math.max(1, Math.min(365, Math.round(requestedDays))) : 7;
    const futureEnd = new Date(now.getFullYear(), now.getMonth(), now.getDate() + futureDays + 1);
    const dates = queueDueDates(item);
    const dueToday = dates.some((date) => date >= todayStart && date < tomorrowStart);
    const overdue = dates.some((date) => date < todayStart);
    const dueNext = dates.some((date) => date >= tomorrowStart && date < futureEnd);
    return (modes.has("today") && dueToday)
      || (modes.has("overdue") && overdue)
      || (modes.has("next") && dueNext);
  }

  function classifyOpenQueue(item, user) {
    if (!item || !user) {
      return null;
    }
    const status = normalizeName(item.status);
    const rank = Number.isFinite(item.statusRank) ? item.statusRank : null;
    if ((rank !== null && rank >= 1300) || status === "complete" || status === "voided") {
      return null;
    }

    if (responsibleTextMatches(item.responsible, user)) {
      return {
        code: "current",
        label: "In court",
        detail: item.responsible,
        dueAt: queueDueAt(item)
      };
    }

    const required = item.workflow && Array.isArray(item.workflow.required)
      ? item.workflow.required
      : [];
    const firstUnreviewedIndex = required.findIndex((entry) => !workflowEntryReviewed(entry));
    if (firstUnreviewedIndex < 0) {
      return null;
    }
    const matchingIndex = required.findIndex((entry, index) => index >= firstUnreviewedIndex
      && !workflowEntryReviewed(entry)
      && identityMatches(entry && entry.assigned, user));
    if (matchingIndex < 0) {
      return null;
    }

    const inReview = rank === 800 || status === "review";
    const beforeReview = (rank !== null && rank < 800) || status === "open" || status === "pending";
    if (inReview && matchingIndex === firstUnreviewedIndex) {
      return {
        code: "current",
        label: "In court",
        detail: "Required review is ready",
        position: matchingIndex + 1,
        dueAt: queueDueAt(item)
      };
    }
    if (inReview || beforeReview) {
      const ahead = Math.max(0, matchingIndex - firstUnreviewedIndex);
      return {
        code: "upcoming",
        label: "Coming up",
        detail: inReview && ahead > 0
          ? `${ahead} required review${ahead === 1 ? "" : "s"} ahead`
          : "Assigned in required review workflow",
        position: matchingIndex + 1,
        dueAt: queueDueAt(item)
      };
    }
    return null;
  }

  function buildActivityIndex(scanResult) {
    if (!scanResult || !scanResult.context || !scanResult.context.user) {
      throw new Error("A valid Helix scan result is required.");
    }
    const user = scanResult.context.user;
    const commentsByItem = groupByItem(scanResult.comments);
    const attachmentsByItem = groupByItem(scanResult.attachments);
    const historiesByItem = groupByItem(scanResult.histories);
    const results = [];
    const openQueue = [];
    const peopleByKey = new Map();

    function rememberPerson(identity, isCurrent) {
      const person = personSummary(identity, isCurrent);
      if (!person) return null;
      const existing = peopleByKey.get(person.key);
      peopleByKey.set(person.key, existing
        ? Object.assign({}, existing, person, { isCurrent: existing.isCurrent || person.isCurrent })
        : person);
      return peopleByKey.get(person.key);
    }

    const currentPerson = rememberPerson(user, true);

    const scannedItems = Array.isArray(scanResult.items) ? scanResult.items : scanResult.rfis;
    for (const item of Array.isArray(scannedItems) ? scannedItems : []) {
      if (!item || !item.id) {
        continue;
      }
      const events = [];
      const fallbackDate = item.dates && (item.dates.modified || item.dates.created);
      const activityItemLabel = item.itemType === "submittal" ? "Submittal" : "RFI";

      if (identityMatches(item.creator, user)) {
        events.push(event("created", item.dates && item.dates.created, `${activityItemLabel} created`, item.id));
      }
      if (identityMatches(item.responseEditor, user)) {
        events.push(event("responded", fallbackDate, "Current response", `response-${item.id}`));
      }
      if (editorMatches(item.lastEditorName, user)) {
        events.push(event("edited", fallbackDate, `Current ${activityItemLabel} revision`, `current-${item.id}`));
      }
      if (identityMatches(item.acceptedBy, user)) {
        events.push(event("accepted", fallbackDate, "Status advanced or accepted", `accepted-${item.id}`));
      }
      if (identityMatches(item.rejectedBy, user)) {
        events.push(event("rejected", fallbackDate, "Status rejected", `rejected-${item.id}`));
      }

      addWorkflowEvents(events, item, user);

      for (const comment of commentsByItem.get(String(item.id)) || []) {
        if (identityMatches(comment.author, user)) {
          events.push(event("commented", comment.modifiedAt || comment.createdAt, comment.text, comment.id));
        }
      }
      for (const attachment of attachmentsByItem.get(String(item.id)) || []) {
        if (identityMatches(attachment.uploader, user)
          || (!attachment.uploader?.id && editorMatches(attachment.editorName, user))) {
          events.push(event("uploaded", attachment.createdAt || attachment.modifiedAt, attachment.filename, attachment.id));
        }
      }
      for (const history of historiesByItem.get(String(item.id)) || []) {
        const historyDate = history.modifiedAt || history.createdAt;
        if (editorMatches(history.editorName, user)) {
          events.push(event("edited", historyDate, history.status ? `Edited while ${history.status}` : "Historical edit", history.id));
        }
        if (identityMatches(history.responseEditor, user)) {
          events.push(event("responded", historyDate, "Historical response", `response-${history.id}`));
        }
        if (identityMatches(history.acceptedBy, user)) {
          events.push(event("accepted", historyDate, "Historical status advance", `accepted-${history.id}`));
        }
        if (identityMatches(history.rejectedBy, user)) {
          events.push(event("rejected", historyDate, "Historical rejection", `rejected-${history.id}`));
        }
      }

      const uniqueEvents = deduplicateEvents(events);
      const reasons = aggregateReasons(uniqueEvents);
      const latestActivityAt = latestDate(uniqueEvents.map((item) => item.timestamp)) || fallbackDate || null;
      const searchParts = [
        item.number,
        item.title,
        item.status,
        item.contract,
        item.projectName,
        item.responsible,
        ...(Array.isArray(item.searchTerms) ? item.searchTerms : []),
        ...reasons.map((reason) => reason.label),
        ...uniqueEvents.map((item) => item.detail)
      ];
      const enriched = Object.assign({}, item, {
        events: uniqueEvents.sort((a, b) => String(b.timestamp || "").localeCompare(String(a.timestamp || ""))),
        reasons,
        latestActivityAt,
        searchText: searchParts.filter(Boolean).join(" ").toLocaleLowerCase()
      });
      if (uniqueEvents.length > 0) {
        results.push(enriched);
      }
    }

    const queueSource = Array.isArray(scanResult.queueItems) ? scanResult.queueItems : scannedItems;
    for (const item of Array.isArray(queueSource) ? queueSource : []) {
      if (!item || !item.id) continue;
      const required = item.workflow && Array.isArray(item.workflow.required)
        ? item.workflow.required
        : [];
      const candidatePeople = new Map();
      required.forEach((entry) => {
        if (workflowEntryReviewed(entry) || !entry || !entry.assigned) return;
        const person = rememberPerson(entry.assigned, false);
        if (person) candidatePeople.set(person.key, person);
      });
      if (currentPerson && responsibleTextMatches(item.responsible, user)) {
        candidatePeople.set(currentPerson.key, currentPerson);
      }
      const queueAssignments = Array.from(candidatePeople.values()).map((person) => {
        const state = classifyOpenQueue(item, person);
        return state ? { personKey: person.key, person, state } : null;
      }).filter(Boolean);
      if (queueAssignments.length === 0) continue;
      const currentAssignment = currentPerson
        ? queueAssignments.find((assignment) => assignment.personKey === currentPerson.key)
        : null;
      const queueState = (currentAssignment || queueAssignments[0]).state;
      const typeLabel = item.itemType === "submittal" ? "Submittal" : "RFI";
      const searchText = [
        item.number,
        item.title,
        item.status,
        item.contract,
        item.projectName,
        item.responsible,
        typeLabel,
        ...(Array.isArray(item.searchTerms) ? item.searchTerms : []),
        queueState.label,
        queueState.detail,
        ...queueAssignments.map((assignment) => assignment.person.fullName)
      ].filter(Boolean).join(" ").toLocaleLowerCase();
      openQueue.push(Object.assign({}, item, {
        events: [],
        reasons: [],
        latestActivityAt: item.dates && (item.dates.modified || item.dates.created) || null,
        queueAssignments,
        queueState,
        searchText
      }));
    }

    const projects = Array.isArray(scanResult.projects) ? scanResult.projects : Array.from(
      new Map(openQueue.map((item) => [String(item.projectId), {
        id: String(item.projectId),
        name: item.projectName || String(item.projectId)
      }])).values()
    ).sort((a, b) => a.name.localeCompare(b.name));

    const people = Array.from(peopleByKey.values()).sort((a, b) => {
      if (a.isCurrent !== b.isCurrent) return a.isCurrent ? -1 : 1;
      return a.fullName.localeCompare(b.fullName);
    });

    return {
      context: scanResult.context,
      scannedAt: scanResult.scannedAt || new Date().toISOString(),
      warnings: Array.isArray(scanResult.warnings) ? scanResult.warnings : [],
      results: sortResults(results, "latest"),
      openQueue: sortOpenQueue(openQueue),
      projects,
      people,
      currentPersonKey: currentPerson && currentPerson.key
    };
  }

  function naturalParts(value) {
    return String(value || "").split(/(\d+)/).map((part) => /^\d+$/.test(part) ? Number(part) : part.toLocaleLowerCase());
  }

  function compareNatural(left, right) {
    const a = naturalParts(left);
    const b = naturalParts(right);
    const length = Math.max(a.length, b.length);
    for (let index = 0; index < length; index += 1) {
      if (a[index] === b[index]) {
        continue;
      }
      if (a[index] === undefined) return -1;
      if (b[index] === undefined) return 1;
      if (typeof a[index] === typeof b[index]) return a[index] < b[index] ? -1 : 1;
      return String(a[index]).localeCompare(String(b[index]));
    }
    return 0;
  }

  function sortResults(results, sort) {
    const copy = Array.from(results || []);
    if (sort === "number") {
      return copy.sort((a, b) => compareNatural(a.number, b.number));
    }
    if (sort === "title") {
      return copy.sort((a, b) => String(a.title || "").localeCompare(String(b.title || "")));
    }
    return copy.sort((a, b) => String(b.latestActivityAt || "").localeCompare(String(a.latestActivityAt || ""))
      || compareNatural(a.number, b.number));
  }

  function sortOpenQueue(results, sort) {
    if (sort === "number" || sort === "title") {
      return sortResults(results, sort);
    }
    if (sort === "project") {
      return Array.from(results || []).sort((a, b) => String(a.projectName || "").localeCompare(String(b.projectName || ""))
        || String(a.itemType || "").localeCompare(String(b.itemType || ""))
        || compareNatural(a.number, b.number));
    }
    if (sort === "type") {
      return Array.from(results || []).sort((a, b) => String(a.itemType || "").localeCompare(String(b.itemType || ""))
        || String(a.projectName || "").localeCompare(String(b.projectName || ""))
        || compareNatural(a.number, b.number));
    }
    const stateOrder = { current: 0, upcoming: 1 };
    return Array.from(results || []).sort((a, b) => {
      const stateDifference = (stateOrder[a.queueState && a.queueState.code] ?? 99)
        - (stateOrder[b.queueState && b.queueState.code] ?? 99);
      if (stateDifference) return stateDifference;
      const leftDate = a.queueState && a.queueState.dueAt ? a.queueState.dueAt : "9999";
      const rightDate = b.queueState && b.queueState.dueAt ? b.queueState.dueAt : "9999";
      return String(leftDate).localeCompare(String(rightDate)) || compareNatural(a.number, b.number);
    });
  }

  function filterOpenQueue(results, filters) {
    const value = filters || {};
    const search = normalizeName(value.search);
    const status = value.status && value.status !== "all" ? normalizeName(value.status) : null;
    const hasProjectFilter = Array.isArray(value.projects);
    const hasTypeFilter = Array.isArray(value.types);
    const hasPeopleFilter = Array.isArray(value.people);
    const hasStateFilter = Array.isArray(value.states);
    const selectedProjects = new Set(hasProjectFilter ? value.projects.map(String) : []);
    const selectedTypes = new Set(hasTypeFilter ? value.types.map(String) : []);
    const selectedPeople = new Set(hasPeopleFilter ? value.people.map(String) : []);
    const selectedStates = new Set(hasStateFilter ? value.states.map(String) : []);
    const stateOrder = { current: 0, upcoming: 1 };
    const filtered = [];
    (results || []).forEach((item) => {
      if (search && !String(item.searchText || "").includes(search)) return false;
      if (status && normalizeName(item.status) !== status) return false;
      if (hasProjectFilter && !selectedProjects.has(String(item.projectId))) return false;
      if (hasTypeFilter && !selectedTypes.has(String(item.itemType))) return false;
      const dueFilter = Array.isArray(value.dueModes)
        ? { modes: value.dueModes, days: value.dueDays }
        : value.due;
      if (!matchesDueFilter(item, dueFilter, value.now)) return false;
      const assignments = Array.isArray(item.queueAssignments) ? item.queueAssignments : [];
      let relevantAssignments = hasPeopleFilter
        ? assignments.filter((assignment) => selectedPeople.has(String(assignment.personKey)))
        : assignments;
      if (hasStateFilter) {
        relevantAssignments = relevantAssignments.filter((assignment) => assignment.state
          && selectedStates.has(String(assignment.state.code)));
      }
      if (hasPeopleFilter && relevantAssignments.length === 0) return false;
      if (relevantAssignments.length === 0) {
        if (hasStateFilter && (!item.queueState || !selectedStates.has(String(item.queueState.code)))) return false;
        filtered.push(item);
        return true;
      }
      const orderedAssignments = Array.from(relevantAssignments).sort((a, b) => {
        const stateDifference = (stateOrder[a.state && a.state.code] ?? 99)
          - (stateOrder[b.state && b.state.code] ?? 99);
        return stateDifference || String(a.person && a.person.fullName || "")
          .localeCompare(String(b.person && b.person.fullName || ""));
      });
      filtered.push(Object.assign({}, item, {
        queueState: orderedAssignments[0].state,
        queuePeople: orderedAssignments.map((assignment) => ({
          personKey: assignment.personKey,
          fullName: assignment.person && assignment.person.fullName || "Unknown person",
          state: assignment.state
        }))
      }));
      return true;
    });
    return sortOpenQueue(filtered, value.sort || "latest");
  }

  function queueReasonLabels(item) {
    const value = item || {};
    const labels = [];
    const seen = new Set();
    const people = Array.isArray(value.queuePeople) ? value.queuePeople : [];
    const personNames = people.map((person) => normalizeName(person.fullName)).filter(Boolean);
    const add = (label, suppressPersonMatch) => {
      const text = String(label || "").trim();
      const normalized = normalizeName(text);
      if (!text || seen.has(normalized)) return;
      if (suppressPersonMatch && personNames.some((name) => normalized.includes(name))) return;
      seen.add(normalized);
      labels.push(text);
    };
    people.forEach((person) => add(
      `${person.fullName || "Unknown person"} — ${person.state && person.state.code === "current" ? "in court" : "coming up"}`,
      false
    ));
    add(value.queueState && (value.queueState.detail || value.queueState.label), true);
    add(value.responsible, true);
    return labels;
  }

  function filterResults(results, filters) {
    const value = filters || {};
    const search = normalizeName(value.search);
    const status = value.status && value.status !== "all" ? normalizeName(value.status) : null;
    const selectedReasons = new Set(Array.isArray(value.reasons) ? value.reasons : []);
    const hasTypeFilter = Array.isArray(value.types);
    const selectedTypes = new Set(hasTypeFilter ? value.types.map(String) : []);

    const filtered = (results || []).filter((item) => {
      if (search && !String(item.searchText || "").includes(search)) {
        return false;
      }
      if (status && normalizeName(item.status) !== status) {
        return false;
      }
      if (hasTypeFilter && !selectedTypes.has(String(item.itemType))) {
        return false;
      }
      if (selectedReasons.size > 0 && !item.reasons.some((reason) => selectedReasons.has(reason.code))) {
        return false;
      }
      return true;
    });
    return sortResults(filtered, value.sort || "latest");
  }

  return Object.freeze({
    REASON_DEFINITIONS,
    identityMatches,
    personKey,
    companyPersonKeys,
    classifyOpenQueue,
    buildActivityIndex,
    filterResults,
    filterOpenQueue,
    queueReasonLabels,
    matchesDueFilter,
    sortResults,
    sortOpenQueue,
    formatDate(value) {
      const iso = normalizeDate(value);
      if (!iso) return "Date unavailable";
      return new Intl.DateTimeFormat(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric"
      }).format(new Date(iso));
    }
  });
});
