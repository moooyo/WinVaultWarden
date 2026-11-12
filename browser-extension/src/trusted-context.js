// Rebuilds the trusted request context from `port.sender` and a fresh browser frame-tree snapshot. The page can
// put anything it likes in the wire payload; none of it is read here. sender.id proves the message
// came from this extension's own content script. sender.origin is the browser's authoritative frame
// origin, while sender.url / sender.tab.url identify the frame and top-level document. Requiring the
// independently supplied frame origin and URL to agree prevents an opaque sandbox from inheriting
// trust from its non-opaque document URL. An opaque origin, a spoofed extension id, or a missing
// tab/frame/document id or incomplete ancestor chain is refused with a stable security_sender code. The returned context is
// frozen and matches Core.Passkeys.BrowserRequestContext (camelCase) on the wire.
export function buildTrustedContext(sender, runtimeId, policyAllowed, frames, createPolicyAllowed, trustedOrigins) {
  if (
    sender?.id !== runtimeId ||
    !Number.isInteger(sender?.tab?.id) || sender.tab.id < 0 ||
    !Number.isInteger(sender?.frameId) || sender.frameId < 0 ||
    typeof sender?.documentId !== "string" || sender.documentId.length === 0 ||
    !Array.isArray(frames) || frames.length === 0 || frames.length > 4096
  ) {
    throw new Error("security_sender");
  }

  const frameOrigin = canonicalOrigin(sender.origin);
  const frameUrlOrigin = canonicalOrigin(sender.url);
  if (frameOrigin !== frameUrlOrigin) {
    throw new Error("security_sender");
  }
  const frameById = new Map();
  for (const frame of frames) {
    if (!Number.isInteger(frame?.frameId) || frame.frameId < 0 || frameById.has(frame.frameId)) {
      throw new Error("security_sender");
    }
    frameById.set(frame.frameId, frame);
  }
  const leaf = frameById.get(sender.frameId);
  if (leaf?.documentId !== sender.documentId || canonicalOrigin(leaf.url) !== frameOrigin) {
    throw new Error("security_sender");
  }

  const visited = new Set();
  let sameOriginWithAncestors = true;
  let topOrigin;
  let frame = leaf;
  while (frame) {
    if (
      visited.has(frame.frameId) || frame.documentLifecycle !== "active" ||
      typeof frame.documentId !== "string" || frame.documentId.length === 0
    ) {
      throw new Error("security_sender");
    }
    visited.add(frame.frameId);
    // A sandboxed ancestor can have an HTTPS document URL but an opaque origin. Its current
    // document therefore needs an independent browser-supplied runtime sender, not only a URL.
    const ancestor = trustedOrigins?.get(frame.frameId);
    if (
      ancestor?.id !== runtimeId || ancestor.frameId !== frame.frameId ||
      ancestor.tab?.id !== sender.tab.id || ancestor.documentId !== frame.documentId
    ) {
      throw new Error("security_sender");
    }
    const ancestorOrigin = canonicalOrigin(ancestor.origin);
    if (canonicalOrigin(frame.url) !== ancestorOrigin || canonicalOrigin(ancestor.url) !== ancestorOrigin) {
      throw new Error("security_sender");
    }
    sameOriginWithAncestors &&= ancestorOrigin === frameOrigin;
    if (frame.frameId === 0) {
      if (frame.parentFrameId !== -1) throw new Error("security_sender");
      topOrigin = ancestorOrigin;
      break;
    }
    if (!Number.isInteger(frame.parentFrameId) || frame.parentFrameId < 0) {
      throw new Error("security_sender");
    }
    frame = frameById.get(frame.parentFrameId);
  }
  if (!topOrigin) throw new Error("security_sender");

  return Object.freeze({
    frameOrigin,
    topOrigin,
    tabId: sender.tab.id,
    frameId: sender.frameId,
    documentId: sender.documentId,
    sameOriginWithAncestors,
    // Both same-origin and cross-origin requests must honor the document's actual policy.
    publicKeyCredentialsGetAllowed: policyAllowed === true,
    publicKeyCredentialsCreateAllowed: createPolicyAllowed === true,
  });
}

function canonicalOrigin(value) {
  const url = new URL(value);
  const loopback = url.hostname === "localhost" || url.hostname === "127.0.0.1" || url.hostname === "[::1]";
  if (url.origin === "null" || !(url.protocol === "https:" || (url.protocol === "http:" && loopback))) {
    throw new Error("security_sender");
  }
  return url.origin;
}
