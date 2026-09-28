"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const ProviderCore = require("../provider-core.js");
const ProcoreCore = require("../procore-core.js");

const source = fs.readFileSync(path.resolve(__dirname, "..", "procore-content-script.js"), "utf8");

function waitFor(predicate) {
  return new Promise((resolve, reject) => {
    const deadline = Date.now() + 5000;
    const poll = () => {
      if (predicate()) resolve();
      else if (Date.now() >= deadline) reject(new Error("Timed out waiting for Procore content script."));
      else setTimeout(poll, 5);
    };
    poll();
  });
}

function documentWithRows(rows) {
  const rowNodes = rows.map((row) => ({
    querySelectorAll() {
      return Object.entries(row).map(([column, value]) => ({
        textContent: value.text,
        getAttribute(name) { return name === "col-id" ? column : null; },
        querySelector() {
          return value.href ? {
            href: value.href,
            getAttribute() { return value.href; }
          } : null;
        }
      }));
    }
  }));
  return {
    querySelectorAll(selector) { return selector === '[role="row"]' ? rowNodes : []; },
    querySelector() { return null; }
  };
}

function documentWithOverviewWidget(rows, project) {
  const itemSelector = 'a[href*="/project/rfi/show/"], a[href*="/project/submittal/"], a[href*="/project/submittals/"], a[href*="/project/submittal_log/"], a[href*="/tools/rfis/"], a[href*="/tools/submittals/"]';
  const card = { parentElement: null };
  const headingParent = { parentElement: card, querySelector() { return null; } };
  const heading = { textContent: "My Open Items", parentElement: headingParent };
  const links = rows.map((row) => {
    const link = {
      textContent: row.title,
      href: row.href,
      getAttribute(name) { return name === "href" ? row.href : null; },
      closest() { return null; }
    };
    const cells = [row.type, row.title, row.dueDate, row.status].map((text, index) => ({
      textContent: text,
      parentElement: null,
      children: index === 1 ? [link] : [],
      contains(node) { return index === 1 && node === link; }
    }));
    const rowNode = { children: cells, parentElement: card };
    cells.forEach((cell) => { cell.parentElement = rowNode; });
    link.parentElement = cells[1];
    return link;
  });
  card.querySelector = (selector) => selector === itemSelector ? links[0] || null : null;
  card.querySelectorAll = (selector) => selector === itemSelector ? links : [];

  const image = {
    getAttribute(name) { return name === "alt" ? project.name : null; }
  };
  const homeLink = {
    textContent: project.name,
    href: project.href,
    getAttribute(name) { return name === "href" ? project.href : null; },
    querySelector(selector) { return selector === "img[alt]" ? image : null; }
  };
  return {
    querySelectorAll(selector) {
      if (selector === '[role="row"]') return [];
      if (selector === "h1, h2, h3, h4, h5, h6") return [heading];
      return [];
    },
    querySelector(selector) {
      return selector.includes("/3364579/project/home") ? homeLink : null;
    }
  };
}

function discoveryDocument(windowObject, companies) {
  let currentIndex = 0;
  let projectPickerOpen = false;
  let companyMenuOpen = false;

  const image = {
    textContent: "",
    getAttribute(name) { return name === "alt" ? companies[currentIndex].name : null; }
  };
  const homeLink = {
    textContent: "",
    get href() { return `/${companies[currentIndex].id}/company/home`; },
    getAttribute(name) { return name === "href" ? this.href : null; },
    querySelector(selector) { return selector === "img[alt]" ? image : null; }
  };
  const projectButton = {
    get textContent() { return `${companies[currentIndex].name} Select a Project`; },
    click() { projectPickerOpen = !projectPickerOpen; if (!projectPickerOpen) companyMenuOpen = false; },
    getAttribute(name) { return name === "aria-expanded" ? String(projectPickerOpen) : null; }
  };
  const changeButton = {
    textContent: "Change",
    click() { companyMenuOpen = true; },
    getAttribute() { return null; }
  };
  const closeButton = {
    textContent: "Close",
    click() { companyMenuOpen = false; },
    getAttribute() { return null; }
  };
  const menuParent = {
    parentElement: null,
    querySelectorAll(selector) { return selector === "button" ? [closeButton] : []; }
  };
  const menu = { parentElement: menuParent };
  const menuItems = companies.map((company, index) => ({
    textContent: company.name,
    click() {
      currentIndex = index;
      projectPickerOpen = false;
      companyMenuOpen = false;
      windowObject.location.href = `https://app.procore.com/webclients/host/companies/${company.id}/tools/hubs/company-hub/views/portfolio`;
    }
  }));
  const listbox = {
    scrollHeight: 0,
    clientHeight: 0,
    scrollTop: 0,
    querySelectorAll() { return []; },
    dispatchEvent() {}
  };
  function projectLinks() {
    if (!projectPickerOpen || companyMenuOpen) return [];
    return companies[currentIndex].projects.map((project) => ({
      textContent: project.name,
      href: `https://app.procore.com/${project.id}/project/home`,
      getAttribute(name) { return name === "href" ? this.href : null; }
    }));
  }
  return {
    querySelectorAll(selector) {
      if (selector === '[role="row"]' || selector === "h1, h2, h3, h4, h5, h6") return [];
      if (selector === "button") {
        return [projectButton, ...(projectPickerOpen ? [changeButton] : []), ...(companyMenuOpen ? [closeButton] : [])];
      }
      if (selector === '[role="option"][href*="/project/home"]') return projectLinks();
      if (selector === '[role="menuitem"]') return companyMenuOpen ? menuItems : [];
      return [];
    },
    querySelector(selector) {
      if (selector === 'a[href*="/company/home"] img[alt]') return image;
      if (selector === 'a[href*="/company/home"]') return homeLink;
      if (selector === "#react_company_select") return projectPickerOpen ? changeButton : null;
      if (selector === '[role="menuitem"]') return companyMenuOpen ? menuItems[0] : null;
      if (selector === '[role="menu"]') return companyMenuOpen ? menu : null;
      if (selector === '[role="listbox"][aria-label="Projects"]' || selector === '[role="listbox"]') {
        return projectPickerOpen && !companyMenuOpen ? listbox : null;
      }
      return null;
    }
  };
}

const visibleRow = {
  company_name: { text: "Example Co", href: "https://app.procore.com/12/company/home" },
  "project.number": { text: "100", href: "https://app.procore.com/34/project/home" },
  project_name: { text: "Airport", href: "https://app.procore.com/34/project/home" },
  item_type: { text: "RFI", href: "" },
  title: { text: "RFI-12: Confirm routing", href: "https://app.procore.com/34/project/rfi/show/99" },
  due_date: { text: "2026-09-10", href: "" },
  status: { text: "Open", href: "" }
};

test("Procore content script works standalone from rendered page rows", async () => {
  const messages = [];
  const uiState = { data: [], errors: [], statuses: [] };
  const chrome = {
    runtime: {
      lastError: null,
      sendMessage(message, callback) {
        messages.push(message);
        if (message.type === "TASKHUB_STATUS") {
          callback({ ok: true, result: { connected: false, user: null } });
        } else callback({ ok: false, error: "Task Hub is unavailable." });
      }
    }
  };
  const windowObject = {
    location: { href: "https://app.procore.com/webclients/host/companies/12/tools/opentasks" },
    open() {},
    setInterval() { return 1; }
  };
  windowObject.top = windowObject;
  const ui = {
    shadowRoot: { host: { hidden: false } },
    setContext() {},
    setTaskHubStatus(value) { uiState.statuses.push(value); },
    setLoading() {},
    setData(value) { uiState.data.push(value); },
    setCacheWarning() {},
    setTaskHubSyncing() {},
    setTaskHubSyncResult() {},
    setTaskHubSyncError() {},
    setError(value) { uiState.errors.push(value); },
    notifyOpenFailure() {}
  };

  vm.runInNewContext(source, {
    window: windowObject,
    document: documentWithRows([visibleRow]),
    chrome,
    URL,
    Date,
    Error,
    Promise,
    setTimeout,
    TaskHubProviderCore: ProviderCore,
    TaskHubProcoreCore: ProcoreCore,
    TaskHubActivityUI: { mount() { return ui; } }
  });

  await waitFor(() => uiState.statuses.length > 0);
  assert.equal(uiState.errors.length, 0);
  assert.equal(uiState.data[0].openQueue[0].title, "Confirm routing");
  assert.equal(uiState.data[0].context.sourceMode, "visible_page");
  assert.equal(uiState.statuses.at(-1).connected, false);
  assert.equal(messages.some((message) => message.type === "PROCORE_QUEUE"), false);
  assert.equal(messages.some((message) => message.type === "PROCORE_SNAPSHOT_SYNC"), false);
});

test("Procore content script reads the project overview My Open Items widget", async () => {
  const uiState = { data: [], statuses: [] };
  const chrome = {
    runtime: {
      lastError: null,
      sendMessage(message, callback) {
        if (message.type === "TASKHUB_STATUS") {
          callback({ ok: true, result: { connected: false, user: null } });
        } else callback({ ok: false, error: "Task Hub is unavailable." });
      }
    }
  };
  const windowObject = {
    location: { href: "https://app.procore.com/3364579/project/home" },
    open() {},
    setInterval() { return 1; }
  };
  windowObject.top = windowObject;
  const ui = {
    shadowRoot: { host: { hidden: false } },
    setContext() {},
    setTaskHubStatus(value) { uiState.statuses.push(value); },
    setLoading() {},
    setData(value) { uiState.data.push(value); },
    setCacheWarning() {},
    setTaskHubSyncing() {},
    setTaskHubSyncResult() {},
    setTaskHubSyncError() {},
    notifyOpenFailure() {}
  };

  vm.runInNewContext(source, {
    window: windowObject,
    document: documentWithOverviewWidget([
      {
        type: "RFI",
        title: "#127: Building 01 Location and Designation Clarification",
        href: "https://app.procore.com/3364579/project/rfi/show/27339791",
        dueDate: "Aug 17, 2026",
        status: "Open"
      },
      {
        type: "RFI",
        title: "#145: Conflicting Pathway Locations",
        href: "https://app.procore.com/3364579/project/rfi/show/27453843",
        dueDate: "Aug 27, 2026",
        status: "Open"
      }
    ], {
      name: "J11004.000 - Aligned Data Centers - CMH01",
      href: "https://app.procore.com/3364579/project/home"
    }),
    chrome,
    URL,
    Date,
    Error,
    Promise,
    setTimeout,
    TaskHubProviderCore: ProviderCore,
    TaskHubProcoreCore: ProcoreCore,
    TaskHubActivityUI: { mount() { return ui; } }
  });

  await waitFor(() => uiState.statuses.length > 0);
  assert.equal(uiState.data[0].openQueue.length, 2);
  assert.equal(uiState.data[0].projects.length, 1);
  assert.equal(uiState.data[0].projects[0].name, "J11004.000 - Aligned Data Centers - CMH01");
  assert.equal(uiState.data[0].openQueue[0].number, "127");
  assert.equal(uiState.data[0].openQueue[1].number, "145");
});

test("Procore content script merges discovered projects into the standalone project filter", async () => {
  const uiState = { data: [], discovery: [] };
  const catalog = {
    version: 1,
    updatedAt: "2026-09-04T18:00:00Z",
    companies: [{
      id: "12",
      name: "Example Company",
      projects: [{
        id: "34",
        name: "Airport",
        url: "https://app.procore.com/34/project/home"
      }, {
        id: "35",
        name: "Hospital",
        url: "https://app.procore.com/35/project/home"
      }]
    }],
    scan: { status: "complete", completedAt: "2026-09-04T18:00:00Z" }
  };
  const chrome = {
    runtime: {
      lastError: null,
      sendMessage(message, callback) {
        if (message.type === "TASKHUB_STATUS") {
          callback({ ok: true, result: { connected: false, user: null } });
        } else if (message.type === "PROCORE_DISCOVERY_READ") {
          callback({ ok: true, result: { catalog, isOwner: false } });
        } else callback({ ok: false, error: "Unavailable" });
      }
    }
  };
  const windowObject = {
    location: { href: "https://app.procore.com/webclients/host/companies/12/tools/opentasks" },
    open() {},
    setInterval() { return 1; }
  };
  windowObject.top = windowObject;
  const ui = {
    shadowRoot: { host: { hidden: false } },
    setContext() {},
    setTaskHubStatus() {},
    setDiscoveryState(value) { uiState.discovery.push(value); },
    setLoading() {},
    setData(value) { uiState.data.push(value); },
    setCacheWarning() {},
    setTaskHubSyncing() {},
    setTaskHubSyncResult() {},
    setTaskHubSyncError() {},
    notifyOpenFailure() {}
  };

  vm.runInNewContext(source, {
    window: windowObject,
    document: documentWithRows([]),
    chrome,
    URL,
    Date,
    Error,
    Promise,
    Event,
    setTimeout,
    TaskHubProviderCore: ProviderCore,
    TaskHubProcoreCore: ProcoreCore,
    TaskHubActivityUI: { mount() { return ui; } }
  });

  await waitFor(() => uiState.data.some((value) => value.projects.length === 2));
  assert.deepEqual(
    Array.from(uiState.data.at(-1).projects, (project) => [project.id, project.name]),
    [
      ["34", "Example Company — Airport"],
      ["35", "Example Company — Hospital"]
    ]
  );
  assert.equal(uiState.discovery.at(-1).companyCount, 1);
  assert.equal(uiState.discovery.at(-1).projectCount, 2);
});

test("Procore discovery scans invited companies, restores the origin, and persists all projects", async () => {
  let callbacks;
  let storedCatalog = ProcoreCore.normalizeDiscoveryCatalog(null);
  const windowObject = {
    location: {
      href: "https://app.procore.com/webclients/host/companies/12/tools/opentasks",
      assign(value) { this.href = value; }
    },
    open() {},
    setInterval() { return 1; }
  };
  windowObject.top = windowObject;
  const companies = [{
    id: "12",
    name: "Example Company",
    projects: [{ id: "34", name: "Airport" }]
  }, {
    id: "13",
    name: "Partner Company",
    projects: [
      { id: "35", name: "Hospital" },
      { id: "36", name: "Data Center" }
    ]
  }];
  const chrome = {
    runtime: {
      lastError: null,
      sendMessage(message, callback) {
        if (message.type === "TASKHUB_STATUS") {
          callback({ ok: true, result: { connected: false, user: null } });
        } else if (message.type === "PROCORE_DISCOVERY_READ") {
          callback({ ok: true, result: { catalog: storedCatalog, isOwner: false } });
        } else if (message.type === "PROCORE_DISCOVERY_START") {
          storedCatalog = ProcoreCore.normalizeDiscoveryCatalog({
            companies: storedCatalog.companies,
            scan: {
              id: "scan-1",
              ownerTabId: 1,
              status: "running",
              phase: "enumerate",
              startedAt: new Date().toISOString(),
              originalUrl: message.origin.url,
              originalCompanyId: message.origin.companyId,
              originalCompanyName: message.origin.companyName,
              message: "Reading invited companies…"
            }
          });
          callback({ ok: true, result: { catalog: storedCatalog, isOwner: true } });
        } else if (message.type === "PROCORE_DISCOVERY_WRITE") {
          storedCatalog = ProcoreCore.normalizeDiscoveryCatalog(message.catalog);
          callback({ ok: true, result: { catalog: storedCatalog, isOwner: true } });
        } else callback({ ok: false, error: "Unavailable" });
      }
    }
  };
  const ui = {
    shadowRoot: { host: { hidden: false } },
    setContext() {},
    setTaskHubStatus() {},
    setDiscoveryState() {},
    setLoading() {},
    setData() {},
    setCacheWarning() {},
    setTaskHubSyncing() {},
    setTaskHubSyncResult() {},
    setTaskHubSyncError() {},
    notifyOpenFailure() {}
  };

  vm.runInNewContext(source, {
    window: windowObject,
    document: discoveryDocument(windowObject, companies),
    chrome,
    URL,
    Date,
    Error,
    Promise,
    Event,
    setTimeout,
    TaskHubProviderCore: ProviderCore,
    TaskHubProcoreCore: ProcoreCore,
    TaskHubActivityUI: { mount(value) { callbacks = value; return ui; } }
  });

  await waitFor(() => Boolean(callbacks));
  callbacks.onDiscover({ cancel: false });
  await waitFor(() => storedCatalog.scan.status === "complete");
  assert.equal(windowObject.location.href, "https://app.procore.com/webclients/host/companies/12/tools/opentasks");
  assert.deepEqual(
    storedCatalog.companies.map((company) => [company.id, company.name, company.projects.length]),
    [
      ["12", "Example Company", 1],
      ["13", "Partner Company", 2]
    ]
  );
  assert.equal(storedCatalog.scan.completedCompanies.length, 2);
});

test("Procore content script optionally upgrades to the official API through Task Hub", async () => {
  const messages = [];
  const openedUrls = [];
  const uiState = { contexts: [], data: [], errors: [] };
  let callbacks;
  const queue = {
    scanned_at: "2026-09-04T12:00:00Z",
    identity: { id: 7, person_key: "procore:user:7", full_name: "Pat Builder" },
    warnings: [],
    items: [{
      item_id: 99,
      item_type: "rfi",
      project_id: 34,
      project_name: "Airport",
      number: "RFI-12",
      title: "Confirm routing",
      status: "Open",
      source_url: "https://app.procore.com/34/project/rfi/show/99",
      assignments: []
    }]
  };
  const responses = {
    TASKHUB_STATUS: { connected: true, autoSync: false, user: { id: "taskhub-user" } },
    PROCORE_STATUS: { connected: true, display_name: "Pat Builder" },
    TASKHUB_ACTIVITY_CACHE_READ: null,
    TASKHUB_ACTIVITY_CACHE_WRITE: { cached: true },
    PROCORE_QUEUE: queue
  };
  const chrome = {
    runtime: {
      lastError: null,
      sendMessage(message, callback) {
        messages.push(message);
        callback({ ok: true, result: responses[message.type] });
      }
    }
  };
  const windowObject = {
    location: { href: "https://us02.procore.com/webclients/host/companies/12/projects/34/tools/rfis" },
    open(url) { openedUrls.push(url); },
    setInterval() { return 1; }
  };
  windowObject.top = windowObject;
  const ui = {
    shadowRoot: { host: { hidden: false } },
    setContext(value) { uiState.contexts.push(value); },
    setTaskHubStatus() {},
    setLoading() {},
    setData(value) { uiState.data.push(value); },
    setCacheWarning() {},
    setTaskHubSyncing() {},
    setTaskHubSyncResult() {},
    setTaskHubSyncError() {},
    setError(value) { uiState.errors.push(value); },
    notifyOpenFailure() {}
  };
  vm.runInNewContext(source, {
    window: windowObject,
    document: documentWithRows([visibleRow]),
    chrome,
    URL,
    Date,
    Error,
    Promise,
    setTimeout,
    TaskHubProviderCore: ProviderCore,
    TaskHubProcoreCore: ProcoreCore,
    TaskHubActivityUI: {
      mount(value) { callbacks = value; return ui; }
    }
  });

  await waitFor(() => Boolean(callbacks));
  await waitFor(() => messages.some((message) => message.type === "PROCORE_QUEUE"));
  await waitFor(() => messages.some((message) => message.type === "TASKHUB_ACTIVITY_CACHE_WRITE"));

  assert.equal(uiState.errors.length, 0);
  assert.equal(uiState.data.at(-1).context.provider, "procore");
  assert.equal(uiState.data.at(-1).openQueue[0].title, "Confirm routing");
  assert.ok(messages.some((message) => message.type === "PROCORE_QUEUE"));
  assert.ok(messages.some((message) => message.type === "TASKHUB_ACTIVITY_CACHE_WRITE"));
  assert.match(
    messages.find((message) => message.type === "TASKHUB_ACTIVITY_CACHE_WRITE").key,
    /^taskhub-activity-cache-v1:procore:/
  );
  callbacks.onOpenItem({ sourceUrl: "javascript:alert(1)" });
  callbacks.onOpenItem({ sourceUrl: "https://app.procore.com/34/project/rfi/show/99" });
  assert.deepEqual(openedUrls, ["https://app.procore.com/34/project/rfi/show/99"]);
});
