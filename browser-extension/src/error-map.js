// Stable code -> DOMException mapping for the MAIN-world shim. It mirrors protocol/passkey-errors.json
// exactly: every wire code resolves to a fixed DOMException name and a generic, code-derived message.
// The backend never sends a human-readable message across the wire, and the shim never reads one, so
// nothing the native host or App says can leak into a page-visible exception. classic (non-module)
// script; published frozen on globalThis for both the content script and the node:vm tests.
(() => {
  const names = new Map([
    ["user_cancelled", "NotAllowedError"],
    ["user_verification_failed", "NotAllowedError"],
    ["deadline_exceeded", "NotAllowedError"],
    ["rate_limited", "NotAllowedError"],
    ["request_in_progress", "NotAllowedError"],
    ["credential_unusable", "NotAllowedError"],
    ["internal_error", "NotAllowedError"],
    ["credential_excluded", "InvalidStateError"],
    ["unsupported_operation", "NotSupportedError"],
    ["unsupported_algorithm", "NotSupportedError"],
    ["security_insecure_origin", "SecurityError"],
    ["security_invalid_rp", "SecurityError"],
    ["security_public_suffix", "SecurityError"],
    ["security_idn", "SecurityError"],
    ["security_frame", "SecurityError"],
    ["security_permissions_policy", "SecurityError"],
    ["security_sender", "SecurityError"],
    ["operation_aborted", "AbortError"],
  ]);
  const messages = new Map([
    ["NotAllowedError", "WinVaultWarden 未完成此通行密钥请求。"],
    ["InvalidStateError", "该网站已存在被排除的通行密钥。"],
    ["NotSupportedError", "WinVaultWarden 不支持此通行密钥请求。"],
    ["SecurityError", "通行密钥请求未通过来源安全检查。"],
    ["AbortError", "通行密钥请求已取消。"],
  ]);

  Object.defineProperty(globalThis, "WinVaultWardenErrorMap", {
    configurable: true,
    value: Object.freeze({
      // Only client_not_handled means "the App is not handling this"; every other code is terminal.
      shouldFallback: (response) =>
        response?.ok === false && response?.error?.code === "client_not_handled",
      toException: (code) => {
        const name = names.get(code) ?? "NotAllowedError";
        return new DOMException(messages.get(name), name);
      },
    }),
  });
})();
