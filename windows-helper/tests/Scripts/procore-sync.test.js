"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const ProcoreSync = require("../../src/SmcCaTracker.App/Scripts/procore-sync.js");

function fakeProcore(routes) {
  const calls = [];
  const fetch = async (path, init) => {
    calls.push({ path, companyId: init && init.headers && init.headers["Procore-Company-Id"] });
    const url = new URL(path, "https://app.procore.com");
    const key = url.pathname + (url.searchParams.get("company_id") ? `?company_id=${url.searchParams.get("company_id")}` : "");
    const route = routes[key];
    if (route === undefined) return { ok: false, status: 404, headers: { get: () => "application/json" }, json: async () => ({}) };
    if (typeof route === "number") return { ok: false, status: route, headers: { get: () => "application/json" }, json: async () => ({}) };
    if (route && route.html) return { ok: true, status: 200, headers: { get: () => "text/html" }, json: async () => { throw new Error("html"); } };
    const page = Number(url.searchParams.get("page") || 1);
    const body = Array.isArray(route) ? route.slice((page - 1) * 100, page * 100) : route;
    return { ok: true, status: 200, headers: { get: () => "application/json; charset=utf-8" }, json: async () => body };
  };
  return { fetch, calls };
}

const win = { location: { hostname: "app.procore.com" } };

test("discovers every company and its active projects with one sign-in", async () => {
  const procore = fakeProcore({
    "/rest/v1.0/me": { id: 4242, name: "Tim S", login: "tim@smc.example" },
    "/rest/v1.0/companies": [
      { id: 10, name: "Gilbane Building Company" },
      { id: 20, name: "Ryan Companies" },
      { id: 30, name: "Old GC", is_active: false }
    ],
    "/rest/v1.0/projects?company_id=10": [
      { id: 111, display_name: "J11004.000 - Aligned Data Centers - CMH01", active: true },
      { id: 112, name: "Closed job", active: false }
    ],
    "/rest/v1.0/projects?company_id=20": [
      { id: 221, project_number: "007941000", name: "ADC LBB01 - Lubbock TX", active: true }
    ]
  });

  const result = await ProcoreSync.discover(win, { fetch: procore.fetch });

  assert.deepEqual(result.user, { fullName: "Tim S", id: "4242", email: "tim@smc.example" });
  assert.deepEqual(result.projects, [
    { projectId: "111", accountId: "10", accountName: "Gilbane Building Company", name: "J11004.000 - Aligned Data Centers - CMH01", host: "app.procore.com" },
    { projectId: "221", accountId: "20", accountName: "Ryan Companies", name: "007941000 - ADC LBB01 - Lubbock TX", host: "app.procore.com" }
  ]);
  assert.equal(result.skippedInactive, 1);
  assert.equal(result.companies.length, 3);
  // Each company's projects are asked for in that company's context.
  assert.ok(procore.calls.some((call) => call.path.startsWith("/rest/v1.0/projects?company_id=20&page=1") && call.companyId === "20"));
  assert.ok(!procore.calls.some((call) => call.path.includes("company_id=30")));
});

test("a company whose projects can't be listed is reported and the rest still count", async () => {
  const procore = fakeProcore({
    "/rest/v1.0/me": { name: "Tim S" },
    "/rest/v1.0/companies": [{ id: 10, name: "A" }, { id: 20, name: "B" }],
    "/rest/v1.0/projects?company_id=10": 403,
    "/rest/v1.0/projects?company_id=20": [{ id: 5, name: "Job", active: true }]
  });

  const result = await ProcoreSync.discover(win, { fetch: procore.fetch });

  assert.equal(result.projects.length, 1);
  assert.match(result.companies[0].error, /403/);
  assert.equal(result.companies[0].status, 403);
});

test("a list that ignores paging is read once instead of forever", async () => {
  const same = Array.from({ length: 120 }, (_, index) => ({ id: index + 1 }));
  const calls = [];
  const fetch = async (path) => {
    calls.push(path);
    return { ok: true, status: 200, headers: { get: () => "application/json" }, json: async () => same };
  };

  const result = await ProcoreSync.readProject(win, { projectId: "9" }, { fetch });

  assert.equal(result.rfis.length, 120);
  assert.equal(calls.filter((path) => path.includes("/rfis")).length, 2);
});

test("companies on another Procore server can be listed there by id", async () => {
  const procore = fakeProcore({
    "/rest/v1.0/projects?company_id=10": [{ id: 111, display_name: "J11004.000 - Aligned Data Centers - CMH01", active: true }]
  });

  const result = await ProcoreSync.listCompanyProjects({ location: { hostname: "app.procore.com" } },
    [{ id: "10", name: "Gilbane Building Company" }, { id: "20", name: "Ryan Companies" }], { fetch: procore.fetch });

  assert.deepEqual(result.projects.map((project) => [project.projectId, project.host]), [["111", "app.procore.com"]]);
  assert.equal(result.companies[1].status, 404);
});

test("discovery stops with the status when the sign-in isn't accepted", async () => {
  await assert.rejects(
    ProcoreSync.discover(win, { fetch: fakeProcore({ "/rest/v1.0/me": 401, "/rest/v1.0/companies": 401 }).fetch }),
    (error) => error.status === 401
  );
  await assert.rejects(
    ProcoreSync.discover(win, { fetch: fakeProcore({ "/rest/v1.0/me": { html: true }, "/rest/v1.0/companies": { html: true } }).fetch }),
    (error) => error.status === 406
  );
});

test("lists are read page by page until a short page", async () => {
  const many = Array.from({ length: 250 }, (_, index) => ({ id: index + 1, subject: `RFI ${index + 1}` }));
  const procore = fakeProcore({ "/rest/v1.0/projects/9/rfis": many, "/rest/v1.0/projects/9/submittals": [] });

  const result = await ProcoreSync.readProject(win, { projectId: "9", companyId: "10" }, { fetch: procore.fetch });

  assert.equal(result.rfis.length, 250);
  assert.equal(procore.calls.filter((call) => call.path.includes("/rfis")).length, 3);
  assert.ok(procore.calls.every((call) => call.companyId === "10"));
});

test("a tool you can't see on a project counts as empty instead of failing the project", async () => {
  const procore = fakeProcore({ "/rest/v1.0/projects/9/rfis": [{ id: 1 }], "/rest/v1.0/projects/9/submittals": 403 });

  const result = await ProcoreSync.readProject(win, { projectId: "9", companyId: "10" }, { fetch: procore.fetch });

  assert.equal(result.rfis.length, 1);
  assert.deepEqual(result.submittals, []);
  assert.deepEqual(result.unavailable, ["submittals"]);
});

test("a project with neither list readable fails with its status; signed out fails with 401", async () => {
  await assert.rejects(
    ProcoreSync.readProject(win, { projectId: "9" }, { fetch: fakeProcore({ "/rest/v1.0/projects/9/rfis": 403, "/rest/v1.0/projects/9/submittals": 403 }).fetch }),
    (error) => error.status === 403
  );
  await assert.rejects(
    ProcoreSync.readProject(win, { projectId: "9" }, { fetch: fakeProcore({ "/rest/v1.0/projects/9/rfis": 401 }).fetch }),
    (error) => error.status === 401
  );
});

test("finds projects from the links on Procore's home page", () => {
  const anchor = (href, text) => ({ getAttribute: () => href, innerText: text });
  const doc = {
    querySelectorAll: () => [
      anchor("/webclients/host/companies/10/projects/111/tools/projecthome", "Gilbane Building Company\nJ11004.000 - Aligned Data Centers - CMH01\nPataskala, OH"),
      anchor("https://app.procore.com/221/project/home", "Ryan Companies\n007941000 - ADC LBB01 - Lubbock TX"),
      anchor("https://example.com/companies/1/projects/2", "Elsewhere"),
      anchor("/webclients/host/companies/10/projects/111/tools/rfis", "duplicate")
    ]
  };

  assert.deepEqual(ProcoreSync.projectsFromPage(doc, "app.procore.com"), [
    { projectId: "111", accountId: "10", accountName: null, name: "J11004.000 - Aligned Data Centers - CMH01", host: "app.procore.com" },
    { projectId: "221", accountId: null, accountName: null, name: "007941000 - ADC LBB01 - Lubbock TX", host: "app.procore.com" }
  ]);
});

test("pages are asked for several at a time when Procore says how many rows there are", async () => {
  const all = Array.from({ length: 1250 }, (_, index) => ({ id: index + 1 }));
  let inFlight = 0;
  let most = 0;
  const fetch = async (path) => {
    inFlight += 1;
    most = Math.max(most, inFlight);
    await new Promise((resolve) => setTimeout(resolve, 5));
    inFlight -= 1;
    const url = new URL(path, "https://app.procore.com");
    const page = Number(url.searchParams.get("page"));
    const body = url.pathname.endsWith("/rfis") ? all.slice((page - 1) * 100, page * 100) : [];
    return { ok: true, status: 200, headers: { get: (name) => (name === "Total" ? String(url.pathname.endsWith("/rfis") ? all.length : 0) : "application/json") }, json: async () => body };
  };

  const result = await ProcoreSync.readProject(win, { projectId: "9" }, { fetch });

  assert.equal(result.rfis.length, 1250);
  assert.ok(most > 1, "pages were read in parallel");
});

test("a server that sends fewer rows per page than asked for is still read to the end", async () => {
  // Asked for 100 per page, Procore sends 50 and says there are 250.
  const all = Array.from({ length: 250 }, (_, index) => ({ id: index + 1 }));
  const fetch = async (path) => {
    const url = new URL(path, "https://app.procore.com");
    const page = Number(url.searchParams.get("page"));
    const body = url.pathname.endsWith("/rfis") ? all.slice((page - 1) * 50, page * 50) : [];
    return { ok: true, status: 200, headers: { get: (name) => (name === "Total" ? String(url.pathname.endsWith("/rfis") ? all.length : 0) : "application/json") }, json: async () => body };
  };

  const result = await ProcoreSync.readProject(win, { projectId: "9" }, { fetch });

  assert.equal(result.rfis.length, 250);
  assert.deepEqual(result.rfis.map((row) => row.id), all.map((row) => row.id));
});

test("after the first read only changed items are asked for", async () => {
  const calls = [];
  const fetch = async (path) => {
    calls.push(decodeURIComponent(path));
    return { ok: true, status: 200, headers: { get: () => "application/json" }, json: async () => [{ id: 1 }] };
  };

  const result = await ProcoreSync.readProject(win, { projectId: "9", updatedSince: "2026-10-01T10:00:00.000Z" }, { fetch });

  assert.equal(result.changesOnly, true);
  assert.ok(calls.every((path) => path.includes("filters[updated_at]=2026-10-01T10:00:00.000Z...")));
});

test("an RFI's files are its question's and every answer's, dated by what they came with", async () => {
  const procore = fakeProcore({
    "/rest/v1.0/projects/9/rfis/77": {
      id: 77,
      questions: [{
        question_date: "2026-10-01T21:48:59Z",
        attachments: [{ id: 1, filename: "Question sketch.pdf", name: "ignored", url: "https://storage.procore.com/api/v5/files/1?sig=a" }],
        answers: [
          { answer_date: "2026-10-08T02:21:19Z", attachments: [{ id: 2, name: "Answer.pdf", url: "https://storage.procore.com/api/v5/files/2?sig=b" }] },
          { answer_date: "2026-10-09T10:00:00Z", attachments: [] }
        ]
      }]
    }
  });

  const result = await ProcoreSync.readItemFiles(win, { projectId: "9", companyId: "10", itemType: "rfi", itemId: "77" }, { fetch: procore.fetch });

  assert.deepEqual(result.attachments, [
    { id: "1", filename: "Question sketch.pdf", uploadedAt: "2026-10-01T21:48:59Z", url: "https://storage.procore.com/api/v5/files/1?sig=a" },
    { id: "2", filename: "Answer.pdf", uploadedAt: "2026-10-08T02:21:19Z", url: "https://storage.procore.com/api/v5/files/2?sig=b" }
  ]);
  assert.deepEqual(procore.calls.map((call) => [call.path, call.companyId]), [["/rest/v1.0/projects/9/rfis/77", "10"]]);
});

test("a Submittal's files are its own and every response's, each listed once", async () => {
  const file = (id, name) => ({ id, filename: name, url: `https://storage.procore.com/api/v5/files/${id}?sig=x` });
  const procore = fakeProcore({
    "/rest/v1.1/projects/9/submittals/55": {
      id: 55,
      created_at: "2026-10-06T13:58:59Z",
      received_date: "2026-10-05",
      distributed_at: "2026-10-08T14:38:50Z",
      attachments: [file(1, "Product data.pdf")],
      approvers: [
        { response: { name: "Submitted" }, returned_date: "2026-10-06", attachments: [file(2, "Product data.pdf")] },
        { response: { name: "Awaiting Response" }, returned_date: null, sent_date: "2026-10-06", attachments: [] },
        { response: { name: "Approved as Noted" }, returned_date: null, sent_date: "2026-10-07", attachments: [file(3, "Reviewed.pdf")] }
      ],
      distributed_submittals: [
        { sent_at: "2026-10-08T14:38:50Z", final_attachments: [file(3, "Reviewed.pdf"), file(4, "Stamped.pdf")] }
      ]
    }
  });

  const result = await ProcoreSync.readItemFiles(win, { projectId: "9", companyId: "10", itemType: "submittal", itemId: "55" }, { fetch: procore.fetch });

  // The same file name under another id is another file (it is kept); the same id is listed once.
  assert.deepEqual(result.attachments.map((entry) => [entry.id, entry.filename, entry.uploadedAt]), [
    ["1", "Product data.pdf", "2026-10-06T13:58:59Z"],
    ["2", "Product data.pdf", "2026-10-06"],
    ["3", "Reviewed.pdf", "2026-10-07"],
    ["4", "Stamped.pdf", "2026-10-08T14:38:50Z"]
  ]);
  assert.equal(procore.calls[0].companyId, "10");
});

test("files without a link or id are left out, and a nameless file is still listed", async () => {
  const procore = fakeProcore({
    "/rest/v1.0/projects/9/rfis/77": {
      questions: [{ question_date: null, attachments: [{ id: 1, url: "" }, { url: "https://storage.procore.com/x" }, { id: 3, url: "https://storage.procore.com/3" }] }]
    }
  });

  const result = await ProcoreSync.readItemFiles(win, { projectId: "9", itemType: "rfi", itemId: "77" }, { fetch: procore.fetch });

  assert.deepEqual(result.attachments, [{ id: "3", filename: "Attachment", uploadedAt: null, url: "https://storage.procore.com/3" }]);
});

test("an item that can't be read fails with Procore's status", async () => {
  await assert.rejects(
    ProcoreSync.readItemFiles(win, { projectId: "9", itemType: "submittal", itemId: "1" }, { fetch: fakeProcore({}).fetch }),
    (error) => error.status === 404
  );
  await assert.rejects(
    ProcoreSync.readItemFiles(win, { projectId: "9", itemType: "rfi", itemId: "1" }, { fetch: fakeProcore({ "/rest/v1.0/projects/9/rfis/1": 401 }).fetch }),
    (error) => error.status === 401
  );
});

test("if Procore refuses the changes filter, everything is read instead", async () => {
  const calls = [];
  const fetch = async (path) => {
    calls.push(path);
    if (path.includes("filters")) return { ok: false, status: 400, headers: { get: () => "application/json" }, json: async () => ({}) };
    return { ok: true, status: 200, headers: { get: () => "application/json" }, json: async () => [{ id: 1 }, { id: 2 }] };
  };

  const result = await ProcoreSync.readProject(win, { projectId: "9", updatedSince: "2026-10-01T10:00:00.000Z" }, { fetch });

  assert.equal(result.changesOnly, false);
  assert.equal(result.rfis.length, 2);
  assert.equal(result.submittals.length, 2);
});
