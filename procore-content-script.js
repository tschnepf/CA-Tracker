(function startProcoreActivityExtension() {
  "use strict";

  if (window.top !== window || window.__TASKHUB_PROCORE_CONTENT_STARTED) return;
  if (!TaskHubProviderCore || TaskHubProviderCore.providerForUrl(window.location.href) !== "procore") return;
  window.__TASKHUB_PROCORE_CONTENT_STARTED = true;

  const CACHE_TTL_MS = 15 * 60 * 1000;
  let pageContext;
  try {
    pageContext = TaskHubProcoreCore.pageContext(window.location.href);
  } catch (_error) {
    return;
  }
  let indexData = null;
  let baseIndexData = null;
  let taskHubStatus = null;
  let procoreStatus = null;
  let scanPromise = null;
  let lastVisibleSignature = "";
  let discoveryCatalog = TaskHubProcoreCore.normalizeDiscoveryCatalog(null);
  let discoveryPromise = null;
  // Taken before Procore's own router can rewrite the URL; see runHelperRefresh.
  const helperRefreshId = typeof TaskHubWindowsHelperSnapshot === "undefined"
    ? null
    : TaskHubWindowsHelperSnapshot.takeRefreshId(window);

  const PROCORE_ITEM_LINK_SELECTOR = [
    'a[href*="/project/rfi/show/"]',
    'a[href*="/project/submittal/"]',
    'a[href*="/project/submittals/"]',
    'a[href*="/project/submittal_log/"]',
    'a[href*="/tools/rfis/"]',
    'a[href*="/tools/submittals/"]'
  ].join(", ");

  function runtimeMessage(message) {
    return new Promise((resolve, reject) => {
      chrome.runtime.sendMessage(message, (response) => {
        if (chrome.runtime.lastError) reject(new Error(chrome.runtime.lastError.message));
        else if (!response || response.ok !== true) reject(new Error(response && response.error || "SMC-CA-Tracker request failed."));
        else resolve(response.result);
      });
    });
  }

  function textContent(node) {
    const value = node && typeof node === "object" && "textContent" in node
      ? node.textContent : node;
    return String(value || "").replace(/\s+/g, " ").trim();
  }

  function delay(milliseconds) {
    return new Promise((resolve) => setTimeout(resolve, milliseconds));
  }

  async function waitFor(predicate, message, timeoutMilliseconds) {
    const deadline = Date.now() + (Number(timeoutMilliseconds) || 15000);
    while (Date.now() < deadline) {
      const value = predicate();
      if (value) return value;
      await delay(100);
    }
    throw new Error(message || "Procore did not finish loading the requested selector.");
  }

  function directCompanyContext() {
    try {
      const context = TaskHubProcoreCore.pageContext(window.location.href);
      if (context.companyId) return context;
    } catch (_error) {
      // Fall through to the visible company-home link.
    }
    const home = document.querySelector('a[href*="/company/home"]');
    const href = String(home && (home.href || home.getAttribute("href")) || "");
    const match = href.match(/\/(\d+)\/company\/home(?:\/|$)/i);
    return { companyId: match ? match[1] : "", projectId: "" };
  }

  function currentCompanyName() {
    const companyImage = document.querySelector('a[href*="/company/home"] img[alt]');
    const imageName = textContent(companyImage && companyImage.getAttribute("alt"));
    if (imageName) return imageName;
    const selectorButton = Array.from(document.querySelectorAll("button"))
      .find((button) => textContent(button).includes("Select a Project"));
    return textContent(selectorButton).replace(/\s*Select a Project\s*$/i, "").trim();
  }

  function projectPickerButton() {
    return Array.from(document.querySelectorAll("button"))
      .find((button) => textContent(button).includes("Select a Project")) || null;
  }

  function visibleProjectLinks() {
    return Array.from(document.querySelectorAll('[role="option"][href*="/project/home"]'));
  }

  async function openProjectPicker() {
    if (visibleProjectLinks().length || document.querySelector("#react_company_select")) return;
    const button = await waitFor(
      () => projectPickerButton(),
      "Procore's project selector is unavailable for this company."
    );
    button.click();
    await waitFor(
      () => visibleProjectLinks().length || document.querySelector("#react_company_select"),
      "Procore did not open the project selector."
    );
  }

  function closeProjectPicker() {
    const button = projectPickerButton();
    if (button && button.getAttribute("aria-expanded") === "true") button.click();
  }

  function projectListScroller() {
    const listbox = document.querySelector('[role="listbox"][aria-label="Projects"]')
      || document.querySelector('[role="listbox"]');
    if (!listbox) return null;
    return [listbox, ...Array.from(listbox.querySelectorAll("*"))]
      .find((node) => Number(node.scrollHeight) > Number(node.clientHeight) + 2) || listbox;
  }

  async function collectCurrentCompanyProjects() {
    if (document.querySelector('[role="menuitem"]')) {
      closeCompanyMenu();
      await waitFor(
        () => !document.querySelector('[role="menuitem"]'),
        "Procore did not close the company selector."
      );
    }
    await openProjectPicker();
    const projects = new Map();
    let previousSize = -1;
    for (let round = 0; round < 120; round += 1) {
      for (const link of visibleProjectLinks()) {
        const href = String(link.href || link.getAttribute("href") || "");
        let absolute;
        try { absolute = new URL(href, window.location.href).href; } catch (_error) { continue; }
        const safeUrl = TaskHubProcoreCore.safeProjectHomeUrl(absolute);
        const match = safeUrl && new URL(safeUrl).pathname.match(/^\/(\d+)\/project\/home\/?$/i);
        const name = textContent(link);
        if (match && name) projects.set(match[1], { id: match[1], name, url: safeUrl });
      }
      const scroller = projectListScroller();
      if (!scroller || Number(scroller.scrollHeight) <= Number(scroller.clientHeight) + 2) break;
      const atBottom = Number(scroller.scrollTop) + Number(scroller.clientHeight) >= Number(scroller.scrollHeight) - 2;
      if (atBottom && projects.size === previousSize) break;
      previousSize = projects.size;
      scroller.scrollTop = Math.min(
        Number(scroller.scrollHeight),
        Number(scroller.scrollTop) + Math.max(120, Number(scroller.clientHeight) * 0.8)
      );
      scroller.dispatchEvent(new Event("scroll", { bubbles: true }));
      await delay(100);
    }
    closeProjectPicker();
    return Array.from(projects.values()).sort((left, right) => left.name.localeCompare(right.name));
  }

  async function openCompanyMenu() {
    if (document.querySelector('[role="menuitem"]')) return;
    await openProjectPicker();
    const changeButton = await waitFor(
      () => document.querySelector("#react_company_select")
        || Array.from(document.querySelectorAll("button")).find((button) => textContent(button) === "Change"),
      "Procore's company selector is unavailable."
    );
    changeButton.click();
    await waitFor(
      () => document.querySelector('[role="menuitem"]'),
      "Procore did not display the invited-company list."
    );
  }

  function closeCompanyMenu() {
    const menu = document.querySelector('[role="menu"]');
    let candidate = menu && menu.parentElement;
    for (let depth = 0; candidate && depth < 7; depth += 1) {
      const closeButton = Array.from(candidate.querySelectorAll("button"))
        .find((button) => textContent(button) === "Close");
      if (closeButton) {
        closeButton.click();
        return;
      }
      candidate = candidate.parentElement;
    }
  }

  async function invitedCompanyNames() {
    await openCompanyMenu();
    const names = Array.from(document.querySelectorAll('[role="menuitem"]'))
      .map((item) => textContent(item)).filter(Boolean);
    if (!names.length) throw new Error("Procore did not expose any invited companies.");
    closeCompanyMenu();
    await delay(150);
    return Array.from(new Set(names));
  }

  async function switchCompany(name) {
    const targetName = textContent(name);
    if (!targetName) throw new Error("A company name is required for discovery.");
    if (currentCompanyName() === targetName) {
      closeCompanyMenu();
      return;
    }
    const previousCompanyId = directCompanyContext().companyId;
    await openCompanyMenu();
    const target = Array.from(document.querySelectorAll('[role="menuitem"]'))
      .find((item) => textContent(item) === targetName);
    if (!target) throw new Error(`${targetName} is no longer available in Procore's company selector.`);
    target.click();
    await waitFor(() => {
      const context = directCompanyContext();
      return currentCompanyName() === targetName
        && context.companyId
        && context.companyId !== previousCompanyId
        && projectPickerButton();
    }, `Procore did not finish switching to ${targetName}.`, 30000);
    await delay(250);
  }

  function gridRows() {
    return Array.from(document.querySelectorAll('[role="row"]')).map((row) => {
      const record = {};
      for (const cell of Array.from(row.querySelectorAll('[role="gridcell"][col-id]'))) {
        const link = cell.querySelector("a[href]");
        record[String(cell.getAttribute("col-id") || "")] = {
          text: String(cell.textContent || "").replace(/\s+/g, " ").trim(),
          href: link ? String(link.href || link.getAttribute("href") || "") : ""
        };
      }
      return record;
    });
  }

  function projectFromPage() {
    const projectId = String(pageContext.projectId || "");
    if (!/^\d+$/.test(projectId)) return { text: "", href: "" };
    const homeLink = document.querySelector(`a[href*="/${projectId}/project/home"]`);
    const image = homeLink && homeLink.querySelector("img[alt]");
    return {
      text: String(image && image.getAttribute("alt") || textContent(homeLink)),
      href: String(homeLink && (homeLink.href || homeLink.getAttribute("href")) || `/${projectId}/project/home`)
    };
  }

  function myOpenItemsContainer() {
    const heading = Array.from(document.querySelectorAll("h1, h2, h3, h4, h5, h6"))
      .find((node) => textContent(node).toLowerCase() === "my open items");
    let candidate = heading && heading.parentElement;
    for (let depth = 0; candidate && depth < 6; depth += 1) {
      if (candidate.querySelector && candidate.querySelector(PROCORE_ITEM_LINK_SELECTOR)) return candidate;
      candidate = candidate.parentElement;
    }
    return null;
  }

  function overviewRows() {
    const container = myOpenItemsContainer();
    if (!container) return [];
    const project = projectFromPage();
    const seen = new Set();
    return Array.from(container.querySelectorAll(PROCORE_ITEM_LINK_SELECTOR)).flatMap((link) => {
      if (link.closest && link.closest('[role="row"]')) return [];
      const href = String(link.href || link.getAttribute("href") || "");
      if (!href || seen.has(href)) return [];

      let candidate = link.parentElement;
      let cells = [];
      for (let depth = 0; candidate && candidate !== container && depth < 6; depth += 1) {
        const children = Array.from(candidate.children || []);
        const titleIndex = children.findIndex((child) => child === link || child.contains(link));
        if (titleIndex > 0 && children.length >= 3 && children.length <= 8) {
          cells = children;
          break;
        }
        candidate = candidate.parentElement;
      }
      if (!cells.length) return [];

      const titleIndex = cells.findIndex((cell) => cell === link || cell.contains(link));
      const hrefLower = href.toLowerCase();
      const itemType = hrefLower.includes("submittal") ? "Submittal" : "RFI";
      const dueDate = cells[titleIndex + 1];
      const status = cells[titleIndex + 2];
      seen.add(href);
      return [{
        item_type: { text: textContent(cells[titleIndex - 1]) || itemType, href: "" },
        title: { text: textContent(link), href },
        due_date: { text: textContent(dueDate), href: "" },
        status: { text: textContent(status), href: "" },
        project_name: project,
        "project.number": project
      }];
    });
  }

  function visibleRows() {
    const rows = gridRows();
    const hrefs = new Set(rows.map((row) => row.title && row.title.href).filter(Boolean));
    for (const row of overviewRows()) {
      if (!hrefs.has(row.title.href)) rows.push(row);
    }
    return rows;
  }

  function localQueue() {
    return TaskHubProcoreCore.visibleQueueFromRows(
      window.location.href,
      visibleRows(),
      new Date().toISOString()
    );
  }

  function queueSignature(queue) {
    return JSON.stringify((queue.items || []).map((item) => [
      item.project_id,
      item.item_type,
      item.item_id,
      item.title,
      item.status,
      item.due_at
    ]));
  }

  function activityCacheKey() {
    const identityId = taskHubStatus && taskHubStatus.user
      && (taskHubStatus.user.id || taskHubStatus.user.email);
    if (!identityId || !pageContext.companyId) return "";
    const scope = TaskHubProviderCore.scopedKey(
      "procore",
      `${pageContext.companyId}:${pageContext.projectId || "all"}`,
      identityId
    );
    return `taskhub-activity-cache-v1:${scope}`;
  }

  async function ensureCompanyContext() {
    if (pageContext.companyId) return pageContext;
    const queue = localQueue();
    if (queue.company_id) {
      pageContext = { companyId: String(queue.company_id), projectId: pageContext.projectId };
      return pageContext;
    }
    if (!pageContext.projectId) return pageContext;
    const resolved = await runtimeMessage({
      type: "PROCORE_CONTEXT_RESOLVE",
      projectId: pageContext.projectId
    });
    const companyId = String(resolved && resolved.companyId || "");
    if (/^\d+$/.test(companyId)) pageContext = { companyId, projectId: pageContext.projectId };
    return pageContext;
  }

  const ui = TaskHubActivityUI.mount({
    onOpen() {
      if (!indexData) void loadQueue(false).catch(() => {});
    },
    onRefresh() {
      void loadQueue(false, true).catch(() => {});
    },
    onDiscover(action) {
      const operation = action && action.cancel ? cancelDiscovery() : startDiscovery();
      void operation.catch((error) => showDiscoveryFailure(error));
    },
    onTaskHubSync() {
      void loadQueue(true, true).catch(() => {});
    },
    onTaskHubSettings() {
      void runtimeMessage({ type: "TASKHUB_ACTIVITY_OPEN_OPTIONS" });
    },
    onOpenItem(item, button) {
      const sourceUrl = TaskHubProcoreCore.safeSourceUrl(item && item.sourceUrl);
      if (!sourceUrl) {
        ui.notifyOpenFailure(button, "Link unavailable");
        return;
      }
      window.open(sourceUrl, "_blank", "noopener");
    }
  });

  function setContext(context) {
    ui.setContext(Object.assign({}, context, {
      provider: "procore",
      itemType: "overview",
      itemLabel: "Item",
      itemLabelPlural: "Items"
    }));
  }

  setContext({ sourceMode: "visible_page" });

  function renderConnectionStatus() {
    const connected = Boolean(taskHubStatus && taskHubStatus.connected);
    ui.setTaskHubStatus({ connected, user: connected ? taskHubStatus.user : null });
  }

  function discoveryCounts(catalogValue) {
    const companies = Array.isArray(catalogValue && catalogValue.companies)
      ? catalogValue.companies : [];
    return {
      companyCount: companies.length,
      projectCount: companies.reduce((count, company) => (
        count + (Array.isArray(company.projects) ? company.projects.length : 0)
      ), 0)
    };
  }

  function showDiscoveryFailure(error) {
    if (typeof ui.setDiscoveryState !== "function") return;
    const counts = discoveryCounts(discoveryCatalog);
    ui.setDiscoveryState(Object.assign({}, counts, {
      status: "error",
      message: error instanceof Error ? error.message : "Company discovery could not start."
    }));
  }

  function renderDiscoveryState() {
    if (typeof ui.setDiscoveryState !== "function") return;
    const counts = discoveryCounts(discoveryCatalog);
    const scan = discoveryCatalog.scan || {};
    let message = scan.message || "";
    if (scan.status === "complete" && !message) {
      message = `${counts.companyCount} companies • ${counts.projectCount} projects`;
    } else if (scan.status === "cancelled" && !message) {
      message = `Discovery cancelled • ${counts.projectCount} projects retained`;
    } else if (scan.status === "error" && !message) {
      message = scan.error || "Company discovery did not finish.";
    }
    ui.setDiscoveryState(Object.assign({}, counts, {
      status: scan.status || "idle",
      message
    }));
  }

  function renderCatalogData() {
    if (!baseIndexData) {
      renderDiscoveryState();
      return;
    }
    indexData = TaskHubProcoreCore.mergeDiscoveryCatalog(baseIndexData, discoveryCatalog);
    ui.setData(indexData);
    renderDiscoveryState();
  }

  async function readDiscoveryCatalog() {
    try {
      const result = await runtimeMessage({ type: "PROCORE_DISCOVERY_READ" });
      discoveryCatalog = TaskHubProcoreCore.normalizeDiscoveryCatalog(result && result.catalog);
      renderCatalogData();
      return result;
    } catch (_error) {
      renderDiscoveryState();
      return { catalog: discoveryCatalog, isOwner: false };
    }
  }

  async function writeDiscoveryCatalog() {
    const result = await runtimeMessage({
      type: "PROCORE_DISCOVERY_WRITE",
      catalog: discoveryCatalog
    });
    discoveryCatalog = TaskHubProcoreCore.normalizeDiscoveryCatalog(result && result.catalog);
    renderCatalogData();
    return result;
  }

  function upsertDiscoveredCompany(company) {
    const normalizedId = String(company && company.id || "");
    const existing = (discoveryCatalog.companies || []).find((entry) => entry.id === normalizedId);
    const priorProjects = new Map((existing && existing.projects || []).map((project) => [project.id, project]));
    const projects = (company.projects || []).map((project) => Object.assign({}, project, {
      scanStatus: priorProjects.get(project.id) && priorProjects.get(project.id).scanStatus || "not_scanned",
      discoveredAt: new Date().toISOString()
    }));
    const nextCompany = Object.assign({}, company, {
      discoveredAt: new Date().toISOString(),
      projects
    });
    discoveryCatalog.companies = [
      ...(discoveryCatalog.companies || []).filter((entry) => entry.id !== normalizedId),
      nextCompany
    ];
  }

  async function enumerateDiscoveryCompanies() {
    const names = await invitedCompanyNames();
    const currentName = currentCompanyName();
    if (currentName && !names.includes(currentName)) names.unshift(currentName);
    discoveryCatalog.scan.phase = "scan";
    discoveryCatalog.scan.pendingCompanies = names;
    discoveryCatalog.scan.completedCompanies = [];
    discoveryCatalog.scan.totalCompanies = names.length;
    discoveryCatalog.scan.currentTarget = "";
    discoveryCatalog.scan.message = `Found ${names.length} invited compan${names.length === 1 ? "y" : "ies"}. Starting project discovery…`;
    await writeDiscoveryCatalog();
  }

  async function scanCurrentDiscoveryCompany(companyName) {
    const companyContext = directCompanyContext();
    const companyId = String(companyContext.companyId || "");
    if (!/^\d+$/.test(companyId)) throw new Error(`Procore did not expose an ID for ${companyName}.`);
    let company;
    try {
      const projects = await collectCurrentCompanyProjects();
      company = {
        id: companyId,
        name: companyName,
        status: "discovered",
        error: "",
        projects
      };
    } catch (error) {
      company = {
        id: companyId,
        name: companyName,
        status: "access_denied",
        error: error instanceof Error ? error.message : "Project discovery was denied.",
        projects: []
      };
    }
    upsertDiscoveredCompany(company);
    discoveryCatalog.scan.pendingCompanies = discoveryCatalog.scan.pendingCompanies
      .filter((name) => name !== companyName);
    discoveryCatalog.scan.completedCompanies = Array.from(new Set([
      ...discoveryCatalog.scan.completedCompanies,
      companyName
    ]));
    discoveryCatalog.scan.currentTarget = "";
    const counts = discoveryCounts(discoveryCatalog);
    discoveryCatalog.scan.message = `Discovered ${discoveryCatalog.scan.completedCompanies.length} of ${discoveryCatalog.scan.totalCompanies} companies • ${counts.projectCount} projects`;
    await writeDiscoveryCatalog();
  }

  async function finishDiscovery() {
    let scan = discoveryCatalog.scan;
    const originalName = scan.originalCompanyName;
    if (originalName && currentCompanyName() !== originalName) {
      scan.phase = "restore";
      scan.currentTarget = originalName;
      scan.message = `Restoring ${originalName}…`;
      await writeDiscoveryCatalog();
      await switchCompany(originalName);
      scan = discoveryCatalog.scan;
    }
    const finalStatus = scan.error ? "error" : scan.cancelRequested ? "cancelled" : "complete";
    const counts = discoveryCounts(discoveryCatalog);
    scan.status = finalStatus;
    scan.phase = "";
    scan.currentTarget = "";
    scan.completedAt = new Date().toISOString();
    scan.message = finalStatus === "complete"
      ? `${counts.companyCount} companies • ${counts.projectCount} projects discovered`
      : finalStatus === "cancelled"
        ? `Discovery cancelled • ${counts.projectCount} projects retained`
        : scan.error;
    await writeDiscoveryCatalog();
    const originalUrl = scan.originalUrl;
    if (originalUrl && window.location.href !== originalUrl && window.location
      && typeof window.location.assign === "function") {
      window.location.assign(originalUrl);
    }
  }

  async function continueDiscovery() {
    if (discoveryPromise) return discoveryPromise;
    discoveryPromise = (async () => {
      try {
        if (discoveryCatalog.scan.phase === "enumerate") await enumerateDiscoveryCompanies();
        while (discoveryCatalog.scan.status === "running"
          && discoveryCatalog.scan.phase === "scan"
          && discoveryCatalog.scan.pendingCompanies.length) {
          if (discoveryCatalog.scan.cancelRequested) break;
          const targetName = discoveryCatalog.scan.pendingCompanies[0];
          discoveryCatalog.scan.currentTarget = targetName;
          discoveryCatalog.scan.message = `Opening ${targetName}…`;
          await writeDiscoveryCatalog();
          if (currentCompanyName() !== targetName) await switchCompany(targetName);
          await scanCurrentDiscoveryCompany(targetName);
        }
        if (discoveryCatalog.scan.status === "running") await finishDiscovery();
      } catch (error) {
        discoveryCatalog.scan.error = error instanceof Error ? error.message : "Company discovery failed.";
        discoveryCatalog.scan.pendingCompanies = [];
        discoveryCatalog.scan.phase = "restore";
        discoveryCatalog.scan.message = `Discovery stopped: ${discoveryCatalog.scan.error}`;
        try {
          await writeDiscoveryCatalog();
          await finishDiscovery();
        } catch (_restoreError) {
          discoveryCatalog.scan.status = "error";
          discoveryCatalog.scan.phase = "";
          discoveryCatalog.scan.completedAt = new Date().toISOString();
          discoveryCatalog.scan.message = discoveryCatalog.scan.error;
          try { await writeDiscoveryCatalog(); } catch (_writeError) { renderDiscoveryState(); }
        }
      } finally {
        discoveryPromise = null;
      }
    })();
    return discoveryPromise;
  }

  async function startDiscovery() {
    const context = directCompanyContext();
    const companyName = currentCompanyName();
    const result = await runtimeMessage({
      type: "PROCORE_DISCOVERY_START",
      origin: {
        url: window.location.href,
        companyId: context.companyId,
        companyName
      }
    });
    discoveryCatalog = TaskHubProcoreCore.normalizeDiscoveryCatalog(result && result.catalog);
    renderCatalogData();
    return continueDiscovery();
  }

  async function cancelDiscovery() {
    const result = await runtimeMessage({ type: "PROCORE_DISCOVERY_CANCEL" });
    discoveryCatalog = TaskHubProcoreCore.normalizeDiscoveryCatalog(result && result.catalog);
    renderCatalogData();
    return continueDiscovery();
  }

  async function loadConnectionStatus() {
    try {
      taskHubStatus = await runtimeMessage({ type: "TASKHUB_STATUS" });
    } catch (_error) {
      taskHubStatus = { connected: false, autoSync: true, user: null };
    }
    try {
      procoreStatus = taskHubStatus.connected
        ? await runtimeMessage({ type: "PROCORE_STATUS" })
        : { connected: false };
    } catch (_error) {
      procoreStatus = { connected: false };
    }
    renderConnectionStatus();
    return taskHubStatus;
  }

  async function readCache() {
    const cacheKey = activityCacheKey();
    if (!cacheKey) return false;
    try {
      const cached = await runtimeMessage({ type: "TASKHUB_ACTIVITY_CACHE_READ", key: cacheKey });
      if (!cached || cached.version !== 1 || cached.provider !== "procore" || !cached.index) return false;
      if (Date.now() - Number(cached.storedAt || 0) > CACHE_TTL_MS) return false;
      baseIndexData = Object.assign({}, cached.index, { cached: true });
      indexData = TaskHubProcoreCore.mergeDiscoveryCatalog(
        baseIndexData,
        discoveryCatalog
      );
      setContext(indexData.context);
      ui.setData(indexData);
      renderDiscoveryState();
      return true;
    } catch (_error) {
      return false;
    }
  }

  async function writeCache(index) {
    const cacheKey = activityCacheKey();
    if (!cacheKey) return;
    try {
      await runtimeMessage({
        type: "TASKHUB_ACTIVITY_CACHE_WRITE",
        key: cacheKey,
        value: { version: 1, provider: "procore", storedAt: Date.now(), index }
      });
      ui.setCacheWarning("");
    } catch (_error) {
      ui.setCacheWarning("Chrome could not save this Procore activity cache. The live results are still available.");
    }
  }

  function renderQueue(queue) {
    baseIndexData = TaskHubProcoreCore.queueIndex(queue);
    indexData = TaskHubProcoreCore.mergeDiscoveryCatalog(
      baseIndexData,
      discoveryCatalog
    );
    setContext(indexData.context);
    ui.setData(indexData);
    renderDiscoveryState();
    return indexData;
  }

  function renderVisibleQueue(force) {
    const queue = localQueue();
    const signature = queueSignature(queue);
    if (force || signature !== lastVisibleSignature) {
      lastVisibleSignature = signature;
      renderQueue(queue);
    }
    if (!pageContext.companyId && queue.company_id) {
      pageContext = { companyId: String(queue.company_id), projectId: pageContext.projectId };
    }
    return queue;
  }

  function helperCatalog(visibleQueue) {
    return TaskHubWindowsHelperSnapshot.buildProcoreCatalog(visibleQueue, discoveryCatalog, window.location.hostname);
  }

  async function sendWindowsHelperSnapshots(visibleQueue) {
    // Optional: visible Procore rows are a partial view, so the helper never treats
    // folders missing from this snapshot as orphaned.
    try {
      if (typeof TaskHubWindowsHelperSnapshot === "undefined" || !visibleQueue) return;
      for (const snapshot of TaskHubWindowsHelperSnapshot.buildProcoreSnapshots(visibleQueue)) {
        await runtimeMessage({ type: "WINDOWS_HELPER_SNAPSHOT", snapshot, catalog: helperCatalog(visibleQueue) });
      }
    } catch (_error) {
      // The Windows helper is optional; Procore stays fully usable without it.
    }
  }

  // Procore renders its grid after the page loads; wait until the row count stops changing.
  function waitForSettledRows() {
    return new Promise((resolve) => {
      const started = Date.now();
      let lastCount = -1;
      let stableSince = started;
      const timer = setInterval(() => {
        const count = visibleRows().length;
        const now = Date.now();
        if (count !== lastCount) {
          lastCount = count;
          stableSince = now;
        }
        const elapsed = now - started;
        const settled = count > 0 && elapsed >= 4000 && now - stableSince >= 3000;
        if (settled || (count === 0 && elapsed >= 20000) || elapsed >= 45000) {
          clearInterval(timer);
          resolve(count);
        }
      }, 500);
    });
  }

  // The CA Tracker app opened this page to refresh a project: claim the request through the
  // native host (which only accepts ids the app created), read the grid, and hand it over.
  // The host then says whether to continue on the Submittals page or finish.
  async function runHelperRefresh(id) {
    let request;
    try {
      request = await runtimeMessage({ type: "WINDOWS_HELPER_REFRESH_CLAIM", id });
    } catch (error) {
      ui.setCacheWarning(`CA Tracker refresh: ${error.message}`);
      return;
    }
    try {
      const expectedTool = Array.isArray(request.itemTypes) && request.itemTypes[0] === "submittal" ? "submittals" : "rfis";
      if (String(pageContext.projectId || "") !== String(request.projectId)
        || !new RegExp(`/tools/${expectedTool}(?:/|$)`, "i").test(window.location.pathname)) {
        throw new Error(`Procore did not open the project's ${expectedTool === "rfis" ? "RFIs" : "Submittals"} list. Check that you are signed in to Procore in Chrome.`);
      }
      await waitForSettledRows();
      const visibleQueue = localQueue();
      const snapshot = TaskHubWindowsHelperSnapshot.buildProcoreProjectSnapshot(visibleQueue, request);
      const result = await runtimeMessage({
        type: "WINDOWS_HELPER_SNAPSHOT",
        snapshot,
        catalog: helperCatalog(visibleQueue),
        refresh: { id, step: Number(request.step) || 0 }
      });
      if (!result || !result.sent) {
        throw new Error(result && result.error || "The CA Tracker app did not receive the data.");
      }
      if (result.refresh && result.refresh.status === "Failed") {
        ui.setCacheWarning(`CA Tracker refresh failed: ${result.refresh.error || "the app rejected this page."}`);
      } else if (result.refresh && result.refresh.nextUrl) {
        window.location.assign(result.refresh.nextUrl);
      }
    } catch (error) {
      try {
        await runtimeMessage({ type: "WINDOWS_HELPER_REFRESH_FAILED", id, error: error.message });
      } catch (_error) {
        // Nothing more to report; the app times the refresh out.
      }
      ui.setCacheWarning(`CA Tracker refresh failed: ${error.message}`);
    }
  }

  async function loadQueue(forceSync, forceRefresh) {
    if (scanPromise) return scanPromise;
    scanPromise = (async () => {
      const visibleQueue = renderVisibleQueue(true);
      void sendWindowsHelperSnapshots(visibleQueue);
      try {
        if (!taskHubStatus || forceRefresh) await loadConnectionStatus();
        if (!taskHubStatus.connected) {
          if (forceSync) ui.setTaskHubSyncError("Connect Task Hub in settings to sync. Procore browsing remains available standalone.");
          return indexData;
        }

        await ensureCompanyContext();
        if (procoreStatus && procoreStatus.connected && !forceRefresh && await readCache()) {
          return indexData;
        }
        const shouldSync = forceSync || taskHubStatus.autoSync !== false;
        if (shouldSync) ui.setTaskHubSyncing(indexData && indexData.openQueue.length || 0);

        if (procoreStatus && procoreStatus.connected && /^\d+$/.test(pageContext.companyId || "")) {
          ui.setLoading("Reading your Procore assignments through the official API…");
          const response = await runtimeMessage({
            type: shouldSync ? "PROCORE_SYNC" : "PROCORE_QUEUE",
            context: pageContext
          });
          const queue = shouldSync ? response && response.queue : response;
          renderQueue(queue);
          await writeCache(indexData);
          if (shouldSync) ui.setTaskHubSyncResult(response, indexData.openQueue.length);
          return indexData;
        }

        if (shouldSync) {
          const response = await runtimeMessage({
            type: "PROCORE_SNAPSHOT_SYNC",
            snapshot: visibleQueue
          });
          ui.setTaskHubSyncResult(response, indexData.openQueue.length);
        }
        return indexData;
      } catch (error) {
        const message = error instanceof Error ? error.message : "Optional Procore sync failed.";
        renderQueue(visibleQueue);
        ui.setCacheWarning(`Standalone Procore results are available. Optional sync failed: ${message}`);
        if (forceSync) ui.setTaskHubSyncError(message);
        return indexData;
      } finally {
        scanPromise = null;
      }
    })();
    return scanPromise;
  }

  function watchRoute() {
    let nextContext;
    try {
      if (TaskHubProviderCore.providerForUrl(window.location.href) !== "procore") throw new Error("Unsupported route");
      nextContext = TaskHubProcoreCore.pageContext(window.location.href);
    } catch (_error) {
      ui.shadowRoot.host.hidden = true;
      return;
    }
    ui.shadowRoot.host.hidden = false;
    if (!nextContext.companyId && nextContext.projectId === pageContext.projectId) {
      nextContext = { companyId: pageContext.companyId, projectId: nextContext.projectId };
    }
    const routeChanged = nextContext.companyId !== pageContext.companyId
      || nextContext.projectId !== pageContext.projectId;
    if (routeChanged) {
      pageContext = nextContext;
      indexData = null;
      baseIndexData = null;
      lastVisibleSignature = "";
      setContext({ sourceMode: "visible_page" });
    }
    const previousSignature = lastVisibleSignature;
    const queue = renderVisibleQueue(routeChanged);
    const rowsChanged = lastVisibleSignature !== previousSignature;
    if (rowsChanged && queue.items.length
      && taskHubStatus && taskHubStatus.connected && taskHubStatus.autoSync !== false
      && !(procoreStatus && procoreStatus.connected)) {
      void loadQueue(false, true).catch(() => {});
    }
  }

  renderVisibleQueue(true);
  void (async () => {
    let refreshId = helperRefreshId;
    if (!refreshId) {
      try {
        refreshId = await runtimeMessage({ type: "WINDOWS_HELPER_REFRESH_TAKE" });
      } catch (_error) {
        refreshId = null;
      }
    }
    if (typeof refreshId === "string" && /^[a-f0-9]{32}$/.test(refreshId)) await runHelperRefresh(refreshId);
  })();
  void readDiscoveryCatalog().then((result) => {
    if (result && result.isOwner && discoveryCatalog.scan.status === "running") {
      return continueDiscovery();
    }
    return null;
  }).catch(() => {});
  void loadConnectionStatus().then(() => loadQueue(false)).catch(() => {});
  window.setInterval(watchRoute, 1000);
})();
