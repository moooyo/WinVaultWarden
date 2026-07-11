// MAIN- and ISOLATED-world helper shared by both content scripts. It is a classic (non-module)
// script that publishes a single frozen object on globalThis so the page-world shim, the isolated
// relay, and the node:vm tests can all reach it without imports. It carries no page-supplied trust:
// the random channel/request ids only correlate messages, they never authorise anything.
(() => {
  function randomId(byteLength) {
    const bytes = new Uint8Array(byteLength);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
  }

  // Clamp the page-requested timeout into the same [1ms, 120s] window the native host enforces and
  // turn it into an absolute unix-epoch deadline. A missing/non-finite timeout defaults to 60s.
  function deadlineUnixMs(timeout) {
    const requested = Number.isFinite(timeout) ? Number(timeout) : 60_000;
    return Date.now() + Math.min(Math.max(requested, 1), 120_000);
  }

  Object.defineProperty(globalThis, "WinVaultWardenRequestCoordinator", {
    configurable: true,
    value: Object.freeze({
      randomChannelId: () => randomId(32),
      randomRequestId: () => randomId(16),
      deadlineUnixMs,
    }),
  });
})();
