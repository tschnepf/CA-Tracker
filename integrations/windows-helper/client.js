(function attachWindowsHelperClient(root, factory) {
  const api = factory();
  root.TaskHubWindowsHelperClient = api;
  if (typeof module === "object" && module.exports) module.exports = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function createWindowsHelperClientModule() {
  "use strict";

  // Optional bridge to the CA Tracker Windows helper through Chrome native messaging.
  // The nativeMessaging permission is optional and requested from extension settings;
  // without it (or without the helper installed) every call resolves quietly so Helix
  // and Procore keep working exactly as before.
  const HOST_NAME = "com.smc.catracker";
  const PERMISSION = { permissions: ["nativeMessaging"] };
  const MAX_MESSAGE_BYTES = 8 * 1024 * 1024;

  function create(options) {
    const chromeObject = options && options.chrome;

    function hasPermission() {
      return new Promise((resolve) => {
        if (!chromeObject || !chromeObject.permissions
          || typeof chromeObject.permissions.contains !== "function") {
          resolve(false);
          return;
        }
        try {
          const maybePromise = chromeObject.permissions.contains(PERMISSION, (granted) => {
            resolve(Boolean(granted));
          });
          if (maybePromise && typeof maybePromise.then === "function") {
            maybePromise.then((granted) => resolve(Boolean(granted)), () => resolve(false));
          }
        } catch (_error) {
          resolve(false);
        }
      });
    }

    function sendNative(message) {
      return new Promise((resolve, reject) => {
        const runtime = chromeObject && chromeObject.runtime;
        if (!runtime || typeof runtime.sendNativeMessage !== "function") {
          reject(new Error("Chrome has not granted the Windows helper permission."));
          return;
        }
        runtime.sendNativeMessage(HOST_NAME, message, (response) => {
          const lastError = runtime.lastError;
          if (lastError) {
            reject(new Error(/not found/i.test(lastError.message || "")
              ? "The CA Tracker Windows helper is not installed or not registered with Chrome."
              : lastError.message || "The Windows helper did not respond."));
            return;
          }
          if (!response || response.ok !== true) {
            reject(new Error(response && response.error || "The Windows helper rejected the request."));
            return;
          }
          resolve(response);
        });
      });
    }

    async function status() {
      const permission = await hasPermission();
      if (!permission) return { permission: false, installed: false };
      try {
        const response = await sendNative({ type: "ping" });
        return { permission: true, installed: true, version: String(response.version || "") };
      } catch (error) {
        return { permission: true, installed: false, error: error.message };
      }
    }

    function validateSnapshot(snapshot) {
      if (!snapshot || typeof snapshot !== "object") throw new Error("A helper snapshot is required.");
      if (!["helix", "procore"].includes(snapshot.provider)) throw new Error("Unsupported snapshot provider.");
      if (!snapshot.projectId || !Array.isArray(snapshot.items) || !Array.isArray(snapshot.itemTypes)) {
        throw new Error("Helper snapshots require a project, item types, and items.");
      }
      const size = JSON.stringify(snapshot).length;
      if (size > MAX_MESSAGE_BYTES) throw new Error("The project snapshot is too large for the Windows helper.");
    }

    function validRefreshId(value) {
      return typeof value === "string" && /^[a-f0-9]{32}$/.test(value);
    }

    async function sendSnapshot(snapshot, extras) {
      validateSnapshot(snapshot);
      if (!await hasPermission()) return { sent: false, reason: "not_enabled" };
      const message = { type: "snapshot", snapshot };
      const options = extras && typeof extras === "object" ? extras : {};
      if (options.catalog && typeof options.catalog === "object"
        && options.catalog.provider === snapshot.provider && Array.isArray(options.catalog.projects)) {
        message.catalog = options.catalog;
      }
      if (options.refresh && validRefreshId(options.refresh.id)) {
        message.refresh = { id: options.refresh.id, step: Number(options.refresh.step) || 0 };
      }
      try {
        const response = await sendNative(message);
        return {
          sent: true,
          itemCount: snapshot.items.length,
          helperVersion: String(response.version || ""),
          refresh: response.refresh || null
        };
      } catch (error) {
        return { sent: false, reason: "unavailable", error: error.message };
      }
    }

    async function claimRefresh(id, provider) {
      if (!validRefreshId(id)) throw new Error("Invalid refresh request.");
      if (!await hasPermission()) {
        throw new Error("Enable the Windows helper in TaskHub Helper settings so the CA Tracker app can refresh.");
      }
      const response = await sendNative({ type: "claimRefresh", id, provider });
      return response.request;
    }

    async function refreshFailed(id, error) {
      if (!validRefreshId(id) || !await hasPermission()) return false;
      try {
        await sendNative({ type: "refreshFailed", id, error: String(error || "").slice(0, 500) });
        return true;
      } catch (_error) {
        return false;
      }
    }

    return Object.freeze({ status, sendSnapshot, hasPermission, claimRefresh, refreshFailed });
  }

  return Object.freeze({ HOST_NAME, PERMISSION, create });
});
