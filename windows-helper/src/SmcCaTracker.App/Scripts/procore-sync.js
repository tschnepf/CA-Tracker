(function attachCaTrackerProcoreSync(root, factory) {
  const api = factory();
  root.CaTrackerProcoreSync = api;
  if (typeof module === "object" && module.exports) module.exports = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function createProcoreSync() {
  "use strict";

  // Read-only. Runs inside a signed-in Procore page and asks Procore's REST service (same site, same
  // sign-in as Procore's own web pages) for:
  //   - the companies you belong to and every project you can see in each (discover),
  //   - every RFI and Submittal of one project (readProject), and
  //   - the files of one RFI or Submittal, for downloading (readItemFiles).
  // No API app has to be installed in any company: it only sees what you can already see.

  const PAGE_SIZE = 100;
  const MAX_PAGES = 1000;
  // Pages asked for at the same time once Procore has said how many rows a list has.
  const PARALLEL_PAGES = 6;

  function httpError(message, status) {
    const error = new Error(message);
    error.status = Number(status || 0);
    return error;
  }

  function withPage(path, page) {
    const joiner = path.includes("?") ? "&" : "?";
    return `${path}${joiner}page=${page}&per_page=${PAGE_SIZE}`;
  }

  async function getJson(fetchImpl, path, companyId) {
    return (await getPage(fetchImpl, path, companyId)).body;
  }

  // One request; also returns Procore's "Total" header (how many rows the whole list has), when sent.
  async function getPage(fetchImpl, path, companyId) {
    const headers = { Accept: "application/json" };
    if (companyId) headers["Procore-Company-Id"] = String(companyId);
    const response = await fetchImpl(path, { credentials: "include", headers });
    if (!response.ok) {
      throw httpError(`Procore answered ${response.status} for ${path.split("?")[0]}`, response.status);
    }
    const type = response.headers && typeof response.headers.get === "function"
      ? String(response.headers.get("content-type") || "")
      : "application/json";
    if (type && !/json/i.test(type)) {
      // A sign-in page or other HTML instead of data: the session isn't accepted here.
      throw httpError(`Procore returned a web page instead of data for ${path.split("?")[0]}`, 406);
    }
    const totalHeader = response.headers && typeof response.headers.get === "function" ? response.headers.get("Total") : null;
    const total = /^\d+$/.test(String(totalHeader || "").trim()) ? Number(String(totalHeader).trim()) : null;
    return { body: await response.json(), total };
  }

  function rowKey(row) {
    return row && typeof row === "object" && row.id !== undefined && row.id !== null ? String(row.id) : null;
  }

  // Reads a whole list. When Procore says how many rows there are (its "Total" header), the remaining
  // pages are asked for several at a time, counted by how many rows it actually sends per page (it may
  // send fewer than asked for); otherwise page by page until a short page. Some Procore lists ignore
  // paging and send everything on every page, so a page that brings nothing new ends the read, and rows
  // are kept once each.
  async function list(fetchImpl, path, companyId) {
    const rows = [];
    const seen = new Set();
    const keep = (batch) => {
      if (!Array.isArray(batch)) throw httpError("Procore returned an unexpected list.", 406);
      let added = 0;
      for (const row of batch) {
        const key = rowKey(row);
        if (key === null || !seen.has(key)) {
          if (key !== null) seen.add(key);
          rows.push(row);
          added += 1;
        }
      }
      return added;
    };

    const first = await getPage(fetchImpl, withPage(path, 1), companyId);
    const firstAdded = keep(first.body);
    if (first.body.length === 0 || firstAdded === 0) return rows;

    if (first.total !== null) {
      if (first.total <= first.body.length) return rows;
      const pages = Math.min(MAX_PAGES, Math.ceil(first.total / first.body.length));
      for (let start = 2; start <= pages; start += PARALLEL_PAGES) {
        const numbers = [];
        for (let page = start; page < start + PARALLEL_PAGES && page <= pages; page += 1) numbers.push(page);
        const batches = await Promise.all(numbers.map((page) => getJson(fetchImpl, withPage(path, page), companyId)));
        let added = 0;
        for (const batch of batches) added += keep(batch);
        if (added === 0) break;
      }
      return rows;
    }

    if (first.body.length < PAGE_SIZE) return rows;
    for (let page = 2; page <= MAX_PAGES; page += 1) {
      const batch = await getJson(fetchImpl, withPage(path, page), companyId);
      const added = keep(batch);
      if (batch.length < PAGE_SIZE || added === 0) break;
    }
    return rows;
  }

  function text(value) {
    if (value === null || value === undefined) return "";
    return String(value).trim();
  }

  function isActive(project) {
    if (!project || typeof project !== "object") return false;
    if (project.active === false) return false;
    const status = text(project.status_name || project.project_stage && project.project_stage.name).toLowerCase();
    return !/^(inactive|closed|archived)$/.test(status);
  }

  function projectName(project) {
    const display = text(project.display_name);
    if (display) return display;
    const number = text(project.project_number);
    const name = text(project.name);
    return number && name && !name.startsWith(number) ? `${number} - ${name}` : name || number;
  }

  /**
   * Lists the companies this sign-in belongs to and the active projects it can see in each.
   * Resolves to { user, companies: [{ id, name, projectCount, error? }], projects: [{ projectId,
   * accountId, accountName, name, host }], skippedInactive }. A company whose projects can't be listed
   * is reported with its error and skipped; the rest still count.
   */
  async function discover(win, options) {
    const opts = options || {};
    const fetchImpl = opts.fetch || win.fetch.bind(win);
    const host = opts.host || (win.location && win.location.hostname) || "app.procore.com";
    const includeInactive = Boolean(opts.includeInactive);

    let user = null;
    try {
      const me = await getJson(fetchImpl, "/rest/v1.0/me", null);
      user = me && typeof me === "object"
        ? {
            fullName: text(me.name || me.login) || null,
            // Procore's user id and login (an email) say who this is on every project.
            id: text(me.id) || null,
            email: text(me.login).includes("@") ? text(me.login) : null
          }
        : null;
    } catch (error) {
      if (error && (error.status === 401 || error.status === 406)) throw error;
    }

    const companies = (await list(fetchImpl, "/rest/v1.0/companies", null))
      .filter((company) => text(company && company.id))
      .map((company) => ({ id: text(company.id), name: text(company.name) || null, active: company.is_active !== false }));
    const listed = await listCompanyProjects(win, companies, { fetch: fetchImpl, host, includeInactive });
    return Object.assign({ user }, listed);
  }

  /**
   * Lists the active projects you can see in each of the given companies ({ id, name, active }), on
   * this page's Procore server. A company hosted on another Procore server answers 404 here; it is
   * reported with its error and status so it can be asked for on the other server.
   */
  async function listCompanyProjects(win, companies, options) {
    const opts = options || {};
    const fetchImpl = opts.fetch || win.fetch.bind(win);
    const host = opts.host || (win.location && win.location.hostname) || "app.procore.com";
    const includeInactive = Boolean(opts.includeInactive);
    const result = { companies: [], projects: [], skippedInactive: 0 };
    const seen = new Set();
    for (const company of companies || []) {
      const companyId = text(company && company.id);
      if (!companyId) continue;
      const companyName = text(company.name) || null;
      const entry = { id: companyId, name: companyName, projectCount: 0 };
      result.companies.push(entry);
      if (company.active === false) continue;
      let projects;
      try {
        projects = await list(fetchImpl, `/rest/v1.0/projects?company_id=${encodeURIComponent(companyId)}`, companyId);
      } catch (error) {
        if (error && error.status === 401) throw error;
        entry.error = String(error && error.message || error);
        entry.status = Number(error && error.status || 0);
        continue;
      }
      for (const project of projects) {
        const projectId = text(project && project.id);
        const name = projectId ? projectName(project) : "";
        if (!projectId || !name || seen.has(projectId)) continue;
        if (!includeInactive && !isActive(project)) {
          result.skippedInactive += 1;
          continue;
        }
        seen.add(projectId);
        entry.projectCount += 1;
        result.projects.push({ projectId, accountId: companyId, accountName: companyName, name, host });
      }
    }
    return result;
  }

  /**
   * Finds projects from links on the current page (Procore's home page lists recent projects with
   * links). Used only when Procore's REST service won't list companies for this sign-in.
   */
  function projectsFromPage(doc, host) {
    const found = new Map();
    const anchors = doc && typeof doc.querySelectorAll === "function" ? doc.querySelectorAll("a[href]") : [];
    for (const anchor of Array.from(anchors)) {
      let url;
      try {
        url = new URL(anchor.getAttribute("href"), `https://${host}/`);
      } catch (_error) {
        continue;
      }
      if (!/(^|\.)procore\.com$/i.test(url.hostname)) continue;
      const modern = url.pathname.match(/\/companies\/(\d+)\/projects\/(\d+)(?:\/|$)/i);
      const legacy = url.pathname.match(/^\/(\d+)\/project(?:\/|$)/i);
      const companyId = modern ? modern[1] : "";
      const projectId = modern ? modern[2] : legacy ? legacy[1] : "";
      if (!projectId || found.has(projectId)) continue;
      const lines = text(anchor.innerText || anchor.textContent).split(/\n+/).map(text).filter(Boolean);
      const name = lines.find((line) => /\d/.test(line) && line.length > 3) || lines[0] || "";
      if (!name) continue;
      found.set(projectId, { projectId, accountId: companyId || null, accountName: null, name, host: url.hostname });
    }
    return Array.from(found.values());
  }

  /**
   * Reads every RFI and Submittal of one project. A tool you can't see on that project (403/404)
   * counts as empty and is named in `unavailable`, so one project's permissions don't stop the rest.
   * Resolves to { rfis, submittals, unavailable: ["rfis"|"submittals"] }. Rejects with `status` when
   * neither list can be read (401: signed out; 403/404: no access to the project).
   */
  async function readProject(win, request, options) {
    const opts = options || {};
    const fetchImpl = opts.fetch || win.fetch.bind(win);
    const base = `/rest/v1.0/projects/${encodeURIComponent(String(request.projectId))}`;
    const unavailable = [];
    const failures = [];
    // Changes only: items updated since the last read (Procore's updated_at filter). If Procore
    // doesn't take the filter, everything is read instead.
    let changesOnly = Boolean(request.updatedSince);
    const range = changesOnly
      ? `filters[updated_at]=${encodeURIComponent(`${request.updatedSince}...${new Date(Date.now() + 86400000).toISOString()}`)}`
      : "";
    async function listTool(name) {
      if (!changesOnly) return list(fetchImpl, `${base}/${name}`, request.companyId);
      try {
        return await list(fetchImpl, `${base}/${name}?${range}`, request.companyId);
      } catch (error) {
        if (error && (error.status === 400 || error.status === 422)) {
          changesOnly = false;
          return list(fetchImpl, `${base}/${name}`, request.companyId);
        }
        throw error;
      }
    }
    async function tool(name) {
      try {
        return await listTool(name);
      } catch (error) {
        if (error && error.status === 401) throw error;
        if (error && (error.status === 403 || error.status === 404)) {
          unavailable.push(name);
          failures.push(error);
          return [];
        }
        throw error;
      }
    }
    let rfis = await tool("rfis");
    const rfisWereChanges = changesOnly;
    const submittals = await tool("submittals");
    if (rfisWereChanges && !changesOnly && unavailable.indexOf("rfis") === -1) {
      // Procore refused the filter for Submittals after taking it for RFIs: read RFIs in full too,
      // so both lists are complete.
      rfis = await tool("rfis");
    }
    if (unavailable.length === 2) {
      const status = failures[0] && failures[0].status || 403;
      throw httpError(status === 404
        ? "Procore couldn't find this project (it may have been removed or you no longer have access)."
        : "You don't have access to this project's RFIs or Submittals in Procore.", status);
    }
    return { rfis, submittals, unavailable, changesOnly };
  }

  /**
   * Lists the files of one RFI or Submittal, with their download links (Procore's file store; they work
   * without the sign-in for a while). An RFI's files are on its question and on each answer; a Submittal's
   * on the submittal itself, on each response, and on its distributions (which repeat the responses'
   * files under the same ids, so each file is listed once). Procore gives no upload date, so each file
   * carries the date of what it came with: the question, the answer, the response or the submittal.
   * Resolves to { attachments: [{ id, filename, uploadedAt, url }] }. Rejects with `status` (401: signed
   * out; 403: no access; 404: not found).
   */
  async function readItemFiles(win, request, options) {
    const opts = options || {};
    const fetchImpl = opts.fetch || win.fetch.bind(win);
    const project = encodeURIComponent(String(request.projectId));
    const item = encodeURIComponent(String(request.itemId));
    const attachments = [];
    const seen = new Set();
    const add = (files, date) => {
      for (const file of Array.isArray(files) ? files : []) {
        const id = text(file && file.id);
        const url = text(file && file.url);
        if (!id || !url || seen.has(id)) continue;
        seen.add(id);
        attachments.push({ id, filename: text(file.filename || file.name) || "Attachment", uploadedAt: text(date) || null, url });
      }
    };

    if (request.itemType === "submittal") {
      const submittal = await getJson(fetchImpl, `/rest/v1.1/projects/${project}/submittals/${item}`, request.companyId);
      add(submittal.attachments, submittal.created_at || submittal.received_date);
      for (const response of Array.isArray(submittal.approvers) ? submittal.approvers : []) {
        add(response && response.attachments, response && (response.returned_date || response.sent_date));
      }
      for (const distribution of Array.isArray(submittal.distributed_submittals) ? submittal.distributed_submittals : []) {
        add(distribution && distribution.final_attachments, distribution && distribution.sent_at || submittal.distributed_at);
      }
    } else {
      const rfi = await getJson(fetchImpl, `/rest/v1.0/projects/${project}/rfis/${item}`, request.companyId);
      for (const question of Array.isArray(rfi.questions) ? rfi.questions : []) {
        add(question && question.attachments, question && question.question_date);
        for (const answer of Array.isArray(question && question.answers) ? question.answers : []) {
          add(answer && answer.attachments, answer && answer.answer_date);
        }
      }
    }
    return { attachments };
  }

  return Object.freeze({ discover, listCompanyProjects, projectsFromPage, readProject, readItemFiles, isActive, projectName, withPage });
});
