import test from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFileSync } from "node:fs";
import { webcrypto } from "node:crypto";

// Loads the classic (non-module) content scripts into an isolated realm that fakes just
// enough of window/navigator/crypto for the MAIN-world WebAuthn shim. No DOM library.
function loadClassic(sandbox, ...relPaths) {
  const context = vm.createContext(sandbox);
  for (const rel of relPaths) {
    const code = readFileSync(new URL(`../src/${rel}`, import.meta.url), "utf8");
    vm.runInContext(code, context, { filename: rel });
  }
  return context;
}

function randomHex(byteLength) {
  const bytes = new Uint8Array(byteLength);
  webcrypto.getRandomValues(bytes);
  return Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
}

function makeWindow(origin) {
  const messageListeners = [];
  const namedListeners = new Map();
  const win = {
    location: { origin },
    addEventListener(type, fn) {
      if (type === "message") {
        messageListeners.push(fn);
        return;
      }
      const list = namedListeners.get(type) ?? [];
      list.push(fn);
      namedListeners.set(type, list);
    },
    removeEventListener(type, fn) {
      const list = type === "message" ? messageListeners : namedListeners.get(type);
      if (!list) return;
      const index = list.indexOf(fn);
      if (index >= 0) list.splice(index, 1);
    },
    postMessage(data) {
      const event = { data, source: win, origin };
      for (const fn of messageListeners.slice()) fn(event);
    },
    dispatch(type) {
      for (const fn of (namedListeners.get(type) ?? []).slice()) fn({ type });
    },
  };
  win.self = win;
  win.top = win;
  return win;
}

function publicKeyOptions(extra = {}) {
  return {
    publicKey: {
      challenge: new Uint8Array([1, 2, 3, 4]),
      rpId: "example.com",
      timeout: 60_000,
      allowCredentials: [],
      ...extra,
    },
  };
}

function createPageHarness({ answerHandshake = true } = {}) {
  const origin = "https://example.com";
  const win = makeWindow(origin);
  const counts = { get: 0, create: 0 };
  const navigator = {
    credentials: {
      get: async () => {
        counts.get += 1;
        return { __native: "get" };
      },
      create: async () => {
        counts.create += 1;
        return { __native: "create" };
      },
    },
  };

  const sandbox = {
    window: win,
    navigator,
    crypto: webcrypto,
    DOMException,
    structuredClone,
    btoa,
    atob,
    console,
    setTimeout,
    clearTimeout,
    queueMicrotask,
  };
  loadClassic(sandbox, "error-map.js", "request-coordinator.js", "page-webauthn.js");

  const channelId = randomHex(32);
  const posted = [];
  let lastRequestId = null;
  let lastBootstrapId = null;

  // Stand in for the ISOLATED relay: answer the MAIN handshake and capture its traffic.
  win.addEventListener("message", (event) => {
    const message = event.data;
    if (event.source !== win || message?.source !== "winvaultwarden-main") return;

    if (message.kind === "main.ready") {
      lastBootstrapId = message.bootstrapId;
      if (!answerHandshake) return;
      win.postMessage({
        source: "winvaultwarden-isolated",
        kind: "isolated.ready",
        bootstrapId: message.bootstrapId,
        channelId,
      });
      return;
    }
    if (message.kind === "request" || message.kind === "cancel") {
      posted.push(message);
      if (message.kind === "request") lastRequestId = message.id;
    }
  });

  // Announce the isolated world; the shim answers with a bootstrap-bound channel.
  win.postMessage({ source: "winvaultwarden-isolated", kind: "isolated.ready", channelId });

  return {
    channelId,
    posted,
    get nativeGetCalls() {
      return counts.get;
    },
    get nativeCreateCalls() {
      return counts.create;
    },
    get credentials() {
      return navigator.credentials;
    },
    completeHandshake() {
      win.postMessage({
        source: "winvaultwarden-isolated",
        kind: "isolated.ready",
        bootstrapId: lastBootstrapId,
        channelId,
      });
    },
    respond(code, { payload = null } = {}) {
      const id = lastRequestId;
      const response = payload
        ? { v: 1, id, type: "ok", ok: true, payload }
        : { v: 1, id, type: "error", ok: false, error: { code } };
      win.postMessage({
        source: "winvaultwarden-isolated",
        channelId,
        id,
        kind: "response",
        response,
      });
    },
  };
}

test("conditional and silent call native WebAuthn without posting", async () => {
  for (const mediation of ["conditional", "silent"]) {
    const harness = createPageHarness();
    await harness.credentials.get({
      mediation,
      publicKey: { challenge: new Uint8Array([1]), rpId: "example.com" },
    });
    assert.equal(harness.nativeGetCalls, 1);
    assert.equal(harness.posted.length, 0);
  }
});

test("a normal get posts exactly one v1 request with a 16-byte id and no page origin", async () => {
  const harness = createPageHarness();
  const pending = harness.credentials.get(publicKeyOptions());
  assert.equal(harness.posted.length, 1);

  const [message] = harness.posted;
  assert.equal(message.kind, "request");
  assert.match(message.id, /^[0-9a-f]{32}$/u);
  assert.equal(message.channelId, harness.channelId);

  const { request } = message;
  assert.equal(request.v, 1);
  assert.equal(request.id, message.id);
  assert.equal(request.type, "passkey.get");
  assert.equal(typeof request.deadlineUnixMs, "number");
  // The page never dictates origin/crossOrigin/frame/context; those are the browser's job.
  assert.equal(request.origin, undefined);
  assert.equal(request.context, undefined);
  assert.equal(request.payload.origin, undefined);
  assert.equal(request.payload.crossOrigin, undefined);
  assert.equal(request.payload.context, undefined);

  harness.respond("client_not_handled");
  await pending;
});

test("only client_not_handled falls back", async () => {
  const fallback = createPageHarness();
  const pending = fallback.credentials.get(publicKeyOptions());
  fallback.respond("client_not_handled");
  await pending;
  assert.equal(fallback.nativeGetCalls, 1);

  for (const code of ["user_cancelled", "security_invalid_rp", "operation_aborted"]) {
    const harness = createPageHarness();
    const operation = harness.credentials.get(publicKeyOptions());
    harness.respond(code);
    await assert.rejects(operation, (error) => error instanceof DOMException);
    assert.equal(harness.nativeGetCalls, 0);
  }
});

test("a request falls back when the isolated bootstrap never becomes ready", async () => {
  const harness = createPageHarness({ answerHandshake: false });
  const operation = harness.credentials.get(publicKeyOptions({ timeout: 20 }));

  await operation;
  assert.equal(harness.nativeGetCalls, 1);
  assert.equal(harness.posted.filter((message) => message.kind === "request").length, 0);

  // A late ready frame must not retain or revive the expired bind waiter.
  harness.completeHandshake();
  await new Promise((resolve) => setTimeout(resolve, 5));
  assert.equal(harness.posted.filter((message) => message.kind === "request").length, 0);
  assert.equal(harness.nativeGetCalls, 1);
});

test("AbortSignal posts cancel and throws AbortError immediately", async () => {
  const harness = createPageHarness();
  const controller = new AbortController();
  const operation = harness.credentials.get({
    ...publicKeyOptions(),
    signal: controller.signal,
  });
  controller.abort();

  await assert.rejects(operation, { name: "AbortError" });
  assert.equal(harness.posted.at(-1).kind, "cancel");
  assert.equal(harness.nativeGetCalls, 0);
});

test("create is intercepted and only client_not_handled falls back to native", async () => {
  const harness = createPageHarness();
  const operation = harness.credentials.create({
    publicKey: {
      challenge: new Uint8Array([9, 9, 9]),
      rp: { id: "example.com", name: "Example" },
      user: { id: new Uint8Array([1]), name: "a@example.com", displayName: "A" },
      pubKeyCredParams: [{ type: "public-key", alg: -7 }],
    },
  });
  assert.equal(harness.posted.at(-1).request.type, "passkey.create");
  harness.respond("client_not_handled");
  await operation;
  assert.equal(harness.nativeCreateCalls, 1);
});

test("requests without publicKey pass straight through to native", async () => {
  const harness = createPageHarness();
  await harness.credentials.get({ password: {} });
  await harness.credentials.create({ password: {} });
  assert.equal(harness.nativeGetCalls, 1);
  assert.equal(harness.nativeCreateCalls, 1);
  assert.equal(harness.posted.length, 0);
});
