(function attachHelixActivityUI(root, factory) {
  const api = factory(root.HelixActivityCore);
  root.HelixActivityUI = api;
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function createHelixActivityUI(Core) {
  "use strict";

  const STYLES = `
    :host { all: initial; color-scheme: dark; --helix-activity-width: 520px; }
    :host * { font-weight: 400 !important; }
    *, *::before, *::after { box-sizing: border-box; }
    button, input, select { font: inherit; }
    .launcher {
      position: fixed; right: 22px; bottom: 22px; z-index: 2147483645;
      border: 1px solid #92c7ef; border-radius: 999px; padding: 12px 18px;
      color: #06131f; background: #9dd7ff; font: 700 14px/1.2 Inter, Segoe UI, sans-serif;
      box-shadow: 0 10px 30px rgba(0,0,0,.35); cursor: pointer;
    }
    .launcher:hover { background: #c0e7ff; }
    .launcher:focus-visible, button:focus-visible, input:focus-visible, select:focus-visible {
      outline: 3px solid #9dd7ff; outline-offset: 2px;
    }
    .panel {
      position: fixed; inset: 0 0 0 auto; z-index: 2147483646; width: min(var(--helix-activity-width), 100vw);
      display: flex; flex-direction: column; color: #e9f2f8; background: #101a23;
      border-left: 1px solid #334555; box-shadow: -18px 0 45px rgba(0,0,0,.42);
      font: 14px/1.45 Inter, Segoe UI, sans-serif; transform: translateX(102%);
      transition: transform 160ms ease; container-type: inline-size;
    }
    .panel.open { transform: translateX(0); }
    .resize-handle { position: absolute; z-index: 8; inset: 0 auto 0 0; width: 12px; cursor: ew-resize; touch-action: none; }
    .resize-handle::after { content: ""; position: absolute; top: 45%; bottom: 45%; left: 3px; width: 3px; min-height: 42px; border-radius: 2px; background: #4f6e84; transition: background 120ms ease; }
    .resize-handle:hover::after, .resize-handle:focus-visible::after { background: #9dd7ff; }
    .resize-handle:focus-visible { outline: 3px solid #9dd7ff; outline-offset: -3px; }
    .header { padding: 18px 18px 14px; background: linear-gradient(145deg,#183047,#132230); border-bottom: 1px solid #334555; }
    .header-row { display: flex; align-items: flex-start; justify-content: space-between; gap: 14px; }
    h1 { margin: 0; color: #fff; font-size: 21px; line-height: 1.2; }
    .subtitle { margin: 5px 0 0; color: #a9bfd0; font-size: 12px; }
    .close { border: 1px solid #50738d; border-radius: 6px; background: #1d3a50; color: #dff3ff; font-size: 11px; line-height: 1; cursor: pointer; padding: 7px 10px; }
    .privacy { margin-top: 12px; display: flex; gap: 7px; align-items: center; color: #9dd7ff; font-size: 11px; }
    .privacy-dot { width: 7px; height: 7px; border-radius: 50%; background: #57d6a4; box-shadow: 0 0 0 3px rgba(87,214,164,.14); }
    .toolbar { padding: 14px 16px; border-bottom: 1px solid #2c3b48; background: #14212c; }
    .view-switch { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; margin-bottom: 10px; padding: 3px; border-radius: 9px; background: #0e1720; }
    .view-button { border: 0; border-radius: 7px; padding: 8px 10px; color: #9db2c1; background: transparent; cursor: pointer; font-size: 12px; }
    .view-button[aria-pressed="true"] { color: #06131f; background: #9dd7ff; }
    .toolbar-top { display: flex; gap: 8px; }
    .search { flex: 1; min-width: 0; border: 1px solid #425767; border-radius: 7px; padding: 9px 11px; color: #fff; background: #0e1720; }
    .refresh { border: 1px solid #50738d; border-radius: 7px; padding: 9px 12px; color: #dff3ff; background: #1d3a50; cursor: pointer; }
    .filters { margin-top: 10px; display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    select { width: 100%; border: 1px solid #425767; border-radius: 7px; padding: 8px; color: #e9f2f8; background: #0e1720; }
    .queue-filters { margin-top: 10px; display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
    .queue-filters.activity-types { grid-template-columns: 1fr; }
    .filter-menu { position: relative; min-width: 0; }
    .filter-menu summary { list-style: none; border: 1px solid #425767; border-radius: 7px; padding: 8px 28px 8px 10px; color: #e9f2f8; background: #0e1720; cursor: pointer; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .filter-menu summary::-webkit-details-marker { display: none; }
    .filter-menu summary::after { content: "▾"; position: absolute; right: 10px; color: #91a7b7; }
    .filter-popover { position: absolute; z-index: 5; top: calc(100% + 4px); left: 0; right: 0; max-height: 250px; overflow: auto; border: 1px solid #425767; border-radius: 8px; padding: 7px; background: #0e1720; box-shadow: 0 10px 24px rgba(0,0,0,.38); }
    .filter-actions { display: flex; flex-wrap: wrap; gap: 5px; padding-bottom: 6px; border-bottom: 1px solid #2c3b48; margin-bottom: 4px; }
    .filter-action { border: 0; padding: 3px 5px; color: #9dd7ff; background: transparent; cursor: pointer; font-size: 10px; }
    .check-option { display: flex; gap: 7px; align-items: flex-start; padding: 6px 4px; color: #d6e3eb; cursor: pointer; font-size: 11px; }
    .check-option input { flex: none; margin: 2px 0 0; accent-color: #9dd7ff; }
    .due-next { align-items: center; flex-wrap: wrap; }
    .due-days { width: 58px; border: 1px solid #425767; border-radius: 5px; padding: 4px 6px; color: #fff; background: #14212c; }
    .reason-filters { display: flex; gap: 6px; overflow-x: auto; padding: 10px 0 2px; scrollbar-width: thin; }
    .chip { white-space: nowrap; border: 1px solid #425767; border-radius: 999px; padding: 5px 9px; color: #b8c9d5; background: transparent; cursor: pointer; font-size: 11px; }
    .chip[aria-pressed="true"] { color: #07131c; background: #9dd7ff; border-color: #9dd7ff; }
    .summary { padding: 10px 16px; display: flex; justify-content: space-between; gap: 12px; border-bottom: 1px solid #2c3b48; color: #a9bfd0; font-size: 12px; }
    .summary strong { color: #fff; }
    .warnings { margin: 10px 16px 0; padding: 9px 10px; border: 1px solid #8a6634; border-radius: 7px; color: #ffdba6; background: #342716; font-size: 12px; }
    .state { flex: 1; display: grid; place-items: center; padding: 32px; text-align: center; color: #a9bfd0; }
    .state strong { display: block; color: #eef7fd; font-size: 16px; margin-bottom: 5px; }
    .spinner { width: 28px; height: 28px; margin: 0 auto 14px; border: 3px solid #2c4354; border-top-color: #9dd7ff; border-radius: 50%; animation: spin .8s linear infinite; }
    @keyframes spin { to { transform: rotate(360deg); } }
    .results { flex: 1; overflow: auto; padding: 12px 14px 24px; }
    .card { border: 1px solid #2f4352; border-radius: 10px; padding: 13px; margin-bottom: 10px; background: #15232e; }
    .card-top { display: flex; justify-content: space-between; gap: 10px; align-items: flex-start; }
    .item-identity { display: flex; flex-direction: column; align-items: flex-start; gap: 3px; }
    .item-type, .number { color: #9dd7ff; letter-spacing: .02em; }
    .status-badge { flex: none; border: 1px solid #476074; border-radius: 999px; padding: 3px 7px; color: #c7d8e4; font-size: 10px; text-transform: uppercase; letter-spacing: .05em; }
    .badge-stack { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 5px; }
    .queue-badge { border-radius: 999px; padding: 4px 8px; font-size: 10px; text-transform: uppercase; letter-spacing: .04em; }
    .queue-badge.current { color: #241300; background: #ffbe69; }
    .queue-badge.upcoming { color: #071b29; background: #80d4ff; }
    .title { margin: 6px 0 4px; color: #fff; }
    .contract { color: #91a7b7; font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .item-context { display: flex; flex-wrap: wrap; gap: 5px; margin: 7px 0 0; color: #b9cfdd; font-size: 11px; }
    .reason-list { display: flex; flex-wrap: wrap; gap: 5px; margin-top: 10px; }
    .reason { border-radius: 4px; padding: 3px 6px; color: #bce6ff; background: #203b4d; font-size: 10px; }
    .status-column { display: flex; align-items: center; margin-top: 9px; }
    .card-bottom { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-top: 11px; }
    .date { color: #91a7b7; font-size: 11px; }
    .open-rfi { border: 1px solid #50738d; border-radius: 6px; padding: 6px 9px; color: #e7f6ff; background: #1d3a50; cursor: pointer; font-size: 11px; }
    .more { display: block; width: 100%; border: 1px solid #50738d; border-radius: 8px; padding: 10px; color: #dff3ff; background: #1d3a50; cursor: pointer; }
    .hidden { display: none !important; }
    @container (min-width: 720px) {
      .toolbar { display: grid; grid-template-columns: 170px minmax(210px, 1.5fr) minmax(240px, 1fr); gap: 8px 10px; align-items: start; padding: 11px 16px; }
      .view-switch { grid-column: 1; margin-bottom: 0; }
      .toolbar-top { grid-column: 2; }
      .filters { grid-column: 3; margin-top: 0; }
      .queue-filters, .reason-filters { grid-column: 1 / -1; margin-top: 0; }
      .queue-filters { grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); }
      .queue-filters.activity-types { grid-template-columns: 1fr; }
      .reason-filters { padding-top: 0; }
      .results { padding: 9px 11px 18px; }
      .card {
        display: grid;
        grid-template-columns: minmax(85px, .65fr) minmax(140px, 1.8fr) minmax(110px, 1.1fr) minmax(135px, 1.5fr) minmax(110px, .8fr);
        grid-template-areas: "top title contract reasons bottom" "top context contract reasons bottom";
        gap: 4px 12px; align-items: center; padding: 9px 11px; margin-bottom: 7px;
      }
      .queue-card {
        grid-template-columns: minmax(80px, .65fr) minmax(130px, 1.8fr) minmax(95px, 1.1fr) minmax(120px, 1.5fr) minmax(75px, .7fr) minmax(100px, .8fr);
        grid-template-areas: "top title contract reasons status bottom" "top context contract reasons status bottom";
      }
      .card-top { grid-area: top; flex-direction: column; align-items: flex-start; gap: 4px; }
      .badge-stack { justify-content: flex-start; }
      .title { grid-area: title; margin: 0; line-height: 1.25; }
      .contract { grid-area: contract; }
      .item-context { grid-area: context; margin: 0; }
      .reason-list { grid-area: reasons; margin-top: 0; }
      .status-column { grid-area: status; margin-top: 0; }
      .card-bottom { grid-area: bottom; flex-direction: column; align-items: flex-end; margin-top: 0; gap: 4px; text-align: right; }
    }
    @container (min-width: 980px) {
      .card { grid-template-columns: minmax(105px, .7fr) minmax(210px, 2fr) minmax(155px, 1.2fr) minmax(220px, 1.7fr) minmax(140px, .8fr); }
      .queue-card { grid-template-columns: minmax(105px, .7fr) minmax(190px, 2fr) minmax(135px, 1.2fr) minmax(190px, 1.7fr) minmax(90px, .7fr) minmax(130px, .8fr); }
    }
    @media (max-width: 420px) { .resize-handle { display: none; } }
    @media (prefers-reduced-motion: reduce) { .panel { transition: none; } .spinner { animation: none; } }
  `;

  function element(documentObject, tag, className, text) {
    const node = documentObject.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  function mount(options) {
    if (!Core) {
      throw new Error("The activity core must be loaded before the UI.");
    }
    const callbacks = options || {};
    const documentObject = callbacks.document || document;
    const existing = documentObject.getElementById("helix-activity-extension-root");
    if (existing) {
      return existing.__helixActivityUi;
    }

    const host = documentObject.createElement("div");
    host.id = "helix-activity-extension-root";
    const shadow = host.attachShadow({ mode: "open" });
    const style = documentObject.createElement("style");
    style.textContent = STYLES;
    shadow.appendChild(style);

    const launcher = element(documentObject, "button", "launcher", "My Helix Activity");
    launcher.type = "button";
    launcher.setAttribute("aria-expanded", "false");
    shadow.appendChild(launcher);

    const panel = element(documentObject, "aside", "panel");
    panel.setAttribute("role", "dialog");
    panel.setAttribute("aria-label", "My Helix Activity");
    panel.setAttribute("aria-hidden", "true");
    shadow.appendChild(panel);

    const resizeHandle = element(documentObject, "div", "resize-handle");
    resizeHandle.tabIndex = 0;
    resizeHandle.setAttribute("role", "separator");
    resizeHandle.setAttribute("aria-orientation", "vertical");
    resizeHandle.setAttribute("aria-label", "Resize activity sidebar");
    resizeHandle.title = "Drag to resize; double-click to reset";
    panel.appendChild(resizeHandle);

    const header = element(documentObject, "header", "header");
    const headerRow = element(documentObject, "div", "header-row");
    const headingWrap = element(documentObject, "div");
    const heading = element(documentObject, "h1", "", "My Helix Activity");
    const subtitle = element(documentObject, "p", "subtitle", "Waiting for Helix…");
    headingWrap.append(heading, subtitle);
    const close = element(documentObject, "button", "close", "Hide");
    close.type = "button";
    close.setAttribute("aria-label", "Hide My Helix Activity");
    headerRow.append(headingWrap, close);
    const privacy = element(documentObject, "div", "privacy");
    privacy.append(element(documentObject, "span", "privacy-dot"), element(documentObject, "span", "", "Read-only • Current login • Cached locally"));
    header.append(headerRow, privacy);
    panel.appendChild(header);

    const toolbar = element(documentObject, "section", "toolbar");
    const viewSwitch = element(documentObject, "div", "view-switch");
    const activityView = element(documentObject, "button", "view-button", "My activity");
    activityView.type = "button";
    activityView.setAttribute("aria-pressed", "true");
    const queueView = element(documentObject, "button", "view-button", "Open queue");
    queueView.type = "button";
    queueView.setAttribute("aria-pressed", "false");
    viewSwitch.append(activityView, queueView);
    const toolbarTop = element(documentObject, "div", "toolbar-top");
    const search = element(documentObject, "input", "search");
    search.type = "search";
    search.placeholder = "Search number, title, contract, activity…";
    search.setAttribute("aria-label", "Search indexed Helix items");
    const refresh = element(documentObject, "button", "refresh", "Refresh");
    refresh.type = "button";
    toolbarTop.append(search, refresh);
    const filters = element(documentObject, "div", "filters");
    const status = element(documentObject, "select");
    status.setAttribute("aria-label", "Filter by status");
    const allStatus = element(documentObject, "option", "", "All statuses");
    allStatus.value = "all";
    status.appendChild(allStatus);
    const sort = element(documentObject, "select");
    sort.setAttribute("aria-label", "Sort Helix items");
    [["latest", "Priority / latest"], ["project", "Project"], ["type", "Type"], ["number", "Item number"], ["title", "Title"]].forEach(([value, label]) => {
      const option = element(documentObject, "option", "", label);
      option.value = value;
      sort.appendChild(option);
    });
    filters.append(status, sort);
    const queueFilters = element(documentObject, "div", "queue-filters hidden");
    const projectMenu = element(documentObject, "details", "filter-menu");
    const projectSummary = element(documentObject, "summary", "", "All projects");
    const projectPopover = element(documentObject, "div", "filter-popover");
    const projectActions = element(documentObject, "div", "filter-actions");
    const selectAllProjects = element(documentObject, "button", "filter-action", "Select all");
    selectAllProjects.type = "button";
    const clearProjects = element(documentObject, "button", "filter-action", "Clear");
    clearProjects.type = "button";
    projectActions.append(selectAllProjects, clearProjects);
    const projectOptions = element(documentObject, "div");
    projectPopover.append(projectActions, projectOptions);
    projectMenu.append(projectSummary, projectPopover);
    const typeMenu = element(documentObject, "details", "filter-menu");
    const typeSummary = element(documentObject, "summary", "", "All types");
    const typePopover = element(documentObject, "div", "filter-popover");
    [["rfi", "RFI"], ["submittal", "Submittal"]].forEach(([value, label]) => {
      const option = element(documentObject, "label", "check-option");
      const input = element(documentObject, "input");
      input.type = "checkbox";
      input.value = value;
      input.checked = true;
      option.append(input, documentObject.createTextNode(label));
      typePopover.appendChild(option);
    });
    typeMenu.append(typeSummary, typePopover);
    const peopleMenu = element(documentObject, "details", "filter-menu");
    const peopleSummary = element(documentObject, "summary", "", "Me");
    const peoplePopover = element(documentObject, "div", "filter-popover");
    const peopleActions = element(documentObject, "div", "filter-actions");
    const selectOnlyMe = element(documentObject, "button", "filter-action", "Only me");
    selectOnlyMe.type = "button";
    const selectCompanyPeople = element(documentObject, "button", "filter-action", "All people in my company");
    selectCompanyPeople.type = "button";
    const selectAllPeople = element(documentObject, "button", "filter-action", "All people");
    selectAllPeople.type = "button";
    peopleActions.append(selectOnlyMe, selectCompanyPeople, selectAllPeople);
    const peopleOptions = element(documentObject, "div");
    peoplePopover.append(peopleActions, peopleOptions);
    peopleMenu.append(peopleSummary, peoplePopover);
    const queueTiming = element(documentObject, "select");
    queueTiming.setAttribute("aria-label", "Filter queue timing");
    [["current", "In court now"], ["all", "Current + future"], ["upcoming", "Future only"]].forEach(([value, label]) => {
      const option = element(documentObject, "option", "", label);
      option.value = value;
      queueTiming.appendChild(option);
    });
    const dueMenu = element(documentObject, "details", "filter-menu");
    const dueSummary = element(documentObject, "summary", "", "Any due date");
    const duePopover = element(documentObject, "div", "filter-popover");
    const dueActions = element(documentObject, "div", "filter-actions");
    const clearDueRanges = element(documentObject, "button", "filter-action", "Any date");
    clearDueRanges.type = "button";
    dueActions.appendChild(clearDueRanges);
    const dueOptions = element(documentObject, "div");
    [["overdue", "Overdue"], ["today", "Due today"]].forEach(([value, label]) => {
      const option = element(documentObject, "label", "check-option");
      const input = element(documentObject, "input");
      input.type = "checkbox";
      input.value = value;
      option.append(input, documentObject.createTextNode(label));
      dueOptions.appendChild(option);
    });
    const nextDueOption = element(documentObject, "label", "check-option due-next");
    const nextDueCheck = element(documentObject, "input");
    nextDueCheck.type = "checkbox";
    nextDueCheck.value = "next";
    const dueDays = element(documentObject, "input", "due-days");
    dueDays.type = "number";
    dueDays.min = "1";
    dueDays.max = "365";
    dueDays.value = "7";
    dueDays.setAttribute("aria-label", "Number of future due days");
    nextDueOption.append(nextDueCheck, documentObject.createTextNode("Next"), dueDays, documentObject.createTextNode("days"));
    dueOptions.appendChild(nextDueOption);
    duePopover.append(dueActions, dueOptions);
    dueMenu.append(dueSummary, duePopover);
    queueFilters.append(projectMenu, typeMenu, peopleMenu, queueTiming, dueMenu);
    const reasonFilters = element(documentObject, "div", "reason-filters");
    const allReasons = element(documentObject, "button", "chip", "All activity");
    allReasons.type = "button";
    allReasons.dataset.reason = "all";
    allReasons.setAttribute("aria-pressed", "true");
    reasonFilters.appendChild(allReasons);
    Core.REASON_DEFINITIONS.forEach((reason) => {
      const chip = element(documentObject, "button", "chip", reason.label);
      chip.type = "button";
      chip.dataset.reason = reason.code;
      chip.setAttribute("aria-pressed", "false");
      reasonFilters.appendChild(chip);
    });
    toolbar.append(viewSwitch, toolbarTop, filters, queueFilters, reasonFilters);
    panel.appendChild(toolbar);

    const summary = element(documentObject, "div", "summary hidden");
    const summaryCount = element(documentObject, "span");
    const summaryDate = element(documentObject, "span");
    summary.append(summaryCount, summaryDate);
    panel.appendChild(summary);
    const warnings = element(documentObject, "div", "warnings hidden");
    panel.appendChild(warnings);
    const state = element(documentObject, "div", "state");
    state.append(element(documentObject, "div", "", "Open the panel to build your read-only activity index."));
    panel.appendChild(state);
    const results = element(documentObject, "main", "results hidden");
    panel.appendChild(results);

    let indexData = null;
    let visibleLimit = 100;
    let itemLabel = "Item";
    let itemLabelPlural = "Items";
    let activityTitle = "My Helix Activity";
    let currentView = "activity";
    let contextItemType = null;
    let currentPersonKey = null;
    let panelIsOpen = false;
    let panelWidth = 520;
    const selectedReasons = new Set();
    const selectedProjects = new Set();
    const selectedTypes = new Set(["rfi", "submittal"]);
    const selectedPeople = new Set();
    let selectedPeopleScope = "only-me";
    const selectedDueModes = new Set();
    let pendingViewState = null;

    function stringArray(value, maximum) {
      if (!Array.isArray(value)) return [];
      return Array.from(new Set(value.slice(0, maximum || 500).map((item) => String(item))));
    }

    function normalizedViewState(value) {
      const source = value && typeof value === "object" ? value : {};
      const reasonCodes = new Set(Core.REASON_DEFINITIONS.map((reason) => reason.code));
      return {
        view: source.view === "queue" ? "queue" : "activity",
        search: typeof source.search === "string" ? source.search.slice(0, 500) : "",
        status: typeof source.status === "string" ? source.status.slice(0, 100) : "all",
        sort: ["latest", "project", "type", "number", "title"].includes(source.sort) ? source.sort : "latest",
        reasons: stringArray(source.reasons).filter((reason) => reasonCodes.has(reason)),
        projects: stringArray(source.projects),
        types: stringArray(source.types).filter((type) => ["rfi", "submittal"].includes(type)),
        people: stringArray(source.people),
        peopleScope: ["only-me", "company", "all", "custom"].includes(source.peopleScope)
          ? source.peopleScope
          : null,
        queueTiming: ["current", "all", "upcoming"].includes(source.queueTiming) ? source.queueTiming : "current",
        dueModes: stringArray(source.dueModes).filter((mode) => ["overdue", "today", "next"].includes(mode)),
        dueDays: Number.isFinite(Number(source.dueDays))
          ? Math.max(1, Math.min(365, Math.round(Number(source.dueDays))))
          : 7
      };
    }

    function widthLimit() {
      const view = documentObject.defaultView;
      const viewportWidth = view && Number(view.innerWidth) ? Number(view.innerWidth) : 1200;
      return Math.max(360, Math.min(1200, viewportWidth));
    }

    function setPanelWidth(value, notify) {
      const numeric = Number(value);
      if (!Number.isFinite(numeric)) return panelWidth;
      panelWidth = Math.round(Math.max(360, Math.min(widthLimit(), numeric)));
      host.style.setProperty("--helix-activity-width", `${panelWidth}px`);
      resizeHandle.setAttribute("aria-valuemin", "360");
      resizeHandle.setAttribute("aria-valuemax", String(widthLimit()));
      resizeHandle.setAttribute("aria-valuenow", String(panelWidth));
      if (notify && typeof callbacks.onWidthChange === "function") callbacks.onWidthChange(panelWidth);
      return panelWidth;
    }

    function updateQueueFilterLabels() {
      const allProjects = Array.isArray(indexData && indexData.projects) ? indexData.projects : [];
      projectSummary.textContent = selectedProjects.size === allProjects.length && allProjects.length > 0
        ? `All projects (${allProjects.length})`
        : `${selectedProjects.size} of ${allProjects.length} projects`;
      typeSummary.textContent = selectedTypes.size === 2 ? "All types" : selectedTypes.size === 1
        ? (selectedTypes.has("rfi") ? "RFI only" : "Submittals only")
        : "No types";
      const allPeople = Array.isArray(indexData && indexData.people) ? indexData.people : [];
      peopleSummary.textContent = selectedPeopleScope === "only-me"
        ? "Only me"
        : selectedPeopleScope === "company"
          ? `All people in my company (${selectedPeople.size})`
          : selectedPeopleScope === "all"
            ? `All people (${allPeople.length})`
            : `${selectedPeople.size} people`;
      const dueLabels = [];
      if (selectedDueModes.has("overdue")) dueLabels.push("Overdue");
      if (selectedDueModes.has("today")) dueLabels.push("Today");
      if (selectedDueModes.has("next")) dueLabels.push(`Next ${normalizedDueDays()} days`);
      const dueLabel = dueLabels.length ? dueLabels.join(" + ") : "Any due date";
      dueSummary.textContent = dueLabel;
      dueSummary.title = dueLabel;
    }

    function normalizedDueDays() {
      const value = Number(dueDays.value);
      return Number.isFinite(value) ? Math.max(1, Math.min(365, Math.round(value))) : 7;
    }

    function currentViewState() {
      return {
        view: currentView,
        search: search.value,
        status: status.value,
        sort: sort.value,
        reasons: Array.from(selectedReasons),
        projects: Array.from(selectedProjects),
        types: Array.from(selectedTypes),
        people: Array.from(selectedPeople),
        peopleScope: selectedPeopleScope,
        queueTiming: queueTiming.value,
        dueModes: Array.from(selectedDueModes),
        dueDays: normalizedDueDays()
      };
    }

    function notifyViewStateChange() {
      if (!indexData) pendingViewState = normalizedViewState(currentViewState());
      if (typeof callbacks.onViewStateChange === "function") {
        callbacks.onViewStateChange(currentViewState());
      }
    }

    function selectHasValue(select, value) {
      return Array.from(select.querySelectorAll("option")).some((option) => option.value === value);
    }

    function applyViewState(value, shouldRender) {
      const restored = normalizedViewState(value);
      currentView = restored.view;
      search.value = restored.search;
      status.value = selectHasValue(status, restored.status) ? restored.status : "all";
      sort.value = restored.sort;
      queueTiming.value = restored.queueTiming;
      dueDays.value = String(restored.dueDays);

      selectedReasons.clear();
      restored.reasons.forEach((reason) => selectedReasons.add(reason));
      selectedTypes.clear();
      restored.types.forEach((type) => selectedTypes.add(type));
      selectedDueModes.clear();
      restored.dueModes.forEach((mode) => selectedDueModes.add(mode));

      const projectIds = indexData
        ? new Set((indexData.projects || []).map((project) => String(project.id)))
        : null;
      selectedProjects.clear();
      restored.projects.forEach((projectId) => {
        if (!projectIds || projectIds.has(projectId)) selectedProjects.add(projectId);
      });

      const peopleKeys = indexData
        ? new Set((indexData.people || []).map((person) => String(person.key)))
        : null;
      selectedPeople.clear();
      selectedPeopleScope = restored.peopleScope || "custom";
      if (indexData && selectedPeopleScope !== "custom") {
        selectPeopleScope(selectedPeopleScope);
      } else {
        restored.people.forEach((personKey) => {
          if (!peopleKeys || peopleKeys.has(personKey)) selectedPeople.add(personKey);
        });
        if (indexData && !restored.peopleScope) selectedPeopleScope = inferPeopleScope();
      }

      activityView.setAttribute("aria-pressed", String(currentView === "activity"));
      queueView.setAttribute("aria-pressed", String(currentView === "queue"));
      syncProjectChecks();
      syncTypeChecks();
      syncPeopleChecks();
      syncDueChecks();
      updateReasonButtons();
      updateViewControls();
      visibleLimit = 100;
      if (shouldRender && indexData) render();
      return currentViewState();
    }

    function syncProjectChecks() {
      projectOptions.querySelectorAll('input[type="checkbox"]').forEach((input) => {
        input.checked = selectedProjects.has(input.value);
      });
      updateQueueFilterLabels();
    }

    function syncPeopleChecks() {
      peopleOptions.querySelectorAll('input[type="checkbox"]').forEach((input) => {
        input.checked = selectedPeople.has(input.value);
      });
      selectCompanyPeople.disabled = companyPeopleKeys().size === 0;
      selectCompanyPeople.title = selectCompanyPeople.disabled
        ? "Company information is unavailable for the current Helix user"
        : "Select everyone in your company who appears in the open queue";
      updateQueueFilterLabels();
    }

    function sameSet(left, right) {
      return left.size === right.size && Array.from(left).every((value) => right.has(value));
    }

    function companyPeopleKeys() {
      return new Set(Core.companyPersonKeys(indexData && indexData.people, currentPersonKey));
    }

    function inferPeopleScope() {
      const onlyMe = new Set(currentPersonKey ? [currentPersonKey] : []);
      const allPeople = new Set((indexData && indexData.people || []).map((person) => String(person.key)));
      const companyPeople = companyPeopleKeys();
      if (sameSet(selectedPeople, onlyMe)) return "only-me";
      if (sameSet(selectedPeople, allPeople)) return "all";
      if (companyPeople.size > 0 && sameSet(selectedPeople, companyPeople)) return "company";
      return "custom";
    }

    function selectPeopleScope(scope) {
      selectedPeople.clear();
      selectedPeopleScope = scope;
      if (scope === "only-me") {
        if (currentPersonKey) selectedPeople.add(currentPersonKey);
      } else if (scope === "company") {
        companyPeopleKeys().forEach((key) => selectedPeople.add(key));
      } else if (scope === "all") {
        (indexData && indexData.people || []).forEach((person) => selectedPeople.add(String(person.key)));
      }
    }

    function syncTypeChecks() {
      typePopover.querySelectorAll('input[type="checkbox"]').forEach((input) => {
        input.checked = selectedTypes.has(input.value);
      });
      updateQueueFilterLabels();
    }

    function syncDueChecks() {
      dueOptions.querySelectorAll('input[type="checkbox"]').forEach((input) => {
        input.checked = selectedDueModes.has(input.value);
      });
      updateQueueFilterLabels();
    }

    function updateViewControls() {
      const queueMode = currentView === "queue";
      const latestOption = sort.querySelector('option[value="latest"]');
      if (latestOption) latestOption.textContent = queueMode ? "Priority / due date" : "Latest activity";
      search.placeholder = queueMode
        ? "Search open RFIs and Submittals…"
        : `Search ${itemLabel.toLocaleLowerCase()} number, title, contract, activity…`;
      search.setAttribute("aria-label", queueMode ? "Search open RFIs and Submittals" : `Search indexed ${itemLabelPlural}`);
    }

    function setPanelOpen(open) {
      const nextOpen = Boolean(open);
      const changed = nextOpen !== panelIsOpen;
      panelIsOpen = nextOpen;
      panel.classList.toggle("open", nextOpen);
      panel.setAttribute("aria-hidden", nextOpen ? "false" : "true");
      launcher.setAttribute("aria-expanded", nextOpen ? "true" : "false");
      if (nextOpen) {
        if (typeof callbacks.onOpen === "function") callbacks.onOpen();
        close.focus();
      } else {
        launcher.focus();
      }
      if (changed && typeof callbacks.onPanelChange === "function") callbacks.onPanelChange(nextOpen);
    }

    function setState(title, message, spinning) {
      state.replaceChildren();
      const wrap = element(documentObject, "div");
      if (spinning) wrap.appendChild(element(documentObject, "div", "spinner"));
      wrap.append(element(documentObject, "strong", "", title), element(documentObject, "span", "", message || ""));
      state.appendChild(wrap);
      state.classList.remove("hidden");
      results.classList.add("hidden");
      summary.classList.add("hidden");
    }

    function filtersValue() {
      const value = {
        search: search.value,
        status: status.value,
        sort: sort.value,
        reasons: Array.from(selectedReasons)
      };
      if (currentView === "queue") {
        value.projects = Array.from(selectedProjects);
        value.types = Array.from(selectedTypes);
        value.people = Array.from(selectedPeople);
        value.states = queueTiming.value === "all" ? ["current", "upcoming"] : [queueTiming.value];
        value.dueModes = Array.from(selectedDueModes);
        value.dueDays = normalizedDueDays();
      } else if (contextItemType === "overview") {
        value.types = Array.from(selectedTypes);
      }
      return value;
    }

    function render() {
      if (!indexData) return;
      const queueMode = currentView === "queue";
      const filtered = queueMode
        ? Core.filterOpenQueue(indexData.openQueue || [], filtersValue())
        : Core.filterResults(indexData.results, filtersValue());
      const selectedQueue = queueMode ? filtered : Core.filterOpenQueue(indexData.openQueue || [], {
        projects: Array.from(selectedProjects),
        types: Array.from(selectedTypes),
        people: Array.from(selectedPeople),
        states: queueTiming.value === "all" ? ["current", "upcoming"] : [queueTiming.value],
        dueModes: Array.from(selectedDueModes),
        dueDays: normalizedDueDays()
      });
      launcher.textContent = selectedQueue.length > 0 ? `${activityTitle} • ${selectedQueue.length} open` : activityTitle;
      queueView.textContent = `Open queue (${selectedQueue.length})`;
      reasonFilters.classList.toggle("hidden", queueMode);
      queueFilters.classList.toggle("hidden", !queueMode && contextItemType !== "overview");
      queueFilters.classList.toggle("activity-types", !queueMode);
      projectMenu.classList.toggle("hidden", !queueMode);
      peopleMenu.classList.toggle("hidden", !queueMode);
      queueTiming.classList.toggle("hidden", !queueMode);
      dueMenu.classList.toggle("hidden", !queueMode);
      results.replaceChildren();
      state.classList.add("hidden");
      results.classList.remove("hidden");
      summary.classList.remove("hidden");
      if (queueMode) {
        const currentCount = filtered.filter((item) => item.queueState && item.queueState.code === "current").length;
        const upcomingCount = filtered.filter((item) => item.queueState && item.queueState.code === "upcoming").length;
        summaryCount.replaceChildren(
          element(documentObject, "strong", "", `${currentCount} in court`),
          documentObject.createTextNode(` • ${upcomingCount} coming up`)
        );
      } else {
        summaryCount.replaceChildren(
          documentObject.createTextNode("Showing "),
          element(documentObject, "strong", "", String(filtered.length)),
          documentObject.createTextNode(` of ${indexData.results.length} ${itemLabelPlural}`)
        );
      }
      summaryDate.textContent = indexData.scannedAt
        ? `${indexData.cached ? "Cached" : "Indexed"} ${Core.formatDate(indexData.scannedAt)}`
        : "";

      if (filtered.length === 0) {
        const empty = element(documentObject, "div", "state");
        empty.append(element(documentObject, "div", "", queueMode
          ? "No open items match the selected people and filters."
          : `No ${itemLabelPlural} match these filters.`));
        results.appendChild(empty);
        return;
      }

      filtered.slice(0, visibleLimit).forEach((item) => {
        const card = element(documentObject, "article", `card ${queueMode ? "queue-card" : "activity-card"}`);
        const cardTop = element(documentObject, "div", "card-top");
        const itemIdentity = element(documentObject, "div", "item-identity");
        itemIdentity.append(
          element(documentObject, "span", "item-type", item.itemType === "submittal" ? "Submittal" : "RFI"),
          element(documentObject, "div", "number", item.number)
        );
        const badgeStack = element(documentObject, "div", "badge-stack");
        if (queueMode && item.queueState) {
          badgeStack.appendChild(element(documentObject, "span", `queue-badge ${item.queueState.code}`, item.queueState.label));
        }
        if (!queueMode) badgeStack.appendChild(element(documentObject, "span", "status-badge", item.status));
        cardTop.append(itemIdentity, badgeStack);
        const titleNode = element(documentObject, "div", "title", item.title);
        const contract = element(documentObject, "div", "contract", item.contract || "No contract listed");
        contract.title = item.contract || "No contract listed";
        const itemContext = element(documentObject, "div", "item-context");
        if (queueMode) {
          itemContext.appendChild(element(documentObject, "span", "", item.projectName || item.projectId || "Unknown project"));
        }
        const reasonList = element(documentObject, "div", "reason-list");
        if (queueMode && item.queueState) {
          Core.queueReasonLabels(item).forEach((label) => {
            reasonList.appendChild(element(documentObject, "span", "reason", label));
          });
        } else {
          item.reasons.forEach((reason) => {
            reasonList.appendChild(element(documentObject, "span", "reason", `${reason.label}${reason.count > 1 ? ` ×${reason.count}` : ""}`));
          });
        }
        const statusColumn = queueMode ? element(documentObject, "div", "status-column") : null;
        if (statusColumn) statusColumn.appendChild(element(documentObject, "span", "status-badge", item.status));
        const cardBottom = element(documentObject, "div", "card-bottom");
        if (queueMode) {
          const dateText = item.queueState && item.queueState.dueAt
            ? `Target: ${Core.formatDate(item.queueState.dueAt)}`
            : "Target date unavailable";
          cardBottom.appendChild(element(documentObject, "span", "date", dateText));
        }
        const openButton = element(documentObject, "button", "open-rfi", "Open in Helix");
        openButton.type = "button";
        openButton.addEventListener("click", () => {
          if (typeof callbacks.onOpenItem === "function") callbacks.onOpenItem(item, openButton);
        });
        cardBottom.appendChild(openButton);
        card.append(cardTop, titleNode, contract);
        if (queueMode) card.appendChild(itemContext);
        card.appendChild(reasonList);
        if (statusColumn) card.appendChild(statusColumn);
        card.appendChild(cardBottom);
        results.appendChild(card);
      });

      if (filtered.length > visibleLimit) {
        const more = element(documentObject, "button", "more", `Show ${Math.min(100, filtered.length - visibleLimit)} more`);
        more.type = "button";
        more.addEventListener("click", () => {
          visibleLimit += 100;
          render();
        });
        results.appendChild(more);
      }
    }

    function updateReasonButtons() {
      reasonFilters.querySelectorAll("button[data-reason]").forEach((button) => {
        const reason = button.dataset.reason;
        button.setAttribute("aria-pressed", reason === "all" ? String(selectedReasons.size === 0) : String(selectedReasons.has(reason)));
      });
    }

    let dragState = null;
    resizeHandle.addEventListener("pointerdown", (eventValue) => {
      if (eventValue.button !== undefined && eventValue.button !== 0) return;
      dragState = {
        pointerId: eventValue.pointerId,
        startX: eventValue.clientX,
        startWidth: panel.getBoundingClientRect().width
      };
      if (typeof resizeHandle.setPointerCapture === "function") {
        resizeHandle.setPointerCapture(eventValue.pointerId);
      }
      eventValue.preventDefault();
    });
    resizeHandle.addEventListener("pointermove", (eventValue) => {
      if (!dragState || eventValue.pointerId !== dragState.pointerId) return;
      setPanelWidth(dragState.startWidth + dragState.startX - eventValue.clientX, false);
    });
    const finishResize = (eventValue) => {
      if (!dragState || eventValue.pointerId !== dragState.pointerId) return;
      dragState = null;
      setPanelWidth(panelWidth, true);
    };
    resizeHandle.addEventListener("pointerup", finishResize);
    resizeHandle.addEventListener("pointercancel", finishResize);
    resizeHandle.addEventListener("dblclick", () => setPanelWidth(520, true));
    resizeHandle.addEventListener("keydown", (eventValue) => {
      if (!["ArrowLeft", "ArrowRight", "Home"].includes(eventValue.key)) return;
      eventValue.preventDefault();
      const step = eventValue.shiftKey ? 50 : 20;
      if (eventValue.key === "Home") setPanelWidth(520, true);
      else setPanelWidth(panelWidth + (eventValue.key === "ArrowLeft" ? step : -step), true);
    });

    setPanelWidth(panelWidth, false);
    launcher.addEventListener("click", () => setPanelOpen(true));
    close.addEventListener("click", () => setPanelOpen(false));
    refresh.addEventListener("click", () => {
      if (typeof callbacks.onRefresh === "function") callbacks.onRefresh();
    });
    search.addEventListener("input", () => { visibleLimit = 100; render(); notifyViewStateChange(); });
    status.addEventListener("change", () => { visibleLimit = 100; render(); notifyViewStateChange(); });
    sort.addEventListener("change", () => { visibleLimit = 100; render(); notifyViewStateChange(); });
    queueTiming.addEventListener("change", () => { visibleLimit = 100; render(); notifyViewStateChange(); });
    dueOptions.addEventListener("change", (eventValue) => {
      const input = eventValue.target.closest('input[type="checkbox"]');
      if (!input) return;
      if (input.checked) selectedDueModes.add(input.value); else selectedDueModes.delete(input.value);
      updateQueueFilterLabels();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    dueDays.addEventListener("input", () => {
      selectedDueModes.add("next");
      nextDueCheck.checked = true;
      updateQueueFilterLabels();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    dueDays.addEventListener("change", () => {
      dueDays.value = String(normalizedDueDays());
      updateQueueFilterLabels();
      render();
      notifyViewStateChange();
    });
    clearDueRanges.addEventListener("click", () => {
      selectedDueModes.clear();
      syncDueChecks();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    projectOptions.addEventListener("change", (eventValue) => {
      const input = eventValue.target.closest('input[type="checkbox"]');
      if (!input) return;
      if (input.checked) selectedProjects.add(input.value); else selectedProjects.delete(input.value);
      updateQueueFilterLabels();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    selectAllProjects.addEventListener("click", () => {
      (indexData && indexData.projects || []).forEach((project) => selectedProjects.add(String(project.id)));
      syncProjectChecks();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    clearProjects.addEventListener("click", () => {
      selectedProjects.clear();
      syncProjectChecks();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    typePopover.addEventListener("change", (eventValue) => {
      const input = eventValue.target.closest('input[type="checkbox"]');
      if (!input) return;
      if (input.checked) selectedTypes.add(input.value); else selectedTypes.delete(input.value);
      updateQueueFilterLabels();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    peopleOptions.addEventListener("change", (eventValue) => {
      const input = eventValue.target.closest('input[type="checkbox"]');
      if (!input) return;
      if (input.checked) selectedPeople.add(input.value); else selectedPeople.delete(input.value);
      selectedPeopleScope = inferPeopleScope();
      updateQueueFilterLabels();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    selectOnlyMe.addEventListener("click", () => {
      selectPeopleScope("only-me");
      syncPeopleChecks();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    selectCompanyPeople.addEventListener("click", () => {
      selectPeopleScope("company");
      syncPeopleChecks();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    selectAllPeople.addEventListener("click", () => {
      selectPeopleScope("all");
      syncPeopleChecks();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    activityView.addEventListener("click", () => {
      currentView = "activity";
      activityView.setAttribute("aria-pressed", "true");
      queueView.setAttribute("aria-pressed", "false");
      updateViewControls();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    queueView.addEventListener("click", () => {
      currentView = "queue";
      activityView.setAttribute("aria-pressed", "false");
      queueView.setAttribute("aria-pressed", "true");
      updateViewControls();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    reasonFilters.addEventListener("click", (eventValue) => {
      const button = eventValue.target.closest("button[data-reason]");
      if (!button) return;
      const reason = button.dataset.reason;
      if (reason === "all") {
        selectedReasons.clear();
      } else if (selectedReasons.has(reason)) {
        selectedReasons.delete(reason);
      } else {
        selectedReasons.add(reason);
      }
      updateReasonButtons();
      visibleLimit = 100;
      render();
      notifyViewStateChange();
    });
    panel.addEventListener("keydown", (eventValue) => {
      if (eventValue.key === "Escape") setPanelOpen(false);
    });

    const api = {
      setPanelOpen,
      setWidth(value) { return setPanelWidth(value, false); },
      setViewState(value) {
        pendingViewState = normalizedViewState(value);
        const restored = applyViewState(pendingViewState, Boolean(indexData));
        if (indexData) pendingViewState = null;
        return restored;
      },
      getViewState() { return currentViewState(); },
      setContext(context) {
        contextItemType = context && context.itemType;
        itemLabel = context && context.itemLabel ? context.itemLabel : "Item";
        itemLabelPlural = context && context.itemLabelPlural ? context.itemLabelPlural : `${itemLabel}s`;
        activityTitle = "My Helix Activity";
        launcher.textContent = activityTitle;
        heading.textContent = activityTitle;
        panel.setAttribute("aria-label", activityTitle);
        close.setAttribute("aria-label", `Hide ${activityTitle}`);
        updateViewControls();
        sort.setAttribute("aria-label", `Sort ${itemLabelPlural}`);
        const numberOption = sort.querySelector('option[value="number"]');
        if (numberOption) numberOption.textContent = `${itemLabel} number`;
        reasonFilters.querySelectorAll("button[data-reason]").forEach((button) => {
          button.hidden = context && context.itemType === "submittal"
            ? ["responded", "accepted"].includes(button.dataset.reason)
            : false;
        });
        subtitle.textContent = context && context.user
          ? `${context.user.fullName}${context.user.company ? ` • ${context.user.company}` : ""}`
          : "Current Helix user";
      },
      setLoading(message) {
        refresh.disabled = true;
        setState("Building your activity index", message || "Reading Helix data…", true);
      },
      setProgress(update) {
        setState("Building your activity index", update && update.message ? update.message : "Reading Helix data…", true);
      },
      setError(message) {
        refresh.disabled = false;
        setState("Unable to build the index", message || "Please reload the Helix page and try again.", false);
      },
      setData(value) {
        const restoredState = pendingViewState;
        const previousStatus = status.value;
        const previousProjects = new Set(selectedProjects);
        const previousProjectCount = projectOptions.querySelectorAll('input[type="checkbox"]').length;
        const previousPeople = new Set(selectedPeople);
        const previousPeopleCount = peopleOptions.querySelectorAll('input[type="checkbox"]').length;
        const previousPeopleScope = selectedPeopleScope;
        indexData = value;
        refresh.disabled = false;
        visibleLimit = 100;
        const openQueue = Array.isArray(value.openQueue) ? value.openQueue : [];
        const statuses = Array.from(new Set([...value.results, ...openQueue].map((item) => item.status).filter(Boolean))).sort();
        status.replaceChildren(allStatus);
        statuses.forEach((statusValue) => {
          const option = element(documentObject, "option", "", statusValue);
          option.value = statusValue;
          status.appendChild(option);
        });
        if (!restoredState && selectHasValue(status, previousStatus)) status.value = previousStatus;
        const projects = Array.isArray(value.projects) ? value.projects : [];
        const previouslyAllSelected = previousProjectCount === 0 || previousProjects.size === previousProjectCount;
        selectedProjects.clear();
        projectOptions.replaceChildren();
        projects.forEach((project) => {
          const projectId = String(project.id);
          if (previouslyAllSelected || previousProjects.has(projectId)) selectedProjects.add(projectId);
          const option = element(documentObject, "label", "check-option");
          const input = element(documentObject, "input");
          input.type = "checkbox";
          input.value = projectId;
          input.checked = selectedProjects.has(projectId);
          option.append(input, documentObject.createTextNode(project.name || projectId));
          projectOptions.appendChild(option);
        });
        currentPersonKey = value.currentPersonKey || null;
        const people = Array.isArray(value.people) ? value.people : [];
        selectedPeople.clear();
        peopleOptions.replaceChildren();
        people.forEach((person) => {
          const key = String(person.key);
          const option = element(documentObject, "label", "check-option");
          const input = element(documentObject, "input");
          input.type = "checkbox";
          input.value = key;
          input.checked = selectedPeople.has(key);
          const label = `${person.fullName || "Unknown person"}${person.isCurrent ? " (me)" : ""}`;
          option.append(input, documentObject.createTextNode(label));
          peopleOptions.appendChild(option);
        });
        if (restoredState) {
          applyViewState(restoredState, false);
          pendingViewState = null;
        } else {
          selectedPeopleScope = previousPeopleCount === 0 ? "only-me" : previousPeopleScope;
          if (selectedPeopleScope === "custom") {
            previousPeople.forEach((key) => {
              if (people.some((person) => String(person.key) === key)) selectedPeople.add(key);
            });
          } else {
            selectPeopleScope(selectedPeopleScope);
          }
          syncPeopleChecks();
          updateQueueFilterLabels();
        }
        if (value.warnings && value.warnings.length) {
          warnings.textContent = `Some activity may be incomplete: ${value.warnings.map((warning) => warning.collection).join(", ")}.`;
          warnings.classList.remove("hidden");
        } else {
          warnings.classList.add("hidden");
        }
        render();
      },
      notifyOpenFailure(button, message) {
        if (!button) return;
        const original = button.textContent;
        button.textContent = message || "Unable to open";
        button.disabled = true;
        setTimeout(() => {
          button.textContent = original;
          button.disabled = false;
        }, 2500);
      },
      get shadowRoot() { return shadow; }
    };
    host.__helixActivityUi = api;
    (documentObject.body || documentObject.documentElement).appendChild(host);
    return api;
  }

  return Object.freeze({ mount, STYLES });
});
