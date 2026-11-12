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
const frameConnections = new Map();

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
  const frameState = { disconnected: false };
  const frameConnection = registerFrame(port, activeOperations, frameState);

  port.onMessage.addListener((message) => {
    if (message?.kind === "request") {
      void handleRequest(port, activeOperations, frameState, message);
    } else if (message?.kind === "cancel" && typeof message.id === "string") {
      const operation = activeOperations.get(message.id);
      if (operation && releaseOperation(activeOperations, message.id, operation)) {
        operation.cancel();
      }
    }
  });

  port.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    revokeFrame(frameConnection);
    frameState.disconnected = true;
    cancelOperations(activeOperations);
  });
  try { port.postMessage({ kind: "frame.ready" }); } catch { /* The document already left. */ }
});

async function handleRequest(port, activeOperations, frameState, message) {
  const pageId = message?.id;
  const source = message?.request;
  if (frameState.disconnected || typeof pageId !== "string" || !source || typeof source !== "object") {
    return;
  }

  // The envelope id is the sole frame-local correlation key. A different nested id must never be
  // allowed to cancel one request while delivering another request's response.
  if (
    source.id !== pageId || !/^[0-9a-f]{32}$/u.test(pageId) || source.v !== 1 ||
    !["passkey.get", "passkey.create"].includes(source.type) ||
    !Number.isSafeInteger(source.deadlineUnixMs) || source.deadlineUnixMs <= 0 ||
    !source.payload || typeof source.payload !== "object" || Array.isArray(source.payload)
  ) {
    postResponse(port, pageId, failure(pageId, "security_sender"));
    return;
  }

  // A duplicate page id cannot receive a second failure response because that response could settle
  // the already-running operation. Ignore it and preserve ownership of the original capability.
  if (activeOperations.has(pageId)) {
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

  // Reserve admission before querying the browser. Cancel/disconnect can revoke this reservation
  // while getAllFrames is pending, and concurrent requests cannot bypass the admission limits.
  const operation = {
    native: null,
    cancel() { this.native?.cancel(); },
  };
  activeOperations.set(pageId, operation);
  activeGlobalCount += 1;

  try {
    let context;
    try {
      const frames = await readCurrentFrames(port.sender?.tab?.id, source.deadlineUnixMs);
      const currentConnections = frameConnections.get(port.sender?.tab?.id);
      const trustedOrigins = new Map(
        [...(currentConnections ?? [])].map(([frameId, connection]) => [frameId, connection.sender]),
      );
      context = buildTrustedContext(
        port.sender,
        chrome.runtime.id,
        message.publicKeyCredentialsGetAllowed,
        frames,
        message.publicKeyCredentialsCreateAllowed,
        trustedOrigins,
      );
      const frameById = new Map(frames.map((frame) => [frame.frameId, frame]));
      operation.ancestors = new Set();
      for (let frame = frameById.get(context.frameId); frame; frame = frameById.get(frame.parentFrameId)) {
        operation.ancestors.add(currentConnections.get(frame.frameId));
        if (frame.frameId === 0) break;
      }
    } catch {
      if (activeOperations.get(pageId) === operation) {
        postResponse(port, pageId, failure(pageId, "security_sender"));
      }
      return;
    }
    if (frameState.disconnected || activeOperations.get(pageId) !== operation) return;

    const request = {
      v: 1,
      type: source.type,
      deadlineUnixMs: source.deadlineUnixMs,
      context,
      payload: selectPayload(source.type, source.payload),
    };
    // Every security field is rebuilt; page data cannot select a frame, ancestor or document.
    delete request.origin;
    delete request.crossOrigin;
    delete request.topOrigin;
    delete request.sameOriginWithAncestors;
    if (request.payload && typeof request.payload === "object") {
      request.payload = {
        ...request.payload,
        origin: context.frameOrigin,
        crossOrigin: !context.sameOriginWithAncestors,
        topOrigin: context.topOrigin,
      };
      delete request.payload.context;
      delete request.payload.frameOrigin;
      delete request.payload.sameOriginWithAncestors;
    }

    operation.native = startNativeRequest(request);
    const response = await operation.native.result;
    // Cancellation, frame disconnect, or page-id reuse revokes delivery authority even if a native
    // response was already queued as a microtask.
    if (activeOperations.get(pageId) === operation) {
      postResponse(port, pageId, bindPageId(response, pageId));
    }
  } catch {
    if (activeOperations.get(pageId) === operation) {
      postResponse(port, pageId, failure(pageId, "internal_error"));
    }
  } finally {
    releaseOperation(activeOperations, pageId, operation);
  }
}

function selectPayload(type, source) {
  const keys = type === "passkey.get"
    ? ["rpId", "challenge", "allowCredentials", "userVerification", "mediation", "timeout"]
    : ["rpId", "challenge", "rp", "user", "pubKeyCredParams", "excludeCredentials", "authenticatorSelection", "extensions", "attestation", "timeout"];
  return Object.fromEntries(keys.filter((key) => Object.hasOwn(source, key)).map((key) => [key, source[key]]));
}

function registerFrame(port, operations, state) {
  const sender = port.sender;
  if (
    sender?.id !== chrome.runtime.id || !Number.isInteger(sender?.tab?.id) || sender.tab.id < 0 ||
    !Number.isInteger(sender.frameId) || sender.frameId < 0 ||
    typeof sender.documentId !== "string" || sender.documentId.length === 0
  ) return null;

  let tab = frameConnections.get(sender.tab.id);
  if (!tab) {
    tab = new Map();
    frameConnections.set(sender.tab.id, tab);
  }
  const previous = tab.get(sender.frameId);
  if (previous) {
    revokeFrame(previous);
    try { previous.port.disconnect(); } catch { /* Already disconnected. */ }
    // revokeFrame may have removed the tab when the previous connection was its last entry.
    frameConnections.set(sender.tab.id, tab);
  }
  const current = { sender, port, operations, state };
  tab.set(sender.frameId, current);
  return current;
}

function revokeFrame(connection) {
  if (!connection) return;
  connection.state.disconnected = true;
  const tab = frameConnections.get(connection.sender.tab.id);
  if (tab?.get(connection.sender.frameId) === connection) {
    tab.delete(connection.sender.frameId);
    if (tab.size === 0) frameConnections.delete(connection.sender.tab.id);
  }
  cancelOperations(connection.operations);
  for (const sibling of tab?.values() ?? []) {
    for (const [id, operation] of [...sibling.operations]) {
      if (operation.ancestors?.has(connection) && releaseOperation(sibling.operations, id, operation)) {
        operation.cancel();
        postResponse(sibling.port, id, failure(id, "operation_aborted"));
      }
    }
  }
}

function cancelOperations(operations) {
  for (const [id, operation] of [...operations]) {
    if (releaseOperation(operations, id, operation)) operation.cancel();
  }
}

async function readCurrentFrames(tabId, deadlineUnixMs) {
  if (!Number.isInteger(tabId) || tabId < 0 || !chrome.webNavigation?.getAllFrames) {
    throw new Error("security_sender");
  }
  const remaining = Number(deadlineUnixMs) - Date.now();
  if (!Number.isFinite(remaining) || remaining <= 0) throw new Error("security_sender");
  let timer;
  try {
    return await Promise.race([
      chrome.webNavigation.getAllFrames({ tabId }),
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error("security_sender")), Math.min(remaining, 3000));
      }),
    ]);
  } finally {
    clearTimeout(timer);
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
