using System.IO;
using System.Reflection;
using System.Text.Json;
using SmcCaTracker.Core.Models;

namespace SmcCaTracker.App.Services;

/// <summary>
/// The JavaScript the built-in browser runs inside a Helix or Procore page. The read-only scanning
/// code lives in the Scripts folder (bridge-core.js, activity-core.js, procore-core.js, procore-sync.js, snapshot.js)
/// and is embedded at build time. Results come back with chrome.webview.postMessage, WebView2's
/// channel from the page to the app.
/// </summary>
public static class ProviderScripts
{
    public static IReadOnlyList<string> LibrariesFor(string provider) =>
        string.Equals(provider, Providers.Procore, StringComparison.OrdinalIgnoreCase)
            ? new[] { Load("activity-core.js"), Load("procore-core.js"), Load("snapshot.js") }
            : new[] { Load("bridge-core.js"), Load("activity-core.js"), Load("snapshot.js") };

    /// <summary>The libraries the background Helix sync page needs, in load order.</summary>
    public static IReadOnlyList<string> HelixSyncLibraries =>
        new[] { Load("bridge-core.js"), Load("activity-core.js"), Load("snapshot.js"), Load("sync-core.js") };

    /// <summary>
    /// Background sync: posts "sync-readiness" with state signedOut / loading / ready (and the user),
    /// or "sync-missing" when the page has reloaded and needs the libraries again.
    /// </summary>
    public static string HelixSyncReadiness(string requestId) => """
        ((requestId) => {
          const post = (message) => window.chrome.webview.postMessage(Object.assign({ requestId }, message));
          if (typeof CaTrackerSync === "undefined") { post({ type: "sync-missing" }); return; }
          try {
            post(Object.assign({ type: "sync-readiness" }, CaTrackerSync.readiness(window)));
          } catch (error) {
            post({ type: "sync-readiness", state: "loading", message: String(error && error.message || error) });
          }
        })(
        """ + JsonSerializer.Serialize(requestId) + ");";

    /// <summary>
    /// Background sync of one Helix project: posts "sync-result" with the project's snapshot (all its
    /// items, read in full or as changes since the last read), "sync-error", or "sync-missing".
    /// </summary>
    public static string HelixSyncProject(string requestId, string projectId, string projectName, bool full, double fullAfterMs)
    {
        var request = JsonSerializer.Serialize(new { requestId, projectId, projectName, full, fullAfterMs });
        return """
            (async (request) => {
              const post = (message) => window.chrome.webview.postMessage(Object.assign({ requestId: request.requestId }, message));
              if (typeof CaTrackerSync === "undefined") { post({ type: "sync-missing" }); return; }
              try {
                const result = await CaTrackerSync.syncProject(window, request);
                post({
                  type: "sync-result",
                  snapshot: result.snapshot,
                  mode: result.mode,
                  itemCount: result.itemCount,
                  changedCount: result.changedCount,
                  user: result.user
                });
              } catch (error) {
                post({ type: "sync-error", message: String(error && error.message || error) });
              }
            })(
            """ + request + ");";
    }

    /// <summary>Posts "sync-sample" with an anonymized copy of a synced project's items (for test cases).</summary>
    public static string HelixExportSample(string requestId, string projectId)
    {
        var request = JsonSerializer.Serialize(new { requestId, projectId });
        return """
            ((request) => {
              const post = (message) => window.chrome.webview.postMessage(Object.assign({ requestId: request.requestId }, message));
              if (typeof CaTrackerSync === "undefined") { post({ type: "sync-missing" }); return; }
              try {
                post({ type: "sync-sample", sample: CaTrackerSync.exportSample(window, request.projectId) });
              } catch (error) {
                post({ type: "sync-error", message: String(error && error.message || error) });
              }
            })(
            """ + request + ");";
    }

    /// <summary>Background sync: posts "sync-catalog" with the Helix projects the user is assigned to.</summary>
    public static string HelixSyncCatalog(string requestId) => """
        (async (requestId) => {
          const post = (message) => window.chrome.webview.postMessage(Object.assign({ requestId }, message));
          if (typeof HelixBridgeCore === "undefined") { post({ type: "sync-missing" }); return; }
          try {
            const assigned = await HelixBridgeCore.listAssignedProjects(window);
            post({
              type: "sync-catalog",
              catalog: {
                provider: "helix",
                projects: assigned.projects.map((project) => ({
                  projectId: project.id, accountId: null, accountName: null, name: project.name, host: "project.helixteam.com"
                }))
              }
            });
          } catch (error) {
            post({ type: "sync-error", message: String(error && error.message || error) });
          }
        })(
        """ + JsonSerializer.Serialize(requestId) + ");";

    /// <summary>The library the background Procore page needs (discovery and reading; read-only).</summary>
    public static string ProcoreSyncLibrary => Load("procore-sync.js");

    /// <summary>
    /// Lists the companies this Procore sign-in belongs to and every active project in each. Posts
    /// "procore-discovered" with { user, companies, projects, skippedInactive }, or "procore-error" with
    /// the HTTP status (when Procore won't list companies for this sign-in, the projects linked on the
    /// current page are sent with "procore-discovered" and fromPage: true instead), or "procore-missing".
    /// </summary>
    public static string ProcoreDiscover(string requestId) => """
        (async (requestId) => {
          const post = (message) => window.chrome.webview.postMessage(Object.assign({ requestId }, message));
          if (typeof CaTrackerProcoreSync === "undefined") { post({ type: "procore-missing" }); return; }
          try {
            const result = await CaTrackerProcoreSync.discover(window, {});
            post(Object.assign({ type: "procore-discovered", fromPage: false }, result));
          } catch (error) {
            const status = Number(error && error.status || 0);
            if (status !== 401) {
              // Procore wouldn't list companies here: take the projects the home page links to.
              const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
              for (let wait = 0; wait < 20; wait += 1) {
                const projects = CaTrackerProcoreSync.projectsFromPage(document, window.location.hostname);
                if (projects.length > 0) {
                  post({ type: "procore-discovered", fromPage: true, user: null, companies: [], projects, skippedInactive: 0,
                    message: String(error && error.message || error) });
                  return;
                }
                await sleep(500);
              }
            }
            post({ type: "procore-error", status, message: String(error && error.message || error) });
          }
        })(
        """ + JsonSerializer.Serialize(requestId) + ");";

    /// <summary>
    /// Lists the projects of the given companies on this page's Procore server (for companies hosted
    /// on a different Procore server than your own). Posts "procore-discovered" like <see cref="ProcoreDiscover"/>.
    /// </summary>
    public static string ProcoreListCompanies(string requestId, IEnumerable<(string Id, string? Name)> companies)
    {
        var request = JsonSerializer.Serialize(new
        {
            requestId,
            companies = companies.Select(company => new { id = company.Id, name = company.Name, active = true })
        });
        return """
            (async (request) => {
              const post = (message) => window.chrome.webview.postMessage(Object.assign({ requestId: request.requestId }, message));
              if (typeof CaTrackerProcoreSync === "undefined") { post({ type: "procore-missing" }); return; }
              try {
                const result = await CaTrackerProcoreSync.listCompanyProjects(window, request.companies, {});
                post(Object.assign({ type: "procore-discovered", fromPage: false, user: null }, result));
              } catch (error) {
                post({ type: "procore-error", status: Number(error && error.status || 0), message: String(error && error.message || error) });
              }
            })(
            """ + request + ");";
    }

    /// <summary>
    /// Background Procore read through the signed-in Procore page: asks Procore's REST service (same
    /// site, same sign-in as its web pages) for every RFI and Submittal of a project, page by page.
    /// Posts "procore-result" with both lists (and any tool you can't see there), "procore-error"
    /// with the HTTP status, or "procore-missing".
    /// </summary>
    public static string ProcoreReadProject(string requestId, string projectId, string? companyId, string? updatedSince = null)
    {
        var request = JsonSerializer.Serialize(new { requestId, projectId, companyId, updatedSince });
        return """
            (async (request) => {
              const post = (message) => window.chrome.webview.postMessage(Object.assign({ requestId: request.requestId }, message));
              if (typeof CaTrackerProcoreSync === "undefined") { post({ type: "procore-missing" }); return; }
              try {
                const result = await CaTrackerProcoreSync.readProject(window, request, {});
                post({ type: "procore-result", rfis: result.rfis, submittals: result.submittals, unavailable: result.unavailable, changesOnly: result.changesOnly });
              } catch (error) {
                post({ type: "procore-error", status: Number(error && error.status || 0), message: String(error && error.message || error) });
              }
            })(
            """ + request + ");";
    }

    public static string HelixScan => """
        (async () => {
          const post = (message) => window.chrome.webview.postMessage(message);
          const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
          try {
            const deadline = Date.now() + 120000;
            let raw = null;
            let lastError = null;
            while (!raw && Date.now() < deadline) {
              try {
                HelixBridgeCore.getContext(window);
                raw = await HelixBridgeCore.scan(window, (update) => post({
                  type: "progress",
                  message: String(update && update.message || "Reading Helix…")
                }));
              } catch (error) {
                lastError = error;
                const text = String(error && error.message || "");
                if (!/still loading|not ready|Open a Helix project|getIdToken|Cannot read properties of (?:null|undefined)/i.test(text)) throw error;
                post({ type: "progress", message: "Waiting for Helix to finish loading…" });
                await sleep(1000);
              }
            }
            if (!raw) throw lastError || new Error("Helix did not finish loading the project.");
            post({
              type: "result",
              snapshot: CaTrackerSnapshot.buildHelixSnapshot(raw),
              catalog: CaTrackerSnapshot.buildHelixCatalog(raw)
            });
          } catch (error) {
            post({ type: "error", message: String(error && error.message || error) });
          }
        })();
        """;

    /// <summary>The Helix page library needed by <see cref="HelixSignInCheck"/>.</summary>
    public static string HelixBridge => Load("bridge-core.js");

    /// <summary>
    /// Run after each Helix page loads in the Connect to Helix window: reports "signedIn" with the user
    /// and their assigned projects once Helix has loaded them, or "signedOut" while a sign-in form shows.
    /// </summary>
    public static string HelixSignInCheck => """
        (async () => {
          const post = (message) => window.chrome.webview.postMessage(message);
          const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
          const signInForm = () => Boolean(document.querySelector('input[type="password"]'));
          const report = (user, projects) => post({
            type: "signedIn",
            user: { fullName: user.fullName, company: user.company },
            catalog: {
              provider: "helix",
              projects: projects.map((project) => ({
                projectId: project.id, accountId: null, accountName: null, name: project.name, host: "project.helixteam.com"
              }))
            }
          });
          const deadline = Date.now() + 20000;
          let user = null;
          while (Date.now() < deadline) {
            if (signInForm()) { post({ type: "signedOut" }); return; }
            user = HelixBridgeCore.currentUser(window);
            if (user) {
              try {
                report(user, (await HelixBridgeCore.listAssignedProjects(window)).projects);
                return;
              } catch (error) {
                if (!/not ready|still loading/i.test(String(error && error.message || ""))) { report(user, []); return; }
              }
            }
            await sleep(1000);
          }
          if (user) report(user, []);
          else post({ type: signInForm() ? "signedOut" : "unknown" });
        })();
        """;

    /// <summary>Run after each Procore page loads in the Connect to Procore window.</summary>
    public static string ProcoreSignInCheck => """
        (async () => {
          const post = (message) => window.chrome.webview.postMessage(message);
          await new Promise((resolve) => setTimeout(resolve, 1500));
          post({ type: document.querySelector('input[type="password"]') ? "signedOut" : "signedIn" });
        })();
        """;

    /// <summary>
    /// Lists a Helix item's Details report and current attachments (names, upload dates and
    /// short-lived download links) from a page of the item's project. Read-only.
    /// </summary>
    public static string HelixPrepareDownload(string itemId, string itemType, string projectId)
    {
        var request = JsonSerializer.Serialize(new { itemId, itemType, projectId });
        return """
            (async (request) => {
              const post = (message) => window.chrome.webview.postMessage(message);
              const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
              try {
                const deadline = Date.now() + 60000;
                let lastError = null;
                while (Date.now() < deadline) {
                  try {
                    const result = await HelixBridgeCore.prepareItemDownload(window, request.itemId, request.itemType, request.projectId);
                    post({ type: "prepared", report: result.report, reportError: result.reportError || null, attachments: result.attachments });
                    return;
                  } catch (error) {
                    lastError = error;
                    // Helix finishes signing in after the page loads ("… reading 'getIdToken'"), so
                    // errors like that mean "not yet" rather than "no".
                    if (!/still loading|not ready|unavailable|getIdToken|Cannot read properties of (?:null|undefined)/i
                      .test(String(error && error.message || ""))) throw error;
                    await sleep(1500);
                  }
                }
                throw lastError || new Error("Helix did not finish loading.");
              } catch (error) {
                post({ type: "error", message: String(error && error.message || error) });
              }
            })(
            """ + request + ");";
    }

    /// <summary>
    /// Opens one Helix item in Helix's own detail viewer, on its project's RFI or Submittal page
    /// (Helix has no web address for a single item). Waits while Helix finishes loading.
    /// </summary>
    public static string HelixOpenItem(string itemId, string itemType, string projectId)
    {
        var request = JsonSerializer.Serialize(new { itemId, itemType, projectId });
        return """
            (async (request) => {
              const post = (message) => window.chrome.webview.postMessage(message);
              const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
              try {
                const deadline = Date.now() + 90000;
                let lastError = null;
                while (Date.now() < deadline) {
                  try {
                    const result = await HelixBridgeCore.openItem(window, request.itemId, request.itemType, request.projectId);
                    if (result && result.opened) {
                      post({ type: "opened", itemId: request.itemId });
                      return;
                    }
                    lastError = new Error("Helix didn't open the item.");
                  } catch (error) {
                    lastError = error;
                    if (!/still loading|not ready|unavailable|could not be found on the current|getIdToken|Cannot read properties of (?:null|undefined)/i
                      .test(String(error && error.message || ""))) throw error;
                  }
                  await sleep(1500);
                }
                throw lastError || new Error("Helix did not finish loading.");
              } catch (error) {
                post({ type: "error", itemId: request.itemId, message: String(error && error.message || error) });
              }
            })(
            """ + request + ");";
    }

    /// <summary>
    /// Run every few seconds while an item is open in CA Tracker's browser: posts "court" with what
    /// decides whose court the item is in, so the app notices when it has been sent on. Posts
    /// "court-missing" when the page library needs loading again (after Helix reloads the page).
    /// </summary>
    public static string HelixItemCourt(string itemId, string itemType, string projectId)
    {
        var request = JsonSerializer.Serialize(new { itemId, itemType, projectId });
        return """
            (async (request) => {
              const post = (message) => window.chrome.webview.postMessage(message);
              if (typeof HelixBridgeCore === "undefined") {
                post({ type: "court-missing" });
                return;
              }
              try {
                const state = await HelixBridgeCore.itemCourtState(window, request.itemId, request.itemType, request.projectId);
                post(Object.assign({ type: "court" }, state));
              } catch (error) {
                post({ type: "court-error", message: String(error && error.message || error) });
              }
            })(
            """ + request + ");";
    }

    /// <summary>
    /// Re-reads just the open project's items of the page's type (no cross-project queue, comments or
    /// uploads) and posts "quick-result" with a snapshot, after an item was worked on in CA Tracker's
    /// browser. Needs <see cref="LibrariesFor"/> loaded first.
    /// </summary>
    public static string HelixQuickScan => """
        (async () => {
          const post = (message) => window.chrome.webview.postMessage(message);
          try {
            const raw = await HelixBridgeCore.scanProjectItems(window);
            post({ type: "quick-result", snapshot: CaTrackerSnapshot.buildHelixSnapshot(raw) });
          } catch (error) {
            post({ type: "quick-error", message: String(error && error.message || error) });
          }
        })();
        """;

    /// <summary>
    /// Run on a Helix Details report page: starts Helix's own PDF export and hands the PDF back to
    /// the app as a data URL instead of saving it. Adapted from the earlier extension.
    /// </summary>
    public static string HelixReportExport(string kind, string fallbackFilename)
    {
        var request = JsonSerializer.Serialize(new { kind, fallbackFilename });
        return """
            (async (request) => {
              const post = (message) => window.chrome.webview.postMessage(message);
              const kind = request.kind;
              const fallbackFilename = request.fallbackFilename;
              const sendError = (error) => post({
                type: "report-error",
                message: error && error.message ? error.message : String(error || "The report export failed.")
              });
              const toDataUrl = (blob) => new Promise((resolve, reject) => {
                const reader = new FileReader();
                reader.onload = () => resolve(String(reader.result || ""));
                reader.onerror = () => reject(reader.error || new Error("The report could not be read."));
                reader.readAsDataURL(blob);
              });
              const sendBlob = async (source, filename) => {
                try {
                  let blob = source instanceof Blob ? source : null;
                  if (!blob) {
                    if (!source) throw new Error("Helix returned an empty PDF export.");
                    const response = await fetch(source);
                    if (!response.ok) throw new Error(`Helix returned ${response.status} while creating the report.`);
                    blob = await response.blob();
                  }
                  post({ type: "report", dataUrl: await toDataUrl(blob), filename: String(filename || fallbackFilename || "Details.pdf") });
                } catch (error) {
                  sendError(error);
                }
              };
              const waitFor = async (predicate, timeoutMs) => {
                const deadline = Date.now() + timeoutMs;
                while (Date.now() < deadline) {
                  try {
                    const value = predicate();
                    if (value) return value;
                  } catch (_error) {
                    // The report initializes several globals asynchronously.
                  }
                  await new Promise((resolve) => setTimeout(resolve, 250));
                }
                throw new Error("The Helix Details report did not finish loading.");
              };
              try {
                if (kind === "legacy") {
                  const viewer = await waitFor(() => {
                    const jquery = window.jQuery || window.$;
                    return typeof jquery === "function" ? jquery("#reportViewer1").data("telerik_ReportViewer") : null;
                  }, 120000);
                  await waitFor(() => document.querySelector(".trv-page-container, .trv-pages-area .trv-page-wrapper"), 120000);
                  const originalOpen = window.open;
                  window.open = (url) => {
                    window.open = originalOpen;
                    void sendBlob(new URL(String(url), window.location.href).href, fallbackFilename);
                    return window;
                  };
                  viewer.exportReport("PDF");
                  return;
                }
                const exportButton = await waitFor(() => {
                  const loadingMask = document.getElementById("loadingMask");
                  const loading = loadingMask && window.getComputedStyle(loadingMask).display !== "none";
                  const button = document.getElementById("export")
                    || Array.from(document.querySelectorAll("button, a")).find((element) => /^(download|export)$/i.test(element.textContent.trim()));
                  if (loading || !button || button.disabled) return null;
                  const jquery = window.jQuery || window.$;
                  if (kind === "client" && jquery && typeof jquery._data === "function") {
                    const events = jquery._data(button, "events");
                    if (!events || !Array.isArray(events.click) || events.click.length === 0) return null;
                  }
                  return button;
                }, 120000);
                const kendo = window.kendo;
                const pdf = kendo && kendo.drawing && kendo.drawing.pdf;
                const originalKendoSaveAs = kendo && kendo.saveAs;
                const originalPdfSaveAs = pdf && pdf.saveAs;
                const exportPdf = kendo && kendo.drawing && kendo.drawing.exportPDF;
                if (!kendo || (typeof originalKendoSaveAs !== "function"
                  && !(typeof originalPdfSaveAs === "function" && typeof exportPdf === "function"))) {
                  throw new Error("Helix's PDF export service is unavailable.");
                }
                let delivered = false;
                const restore = () => {
                  if (typeof originalKendoSaveAs === "function") kendo.saveAs = originalKendoSaveAs;
                  if (pdf && typeof originalPdfSaveAs === "function") pdf.saveAs = originalPdfSaveAs;
                };
                const deliver = (source, filename) => {
                  if (delivered) return;
                  delivered = true;
                  restore();
                  void sendBlob(source, filename || fallbackFilename);
                };
                if (typeof originalKendoSaveAs === "function") {
                  kendo.saveAs = (options) => {
                    const settings = options && typeof options === "object" ? options : {};
                    deliver(settings.dataURI || settings.dataUrl || settings.blob, settings.fileName);
                  };
                }
                if (pdf && typeof originalPdfSaveAs === "function" && typeof exportPdf === "function") {
                  pdf.saveAs = (group, filename) => {
                    Promise.resolve(exportPdf(group))
                      .then((dataUri) => deliver(dataUri, filename))
                      .catch((error) => {
                        restore();
                        sendError(error);
                      });
                  };
                }
                const needsDraftConfirmation = Boolean(document.querySelector(".missingRequired"));
                exportButton.click();
                if (needsDraftConfirmation) {
                  const draftDeadline = Date.now() + 5000;
                  while (Date.now() < draftDeadline) {
                    const draftButton = Array.from(document.querySelectorAll("button, a")).find((element) => /download draft/i.test(element.textContent || ""));
                    if (draftButton) {
                      draftButton.click();
                      break;
                    }
                    await new Promise((resolve) => setTimeout(resolve, 100));
                  }
                }
              } catch (error) {
                sendError(error);
              }
            })(
            """ + request + ");";
    }

    /// <summary>Reads the rows Procore renders in its RFI or Submittal list once the grid stops changing.</summary>
    public static string ProcoreScan(string projectId, string? accountId, string projectName, string itemType)
    {
        var request = JsonSerializer.Serialize(new
        {
            projectId,
            accountId,
            projectName,
            itemTypes = new[] { itemType }
        });
        return """
            (async (request) => {
              const post = (message) => window.chrome.webview.postMessage(message);
              const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
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
              try {
                const started = Date.now();
                let lastCount = -1;
                let stableSince = started;
                for (;;) {
                  await sleep(500);
                  const count = gridRows().filter((row) => Object.values(row).some((cell) => cell.href)).length;
                  const now = Date.now();
                  if (count !== lastCount) {
                    lastCount = count;
                    stableSince = now;
                    post({ type: "progress", message: `Reading the Procore list… ${count} rows` });
                  }
                  const elapsed = now - started;
                  if ((count > 0 && elapsed >= 4000 && now - stableSince >= 3000)
                    || (count === 0 && elapsed >= 20000) || elapsed >= 45000) break;
                }
                const queue = CaTrackerProcoreCore.visibleQueueFromRows(window.location.href, gridRows(), new Date().toISOString());
                post({
                  type: "result",
                  snapshot: CaTrackerSnapshot.buildProcoreProjectSnapshot(queue, request),
                  catalog: CaTrackerSnapshot.buildProcoreCatalog(queue, null, window.location.hostname)
                });
              } catch (error) {
                post({ type: "error", message: String(error && error.message || error) });
              }
            })(
            """ + request + ");";
    }

    private static readonly Dictionary<string, string> Cache = new();

    private static string Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"ProviderScripts.{name}")
                ?? throw new InvalidOperationException($"The built-in script {name} is missing from this build.");
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            Cache[name] = text;
            return text;
        }
    }
}
