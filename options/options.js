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
  const procoreState = document.getElementById("procore-state");
  const procoreAccount = document.getElementById("procore-account");
  const connectProcoreButton = document.getElementById("connect-procore");
  const disconnectProcoreButton = document.getElementById("disconnect-procore");
  const procoreMessage = document.getElementById("procore-message");
  const helperState = document.getElementById("helper-state");
  const helperDetail = document.getElementById("helper-detail");
  const enableHelperButton = document.getElementById("enable-helper");
  const disableHelperButton = document.getElementById("disable-helper");
  const helperMessage = document.getElementById("helper-message");
  const HELPER_PERMISSION = { permissions: ["nativeMessaging"] };
  let cacheEntryCount = 0;
  let taskHubConnected = false;

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

  function showProcoreMessage(value, isError) {
    procoreMessage.textContent = value || "";
    procoreMessage.classList.toggle("error", Boolean(isError));
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
    cacheCount.textContent = `${entryCount} cached activity ${entryCount === 1 ? "context" : "contexts"}`;
    clearCacheButton.disabled = entryCount === 0;
  }

  function render(status) {
    const isConnected = Boolean(status && status.connected);
    taskHubConnected = isConnected;
    form.hidden = isConnected;
    connected.hidden = !isConnected;
    if (isConnected) {
      connectedUrl.textContent = status.baseUrl;
      connectedUser.textContent = status.user && (status.user.display_name || status.user.email) || "Connected user";
      autoSync.checked = status.autoSync !== false;
    } else if (status && status.baseUrl) {
      baseUrl.value = status.baseUrl;
    }
    connectProcoreButton.disabled = !isConnected;
  }

  function renderProcore(status) {
    const isConnected = Boolean(status && status.connected);
    procoreState.textContent = isConnected ? "API connected" : "Standalone";
    procoreAccount.textContent = isConnected
      ? status.display_name || status.login || `Procore user ${status.procore_user_id || ""}`
      : taskHubConnected
        ? "Optional: connect for broader API-backed scans."
        : "Visible-page activity works without Task Hub or a Procore API connection.";
    connectProcoreButton.hidden = isConnected;
    connectProcoreButton.disabled = !taskHubConnected;
    disconnectProcoreButton.hidden = !isConnected;
  }

  async function loadStatus() {
    try {
      const status = await runtimeMessage({ type: "TASKHUB_STATUS" });
      render(status);
      if (status.connected) {
        renderProcore(await runtimeMessage({ type: "PROCORE_STATUS" }));
      } else {
        renderProcore({ connected: false });
      }
    } catch (error) {
      showMessage(error.message, true);
      renderProcore({ connected: false });
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
      const normalized = TaskHubHelperClient.normalizeBaseUrl(baseUrl.value);
      const granted = await chrome.permissions.request({
        origins: [TaskHubHelperClient.permissionOrigin(normalized)]
      });
      if (!granted) throw new Error("Chrome did not grant access to that Task Hub server.");
      showMessage("Opening TaskHub ID…");
      const status = await runtimeMessage({
        type: "HELIX_TASKHUB_OIDC_LOGIN",
        baseUrl: normalized
      });
      render(status);
      showMessage("Task Hub connected. Sync is ready; the Procore API connection below remains optional.");
      await loadStatus();
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
      renderProcore({ connected: false });
      showMessage("Disconnected. Locally cached activity remains available.");
    } catch (error) {
      showMessage(error.message, true);
    } finally {
      disconnectButton.disabled = false;
    }
  });

  clearCacheButton.addEventListener("click", async () => {
    clearCacheButton.disabled = true;
    showCacheMessage("Clearing cached activity data…");
    try {
      const result = await runtimeMessage({ type: "HELIX_ACTIVITY_CACHE_CLEAR" });
      renderCacheStatus(result);
      const entries = Number(result && result.entriesCleared) || 0;
      showCacheMessage(
        `Cleared ${entries} cached ${entries === 1 ? "entry" : "entries"} (${formatBytes(result && result.bytesCleared)}). Open Helix or Procore and refresh to rebuild.`
      );
    } catch (error) {
      showCacheMessage(error.message, true);
      await loadCacheStatus();
    } finally {
      clearCacheButton.disabled = cacheEntryCount === 0;
    }
  });

  connectProcoreButton.addEventListener("click", async () => {
    connectProcoreButton.disabled = true;
    showProcoreMessage("Opening Procore authorization…");
    try {
      const status = await runtimeMessage({ type: "PROCORE_CONNECT" });
      renderProcore(status);
      showProcoreMessage("Procore API connected. Broader API-backed assignment scans are now available.");
    } catch (error) {
      showProcoreMessage(error.message, true);
    } finally {
      connectProcoreButton.disabled = false;
    }
  });

  disconnectProcoreButton.addEventListener("click", async () => {
    disconnectProcoreButton.disabled = true;
    try {
      renderProcore(await runtimeMessage({ type: "PROCORE_DISCONNECT" }));
      showProcoreMessage("Procore disconnected from Task Hub.");
    } catch (error) {
      showProcoreMessage(error.message, true);
    } finally {
      disconnectProcoreButton.disabled = false;
    }
  });

  function showHelperMessage(value, isError) {
    helperMessage.textContent = value || "";
    helperMessage.classList.toggle("error", Boolean(isError));
  }

  function renderHelper(status) {
    const permission = Boolean(status && status.permission);
    const installed = Boolean(status && status.installed);
    helperState.textContent = !permission ? "Off" : installed ? "Connected" : "Not found";
    helperDetail.textContent = !permission
      ? "Optional: enable to feed the CA Tracker app on this Windows computer."
      : installed
        ? `CA Tracker helper ${status.version || ""} is receiving item lists after each fresh scan.`.replace("  ", " ")
        : "Chrome allows the helper, but it is not installed or registered on this computer. Open the CA Tracker app and choose Settings → Register with Chrome.";
    enableHelperButton.hidden = permission;
    disableHelperButton.hidden = !permission;
  }

  async function loadHelperStatus() {
    try {
      renderHelper(await runtimeMessage({ type: "WINDOWS_HELPER_STATUS" }));
    } catch (error) {
      renderHelper({ permission: false });
      showHelperMessage(error.message, true);
    }
  }

  enableHelperButton.addEventListener("click", async () => {
    enableHelperButton.disabled = true;
    try {
      const granted = await chrome.permissions.request(HELPER_PERMISSION);
      if (!granted) throw new Error("Chrome did not grant the Windows helper permission.");
      showHelperMessage("Windows helper enabled. Refresh a Helix or Procore page to send its items.");
      await loadHelperStatus();
    } catch (error) {
      showHelperMessage(error.message, true);
    } finally {
      enableHelperButton.disabled = false;
    }
  });

  disableHelperButton.addEventListener("click", async () => {
    disableHelperButton.disabled = true;
    try {
      await chrome.permissions.remove(HELPER_PERMISSION);
      showHelperMessage("Windows helper disabled. Item lists are no longer sent.");
      renderHelper({ permission: false });
    } catch (error) {
      showHelperMessage(error.message, true);
    } finally {
      disableHelperButton.disabled = false;
    }
  });

  void loadStatus();
  void loadCacheStatus();
  void loadHelperStatus();
})();
