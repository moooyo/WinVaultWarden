import test from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFileSync } from "node:fs";
import { webcrypto } from "node:crypto";

// Task 7 cancellation contract across the three extension surfaces:
//   page-webauthn.js (MAIN)   — AbortSignal, late-response discard, deadline -> NotAllowedError
//   content-webauthn.js (ISO) — pagehide/navigation cancels every active id exactly once
//   native-port.js            — disconnect maps to operation_aborted (ready) / client_not_handled (pre-ready)

function loadClassic(sandbox, ...relPaths) {
  const context = vm.createContext(sandbox);
  for (const rel of relPaths) {
    const code = readFileSync(new URL(`../src/${rel}`, import.meta.url), "utf8");
    vm.runInContext(code, context, { filename: rel });
  }
  return context;
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 5));

function makeWindow(origin) {
  const messageListeners = [];
  const named = new Map();
  const win = {
    location: { origin },
    addEventListener(type, fn) {
      if (type === "message") {
        messageListeners.push(fn);
        return;
      }
      const list = named.get(type) ?? [];
      list.push(fn);
      named.set(type, list);
    },
    removeEventListener(type, fn) {
      const list = type === "message" ? messageListeners : named.get(type);
      const i = list ? list.indexOf(fn) : -1;
      if (i >= 0) list.splice(i, 1);
    },
    postMessage(data) {
      const event = { data, source: win, origin };
      for (const fn of messageListeners.slice()) fn(event);
    },
    dispatch(type) {
      for (const fn of (named.get(type) ?? []).slice()) fn({ type });
    },
  };
  win.self = win;
  win.top = win;
  return win;
}

// ── page-webauthn (MAIN shim) ─────────────────────────────────────────────────

function publicKeyOptions(extra = {}) {
  return {
    publicKey: { challenge: new Uint8Array([1, 2, 3, 4]), rpId: "example.com", timeout: 60_000, allowCredentials: [], ...extra },
  };
}

function createPageHarness() {
  const origin = "https://example.com";
  const win = makeWindow(origin);
  const counts = { get: 0, create: 0 };
  const navigator = {
    credentials: {
      get: async () => { counts.get += 1; return { __native: "get" }; },
      create: async () => { counts.create += 1; return { __native: "create" }; },
    },
  };
  const sandbox = { window: win, navigator, crypto: webcrypto, DOMException, structuredClone, btoa, atob, console, setTimeout, clearTimeout, queueMicrotask };
  loadClassic(sandbox, "error-map.js", "request-coordinator.js", "page-webauthn.js");

  const channelId = "c".repeat(64);
  const posted = [];
  let lastRequestId = null;
  win.addEventListener("message", (event) => {
    const message = event.data;
    if (event.source !== win || message?.source !== "winvaultwarden-main") return;
    if (message.kind === "main.ready") {
      win.postMessage({ source: "winvaultwarden-isolated", kind: "isolated.ready", bootstrapId: message.bootstrapId, channelId });
      return;
    }
    if (message.kind === "request" || message.kind === "cancel") {
      posted.push(message);
      if (message.kind === "request") lastRequestId = message.id;
    }
  });
  win.postMessage({ source: "winvaultwarden-isolated", kind: "isolated.ready", channelId });

  return {
    posted,
    get nativeGetCalls() { return counts.get; },
    get credentials() { return navigator.credentials; },
    respond(code, { payload = null } = {}) {
      const id = lastRequestId;
      const response = payload
        ? { v: 1, id, type: "ok", ok: true, payload }
        : { v: 1, id, type: "error", ok: false, error: { code, message: "SENTINEL_INTERNAL" } };
      win.postMessage({ source: "winvaultwarden-isolated", channelId, id, kind: "response", response });
    },
  };
}

test("already-aborted signal sends no request and throws AbortError", async () => {
  const harness = createPageHarness();
  const controller = new AbortController();
  controller.abort();
  const op = harness.credentials.get({ ...publicKeyOptions(), signal: controller.signal });
  await assert.rejects(op, { name: "AbortError" });
  assert.equal(harness.posted.filter((m) => m.kind === "request").length, 0);
  assert.equal(harness.nativeGetCalls, 0);
});

test("an active abort posts exactly one cancel", async () => {
  const harness = createPageHarness();
  const controller = new AbortController();
  const op = harness.credentials.get({ ...publicKeyOptions(), signal: controller.signal });
  controller.abort();
  await assert.rejects(op, { name: "AbortError" });
  assert.equal(harness.posted.filter((m) => m.kind === "cancel").length, 1);
});

test("a late success after abort is discarded", async () => {
  const harness = createPageHarness();
  const controller = new AbortController();
  const op = harness.credentials.get({ ...publicKeyOptions(), signal: controller.signal });
  controller.abort();
  await assert.rejects(op, { name: "AbortError" });
  // A success frame arrives late for the same id: it must be dropped, never resolve, never fall back.
  harness.respond(null, { payload: { credentialId: "AQID", authenticatorData: "AA", clientDataJSON: "BB", signature: "CC", userHandle: null } });
  await flush();
  assert.equal(harness.nativeGetCalls, 0);
});

test("a deadline_exceeded response rejects with NotAllowedError and no fallback", async () => {
  const harness = createPageHarness();
  const op = harness.credentials.get(publicKeyOptions());
  harness.respond("deadline_exceeded");
  await assert.rejects(op, { name: "NotAllowedError" });
  assert.equal(harness.nativeGetCalls, 0);
});

test("client_not_handled falls back to the platform authenticator exactly once", async () => {
  const harness = createPageHarness();
  const op = harness.credentials.get(publicKeyOptions());
  harness.respond("client_not_handled");
  await op;
  assert.equal(harness.nativeGetCalls, 1);
});

// ── content-webauthn (ISOLATED relay) ─────────────────────────────────────────

function createRelayHarness() {
  const win = makeWindow("https://example.com");
  const portPosts = [];
  let portDisconnected = false;
  const portListeners = { message: [], disconnect: [] };
  const chrome = {
    runtime: {
      id: "ext-id",
      lastError: null,
      connect: () => ({
        postMessage: (m) => portPosts.push(m),
        disconnect: () => { portDisconnected = true; },
        onMessage: { addListener: (fn) => portListeners.message.push(fn) },
        onDisconnect: { addListener: (fn) => portListeners.disconnect.push(fn) },
      }),
    },
  };
  const document = { permissionsPolicy: { allowsFeature: () => true } };
  const sandbox = { window: win, chrome, document, structuredClone, crypto: webcrypto, console };
  loadClassic(sandbox, "request-coordinator.js", "content-webauthn.js");

  let channelId = null;
  win.addEventListener("message", (event) => {
    const message = event.data;
    if (message?.source === "winvaultwarden-isolated" && message.kind === "isolated.ready") {
      channelId = message.channelId;
    }
  });
  // Provoke the relay to (re)announce its channel so we learn the channelId.
  win.postMessage({ source: "winvaultwarden-main", kind: "main.ready", bootstrapId: "boot" });

  return {
    get channelId() { return channelId; },
    get portPosts() { return portPosts; },
    get portDisconnected() { return portDisconnected; },
    sendRequest(id) {
      win.postMessage({ source: "winvaultwarden-main", channelId, id, kind: "request", request: { v: 1, id, type: "passkey.get", payload: {} } });
    },
    pagehide() { win.dispatch("pagehide"); },
  };
}

test("pagehide cancels every active id exactly once and disconnects", async () => {
  const harness = createRelayHarness();
  assert.equal(typeof harness.channelId, "string");
  harness.sendRequest("id-a");
  harness.sendRequest("id-b");

  harness.pagehide();

  const cancels = harness.portPosts.filter((m) => m.kind === "cancel").map((m) => m.id).sort();
  assert.deepEqual(cancels, ["id-a", "id-b"]);
  assert.equal(harness.portDisconnected, true);
});

// ── native-port ───────────────────────────────────────────────────────────────

let nativePortVersion = 0;

function makeNativeEnv() {
  const posts = [];
  const listeners = { message: [], disconnect: [] };
  const port = {
    postMessage: (m) => posts.push(m),
    onMessage: { addListener: (fn) => listeners.message.push(fn) },
    onDisconnect: { addListener: (fn) => listeners.disconnect.push(fn) },
  };
  return {
    posts,
    chrome: { runtime: { id: "ext-id", lastError: null, connectNative: () => port } },
    emitMessage: (m) => listeners.message.slice().forEach((fn) => fn(m)),
    emitDisconnect: () => listeners.disconnect.slice().forEach((fn) => fn()),
  };
}

async function loadNativePort(env) {
  globalThis.chrome = env.chrome;
  return import(`../src/native-port.js?v=${nativePortVersion++}`);
}

test("disconnect after ready completes in-flight requests as operation_aborted", async () => {
  const env = makeNativeEnv();
  const np = await loadNativePort(env);
  const pending = np.sendNative({ v: 1, id: "req-ready", type: "passkey.get" });

  const ping = env.posts.find((m) => m.type === "ping");
  env.emitMessage({ v: 1, id: ping.id, type: "pong" });
  await flush(); // let readyPromise resolve and the request post

  env.emitDisconnect();
  const response = await pending;
  assert.equal(response.error.code, "operation_aborted");
});

test("disconnect before the first pong collapses to client_not_handled", async () => {
  const env = makeNativeEnv();
  const np = await loadNativePort(env);
  const pending = np.sendNative({ v: 1, id: "req-preready", type: "passkey.get" });
  await flush();

  env.emitDisconnect(); // host never came up
  const response = await pending;
  assert.equal(response.error.code, "client_not_handled");
});
