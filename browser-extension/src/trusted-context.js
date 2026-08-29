// Rebuilds the trusted request context from the browser-supplied `port.sender` alone. The page can
// put anything it likes in the wire payload; none of it is read here. sender.id proves the message
// came from this extension's own content script. sender.origin is the browser's authoritative frame
// origin, while sender.url / sender.tab.url identify the frame and top-level document. Requiring the
// independently supplied frame origin and URL to agree prevents an opaque sandbox from inheriting
// trust from its non-opaque document URL. An opaque origin, a spoofed extension id, or a missing
// tab/frame/document id is refused with a stable security_sender code. The returned context is
// frozen and matches Core.Passkeys.BrowserRequestContext (camelCase) on the wire.
export function buildTrustedContext(sender, runtimeId, policyAllowed) {
  if (
    sender?.id !== runtimeId ||
    !Number.isInteger(sender?.tab?.id) ||
    !Number.isInteger(sender?.frameId) ||
    typeof sender?.documentId !== "string"
  ) {
    throw new Error("security_sender");
  }

  const frameOrigin = canonicalOrigin(sender.origin);
  const frameUrlOrigin = canonicalOrigin(sender.url);
  if (frameOrigin !== frameUrlOrigin) {
    throw new Error("security_sender");
  }
  const topOrigin = canonicalOrigin(sender.tab.url);
  return Object.freeze({
    frameOrigin,
    topOrigin,
    tabId: sender.tab.id,
    frameId: sender.frameId,
    documentId: sender.documentId,
    // Strict boolean: only the isolated world's real Permissions Policy verdict grants cross-origin
    // get. Anything other than the literal `true` is treated as "not allowed".
    publicKeyCredentialsGetAllowed: policyAllowed === true,
  });
}

function canonicalOrigin(value) {
  const url = new URL(value);
  if (url.origin === "null") {
    throw new Error("security_sender");
  }
  return url.origin;
}
