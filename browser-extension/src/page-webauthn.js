(function installWinVaultWardenWebAuthnBridge() {
  const PAGE_SOURCE = "winvaultwarden-page";
  const CONTENT_SOURCE = "winvaultwarden-content";
  const FALLBACK_ERROR_CODES = new Set([
    "native_host_unavailable",
    "not_implemented",
    "vault_locked",
    "credential_not_found",
  ]);
  const credentials = navigator.credentials;

  if (!credentials || credentials.__winVaultWardenPatched) {
    return;
  }

  const nativeCreate = credentials.create?.bind(credentials);
  const nativeGet = credentials.get?.bind(credentials);

  if (nativeCreate) {
    Object.defineProperty(credentials, "create", {
      configurable: true,
      value: async function create(options) {
      if (!options?.publicKey) {
        return nativeCreate(options);
      }

      const response = await sendPasskeyRequest("passkey.create", options);
      if (shouldFallback(response)) {
        return nativeCreate(options);
      }

      return responseToCredential(response);
      },
    });
  }

  if (nativeGet) {
    Object.defineProperty(credentials, "get", {
      configurable: true,
      value: async function get(options) {
      if (!options?.publicKey) {
        return nativeGet(options);
      }

      const response = await sendPasskeyRequest("passkey.get", options);
      if (shouldFallback(response)) {
        return nativeGet(options);
      }

      return responseToCredential(response);
      },
    });
  }

  Object.defineProperty(credentials, "__winVaultWardenPatched", {
    value: true,
    enumerable: false,
  });

  function sendPasskeyRequest(type, options) {
    const id = crypto.randomUUID();
    const payload = type === "passkey.create" ? mapCreateOptions(options) : mapGetOptions(options);
    const request = {
      id,
      type,
      payload,
    };

    return new Promise((resolve) => {
      const timeout = window.setTimeout(() => {
        window.removeEventListener("message", onMessage);
        resolve(errorResponse(id, "extension_bridge_timeout", "WinVaultWarden extension bridge timed out."));
      }, 30000);

      function onMessage(event) {
        if (event.source !== window || event.data?.source !== CONTENT_SOURCE || event.data.id !== id) {
          return;
        }

        window.clearTimeout(timeout);
        window.removeEventListener("message", onMessage);
        resolve(event.data.response ?? errorResponse(id, "empty_response", "WinVaultWarden returned an empty response."));
      }

      window.addEventListener("message", onMessage);
      window.postMessage({ source: PAGE_SOURCE, request }, window.location.origin);
    });
  }

  function shouldFallback(response) {
    return !response?.ok && FALLBACK_ERROR_CODES.has(response?.error?.code);
  }

  function responseToCredential(response) {
    if (!response?.ok) {
      throw new DOMException(response?.error?.message ?? "WinVaultWarden passkey request failed.", "NotAllowedError");
    }

    if (!response.payload) {
      throw new DOMException("WinVaultWarden passkey response did not include a credential.", "UnknownError");
    }

    return decodeCredential(response.payload);
  }

  function mapCreateOptions(options) {
    const keyOptions = options.publicKey;
    return {
      origin: window.location.origin,
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
      origin: window.location.origin,
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

  function errorResponse(id, code, message) {
    return {
      id,
      ok: false,
      type: "error",
      error: { code, message },
    };
  }
})();
