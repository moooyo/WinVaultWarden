// Lazy singleton Native Messaging port to the WinVaultWarden host. One persistent connectNative port
// serves every frame for the life of the service worker; it is opened on first use, sends a v1 `ping`
// and waits for the host `pong` before it forwards any passkey request. Disconnect semantics are the
// whole point of the persistence:
//   * disconnect BEFORE the first pong (host never came up) -> waiting requests collapse to
//     client_not_handled, which lets the MAIN shim fall back to the platform authenticator;
//   * disconnect AFTER ready (App/host died mid-ceremony) -> in-flight requests complete as
//     operation_aborted, a terminal AbortError the page must not retry silently.
// Unknown or late responses are dropped. The host disconnect reason (chrome.runtime.lastError) is
// never surfaced. If the worker restarts, the module state is fresh and a new port is opened.
const HOST_NAME = "com.winvaultwarden.browser";

let port = null;
let state = "idle"; // idle | connecting | ready | closed
let readyPromise = null;
let resolveReady = null;
let rejectReady = null;
let pingId = null;
const pending = new Map();

function randomId() {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
}

function failure(id, code) {
  return { v: 1, id, type: "error", ok: false, error: { code } };
}

function ensurePort() {
  if (port) {
    return;
  }

  state = "connecting";
  readyPromise = new Promise((resolve, reject) => {
    resolveReady = resolve;
    rejectReady = reject;
  });
  // Never let a pre-ready disconnect surface as an unhandled rejection; send() handles it explicitly.
  readyPromise.catch(() => {});

  try {
    port = chrome.runtime.connectNative(HOST_NAME);
  } catch {
    port = null;
    state = "closed";
    rejectReady();
    return;
  }

  port.onMessage.addListener(onMessage);
  port.onDisconnect.addListener(onDisconnect);

  pingId = randomId();
  try {
    port.postMessage({ v: 1, id: pingId, type: "ping" });
  } catch {
    // A synchronous post failure will be followed by onDisconnect; let it drive the state.
  }
}

function onMessage(message) {
  if (state === "connecting" && message?.type === "pong" && message.id === pingId) {
    state = "ready";
    resolveReady();
    return;
  }

  const id = message?.id;
  if (typeof id !== "string") {
    return;
  }
  const resolve = pending.get(id);
  if (!resolve) {
    // Unknown or late response: count-and-drop, never route it to a frame.
    return;
  }
  pending.delete(id);
  resolve(message);
}

function onDisconnect() {
  // Acknowledge but never expose the disconnect reason.
  void chrome.runtime.lastError;
  const wasReady = state === "ready";
  state = "closed";
  port = null;
  if (rejectReady) {
    rejectReady();
  }
  const code = wasReady ? "operation_aborted" : "client_not_handled";
  for (const [id, resolve] of pending) {
    resolve(failure(id, code));
  }
  pending.clear();
}

export function sendNative(request) {
  ensurePort();
  if (!port || state === "closed") {
    return Promise.resolve(failure(request.id, "client_not_handled"));
  }

  return readyPromise.then(
    () => {
      if (!port || state === "closed") {
        return failure(request.id, "client_not_handled");
      }
      return new Promise((resolve) => {
        pending.set(request.id, resolve);
        try {
          port.postMessage(request);
        } catch {
          pending.delete(request.id);
          resolve(failure(request.id, "operation_aborted"));
        }
      });
    },
    () => failure(request.id, "client_not_handled"),
  );
}

export function cancelNative(id) {
  if (!port || state === "closed") {
    return;
  }
  try {
    port.postMessage({ v: 1, id, type: "cancel" });
  } catch {
    // Best-effort: the host cancels the ceremony; a dead port needs no cancel.
  }
}
