"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const TaskHubClient = require("../integrations/taskhub/client.js");

function response(status, payload) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => "application/json" },
    async json() { return payload; }
  };
}

function chromeStorage(initial) {
  const data = Object.assign({}, initial);
  let accessLevel = null;
  return {
    data,
    chrome: {
      runtime: { lastError: null },
      storage: {
        local: {
          get(key, callback) {
            callback({ [key]: data[key] });
          },
          set(value, callback) {
            Object.assign(data, value);
            if (callback) callback();
          },
          remove(keys, callback) {
            (Array.isArray(keys) ? keys : [keys]).forEach((key) => delete data[key]);
            if (callback) callback();
          },
          async setAccessLevel(value) {
            accessLevel = value.accessLevel;
          }
        }
      }
    },
    getAccessLevel() {
      return accessLevel;
    }
  };
}

test("Task Hub URLs require HTTPS except for loopback development", () => {
  assert.equal(
    TaskHubClient.normalizeBaseUrl("https://tasks.example.com/app/"),
    "https://tasks.example.com/app"
  );
  assert.equal(TaskHubClient.normalizeBaseUrl("http://localhost:8080/"), "http://localhost:8080");
  assert.equal(TaskHubClient.permissionOrigin("https://tasks.example.com/app"), "https://tasks.example.com/*");
  assert.throws(() => TaskHubClient.normalizeBaseUrl("http://tasks.example.com"), /HTTPS/);
  assert.throws(() => TaskHubClient.normalizeBaseUrl("https://user:secret@tasks.example.com"), /credentials/);
  assert.throws(() => TaskHubClient.normalizeBaseUrl("https://tasks.example.com?token=secret"), /query/);
});

test("login persists revocable tokens but never the submitted password", async () => {
  const storage = chromeStorage();
  const requests = [];
  const client = TaskHubClient.create({
    chrome: storage.chrome,
    fetch: async (url, options) => {
      requests.push({ url, options });
      return response(200, {
        access: "access-token",
        refresh: "refresh-token",
        user: { email: "person@example.com", display_name: "Person" }
      });
    }
  });

  await client.restrictCredentialStorage();
  const status = await client.login({
    baseUrl: "https://tasks.example.com/",
    email: "person@example.com",
    password: "not-saved-password"
  });

  assert.equal(storage.getAccessLevel(), "TRUSTED_CONTEXTS");
  assert.equal(status.connected, true);
  assert.equal(requests[0].url, "https://tasks.example.com/integrations/helix/auth/login");
  assert.deepEqual(JSON.parse(requests[0].options.body), {
    email: "person@example.com",
    password: "not-saved-password"
  });
  assert.equal(JSON.stringify(storage.data).includes("not-saved-password"), false);
  assert.equal(storage.data[TaskHubClient.AUTH_KEY].refresh, "refresh-token");
});

test("sync rotates an expired refresh token and retries once", async () => {
  const storage = chromeStorage({
    [TaskHubClient.SETTINGS_KEY]: {
      baseUrl: "https://tasks.example.com",
      autoSync: true
    },
    [TaskHubClient.AUTH_KEY]: {
      access: "expired-access",
      refresh: "old-refresh",
      user: { email: "person@example.com" }
    }
  });
  const requests = [];
  const responses = [
    response(401, { detail: "expired" }),
    response(200, { access: "new-access", refresh: "new-refresh" }),
    response(200, { created: 1, updated: 0, unchanged: 0, archived: 0 })
  ];
  const client = TaskHubClient.create({
    chrome: storage.chrome,
    fetch: async (url, options) => {
      requests.push({ url, options });
      return responses.shift();
    }
  });

  const result = await client.sync({ schema_version: 1, items: [] });

  assert.equal(result.created, 1);
  assert.equal(requests.length, 3);
  assert.equal(requests[0].options.headers.Authorization, "Bearer expired-access");
  assert.equal(requests[1].url, "https://tasks.example.com/integrations/helix/auth/refresh");
  assert.deepEqual(JSON.parse(requests[1].options.body), { refresh: "old-refresh" });
  assert.equal(requests[2].options.headers.Authorization, "Bearer new-access");
  assert.equal(storage.data[TaskHubClient.AUTH_KEY].refresh, "new-refresh");
});

test("disconnected status is local-only and logout clears tokens after a server failure", async () => {
  const emptyStorage = chromeStorage();
  let emptyFetchCount = 0;
  const disconnected = TaskHubClient.create({
    chrome: emptyStorage.chrome,
    fetch: async () => {
      emptyFetchCount += 1;
      return response(500, {});
    }
  });
  assert.deepEqual(await disconnected.status(), {
    connected: false,
    baseUrl: "",
    autoSync: true,
    connectedAt: null,
    user: null
  });
  assert.equal(emptyFetchCount, 0);

  const connectedStorage = chromeStorage({
    [TaskHubClient.SETTINGS_KEY]: { baseUrl: "https://tasks.example.com" },
    [TaskHubClient.AUTH_KEY]: { access: "access", refresh: "refresh", user: {} }
  });
  const connected = TaskHubClient.create({
    chrome: connectedStorage.chrome,
    fetch: async () => {
      throw new Error("offline");
    }
  });
  assert.deepEqual(await connected.logout(), {
    connected: false,
    baseUrl: "",
    user: null,
    autoSync: true
  });
  assert.equal(connectedStorage.data[TaskHubClient.SETTINGS_KEY], undefined);
  assert.equal(connectedStorage.data[TaskHubClient.AUTH_KEY], undefined);
});
