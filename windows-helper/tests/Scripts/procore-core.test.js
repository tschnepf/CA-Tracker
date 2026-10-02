"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const ProcoreCore = require("../../src/SmcCaTracker.App/Scripts/procore-core.js");

test("extracts company and project identifiers from supported Procore routes", () => {
  assert.deepEqual(
    ProcoreCore.pageContext("https://us02.procore.com/webclients/host/companies/12/projects/34/tools/rfis"),
    { companyId: "12", projectId: "34" }
  );
  assert.deepEqual(
    ProcoreCore.pageContext("https://app.procore.com/webclients/host/companies/12/tools/portfolio"),
    { companyId: "12", projectId: "" }
  );
  assert.deepEqual(
    ProcoreCore.pageContext("https://app.procore.com/34/project/rfi/show/99"),
    { companyId: "", projectId: "34" }
  );
});

test("maps backend Procore queue data to the shared activity UI contract", () => {
  const index = ProcoreCore.queueIndex({
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
      responsible: "Pat Builder",
      due_at: "2026-09-08",
      source_url: "https://app.procore.com/34/project/rfi/show/99",
      assignments: []
    }]
  });

  assert.equal(index.context.provider, "procore");
  assert.equal(index.currentPersonKey, "procore:user:7");
  assert.equal(index.openQueue[0].queueState.code, "current");
  assert.equal(index.openQueue[0].reasons[0].code, "assigned");
  assert.equal(index.openQueue[0].sourceUrl, "https://app.procore.com/34/project/rfi/show/99");
  assert.deepEqual(index.projects, [{ id: "34", name: "Airport" }]);
});

test("accepts only supported Procore application deep links", () => {
  assert.equal(
    ProcoreCore.safeSourceUrl("https://us02.procore.com/34/project/rfi/show/99"),
    "https://us02.procore.com/34/project/rfi/show/99"
  );
  assert.equal(ProcoreCore.safeSourceUrl("javascript:alert(1)"), "");
  assert.equal(ProcoreCore.safeSourceUrl("https://example.com/34/project/rfi/show/99"), "");
  assert.equal(ProcoreCore.safeSourceUrl("https://app.procore.com/example"), "");
});

test("normalizes visible Procore My Open Items rows without page credentials", () => {
  const queue = ProcoreCore.visibleQueueFromRows(
    "https://app.procore.com/webclients/host/companies/155363/tools/opentasks",
    [
      {
        company_name: { text: "Example Company", href: "/155363/company/home" },
        "project.number": { text: "P-100", href: "/3364579/project/home" },
        project_name: { text: "Data Center", href: "/3364579/project/home" },
        item_type: { text: "RFI" },
        title: {
          text: "#145: Confirm pathway locations",
          href: "https://app.procore.com/3364579/project/rfi/show/27453843"
        },
        due_date: { text: "Aug 27, 2026" },
        status: { text: "Open" }
      },
      {
        company_name: { text: "Example Company", href: "/155363/company/home" },
        project_name: { text: "Data Center", href: "/3364579/project/home" },
        item_type: { text: "Submittal" },
        title: {
          text: "#03 30 00-1: Concrete mix design",
          href: "/3364579/project/submittals/1234"
        },
        due_date: { text: "" },
        status: { text: "Open" }
      },
      {
        item_type: { text: "RFI" },
        title: { text: "Unsafe", href: "javascript:alert(1)" }
      }
    ],
    "2026-09-04T12:00:00Z"
  );

  assert.equal(queue.source_mode, "visible_page");
  assert.equal(queue.complete, false);
  assert.equal(queue.items.length, 2);
  assert.deepEqual(
    queue.items.map((item) => [item.item_type, item.item_id, item.project_id, item.number]),
    [
      ["rfi", "27453843", "3364579", "145"],
      ["submittal", "1234", "3364579", "03 30 00-1"]
    ]
  );
  assert.equal(queue.items[0].title, "Confirm pathway locations");
  assert.equal(queue.items[0].source_metadata.source_mode, "visible_page");
  assert.match(queue.items[0].due_at, /^2026-08-27T/);
});

test("normalizes and merges a standalone Procore company and project catalog", () => {
  const catalog = ProcoreCore.normalizeDiscoveryCatalog({
    version: 1,
    updatedAt: "2026-09-04T18:00:00Z",
    companies: [
      {
        id: 155363,
        name: "Gilbane Building Company",
        projects: [{
          id: 3364579,
          name: "J11004.000 - Aligned Data Centers - CMH01",
          url: "https://app.procore.com/3364579/project/home",
          scanStatus: "scanned",
          discoveredAt: "2026-09-04T18:00:00Z"
        }]
      },
      {
        id: 3118,
        name: "HITT Contracting Inc.",
        projects: [
          {
            id: 3116072,
            name: "191167 - PointOne Remington Tech Park",
            url: "https://app.procore.com/3116072/project/home"
          },
          {
            id: "bad",
            name: "Unsafe",
            url: "javascript:alert(1)"
          }
        ]
      }
    ],
    scan: {
      status: "complete",
      completedAt: "2026-09-04T18:00:00Z"
    }
  });

  assert.equal(catalog.companies.length, 2);
  assert.equal(catalog.companies[1].projects.length, 1);
  assert.equal(catalog.companies[1].projects[0].scanStatus, "not_scanned");
  const merged = ProcoreCore.mergeDiscoveryCatalog({
    projects: [{ id: "999", name: "Queue-only project" }],
    openQueue: []
  }, catalog);
  assert.equal(merged.discoveryCatalog.companyCount, 2);
  assert.equal(merged.discoveryCatalog.projectCount, 2);
  assert.deepEqual(merged.projects.map((project) => project.id), ["3364579", "3116072", "999"]);
  assert.equal(
    merged.projects[1].name,
    "HITT Contracting Inc. — 191167 - PointOne Remington Tech Park"
  );
});

test("rejects unsafe project-home URLs from discovery catalogs", () => {
  assert.equal(
    ProcoreCore.safeProjectHomeUrl("https://us02.procore.com/123/project/home"),
    "https://us02.procore.com/123/project/home"
  );
  assert.equal(ProcoreCore.safeProjectHomeUrl("https://example.com/123/project/home"), "");
  assert.equal(ProcoreCore.safeProjectHomeUrl("javascript:alert(1)"), "");
});

test("project RFI and Submittal lists are read without an item-type column", () => {
  const page = "https://app.procore.com/webclients/host/companies/12/projects/34/tools/rfis";
  const queue = ProcoreCore.visibleQueueFromRows(page, [
    {},
    {
      number: { text: "12", href: "https://app.procore.com/webclients/host/companies/12/projects/34/tools/rfis/901" },
      subject: { text: "Slab edge at grid B", href: "" },
      status: { text: "Open", href: "" }
    },
    {
      title: { text: "#S-4: Doors", href: "https://app.procore.com/webclients/host/companies/12/projects/34/tools/submittals/77" },
      status: { text: "Closed", href: "" }
    }
  ], "2026-09-28T20:00:00Z");
  assert.deepEqual(queue.items.map((item) => [item.item_type, item.item_id, item.number, item.title, item.status]), [
    ["rfi", "901", "12", "Slab edge at grid B", "Open"],
    ["submittal", "77", "S-4", "Doors", "Closed"]
  ]);
});

test("a title with a colon isn't taken for a number, and submittal log links give their id", () => {
  const page = "https://app.procore.com/webclients/host/companies/12/projects/34/tools/submittals";
  const queue = ProcoreCore.visibleQueueFromRows(page, [
    {
      number: { text: "08 71 00-3", href: "" },
      title: { text: "Door Hardware: confirm finish", href: "https://app.procore.com/34/project/submittals_log/5501" },
      status: { text: "Open", href: "" }
    },
    {
      title: { text: "Concrete: mix design", href: "https://app.procore.com/webclients/host/companies/12/projects/34/tools/submittals/78" },
      status: { text: "Open", href: "" }
    }
  ], "2026-09-28T20:00:00Z");
  assert.deepEqual(queue.items.map((item) => [item.item_id, item.number, item.title]), [
    ["5501", "08 71 00-3", "Door Hardware: confirm finish"],
    ["78", "", "Concrete: mix design"]
  ]);
});

// A page with the given inputs (by selector match) and buttons/links (by text).
function page(options) {
  const inputs = options.inputs || [];
  const buttons = (options.buttons || []).map((text) => ({ innerText: text }));
  return {
    document: {
      querySelector: (selector) => inputs.some((input) => selector.includes(input)) ? {} : null,
      querySelectorAll: () => buttons
    }
  };
}

const answer = (status, body) => async () => ({ status, ok: status >= 200 && status < 300, json: async () => body });

test("Procore saying who is signed in decides the sign-in check", async () => {
  const signedIn = await ProcoreCore.signInState(page({}), answer(200, { id: 7, login: "pat@example.com", name: "Pat Engineer" }));
  assert.deepEqual(signedIn, { state: "signedIn", user: { fullName: "Pat Engineer", company: null } });
  assert.equal((await ProcoreCore.signInState(page({}), answer(401, {}))).state, "signedOut");
});

test("while Procore doesn't say, a sign-in form means signed out and anything else means not yet", async () => {
  const failing = async () => { throw new Error("network"); };
  assert.equal((await ProcoreCore.signInState(page({ inputs: ['type="password"'] }), failing)).state, "signedOut");
  assert.equal((await ProcoreCore.signInState(page({ inputs: ['type="email"'] }), answer(500, {}))).state, "signedOut");
  assert.equal((await ProcoreCore.signInState(page({ buttons: ["Log in"] }), answer(503, {}))).state, "signedOut");
  assert.equal((await ProcoreCore.signInState(page({ buttons: ["Projects", "Login history"] }), failing)).state, "pending");
  assert.equal((await ProcoreCore.signInState(page({ buttons: ["Sign in"] }), answer(200, { name: "Pat" }))).state, "signedIn");
});
