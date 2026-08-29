// Service-worker background. Each frame owns page-local request ids, while native-port owns its
// independent wire ids. This layer keeps only capability handles, rebuilds trusted context from the
// runtime port sender, and rebinds native responses to the originating page id. Per-frame and global
// admission limits fail fast before another ceremony can be queued.
import { buildTrustedContext } from "./trusted-context.js";
import { startNativeRequest } from "./native-port.js";

const FRAME_PORT_NAME = "winvaultwarden-passkey-frame-v1";
const MAX_ACTIVE_PER_FRAME = 1;
const MAX_ACTIVE_GLOBAL = 8;

let activeGlobalCount = 0;

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== FRAME_PORT_NAME) {
    try {
      port.disconnect();
    } catch {
      // Already gone.
    }
    return;
  }

  const activeOperations = new Map();

  port.onMessage.addListener((message) => {
    if (message?.kind === "request") {
      void handleRequest(port, activeOperations, message);
    } else if (message?.kind === "cancel" && typeof message.id === "string") {
      const operation = activeOperations.get(message.id);
      if (operation && releaseOperation(activeOperations, message.id, operation)) {
        operation.cancel();
      }
    }
  });

  port.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    for (const [id, operation] of [...activeOperations]) {
      if (releaseOperation(activeOperations, id, operation)) {
        operation.cancel();
      }
    }
  });
});

async function handleRequest(port, activeOperations, message) {
  const pageId = message?.id;
  const source = message?.request;
  if (typeof pageId !== "string" || !source || typeof source !== "object") {
    return;
  }

  // The envelope id is the sole frame-local correlation key. A different nested id must never be
  // allowed to cancel one request while delivering another request's response.
  if (source.id !== pageId) {
    postResponse(port, pageId, failure(pageId, "security_sender"));
    return;
  }

  // A duplicate page id cannot receive a second failure response because that response could settle
  // the already-running operation. Ignore it and preserve ownership of the original capability.
  if (activeOperations.has(pageId)) {
    return;
  }

  let context;
  try {
    context = buildTrustedContext(port.sender, chrome.runtime.id, message.publicKeyCredentialsGetAllowed);
  } catch {
    postResponse(port, pageId, failure(pageId, "security_sender"));
    return;
  }

  if (activeOperations.size >= MAX_ACTIVE_PER_FRAME) {
    postResponse(port, pageId, failure(pageId, "request_in_progress"));
    return;
  }
  if (activeGlobalCount >= MAX_ACTIVE_GLOBAL) {
    postResponse(port, pageId, failure(pageId, "rate_limited"));
    return;
  }

  const sourceWithoutId = { ...source };
  delete sourceWithoutId.id;
  const request = { ...sourceWithoutId, v: 1, context };
  // Discard any page-supplied top-level origin/crossOrigin; the trusted context is authoritative.
  delete request.origin;
  delete request.crossOrigin;
  if (request.payload && typeof request.payload === "object") {
    request.payload = {
      ...request.payload,
      origin: context.frameOrigin,
      crossOrigin: context.frameOrigin !== context.topOrigin,
    };
    delete request.payload.context;
  }

  const operation = startNativeRequest(request);
  activeOperations.set(pageId, operation);
  activeGlobalCount += 1;

  try {
    const response = await operation.result;
    // Cancellation, frame disconnect, or page-id reuse revokes delivery authority even if a native
    // response was already queued as a microtask.
    if (activeOperations.get(pageId) === operation) {
      postResponse(port, pageId, bindPageId(response, pageId));
    }
  } finally {
    releaseOperation(activeOperations, pageId, operation);
  }
}

function releaseOperation(activeOperations, pageId, operation) {
  if (activeOperations.get(pageId) !== operation) {
    return false;
  }
  activeOperations.delete(pageId);
  activeGlobalCount = Math.max(0, activeGlobalCount - 1);
  return true;
}

function bindPageId(response, pageId) {
  if (!response || typeof response !== "object") {
    return failure(pageId, "internal_error");
  }
  return { ...response, v: 1, id: pageId };
}

function postResponse(port, id, response) {
  try {
    port.postMessage({ id, response });
  } catch {
    // The frame is gone; there is nothing left to deliver.
  }
}

function failure(id, code) {
  return { v: 1, id, type: "error", ok: false, error: { code } };
}
