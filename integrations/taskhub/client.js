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
    const message = payload && (payload.message || payload.detail)
      || nestedMessage
      || `Task Hub request failed (${response.status}).`;
    const error = new Error(String(message));
    error.status = response.status;
    return error;
  }

  function create(dependencies) {
    const chromeObject = dependencies && dependencies.chrome
      || (typeof chrome !== "undefined" ? chrome : null);
    const fetchFunction = dependencies && dependencies.fetch
      || (typeof fetch !== "undefined" ? fetch.bind(globalThis) : null);
    if (!chromeObject || !fetchFunction) {
      throw new Error("Chrome storage and fetch are required.");
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

    async function rawRequest(baseUrl, path, options) {
      const response = await fetchFunction(`${normalizeBaseUrl(baseUrl)}${path}`, options);
      const payload = await responsePayload(response);
      if (!response.ok) throw responseError(response, payload);
      return payload;
    }

    async function refreshCredentials(currentSettings, currentAuth) {
      if (!currentSettings || !currentAuth || !currentAuth.refresh) {
        throw new Error("Task Hub login has expired. Sign in again.");
      }
      try {
        const payload = await rawRequest(
          currentSettings.baseUrl,
          "/integrations/helix/auth/refresh",
          {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ refresh: currentAuth.refresh })
          }
        );
        const nextAuth = {
          access: String(payload.access || ""),
          refresh: String(payload.refresh || currentAuth.refresh),
          user: currentAuth.user
        };
        if (!nextAuth.access) throw new Error("Task Hub did not return a refreshed access token.");
        await storageSet(chromeObject, AUTH_KEY, nextAuth);
        return nextAuth;
      } catch (error) {
        await storageRemove(chromeObject, AUTH_KEY);
        throw error;
      }
    }

    async function authenticatedRequest(path, options) {
      const currentSettings = await settings();
      let currentAuth = await auth();
      if (!currentSettings || !currentAuth || !currentAuth.access) {
        throw new Error("Connect and sign in to Task Hub first.");
      }
      const send = async (credentials) => rawRequest(currentSettings.baseUrl, path, {
        ...(options || {}),
        headers: {
          "Content-Type": "application/json",
          ...(options && options.headers ? options.headers : {}),
          Authorization: `Bearer ${credentials.access}`
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

    async function login({ baseUrl, email, password }) {
      const normalizedBaseUrl = normalizeBaseUrl(baseUrl);
      const payload = await rawRequest(normalizedBaseUrl, "/integrations/helix/auth/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          email: String(email || "").trim(),
          password: String(password || "")
        })
      });
      if (!payload || !payload.access || !payload.refresh || !payload.user) {
        throw new Error("Task Hub returned an invalid login response.");
      }
      const connectedAt = new Date().toISOString();
      await storageSet(chromeObject, SETTINGS_KEY, {
        baseUrl: normalizedBaseUrl,
        autoSync: true,
        connectedAt
      });
      await storageSet(chromeObject, AUTH_KEY, {
        access: String(payload.access),
        refresh: String(payload.refresh),
        user: payload.user
      });
      return { connected: true, baseUrl: normalizedBaseUrl, user: payload.user, autoSync: true, connectedAt };
    }

    async function status() {
      const currentSettings = await settings();
      const currentAuth = await auth();
      return {
        connected: Boolean(currentSettings && currentAuth && currentAuth.refresh),
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
        if (currentSettings && currentAuth && currentAuth.refresh) {
          try {
            await rawRequest(currentSettings.baseUrl, "/integrations/helix/auth/logout", {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ refresh: currentAuth.refresh })
            });
          } catch (_error) {
            // Local disconnect must still work if the self-hosted server is offline.
          }
        }
      } finally {
        await storageRemove(chromeObject, [AUTH_KEY, SETTINGS_KEY]);
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
    normalizeBaseUrl,
    permissionOrigin,
    create
  });
});
