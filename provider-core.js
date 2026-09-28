(function attachTaskHubProviderCore(root, factory) {
  const api = factory();
  root.TaskHubProviderCore = api;
  if (typeof module === "object" && module.exports) module.exports = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function createProviderCore() {
  "use strict";

  const PROVIDERS = Object.freeze({
    HELIX: "helix",
    PROCORE: "procore"
  });

  const PROCORE_HOST = /^(?:app|[a-z]{2}\d{2})\.procore\.com$/i;
  const HELIX_PATH = /^\/Projects\/[^/]+\/(?:Overview|RFI|Submittals)(?:\/|$)/i;
  const PROCORE_PATHS = [
    /^\/\d+\/project\//i,
    /^\/webclients\/host\/companies\/\d+(?:\/projects\/\d+)?\/tools\//i
  ];

  function parsedUrl(value) {
    try {
      return new URL(String(value || ""));
    } catch (_error) {
      return null;
    }
  }

  function providerForUrl(value) {
    const url = parsedUrl(value);
    if (!url || url.protocol !== "https:") return null;
    if (url.hostname === "project.helixteam.com" && HELIX_PATH.test(url.pathname)) {
      return PROVIDERS.HELIX;
    }
    if (PROCORE_HOST.test(url.hostname)
      && PROCORE_PATHS.some((pattern) => pattern.test(url.pathname))) {
      return PROVIDERS.PROCORE;
    }
    return null;
  }

  function requireProvider(value) {
    const normalized = String(value || "").toLowerCase();
    if (!Object.values(PROVIDERS).includes(normalized)) {
      throw new Error("Unsupported activity provider.");
    }
    return normalized;
  }

  function scopedKey(provider, accountId, identityId) {
    const normalizedProvider = requireProvider(provider);
    const normalizedAccount = String(accountId || "").trim();
    const normalizedIdentity = String(identityId || "").trim();
    if (!normalizedAccount || !normalizedIdentity) {
      throw new Error("Provider cache keys require account and identity identifiers.");
    }
    return [normalizedProvider, normalizedAccount, normalizedIdentity]
      .map((part) => encodeURIComponent(part))
      .join(":");
  }

  function normalizeWorkItem(provider, item) {
    const normalizedProvider = requireProvider(provider);
    if (!item || typeof item !== "object") throw new Error("A work item is required.");
    const itemType = String(item.itemType || "").toLowerCase();
    if (!new Set(["rfi", "submittal"]).has(itemType)) {
      throw new Error("Work items must be RFIs or submittals.");
    }
    const itemId = String(item.itemId || item.id || "").trim();
    const projectId = String(item.projectId || "").trim();
    const title = String(item.title || "").trim();
    if (!itemId || !projectId || !title) {
      throw new Error("Work items require item, project, and title identifiers.");
    }
    return Object.freeze({
      ...item,
      provider: normalizedProvider,
      itemType,
      itemId,
      projectId,
      number: String(item.number || "").trim(),
      title,
      status: String(item.status || "").trim(),
      sourceMetadata: item.sourceMetadata && typeof item.sourceMetadata === "object"
        ? { ...item.sourceMetadata }
        : {}
    });
  }

  return Object.freeze({
    PROVIDERS,
    normalizeWorkItem,
    providerForUrl,
    requireProvider,
    scopedKey
  });
});
