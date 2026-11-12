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
    dispatch(type, detail = {}) {
      for (const fn of (namedListeners.get(type) ?? []).slice()) fn({ type, ...detail });
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
      if (state.disconnectCalled) throw new Error("Disconnected port");
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
      state.disconnectCalled = true;
      for (const fn of disconnectListeners.slice()) fn();
    },
  };
}

function createContentHarness({ isTop = true, policy = "allow", createPolicy = policy, lastErrorMessage = null } = {}) {
  const origin = "https://frame.example";
  const win = makeWindow(origin, { isTop });
  const portHandle = makePort();
  const portHandles = [];
  const connectCalls = [];
  const chrome = {
    runtime: {
      get lastError() {
        return lastErrorMessage ? { message: lastErrorMessage } : null;
      },
      connect(options) {
        const handle = portHandles.length === 0 ? portHandle : makePort();
        handle.port.name = options?.name;
        portHandles.push(handle);
        connectCalls.push(options);
        return handle.port;
      },
    },
  };

  const grants = policy === "allow" || policy === "featurePolicy";
  const queriedFeatures = [];
  // The canonical WebAuthn Permissions Policy token is `publickey-credentials-get` (no hyphen
  // between "public" and "key"). Only the canonical spelling is ever granted; the misspelled
  // `public-key-credentials-get` must never yield a verdict.
  const allowsFeature = (feature) => {
    queriedFeatures.push(feature);
    if (feature === "publickey-credentials-get") return grants;
    return feature === "publickey-credentials-create" && ["allow", "featurePolicy"].includes(createPolicy);
  };
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
  // Real Chrome/Edge dedupe an identical content-script path across content_scripts entries and
  // only inject it into the first world that claims it, so the ISOLATED relay never sees a
  // `WinVaultWardenRequestCoordinator` global in production. The harness must not load
  // request-coordinator.js here, or it would mask that reality.
  const context = loadClassic(sandbox, "content-webauthn.js");

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
    portHandles,
    fromIsolated,
    queriedFeatures,
    channelId: ready?.channelId,
    sendFromMain(message) {
      win.postMessage({ source: "winvaultwarden-main", ...message });
    },
    pagehide() {
      win.dispatch("pagehide");
    },
    pageshow() {
      win.dispatch("pageshow", { persisted: true });
    },
  };
}

test("installs the relay, connects the port, and announces readiness without a global WinVaultWardenRequestCoordinator", () => {
  const harness = createContentHarness();

  assert.equal(harness.context.WinVaultWardenRequestCoordinator, undefined);
  assert.equal(harness.connectCalls.length, 1);
  assert.equal(harness.connectCalls[0].name, "winvaultwarden-passkey-frame-v1");
  assert.ok(harness.fromIsolated.some((message) => message.kind === "isolated.ready"));
  assert.match(harness.channelId, /^[0-9a-f]{64}$/u);
});

test("each frame mints its own distinct 32-byte channel id", () => {
  const first = createContentHarness();
  const second = createContentHarness();

  assert.match(first.channelId, /^[0-9a-f]{64}$/u);
  assert.match(second.channelId, /^[0-9a-f]{64}$/u);
  assert.notEqual(first.channelId, second.channelId);
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
  assert.equal(forwarded.id, id);
  assert.equal(forwarded.request.id, id);
  assert.equal(forwarded.publicKeyCredentialsGetAllowed, true);
  assert.equal(forwarded.request.origin, undefined);
  assert.equal(forwarded.request.crossOrigin, undefined);
  assert.equal(forwarded.request.context, undefined);
  assert.equal(forwarded.request.payload.origin, undefined);
  assert.equal(forwarded.request.payload.crossOrigin, undefined);
  assert.equal(forwarded.request.payload.context, undefined);
});

test("rejects divergent envelope and nested request ids without forwarding", () => {
  const harness = createContentHarness();
  const outerId = randomHex(16);
  const innerId = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id: outerId,
    kind: "request",
    request: { v: 1, id: innerId, type: "passkey.get", payload: {} },
  });

  assert.equal(harness.portHandle.posted.length, 0);
  const response = harness.fromIsolated.find(
    (message) => message.kind === "response" && message.id === outerId,
  );
  assert.equal(response.response.error.code, "security_sender");
  assert.equal(response.response.id, outerId);
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
    response: {
      v: 1,
      id: randomHex(16),
      type: "passkey.get",
      ok: true,
      payload: { credentialId: "abc" },
    },
  });

  const response = harness.fromIsolated.find((message) => message.kind === "response");
  assert.ok(response);
  assert.equal(response.source, "winvaultwarden-isolated");
  assert.equal(response.channelId, harness.channelId);
  assert.equal(response.id, id);
  assert.equal(response.response.id, id);
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

test("a cancel racing a disconnected port is still locally settled", () => {
  const harness = createContentHarness();
  const id = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id,
    kind: "request",
    request: { v: 1, id, type: "passkey.get", payload: {} },
  });
  harness.portHandle.port.postMessage = () => {
    throw new Error("port disconnected");
  };

  assert.doesNotThrow(() =>
    harness.sendFromMain({ channelId: harness.channelId, id, kind: "cancel" }),
  );

  harness.portHandle.emitMessage({
    id,
    response: { v: 1, id, type: "passkey.get", ok: true, payload: { credentialId: "late" } },
  });
  assert.equal(
    harness.fromIsolated.filter((message) => message.kind === "response").length,
    0,
  );
});

test("a response arriving after cancellation is dropped by the relay", () => {
  const harness = createContentHarness();
  const id = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id,
    kind: "request",
    request: { v: 1, id, type: "passkey.get", payload: {} },
  });
  harness.sendFromMain({ channelId: harness.channelId, id, kind: "cancel" });
  const before = harness.fromIsolated.filter((message) => message.kind === "response").length;

  harness.portHandle.emitMessage({
    id,
    response: { v: 1, id, type: "passkey.get", ok: true, payload: { credentialId: "late" } },
  });

  const after = harness.fromIsolated.filter((message) => message.kind === "response").length;
  assert.equal(after, before);
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

test("top-level frames honor an explicit publickey-credentials-get denial", () => {
  const harness = createContentHarness({ isTop: true, policy: "deny" });
  const id = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id,
    kind: "request",
    request: { v: 1, id, type: "passkey.get", payload: {} },
  });
  assert.equal(harness.portHandle.posted[0].publicKeyCredentialsGetAllowed, false);
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

test("queries the canonical publickey-credentials-get token, never the misspelled public-key-credentials-get", () => {
  const harness = createContentHarness({ isTop: false, policy: "allow" });
  const id = randomHex(16);
  harness.sendFromMain({
    channelId: harness.channelId,
    id,
    kind: "request",
    request: { v: 1, id, type: "passkey.get", payload: {} },
  });

  // The isolated relay must consult the real Permissions Policy with the W3C-canonical feature
  // token. Querying the misspelled `public-key-credentials-get` always yields a false verdict on
  // real browsers, silently denying delegated sub-frames.
  assert.ok(
    harness.queriedFeatures.includes("publickey-credentials-get"),
    `queried features: ${JSON.stringify(harness.queriedFeatures)}`,
  );
  assert.ok(
    !harness.queriedFeatures.includes("public-key-credentials-get"),
    `must not query the misspelled token; queried: ${JSON.stringify(harness.queriedFeatures)}`,
  );
  // A delegated sub-frame using the canonical token is granted.
  assert.equal(harness.portHandle.posted[0].publicKeyCredentialsGetAllowed, true);
});

function sendRequest(harness, id = randomHex(16), type = "passkey.get") {
  harness.sendFromMain({ channelId: harness.channelId, id, kind: "request", request: { v: 1, id, type, payload: {} } });
  return id;
}

test("creation policy is read independently from retrieval policy in the top document", () => {
  const harness = createContentHarness({ policy: "allow", createPolicy: "deny" });
  sendRequest(harness, randomHex(16), "passkey.create");
  assert.equal(harness.portHandle.posted[0].publicKeyCredentialsGetAllowed, true);
  assert.equal(harness.portHandle.posted[0].publicKeyCredentialsCreateAllowed, false);
  assert.ok(harness.queriedFeatures.includes("publickey-credentials-create"));
});

test("BFCache restoration reconnects for new ceremonies without replaying cancelled requests", () => {
  const harness = createContentHarness();
  const oldId = sendRequest(harness);
  harness.pagehide();
  harness.pageshow();
  const nextId = sendRequest(harness);

  assert.equal(harness.connectCalls.length, 2);
  assert.equal(harness.portHandle.state.disconnectCalled, true);
  assert.equal(harness.portHandles[1].posted.length, 1);
  assert.equal(harness.portHandles[1].posted[0].id, nextId);
  assert.equal(harness.fromIsolated.find((message) => message.id === oldId)?.response.error.code, "operation_aborted");
  assert.equal(harness.fromIsolated.filter((message) => message.id === nextId && message.kind === "response").length, 0);

  harness.pagehide();
  harness.pageshow();
  sendRequest(harness);
  assert.equal(harness.connectCalls.length, 3, "every BFCache cycle must reconnect");
});

test("an ordinary disconnect aborts its ceremony and reconnects only for a later request", () => {
  const harness = createContentHarness();
  const firstId = sendRequest(harness);
  harness.portHandle.emitDisconnect();
  assert.equal(harness.connectCalls.length, 1);
  const secondId = sendRequest(harness);
  assert.equal(harness.connectCalls.length, 2);
  assert.equal(harness.portHandles[1].posted[0].id, secondId);
  assert.equal(harness.fromIsolated.find((message) => message.id === firstId)?.response.error.code, "operation_aborted");
});

test("late responses on a retired connection cannot settle a reused id", () => {
  const harness = createContentHarness();
  const id = sendRequest(harness);
  harness.portHandle.emitDisconnect();
  sendRequest(harness, id);
  const before = harness.fromIsolated.filter((message) => message.kind === "response").length;
  harness.portHandle.emitMessage({ id, response: { ok: true, payload: { stale: true } } });
  assert.equal(harness.fromIsolated.filter((message) => message.kind === "response").length, before);
  harness.portHandles[1].emitMessage({ id, response: { ok: true, payload: { stale: false } } });
  assert.equal(harness.fromIsolated.at(-1).response.payload.stale, false);
});

test("pagehide after an abrupt port write failure remains harmless", () => {
  const harness = createContentHarness();
  harness.portHandle.port.postMessage = () => { throw new Error("Disconnected"); };
  sendRequest(harness);
  assert.doesNotThrow(() => harness.pagehide());
});

test("an active document rebuilds its identity port after worker disconnection without replay", async () => {
  const harness = createContentHarness();
  const id = sendRequest(harness);
  harness.portHandle.emitDisconnect();
  await new Promise((resolve) => setTimeout(resolve, 130));
  assert.equal(harness.connectCalls.length, 2);
  assert.equal(harness.portHandles[1].posted.length, 0, "interrupted ceremonies must never be replayed");
  harness.portHandles[1].emitMessage({ kind: "frame.ready" });
  assert.equal(harness.fromIsolated.filter((message) => message.id === id).length, 1);
  harness.pagehide();
});

test("pagehide cancels an identity reconnect scheduled after worker disconnection", async () => {
  const harness = createContentHarness();
  harness.portHandle.emitDisconnect();
  harness.pagehide();
  await new Promise((resolve) => setTimeout(resolve, 130));
  assert.equal(harness.connectCalls.length, 1);
});
