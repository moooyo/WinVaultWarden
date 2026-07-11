# Passkeys browser integration

WinVaultWarden stores site passkeys as Bitwarden-compatible `login.fido2Credentials`
inside login ciphers. The browser integration is split into two processes:

1. The Chrome/Edge extension detects WebAuthn requests in the page and talks to
   the local native host through Chrome Native Messaging.
2. `BrowserNativeHost` reads and writes length-prefixed JSON on stdin/stdout,
   validates the browser payload, and forwards `passkey.get` requests to the
   running WinUI app through the local `WinVaultWarden.PasskeyBridge` named pipe.
3. The WinUI app resolves the request against the unlocked in-memory vault,
   asks the user to confirm the specific passkey use, signs the WebAuthn
   assertion, and returns the credential response through the pipe.

The extension injects `src/page-webauthn.js` at `document_start`. That page
script wraps `navigator.credentials.create()` and `navigator.credentials.get()`
only when a `publicKey` option is present. When WinVaultWarden is not running,
the vault is locked, no matching credential exists, or passkey creation is
requested, the page falls back to the browser's native WebAuthn implementation
so websites are not broken by the extension being installed.

Current native message envelope:

```json
{ "id": "request-id", "type": "ping", "payload": {} }
```

Current response envelope:

```json
{ "id": "request-id", "type": "pong", "ok": true, "payload": { "name": "WinVaultWarden.NativeHost", "version": "0.1.0" } }
```

Reserved passkey request types:

- `passkey.create`: future WebAuthn `navigator.credentials.create()` bridge.
- `passkey.get`: WebAuthn `navigator.credentials.get()` assertion bridge.

`passkey.create` payload is normalized by the page script before it reaches the
native host:

```json
{
  "origin": "https://example.com",
  "rpId": "example.com",
  "challenge": "base64url",
  "rp": { "id": "example.com", "name": "Example" },
  "user": { "id": "base64url", "name": "user@example.com", "displayName": "User" },
  "pubKeyCredParams": [{ "type": "public-key", "alg": -7 }],
  "excludeCredentials": [{ "id": "base64url", "type": "public-key", "transports": ["internal"] }]
}
```

`passkey.get` payload:

```json
{
  "origin": "https://example.com",
  "rpId": "example.com",
  "challenge": "base64url",
  "allowCredentials": [{ "id": "base64url", "type": "public-key", "transports": ["internal"] }],
  "userVerification": "preferred",
  "mediation": "conditional"
}
```

Bitwarden stores the FIDO2 private key in `login.fido2Credentials[].keyValue`
as PKCS#8 private key bytes encoded with unpadded base64url.

Native host registration:

1. Load `browser-extension` as an unpacked extension in Chrome or Edge.
2. Copy the generated extension id from `chrome://extensions` or
   `edge://extensions`.
3. Run the installer from the repository root:

```powershell
.\browser-extension\native-messaging\install-native-host.ps1 `
  -ExtensionId <32-character-extension-id> `
  -Browser Both `
  -RuntimeIdentifier win-x64
```

The installer publishes `src/BrowserNativeHost/BrowserNativeHost.csproj`,
generates a native messaging manifest under `%LOCALAPPDATA%\WinVaultWarden`,
and registers it in HKCU for Chrome and/or Edge. Use
`uninstall-native-host.ps1` to remove the registry entries.

Local smoke test:

1. Run the WinVaultWarden Debug app and open the demo vault. The demo vault
   includes a discoverable passkey for `rpId=localhost`.
2. Serve the test page:

```powershell
python -m http.server 8787 -d browser-extension\test-pages
```

3. Open `http://localhost:8787/passkey-get.html` in the browser with the
   extension installed.
4. Click `Request passkey`. WinVaultWarden should show the passkey confirmation
   dialog, then the page should print a WebAuthn assertion JSON response.

The host currently answers `ping`, forwards `passkey.get` to the running
WinVaultWarden app, and returns `not_implemented` for `passkey.create`.

## Mutual authentication and portable signer-pinning

Before any bridge traffic, the App (named-pipe server) and the native host
(pipe client) mutually authenticate. Each side derives the peer PID from the
pipe kernel object (never a self-reported hello), then reads the peer process'
token SID, canonical image path, and MSIX package family, and requires:

1. the peer runs as the **same interactive user** (same token SID), and
2. the peer's normalized executable path equals the expected portable path, and
3. **both packaged** with the same package family, **or both portable** with an
   acceptable Authenticode signature whose **signer public-key SHA-256 matches**.

For portable builds this is **signer-pinning**: `App.exe` and
`BrowserNativeHost.exe` are signed by the same signer, and each side pins the
other's signer `SubjectPublicKeyInfo` SHA-256 (compared in fixed time). A
signature is "acceptable" only when `WinVerifyTrust` returns `S_OK` **or exactly**
`CERT_E_UNTRUSTEDROOT` — i.e. the binary is signed and its digest is intact, but
the chain terminates in a root this machine has not been told to trust. That is
the whole point: **users do not install or trust the signing certificate**, and
the certificate is **not shipped in the portable zip**. Unsigned
(`TRUST_E_NOSIGNATURE`), tampered (`TRUST_E_BAD_DIGEST`), unknown-subject,
expired, revoked, and every other status are rejected fail-closed. This does not
widen to "any self-signed binary" or "anything with a certificate": the pinned
signer SPKI must still match on both ends.

The acceptability rule is the pure, unit-tested
`Core.BrowserIntegration.AuthenticodeTrustDecision`; both readers share it so the
App and host cannot drift. There is no debug/env/setting switch to bypass it.

### Verifying signer-pinning on a real machine

`dotnet test` cannot exercise the Win32 `WinVerifyTrust`/SPKI path against signed
binaries. To confirm two portable binaries are pinned to the same signer, run on
Windows against the extracted portable build:

```powershell
.\browser-extension\native-messaging\verify-signer-pinning.ps1 `
  -AppPath 'D:\WinVaultWarden\App.exe' `
  -HostPath 'D:\WinVaultWarden\BrowserNativeHost.exe'
```

It computes each binary's signer SPKI SHA-256 (the same value the readers pin),
reports whether the digests are acceptable, and exits non-zero unless both are
signed and their signer public keys match. It is a diagnostic aid, not a
substitute for the app's in-process check.
