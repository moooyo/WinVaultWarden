// MAIN-world WebAuthn shim. It is injected declaratively into the page's own realm (manifest
// `world: MAIN`), so it shares globals with the page and must treat everything it can observe as
// public. It never trusts the page for security: it strips origin, cross-origin, frame and context
// out of the wire request entirely and lets the browser (background service worker) rebuild the
// trusted context from `port.sender`. Conditional/silent mediation and non-publicKey requests bypass the
// bridge and hit the platform authenticator directly, so passive UI and password flows are never
// intercepted. It talks to the ISOLATED relay over window.postMessage using a per-frame random
// channel established by a bootstrap handshake; the channel only correlates messages and grants no
// authority, since the real origin is reconstructed by the browser downstream.
(function installWinVaultWardenWebAuthnBridge() {
  const MAIN_SOURCE = "winvaultwarden-main";
  const ISOLATED_SOURCE = "winvaultwarden-isolated";
  const coordinator = globalThis.WinVaultWardenRequestCoordinator;
  const errorMap = globalThis.WinVaultWardenErrorMap;
  const credentials = navigator.credentials;

  if (!credentials || !coordinator || !errorMap || credentials.__winVaultWardenPatched) {
    return;
  }

  const nativeCreate = credentials.create?.bind(credentials);
  const nativeGet = credentials.get?.bind(credentials);

  const bootstrapId = coordinator.randomRequestId();
  let boundChannel = null;
  const bindWaiters = new Set();
  const pending = new Map();

  function targetOrigin() {
    const origin = window.location.origin;
    return origin && origin !== "null" ? origin : "*";
  }

  function postToIsolated(message) {
    window.postMessage({ source: MAIN_SOURCE, ...message }, targetOrigin());
  }

  function bindChannel(channelId) {
    if (boundChannel) return;
    boundChannel = channelId;
    const waiters = [...bindWaiters];
    bindWaiters.clear();
    for (const waiter of waiters) waiter();
  }

  function whenBound(callback) {
    if (boundChannel) {
      callback();
      return () => {};
    } else {
      bindWaiters.add(callback);
      return () => bindWaiters.delete(callback);
    }
  }

  function failure(id, code) {
    return { v: 1, id, type: "error", ok: false, error: { code } };
  }

  window.addEventListener("message", (event) => {
    const message = event.data;
    if (event.source !== window || message?.source !== ISOLATED_SOURCE) {
      return;
    }

    if (message.kind === "isolated.ready") {
      if (message.bootstrapId === bootstrapId && typeof message.channelId === "string") {
        // Bind the first channel that echoes our private bootstrap id.
        bindChannel(message.channelId);
      } else if (!boundChannel) {
        // An isolated world announced itself before it saw our bootstrap; re-offer it.
        postToIsolated({ kind: "main.ready", bootstrapId });
      }
      return;
    }

    if (!boundChannel || message.channelId !== boundChannel || typeof message.id !== "string") {
      return;
    }
    if (message.kind === "response") {
      const resolve = pending.get(message.id);
      if (resolve) {
        pending.delete(message.id);
        resolve(message.response);
      }
    }
  });

  postToIsolated({ kind: "main.ready", bootstrapId });

  function relay(request, signal) {
    return new Promise((resolve, reject) => {
      let state = "waiting"; // waiting | sent | settled
      let deadlineTimer = null;
      let removeBindWaiter = () => {};
      const cleanup = () => {
        if (deadlineTimer !== null) {
          clearTimeout(deadlineTimer);
          deadlineTimer = null;
        }
        removeBindWaiter();
        pending.delete(request.id);
        if (signal) signal.removeEventListener("abort", onAbort);
      };
      const finish = (response) => {
        if (state === "settled") return;
        state = "settled";
        cleanup();
        resolve(response);
      };
      function onAbort() {
        if (state === "settled") return;
        const wasSent = state === "sent";
        state = "settled";
        cleanup();
        if (wasSent && boundChannel) {
          postToIsolated({ kind: "cancel", channelId: boundChannel, id: request.id });
        }
        reject(errorMap.toException("operation_aborted"));
      }

      function onBootstrapDeadline() {
        if (state !== "waiting") return;
        finish(failure(request.id, "client_not_handled"));
      }

      if (signal) {
        if (signal.aborted) {
          onAbort();
          return;
        }
        signal.addEventListener("abort", onAbort, { once: true });
      }

      const remaining = request.deadlineUnixMs - Date.now();
      if (remaining <= 0) {
        onBootstrapDeadline();
        return;
      }
      deadlineTimer = setTimeout(onBootstrapDeadline, remaining);

      removeBindWaiter = whenBound(() => {
        if (state !== "waiting") return;
        if (Date.now() >= request.deadlineUnixMs) {
          onBootstrapDeadline();
          return;
        }

        // From this point the request is owned by the extension/native operation, which carries the
        // same absolute deadline. Clear the bootstrap timer so it cannot turn an in-flight ceremony
        // into a platform fallback.
        state = "sent";
        clearTimeout(deadlineTimer);
        deadlineTimer = null;
        pending.set(request.id, finish);
        postToIsolated({ kind: "request", channelId: boundChannel, id: request.id, request });
      });
    });
  }

  async function interceptedCreate(options) {
    if (!options?.publicKey) {
      return nativeCreate(options);
    }

    const response = await relay(
      {
        v: 1,
        id: coordinator.randomRequestId(),
        type: "passkey.create",
        deadlineUnixMs: coordinator.deadlineUnixMs(options.publicKey.timeout),
        payload: mapCreateOptions(options),
      },
      options.signal,
    );
    if (errorMap.shouldFallback(response)) {
      return nativeCreate(options);
    }
    return responseToCredential(response);
  }

  async function interceptedGet(options) {
    if (!options?.publicKey) {
      return nativeGet(options);
    }
    if (options.mediation === "conditional" || options.mediation === "silent") {
      // Passive/silent flows must reach the platform authenticator directly: never surface bridge
      // UI, never open a native port, never wake the App.
      return nativeGet(options);
    }

    const response = await relay(
      {
        v: 1,
        id: coordinator.randomRequestId(),
        type: "passkey.get",
        deadlineUnixMs: coordinator.deadlineUnixMs(options.publicKey.timeout),
        payload: mapGetOptions(options),
      },
      options.signal,
    );
    if (errorMap.shouldFallback(response)) {
      return nativeGet(options);
    }
    return responseToCredential(response);
  }

  if (nativeCreate) {
    Object.defineProperty(credentials, "create", {
      configurable: true,
      value: function create(options) {
        return interceptedCreate(options);
      },
    });
  }

  if (nativeGet) {
    Object.defineProperty(credentials, "get", {
      configurable: true,
      value: function get(options) {
        return interceptedGet(options);
      },
    });
  }

  Object.defineProperty(credentials, "__winVaultWardenPatched", {
    value: true,
    enumerable: false,
  });

  function responseToCredential(response) {
    if (!response?.ok) {
      // Code-only: the shim never reads response.error.message, so backend text cannot reach the page.
      throw errorMap.toException(response?.error?.code);
    }
    if (!response.payload) {
      throw errorMap.toException("internal_error");
    }
    return decodeCredential(response.payload);
  }

  function mapCreateOptions(options) {
    const keyOptions = options.publicKey;
    return {
      rpId: keyOptions.rp?.id ?? null,
      attestation: keyOptions.attestation ?? null,
      authenticatorSelection: keyOptions.authenticatorSelection
        ? {
            requireResidentKey: keyOptions.authenticatorSelection.requireResidentKey,
            residentKey: keyOptions.authenticatorSelection.residentKey,
            userVerification: keyOptions.authenticatorSelection.userVerification,
          }
        : null,
      challenge: bufferToBase64Url(keyOptions.challenge),
      excludeCredentials:
        keyOptions.excludeCredentials?.map((credential) => ({
          id: bufferToBase64Url(credential.id),
          transports: credential.transports ?? [],
          type: credential.type,
        })) ?? [],
      extensions: {
        credProps: keyOptions.extensions?.credProps,
      },
      pubKeyCredParams:
        keyOptions.pubKeyCredParams
          ?.map((params) => ({
            alg: Number(params.alg),
            type: params.type,
          }))
          .filter((params) => !Number.isNaN(params.alg)) ?? [],
      rp: {
        id: keyOptions.rp?.id ?? null,
        name: keyOptions.rp?.name ?? null,
      },
      user: {
        id: bufferToBase64Url(keyOptions.user.id),
        displayName: keyOptions.user.displayName,
        name: keyOptions.user.name,
      },
      timeout: keyOptions.timeout ?? null,
    };
  }

  function mapGetOptions(options) {
    const keyOptions = options.publicKey;
    return {
      rpId: keyOptions.rpId ?? null,
      challenge: bufferToBase64Url(keyOptions.challenge),
      allowCredentials:
        keyOptions.allowCredentials?.map((credential) => ({
          id: bufferToBase64Url(credential.id),
          transports: credential.transports ?? [],
          type: credential.type,
        })) ?? [],
      userVerification: keyOptions.userVerification ?? null,
      mediation: options.mediation ?? null,
      timeout: keyOptions.timeout ?? null,
    };
  }

  function decodeCredential(payload) {
    return payload.attestationObject ? decodeCreateCredential(payload) : decodeGetCredential(payload);
  }

  function decodeCreateCredential(payload) {
    const credential = {
      id: payload.credentialId,
      rawId: fromBase64Url(payload.credentialId).buffer,
      type: "public-key",
      authenticatorAttachment: "platform",
      response: {
        clientDataJSON: fromBase64Url(payload.clientDataJSON).buffer,
        attestationObject: fromBase64Url(payload.attestationObject).buffer,
        getAuthenticatorData: () => fromBase64Url(payload.authData).buffer,
        getPublicKey: () => fromBase64Url(payload.publicKey).buffer,
        getPublicKeyAlgorithm: () => payload.publicKeyAlgorithm,
        getTransports: () => payload.transports ?? [],
      },
      getClientExtensionResults: () => payload.extensions ?? {},
      toJSON: () => ({
        id: payload.credentialId,
        rawId: payload.credentialId,
        response: {
          clientDataJSON: payload.clientDataJSON,
          authenticatorData: payload.authData,
          transports: payload.transports ?? [],
          publicKey: payload.publicKey,
          publicKeyAlgorithm: payload.publicKeyAlgorithm,
          attestationObject: payload.attestationObject,
        },
        authenticatorAttachment: "platform",
        clientExtensionResults: payload.extensions ?? {},
        type: "public-key",
      }),
    };

    setPrototypeIfAvailable(credential.response, globalThis.AuthenticatorAttestationResponse?.prototype);
    setPrototypeIfAvailable(credential, globalThis.PublicKeyCredential?.prototype);
    return credential;
  }

  function decodeGetCredential(payload) {
    const credential = {
      id: payload.credentialId,
      rawId: fromBase64Url(payload.credentialId).buffer,
      type: "public-key",
      authenticatorAttachment: "platform",
      response: {
        authenticatorData: fromBase64Url(payload.authenticatorData).buffer,
        clientDataJSON: fromBase64Url(payload.clientDataJSON).buffer,
        signature: fromBase64Url(payload.signature).buffer,
        userHandle: payload.userHandle ? fromBase64Url(payload.userHandle).buffer : null,
      },
      getClientExtensionResults: () => ({}),
      toJSON: () => ({
        id: payload.credentialId,
        rawId: payload.credentialId,
        response: {
          authenticatorData: payload.authenticatorData,
          clientDataJSON: payload.clientDataJSON,
          signature: payload.signature,
          userHandle: payload.userHandle,
        },
        authenticatorAttachment: "platform",
        clientExtensionResults: {},
        type: "public-key",
      }),
    };

    setPrototypeIfAvailable(credential.response, globalThis.AuthenticatorAssertionResponse?.prototype);
    setPrototypeIfAvailable(credential, globalThis.PublicKeyCredential?.prototype);
    return credential;
  }

  function bufferToBase64Url(bufferSource) {
    if (bufferSource instanceof ArrayBuffer) {
      return toBase64Url(new Uint8Array(bufferSource));
    }

    return toBase64Url(new Uint8Array(bufferSource.buffer, bufferSource.byteOffset, bufferSource.byteLength));
  }

  function setPrototypeIfAvailable(value, prototype) {
    if (prototype) {
      Object.setPrototypeOf(value, prototype);
    }
  }

  function toBase64Url(bytes) {
    let binary = "";
    for (const byte of bytes) {
      binary += String.fromCharCode(byte);
    }

    return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/u, "");
  }

  function fromBase64Url(value) {
    const base64 = value.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(value.length / 4) * 4, "=");
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }

    return bytes;
  }
})();
