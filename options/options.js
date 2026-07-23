(function startTaskHubOptions() {
  "use strict";

  const form = document.getElementById("login-form");
  const connected = document.getElementById("connected");
  const baseUrl = document.getElementById("base-url");
  const email = document.getElementById("email");
  const password = document.getElementById("password");
  const connectButton = document.getElementById("connect");
  const disconnectButton = document.getElementById("disconnect");
  const autoSync = document.getElementById("auto-sync");
  const connectedUrl = document.getElementById("connected-url");
  const connectedUser = document.getElementById("connected-user");
  const message = document.getElementById("message");

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
      showMessage("Signing in…");
      const status = await runtimeMessage({
        type: "HELIX_TASKHUB_LOGIN",
        baseUrl: normalized,
        email: email.value,
        password: password.value
      });
      password.value = "";
      render(status);
      showMessage("Connected. Return to Helix and refresh the activity queue to sync.");
    } catch (error) {
      password.value = "";
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

  void loadStatus();
})();

