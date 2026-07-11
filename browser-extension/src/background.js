// Service-worker background. It is the only component that assigns trust. Each frame's isolated relay
// opens one long-lived runtime port named "winvaultwarden-passkey-frame-v1"; this worker rebuilds the
// request context from that port's `sender` (never from the page payload), pins the ceremony origin to
// the trusted frame origin, and multiplexes every frame onto the single persistent native port. A
// wrong port name is refused, an untrusted sender is answered with a code-only security_sender, and a
// frame disconnect cancels that frame's in-flight native requests.
import { buildTrustedContext } from "./trusted-context.js";
import { sendNative, cancelNative } from "./native-port.js";

const FRAME_PORT_NAME = "winvaultwarden-passkey-frame-v1";

chrome.runtime.onConnect.addListener((port) => {
  if (port.name !== FRAME_PORT_NAME) {
    // Only the passkey frame relay may connect; refuse anything else immediately.
    try {
      port.disconnect();
    } catch {
      // Already gone.
    }
    return;
  }

  const activeIds = new Set();

  port.onMessage.addListener((message) => {
    if (message?.kind === "request") {
      void handleRequest(port, activeIds, message);
    } else if (message?.kind === "cancel" && typeof message.id === "string") {
      if (activeIds.delete(message.id)) {
        cancelNative(message.id);
      }
    }
  });

  port.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    for (const id of activeIds) {
      cancelNative(id);
    }
    activeIds.clear();
  });
});

async function handleRequest(port, activeIds, message) {
  const source = message.request;
  const id = source?.id;
  if (typeof id !== "string") {
    return;
  }

  let context;
  try {
    context = buildTrustedContext(port.sender, chrome.runtime.id, message.publicKeyCredentialsGetAllowed);
  } catch {
    postResponse(port, id, failure(id, "security_sender"));
    return;
  }

  const request = { ...source, v: 1, context };
  // Discard any page-supplied top-level origin/crossOrigin; the trusted context is authoritative.
  delete request.origin;
  delete request.crossOrigin;
  if (request.payload && typeof request.payload === "object") {
    // The App derives the ceremony origin/crossOrigin from the payload; pin them to the trusted
    // frame/top origins so a page can never assert an origin that is not its own.
    request.payload = {
      ...request.payload,
      origin: context.frameOrigin,
      crossOrigin: context.frameOrigin !== context.topOrigin,
    };
    delete request.payload.context;
  }

  activeIds.add(id);
  try {
    const response = await sendNative(request);
    postResponse(port, id, response);
  } finally {
    activeIds.delete(id);
  }
}

function postResponse(port, id, response) {
  try {
    port.postMessage({ id, response });
  } catch {
    // The frame is gone; there is nothing left to deliver.
  }
}

function failure(id, code) {
  return { v: 1, id, type: "error", ok: false, error: { code } };
}
