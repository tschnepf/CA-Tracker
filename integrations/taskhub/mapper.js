(function attachHelixTaskHubMapper(root, factory) {
  const api = factory(root.HelixActivityCore);
  root.HelixTaskHubMapper = api;
  if (typeof module === "object" && module.exports) {
    module.exports = factory(require("../../activity-core.js"));
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function createTaskHubMapper(Core) {
  "use strict";

  function validDate(value) {
    if (!value) return null;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }

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

  function courtDate(item, assignment, scannedAt) {
    if (assignment && assignment.state && assignment.state.courtStartedAt) {
      return {
        value: validDate(assignment.state.courtStartedAt),
        source: assignment.state.courtDateSource || "observed"
      };
    }
    return Core.courtDate(item, assignment, scannedAt);
  }

  function selectedPersonKeys(index, viewState) {
    const available = new Set((index && index.people || []).map((person) => String(person.key)));
    const requested = Array.isArray(viewState && viewState.people)
      ? viewState.people.map(String).filter((key) => available.has(key))
      : [];
    if (requested.length) return Array.from(new Set(requested));
    return index && index.currentPersonKey ? [String(index.currentPersonKey)] : [];
  }

  function itemUrl(item) {
    const route = item.itemType === "submittal" ? "Submittals" : "RFI";
    const base = `https://project.helixteam.com/Projects/${encodeURIComponent(item.projectId)}/${route}`;
    return `${base}#helix-activity-open=${encodeURIComponent(String(item.id))}`;
  }

  function buildSnapshot(index, viewState) {
    if (!Core || !index) throw new Error("A Helix activity index is required.");
    const selectedPeople = selectedPersonKeys(index, viewState);
    if (!selectedPeople.length) {
      throw new Error("Select at least one Helix person before syncing.");
    }
    const filtered = Core.filterOpenQueue(index.openQueue || [], {
      people: selectedPeople,
      states: ["current"],
      sort: "latest"
    });
    const items = filtered.map((item) => {
      const assignments = (item.queueAssignments || []).filter((assignment) =>
        selectedPeople.includes(String(assignment.personKey))
        && assignment.state
        && assignment.state.code === "current"
      ).map((assignment) => {
        const court = courtDate(item, assignment, index.scannedAt);
        return {
          person_key: String(assignment.personKey),
          full_name: String(assignment.person && assignment.person.fullName || "Unknown person"),
          company: String(assignment.person && assignment.person.company || ""),
          court_started_at: court.value,
          court_date_source: court.source
        };
      });
      const due = earliestDue(item);
      return {
        item_id: String(item.id),
        item_type: item.itemType === "submittal" ? "submittal" : "rfi",
        project_id: String(item.projectId),
        project_name: String(item.projectName || item.projectId),
        number: String(item.number || ""),
        title: String(item.title || ""),
        status: String(item.status || ""),
        responsible: String(item.responsible || ""),
        helix_created_at: validDate(item.dates && item.dates.created),
        helix_modified_at: validDate(item.dates && item.dates.modified),
        due_at: due.dueAt,
        due_date_source: due.source,
        source_url: itemUrl(item),
        assignments
      };
    }).filter((item) => item.assignments.length > 0);
    return {
      schema_version: 1,
      scanned_at: validDate(index.scannedAt) || new Date().toISOString(),
      complete: !Array.isArray(index.warnings) || index.warnings.length === 0,
      selected_people: selectedPeople,
      items
    };
  }

  return Object.freeze({
    buildSnapshot,
    earliestDue,
    courtDate,
    selectedPersonKeys,
    itemUrl
  });
});
