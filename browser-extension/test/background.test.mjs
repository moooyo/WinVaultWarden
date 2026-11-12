import test from "node:test";
import assert from "node:assert/strict";
import { buildTrustedContext as reconstructTrustedContext } from "../src/trusted-context.js";

const RUNTIME_ID = "trusted-extension-id";
const FRAME_PORT_NAME = "winvaultwarden-passkey-frame-v1";

let onConnectListener = null;
const connectNativeCalls = [];
let nativePort = null;
const currentFrames = new Map();
const ancestorPorts = new Map();
let frameQueryOverride = null;

function framesFor(sender) {
  const root = {
    frameId: 0, parentFrameId: -1, documentId: "top-doc", documentLifecycle: "active", url: sender.tab.url,
  };
  return sender.frameId === 0
    ? [{ ...root, documentId: sender.documentId, url: sender.url }]
    : [root, {
      frameId: sender.frameId, parentFrameId: 0, documentId: sender.documentId,
      documentLifecycle: "active", url: sender.url,
    }];
}

function originsFor(sender, frames) {
  return new Map(frames.map((frame) => [frame.frameId, {
    ...sender, frameId: frame.frameId, documentId: frame.documentId,
    origin: new URL(frame.url).origin, url: frame.url,
  }]));
}

function buildTrustedContext(sender, runtimeId, policyAllowed, frames = framesFor(sender), createAllowed = true, origins = originsFor(sender, frames)) {
  return reconstructTrustedContext(sender, runtimeId, policyAllowed, frames, createAllowed, origins);
}

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
  webNavigation: {
    async getAllFrames({ tabId }) {
      if (frameQueryOverride) return frameQueryOverride(tabId);
      return [...(currentFrames.get(tabId)?.values() ?? [])];
    },
  },
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

function makeFramePort(sender, name = FRAME_PORT_NAME, registerAncestors = true) {
  if (sender?.tab && Number.isInteger(sender.frameId)) {
    const frames = currentFrames.get(sender.tab.id) ?? new Map();
    for (const frame of framesFor(sender)) {
      if (frame.frameId !== 0 || sender.frameId === 0 || !frames.has(0)) {
        frames.set(frame.frameId, frame);
      }
    }
    currentFrames.set(sender.tab.id, frames);
    if (registerAncestors && sender.frameId !== 0 && onConnectListener) {
      const root = frames.get(0);
      if (ancestorPorts.get(sender.tab.id)?.documentId !== root.documentId) {
        const rootSender = {
          ...sender, id: RUNTIME_ID, frameId: 0, documentId: root.documentId,
          origin: new URL(root.url).origin, url: root.url,
        };
        onConnectListener(makeFramePort(rootSender, FRAME_PORT_NAME, false).port);
      }
    }
  }
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
  if (sender?.frameId === 0 && sender.id === RUNTIME_ID) {
    ancestorPorts.set(sender.tab.id, { documentId: sender.documentId, port });
  }
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
    origin: overrides.frameId === 0 ? "https://top.example" : "https://frame.example",
    url: overrides.frameId === 0 ? "https://top.example/login?x=1#y" : "https://frame.example/login?x=1#y",
    frameId: 3,
    documentId: "doc-123",
    tab: { id: 42, url: "https://top.example/dashboard" },
    ...overrides,
  };
}

function forgedRequest(id) {
  return {
    kind: "request",
    id,
    publicKeyCredentialsGetAllowed: true,
    publicKeyCredentialsCreateAllowed: true,
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

function completeNativeRequest(request, payload = {}) {
  nativePort.emitMessage({
    v: 1,
    id: request.id,
    type: request.type,
    ok: true,
    payload,
  });
}

test("buildTrustedContext derives origins and ids only from browser metadata", async () => {
  const context = buildTrustedContext(trustedSender(), RUNTIME_ID, true);

  assert.deepEqual(context, {
    frameOrigin: "https://frame.example",
    topOrigin: "https://top.example",
    tabId: 42,
    frameId: 3,
    documentId: "doc-123",
    sameOriginWithAncestors: false,
    publicKeyCredentialsGetAllowed: true,
    publicKeyCredentialsCreateAllowed: true,
  });
  assert.ok(Object.isFrozen(context));
});

test("buildTrustedContext rejects mismatched sender id and non-canonical shapes", async () => {

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
  assert.equal(buildTrustedContext(trustedSender(), RUNTIME_ID, false).publicKeyCredentialsGetAllowed, false);
  assert.equal(buildTrustedContext(trustedSender(), RUNTIME_ID, "true").publicKeyCredentialsGetAllowed, false);
  assert.equal(buildTrustedContext(trustedSender(), RUNTIME_ID, true, framesFor(trustedSender()), "true")
    .publicKeyCredentialsCreateAllowed, false);
});

test("the complete A-to-B-to-A ancestor chain is cross-origin even when frame and top match", () => {
  const sender = trustedSender({ tab: { id: 42, url: "https://frame.example/" } });
  const frames = [
    { frameId: 0, parentFrameId: -1, documentId: "root-a", documentLifecycle: "active", url: "https://frame.example/" },
    { frameId: 2, parentFrameId: 0, documentId: "middle-b", documentLifecycle: "active", url: "https://other.example/" },
    { frameId: 3, parentFrameId: 2, documentId: sender.documentId, documentLifecycle: "active", url: sender.url },
  ];
  const context = buildTrustedContext(sender, RUNTIME_ID, true, frames);
  assert.equal(context.frameOrigin, context.topOrigin);
  assert.equal(context.sameOriginWithAncestors, false);
});

test("only a complete active browser frame tree grants same-origin ancestor status", () => {
  const sender = trustedSender({ tab: { id: 42, url: "https://frame.example/" } });
  const frames = framesFor(sender);
  assert.equal(buildTrustedContext(sender, RUNTIME_ID, true, frames).sameOriginWithAncestors, true);
  assert.throws(() => buildTrustedContext(sender, RUNTIME_ID, true, []));
  assert.throws(() => buildTrustedContext(sender, RUNTIME_ID, true, frames.slice(1)));
  assert.throws(() => buildTrustedContext(sender, RUNTIME_ID, true,
    frames.map((frame) => frame.frameId === 3 ? { ...frame, documentId: "replaced" } : frame)));
  assert.throws(() => buildTrustedContext(sender, RUNTIME_ID, true,
    frames.map((frame) => ({ ...frame, documentLifecycle: "cached" }))));
  assert.throws(() => buildTrustedContext(sender, RUNTIME_ID, true,
    frames.map((frame) => frame.frameId === 3 ? { ...frame, parentFrameId: 3 } : frame)));
  assert.throws(() => buildTrustedContext(sender, RUNTIME_ID, true, [...frames, frames[0]]));
});

test("top origin comes from the current browser tree rather than a stale sender tab URL", () => {
  const sender = trustedSender();
  const frames = framesFor(sender);
  frames[0] = { ...frames[0], url: "https://current-top.example/" };
  const context = buildTrustedContext(sender, RUNTIME_ID, true, frames);
  assert.equal(context.topOrigin, "https://current-top.example");
});

test("an HTTPS ancestor URL cannot impersonate an opaque browser origin", () => {
  const sender = trustedSender({ tab: { id: 42, url: "https://frame.example/" } });
  const frames = framesFor(sender);
  const origins = originsFor(sender, frames);
  origins.get(0).origin = "null";
  assert.throws(() => buildTrustedContext(sender, RUNTIME_ID, true, frames, true, origins));
  origins.delete(0);
  assert.throws(() => buildTrustedContext(sender, RUNTIME_ID, true, frames, true, origins));
});

test("an insecure intermediate document cannot become a trusted WebAuthn ancestor", () => {
  const sender = trustedSender({ tab: { id: 42, url: "https://frame.example/" } });
  const frames = [
    ...framesFor(sender),
    { frameId: 2, parentFrameId: 0, documentId: "http-middle", documentLifecycle: "active", url: "http://other.example/" },
  ];
  frames.find((frame) => frame.frameId === 3).parentFrameId = 2;
  assert.throws(() => buildTrustedContext(sender, RUNTIME_ID, true, frames));
});

test("the trusted sender context beats page-forged origin/context", async () => {
  const listener = await ensureBackground();
  const frame = makeFramePort(trustedSender());
  listener(frame.port);
  const pageId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
  frame.emitMessage(forgedRequest(pageId));
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
    sameOriginWithAncestors: false,
    publicKeyCredentialsGetAllowed: true,
    publicKeyCredentialsCreateAllowed: true,
  });
  // The forged top-level origin/crossOrigin are gone, and the payload origin is the trusted one.
  assert.equal(request.origin, undefined);
  assert.equal(request.crossOrigin, undefined);
  assert.equal(request.payload.origin, "https://frame.example");
  assert.notEqual(request.payload.origin, "https://evil.example");
  assert.equal(request.payload.context, undefined);
  assert.match(request.id, /^[0-9a-f]{32}$/u);
  assert.notEqual(request.id, pageId);

  completeNativeRequest(request, { credentialId: "credential" });
  await tick();
  const response = frame.posted.find((message) => message.id === pageId);
  assert.equal(response.response.id, pageId);
  assert.equal(response.response.payload.credentialId, "credential");
});

test("buildTrustedContext accepts a matching top-level sender origin", async () => {
  const context = buildTrustedContext(
    trustedSender({
      origin: "https://top.example",
      url: "https://top.example/dashboard?view=vault",
      frameId: 0,
      documentId: "top-doc",
    }),
    RUNTIME_ID,
    true,
  );

  assert.equal(context.frameOrigin, "https://top.example");
  assert.equal(context.topOrigin, "https://top.example");
  assert.equal(context.frameId, 0);
});

test("buildTrustedContext rejects a missing sender origin", async () => {
  const missingOrigin = trustedSender();
  delete missingOrigin.origin;

  assert.throws(() => buildTrustedContext(missingOrigin, RUNTIME_ID, true));
});

test("buildTrustedContext rejects an opaque sandbox sender origin", async () => {

  assert.throws(() => buildTrustedContext(trustedSender({ origin: "null" }), RUNTIME_ID, true));
});

test("buildTrustedContext rejects non-tuple and malformed sender origins", async () => {

  assert.throws(
    () => buildTrustedContext(
      trustedSender({ origin: "data:text/html,sandboxed" }),
      RUNTIME_ID,
      true,
    ),
  );
  assert.throws(
    () => buildTrustedContext(
      trustedSender({ origin: "not a valid origin" }),
      RUNTIME_ID,
      true,
    ),
  );
});

test("buildTrustedContext rejects a sender origin that mismatches the frame URL", async () => {

  assert.throws(
    () => buildTrustedContext(
      trustedSender({ origin: "https://other.example" }),
      RUNTIME_ID,
      true,
    ),
  );
});

test("one connectNative port serves two sequential requests", async () => {
  const listener = await ensureBackground();
  const frame = makeFramePort(trustedSender());
  listener(frame.port);

  frame.emitMessage(forgedRequest("11111111111111111111111111111111"));
  await tick();
  const first = nativePort.requests().at(-1);
  completeNativeRequest(first);
  await tick();
  frame.emitMessage(forgedRequest("22222222222222222222222222222222"));
  await tick();
  const second = nativePort.requests().at(-1);

  assert.equal(connectNativeCalls.length, 1);
  assert.equal(connectNativeCalls[0], "com.winvaultwarden.browser");
  assert.notEqual(first.id, second.id);
  assert.notEqual(first.id, "11111111111111111111111111111111");
  assert.notEqual(second.id, "22222222222222222222222222222222");

  completeNativeRequest(second);
  await tick();
});

test("two frames may reuse a page id while receiving distinct native wire ids", async () => {
  const listener = await ensureBackground();
  const firstFrame = makeFramePort(trustedSender({ frameId: 10, documentId: "same-id-first" }));
  const secondFrame = makeFramePort(trustedSender({ frameId: 11, documentId: "same-id-second" }));
  listener(firstFrame.port);
  listener(secondFrame.port);
  const pageId = "cccccccccccccccccccccccccccccccc";
  const before = nativePort.requests().length;

  firstFrame.emitMessage(forgedRequest(pageId));
  secondFrame.emitMessage(forgedRequest(pageId));
  await tick();
  const [firstRequest, secondRequest] = nativePort.requests().slice(before);

  assert.ok(firstRequest);
  assert.ok(secondRequest);
  assert.notEqual(firstRequest.id, secondRequest.id);
  assert.notEqual(firstRequest.id, pageId);
  assert.notEqual(secondRequest.id, pageId);

  completeNativeRequest(firstRequest, { frame: "first" });
  completeNativeRequest(secondRequest, { frame: "second" });
  await tick();
  assert.equal(firstFrame.posted.find((item) => item.id === pageId).response.payload.frame, "first");
  assert.equal(secondFrame.posted.find((item) => item.id === pageId).response.payload.frame, "second");
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
  const request = nativePort.requests().at(-1);
  frame.emitMessage({ kind: "cancel", id: "55555555555555555555555555555555" });
  await tick();

  const cancel = nativePort.posted.find(
    (message) => message.type === "cancel" && message.id === request.id,
  );
  assert.ok(cancel);
  assert.notEqual(cancel.id, "55555555555555555555555555555555");
});

test("a mismatched envelope and nested id is rejected before native dispatch", async () => {
  const listener = await ensureBackground();
  const frame = makeFramePort(trustedSender());
  listener(frame.port);
  const before = nativePort.requests().length;
  const message = forgedRequest("66666666666666666666666666666666");
  message.request.id = "77777777777777777777777777777777";

  frame.emitMessage(message);
  await tick();

  assert.equal(nativePort.requests().length, before);
  const response = frame.posted.find(
    (item) => item.id === "66666666666666666666666666666666",
  );
  assert.equal(response.response.error.code, "security_sender");
  assert.equal(response.response.id, "66666666666666666666666666666666");
});

test("a frame admits one ceremony and fails a second distinct id immediately", async () => {
  const listener = await ensureBackground();
  const frame = makeFramePort(trustedSender());
  listener(frame.port);
  const firstId = "88888888888888888888888888888888";
  const secondId = "99999999999999999999999999999999";
  const before = nativePort.requests().length;

  frame.emitMessage(forgedRequest(firstId));
  frame.emitMessage(forgedRequest(secondId));
  await tick();

  assert.equal(nativePort.requests().length, before + 1);
  const rejected = frame.posted.find((item) => item.id === secondId);
  assert.equal(rejected.response.error.code, "request_in_progress");

  frame.emitMessage({ kind: "cancel", id: firstId });
  await tick();
});

test("global admission rejects the ninth concurrent frame without queuing it", async () => {
  const listener = await ensureBackground();
  const frames = Array.from({ length: 9 }, (_, index) => {
    const frame = makeFramePort(trustedSender({
      frameId: index,
      documentId: `global-doc-${index}`,
    }));
    listener(frame.port);
    return frame;
  });
  const ids = frames.map((_, index) => index.toString(16).padStart(32, "a"));
  const before = nativePort.requests().length;

  frames.forEach((frame, index) => frame.emitMessage(forgedRequest(ids[index])));
  await tick();

  assert.equal(nativePort.requests().length, before + 8);
  const rejected = frames[8].posted.find((item) => item.id === ids[8]);
  assert.equal(rejected.response.error.code, "rate_limited");

  frames.slice(0, 8).forEach((frame, index) => {
    frame.emitMessage({ kind: "cancel", id: ids[index] });
  });
  await tick();
});

test("a cancelled page id can be reused without accepting the old native response", async () => {
  const listener = await ensureBackground();
  const frame = makeFramePort(trustedSender());
  listener(frame.port);
  const pageId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

  frame.emitMessage(forgedRequest(pageId));
  await tick();
  const oldRequest = nativePort.requests().at(-1);
  frame.emitMessage({ kind: "cancel", id: pageId });
  await tick();

  frame.emitMessage(forgedRequest(pageId));
  await tick();
  const currentRequest = nativePort.requests().at(-1);
  assert.notEqual(currentRequest.id, oldRequest.id);

  completeNativeRequest(oldRequest, { stale: true });
  await tick();
  assert.equal(frame.posted.filter((item) => item.id === pageId).length, 0);

  completeNativeRequest(currentRequest, { stale: false });
  await tick();
  const response = frame.posted.find((item) => item.id === pageId);
  assert.equal(response.response.id, pageId);
  assert.equal(response.response.payload.stale, false);
});

test("cancellation while browser context is pending never opens a native ceremony", async () => {
  const listener = await ensureBackground();
  const sender = trustedSender({ tab: { id: 700, url: "https://top.example/" } });
  const frame = makeFramePort(sender);
  listener(frame.port);
  const pageId = "dddddddddddddddddddddddddddddddd";
  const before = nativePort.requests().length;
  let releaseFrames;
  frameQueryOverride = () => new Promise((resolve) => { releaseFrames = resolve; });
  try {
    frame.emitMessage(forgedRequest(pageId));
    frame.emitMessage({ kind: "cancel", id: pageId });
    releaseFrames(framesFor(sender));
    await tick();
    assert.equal(nativePort.requests().length, before);
    assert.equal(frame.posted.filter((message) => message.response).length, 0);
  } finally {
    frameQueryOverride = null;
  }
});

test("a stale document port is rejected when a frame id has navigated", async () => {
  const listener = await ensureBackground();
  const sender = trustedSender({ tab: { id: 701, url: "https://top.example/" } });
  const frame = makeFramePort(sender);
  listener(frame.port);
  currentFrames.get(701).get(sender.frameId).documentId = "new-document";
  const before = nativePort.requests().length;
  const pageId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
  frame.emitMessage(forgedRequest(pageId));
  await tick();
  assert.equal(nativePort.requests().length, before);
  assert.equal(frame.posted.find((message) => message.response).response.error.code, "security_sender");
});

test("background overwrites forged ancestor and top-origin data using the complete frame tree", async () => {
  const listener = await ensureBackground();
  const sender = trustedSender({ tab: { id: 702, url: "https://frame.example/" } });
  const frame = makeFramePort(sender);
  listener(frame.port);
  listener(makeFramePort({ ...sender, frameId: 2, documentId: "middle-b", origin: "https://other.example", url: "https://other.example/" }, FRAME_PORT_NAME, false).port);
  const frames = currentFrames.get(702);
  frames.set(2, { frameId: 2, parentFrameId: 0, documentId: "middle-b", documentLifecycle: "active", url: "https://other.example/" });
  frames.get(sender.frameId).parentFrameId = 2;
  const pageId = "ffffffffffffffffffffffffffffffff";
  const message = forgedRequest(pageId);
  message.request.sameOriginWithAncestors = true;
  message.request.nonce = "page-controlled-nonce";
  message.request.unexpected = "page-controlled-field";
  message.request.payload.sameOriginWithAncestors = true;
  message.request.payload.nonce = "page-controlled-nonce";
  message.request.payload.topOrigin = "https://forged.example";
  message.request.payload.crossOrigin = false;
  frame.emitMessage(message);
  await tick();
  const request = nativePort.requests().at(-1);
  assert.equal(request.context.sameOriginWithAncestors, false);
  assert.equal(request.payload.crossOrigin, true);
  assert.equal(request.payload.topOrigin, "https://frame.example");
  assert.equal(request.payload.sameOriginWithAncestors, undefined);
  assert.equal(request.sameOriginWithAncestors, undefined);
  assert.equal(request.nonce, undefined);
  assert.equal(request.unexpected, undefined);
  assert.equal(request.payload.nonce, undefined);
  completeNativeRequest(request);
  await tick();
});

test("a leaf cannot supply its own proof for a missing ancestor", async () => {
  const listener = await ensureBackground();
  const sender = trustedSender({ tab: { id: 703, url: "https://top.example/" } });
  const frame = makeFramePort(sender, FRAME_PORT_NAME, false);
  listener(frame.port);
  const pageId = "a0".repeat(16);
  const before = nativePort.requests().length;
  const message = forgedRequest(pageId);
  message.request.context = { sameOriginWithAncestors: true, trustedAncestors: ["https://top.example"] };
  frame.emitMessage(message);
  await tick();
  assert.equal(nativePort.requests().length, before);
  assert.equal(frame.posted.find((item) => item.id === pageId).response.error.code, "security_sender");
});

test("a sandboxed ancestor with an HTTPS URL is rejected using its browser origin", async () => {
  const listener = await ensureBackground();
  const sender = trustedSender({ tab: { id: 704, url: "https://top.example/" } });
  const ancestor = makeFramePort({ ...sender, frameId: 0, documentId: "top-doc", origin: "null", url: "https://top.example/" }, FRAME_PORT_NAME, false);
  listener(ancestor.port);
  const frame = makeFramePort(sender, FRAME_PORT_NAME, false);
  listener(frame.port);
  const pageId = "a1".repeat(16);
  const before = nativePort.requests().length;
  frame.emitMessage(forgedRequest(pageId));
  await tick();
  assert.equal(nativePort.requests().length, before);
  assert.equal(frame.posted.find((item) => item.id === pageId).response.error.code, "security_sender");
});

test("ancestor disconnection revokes a pending child ceremony and drops its late response", async () => {
  const listener = await ensureBackground();
  const sender = trustedSender({ tab: { id: 705, url: "https://top.example/" } });
  const ancestor = makeFramePort({ ...sender, frameId: 0, documentId: "top-doc", origin: "https://top.example", url: "https://top.example/" }, FRAME_PORT_NAME, false);
  listener(ancestor.port);
  const frame = makeFramePort(sender, FRAME_PORT_NAME, false);
  listener(frame.port);
  const pageId = "a2".repeat(16);
  frame.emitMessage(forgedRequest(pageId));
  await tick();
  const request = nativePort.requests().at(-1);
  ancestor.emitDisconnect();
  await tick();
  assert.ok(nativePort.posted.some((item) => item.type === "cancel" && item.id === request.id));
  const response = frame.posted.find((item) => item.id === pageId);
  assert.equal(response.response.error.code, "operation_aborted");
  completeNativeRequest(request, { stale: true });
  await tick();
  assert.equal(frame.posted.filter((item) => item.id === pageId).length, 1);
});
