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
  const connections = [];
  function connectNative() {
    const posts = [];
    const listeners = { message: [], disconnect: [] };
    const state = { disconnectCalled: false };
    const port = {
      postMessage: (m) => posts.push(m),
      disconnect: () => { state.disconnectCalled = true; },
      onMessage: { addListener: (fn) => listeners.message.push(fn) },
      onDisconnect: { addListener: (fn) => listeners.disconnect.push(fn) },
    };
    const handle = {
      posts,
      state,
      emitMessage: (m) => listeners.message.slice().forEach((fn) => fn(m)),
      emitDisconnect: () => listeners.disconnect.slice().forEach((fn) => fn()),
    };
    connections.push(handle);
    return port;
  }
  return {
    connections,
    chrome: { runtime: { id: "ext-id", lastError: null, connectNative } },
    latest: () => connections.at(-1),
  };
}

async function loadNativePort(env) {
  globalThis.chrome = env.chrome;
  return import(`../src/native-port.js?v=${nativePortVersion++}`);
}

test("disconnect after ready completes in-flight requests as operation_aborted", async () => {
  const env = makeNativeEnv();
  const np = await loadNativePort(env);
  const operation = np.startNativeRequest({ v: 1, id: "page-ready", type: "passkey.get" });

  const native = env.latest();
  const ping = native.posts.find((m) => m.type === "ping");
  native.emitMessage({ v: 1, id: ping.id, type: "pong" });
  await flush(); // Let the pong dispatch the queued request.

  native.emitDisconnect();
  const response = await operation.result;
  assert.equal(response.error.code, "operation_aborted");
});

test("disconnect before the first pong collapses to client_not_handled", async () => {
  const env = makeNativeEnv();
  const np = await loadNativePort(env);
  const operation = np.startNativeRequest({ v: 1, id: "page-preready", type: "passkey.get" });
  await flush();

  env.latest().emitDisconnect(); // host never came up
  const response = await operation.result;
  assert.equal(response.error.code, "client_not_handled");
});

test("a never-pong generation expires queued work as fallback and cannot revive it", async () => {
  const env = makeNativeEnv();
  const np = await loadNativePort(env);
  const operation = np.startNativeRequest({
    v: 1,
    id: "page-never-pong",
    type: "passkey.get",
    deadlineUnixMs: Date.now() + 20,
  });
  const staleNative = env.latest();
  const stalePing = staleNative.posts.find((m) => m.type === "ping");

  const response = await operation.result;
  assert.equal(response.error.code, "client_not_handled");
  assert.equal(staleNative.posts.filter((m) => m.type === "passkey.get").length, 0);
  assert.equal(staleNative.state.disconnectCalled, true);

  staleNative.emitMessage({ v: 1, id: stalePing.id, type: "pong" });
  await flush();
  assert.equal(staleNative.posts.filter((m) => m.type === "passkey.get").length, 0);

  const replacement = np.startNativeRequest({
    v: 1,
    id: "page-after-never-pong",
    type: "passkey.get",
    deadlineUnixMs: Date.now() + 1_000,
  });
  const currentNative = env.latest();
  assert.notEqual(currentNative, staleNative);
  const currentPing = currentNative.posts.find((m) => m.type === "ping");
  currentNative.emitMessage({ v: 1, id: currentPing.id, type: "pong" });
  await flush();
  assert.equal(currentNative.posts.filter((m) => m.type === "passkey.get").length, 1);
  replacement.cancel();
  await replacement.result;
});

test("a deadline after native dispatch cancels and terminates without fallback", async () => {
  const env = makeNativeEnv();
  const np = await loadNativePort(env);
  const operation = np.startNativeRequest({
    v: 1,
    id: "page-sent-deadline",
    type: "passkey.get",
    deadlineUnixMs: Date.now() + 25,
  });
  const native = env.latest();
  const ping = native.posts.find((m) => m.type === "ping");
  native.emitMessage({ v: 1, id: ping.id, type: "pong" });
  await flush();
  const request = native.posts.find((m) => m.type === "passkey.get");
  assert.ok(request);

  const response = await operation.result;
  assert.equal(response.error.code, "operation_aborted");
  const cancels = native.posts.filter((m) => m.type === "cancel" && m.id === request.id);
  assert.equal(cancels.length, 1);

  native.emitMessage({ v: 1, id: request.id, type: "ok", ok: true, payload: { late: true } });
  await flush();
  assert.equal(operation.cancel(), false);
  assert.equal(native.posts.filter((m) => m.type === "cancel" && m.id === request.id).length, 1);
});

test("cancel before pong settles locally and the queued request is never sent", async () => {
  const env = makeNativeEnv();
  const np = await loadNativePort(env);
  const operation = np.startNativeRequest({ v: 1, id: "page-queued", type: "passkey.get" });
  const native = env.latest();

  assert.equal(operation.cancel(), true);
  assert.equal(operation.cancel(), false);
  assert.equal((await operation.result).error.code, "operation_aborted");

  const ping = native.posts.find((m) => m.type === "ping");
  native.emitMessage({ v: 1, id: ping.id, type: "pong" });
  await flush();
  assert.equal(native.posts.filter((m) => m.type === "passkey.get").length, 0);
  assert.equal(native.posts.filter((m) => m.type === "cancel").length, 0);
});

test("cancel after send settles immediately and emits at most one wire cancel", async () => {
  const env = makeNativeEnv();
  const np = await loadNativePort(env);
  const operation = np.startNativeRequest({ v: 1, id: "page-sent", type: "passkey.get" });
  const native = env.latest();
  const ping = native.posts.find((m) => m.type === "ping");
  native.emitMessage({ v: 1, id: ping.id, type: "pong" });
  await flush();

  const request = native.posts.find((m) => m.type === "passkey.get");
  assert.match(request.id, /^[0-9a-f]{32}$/u);
  assert.notEqual(request.id, "page-sent");
  assert.equal(operation.cancel(), true);
  assert.equal(operation.cancel(), false);
  assert.equal((await operation.result).error.code, "operation_aborted");

  const cancels = native.posts.filter((m) => m.type === "cancel");
  assert.equal(cancels.length, 1);
  assert.equal(cancels[0].id, request.id);

  native.emitMessage({ v: 1, id: request.id, type: "ok", ok: true, payload: {} });
  await flush();
  assert.equal(native.posts.filter((m) => m.type === "cancel").length, 1);
});

test("an old connection generation cannot settle a request on the replacement connection", async () => {
  const env = makeNativeEnv();
  const np = await loadNativePort(env);

  const first = np.startNativeRequest({ v: 1, id: "page-first", type: "passkey.get" });
  const oldNative = env.latest();
  const oldPing = oldNative.posts.find((m) => m.type === "ping");
  oldNative.emitMessage({ v: 1, id: oldPing.id, type: "pong" });
  await flush();
  oldNative.emitDisconnect();
  assert.equal((await first.result).error.code, "operation_aborted");

  const second = np.startNativeRequest({ v: 1, id: "page-second", type: "passkey.get" });
  const newNative = env.latest();
  const newPing = newNative.posts.find((m) => m.type === "ping");
  newNative.emitMessage({ v: 1, id: newPing.id, type: "pong" });
  await flush();
  const secondRequest = newNative.posts.find((m) => m.type === "passkey.get");

  oldNative.emitMessage({ v: 1, id: secondRequest.id, type: "ok", ok: true, payload: { stale: true } });
  const race = await Promise.race([
    second.result.then(() => "settled"),
    flush().then(() => "pending"),
  ]);
  assert.equal(race, "pending");

  newNative.emitMessage({ v: 1, id: secondRequest.id, type: "ok", ok: true, payload: { stale: false } });
  const response = await second.result;
  assert.equal(response.payload.stale, false);
});
