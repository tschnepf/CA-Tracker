(function startTaskHubOptions() {
  "use strict";

  const form = document.getElementById("login-form");
  const connected = document.getElementById("connected");
  const baseUrl = document.getElementById("base-url");
  const connectButton = document.getElementById("connect");
  const disconnectButton = document.getElementById("disconnect");
  const autoSync = document.getElementById("auto-sync");
  const connectedUrl = document.getElementById("connected-url");
  const connectedUser = document.getElementById("connected-user");
  const message = document.getElementById("message");
  const cacheUsage = document.getElementById("cache-usage");
  const cacheCount = document.getElementById("cache-count");
  const clearCacheButton = document.getElementById("clear-cache");
  const cacheMessage = document.getElementById("cache-message");
  let cacheEntryCount = 0;

  function runtimeMessage(value) {
    return new Promise((resolve, reject) => {
      chrome.runtime.sendMessage(value, (response) => {
        if (chrome.runtime.lastError) reject(new Error(chrome.runtime.lastError.message));
        else if (!response || response.ok !== true) reject(new Error(response && response.error || "Task Hub request failed."));
        else resolve(response.result);
      });
    });
  }

  function showMessage(value, isError) {
    message.textContent = value || "";
    message.classList.toggle("error", Boolean(isError));
  }

  function showCacheMessage(value, isError) {
    cacheMessage.textContent = value || "";
    cacheMessage.classList.toggle("error", Boolean(isError));
  }

  function formatBytes(value) {
    const bytes = Math.max(0, Number(value) || 0);
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  function renderCacheStatus(status) {
    const bytesInUse = Number(status && status.bytesInUse) || 0;
    const budgetBytes = Number(status && status.budgetBytes) || 5 * 1024 * 1024;
    const entryCount = Number(status && status.entryCount) || 0;
    cacheEntryCount = entryCount;
    cacheUsage.textContent = `${formatBytes(bytesInUse)} of ${formatBytes(budgetBytes)}`;
    cacheCount.textContent = `${entryCount} cached Helix ${entryCount === 1 ? "identity" : "identities"}`;
    clearCacheButton.disabled = entryCount === 0;
  }

  function render(status) {
    const isConnected = Boolean(status && status.connected);
    form.hidden = isConnected;
    connected.hidden = !isConnected;
    if (isConnected) {
      connectedUrl.textContent = status.baseUrl;
      connectedUser.textContent = status.user && (status.user.display_name || status.user.email) || "Connected user";
      autoSync.checked = status.autoSync !== false;
    } else if (status && status.baseUrl) {
      baseUrl.value = status.baseUrl;
    }
  }

  async function loadStatus() {
    try {
      render(await runtimeMessage({ type: "HELIX_TASKHUB_STATUS" }));
    } catch (error) {
      showMessage(error.message, true);
    }
  }

  async function loadCacheStatus() {
    try {
      renderCacheStatus(await runtimeMessage({ type: "HELIX_ACTIVITY_CACHE_STATUS" }));
    } catch (error) {
      cacheUsage.textContent = "Unavailable";
      cacheCount.textContent = "";
      cacheEntryCount = 0;
      clearCacheButton.disabled = true;
      showCacheMessage(error.message, true);
    }
  }

  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    showMessage("Requesting permission…");
    connectButton.disabled = true;
    try {
      const normalized = HelixTaskHubClient.normalizeBaseUrl(baseUrl.value);
      const granted = await chrome.permissions.request({
        origins: [HelixTaskHubClient.permissionOrigin(normalized)]
      });
      if (!granted) throw new Error("Chrome did not grant access to that Task Hub server.");
      showMessage("Opening TaskHub ID…");
      const status = await runtimeMessage({
        type: "HELIX_TASKHUB_OIDC_LOGIN",
        baseUrl: normalized
      });
      render(status);
      showMessage("Connected. Return to Helix and refresh the activity queue to sync.");
    } catch (error) {
      showMessage(error.message, true);
    } finally {
      connectButton.disabled = false;
    }
  });

  autoSync.addEventListener("change", async () => {
    try {
      render(await runtimeMessage({
        type: "HELIX_TASKHUB_UPDATE_SETTINGS",
        autoSync: autoSync.checked
      }));
      showMessage(autoSync.checked ? "Automatic sync enabled." : "Automatic sync disabled.");
    } catch (error) {
      showMessage(error.message, true);
    }
  });

  disconnectButton.addEventListener("click", async () => {
    disconnectButton.disabled = true;
    try {
      render(await runtimeMessage({ type: "HELIX_TASKHUB_LOGOUT" }));
      showMessage("Disconnected. Local Helix activity remains available.");
    } catch (error) {
      showMessage(error.message, true);
    } finally {
      disconnectButton.disabled = false;
    }
  });

  clearCacheButton.addEventListener("click", async () => {
    clearCacheButton.disabled = true;
    showCacheMessage("Clearing cached Helix data…");
    try {
      const result = await runtimeMessage({ type: "HELIX_ACTIVITY_CACHE_CLEAR" });
      renderCacheStatus(result);
      const entries = Number(result && result.entriesCleared) || 0;
      showCacheMessage(
        `Cleared ${entries} cached ${entries === 1 ? "entry" : "entries"} (${formatBytes(result && result.bytesCleared)}). Open Helix and refresh to rebuild.`
      );
    } catch (error) {
      showCacheMessage(error.message, true);
      await loadCacheStatus();
    } finally {
      clearCacheButton.disabled = cacheEntryCount === 0;
    }
  });

  void loadStatus();
  void loadCacheStatus();
})();
