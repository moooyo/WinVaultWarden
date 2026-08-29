// Lazy singleton Native Messaging connection to the WinVaultWarden host. Callers receive an
// operation capability rather than addressing global pending state by id. The module mints every
// native wire id, registers the queued operation before opening/waking the host, and binds it to one
// connection generation. This keeps page/frame ids outside the native protocol and makes late
// messages from a disconnected generation harmless.
const HOST_NAME = "com.winvaultwarden.browser";
const MAX_PENDING_NATIVE_REQUESTS = 8;
const DEFAULT_REQUEST_TIMEOUT_MS = 60_000;
const MAX_REQUEST_TIMEOUT_MS = 120_000;

let connection = null;
let nextConnectionGeneration = 0;
let nextWireSequence = 0n;
const wirePrefix = randomHex(8);
const pending = new Map();

function randomHex(byteLength) {
  const bytes = new Uint8Array(byteLength);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
}

function nextWireId() {
  nextWireSequence += 1n;
  if (nextWireSequence > 0xffffffffffffffffn) {
    throw new Error("Native request sequence exhausted");
  }
  return `${wirePrefix}${nextWireSequence.toString(16).padStart(16, "0")}`;
}

function failure(id, code) {
  return { v: 1, id, type: "error", ok: false, error: { code } };
}

function completedOperation(response) {
  return Object.freeze({
    result: Promise.resolve(response),
    cancel: () => false,
  });
}

function localDeadlineUnixMs(request) {
  const now = Date.now();
  const requested = Number(request?.deadlineUnixMs);
  if (!Number.isFinite(requested)) {
    return now + DEFAULT_REQUEST_TIMEOUT_MS;
  }
  return Math.min(requested, now + MAX_REQUEST_TIMEOUT_MS);
}

function ensureConnection() {
  if (connection && connection.state !== "closed") {
    return connection;
  }

  const current = {
    generation: ++nextConnectionGeneration,
    state: "connecting", // connecting | ready | closed
    port: null,
    pingId: randomHex(16),
  };
  connection = current;

  try {
    current.port = chrome.runtime.connectNative(HOST_NAME);
  } catch {
    closeConnection(current);
    return current;
  }

  current.port.onMessage.addListener((message) => onMessage(current, message));
  current.port.onDisconnect.addListener(() => onDisconnect(current));

  try {
    current.port.postMessage({ v: 1, id: current.pingId, type: "ping" });
  } catch {
    closeConnection(current);
  }

  return current;
}

function onMessage(source, message) {
  if (connection !== source || source.state === "closed") {
    return;
  }

  if (source.state === "connecting" && message?.type === "pong" && message.id === source.pingId) {
    source.state = "ready";
    for (const entry of pending.values()) {
      if (entry.connectionGeneration === source.generation && entry.state === "queued") {
        sendEntry(source, entry);
      }
    }
    return;
  }

  const id = message?.id;
  if (typeof id !== "string") {
    return;
  }

  const entry = pending.get(id);
  if (
    !entry ||
    entry.state !== "sent" ||
    entry.connectionGeneration !== source.generation
  ) {
    // Unknown, queued, cancelled, completed, or from another connection generation.
    return;
  }

  if (Date.now() >= entry.deadlineUnixMs) {
    expireEntry(entry);
    return;
  }

  settle(entry, message);
}

function onDisconnect(source) {
  // Acknowledge but never expose the browser-provided disconnect reason.
  void chrome.runtime.lastError;
  closeConnection(source);
}

function closeConnection(source) {
  if (source.state === "closed") {
    return;
  }

  const wasReady = source.state === "ready";
  source.state = "closed";
  if (connection === source) {
    connection = null;
  }

  const code = wasReady ? "operation_aborted" : "client_not_handled";
  for (const entry of [...pending.values()]) {
    if (entry.connectionGeneration === source.generation) {
      settle(entry, failure(entry.wireId, code));
    }
  }
}

function sendEntry(source, entry) {
  if (
    connection !== source ||
    source.state !== "ready" ||
    entry.state !== "queued" ||
    entry.connectionGeneration !== source.generation
  ) {
    return;
  }
  if (Date.now() >= entry.deadlineUnixMs) {
    expireEntry(entry);
    return;
  }

  // Set the state before postMessage so even a synchronous test double cannot race the response.
  entry.state = "sent";
  try {
    source.port.postMessage(entry.request);
  } catch {
    // A synchronous write failure means the generation is no longer usable. Abort all operations
    // bound to it and let a later request open a fresh native connection.
    closeConnection(source);
  }
}

function settle(entry, response) {
  if (entry.state === "cancelled" || entry.state === "settled") {
    return false;
  }

  entry.state = "settled";
  pending.delete(entry.wireId);
  clearDeadline(entry);
  entry.resolve(response);
  return true;
}

function clearDeadline(entry) {
  if (entry.deadlineTimer !== null) {
    clearTimeout(entry.deadlineTimer);
    entry.deadlineTimer = null;
  }
}

function retireConnectionIfUnused(source) {
  if (
    !source ||
    source.state === "closed" ||
    [...pending.values()].some((entry) => entry.connectionGeneration === source.generation)
  ) {
    return;
  }

  source.state = "closed";
  if (connection === source) {
    connection = null;
  }
  try {
    source.port?.disconnect?.();
  } catch {
    // The generation is already retired locally; a failed best-effort disconnect changes nothing.
  }
}

function expireEntry(entry) {
  if (entry.state === "queued") {
    const source = connection?.generation === entry.connectionGeneration ? connection : null;
    settle(entry, failure(entry.wireId, "client_not_handled"));
    retireConnectionIfUnused(source);
    return;
  }
  if (entry.state === "sent") {
    cancelEntry(entry);
  }
}

function cancelEntry(entry) {
  if (entry.state === "cancelled" || entry.state === "settled") {
    return false;
  }

  const shouldNotifyHost = entry.state === "sent";
  entry.state = "cancelled";
  pending.delete(entry.wireId);
  clearDeadline(entry);
  entry.resolve(failure(entry.wireId, "operation_aborted"));

  if (shouldNotifyHost && !entry.cancelSent) {
    entry.cancelSent = true;
    const source = connection;
    if (
      source &&
      source.state === "ready" &&
      source.generation === entry.connectionGeneration
    ) {
      try {
        source.port.postMessage({ v: 1, id: entry.wireId, type: "cancel" });
      } catch {
        closeConnection(source);
      }
    }
  }

  return true;
}

export function startNativeRequest(request) {
  const wireId = nextWireId();
  if (pending.size >= MAX_PENDING_NATIVE_REQUESTS) {
    return completedOperation(failure(wireId, "rate_limited"));
  }

  let resolveResult;
  const result = new Promise((resolve) => {
    resolveResult = resolve;
  });
  const entry = {
    wireId,
    request: { ...request, v: 1, id: wireId },
    state: "queued",
    connectionGeneration: null,
    cancelSent: false,
    deadlineUnixMs: localDeadlineUnixMs(request),
    deadlineTimer: null,
    resolve: resolveResult,
  };

  // Registration deliberately precedes connectNative/ping. A cancel may therefore claim and
  // settle the operation while the host is still starting, and the later pong cannot send it.
  pending.set(wireId, entry);
  const remaining = entry.deadlineUnixMs - Date.now();
  if (remaining <= 0) {
    expireEntry(entry);
    return Object.freeze({
      result,
      cancel: () => cancelEntry(entry),
    });
  }
  entry.deadlineTimer = setTimeout(() => expireEntry(entry), remaining);

  const source = ensureConnection();
  entry.connectionGeneration = source.generation;

  if (source.state === "ready") {
    sendEntry(source, entry);
  } else if (source.state === "closed") {
    settle(entry, failure(wireId, "client_not_handled"));
  }

  return Object.freeze({
    result,
    cancel: () => cancelEntry(entry),
  });
}
