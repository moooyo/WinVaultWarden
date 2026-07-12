// ISOLATED-world relay. It runs in the extension's private world (manifest `world: ISOLATED`), so
// its variables are unreachable from the page, but the window.postMessage medium it shares with the
// MAIN shim is not — hence it treats the channel purely as correlation and lets the browser rebuild
// the trusted context. It keeps a single long-lived runtime port to the service worker for the life
// of the frame (never a one-shot message, never a dynamically injected script element). It reads
// the frame's Permissions Policy verdict directly from the real document so a sub-frame can only get
// a passkey when the top document delegated `publickey-credentials-get`. Page-supplied
// origin/crossOrigin/context are stripped before anything leaves the frame; disconnect reasons and
// chrome.runtime.lastError never reach the page.
(function installWinVaultWardenIsolatedRelay() {
  const MAIN_SOURCE = "winvaultwarden-main";
  const ISOLATED_SOURCE = "winvaultwarden-isolated";
  const PORT_NAME = "winvaultwarden-passkey-frame-v1";
  const coordinator = globalThis.WinVaultWardenRequestCoordinator;

  if (!coordinator || window.__winVaultWardenIsolatedRelay) {
    return;
  }
  Object.defineProperty(window, "__winVaultWardenIsolatedRelay", { value: true });

  const channelId = coordinator.randomChannelId();
  const active = new Set();
  const port = chrome.runtime.connect({ name: PORT_NAME });

  function targetOrigin() {
    const origin = window.location.origin;
    return origin && origin !== "null" ? origin : "*";
  }

  function postToMain(message) {
    window.postMessage({ source: ISOLATED_SOURCE, ...message }, targetOrigin());
  }

  function publicKeyCredentialsGetAllowed() {
    if (window.top === window) {
      return true;
    }
    const policy = document.permissionsPolicy ?? document.featurePolicy;
    return Boolean(policy?.allowsFeature?.("publickey-credentials-get"));
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
      active.add(message.id);
      const request = structuredClone(message.request) ?? {};
      delete request.origin;
      delete request.crossOrigin;
      delete request.context;
      if (request.payload && typeof request.payload === "object") {
        delete request.payload.origin;
        delete request.payload.crossOrigin;
        delete request.payload.context;
      }
      port.postMessage({
        kind: "request",
        request,
        publicKeyCredentialsGetAllowed: publicKeyCredentialsGetAllowed(),
      });
    } else if (message.kind === "cancel" && active.delete(message.id)) {
      port.postMessage({ kind: "cancel", id: message.id });
    }
  });

  port.onMessage.addListener((message) => {
    if (!message || typeof message.id !== "string") {
      return;
    }
    active.delete(message.id);
    postToMain({ channelId, id: message.id, kind: "response", response: message.response });
  });

  port.onDisconnect.addListener(() => {
    // Acknowledge but never surface the disconnect reason: chrome.runtime.lastError.message must not
    // reach the page. Every request still in flight ends as a code-only operation_aborted.
    void chrome.runtime.lastError;
    for (const id of active) {
      postToMain({
        channelId,
        id,
        kind: "response",
        response: { v: 1, id, type: "error", ok: false, error: { code: "operation_aborted" } },
      });
    }
    active.clear();
  });

  window.addEventListener(
    "pagehide",
    () => {
      for (const id of active) {
        port.postMessage({ kind: "cancel", id });
      }
      active.clear();
      port.disconnect();
    },
    { once: true },
  );

  // Announce this frame's channel so a MAIN world that loaded first re-offers its bootstrap.
  postToMain({ kind: "isolated.ready", channelId });
})();
