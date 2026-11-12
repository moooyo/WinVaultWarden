// ISOLATED-world relay. It runs in the extension's private world (manifest `world: ISOLATED`), so
// its variables are unreachable from the page, but the window.postMessage medium it shares with the
// MAIN shim is not — hence it treats the channel purely as correlation and lets the browser rebuild
// the trusted context. It keeps a long-lived runtime port while the document is active and replaces
// it after disconnection or BFCache restoration. It reads
// the frame's Permissions Policy verdict directly from the real document so a sub-frame can only get
// a passkey when the top document delegated `publickey-credentials-get`. Page-supplied
// origin/crossOrigin/context are stripped before anything leaves the frame; disconnect reasons and
// chrome.runtime.lastError never reach the page.
(function installWinVaultWardenIsolatedRelay() {
  const MAIN_SOURCE = "winvaultwarden-main";
  const ISOLATED_SOURCE = "winvaultwarden-isolated";
  const PORT_NAME = "winvaultwarden-passkey-frame-v1";

  // Real Chrome/Edge dedupe an identical content-script file path across content_scripts entries
  // and only inject it into the first world that declares it, so this file cannot rely on a
  // `request-coordinator.js` sibling ever having run in the ISOLATED world (it only runs in MAIN).
  // Mint the 32-byte channel id locally instead of importing the shared coordinator.
  function randomChannelId() {
    const bytes = new Uint8Array(32);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
  }

  if (window.__winVaultWardenIsolatedRelay) {
    return;
  }
  Object.defineProperty(window, "__winVaultWardenIsolatedRelay", { value: true });

  const channelId = randomChannelId();
  const active = new Set();
  let port = null;
  let suspended = false;
  let reconnectTimer = null;
  let reconnectAttempts = 0;

  function targetOrigin() {
    const origin = window.location.origin;
    return origin && origin !== "null" ? origin : "*";
  }

  function postToMain(message) {
    window.postMessage({ source: ISOLATED_SOURCE, ...message }, targetOrigin());
  }

  function failure(id, code) {
    return { v: 1, id, type: "error", ok: false, error: { code } };
  }

  function allowsFeature(feature) {
    try {
      const policy = document.permissionsPolicy ?? document.featurePolicy;
      // Top documents can explicitly disable WebAuthn too. Absence of the policy API is not a grant.
      return policy?.allowsFeature?.(feature) === true;
    } catch {
      return false;
    }
  }

  function abortActive() {
    const ids = [...active];
    active.clear();
    for (const id of ids) {
      postToMain({ channelId, id, kind: "response", response: failure(id, "operation_aborted") });
    }
  }

  function ensurePort() {
    if (suspended) return null;
    if (port) return port;
    if (reconnectTimer !== null) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
    let connected;
    try {
      connected = chrome.runtime.connect({ name: PORT_NAME });
    } catch {
      scheduleReconnect();
      return null;
    }
    port = connected;
    connected.onMessage.addListener((message) => {
      if (port === connected && message?.kind === "frame.ready") {
        reconnectAttempts = 0;
        return;
      }
      // A late message from an old connection must never settle a request on its replacement.
      if (port !== connected || !message || typeof message.id !== "string" || !active.delete(message.id)) {
        return;
      }
      const response =
        message.response && typeof message.response === "object"
          ? { ...message.response, v: 1, id: message.id }
          : failure(message.id, "internal_error");
      postToMain({ channelId, id: message.id, kind: "response", response });
    });
    connected.onDisconnect.addListener(() => {
      // Browser diagnostics may include private paths. Only the stable code reaches the page.
      void chrome.runtime.lastError;
      if (port !== connected) return;
      port = null;
      abortActive();
      // Rebuild ancestor identity after a worker restart without replaying interrupted ceremonies.
      scheduleReconnect();
    });
    return connected;
  }

  function scheduleReconnect() {
    if (suspended || port || reconnectTimer !== null || reconnectAttempts >= 5) return;
    const delay = 100 * (2 ** reconnectAttempts++);
    reconnectTimer = setTimeout(() => {
      reconnectTimer = null;
      ensurePort();
    }, delay);
  }

  window.addEventListener("message", (event) => {
    const message = event.data;
    if (event.source !== window || message?.source !== MAIN_SOURCE) {
      return;
    }

    if (message.kind === "main.ready" && typeof message.bootstrapId === "string") {
      postToMain({ kind: "isolated.ready", bootstrapId: message.bootstrapId, channelId });
      return;
    }

    if (message.channelId !== channelId || typeof message.id !== "string") {
      return;
    }

    if (message.kind === "request") {
      if (active.has(message.id)) {
        return;
      }
      if (!message.request || typeof message.request !== "object" || message.request.id !== message.id) {
        postToMain({
          channelId,
          id: message.id,
          kind: "response",
          response: failure(message.id, "security_sender"),
        });
        return;
      }

      const request = structuredClone(message.request);
      // The envelope id is authoritative throughout the frame relay. Reassign it after cloning so
      // no outer/inner divergence can cross the runtime port boundary.
      request.id = message.id;
      delete request.origin;
      delete request.crossOrigin;
      delete request.context;
      delete request.topOrigin;
      delete request.sameOriginWithAncestors;
      if (request.payload && typeof request.payload === "object") {
        delete request.payload.origin;
        delete request.payload.crossOrigin;
        delete request.payload.context;
        delete request.payload.frameOrigin;
        delete request.payload.topOrigin;
        delete request.payload.sameOriginWithAncestors;
      }

      active.add(message.id);
      try {
        const connected = ensurePort();
        if (!connected) throw new Error("Unavailable runtime port");
        connected.postMessage({
          kind: "request",
          id: message.id,
          request,
          publicKeyCredentialsGetAllowed: allowsFeature("publickey-credentials-get"),
          publicKeyCredentialsCreateAllowed: allowsFeature("publickey-credentials-create"),
        });
      } catch {
        const disconnected = port;
        port = null;
        abortActive();
        try { disconnected?.disconnect(); } catch { /* Already disconnected. */ }
        scheduleReconnect();
      }
    } else if (message.kind === "cancel" && active.delete(message.id)) {
      try {
        port?.postMessage({ kind: "cancel", id: message.id });
      } catch {
        // Cancellation already revoked response delivery. A concurrently disconnected port needs
        // no further action and must not surface an exception in the isolated world.
      }
    }
  });

  window.addEventListener("pagehide", () => {
    suspended = true;
    if (reconnectTimer !== null) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
    const disconnected = port;
    port = null;
    for (const id of [...active]) {
      try { disconnected?.postMessage({ kind: "cancel", id }); } catch { /* Best effort. */ }
    }
    abortActive();
    try { disconnected?.disconnect(); } catch { /* Already disconnected. */ }
  });

  window.addEventListener("pageshow", () => {
    suspended = false;
    reconnectAttempts = 0;
    ensurePort();
    postToMain({ kind: "isolated.ready", channelId });
  });

  ensurePort();

  // Announce this frame's channel so a MAIN world that loaded first re-offers its bootstrap.
  postToMain({ kind: "isolated.ready", channelId });
})();
