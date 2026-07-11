import test from "node:test";
import assert from "node:assert/strict";

const RUNTIME_ID = "trusted-extension-id";
const FRAME_PORT_NAME = "winvaultwarden-passkey-frame-v1";

let onConnectListener = null;
const connectNativeCalls = [];
let nativePort = null;

function tick() {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

function makeNativePort() {
  const posted = [];
  const messageListeners = [];
  const disconnectListeners = [];
  const state = { disconnectCalled: false };
  function emitMessage(message) {
    for (const fn of messageListeners.slice()) fn(message);
  }
  const port = {
    postMessage(message) {
      posted.push(message);
      if (message.type === "ping") {
        queueMicrotask(() =>
          emitMessage({
            id: message.id,
            type: "pong",
            ok: true,
            payload: { name: "WinVaultWarden.NativeHost", version: "0.1.0" },
          }),
        );
      }
    },
    onMessage: { addListener: (fn) => messageListeners.push(fn) },
    onDisconnect: { addListener: (fn) => disconnectListeners.push(fn) },
    disconnect() {
      state.disconnectCalled = true;
    },
  };
  return {
    port,
    posted,
    state,
    emitMessage,
    emitDisconnect: () => {
      for (const fn of disconnectListeners.slice()) fn();
    },
    requests: () => posted.filter((message) => message.type !== "ping" && message.type !== "cancel"),
  };
}

globalThis.chrome = {
  runtime: {
    id: RUNTIME_ID,
    lastError: null,
    onConnect: { addListener: (fn) => (onConnectListener = fn) },
    connectNative(name) {
      connectNativeCalls.push(name);
      nativePort = makeNativePort();
      return nativePort.port;
    },
  },
};

function makeFramePort(sender, name = FRAME_PORT_NAME) {
  const posted = [];
  const messageListeners = [];
  const disconnectListeners = [];
  const state = { disconnectCalled: false };
  const port = {
    name,
    sender,
    postMessage(message) {
      posted.push(message);
    },
    onMessage: { addListener: (fn) => messageListeners.push(fn) },
    onDisconnect: { addListener: (fn) => disconnectListeners.push(fn) },
    disconnect() {
      state.disconnectCalled = true;
    },
  };
  return {
    port,
    posted,
    state,
    emitMessage: (message) => {
      for (const fn of messageListeners.slice()) fn(message);
    },
    emitDisconnect: () => {
      for (const fn of disconnectListeners.slice()) fn();
    },
  };
}

function trustedSender(overrides = {}) {
  return {
    id: RUNTIME_ID,
    url: "https://frame.example/login?x=1#y",
    frameId: 3,
    documentId: "doc-123",
    tab: { id: 42, url: "https://top.example/dashboard" },
    ...overrides,
  };
}

function forgedRequest(id) {
  return {
    kind: "request",
    publicKeyCredentialsGetAllowed: true,
    request: {
      v: 1,
      id,
      type: "passkey.get",
      deadlineUnixMs: Date.now() + 60_000,
      origin: "https://evil.example",
      crossOrigin: true,
      context: { frameOrigin: "https://evil.example", tabId: 999, frameId: 0 },
      payload: {
        challenge: "AAAA",
        rpId: "frame.example",
        origin: "https://evil.example",
        crossOrigin: true,
        context: { forged: true },
      },
    },
  };
}

async function ensureBackground() {
  await import("../src/background.js");
  return onConnectListener;
}

test("buildTrustedContext derives origins and ids only from the sender", async () => {
  const { buildTrustedContext } = await import("../src/trusted-context.js");
  const context = buildTrustedContext(trustedSender(), RUNTIME_ID, true);

  assert.deepEqual(context, {
    frameOrigin: "https://frame.example",
    topOrigin: "https://top.example",
    tabId: 42,
    frameId: 3,
    documentId: "doc-123",
    publicKeyCredentialsGetAllowed: true,
  });
  assert.ok(Object.isFrozen(context));
});

test("buildTrustedContext rejects mismatched sender id and non-canonical shapes", async () => {
  const { buildTrustedContext } = await import("../src/trusted-context.js");

  assert.throws(() => buildTrustedContext(trustedSender({ id: "other" }), RUNTIME_ID, true));
  assert.throws(() => buildTrustedContext(trustedSender({ frameId: 1.5 }), RUNTIME_ID, true));
  assert.throws(() => buildTrustedContext(trustedSender({ documentId: 123 }), RUNTIME_ID, true));
  assert.throws(
    () => buildTrustedContext(trustedSender({ tab: { id: "x", url: "https://top.example/" } }), RUNTIME_ID, true),
  );
  assert.throws(
    () => buildTrustedContext(trustedSender({ url: "data:text/html,hi" }), RUNTIME_ID, true),
  );
});

test("buildTrustedContext forces publicKeyCredentialsGetAllowed to a strict boolean", async () => {
  const { buildTrustedContext } = await import("../src/trusted-context.js");
  assert.equal(buildTrustedContext(trustedSender(), RUNTIME_ID, false).publicKeyCredentialsGetAllowed, false);
  assert.equal(buildTrustedContext(trustedSender(), RUNTIME_ID, "true").publicKeyCredentialsGetAllowed, false);
});

test("the trusted sender context beats page-forged origin/context", async () => {
  const listener = await ensureBackground();
  const frame = makeFramePort(trustedSender());
  listener(frame.port);
  frame.emitMessage(forgedRequest("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
  await tick();

  const [request] = nativePort.requests();
  assert.ok(request, "a request should reach the native port");
  assert.equal(request.v, 1);
  assert.deepEqual(request.context, {
    frameOrigin: "https://frame.example",
    topOrigin: "https://top.example",
    tabId: 42,
    frameId: 3,
    documentId: "doc-123",
    publicKeyCredentialsGetAllowed: true,
  });
  // The forged top-level origin/crossOrigin are gone, and the payload origin is the trusted one.
  assert.equal(request.origin, undefined);
  assert.equal(request.crossOrigin, undefined);
  assert.equal(request.payload.origin, "https://frame.example");
  assert.notEqual(request.payload.origin, "https://evil.example");
  assert.equal(request.payload.context, undefined);
});

test("one connectNative port serves two requests", async () => {
  const listener = await ensureBackground();
  const frame = makeFramePort(trustedSender());
  listener(frame.port);

  frame.emitMessage(forgedRequest("11111111111111111111111111111111"));
  await tick();
  frame.emitMessage(forgedRequest("22222222222222222222222222222222"));
  await tick();

  assert.equal(connectNativeCalls.length, 1);
  assert.equal(connectNativeCalls[0], "com.winvaultwarden.browser");
  const ids = nativePort.requests().map((request) => request.id);
  assert.ok(ids.includes("11111111111111111111111111111111"));
  assert.ok(ids.includes("22222222222222222222222222222222"));
});

test("a wrong port name is refused without touching the native host", async () => {
  const listener = await ensureBackground();
  const before = connectNativeCalls.length;
  const frame = makeFramePort(trustedSender(), "totally-wrong");
  listener(frame.port);
  frame.emitMessage(forgedRequest("33333333333333333333333333333333"));
  await tick();

  assert.equal(connectNativeCalls.length, before);
  assert.equal(frame.state.disconnectCalled, true);
});

test("an untrusted sender is answered with security_sender, never forwarded", async () => {
  const listener = await ensureBackground();
  const before = connectNativeCalls.length;
  const frame = makeFramePort(trustedSender({ id: "spoofed" }));
  listener(frame.port);
  frame.emitMessage(forgedRequest("44444444444444444444444444444444"));
  await tick();

  assert.equal(connectNativeCalls.length, before);
  const response = frame.posted.find((message) => message.id === "44444444444444444444444444444444");
  assert.ok(response);
  assert.equal(response.response.ok, false);
  assert.equal(response.response.error.code, "security_sender");
});

test("a cancel from the frame forwards to the native port", async () => {
  const listener = await ensureBackground();
  const frame = makeFramePort(trustedSender());
  listener(frame.port);
  frame.emitMessage(forgedRequest("55555555555555555555555555555555"));
  await tick();
  frame.emitMessage({ kind: "cancel", id: "55555555555555555555555555555555" });
  await tick();

  const cancel = nativePort.posted.find((message) => message.type === "cancel");
  assert.ok(cancel);
  assert.equal(cancel.id, "55555555555555555555555555555555");
});
