(function attachHelixBridgeCore(root, factory) {
  const api = factory();
  root.HelixBridgeCore = api;
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function createHelixBridgeCore() {
  "use strict";

  const COLLECTIONS = Object.freeze({
    rfis: "HelixProjectRFIs",
    submittals: "HelixProjectSubmittalPackages",
    directories: "HelixProjectDirectory",
    comments: "HelixProjectComments",
    attachments: "HelixProjectAttachments",
    histories: "HelixProjectHistories",
    reports: "HelixProjectReports"
  });
  const ALLOWED_COLLECTIONS = new Set(Object.values(COLLECTIONS));
  const PAGE_SIZE = 250;
  const MAX_RECORDS = 20000;
  const HISTORY_BATCH_SIZE = 20;
  const SCAN_READ_CONCURRENCY = 3;

  const RFI_FIELDS = [
    "_id",
    "refProject",
    "displayNumber",
    "number",
    "referenceNumber",
    "title",
    "status",
    "statusMessage",
    "refContract",
    "dateRequestedReview",
    "dateContractualReview",
    "datePending",
    "dateReview",
    "dateConfirmation",
    "dateComplete",
    "dateVoided",
    "refDirectoryCreator",
    "refDirectoryResponseEditor",
    "refDirectoryAccepted",
    "refDirectoryRejected",
    "workflow",
    "_kmd",
    "nameEditor",
    "lastModifiedPlatform",
    "countAttachments",
    "countComments"
  ];
  const SUBMITTAL_FIELDS = [
    "_id",
    "refProject",
    "number",
    "description",
    "title",
    "status",
    "statusMessage",
    "refContractSupplier",
    "refsSubmittalRegisters",
    "dateOpen",
    "dateDue",
    "datePackagePending",
    "datePending",
    "dateReview",
    "dateConfirmation",
    "dateComplete",
    "dateVoided",
    "dateRequestedReview",
    "dateContractualReview",
    "dateManuallyRejected",
    "refDirectoryCreator",
    "refDirectoryRejected",
    "workflow",
    "_kmd",
    "nameEditor",
    "lastModifiedPlatform",
    "countAttachments",
    "countComments"
  ];
  const ITEM_TYPES = Object.freeze({
    rfi: Object.freeze({ collection: COLLECTIONS.rfis, fields: RFI_FIELDS, label: "RFI", plural: "RFIs" }),
    submittal: Object.freeze({
      collection: COLLECTIONS.submittals,
      fields: SUBMITTAL_FIELDS,
      label: "Submittal",
      plural: "Submittals"
    })
  });

  const COMMENT_FIELDS = ["_id", "idItem", "idProject", "comment", "refDirectory", "_kmd"];
  const ATTACHMENT_FIELDS = [
    "_id",
    "idItem",
    "idProject",
    "filename",
    "description",
    "nameEditor",
    "refDirectoryCreator",
    "isActive",
    "isCurrent",
    "_kmd"
  ];
  const DOWNLOAD_ATTACHMENT_FIELDS = [
    "_id",
    "idItem",
    "idProject",
    "filename",
    "description",
    "isActive",
    "isCurrent",
    "_kmd"
  ];
  const REPORT_FIELDS = [
    "_id",
    "reportName",
    "fileName",
    "functionName",
    "featureName",
    "subFeatureName",
    "refFeature",
    "refFeatures",
    "isActive",
    "isV4",
    "isClientReport",
    "settingsParams"
  ];
  const HISTORY_FIELDS = [
    "_id",
    "originalId",
    "nameEditor",
    "lastModifiedPlatform",
    "status",
    "refDirectoryResponseEditor",
    "refDirectoryAccepted",
    "refDirectoryRejected",
    "_kmd"
  ];
  const DIRECTORY_FIELDS = [
    "_id",
    "refContact._id",
    "refProject._id",
    "refProject.name",
    "isActive"
  ];

  let itemNavigationCache = new Map();

  function parsePageContext(urlValue) {
    try {
      const url = new URL(String(urlValue));
      if (url.origin !== "https://project.helixteam.com") {
        return null;
      }
      const match = url.pathname.match(/^\/Projects\/([^/]+)\/(RFI|Submittals|Overview)(?:\/|$)/i);
      if (!match) {
        return null;
      }
      return {
        projectId: decodeURIComponent(match[1]),
        itemType: match[2].toLocaleLowerCase() === "rfi"
          ? "rfi"
          : match[2].toLocaleLowerCase() === "submittals" ? "submittal" : "overview"
      };
    } catch (_error) {
      return null;
    }
  }

  function parseProjectId(urlValue) {
    const page = parsePageContext(urlValue);
    return page ? page.projectId : null;
  }

  function getIdentityId(value) {
    if (!value || typeof value !== "object") {
      return null;
    }
    return value._id || value.id || (value.refDirectory && value.refDirectory._id) || null;
  }

  function getIdentityName(value) {
    if (!value || typeof value !== "object") {
      return null;
    }
    return value.fullName || value.name || (value.refDirectory && value.refDirectory.fullName) || null;
  }

  function getIdentityCompanyId(value) {
    if (!value || typeof value !== "object") {
      return null;
    }
    return value.companyId
      || (value.refCompany && (value.refCompany._id || value.refCompany.id))
      || (value.refDirectory && value.refDirectory.refCompany && value.refDirectory.refCompany._id)
      || null;
  }

  function getIdentityCompanyName(value) {
    if (!value || typeof value !== "object") {
      return null;
    }
    const company = value.refCompany || (value.refDirectory && value.refDirectory.refCompany);
    return value.company || (company && (company.name || company.nameCompany || company.displayName)) || null;
  }

  function normalizeName(value) {
    return String(value || "").trim().toLocaleLowerCase();
  }

  function sameIdentity(value, user) {
    const candidateId = getIdentityId(value);
    if (candidateId && user.id) {
      return String(candidateId) === String(user.id);
    }
    const candidateName = getIdentityName(value);
    return Boolean(candidateName && user.fullName && normalizeName(candidateName) === normalizeName(user.fullName));
  }

  function getContext(windowObject) {
    const page = parsePageContext(windowObject.location && windowObject.location.href);
    if (!page) {
      throw new Error("Open a Helix project Overview, RFI, or Submittals page before using the extension.");
    }

    const directoryInfo = windowObject.UserInfo && windowObject.UserInfo.directoryInfo;
    const userId = getIdentityId(directoryInfo);
    const fullName = getIdentityName(directoryInfo);
    if (!directoryInfo || !userId || !fullName) {
      throw new Error("Helix is still loading the currently logged-in user.");
    }

    const itemConfig = ITEM_TYPES[page.itemType] || { label: "Helix", plural: "Items" };
    const contactId = directoryInfo.refContact && directoryInfo.refContact._id;
    return {
      projectId: page.projectId,
      projectName: windowObject.UserInfo && windowObject.UserInfo.projectInfo
        && windowObject.UserInfo.projectInfo.name
        ? String(windowObject.UserInfo.projectInfo.name)
        : page.projectId,
      contactId: contactId ? String(contactId) : null,
      itemType: page.itemType,
      itemLabel: itemConfig.label,
      itemLabelPlural: itemConfig.plural,
      user: {
        id: String(userId),
        contactId: contactId ? String(contactId) : null,
        fullName: String(fullName),
        companyId: getIdentityCompanyId(directoryInfo) ? String(getIdentityCompanyId(directoryInfo)) : null,
        company: directoryInfo.refCompany && (directoryInfo.refCompany.name || directoryInfo.refCompany.nameCompany)
          ? String(directoryInfo.refCompany.name || directoryInfo.refCompany.nameCompany)
          : null
      }
    };
  }

  function resolveMaster(windowObject) {
    if (windowObject.Master && typeof windowObject.Master.readFromCollection === "function") {
      return windowObject.Master;
    }

    const handler = windowObject.featureScriptHandler;
    if (handler && typeof handler.getCurrentMasterObject === "function") {
      const current = handler.getCurrentMasterObject();
      if (current && current.Master && typeof current.Master.readFromCollection === "function") {
        return current.Master;
      }
    }

    throw new Error("Helix's read-only data service is not ready yet.");
  }

  function getDetailWindowWidgets(windowObject) {
    const jquery = windowObject.jQuery || windowObject.$;
    if (typeof jquery !== "function") {
      return [];
    }

    const targets = ["#detailWindow"];
    const documentObject = windowObject.document;
    if (documentObject && typeof documentObject.querySelectorAll === "function") {
      targets.push(...documentObject.querySelectorAll('[id^="DetailEdit_"][id$="_detailWindow"]'));
    }

    const widgets = [];
    targets.forEach((target) => {
      try {
        const selection = jquery(target);
        const widget = selection && typeof selection.data === "function"
          ? selection.data("kendoWindow")
          : null;
        if (widget && !widgets.includes(widget)) {
          widgets.push(widget);
        }
      } catch (_error) {
        // A detail window can be destroyed while Helix is updating its viewer.
      }
    });
    return widgets;
  }

  function isDetailWindowVisible(widget) {
    try {
      if (widget.wrapper && typeof widget.wrapper.is === "function") {
        return Boolean(widget.wrapper.is(":visible"));
      }
      if (typeof widget.visible === "boolean") {
        return widget.visible;
      }
      if (widget.options && typeof widget.options.visible === "boolean") {
        return widget.options.visible;
      }
    } catch (_error) {
      return false;
    }
    return true;
  }

  function getOpenDetailItemId(master, windowObject) {
    const stack = master && Array.isArray(master.detailStack) ? master.detailStack : [];
    const current = stack[stack.length - 1];
    if (current && current.id != null) {
      return String(current.id);
    }

    const loadedModel = windowObject.DetailEdit && windowObject.DetailEdit.loadedModel;
    if (loadedModel && loadedModel._id != null) {
      return String(loadedModel._id);
    }
    return null;
  }

  async function closeOtherDetailView(windowObject, master, itemId) {
    const openWidgets = getDetailWindowWidgets(windowObject).filter(isDetailWindowVisible);
    if (openWidgets.length === 0) {
      return false;
    }

    const requestedId = String(itemId);
    if (getOpenDetailItemId(master, windowObject) === requestedId) {
      return "already-open";
    }

    for (let index = openWidgets.length - 1; index >= 0; index -= 1) {
      const widget = openWidgets[index];
      if (typeof widget.close !== "function") {
        throw new Error("Helix's open detail viewer could not be closed.");
      }
      widget.close();
    }

    if (master.hasChanges && openWidgets.some(isDetailWindowVisible)) {
      throw new Error("Helix kept the current item open because it has unsaved changes. Finish or discard them, then try again.");
    }

    const deadline = Date.now() + 1000;
    while (openWidgets.some(isDetailWindowVisible) && Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    if (openWidgets.some(isDetailWindowVisible)) {
      throw new Error("Helix's current detail viewer did not close. Close it and try again.");
    }
    return true;
  }

  function findPageGridNamespace(windowObject, collectionName) {
    const candidates = [];
    if (windowObject.gridNamespace) {
      candidates.push(windowObject.gridNamespace);
    }

    const masterObjectList = windowObject.MasterObjectList;
    if (masterObjectList && masterObjectList.gridPage && masterObjectList.gridPage.gridNamespace) {
      candidates.push(masterObjectList.gridPage.gridNamespace);
    }

    const handler = windowObject.featureScriptHandler;
    if (handler && typeof handler.getCurrentMasterObject === "function") {
      try {
        const current = handler.getCurrentMasterObject();
        if (current && current.gridNamespace) {
          candidates.push(current.gridNamespace);
        }
      } catch (_error) {
        // Helix can briefly have no current feature object while closing a detail window.
      }
    }

    return candidates.find((candidate, index) => candidate
      && candidates.indexOf(candidate) === index
      && candidate.collectionName === collectionName
      && Array.isArray(candidate.detailEditColumns)
      && candidate.detailEditColumns.length > 0
      && typeof candidate.baseGrid_onManipulateDataOnRead === "function") || null;
  }

  async function waitForPageGridNamespace(windowObject, collectionName) {
    const deadline = Date.now() + 5000;
    let gridNamespace = findPageGridNamespace(windowObject, collectionName);
    while (!gridNamespace && Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 50));
      gridNamespace = findPageGridNamespace(windowObject, collectionName);
    }
    if (!gridNamespace) {
      throw new Error("Helix's item viewer is still initializing. Please try opening the item again.");
    }
    return gridNamespace;
  }

  function normalizeDetailRecord(gridNamespace, record, itemId, projectId) {
    let normalized;
    try {
      const result = gridNamespace.baseGrid_onManipulateDataOnRead([
        JSON.parse(JSON.stringify(record))
      ]);
      normalized = Array.isArray(result) ? result[0] : null;
    } catch (_error) {
      throw new Error("Helix could not prepare this item for its detail viewer.");
    }

    const normalizedProject = normalized && normalized.refProject && normalized.refProject._id;
    if (!normalized || String(normalized._id) !== String(itemId)
      || (normalizedProject && String(normalizedProject) !== String(projectId))) {
      throw new Error("Helix returned an invalid item while preparing the detail viewer.");
    }
    return normalized;
  }

  async function readCompleteDetailRecord(master, config, itemId, projectId) {
    const records = await readAll(master, config.collection, {
      filter: { _id: String(itemId), "refProject._id": String(projectId) },
      sort: { _id: 1 }
    });
    return records.find((candidate) => candidate && String(candidate._id) === String(itemId)
      && (!candidate.refProject || String(candidate.refProject._id) === String(projectId))) || null;
  }

  function itemRoute(itemType, projectId) {
    const route = itemType === "rfi" ? "RFI" : "Submittals";
    return `https://project.helixteam.com/Projects/${encodeURIComponent(projectId)}/${route}`;
  }

  function getDownloadTarget(windowObject, itemId, itemType, projectId) {
    const page = parsePageContext(windowObject.location && windowObject.location.href);
    if (!page) {
      throw new Error("The current tab is no longer on a supported Helix activity page.");
    }
    const targetType = itemType || page.itemType;
    const targetProjectId = String(projectId || page.projectId);
    if (!ITEM_TYPES[targetType]) {
      throw new Error("The requested Helix item type is not supported.");
    }
    const samePage = page.itemType === targetType && page.projectId === targetProjectId;
    const cachedItem = itemNavigationCache.get(`${targetType}:${String(itemId)}`);
    if (!samePage) {
      if (!cachedItem) {
        throw new Error("This item could not be found on the current Helix page.");
      }
      if (String(cachedItem.projectId) !== targetProjectId) {
        throw new Error("This item does not belong to the requested project.");
      }
      return {
        itemId: String(itemId),
        itemType: targetType,
        projectId: targetProjectId,
        navigateTo: itemRoute(targetType, targetProjectId)
      };
    }
    return {
      itemId: String(itemId),
      itemType: targetType,
      projectId: targetProjectId,
      navigateTo: null
    };
  }

  function getPackageTarget(windowObject, itemId, itemType, projectId) {
    const page = parsePageContext(windowObject.location && windowObject.location.href);
    if (!page) {
      throw new Error("The current tab is no longer on a supported Helix activity page.");
    }
    const targetType = itemType || page.itemType;
    if (!ITEM_TYPES[targetType]) {
      throw new Error("The requested Helix item type is not supported.");
    }
    return {
      itemId: String(itemId),
      itemType: targetType,
      projectId: String(projectId || page.projectId)
    };
  }

  function reportFeatureFilter(featureName) {
    return {
      $or: [
        { featureName },
        { "refFeature.displayName": featureName },
        { "refFeature.referenceName": featureName },
        { "refFeature.name": featureName },
        { "refFeatures.displayName": featureName },
        { "refFeatures.referenceName": featureName },
        { "refFeatures.name": featureName },
        {
          refFeatures: {
            $elemMatch: {
              $or: [
                { displayName: featureName },
                { referenceName: featureName },
                { name: featureName }
              ]
            }
          }
        }
      ]
    };
  }

  function chooseDetailsReport(reports, featureName) {
    const feature = normalizeName(featureName);
    const exactNames = feature === "rfis"
      ? new Set(["details (rfis)", "details (rfi)"])
      : new Set(["details (submittals)", "details (submittal)"]);
    const active = reports.filter((report) => report && report._id && report.isActive !== false);
    return active.find((report) => exactNames.has(normalizeName(report.reportName)))
      || active.find((report) => {
        const name = normalizeName(report.reportName);
        return name.includes("details") && name.includes(feature === "rfis" ? "rfi" : "submittal");
      })
      || null;
  }

  function primitiveReportSetting(record, remoteProp, itemId, projectId) {
    const expression = String(remoteProp || "").replace(/^window\.[^.]+\./, "");
    if (expression === "DetailEdit.loadedModel._id") return itemId;
    if (expression === "Master.projectID") return projectId;
    const match = expression.match(/^DetailEdit\.loadedModel\.([a-zA-Z0-9_.]+)$/);
    if (!match) return null;
    const value = match[1].split(".").reduce((current, key) => (
      current && typeof current === "object" ? current[key] : null
    ), record);
    return ["string", "number", "boolean"].includes(typeof value) ? value : null;
  }

  function buildReportDownload(report, record, context, target) {
    const reportName = String(report.reportName || "Details");
    const params = new URLSearchParams();
    if (report.isV4 && report.isClientReport) {
      params.set("rid", String(report._id));
      (Array.isArray(report.settingsParams) ? report.settingsParams : []).forEach((setting) => {
        if (!setting || !setting.propName || !setting.remoteProp) return;
        const value = primitiveReportSetting(
          record,
          setting.remoteProp,
          target.itemId,
          target.projectId
        );
        if (value !== null) params.set(String(setting.propName), String(value));
      });
      params.set("helixActivityDownload", "1");
      return {
        name: reportName,
        kind: "client",
        filename: `${target.itemType === "rfi" ? "RFI" : "Submittal"} ${record.displayNumber || record.number || target.itemId} - Details.pdf`,
        url: `https://project.helixteam.com/Reports/clientReportViewer?${params.toString()}`
      };
    }

    if (!report.fileName) {
      throw new Error(`Helix's ${reportName} report is missing its report file.`);
    }
    params.set("reportFile", String(report.fileName));
    params.set("id", target.itemId);
    params.set("idProject", target.projectId);
    const userInfo = context.windowObject.UserInfo || {};
    const directory = userInfo.directoryInfo || {};
    const project = userInfo.projectInfo || {};
    if (directory.timezone) params.set("timezoneContactString", String(directory.timezone));
    if (project.timezone) params.set("timezoneProjectString", String(project.timezone));
    const email = context.windowObject.loginObject && context.windowObject.loginObject.contact
      && context.windowObject.loginObject.contact.email;
    if (email) params.set("user", String(email));
    if (report.isV4) params.set("reportName", reportName);
    params.set("helixActivityDownload", "1");
    return {
      name: reportName,
      kind: report.isV4 ? "v4" : "legacy",
      filename: `${target.itemType === "rfi" ? "RFI" : "Submittal"} ${record.displayNumber || record.number || target.itemId} - Details.pdf`,
      url: `https://project.helixteam.com/Reports/${report.isV4 ? "betaViewer" : "Display"}?${params.toString()}`
    };
  }

  function cloneQuery(query) {
    return JSON.parse(JSON.stringify(query || {}));
  }

  function createConcurrencyLimiter(concurrency) {
    const maximum = Math.max(1, Number(concurrency) || 1);
    const pendingReads = [];
    let activeReads = 0;

    function startNext() {
      if (activeReads >= maximum || pendingReads.length === 0) return;
      const pendingRead = pendingReads.shift();
      activeReads += 1;
      Promise.resolve()
        .then(pendingRead.reader)
        .then(pendingRead.resolve, pendingRead.reject)
        .finally(() => {
          activeReads -= 1;
          startNext();
        });
    }

    return function limitRead(reader) {
      return new Promise((resolve, reject) => {
        pendingReads.push({ reader, resolve, reject });
        startNext();
      });
    };
  }

  async function readAll(master, collectionName, queryOptions, readLimiter) {
    if (!ALLOWED_COLLECTIONS.has(collectionName)) {
      throw new Error("Collection is not permitted by the read-only bridge.");
    }
    if (!master || typeof master.readFromCollection !== "function") {
      throw new Error("Helix's read-only data service is unavailable.");
    }

    const baseQuery = cloneQuery(queryOptions);
    const records = [];
    for (let skip = 0; skip < MAX_RECORDS; skip += PAGE_SIZE) {
      const pageQuery = Object.assign({}, baseQuery, { limit: PAGE_SIZE, skip });
      const page = await (typeof readLimiter === "function"
        ? readLimiter(() => master.readFromCollection(collectionName, pageQuery))
        : master.readFromCollection(collectionName, pageQuery));
      if (!Array.isArray(page)) {
        throw new Error(`Helix returned an unexpected ${collectionName} response.`);
      }
      records.push(...page);
      if (page.length < PAGE_SIZE) {
        return records;
      }
    }
    throw new Error(`${collectionName} exceeded the extension's ${MAX_RECORDS.toLocaleString()} record safety limit.`);
  }

  function chunks(values, size) {
    const result = [];
    for (let index = 0; index < values.length; index += size) {
      result.push(values.slice(index, index + size));
    }
    return result;
  }

  async function mapWithConcurrency(values, concurrency, worker) {
    const output = new Array(values.length);
    let nextIndex = 0;

    async function run() {
      while (nextIndex < values.length) {
        const index = nextIndex;
        nextIndex += 1;
        output[index] = await worker(values[index], index);
      }
    }

    const runners = [];
    const runnerCount = Math.min(concurrency, values.length);
    for (let index = 0; index < runnerCount; index += 1) {
      runners.push(run());
    }
    await Promise.all(runners);
    return output;
  }

  function toIso(value) {
    if (!value) {
      return null;
    }
    const date = value instanceof Date ? value : new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }

  function identitySummary(value) {
    return value && typeof value === "object"
      ? {
          id: getIdentityId(value),
          contactId: value.refContact && value.refContact._id ? String(value.refContact._id) : null,
          fullName: getIdentityName(value),
          companyId: getIdentityCompanyId(value),
          company: getIdentityCompanyName(value)
        }
      : null;
  }

  function projectNameFromReference(value) {
    if (!value || typeof value !== "object") {
      return null;
    }
    return value.name || value.projectName || value.displayName || value.title || null;
  }

  function workflowSummary(workflow) {
    const value = workflow && typeof workflow === "object" ? workflow : {};
    const summarizeEntry = (entry) => ({
      assigned: identitySummary(entry && entry.refDirectoryAssigned),
      reviewed: entry && typeof entry.refDirectoryReviewed === "object"
        ? identitySummary(entry.refDirectoryReviewed)
        : Boolean(entry && entry.refDirectoryReviewed),
      reviewedAt: toIso(entry && entry.dateReviewed)
    });
    return {
      required: Array.isArray(value.workflowRequired) ? value.workflowRequired.map(summarizeEntry) : [],
      optional: Array.isArray(value.workflowOptional) ? value.workflowOptional.map(summarizeEntry) : [],
      notify: Array.isArray(value.refDirectoriesNotify) ? value.refDirectoriesNotify.map(identitySummary).filter(Boolean) : []
    };
  }

  function sanitizeRfi(record) {
    return {
      id: record._id,
      projectId: record.refProject && record.refProject._id,
      projectName: projectNameFromReference(record.refProject),
      number: record.displayNumber || record.number || (record.referenceNumber ? `Draft ${record.referenceNumber}` : "Unnumbered"),
      title: record.title || "Untitled RFI",
      status: record.status && record.status.name ? record.status.name : "Unknown",
      statusRank: record.status && Number.isFinite(record.status.rank) ? record.status.rank : null,
      responsible: record.statusMessage || null,
      contract: record.refContract && (record.refContract.displayName || record.refContract.description)
        ? record.refContract.displayName || record.refContract.description
        : null,
      dates: {
        created: toIso(record._kmd && record._kmd.ect),
        modified: toIso(record._kmd && record._kmd.lmt),
        pending: toIso(record.datePending),
        review: toIso(record.dateReview),
        confirmation: toIso(record.dateConfirmation),
        complete: toIso(record.dateComplete),
        voided: toIso(record.dateVoided),
        requestedReview: toIso(record.dateRequestedReview),
        contractualReview: toIso(record.dateContractualReview)
      },
      creator: identitySummary(record.refDirectoryCreator),
      responseEditor: identitySummary(record.refDirectoryResponseEditor),
      acceptedBy: identitySummary(record.refDirectoryAccepted),
      rejectedBy: identitySummary(record.refDirectoryRejected),
      lastEditorName: record.nameEditor || null,
      lastModifiedPlatform: record.lastModifiedPlatform || null,
      attachmentCount: Number(record.countAttachments) || 0,
      commentCount: Number(record.countComments) || 0,
      workflow: workflowSummary(record.workflow)
    };
  }

  function sanitizeSubmittal(record) {
    const registers = Array.isArray(record.refsSubmittalRegisters)
      ? record.refsSubmittalRegisters
        .filter((item) => item && item.isActive !== false && item.number)
        .map((item) => String(item.number))
      : [];
    return {
      id: record._id,
      projectId: record.refProject && record.refProject._id,
      projectName: projectNameFromReference(record.refProject),
      itemType: "submittal",
      number: record.number || "Unnumbered",
      title: record.description || record.title || "Untitled Submittal",
      status: record.status && record.status.name ? record.status.name : "Unknown",
      statusRank: record.status && Number.isFinite(record.status.rank) ? record.status.rank : null,
      responsible: record.statusMessage || null,
      contract: record.refContractSupplier
        && (record.refContractSupplier.displayName || record.refContractSupplier.description)
        ? record.refContractSupplier.displayName || record.refContractSupplier.description
        : null,
      registers,
      dates: {
        created: toIso(record._kmd && record._kmd.ect),
        modified: toIso(record._kmd && record._kmd.lmt),
        open: toIso(record.dateOpen),
        due: toIso(record.dateDue),
        packagePending: toIso(record.datePackagePending),
        pending: toIso(record.datePending),
        review: toIso(record.dateReview),
        confirmation: toIso(record.dateConfirmation),
        complete: toIso(record.dateComplete),
        voided: toIso(record.dateVoided),
        requestedReview: toIso(record.dateRequestedReview),
        contractualReview: toIso(record.dateContractualReview),
        rejected: toIso(record.dateManuallyRejected)
      },
      creator: identitySummary(record.refDirectoryCreator),
      responseEditor: null,
      acceptedBy: null,
      rejectedBy: identitySummary(record.refDirectoryRejected),
      lastEditorName: record.nameEditor || null,
      lastModifiedPlatform: record.lastModifiedPlatform || null,
      attachmentCount: Number(record.countAttachments) || 0,
      commentCount: Number(record.countComments) || 0,
      workflow: workflowSummary(record.workflow),
      searchTerms: registers
    };
  }

  function sanitizeItem(record, itemType, projectNames, context) {
    const item = itemType === "submittal"
      ? sanitizeSubmittal(record)
      : Object.assign({ itemType: "rfi", searchTerms: [] }, sanitizeRfi(record));
    const names = projectNames instanceof Map ? projectNames : new Map();
    item.projectName = item.projectName
      || names.get(String(item.projectId))
      || (context && String(item.projectId) === String(context.projectId) ? context.projectName : null)
      || String(item.projectId || "Unknown project");
    return item;
  }

  function sanitizeComment(record) {
    return {
      id: record._id,
      itemId: record.idItem,
      text: record.comment || "",
      author: identitySummary(record.refDirectory),
      createdAt: toIso(record._kmd && record._kmd.ect),
      modifiedAt: toIso(record._kmd && record._kmd.lmt)
    };
  }

  function sanitizeAttachment(record) {
    return {
      id: record._id,
      itemId: record.idItem,
      filename: record.description || record.filename || "Attachment",
      uploader: identitySummary(record.refDirectoryCreator),
      editorName: record.nameEditor || null,
      createdAt: toIso(record._kmd && record._kmd.ect),
      modifiedAt: toIso(record._kmd && record._kmd.lmt),
      isActive: record.isActive !== false,
      isCurrent: record.isCurrent !== false
    };
  }

  function sanitizeHistory(record) {
    return {
      id: record._id,
      itemId: record.originalId,
      editorName: record.nameEditor || null,
      platform: record.lastModifiedPlatform || "web",
      status: record.status && record.status.name ? record.status.name : null,
      responseEditor: identitySummary(record.refDirectoryResponseEditor),
      acceptedBy: identitySummary(record.refDirectoryAccepted),
      rejectedBy: identitySummary(record.refDirectoryRejected),
      modifiedAt: toIso(record._kmd && record._kmd.lmt),
      createdAt: toIso(record._kmd && record._kmd.ect)
    };
  }

  function safeMessage(error) {
    if (error instanceof Error && error.message) {
      return error.message.slice(0, 240);
    }
    return "The collection could not be read.";
  }

  async function scan(windowObject, onProgress) {
    const context = getContext(windowObject);
    const master = resolveMaster(windowObject);
    const limitRead = createConcurrencyLimiter(SCAN_READ_CONCURRENCY);
    const warnings = [];
    const progress = typeof onProgress === "function" ? onProgress : function noop() {};

    progress({ phase: "items", message: `Loading ${context.itemLabelPlural} for this project…` });
    const currentItemTypes = context.itemType === "overview" ? ["rfi", "submittal"] : [context.itemType];
    const currentPages = await Promise.all(currentItemTypes.map(async (itemType) => {
      const config = ITEM_TYPES[itemType];
      let records;
      try {
        records = await readAll(master, config.collection, {
          filter: { "refProject._id": context.projectId },
          fields: config.fields,
          sort: { _id: 1 }
        }, limitRead);
      } catch (error) {
        if (context.itemType !== "overview") throw error;
        warnings.push({ collection: `current-project ${config.plural}`, message: safeMessage(error) });
        records = [];
      }
      return records.filter((record) => {
        const recordProject = record && record.refProject && record.refProject._id;
        return !recordProject || String(recordProject) === context.projectId;
      }).map((record) => ({ itemType, record }));
    }));
    const projectDescriptors = currentPages.flat();
    const itemIds = projectDescriptors.map((entry) => String(entry.record._id));
    const itemIdSet = new Set(itemIds);

    async function optionalRead(label, reader) {
      try {
        return await reader();
      } catch (error) {
        warnings.push({ collection: label, message: safeMessage(error) });
        return [];
      }
    }

    progress({ phase: "projects", message: "Loading your assigned Helix projects…" });
    const rawDirectories = context.contactId
      ? await optionalRead("assigned projects", () => readAll(master, COLLECTIONS.directories, {
        filter: { "refContact._id": context.contactId, isActive: true },
        fields: DIRECTORY_FIELDS,
        sort: { "refProject.name": 1 }
      }, limitRead))
      : [];
    if (!context.contactId) {
      warnings.push({
        collection: "assigned projects",
        message: "Helix did not expose the current contact identifier; only the current project can be scanned."
      });
    }
    const assignedProjectsById = new Map();
    rawDirectories.forEach((directory) => {
      const refProject = directory && directory.refProject;
      const refContact = directory && directory.refContact;
      if (!refProject || !refProject._id || directory.isActive === false) return;
      if (refContact && refContact._id && context.contactId
        && String(refContact._id) !== context.contactId) return;
      const projectId = String(refProject._id);
      assignedProjectsById.set(projectId, {
        id: projectId,
        name: String(refProject.name || projectId)
      });
    });
    if (!assignedProjectsById.has(String(context.projectId))) {
      assignedProjectsById.set(String(context.projectId), {
        id: String(context.projectId),
        name: context.projectName
      });
    }
    const projects = Array.from(assignedProjectsById.values())
      .sort((a, b) => a.name.localeCompare(b.name));
    const projectNames = new Map(projects.map((project) => [project.id, project.name]));

    async function readOpenAcrossProjects(label, collection, fields) {
      const pages = await mapWithConcurrency(projects, SCAN_READ_CONCURRENCY, async (project, index) => {
        progress({
          phase: "queue",
          message: `Checking ${label} in project ${index + 1} of ${projects.length}…`,
          completed: index + 1,
          total: projects.length
        });
        try {
          return await readAll(master, collection, {
            filter: {
              "refProject._id": project.id,
              "status.rank": { $lt: 1300 }
            },
            fields,
            sort: { _id: 1 }
          }, limitRead);
        } catch (error) {
          warnings.push({ collection: `${label} — ${project.name}`, message: safeMessage(error) });
          return [];
        }
      });
      return pages.flat();
    }

    progress({ phase: "queue", message: "Loading open RFIs and Submittals across your assigned projects…" });
    const queueRfisPromise = readOpenAcrossProjects("open RFIs", COLLECTIONS.rfis, RFI_FIELDS);
    const queueSubmittalsPromise = readOpenAcrossProjects("open Submittals", COLLECTIONS.submittals, SUBMITTAL_FIELDS);

    progress({ phase: "activity", message: "Loading your comments and uploads…" });
    const commentsPromise = optionalRead("comments", () => readAll(master, COLLECTIONS.comments, {
      filter: { idProject: context.projectId, "refDirectory._id": context.user.id },
      fields: COMMENT_FIELDS,
      sort: { "_kmd.ect": -1 }
    }, limitRead));
    const attachmentsPromise = optionalRead("attachments", () => readAll(master, COLLECTIONS.attachments, {
      filter: {
        idProject: context.projectId,
        $or: [
          { "refDirectoryCreator._id": context.user.id },
          { nameEditor: context.user.fullName }
        ]
      },
      fields: ATTACHMENT_FIELDS,
      sort: { "_kmd.ect": -1 }
    }, limitRead));

    const historyBatches = chunks(itemIds, HISTORY_BATCH_SIZE);
    const historyPromise = optionalRead("history", async () => {
      const pages = await mapWithConcurrency(historyBatches, SCAN_READ_CONCURRENCY, async (batch, index) => {
        progress({
          phase: "history",
          message: `Checking edit history ${index + 1} of ${historyBatches.length}…`,
          completed: index + 1,
          total: historyBatches.length
        });
        return readAll(master, COLLECTIONS.histories, {
          filter: {
            originalId: { $in: batch },
            $or: [
              { nameEditor: context.user.fullName },
              { "refDirectoryResponseEditor._id": context.user.id },
              { "refDirectoryAccepted._id": context.user.id },
              { "refDirectoryRejected._id": context.user.id }
            ]
          },
          fields: HISTORY_FIELDS,
          sort: { "_kmd.lmt": -1 }
        }, limitRead);
      });
      return pages.flat();
    });

    const [rawComments, rawAttachments, rawHistories, rawQueueRfis, rawQueueSubmittals] = await Promise.all([
      commentsPromise,
      attachmentsPromise,
      historyPromise,
      queueRfisPromise,
      queueSubmittalsPromise
    ]);

    const comments = rawComments
      .filter((record) => itemIdSet.has(String(record.idItem)) && sameIdentity(record.refDirectory, context.user))
      .map(sanitizeComment);
    const attachments = rawAttachments
      .filter((record) => {
        if (!itemIdSet.has(String(record.idItem))) {
          return false;
        }
        if (sameIdentity(record.refDirectoryCreator, context.user)) {
          return true;
        }
        return !getIdentityId(record.refDirectoryCreator)
          && normalizeName(record.nameEditor) === normalizeName(context.user.fullName);
      })
      .map(sanitizeAttachment);
    const histories = rawHistories
      .filter((record) => {
        if (!itemIdSet.has(String(record.originalId))) {
          return false;
        }
        return normalizeName(record.nameEditor) === normalizeName(context.user.fullName)
          || sameIdentity(record.refDirectoryResponseEditor, context.user)
          || sameIdentity(record.refDirectoryAccepted, context.user)
          || sameIdentity(record.refDirectoryRejected, context.user);
      })
      .map(sanitizeHistory);

    const queueDescriptors = [
      ...rawQueueRfis.map((record) => ({ itemType: "rfi", record })),
      ...rawQueueSubmittals.map((record) => ({ itemType: "submittal", record }))
    ].filter((entry) => entry.record && entry.record._id
      && entry.record.refProject && entry.record.refProject._id);
    const queueItems = queueDescriptors.map((entry) => sanitizeItem(
      entry.record,
      entry.itemType,
      projectNames,
      context
    ));
    itemNavigationCache = new Map([...queueDescriptors, ...projectDescriptors].map((entry) => [
      `${entry.itemType}:${String(entry.record._id)}`,
      {
        itemType: entry.itemType,
        itemId: String(entry.record._id),
        projectId: String(entry.record.refProject._id)
      }
    ]));
    progress({ phase: "complete", message: "Building your activity index…" });
    const items = projectDescriptors.map((entry) => sanitizeItem(
      entry.record,
      entry.itemType,
      projectNames,
      context
    ));
    const result = {
      context,
      items,
      queueItems,
      projects,
      comments,
      attachments,
      histories,
      warnings,
      scannedAt: new Date().toISOString()
    };
    if (context.itemType === "rfi") {
      result.rfis = items;
    }
    return result;
  }

  async function prepareItemDownload(windowObject, itemId, itemType, projectId) {
    const target = getPackageTarget(windowObject, itemId, itemType, projectId);

    const master = resolveMaster(windowObject);
    const config = ITEM_TYPES[target.itemType];
    const record = await readCompleteDetailRecord(
      master,
      config,
      target.itemId,
      target.projectId
    );
    if (!record) {
      throw new Error("This item could not be found on the current Helix page.");
    }
    if (typeof master.callCustomEndpoint !== "function") {
      throw new Error("Helix's protected file download service is unavailable.");
    }

    const featureName = target.itemType === "rfi" ? "RFIs" : "Submittals";
    const context = getContext(windowObject);
    const accessRules = [{ isGlobal: true }, { idProjects: { $in: [target.projectId] } }];
    if (context.contactId) accessRules.push({ idContacts: { $in: [context.contactId] } });
    const reportFilter = {
      functionName: "DetailView",
      isActive: true,
      idProjectsExcluded: { $nin: [target.projectId] },
      $and: [
        { $or: accessRules },
        reportFeatureFilter(featureName)
      ]
    };
    const rawReadLevel = windowObject.UserInfo && windowObject.UserInfo.rpl;
    const readLevel = Number(rawReadLevel);
    if (rawReadLevel !== null && rawReadLevel !== undefined && Number.isFinite(readLevel)) {
      reportFilter.minReadLevel = { $lte: readLevel };
    }

    const [attachmentRecords, reports] = await Promise.all([
      readAll(master, COLLECTIONS.attachments, {
        filter: {
          idItem: target.itemId,
          isActive: true,
          isCurrent: true
        },
        fields: DOWNLOAD_ATTACHMENT_FIELDS,
        sort: { "_kmd.ect": 1 }
      }),
      readAll(master, COLLECTIONS.reports, {
        filter: reportFilter,
        fields: REPORT_FIELDS,
        sort: { reportName: 1 }
      })
    ]);
    if (attachmentRecords.length > 250) {
      throw new Error("This item has more than 250 current attachments. Download it directly from Helix instead.");
    }
    const report = chooseDetailsReport(reports, featureName);
    if (!report) {
      throw new Error(`Helix did not return an available Details (${featureName}) report for this item.`);
    }

    const attachmentResults = await mapWithConcurrency(
      attachmentRecords.filter((attachment) => attachment && attachment._id),
      SCAN_READ_CONCURRENCY,
      async (attachment) => {
        const protectedFile = await master.callCustomEndpoint("getDownloadURL", {
          collectionName: COLLECTIONS.attachments,
          _id: String(attachment._id),
          propName: "file",
          preferThumbnail: true,
          idProject: target.projectId
        });
        const url = protectedFile && protectedFile.downloadURL;
        if (typeof url !== "string" || !/^https:\/\//i.test(url)) {
          throw new Error(`Helix did not provide a download URL for ${attachment.description || attachment.filename || "an attachment"}.`);
        }
        return {
          id: String(attachment._id),
          filename: String(attachment.description || attachment.filename || protectedFile.filename || "Attachment"),
          uploadedAt: toIso(attachment._kmd && attachment._kmd.ect),
          url
        };
      }
    );

    return {
      prepared: true,
      itemId: target.itemId,
      itemType: target.itemType,
      projectId: target.projectId,
      report: buildReportDownload(report, record, { windowObject }, target),
      attachments: attachmentResults
    };
  }

  async function openItem(windowObject, itemId, itemType, projectId) {
    const target = getDownloadTarget(windowObject, itemId, itemType, projectId);
    if (target.navigateTo) {
      return {
        opened: false,
        itemId: String(itemId),
        navigateTo: target.navigateTo
      };
    }

    const master = resolveMaster(windowObject);
    const config = ITEM_TYPES[target.itemType];
    if (typeof master.loadDetailView !== "function") {
      throw new Error("Helix's detail viewer is unavailable.");
    }
    const record = await readCompleteDetailRecord(master, config, itemId, target.projectId);
    if (!record) {
      throw new Error("This item could not be found on the current Helix page.");
    }
    const detailState = await closeOtherDetailView(windowObject, master, itemId);
    if (detailState === "already-open") {
      return { opened: true, itemId: String(itemId), alreadyOpen: true };
    }
    const gridNamespace = await waitForPageGridNamespace(windowObject, config.collection);
    const detailRecord = normalizeDetailRecord(gridNamespace, record, itemId, target.projectId);
    await master.loadDetailView(null, detailRecord);
    return { opened: true, itemId: String(itemId) };
  }

  return Object.freeze({
    COLLECTIONS,
    ITEM_TYPES,
    parsePageContext,
    parseProjectId,
    getContext,
    readAll,
    scan,
    prepareItemDownload,
    openItem,
    openRfi: openItem,
    _test: Object.freeze({
      sameIdentity,
      sanitizeRfi,
      sanitizeSubmittal,
      sanitizeItem,
      sanitizeComment,
      sanitizeAttachment,
      sanitizeHistory,
      closeOtherDetailView,
      findPageGridNamespace,
      normalizeDetailRecord,
      chooseDetailsReport,
      buildReportDownload,
      navigationDescriptors: () => Array.from(itemNavigationCache.values()).map((value) => Object.assign({}, value)),
      resetCache: () => {
        itemNavigationCache = new Map();
      }
    })
  });
});
