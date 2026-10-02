(function attachCaTrackerProcoreCore(root, factory) {
  const api = factory();
  root.CaTrackerProcoreCore = api;
  if (typeof module === "object" && module.exports) module.exports = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function createProcoreCore() {
  "use strict";

  function pageContext(value) {
    const url = new URL(String(value || ""));
    const modern = url.pathname.match(
      /^\/webclients\/host\/companies\/(\d+)(?:\/projects\/(\d+))?\/tools(?:\/|$)/i
    );
    if (modern) {
      return Object.freeze({ companyId: modern[1], projectId: modern[2] || "" });
    }
    const legacy = url.pathname.match(/^\/(\d+)\/project(?:\/|$)/i);
    if (legacy) return Object.freeze({ companyId: "", projectId: legacy[1] });
    throw new Error("Open a Procore company or project tool before using SMC-CA-Tracker.");
  }

  function asIso(value) {
    if (!value) return null;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }

  function safeSourceUrl(value) {
    try {
      const url = new URL(String(value || ""));
      const supportedHost = /^(?:app|[a-z]{2}\d{2})\.procore\.com$/i.test(url.hostname);
      const supportedPath = /^\/\d+\/project\//i.test(url.pathname)
        || /^\/webclients\/host\/companies\/\d+\//i.test(url.pathname);
      return url.protocol === "https:"
        && supportedHost
        && supportedPath
        && !url.username
        && !url.password
        ? url.href
        : "";
    } catch (_error) {
      return "";
    }
  }

  function safeProjectHomeUrl(value) {
    try {
      const url = new URL(String(value || ""));
      const supportedHost = /^(?:app|[a-z]{2}\d{2})\.procore\.com$/i.test(url.hostname);
      return url.protocol === "https:"
        && supportedHost
        && /^\/\d+\/project\/home\/?$/i.test(url.pathname)
        && !url.username
        && !url.password
        ? url.href
        : "";
    } catch (_error) {
      return "";
    }
  }

  function textValue(value) {
    return String(value || "").replace(/\s+/g, " ").trim();
  }

  function absoluteUrl(value, pageUrl) {
    try {
      return new URL(String(value || ""), String(pageUrl || "")).href;
    } catch (_error) {
      return "";
    }
  }

  function pathIdentifier(value, patterns) {
    try {
      const path = new URL(value).pathname;
      for (const pattern of patterns) {
        const match = path.match(pattern);
        if (match) return match[1];
      }
    } catch (_error) {
      return "";
    }
    return "";
  }

  function cellValue(row, name) {
    const value = row && row[name];
    return value && typeof value === "object"
      ? { text: textValue(value.text), href: textValue(value.href) }
      : { text: textValue(value), href: "" };
  }

  // The first cell whose column id matches, for columns Procore names differently per tool.
  function cellMatching(row, pattern) {
    for (const name of Object.keys(row || {})) {
      if (pattern.test(name)) return cellValue(row, name);
    }
    return { text: "", href: "" };
  }

  // Submittal links come as /project/submittal(s)/…, /project/submittal_log(s)/… or /project/submittals_log/….
  const ITEM_LINK = /\/(?:tools\/(rfis|submittals)\/\d+|project\/(rfi)\/show\/\d+|project\/(submittal)s?(?:_logs?)?(?:\/show)?\/\d+)/i;

  function itemTypeFromHref(href) {
    const match = String(href || "").match(ITEM_LINK);
    if (!match) return "";
    return (match[1] || match[2] || match[3] || "").toLowerCase().startsWith("submittal") ? "submittal" : "rfi";
  }

  function itemLinkCell(row) {
    const title = cellValue(row, "title");
    if (title.href && itemTypeFromHref(title.href)) return { cell: title, name: "title" };
    for (const name of Object.keys(row && typeof row === "object" ? row : {})) {
      const cell = cellValue(row, name);
      if (cell.href && itemTypeFromHref(cell.href)) return { cell, name };
    }
    return { cell: title, name: "title" };
  }

  // "#12: Slab edge" in a title column, a number link with the title in its own column, or a title link
  // (in a column named "subject", say) with the number in its own column.
  function numberAndTitle(row, link) {
    const numberColumn = textValue(cellValue(row, "number").text).replace(/^#/, "");
    if (link.name === "title") {
      const parsed = splitNumberAndTitle(link.cell.text);
      return parsed.number ? parsed : { number: numberColumn, title: parsed.title };
    }
    if (link.name !== "number" && numberColumn) {
      return { number: numberColumn, title: textValue(link.cell.text) };
    }
    const title = ["title", "subject", "name", "description"]
      .map((name) => cellValue(row, name).text)
      .find((text) => text && text !== link.cell.text);
    return { number: textValue(link.cell.text).replace(/^#/, ""), title: textValue(title || link.cell.text) };
  }

  // Only a number-like start counts ("#12:", "RFI-12:", "S 4:", "03 30 00-1:"), so a title such as
  // "Door Hardware: confirm finish" stays whole.
  function splitNumberAndTitle(value) {
    const normalized = textValue(value);
    const match = normalized.match(/^#?([A-Za-z]{0,4}[-\s]?\d[^:]{0,60}):\s*(.+)$/);
    return match
      ? { number: textValue(match[1]), title: textValue(match[2]) }
      : { number: "", title: normalized };
  }

  function visibleQueueFromRows(pageUrl, rows, scannedAtValue) {
    const context = pageContext(pageUrl);
    const scannedAt = asIso(scannedAtValue) || new Date().toISOString();
    const visibleRows = Array.isArray(rows) ? rows : [];
    const observedCompanyId = context.companyId || visibleRows.reduce((result, row) => {
      if (result) return result;
      const companyCell = cellValue(row, "company_name");
      return pathIdentifier(absoluteUrl(companyCell.href, pageUrl), [/^\/(\d+)\/company(?:\/|$)/i]);
    }, "");
    const personKey = `procore:visible:${observedCompanyId || "current"}`;
    const items = [];
    for (const row of visibleRows) {
      // Project RFI/Submittal lists have no "item type" column and may name the title column
      // differently, so fall back to the cell that links to an item and read the type from its link.
      const link = itemLinkCell(row);
      const titleCell = link.cell;
      const typeCell = cellValue(row, "item_type");
      const itemTypeText = typeCell.text.toLowerCase();
      const linkType = itemTypeFromHref(titleCell.href);
      const itemType = itemTypeText.includes("submittal")
        ? "submittal"
        : itemTypeText.includes("rfi") ? "rfi" : linkType;
      if (!itemType) continue;

      const sourceUrl = safeSourceUrl(absoluteUrl(titleCell.href, pageUrl));
      if (!sourceUrl) continue;
      const projectCell = cellValue(row, "project_name");
      const projectNumberCell = cellValue(row, "project.number");
      const projectUrl = absoluteUrl(projectCell.href || projectNumberCell.href || sourceUrl, pageUrl);
      const projectId = pathIdentifier(projectUrl, [
        /^\/(\d+)\/project(?:\/|$)/i,
        /^\/webclients\/host\/companies\/\d+\/projects\/(\d+)(?:\/|$)/i
      ]) || context.projectId;
      const itemId = pathIdentifier(sourceUrl, itemType === "rfi" ? [
        /\/project\/rfi\/show\/(\d+)(?:\/|$)/i,
        /\/tools\/rfis\/(\d+)(?:\/|$)/i
      ] : [
        /\/project\/submittals?(?:_logs?)?(?:\/show)?\/(\d+)(?:\/|$)/i,
        /\/tools\/submittals\/(\d+)(?:\/|$)/i
      ]);
      if (!projectId || !itemId) continue;

      const companyCell = cellValue(row, "company_name");
      const companyUrl = absoluteUrl(companyCell.href, pageUrl);
      const companyId = observedCompanyId || pathIdentifier(companyUrl, [/^\/(\d+)\/company(?:\/|$)/i]);
      const parsedTitle = numberAndTitle(row, link);
      const dueCell = cellValue(row, "due_date");
      const dueAt = asIso((dueCell.text ? dueCell : cellMatching(row, /^due/i)).text);
      const ballInCourt = cellMatching(row, /ball.?in.?court/i).text;
      items.push({
        item_id: itemId,
        item_type: itemType,
        account_id: companyId,
        project_id: projectId,
        project_name: projectCell.text || projectNumberCell.text || `Procore Project ${projectId}`,
        number: parsedTitle.number,
        title: parsedTitle.title || `${itemType === "rfi" ? "RFI" : "Submittal"} ${itemId}`,
        status: cellValue(row, "status").text,
        responsible: "Current Procore user",
        ball_in_court: ballInCourt,
        source_created_at: null,
        source_modified_at: null,
        due_at: dueAt,
        due_date_source: dueAt ? "due" : null,
        source_url: sourceUrl,
        assignments: [{
          person_key: personKey,
          full_name: "Current Procore user",
          company: companyCell.text,
          court_started_at: scannedAt,
          court_date_source: "observed"
        }],
        source_metadata: { company_id: companyId, source_mode: "visible_page" }
      });
    }
    const projectIds = Array.from(new Set(items.map((item) => item.project_id)));
    return {
      schema_version: 1,
      scanned_at: scannedAt,
      complete: false,
      selected_people: [personKey],
      items,
      warnings: [],
      identity: {
        id: personKey,
        person_key: personKey,
        full_name: "Current Procore user",
        login: ""
      },
      company_id: observedCompanyId,
      scope_account_id: observedCompanyId,
      scope_project_ids: projectIds,
      source_mode: "visible_page"
    };
  }

  function queueIndex(queue) {
    if (!queue || typeof queue !== "object" || !queue.identity || !queue.identity.id) {
      throw new Error("The Procore queue is invalid.");
    }
    const scannedAt = asIso(queue.scanned_at) || new Date().toISOString();
    const identity = queue.identity;
    const personKey = String(identity.person_key || `procore:user:${identity.id}`);
    const person = {
      key: personKey,
      id: String(identity.id),
      contactId: null,
      fullName: String(identity.full_name || identity.login || "Procore user"),
      companyId: null,
      company: null,
      isCurrent: true
    };
    const items = (Array.isArray(queue.items) ? queue.items : []).map((source) => {
      const itemType = source.item_type === "submittal" ? "submittal" : "rfi";
      const dueAt = asIso(source.due_at);
      const modifiedAt = asIso(source.source_modified_at);
      const createdAt = asIso(source.source_created_at);
      const assignments = Array.isArray(source.assignments) && source.assignments.length
        ? source.assignments
        : [{ person_key: personKey, full_name: person.fullName }];
      const queueAssignments = assignments.map((assignment) => ({
        personKey: String(assignment.person_key || personKey),
        person: {
          key: String(assignment.person_key || personKey),
          id: String(identity.id),
          fullName: String(assignment.full_name || person.fullName),
          company: String(assignment.company || "") || null,
          isCurrent: String(assignment.person_key || personKey) === personKey
        },
        state: {
          code: "current",
          label: "In court",
          detail: "Procore Ball In Court",
          dueAt,
          courtStartedAt: asIso(assignment.court_started_at) || scannedAt,
          courtDateSource: String(assignment.court_date_source || "observed")
        }
      }));
      const latestActivityAt = modifiedAt || createdAt || scannedAt;
      const number = String(source.number || source.item_id || "");
      const title = String(source.title || `${itemType === "rfi" ? "RFI" : "Submittal"} ${number}`);
      const status = String(source.status || "Open");
      return {
        id: String(source.item_id || ""),
        itemId: String(source.item_id || ""),
        itemType,
        projectId: String(source.project_id || ""),
        projectName: String(source.project_name || source.project_id || "Unknown project"),
        number,
        title,
        status,
        responsible: String(source.responsible || person.fullName),
        contract: "",
        sourceUrl: safeSourceUrl(source.source_url),
        dates: { due: dueAt, modified: modifiedAt, created: createdAt },
        events: [{ kind: "assigned", timestamp: latestActivityAt, detail: "Procore Ball In Court" }],
        reasons: [{ code: "assigned", label: "Assigned", count: 1, lastAt: latestActivityAt }],
        latestActivityAt,
        queueAssignments,
        queueState: queueAssignments[0].state,
        searchText: [number, title, status, source.project_name, source.responsible, itemType]
          .filter(Boolean).join(" ").toLocaleLowerCase()
      };
    }).filter((item) => item.id && item.projectId);
    const projects = Array.from(new Map(items.map((item) => [item.projectId, {
      id: item.projectId,
      name: item.projectName
    }])).values()).sort((left, right) => left.name.localeCompare(right.name));
    return {
      context: {
        provider: "procore",
        sourceMode: String(queue.source_mode || "api"),
        user: { fullName: person.fullName }
      },
      scannedAt,
      warnings: (Array.isArray(queue.warnings) ? queue.warnings : []).map((warning) => ({
        collection: String(warning)
      })),
      results: Array.from(items).sort((left, right) => String(right.latestActivityAt).localeCompare(String(left.latestActivityAt))),
      openQueue: items,
      projects,
      people: [person],
      currentPersonKey: personKey
    };
  }

  function normalizeDiscoveryCatalog(value) {
    const source = value && typeof value === "object" ? value : {};
    const companies = [];
    const companyIds = new Set();
    for (const companyValue of Array.isArray(source.companies) ? source.companies : []) {
      const company = companyValue && typeof companyValue === "object" ? companyValue : {};
      const id = String(company.id || "").trim();
      const name = textValue(company.name).slice(0, 240);
      if (!/^\d+$/.test(id) || !name || companyIds.has(id) || companies.length >= 100) continue;
      companyIds.add(id);
      const projects = [];
      const projectIds = new Set();
      for (const projectValue of Array.isArray(company.projects) ? company.projects : []) {
        const project = projectValue && typeof projectValue === "object" ? projectValue : {};
        const projectId = String(project.id || "").trim();
        const projectName = textValue(project.name).slice(0, 300);
        const projectUrl = safeProjectHomeUrl(project.url);
        if (!/^\d+$/.test(projectId) || !projectName || !projectUrl
          || projectIds.has(projectId) || projects.length >= 2000) continue;
        projectIds.add(projectId);
        projects.push({
          id: projectId,
          name: projectName,
          url: projectUrl,
          scanStatus: ["scanned", "access_denied"].includes(project.scanStatus)
            ? project.scanStatus
            : "not_scanned",
          discoveredAt: asIso(project.discoveredAt)
        });
      }
      companies.push({
        id,
        name,
        status: company.status === "access_denied" ? "access_denied" : "discovered",
        error: textValue(company.error).slice(0, 500),
        discoveredAt: asIso(company.discoveredAt),
        projects
      });
    }

    const scanValue = source.scan && typeof source.scan === "object" ? source.scan : {};
    const scanStatus = ["running", "complete", "cancelled", "error"].includes(scanValue.status)
      ? scanValue.status
      : "idle";
    const pendingCompanies = Array.from(new Set(
      (Array.isArray(scanValue.pendingCompanies) ? scanValue.pendingCompanies : [])
        .map((name) => textValue(name).slice(0, 240)).filter(Boolean)
    )).slice(0, 100);
    const completedCompanies = Array.from(new Set(
      (Array.isArray(scanValue.completedCompanies) ? scanValue.completedCompanies : [])
        .map((name) => textValue(name).slice(0, 240)).filter(Boolean)
    )).slice(0, 100);
    return {
      version: 1,
      updatedAt: asIso(source.updatedAt),
      companies,
      scan: {
        id: textValue(scanValue.id).slice(0, 160),
        ownerTabId: Number.isInteger(scanValue.ownerTabId) ? scanValue.ownerTabId : null,
        status: scanStatus,
        phase: ["enumerate", "scan", "restore"].includes(scanValue.phase) ? scanValue.phase : "",
        startedAt: asIso(scanValue.startedAt),
        completedAt: asIso(scanValue.completedAt),
        originalUrl: safeSourceUrl(scanValue.originalUrl)
          || safeProjectHomeUrl(scanValue.originalUrl)
          || (() => {
            try {
              const url = new URL(String(scanValue.originalUrl || ""));
              return /^(?:app|[a-z]{2}\d{2})\.procore\.com$/i.test(url.hostname)
                && url.protocol === "https:"
                && /^\/webclients\/host\/companies\/\d+\/tools\//i.test(url.pathname)
                ? url.href : "";
            } catch (_error) { return ""; }
          })(),
        originalCompanyId: /^\d+$/.test(String(scanValue.originalCompanyId || ""))
          ? String(scanValue.originalCompanyId) : "",
        originalCompanyName: textValue(scanValue.originalCompanyName).slice(0, 240),
        currentTarget: textValue(scanValue.currentTarget).slice(0, 240),
        pendingCompanies,
        completedCompanies,
        totalCompanies: Math.max(0, Math.min(100, Number(scanValue.totalCompanies) || 0)),
        cancelRequested: Boolean(scanValue.cancelRequested),
        message: textValue(scanValue.message).slice(0, 500),
        error: textValue(scanValue.error).slice(0, 500)
      }
    };
  }

  function mergeDiscoveryCatalog(index, catalogValue) {
    const catalog = normalizeDiscoveryCatalog(catalogValue);
    const source = index && typeof index === "object" ? index : {};
    const projects = new Map();
    for (const company of catalog.companies) {
      for (const project of company.projects) {
        projects.set(project.id, {
          id: project.id,
          name: `${company.name} — ${project.name}`,
          projectName: project.name,
          companyId: company.id,
          companyName: company.name,
          sourceUrl: project.url,
          scanStatus: project.scanStatus
        });
      }
    }
    for (const project of Array.isArray(source.projects) ? source.projects : []) {
      const id = String(project && project.id || "");
      if (!id) continue;
      if (!projects.has(id)) projects.set(id, project);
    }
    return Object.assign({}, source, {
      projects: Array.from(projects.values()).sort((left, right) => (
        String(left.companyName || "\uffff").localeCompare(String(right.companyName || "\uffff"))
        || String(left.projectName || left.name || "").localeCompare(String(right.projectName || right.name || ""))
      )),
      discoveryCatalog: {
        companyCount: catalog.companies.length,
        projectCount: Array.from(projects.values()).filter((project) => project.companyId).length,
        updatedAt: catalog.updatedAt,
        scan: catalog.scan
      }
    });
  }

  // A sign-in form on the page: an email or password box, or a button that says only "Log in" or
  // "Sign in" (maybe "… to Procore" / "… with Google"; not "Login history").
  function showsSignInForm(doc) {
    if (!doc || typeof doc.querySelector !== "function") return false;
    if (doc.querySelector('input[type="password"], input[type="email"], input[name*="email" i], input[autocomplete="username"]')) {
      return true;
    }
    const buttons = typeof doc.querySelectorAll === "function" ? Array.from(doc.querySelectorAll("button, a, input[type=submit]")) : [];
    return buttons.some((element) => /^\s*(log|sign)\s*in(\s+(to|with)\b.*)?\s*$/i
      .test(String(element.innerText || element.value || element.textContent || "")));
  }

  // One look at whether this Procore page is signed in: Procore says who is (the same read its own
  // pages make), or a sign-in form shows. "pending" while neither is clear yet.
  async function signInState(windowObject, fetchImpl) {
    try {
      const response = await fetchImpl("/rest/v1.0/me", { credentials: "include", headers: { Accept: "application/json" } });
      if (response.status === 401) return { state: "signedOut" };
      if (response.ok) {
        const me = await response.json();
        return { state: "signedIn", user: { fullName: (me && (me.name || me.login)) || null, company: null } };
      }
    } catch (_error) {
      // Not answering yet: look at the page instead.
    }
    return { state: showsSignInForm(windowObject && windowObject.document) ? "signedOut" : "pending" };
  }

  return Object.freeze({
    mergeDiscoveryCatalog,
    normalizeDiscoveryCatalog,
    pageContext,
    queueIndex,
    safeProjectHomeUrl,
    safeSourceUrl,
    showsSignInForm,
    signInState,
    visibleQueueFromRows
  });
});
