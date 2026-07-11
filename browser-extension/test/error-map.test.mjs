import test from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { readFileSync } from "node:fs";

// The MAIN-world error map must mirror protocol/passkey-errors.json exactly: every wire code resolves
// to the contracted DOMException name (or a fallback), a code-derived generic message, and never any
// backend-supplied text. This is the single source of truth shared by the shim and the App.
function loadErrorMap() {
  const sandbox = { DOMException, console };
  const context = vm.createContext(sandbox);
  const code = readFileSync(new URL("../src/error-map.js", import.meta.url), "utf8");
  vm.runInContext(code, context, { filename: "error-map.js" });
  return sandbox.WinVaultWardenErrorMap;
}

const contract = JSON.parse(
  readFileSync(new URL("../../protocol/passkey-errors.json", import.meta.url), "utf8"),
);
const errorMap = loadErrorMap();

test("contract is non-empty", () => {
  assert.ok(Array.isArray(contract.errors) && contract.errors.length > 0);
});

for (const item of contract.errors) {
  test(`${item.code} maps exactly`, () => {
    const response = { ok: false, error: { code: item.code, message: "SENTINEL_INTERNAL" } };
    assert.equal(errorMap.shouldFallback(response), item.fallback);
    if (!item.fallback) {
      const error = errorMap.toException(item.code);
      assert.equal(error.name, item.domException);
      // No backend text ever reaches a page-visible exception.
      assert.doesNotMatch(error.message, /SENTINEL_INTERNAL/u);
    }
  });
}

test("only client_not_handled falls back", () => {
  for (const item of contract.errors) {
    assert.equal(item.fallback, item.code === "client_not_handled");
  }
  assert.equal(errorMap.shouldFallback({ ok: false, error: { code: "client_not_handled" } }), true);
});

test("unknown code never falls back and maps to NotAllowedError", () => {
  const unknown = { ok: false, error: { code: "totally_unknown_code", message: "SENTINEL_INTERNAL" } };
  assert.equal(errorMap.shouldFallback(unknown), false);
  const error = errorMap.toException("totally_unknown_code");
  assert.equal(error.name, "NotAllowedError");
  assert.doesNotMatch(error.message, /SENTINEL_INTERNAL/u);
});

test("user_verification_failed is pinned to NotAllowedError", () => {
  // Called out explicitly in the task: a UV failure must not become a NotSupported/Security error.
  assert.equal(errorMap.toException("user_verification_failed").name, "NotAllowedError");
});

test("a successful response never falls back", () => {
  assert.equal(errorMap.shouldFallback({ ok: true }), false);
});
