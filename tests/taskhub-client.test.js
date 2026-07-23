"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { webcrypto } = require("node:crypto");
const TaskHubClient = require("../integrations/taskhub/client.js");

const EXTENSION_ID = "kcngamkbiboiljpfbjpmjoekkfpbidbk";
const REDIRECT_URI = `https://${EXTENSION_ID}.chromiumapp.org/taskhub`;

function response(status, payload) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => "application/json" },
    async json() { return payload; }
  };
}

function oidcConfiguration(overrides) {
  return {
    oidc_discovery_url: "https://tasks.example.com/idp/realms/taskhub/.well-known/openid-configuration",
    client_id: "taskhub-helix-extension",
    redirect_uri: REDIRECT_URI,
    required_scopes: ["openid", "profile", "email", "offline_access", "helix.sync"],
    required_audience: "taskhub-api",
    ...(overrides || {})
  };
}

function discovery(overrides) {
  return {
    issuer: "https://tasks.example.com/idp/realms/taskhub",
    authorization_endpoint: "https://tasks.example.com/idp/realms/taskhub/protocol/openid-connect/auth",
    token_endpoint: "https://tasks.example.com/idp/realms/taskhub/protocol/openid-connect/token",
    revocation_endpoint: "https://tasks.example.com/idp/realms/taskhub/protocol/openid-connect/revoke",
    ...(overrides || {})
  };
}

function chromeStorage(initial, authFlow) {
  const data = Object.assign({}, initial);
  const removedPermissions = [];
  let accessLevel = null;
  const chrome = {
    runtime: {
      lastError: null,
      getManifest() {
        return { version: "1.8.1" };
      }
    },
    identity: {
      getRedirectURL(path) {
        return `https://${EXTENSION_ID}.chromiumapp.org/${path}`;
      },
      launchWebAuthFlow(details, callback) {
        const state = new URL(details.url).searchParams.get("state");
        const result = authFlow
          ? authFlow(details, state)
          : `${REDIRECT_URI}?code=authorization-code&state=${encodeURIComponent(state)}`;
        callback(result);
      }
    },
    permissions: {
      remove(value, callback) {
        removedPermissions.push(...(value.origins || []));
        callback(true);
      }
    },
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
  };
  return {
    data,
    chrome,
    removedPermissions,
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

test("OIDC login uses PKCE, validates the session, and stores no password", async () => {
  const storage = chromeStorage();
  const requests = [];
  const client = TaskHubClient.create({
    chrome: storage.chrome,
    crypto: webcrypto,
    now: () => 1_000_000,
    fetch: async (url, options) => {
      requests.push({ url, options });
      if (url.endsWith("/integrations/helix/auth/config")) return response(200, oidcConfiguration());
      if (url.includes(".well-known/openid-configuration")) return response(200, discovery());
      if (url.endsWith("/protocol/openid-connect/token")) {
        return response(200, {
          access_token: "access-token",
          refresh_token: "refresh-token",
          expires_in: 300,
          token_type: "Bearer"
        });
      }
      if (url.endsWith("/integrations/helix/auth/session")) {
        return response(200, {
          id: "user-id",
          organization_id: "org-id",
          role: "owner",
          email: "person@example.com",
          display_name: "Person"
        });
      }
      throw new Error(`Unexpected URL ${url}`);
    }
  });

  await client.restrictCredentialStorage();
  const status = await client.login({ baseUrl: "https://tasks.example.com/" });

  assert.equal(storage.getAccessLevel(), "TRUSTED_CONTEXTS");
  assert.equal(status.connected, true);
  assert.equal(requests.length, 4);
  const tokenRequest = requests[2];
  const tokenBody = new URLSearchParams(tokenRequest.options.body);
  assert.equal(tokenBody.get("grant_type"), "authorization_code");
  assert.equal(tokenBody.get("client_id"), "taskhub-helix-extension");
  assert.equal(tokenBody.get("redirect_uri"), REDIRECT_URI);
  assert.ok(tokenBody.get("code_verifier").length >= 43);
  assert.equal(JSON.stringify(requests).toLowerCase().includes("password"), false);
  assert.equal(storage.data[TaskHubClient.AUTH_KEY].refreshToken, "refresh-token");
  assert.equal(storage.data[TaskHubClient.AUTH_KEY].expiresAt, 1_300_000);
  assert.equal(
    requests[3].options.headers["X-TaskHub-Extension-Version"],
    "1.8.1"
  );
});

test("OIDC login rejects a state mismatch without exchanging the code", async () => {
  const storage = chromeStorage({}, () => `${REDIRECT_URI}?code=authorization-code&state=wrong`);
  const requests = [];
  const client = TaskHubClient.create({
    chrome: storage.chrome,
    crypto: webcrypto,
    fetch: async (url, options) => {
      requests.push({ url, options });
      if (url.endsWith("/integrations/helix/auth/config")) return response(200, oidcConfiguration());
      return response(200, discovery());
    }
  });

  await assert.rejects(
    () => client.login({ baseUrl: "https://tasks.example.com" }),
    /state did not match/
  );
  assert.equal(requests.length, 2);
  assert.equal(storage.data[TaskHubClient.AUTH_KEY], undefined);
});

test("OIDC discovery and redirect endpoints must remain on the selected server", async () => {
  const storage = chromeStorage();
  const wrongRedirectClient = TaskHubClient.create({
    chrome: storage.chrome,
    crypto: webcrypto,
    fetch: async () => response(200, oidcConfiguration({
      redirect_uri: "https://different.chromiumapp.org/taskhub"
    }))
  });
  await assert.rejects(
    () => wrongRedirectClient.login({ baseUrl: "https://tasks.example.com" }),
    /not registered/
  );

  const externalDiscoveryClient = TaskHubClient.create({
    chrome: chromeStorage().chrome,
    crypto: webcrypto,
    fetch: async () => response(200, oidcConfiguration({
      oidc_discovery_url: "https://attacker.example/.well-known/openid-configuration"
    }))
  });
  await assert.rejects(
    () => externalDiscoveryClient.login({ baseUrl: "https://tasks.example.com" }),
    /outside the selected server/
  );
});

test("sync rotates an expired refresh token and retries once after a 401", async () => {
  const storage = chromeStorage({
    [TaskHubClient.SETTINGS_KEY]: {
      baseUrl: "https://tasks.example.com",
      autoSync: true
    },
    [TaskHubClient.AUTH_KEY]: {
      accessToken: "expired-access",
      refreshToken: "old-refresh",
      expiresAt: 9_999_999,
      clientId: "taskhub-helix-extension",
      tokenEndpoint: "https://tasks.example.com/idp/realms/taskhub/protocol/openid-connect/token",
      revocationEndpoint: "https://tasks.example.com/idp/realms/taskhub/protocol/openid-connect/revoke",
      user: { email: "person@example.com" }
    }
  });
  const requests = [];
  const responses = [
    response(401, { detail: "expired" }),
    response(200, { access_token: "new-access", refresh_token: "new-refresh", expires_in: 300 }),
    response(200, { created: 1, updated: 0, unchanged: 0, archived: 0 })
  ];
  const client = TaskHubClient.create({
    chrome: storage.chrome,
    crypto: webcrypto,
    now: () => 1_000_000,
    fetch: async (url, options) => {
      requests.push({ url, options });
      return responses.shift();
    }
  });

  const result = await client.sync({ schema_version: 1, items: [] });

  assert.equal(result.created, 1);
  assert.equal(requests.length, 3);
  assert.equal(requests[0].options.headers.Authorization, "Bearer expired-access");
  assert.match(requests[1].url, /protocol\/openid-connect\/token$/);
  const refreshBody = new URLSearchParams(requests[1].options.body);
  assert.equal(refreshBody.get("grant_type"), "refresh_token");
  assert.equal(refreshBody.get("refresh_token"), "old-refresh");
  assert.equal(requests[2].options.headers.Authorization, "Bearer new-access");
  assert.equal(requests[2].options.headers["X-TaskHub-Extension-Version"], "1.8.1");
  assert.equal(storage.data[TaskHubClient.AUTH_KEY].refreshToken, "new-refresh");
});

test("disconnect clears local tokens and host permission after revocation failure", async () => {
  const storage = chromeStorage({
    [TaskHubClient.SETTINGS_KEY]: { baseUrl: "https://tasks.example.com" },
    [TaskHubClient.AUTH_KEY]: {
      accessToken: "access",
      refreshToken: "refresh",
      expiresAt: 9_999_999,
      clientId: "taskhub-helix-extension",
      tokenEndpoint: "https://tasks.example.com/idp/realms/taskhub/protocol/openid-connect/token",
      revocationEndpoint: "https://tasks.example.com/idp/realms/taskhub/protocol/openid-connect/revoke",
      user: {}
    }
  });
  const client = TaskHubClient.create({
    chrome: storage.chrome,
    crypto: webcrypto,
    fetch: async () => {
      throw new Error("offline");
    }
  });

  assert.deepEqual(await client.logout(), {
    connected: false,
    baseUrl: "",
    user: null,
    autoSync: true
  });
  assert.equal(storage.data[TaskHubClient.SETTINGS_KEY], undefined);
  assert.equal(storage.data[TaskHubClient.AUTH_KEY], undefined);
  assert.deepEqual(storage.removedPermissions, ["https://tasks.example.com/*"]);
});

test("disconnect updates Task Hub before revoking local credentials", async () => {
  const storage = chromeStorage({
    [TaskHubClient.SETTINGS_KEY]: { baseUrl: "https://tasks.example.com" },
    [TaskHubClient.AUTH_KEY]: {
      accessToken: "access",
      refreshToken: "refresh",
      expiresAt: 9_999_999,
      clientId: "taskhub-helix-extension",
      tokenEndpoint: "https://tasks.example.com/idp/realms/taskhub/protocol/openid-connect/token",
      revocationEndpoint: "https://tasks.example.com/idp/realms/taskhub/protocol/openid-connect/revoke",
      user: {}
    }
  });
  const requests = [];
  const client = TaskHubClient.create({
    chrome: storage.chrome,
    crypto: webcrypto,
    now: () => 1_000_000,
    fetch: async (url, options) => {
      requests.push({ url, options });
      return response(200, {});
    }
  });

  await client.logout();

  assert.equal(
    requests[0].url,
    "https://tasks.example.com/integrations/helix/auth/disconnect"
  );
  assert.equal(requests[0].options.headers["X-TaskHub-Extension-Version"], "1.8.1");
  assert.equal(
    requests[1].url,
    "https://tasks.example.com/idp/realms/taskhub/protocol/openid-connect/revoke"
  );
});
