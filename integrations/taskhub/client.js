(function attachHelixTaskHubClient(root, factory) {
  const api = factory();
  root.HelixTaskHubClient = api;
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  }
})(typeof globalThis !== "undefined" ? globalThis : this, function createTaskHubClientModule() {
  "use strict";

  const SETTINGS_KEY = "helix-taskhub-settings-v1";
  const AUTH_KEY = "helix-taskhub-auth-v1";
  const CALLBACK_PATH = "taskhub";
  const REFRESH_SKEW_MS = 60 * 1000;

  function normalizeBaseUrl(value) {
    const raw = String(value || "").trim();
    if (!raw) throw new Error("Enter the URL of your self-hosted Task Hub.");
    let url;
    try {
      url = new URL(raw);
    } catch (_error) {
      throw new Error("Enter a valid Task Hub URL.");
    }
    const localHost = ["localhost", "127.0.0.1", "[::1]"].includes(url.hostname);
    if (url.protocol !== "https:" && !(url.protocol === "http:" && localHost)) {
      throw new Error("Task Hub must use HTTPS. HTTP is allowed only for local development.");
    }
    if (url.username || url.password || url.search || url.hash) {
      throw new Error("Task Hub URL cannot include credentials, query parameters, or a fragment.");
    }
    return `${url.origin}${url.pathname.replace(/\/+$/, "")}`;
  }

  function permissionOrigin(value) {
    const url = new URL(normalizeBaseUrl(value));
    return `${url.origin}/*`;
  }

  function storageGet(chromeObject, key) {
    return new Promise((resolve) => {
      chromeObject.storage.local.get(key, (value) => {
        if (chromeObject.runtime.lastError) resolve(null);
        else resolve(value && value[key] ? value[key] : null);
      });
    });
  }

  function storageSet(chromeObject, key, value) {
    return new Promise((resolve, reject) => {
      chromeObject.storage.local.set({ [key]: value }, () => {
        if (chromeObject.runtime.lastError) reject(new Error(chromeObject.runtime.lastError.message));
        else resolve();
      });
    });
  }

  function storageRemove(chromeObject, keys) {
    return new Promise((resolve) => {
      chromeObject.storage.local.remove(keys, () => resolve());
    });
  }

  async function responsePayload(response) {
    const contentType = response.headers && response.headers.get
      ? response.headers.get("content-type") || ""
      : "";
    if (!contentType.includes("application/json")) return null;
    try {
      return await response.json();
    } catch (_error) {
      return null;
    }
  }

  function responseError(response, payload) {
    const details = payload && payload.details;
    const nestedMessage = details && typeof details === "object"
      ? details.detail || details.non_field_errors && details.non_field_errors[0]
      : null;
    const message = payload && (
      payload.message
      || payload.detail
      || payload.error_description
      || (typeof payload.error === "string" ? payload.error : "")
    )
      || nestedMessage
      || `Task Hub request failed (${response.status}).`;
    const error = new Error(String(message));
    error.status = response.status;
    error.code = payload && (
      payload.error_code
      || typeof payload.error === "string" && payload.error
      || payload.error && payload.error.code
    ) || "";
    return error;
  }

  function sameOriginUrl(value, expectedOrigin, label) {
    let parsed;
    try {
      parsed = new URL(String(value || ""));
    } catch (_error) {
      throw new Error(`Task Hub returned an invalid ${label}.`);
    }
    if (parsed.origin !== expectedOrigin || parsed.protocol !== new URL(expectedOrigin).protocol) {
      throw new Error(`Task Hub returned a ${label} outside the selected server.`);
    }
    return parsed.toString();
  }

  function base64Url(bytes) {
    let binary = "";
    bytes.forEach((value) => {
      binary += String.fromCharCode(value);
    });
    return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
  }

  function randomValue(cryptoObject, size) {
    const bytes = new Uint8Array(size);
    cryptoObject.getRandomValues(bytes);
    return base64Url(bytes);
  }

  async function codeChallenge(cryptoObject, verifier) {
    const encoded = new TextEncoder().encode(verifier);
    const digest = await cryptoObject.subtle.digest("SHA-256", encoded);
    return base64Url(new Uint8Array(digest));
  }

  function launchWebAuthFlow(chromeObject, url) {
    return new Promise((resolve, reject) => {
      chromeObject.identity.launchWebAuthFlow({ url, interactive: true }, (redirectedTo) => {
        if (chromeObject.runtime.lastError) {
          reject(new Error(chromeObject.runtime.lastError.message || "TaskHub ID sign-in was cancelled."));
        } else if (!redirectedTo) {
          reject(new Error("TaskHub ID sign-in did not return to the extension."));
        } else {
          resolve(redirectedTo);
        }
      });
    });
  }

  function removePermission(chromeObject, baseUrl) {
    if (!chromeObject.permissions || typeof chromeObject.permissions.remove !== "function" || !baseUrl) {
      return Promise.resolve();
    }
    return new Promise((resolve) => {
      chromeObject.permissions.remove({ origins: [permissionOrigin(baseUrl)] }, () => resolve());
    });
  }

  function create(dependencies) {
    const chromeObject = dependencies && dependencies.chrome
      || (typeof chrome !== "undefined" ? chrome : null);
    const fetchFunction = dependencies && dependencies.fetch
      || (typeof fetch !== "undefined" ? fetch.bind(globalThis) : null);
    const cryptoObject = dependencies && dependencies.crypto
      || (typeof crypto !== "undefined" ? crypto : null);
    const now = dependencies && dependencies.now || (() => Date.now());
    if (!chromeObject || !fetchFunction || !cryptoObject) {
      throw new Error("Chrome storage, identity, fetch, and Web Crypto are required.");
    }

    async function restrictCredentialStorage() {
      const storage = chromeObject.storage && chromeObject.storage.local;
      if (!storage || typeof storage.setAccessLevel !== "function") return;
      try {
        await storage.setAccessLevel({ accessLevel: "TRUSTED_CONTEXTS" });
      } catch (_error) {
        // Older supported Chromium builds may not expose the promise form.
      }
    }

    async function settings() {
      return await storageGet(chromeObject, SETTINGS_KEY) || null;
    }

    async function auth() {
      return await storageGet(chromeObject, AUTH_KEY) || null;
    }

    async function rawRequest(url, options) {
      const response = await fetchFunction(url, options);
      const payload = await responsePayload(response);
      if (!response.ok) throw responseError(response, payload);
      return payload;
    }

    async function discover(baseUrl) {
      const normalizedBaseUrl = normalizeBaseUrl(baseUrl);
      const expectedOrigin = new URL(normalizedBaseUrl).origin;
      const configuration = await rawRequest(
        `${normalizedBaseUrl}/integrations/helix/auth/config`,
        { method: "GET", headers: { Accept: "application/json" } }
      );
      if (!configuration || !configuration.client_id || !Array.isArray(configuration.required_scopes)) {
        throw new Error("Task Hub returned an invalid Helix OIDC configuration.");
      }

      const redirectUri = chromeObject.identity.getRedirectURL(CALLBACK_PATH);
      if (configuration.redirect_uri !== redirectUri) {
        throw new Error(
          `This extension package is not registered with Task Hub. Expected redirect ${redirectUri}.`
        );
      }

      const discoveryUrl = sameOriginUrl(
        configuration.oidc_discovery_url,
        expectedOrigin,
        "OIDC discovery URL"
      );
      const discovery = await rawRequest(discoveryUrl, {
        method: "GET",
        headers: { Accept: "application/json" }
      });
      if (!discovery || !discovery.issuer) {
        throw new Error("Task Hub returned an invalid OIDC discovery document.");
      }

      return {
        baseUrl: normalizedBaseUrl,
        clientId: String(configuration.client_id),
        redirectUri,
        scopes: configuration.required_scopes.map(String),
        issuer: sameOriginUrl(discovery.issuer, expectedOrigin, "OIDC issuer").replace(/\/$/, ""),
        authorizationEndpoint: sameOriginUrl(
          discovery.authorization_endpoint,
          expectedOrigin,
          "OIDC authorization endpoint"
        ),
        tokenEndpoint: sameOriginUrl(discovery.token_endpoint, expectedOrigin, "OIDC token endpoint"),
        revocationEndpoint: discovery.revocation_endpoint
          ? sameOriginUrl(discovery.revocation_endpoint, expectedOrigin, "OIDC revocation endpoint")
          : ""
      };
    }

    async function exchangeCode(metadata, code, verifier) {
      const body = new URLSearchParams({
        grant_type: "authorization_code",
        client_id: metadata.clientId,
        code,
        redirect_uri: metadata.redirectUri,
        code_verifier: verifier
      });
      return rawRequest(metadata.tokenEndpoint, {
        method: "POST",
        headers: {
          "Content-Type": "application/x-www-form-urlencoded",
          Accept: "application/json"
        },
        body: body.toString()
      });
    }

    function normalizedTokens(payload, previousRefreshToken) {
      const accessToken = String(payload && payload.access_token || "");
      const refreshToken = String(payload && payload.refresh_token || previousRefreshToken || "");
      const expiresIn = Number(payload && payload.expires_in);
      if (!accessToken || !refreshToken || !Number.isFinite(expiresIn) || expiresIn <= 0) {
        throw new Error("TaskHub ID returned an invalid token response.");
      }
      return {
        accessToken,
        refreshToken,
        expiresAt: now() + expiresIn * 1000
      };
    }

    async function login({ baseUrl }) {
      const metadata = await discover(baseUrl);
      const state = randomValue(cryptoObject, 32);
      const verifier = randomValue(cryptoObject, 64);
      const challenge = await codeChallenge(cryptoObject, verifier);
      const authorizationUrl = new URL(metadata.authorizationEndpoint);
      authorizationUrl.search = new URLSearchParams({
        response_type: "code",
        client_id: metadata.clientId,
        redirect_uri: metadata.redirectUri,
        scope: metadata.scopes.join(" "),
        state,
        code_challenge: challenge,
        code_challenge_method: "S256"
      }).toString();

      const callbackValue = await launchWebAuthFlow(chromeObject, authorizationUrl.toString());
      const callback = new URL(callbackValue);
      const expectedCallback = new URL(metadata.redirectUri);
      if (callback.origin !== expectedCallback.origin || callback.pathname !== expectedCallback.pathname) {
        throw new Error("TaskHub ID returned to an unexpected extension URL.");
      }
      if (callback.searchParams.get("error")) {
        throw new Error(
          callback.searchParams.get("error_description")
          || callback.searchParams.get("error")
          || "TaskHub ID sign-in failed."
        );
      }
      if (callback.searchParams.get("state") !== state) {
        throw new Error("TaskHub ID sign-in state did not match. Try again.");
      }
      const code = callback.searchParams.get("code");
      if (!code) throw new Error("TaskHub ID did not return an authorization code.");

      const tokenPayload = await exchangeCode(metadata, code, verifier);
      const tokens = normalizedTokens(tokenPayload);
      const user = await rawRequest(`${metadata.baseUrl}/integrations/helix/auth/session`, {
        method: "GET",
        headers: {
          Accept: "application/json",
          Authorization: `Bearer ${tokens.accessToken}`
        }
      });
      if (!user || !user.id || !user.email) {
        throw new Error("Task Hub returned an invalid extension session.");
      }

      const connectedAt = new Date(now()).toISOString();
      await storageSet(chromeObject, SETTINGS_KEY, {
        baseUrl: metadata.baseUrl,
        autoSync: true,
        connectedAt
      });
      await storageSet(chromeObject, AUTH_KEY, {
        ...tokens,
        clientId: metadata.clientId,
        tokenEndpoint: metadata.tokenEndpoint,
        revocationEndpoint: metadata.revocationEndpoint,
        user
      });
      return { connected: true, baseUrl: metadata.baseUrl, user, autoSync: true, connectedAt };
    }

    async function refreshCredentials(currentSettings, currentAuth) {
      if (
        !currentSettings
        || !currentAuth
        || !currentAuth.refreshToken
        || !currentAuth.tokenEndpoint
        || !currentAuth.clientId
      ) {
        throw new Error("Task Hub login has expired. Sign in again.");
      }
      try {
        const payload = await rawRequest(currentAuth.tokenEndpoint, {
          method: "POST",
          headers: {
            "Content-Type": "application/x-www-form-urlencoded",
            Accept: "application/json"
          },
          body: new URLSearchParams({
            grant_type: "refresh_token",
            client_id: currentAuth.clientId,
            refresh_token: currentAuth.refreshToken
          }).toString()
        });
        const nextTokens = normalizedTokens(payload, currentAuth.refreshToken);
        const nextAuth = { ...currentAuth, ...nextTokens };
        await storageSet(chromeObject, AUTH_KEY, nextAuth);
        return nextAuth;
      } catch (error) {
        await storageRemove(chromeObject, AUTH_KEY);
        if (error && error.code === "invalid_grant") {
          throw new Error("Task Hub login has expired. Sign in again.");
        }
        throw error;
      }
    }

    async function authenticatedRequest(path, options) {
      const currentSettings = await settings();
      let currentAuth = await auth();
      if (!currentSettings || !currentAuth || !currentAuth.accessToken) {
        throw new Error("Connect and sign in to Task Hub first.");
      }
      if (!currentAuth.expiresAt || currentAuth.expiresAt <= now() + REFRESH_SKEW_MS) {
        currentAuth = await refreshCredentials(currentSettings, currentAuth);
      }
      const send = async (credentials) => rawRequest(`${currentSettings.baseUrl}${path}`, {
        ...(options || {}),
        headers: {
          "Content-Type": "application/json",
          ...(options && options.headers ? options.headers : {}),
          Authorization: `Bearer ${credentials.accessToken}`
        }
      });
      try {
        return await send(currentAuth);
      } catch (error) {
        if (error.status !== 401) throw error;
        currentAuth = await refreshCredentials(currentSettings, currentAuth);
        return send(currentAuth);
      }
    }

    async function status() {
      const currentSettings = await settings();
      const currentAuth = await auth();
      return {
        connected: Boolean(
          currentSettings
          && currentAuth
          && currentAuth.accessToken
          && currentAuth.refreshToken
          && currentAuth.tokenEndpoint
        ),
        baseUrl: currentSettings && currentSettings.baseUrl || "",
        autoSync: currentSettings ? currentSettings.autoSync !== false : true,
        connectedAt: currentSettings && currentSettings.connectedAt || null,
        user: currentAuth && currentAuth.user || null
      };
    }

    async function updateSettings(update) {
      const current = await settings();
      if (!current) throw new Error("Connect to Task Hub before changing sync settings.");
      const next = {
        ...current,
        autoSync: update && update.autoSync !== false
      };
      await storageSet(chromeObject, SETTINGS_KEY, next);
      return status();
    }

    async function logout() {
      const currentSettings = await settings();
      const currentAuth = await auth();
      try {
        if (
          currentAuth
          && currentAuth.revocationEndpoint
          && currentAuth.refreshToken
          && currentAuth.clientId
        ) {
          try {
            await rawRequest(currentAuth.revocationEndpoint, {
              method: "POST",
              headers: {
                "Content-Type": "application/x-www-form-urlencoded",
                Accept: "application/json"
              },
              body: new URLSearchParams({
                token: currentAuth.refreshToken,
                token_type_hint: "refresh_token",
                client_id: currentAuth.clientId
              }).toString()
            });
          } catch (_error) {
            // Local disconnect must still work if the self-hosted server is offline.
          }
        }
      } finally {
        await storageRemove(chromeObject, [AUTH_KEY, SETTINGS_KEY]);
        await removePermission(chromeObject, currentSettings && currentSettings.baseUrl);
      }
      return { connected: false, baseUrl: "", user: null, autoSync: true };
    }

    async function sync(snapshot) {
      return authenticatedRequest("/integrations/helix/sync", {
        method: "POST",
        body: JSON.stringify(snapshot)
      });
    }

    return Object.freeze({
      restrictCredentialStorage,
      login,
      logout,
      status,
      updateSettings,
      sync
    });
  }

  return Object.freeze({
    SETTINGS_KEY,
    AUTH_KEY,
    CALLBACK_PATH,
    normalizeBaseUrl,
    permissionOrigin,
    create
  });
});
