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
