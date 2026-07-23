"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const source = fs.readFileSync(path.resolve(__dirname, "..", "ui.js"), "utf8");

test("cards place the plain item type above the number and omit the My Activity latest date", () => {
  const identityStart = source.indexOf("itemIdentity.append(");
  const typePosition = source.indexOf('"item-type"', identityStart);
  const numberPosition = source.indexOf('"number"', identityStart);

  assert.ok(identityStart >= 0, "Expected a card identity block");
  assert.ok(typePosition > identityStart, "Expected the item type in the identity block");
  assert.ok(numberPosition > typePosition, "Expected the item number after the item type");
  assert.doesNotMatch(source, /\.type-badge\s*\{/);
  assert.doesNotMatch(source, /`Latest:/);
  assert.match(source, /`Target: \$\{Core\.formatDate\(item\.queueState\.dueAt\)\}`/);
});

test("wide queue cards place status between assignment reasons and the open action", () => {
  assert.match(source, /grid-template-areas: "top title contract reasons status bottom"/);
  assert.match(source, /if \(!queueMode\) badgeStack\.appendChild\(element\(documentObject, "span", "status-badge", item\.status\)\)/);
  assert.match(source, /statusColumn\.appendChild\(element\(documentObject, "span", "status-badge", item\.status\)\)/);
});

test("the sidebar does not use bold typography", () => {
  assert.match(source, /:host \* \{ font-weight: 400 !important; \}/);
  assert.doesNotMatch(source, /font-weight:\s*(?:[5-9]00|bold|bolder)/);
});

test("people filtering offers persistent me, company, and all-people scopes", () => {
  assert.match(source, /"Only me"/);
  assert.match(source, /"All people in my company"/);
  assert.match(source, /"All people"/);
  assert.match(source, /peopleScope: selectedPeopleScope/);
  assert.match(source, /Core\.companyPersonKeys/);
  assert.match(source, /selectPeopleScope\("company"\)/);
});
