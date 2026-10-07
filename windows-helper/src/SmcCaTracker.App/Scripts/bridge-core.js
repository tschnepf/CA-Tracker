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
  // Just enough of each saved version to tell whose court the item was in.
  const COURT_HISTORY_FIELDS = ["_id", "originalId", "status", "statusMessage", "workflow", "_kmd"];
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
      throw new Error("Open a Helix project Overview, RFI, or Submittals page before refreshing.");
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
          : null,
        email: signedInEmail(windowObject)
      }
    };
  }

  // The signed-in contact's email. Helix gives it for the person signed in, not for others.
  function signedInEmail(windowObject) {
    const contact = windowObject && windowObject.loginObject && windowObject.loginObject.contact;
    const email = contact && typeof contact.email === "string" ? contact.email.trim() : "";
    return email.includes("@") ? email : null;
  }

  // Who is signed in, read from any Helix page (not only a project page). Null until Helix has
  // loaded the user. Your entry in a project's directory (UserInfo.directoryInfo) is used when the
  // page has one; Helix's home page leaves it empty (since Helix 5.1.90), and then the signed-in
  // contact (loginObject.contact) says who you are.
  function currentUser(windowObject) {
    const directoryInfo = windowObject && windowObject.UserInfo && windowObject.UserInfo.directoryInfo;
    const userId = getIdentityId(directoryInfo);
    const fullName = getIdentityName(directoryInfo);
    if (directoryInfo && userId && fullName) {
      const contactId = directoryInfo.refContact && directoryInfo.refContact._id;
      const companyId = getIdentityCompanyId(directoryInfo);
      const company = getIdentityCompanyName(directoryInfo);
      return {
        id: String(userId),
        contactId: contactId ? String(contactId) : null,
        fullName: String(fullName),
        companyId: companyId ? String(companyId) : null,
        company: company ? String(company) : null,
        email: signedInEmail(windowObject)
      };
    }

    const contact = windowObject && windowObject.loginObject && windowObject.loginObject.contact;
    const contactId = contact && contact._id;
    const contactName = getIdentityName(contact)
      || (contact && [contact.nameFirst, contact.nameLast].filter(Boolean).join(" "));
    if (!contactId || !contactName) return null;
    const companyId = getIdentityCompanyId(contact);
    const company = getIdentityCompanyName(contact);
    return {
      id: String(contactId),
      contactId: String(contactId),
      fullName: String(contactName),
      companyId: companyId ? String(companyId) : null,
      company: company ? String(company) : null,
      email: signedInEmail(windowObject)
    };
  }

  // The signed-in user and the Helix projects they are assigned to, read-only, from any Helix page.
  async function listAssignedProjects(windowObject) {
    const user = currentUser(windowObject);
    if (!user) throw new Error("Helix is still loading the currently logged-in user.");
    if (!user.contactId) return { user, projects: [] };
    const master = resolveMaster(windowObject);
    const directories = await readAll(master, COLLECTIONS.directories, {
      filter: { "refContact._id": user.contactId, isActive: true },
      fields: DIRECTORY_FIELDS,
      sort: { "refProject.name": 1 }
    }, createConcurrencyLimiter(SCAN_READ_CONCURRENCY));
    const byId = new Map();
    for (const directory of directories) {
      const refProject = directory && directory.refProject;
      const refContact = directory && directory.refContact;
      if (!refProject || !refProject._id || directory.isActive === false) continue;
      if (refContact && refContact._id && String(refContact._id) !== user.contactId) continue;
      byId.set(String(refProject._id), { id: String(refProject._id), name: String(refProject.name || refProject._id) });
    }
    return { user, projects: Array.from(byId.values()).sort((a, b) => a.name.localeCompare(b.name)) };
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

  // The item to work on. Told its type and project, any Helix page will do (the Helix home page too, for
  // projects whose RFI or Submittals pages Helix sends to its new app); otherwise they come from the
  // project page open.
  function getPackageTarget(windowObject, itemId, itemType, projectId) {
    const page = itemType && projectId ? null : parsePageContext(windowObject.location && windowObject.location.href);
    if (!page && !(itemType && projectId)) {
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
    throw new Error(`${collectionName} exceeded the ${MAX_RECORDS.toLocaleString()} record safety limit.`);
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

  // What decides whose court an item is in: its status, its "waiting on" text, and the first
  // required review step not yet done (and who it is assigned to).
  function courtSignature(record) {
    const status = record && record.status && record.status.name ? String(record.status.name) : "";
    const message = record && record.statusMessage ? String(record.statusMessage).trim() : "";
    const required = record && record.workflow && Array.isArray(record.workflow.workflowRequired)
      ? record.workflow.workflowRequired
      : [];
    const index = required.findIndex((entry) => !(entry && (entry.dateReviewed || entry.refDirectoryReviewed)));
    const step = index >= 0 ? required[index] : null;
    const assigned = step && step.refDirectoryAssigned
      ? getIdentityId(step.refDirectoryAssigned) || getIdentityName(step.refDirectoryAssigned) || ""
      : "";
    return JSON.stringify([status, message, index, String(assigned)]);
  }

  // When an item reached its current court: the save time of the oldest version in the latest
  // unbroken run of saved versions whose court matches the item as it is now. Helix doesn't store
  // this date itself. Null when there is no saved history to go on.
  function courtStartFromHistory(record, versions) {
    const history = (Array.isArray(versions) ? versions : [])
      .map((version) => ({ at: toIso(version && version._kmd && version._kmd.lmt), signature: courtSignature(version) }))
      .filter((entry) => entry.at);
    if (history.length === 0) return null;
    const current = courtSignature(record);
    const currentAt = toIso(record && record._kmd && record._kmd.lmt);
    const timeline = history.slice();
    if (currentAt && !timeline.some((entry) => entry.at === currentAt)) {
      timeline.push({ at: currentAt, signature: current });
    }
    timeline.sort((left, right) => left.at.localeCompare(right.at));
    let start = null;
    for (let index = timeline.length - 1; index >= 0; index -= 1) {
      if (timeline[index].signature !== current) break;
      start = timeline[index].at;
    }
    return start;
  }

  function isOpenRecord(record) {
    const status = normalizeName(record && record.status && record.status.name);
    const rank = record && record.status && Number.isFinite(record.status.rank) ? record.status.rank : null;
    return !((rank !== null && rank >= 1300) || status === "complete" || status === "voided");
  }

  // failures (optional Set): ids whose history couldn't be read, so a caller can try them again.
  async function readCourtStarts(master, descriptors, readLimiter, progress, failures) {
    const open = descriptors.filter((entry) => entry.record && entry.record._id && isOpenRecord(entry.record));
    const ids = open.map((entry) => String(entry.record._id));
    const batches = chunks(ids, HISTORY_BATCH_SIZE);
    const pages = await mapWithConcurrency(batches, SCAN_READ_CONCURRENCY, async (batch, index) => {
      progress({
        phase: "court",
        message: `Checking when open items reached their current court ${index + 1} of ${batches.length}…`,
        completed: index + 1,
        total: batches.length
      });
      // One batch failing only loses those items' dates.
      try {
        return await readAll(master, COLLECTIONS.histories, {
          filter: { originalId: { $in: batch } },
          fields: COURT_HISTORY_FIELDS,
          sort: { "_kmd.lmt": 1 }
        }, readLimiter);
      } catch (_error) {
        if (failures) batch.forEach((id) => failures.add(id));
        return [];
      }
    });
    const versionsById = new Map();
    for (const version of pages.flat()) {
      const key = String(version && version.originalId);
      if (!versionsById.has(key)) versionsById.set(key, []);
      versionsById.get(key).push(version);
    }
    const starts = new Map();
    for (const entry of open) {
      const key = String(entry.record._id);
      const start = courtStartFromHistory(entry.record, versionsById.get(key));
      if (start) starts.set(key, start);
    }
    return starts;
  }

  function safeMessage(error) {
    if (error instanceof Error && error.message) {
      return error.message.slice(0, 240);
    }
    return "The collection could not be read.";
  }

  // Every RFI and/or Submittal of the page's project (both on an Overview page). On an Overview page a
  // type that can't be read is noted in warnings and skipped; otherwise the error is thrown.
  async function readProjectDescriptors(master, context, limitRead, warnings) {
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
    return currentPages.flat();
  }

  // The signed-in user plus a project to read, so any project can be read from one Helix page
  // (the background sync stays on one page instead of opening every project).
  function syncContext(windowObject, projectId, projectName) {
    const user = currentUser(windowObject);
    if (!user) {
      throw new Error("Helix is still loading the currently logged-in user.");
    }
    return {
      projectId: String(projectId),
      projectName: projectName ? String(projectName) : String(projectId),
      contactId: user.contactId,
      itemType: "overview",
      itemLabel: "Helix",
      itemLabelPlural: "Items",
      user
    };
  }

  // One project's RFIs and Submittals — or, with options.since (ISO time), only those changed since
  // then, or with options.ids only those items — each with when it reached its current court. Rows
  // whose court history couldn't be read are marked courtUnknown. Read-only.
  async function readProjectItems(windowObject, options) {
    const projectId = String(options && options.projectId || "");
    if (!projectId) {
      throw new Error("A Helix project is required.");
    }
    const master = resolveMaster(windowObject);
    const limitRead = createConcurrencyLimiter(SCAN_READ_CONCURRENCY);
    const progress = typeof options.onProgress === "function" ? options.onProgress : function noop() {};
    const since = options.since ? String(options.since) : null;
    const onlyIds = Array.isArray(options.ids) ? options.ids.map(String) : null;
    if (onlyIds && onlyIds.length === 0) {
      return [];
    }
    const pages = await Promise.all(["rfi", "submittal"].map(async (itemType) => {
      const config = ITEM_TYPES[itemType];
      const filter = { "refProject._id": projectId };
      if (since) {
        filter["_kmd.lmt"] = { $gte: since };
      }
      if (onlyIds) {
        filter._id = { $in: onlyIds };
      }
      const records = await readAll(master, config.collection, { filter, fields: config.fields, sort: { _id: 1 } }, limitRead);
      return records
        .filter((record) => record && record._id
          && (!record.refProject || !record.refProject._id || String(record.refProject._id) === projectId))
        .map((record) => ({ itemType, record }));
    }));
    const descriptors = pages.flat();
    const failures = new Set();
    const courtStarts = await readCourtStarts(master, descriptors, limitRead, progress, failures).catch(() => {
      descriptors.forEach((entry) => failures.add(String(entry.record._id)));
      return new Map();
    });
    const context = { projectId, projectName: options.projectName || projectId };
    return descriptors.map((entry) => ({
      itemType: entry.itemType,
      id: String(entry.record._id),
      modifiedAt: toIso(entry.record._kmd && entry.record._kmd.lmt),
      courtUnknown: failures.has(String(entry.record._id)),
      item: Object.assign(sanitizeItem(entry.record, entry.itemType, new Map(), context),
        { courtSince: courtStarts.get(String(entry.record._id)) || null })
    }));
  }

  // The ids of every RFI and Submittal in a project (a small read), so a changes-only sync can drop
  // items that were deleted or moved to another project.
  async function readProjectItemIds(windowObject, projectId) {
    const master = resolveMaster(windowObject);
    const limitRead = createConcurrencyLimiter(SCAN_READ_CONCURRENCY);
    const result = {};
    await Promise.all(["rfi", "submittal"].map(async (itemType) => {
      const records = await readAll(master, ITEM_TYPES[itemType].collection, {
        filter: { "refProject._id": String(projectId) },
        fields: ["_id", "refProject"],
        sort: { _id: 1 }
      }, limitRead);
      result[itemType] = records
        .filter((record) => record && record._id
          && (!record.refProject || !record.refProject._id || String(record.refProject._id) === String(projectId)))
        .map((record) => String(record._id));
    }));
    return result;
  }

  // One project's items and when each reached its current court, without the cross-project queue,
  // comments or uploads a full refresh also reads. Used to update CA Tracker quickly after an item
  // was worked on in its browser. Shaped like scan()'s result so the same snapshot builder applies.
  async function scanProjectItems(windowObject, onProgress) {
    const context = getContext(windowObject);
    const master = resolveMaster(windowObject);
    const limitRead = createConcurrencyLimiter(SCAN_READ_CONCURRENCY);
    const warnings = [];
    const progress = typeof onProgress === "function" ? onProgress : function noop() {};
    progress({ phase: "items", message: `Loading ${context.itemLabelPlural} for this project…` });
    const descriptors = await readProjectDescriptors(master, context, limitRead, warnings);
    const courtStarts = await readCourtStarts(master, descriptors, limitRead, progress).catch((error) => {
      warnings.push({ collection: "court history", message: safeMessage(error) });
      return new Map();
    });
    const items = descriptors.map((entry) => Object.assign(
      sanitizeItem(entry.record, entry.itemType, new Map(), context),
      { courtSince: courtStarts.get(String(entry.record._id)) || null }
    ));
    return {
      context,
      items,
      queueItems: [],
      projects: [],
      comments: [],
      attachments: [],
      histories: [],
      warnings,
      scannedAt: new Date().toISOString()
    };
  }

  // What decides whose court one item is in, read fresh from Helix: cheap enough to check every few
  // seconds while the item is open in CA Tracker, to notice when it has been sent on.
  async function itemCourtState(windowObject, itemId, itemType, projectId) {
    const config = ITEM_TYPES[itemType];
    if (!config) {
      throw new Error("The requested Helix item type is not supported.");
    }
    const master = resolveMaster(windowObject);
    const records = await readAll(master, config.collection, {
      filter: { _id: String(itemId), "refProject._id": String(projectId) },
      fields: config.fields,
      sort: { _id: 1 }
    });
    const record = records.find((candidate) => candidate && String(candidate._id) === String(itemId));
    if (!record) {
      throw new Error("Helix no longer lists this item.");
    }
    return {
      signature: courtSignature(record),
      status: record.status && record.status.name ? String(record.status.name) : null,
      modifiedAt: toIso(record._kmd && record._kmd.lmt)
    };
  }

  async function scan(windowObject, onProgress) {
    const context = getContext(windowObject);
    const master = resolveMaster(windowObject);
    const limitRead = createConcurrencyLimiter(SCAN_READ_CONCURRENCY);
    const warnings = [];
    const progress = typeof onProgress === "function" ? onProgress : function noop() {};

    progress({ phase: "items", message: `Loading ${context.itemLabelPlural} for this project…` });
    const projectDescriptors = await readProjectDescriptors(master, context, limitRead, warnings);
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

    const courtStartsPromise = readCourtStarts(master, projectDescriptors, limitRead, progress).catch((error) => {
      warnings.push({ collection: "court history", message: safeMessage(error) });
      return new Map();
    });

    const [rawComments, rawAttachments, rawHistories, rawQueueRfis, rawQueueSubmittals, courtStarts] = await Promise.all([
      commentsPromise,
      attachmentsPromise,
      historyPromise,
      queueRfisPromise,
      queueSubmittalsPromise,
      courtStartsPromise
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
    const items = projectDescriptors.map((entry) => Object.assign(sanitizeItem(
      entry.record,
      entry.itemType,
      projectNames,
      context
    ), { courtSince: courtStarts.get(String(entry.record._id)) || null }));
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
    // Who you are, for the reports you may see: the page's user, or Helix's login on its home page.
    const user = currentUser(windowObject);
    if (!user) {
      throw new Error("Helix is still loading the currently logged-in user.");
    }
    const accessRules = [{ isGlobal: true }, { idProjects: { $in: [target.projectId] } }];
    if (user.contactId) accessRules.push({ idContacts: { $in: [user.contactId] } });
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
    // The Details PDF is optional: without it the attachments can still be downloaded.
    const report = chooseDetailsReport(reports, featureName);
    let reportDownload = null;
    let reportError = null;
    if (!report) {
      reportError = `Helix did not offer a Details (${featureName}) report for this item.`;
    } else {
      try {
        reportDownload = buildReportDownload(report, record, { windowObject }, target);
      } catch (error) {
        reportError = safeMessage(error);
      }
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
      report: reportDownload,
      reportError,
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
    currentUser,
    listAssignedProjects,
    readAll,
    scan,
    prepareItemDownload,
    openItem,
    scanProjectItems,
    itemCourtState,
    syncContext,
    readProjectItems,
    readProjectItemIds,
    openRfi: openItem,
    _test: Object.freeze({
      sameIdentity,
      sanitizeRfi,
      sanitizeSubmittal,
      sanitizeItem,
      sanitizeComment,
      sanitizeAttachment,
      sanitizeHistory,
      courtSignature,
      courtStartFromHistory,
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
