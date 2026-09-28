"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const ProviderCore = require("../provider-core.js");

test("detects only supported Helix and Procore application routes", () => {
  assert.equal(
    ProviderCore.providerForUrl("https://project.helixteam.com/Projects/p1/RFI"),
    "helix"
  );
  assert.equal(
    ProviderCore.providerForUrl("https://app.procore.com/123/project/rfi/show/456"),
    "procore"
  );
  assert.equal(
    ProviderCore.providerForUrl("https://us02.procore.com/123/project/rfi/show/456"),
    "procore"
  );
  assert.equal(
    ProviderCore.providerForUrl(
      "https://us02.procore.com/webclients/host/companies/1/projects/2/tools/rfis"
    ),
    "procore"
  );
  assert.equal(ProviderCore.providerForUrl("https://developers.procore.com/reference/rest/rfis"), null);
  assert.equal(ProviderCore.providerForUrl("http://app.procore.com/123/project/home"), null);
  assert.equal(ProviderCore.providerForUrl("https://example.com/Projects/p1/RFI"), null);
});

test("builds collision-resistant provider-scoped cache identities", () => {
  assert.equal(ProviderCore.scopedKey("helix", "company:1", "user/2"), "helix:company%3A1:user%2F2");
  assert.equal(ProviderCore.scopedKey("procore", "company:1", "user/2"), "procore:company%3A1:user%2F2");
  assert.throws(() => ProviderCore.scopedKey("unknown", "1", "2"), /Unsupported/);
  assert.throws(() => ProviderCore.scopedKey("helix", "", "2"), /require account/);
});

test("normalizes shared RFI and submittal fields without dropping provider metadata", () => {
  const item = ProviderCore.normalizeWorkItem("procore", {
    id: 42,
    itemType: "RFI",
    projectId: 7,
    number: 12,
    title: "  Confirm pathway routing  ",
    status: "Open",
    sourceMetadata: { companyId: "9" }
  });

  assert.deepEqual(item, {
    id: 42,
    provider: "procore",
    itemType: "rfi",
    itemId: "42",
    projectId: "7",
    number: "12",
    title: "Confirm pathway routing",
    status: "Open",
    sourceMetadata: { companyId: "9" }
  });
  assert.equal(Object.isFrozen(item), true);
});
