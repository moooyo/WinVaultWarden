import test from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFileSync } from "node:fs";
import { webcrypto } from "node:crypto";

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

function makeWindow(origin, { isTop = true } = {}) {
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
  win.top = isTop ? win : { name: "top" };
  return win;
}

function makePort() {
  const posted = [];
  const messageListeners = [];
  const disconnectListeners = [];
  const state = { disconnectCalled: false };
  const port = {
    name: null,
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

function createContentHarness({ isTop = true, policy = "allow", lastErrorMessage = null } = {}) {
  const origin = "https://frame.example";
  const win = makeWindow(origin, { isTop });
  const portHandle = makePort();
  const connectCalls = [];
  const chrome = {
    runtime: {
      get lastError() {
        return lastErrorMessage ? { message: lastErrorMessage } : null;
      },
      connect(options) {
        portHandle.port.name = options?.name;
        connectCalls.push(options);
        return portHandle.port;
      },
    },
  };

  const grants = policy === "allow" || policy === "featurePolicy";
  const allowsFeature = (feature) => grants && feature === "public-key-credentials-get";
  const policyObject = policy === "absent" ? undefined : { allowsFeature };
  const document = {
    permissionsPolicy: policy === "featurePolicy" ? undefined : policyObject,
    featurePolicy: policy === "featurePolicy" ? { allowsFeature } : undefined,
  };

  const sandbox = {
    window: win,
    document,
    chrome,
    crypto: webcrypto,
    structuredClone,
    console,
    setTimeout,
    clearTimeout,
    queueMicrotask,
  };
  const context = loadClassic(sandbox, "request-coordinator.js", "content-webauthn.js");

  const fromIsolated = [];
  win.addEventListener("message", (event) => {
    const message = event.data;
    if (event.source !== win || message?.source !== "winvaultwarden-isolated") return;
    fromIsolated.push(message);
  });

  const bootstrapId = randomHex(16);
  win.postMessage({ source: "winvaultwarden-main", kind: "main.ready", bootstrapId });
  const ready = fromIsolated.find(
    (message) => message.kind === "isolated.ready" && message.bootstrapId === bootstrapId,
  );

  return {
    context,
    connectCalls,
    portHandle,
    fromIsolated,
    channelId: ready?.channelId,
    sendFromMain(message) {
      win.postMessage({ source: "winvaultwarden-main", ...message });
    },
    pagehide() {
      win.dispatch("pagehide");
    },
  };
}

test("coordinator mints 32-byte channels and 16-byte request ids", () => {
  const harness = createContentHarness();
  const coordinator = harness.context.WinVaultWardenRequestCoordinator;

  assert.match(coordinator.randomChannelId(), /^[0-9a-f]{64}$/u);
  assert.match(coordinator.randomRequestId(), /^[0-9a-f]{32}$/u);
  assert.notEqual(coordinator.randomChannelId(), coordinator.randomChannelId());
});

test("each frame connects one named long-lived port and exposes a 32-byte channel", () => {
  const harness = createContentHarness();
  assert.equal(harness.connectCalls.length, 1);
  assert.equal(harness.connectCalls[0].name, "winvaultwarden-passkey-frame-v1");
  assert.match(harness.channelId, /^[0-9a-f]{64}$/u);
});

test("a matching request forwards to the port with origin/context stripped", () => {
  const harness = createContentHarness();
  const id = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id,
    kind: "request",
    request: {
      v: 1,
      id,
      type: "passkey.get",
      origin: "https://evil.example",
      crossOrigin: true,
      context: { forged: true },
      payload: {
        challenge: "AAA",
        origin: "https://evil.example",
        crossOrigin: true,
        context: { forged: true },
      },
    },
  });

  assert.equal(harness.portHandle.posted.length, 1);
  const [forwarded] = harness.portHandle.posted;
  assert.equal(forwarded.kind, "request");
  assert.equal(forwarded.publicKeyCredentialsGetAllowed, true);
  assert.equal(forwarded.request.origin, undefined);
  assert.equal(forwarded.request.crossOrigin, undefined);
  assert.equal(forwarded.request.context, undefined);
  assert.equal(forwarded.request.payload.origin, undefined);
  assert.equal(forwarded.request.payload.crossOrigin, undefined);
  assert.equal(forwarded.request.payload.context, undefined);
});

test("ignores messages on a foreign channel or from another window", () => {
  const harness = createContentHarness();
  const id = randomHex(16);
  harness.sendFromMain({ channelId: "deadbeef", id, kind: "request", request: { id, payload: {} } });
  assert.equal(harness.portHandle.posted.length, 0);
});

test("a port response is relayed back to MAIN on the frame channel", () => {
  const harness = createContentHarness();
  const id = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id,
    kind: "request",
    request: { v: 1, id, type: "passkey.get", payload: {} },
  });
  harness.portHandle.emitMessage({
    id,
    response: { v: 1, id, type: "passkey.get", ok: true, payload: { credentialId: "abc" } },
  });

  const response = harness.fromIsolated.find((message) => message.kind === "response");
  assert.ok(response);
  assert.equal(response.source, "winvaultwarden-isolated");
  assert.equal(response.channelId, harness.channelId);
  assert.equal(response.id, id);
  assert.equal(response.response.payload.credentialId, "abc");
});

test("pagehide cancels active requests and disconnects the port", () => {
  const harness = createContentHarness();
  const id = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id,
    kind: "request",
    request: { v: 1, id, type: "passkey.get", payload: {} },
  });
  harness.pagehide();

  const cancel = harness.portHandle.posted.find((message) => message.kind === "cancel");
  assert.ok(cancel);
  assert.equal(cancel.id, id);
  assert.equal(harness.portHandle.state.disconnectCalled, true);
});

test("a cancel from MAIN forwards to the port once", () => {
  const harness = createContentHarness();
  const id = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id,
    kind: "request",
    request: { v: 1, id, type: "passkey.get", payload: {} },
  });
  harness.sendFromMain({ channelId: harness.channelId, id, kind: "cancel" });
  harness.sendFromMain({ channelId: harness.channelId, id, kind: "cancel" });

  const cancels = harness.portHandle.posted.filter((message) => message.kind === "cancel");
  assert.equal(cancels.length, 1);
});

test("port disconnect completes active requests as operation_aborted without leaking lastError", () => {
  const harness = createContentHarness({ lastErrorMessage: "native host secret detail" });
  const id = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id,
    kind: "request",
    request: { v: 1, id, type: "passkey.get", payload: {} },
  });
  harness.portHandle.emitDisconnect();

  const aborted = harness.fromIsolated.find((message) => message.kind === "response");
  assert.ok(aborted);
  assert.equal(aborted.id, id);
  assert.equal(aborted.response.ok, false);
  assert.equal(aborted.response.error.code, "operation_aborted");
  assert.doesNotMatch(JSON.stringify(aborted), /secret detail/u);
});

test("top-level frames always allow public-key-credentials-get", () => {
  const harness = createContentHarness({ isTop: true, policy: "deny" });
  const id = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id,
    kind: "request",
    request: { v: 1, id, type: "passkey.get", payload: {} },
  });
  assert.equal(harness.portHandle.posted[0].publicKeyCredentialsGetAllowed, true);
});

test("sub-frames honour the Permissions Policy verdict", () => {
  for (const [policy, expected] of [
    ["allow", true],
    ["deny", false],
    ["absent", false],
    ["featurePolicy", true],
  ]) {
    const harness = createContentHarness({ isTop: false, policy });
    const id = randomHex(16);
    harness.sendFromMain({
      channelId: harness.channelId,
      id,
      kind: "request",
      request: { v: 1, id, type: "passkey.get", payload: {} },
    });
    assert.equal(
      harness.portHandle.posted[0].publicKeyCredentialsGetAllowed,
      expected,
      `policy=${policy}`,
    );
  }
});
