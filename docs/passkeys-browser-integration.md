# Passkeys browser integration

WinVaultWarden stores site passkeys as Bitwarden-compatible `login.fido2Credentials`
inside login ciphers. The browser integration is split into three processes:

1. **Chrome/Edge extension** (`browser-extension/`) — detects WebAuthn requests in the
   page and relays them to the local native host over Chrome Native Messaging.
2. **`BrowserNativeHost.exe`** — a self-contained Native AOT stdio host. It validates
   the browser payload against the shared v1 protocol and forwards `passkey.create`/
   `passkey.get` to the running WinUI app over the local
   `WinVaultWarden.PasskeyBridge` named pipe.
3. **The WinUI App** — resolves the request against the unlocked in-memory vault, shows
   the user an approval dialog naming the exact origin/RP and candidate credential,
   signs or creates the WebAuthn assertion, and returns the result through the pipe.

## User decision: browser integration is portable-only

**Browser integration is supported for the portable build only. MSIX does not carry,
register, or claim `BrowserNativeHost.exe`, and the in-app setting to enable it is
disabled (and fail-closed to `false`) when the App is running packaged.**

- In both layouts the Host is resolved by the single formula in
  `Core.BrowserIntegration.BrowserNativeHostLocator`:
  `AppContext.BaseDirectory\BrowserNativeHost.exe`. The App, the installer script, and
  the pipe peer authorizer all call this one method, so none of them can drift onto a
  different path.
- The **portable** publish (`WindowsPackageType=None`) actually ships the Host next to
  `App.exe` (see `src/App/BrowserNativeHost.Publish.targets`), together with a
  `BrowserIntegration\` folder containing the install/uninstall scripts and the two
  native-messaging manifest templates.
- The **MSIX** package deliberately does not ship the Host at all, declares neither a
  `windows.fullTrustProcess` nor a `windows.appExecutionAlias` extension for it, and
  `Settings → Browser integration` is disabled and forced back to off
  (`SettingsViewModel.BrowserIntegrationSupported == false` when packaged). There is no
  server-side "unlock" for this: it is a build-time and settings-time gate, not a
  runtime toggle a user can flip.
- `scripts\verify-browser-native-host-package.ps1` asserts both halves of this in CI:
  the portable root has exactly one `BrowserNativeHost.exe` at the locator path, and the
  unpacked MSIX has none, no matching manifest category, and no textual reference to
  the Host.

## Fixed Chrome/Edge extension identities

The extension ships two **separate, fixed** identities so each browser's Native
Messaging `allowed_origins` can be pinned exactly, instead of trusting whatever ID an
unpacked/dev load happens to assign:

| Browser | Manifest       | Extension ID                         | Native manifest              |
|---------|----------------|---------------------------------------|-------------------------------|
| Chrome  | `manifest.json`      | `ehakaifefnodlknfnekcnljhljcaoclb` | `com.winvaultwarden.browser.chrome.json.template` |
| Edge    | `manifest.edge.json` | `icobpiocjfkfebioicnpmccolpofhpik` | `com.winvaultwarden.browser.edge.json.template`   |

Both IDs are deterministically derived from the `key` embedded in each manifest
(`browser-extension/extension-identities.json` records the same public keys so
`verify-browser-native-host-package.ps1` can recompute and cross-check them). Loading
either manifest unpacked reproduces the fixed ID; there is no need to copy an ID out of
`chrome://extensions` and feed it back into the installer.

## Installing / upgrading / uninstalling (real commands)

The installer and uninstaller ship inside the **portable** package at
`<PortableRoot>\BrowserIntegration\`. They take no extension id (it is fixed by the
shipped templates) and never build or copy artifacts — they only register/unregister
the Host that is already sitting next to `App.exe`.

```powershell
# Portable install (registers Chrome, Edge, or Both; default is Both):
Set-Location D:\Code\WinVaultWarden\artifacts\portable\x64
pwsh -NoProfile -File .\BrowserIntegration\install-native-host.ps1 -Browser Both

# Portable uninstall:
pwsh -NoProfile -File .\BrowserIntegration\uninstall-native-host.ps1 -Browser Both
```

- `install-native-host.ps1` defaults `-AppBaseDirectory` to its own parent folder (the
  portable root), writes a rendered `com.winvaultwarden.browser.json` manifest under
  `%LOCALAPPDATA%\WinVaultWarden\BrowserNativeHost\{chrome|edge}\`, and registers it at
  `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.winvaultwarden.browser` and/or
  `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.winvaultwarden.browser`.
- It **refuses MSIX locations loudly**: if `-AppBaseDirectory` (or its auto-detected
  parent) is under `Program Files\WindowsApps`, or a sibling `AppxManifest.xml` exists,
  it throws `"MSIX 不支持浏览器集成，请使用便携版 (portable) 安装。"` and writes nothing
  to the registry or disk.
- **Upgrade**: overwrite the portable folder with the new version and re-run
  `install-native-host.ps1`; it re-renders the manifest with the (unchanged) absolute
  Host path and overwrites the previous manifest file in place — there is no separate
  "upgrade" command.
- `uninstall-native-host.ps1 -Browser Chrome|Edge|Both` deletes only the selected
  browser's registry key (`reg.exe delete ... /f`) and its manifest file; missing
  entries are treated as success (idempotent — safe to run twice, and running it for
  one browser never touches the other's registration).
- **MSIX has nothing to install**: the same scripts exist for consistency of naming
  only, but per the portable-only decision they will refuse to run from an MSIX
  install directory. There is no MSIX-side registration to perform or undo.

## Wire protocol v1

Every message that crosses the extension↔host and host↔App boundary is versioned
(`"v": 1`) and matches `protocol/browser-passkey-v1.schema.json` exactly (validated in
`Crypto.Tests`/`BrowserNativeHost.Tests` against `protocol/test-vectors`).

**Request** (page → bridge → App):

```json
{
  "v": 1,
  "id": "3f9c2a1e4b7d6081",
  "type": "passkey.get",
  "deadlineUnixMs": 1751600000000,
  "context": {
    "frameOrigin": "https://example.com",
    "topOrigin": "https://example.com",
    "tabId": 42,
    "frameId": 0,
    "documentId": "9F1B...",
    "publicKeyCredentialsGetAllowed": true
  },
  "payload": {
    "rpId": "example.com",
    "challenge": "base64url",
    "allowCredentials": [{ "id": "base64url", "type": "public-key", "transports": ["internal"] }],
    "userVerification": "preferred",
    "mediation": null,
    "timeout": 60000
  }
}
```

**Cancel** (either side, at any time while `id` is in flight):

```json
{ "v": 1, "id": "3f9c2a1e4b7d6081", "type": "cancel" }
```

**Response** — success carries a `type`-matching `payload`; every failure is
**code-only**, never a human-readable message:

```json
{ "v": 1, "id": "3f9c2a1e4b7d6081", "type": "error", "ok": false, "payload": null, "error": { "code": "user_cancelled" } }
```

`context` is never page-supplied: the background service worker rebuilds it from the
browser's own `port.sender` (see below), and the schema's `payload` definition
explicitly forbids `origin`/`crossOrigin`/`context`/`frameOrigin`/`topOrigin` keys, so a
page can never smuggle a second, forged origin into the payload it controls.

`deadlineUnixMs` is an absolute Unix-epoch millisecond deadline computed by the
extension from the page's requested `timeout` (clamped to `[1ms, 120s]`, default 60s if
omitted/non-finite — see `request-coordinator.js`); the App/host enforce it as a hard
ceiling regardless of what the page later does with its own local timer.

## MAIN/ISOLATED worlds and the trusted sender

Manifest v3 content scripts run in two worlds, and trust is assigned strictly by which
world observed a fact — never by what the page says about itself:

- **MAIN world** (`src/error-map.js`, `src/request-coordinator.js`,
  `src/page-webauthn.js`) shares globals with the page. It patches
  `navigator.credentials.create()`/`.get()` only when `options.publicKey` is present,
  and treats everything reachable from this world — including its own `window` — as
  untrusted. It **never** reads `payload.origin` or `crossOrigin` off anything, and it
  strips its own request down before handing it to the isolated relay.
- **ISOLATED world** (`src/request-coordinator.js`, `src/content-webauthn.js`) is
  unreachable from the page's JS, but shares `window.postMessage` with it, so it also
  treats the channel as pure message correlation, not authority. It reads the frame's
  **real** Permissions Policy (`document.permissionsPolicy`/`featurePolicy`) to compute
  `publicKeyCredentialsGetAllowed`, deletes any page-supplied
  `origin`/`crossOrigin`/`context`, and forwards over one persistent
  `chrome.runtime.connect({ name: "winvaultwarden-passkey-frame-v1" })` port — never a
  one-shot `chrome.runtime.sendMessage`.
- **Background service worker** (`src/background.js`) is the only place trust is
  actually assigned. `chrome.runtime.onConnect` refuses any port whose name is not
  `winvaultwarden-passkey-frame-v1`. For an accepted port,
  `trusted-context.js#buildTrustedContext` rebuilds `frameOrigin`/`topOrigin` from
  `port.sender.url`/`port.sender.tab.url`, requires `sender.id` to equal
  `chrome.runtime.id` (this extension, not a spoofed one) and requires integer
  `tab.id`/`frameId` and a string `documentId` — an opaque (`"null"`) origin or a
  missing identifier throws and the request fails as `security_sender`. The resulting
  context is frozen and is the **only** source of `origin`/`crossOrigin` written into
  the outgoing request payload; nothing the page wrote survives.

## Persistent native/pipe sessions

Both hops are long-lived, not one request per connection:

- **Extension ↔ Host**: `src/native-port.js` opens a single lazy `connectNative`
  singleton per service-worker lifetime, sends a v1 `ping`, and only forwards real
  requests after the host's `pong`. A disconnect **before** the first `pong` (host
  never came up) collapses every waiting request to `client_not_handled` — the only
  fallback-eligible code. A disconnect **after** ready (host died mid-ceremony) fails
  every in-flight request with the terminal `operation_aborted`, so a page cannot
  silently retry against the native authenticator after the bridge was actually
  handling it.
- **Host ↔ App**: `PasskeyBridgeServer` runs one named-pipe listener for the App's
  lifetime and accepts multiple concurrent connections; each connection reads a loop of
  `request`/`cancel` frames, dispatches concurrently by `id` (duplicate in-flight `id`
  is rejected as `request_in_progress`), and on disconnect cancels every still-active
  request for that connection so no late response is ever written to a dead pipe.

## Mutual identity, nonce, and portable signer-pinning

Before any bridge traffic, the App (named-pipe server) and the native host (pipe
client) mutually authenticate. Each side derives the peer PID from the pipe kernel
object (never a self-reported hello), then reads the peer process' token SID,
canonical image path, and MSIX package family, and requires:

1. the peer runs as the **same interactive user** (same token SID), and
2. the peer's normalized executable path equals the expected portable path
   (`BrowserNativeHostLocator.ResolveCurrent()`), and
3. **both packaged** with the same package family, **or both portable** with an
   acceptable Authenticode signature whose **signer public-key SHA-256 matches**.

Only after that identity check passes does either side write a byte: the
fixed-order handshake in `Core.Passkeys.PasskeyBridgeHandshake` mints a fresh
32-byte random `sessionNonce` per server `Start()`, writes `server.hello { v,
type: "server.hello", nonce, pid }`, and requires the client to answer with
`client.hello` echoing that exact nonce (constant-time compare) and its own
already-authorized PID. Every subsequent `request`/`cancel`/`response` frame on
that connection carries the same session nonce, so a message from a different
(e.g. replayed-from-restart) session is rejected and the connection is torn
down — a restart always mints a new nonce, so nothing from a previous session
can ever validate again.

For portable builds the identity check is **signer-pinning**: `App.exe` and
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

## Request policy: HTTPS/loopback, PSL, IDN, and frame rules

`Core.Passkeys.WebAuthnRequestPolicy.Evaluate` is the single gate every request must
pass before it reaches the vault, in this fixed order:

1. **Absolute origin, nothing extra**: `frameOrigin`/`topOrigin` must parse as an
   absolute URI with no user-info, query, fragment, or non-root path, or the request is
   denied `security_insecure_origin`.
2. **HTTPS or one of exactly three loopback HTTP hosts**: `https://` is always allowed;
   `http://` is allowed **only** for `localhost`, `127.0.0.1`, or `::1` — any other
   plain-HTTP origin is denied `security_insecure_origin`.
3. **IDN normalization + A-label round-trip**: the host must be NFC-normalized, must
   IDNA-encode/decode back to itself (`GetAscii` → `GetUnicode` → `GetAscii` must be
   stable), and an already-punycoded (`xn--`) input that doesn't match its own
   recomputed ASCII form is rejected — otherwise `security_idn`.
4. **RP ownership**: for an IP-literal frame host the RP id must equal it exactly; for
   a normal hostname the RP id must equal the frame host or be a proper parent domain
   at a label boundary (`example.com` may claim `sub.example.com`, never
   `evilexample.com`) — otherwise `security_invalid_rp`.
5. **Public Suffix List rejection**: an RP id that is itself a public suffix (e.g.
   `co.uk`) is denied `security_public_suffix` (except the literal `localhost`;
   `PublicSuffixList` is refreshed via `scripts\update-public-suffix-list.ps1`).
6. **Frame rules**: `create()` is **top-level frame only** (`frameId == 0`) **and**
   same-origin (`frameOrigin == topOrigin`) — any iframe or cross-origin top-level
   create is denied `security_frame`. `get()` may run cross-origin **only** when the
   isolated world's real Permissions Policy verdict
   (`context.PublicKeyCredentialsGetAllowed`) is `true`; otherwise
   `security_permissions_policy`.

`test-pages/iframe-top.html` + `iframe-child.html` exercise rule 6 end-to-end: one
iframe declares `allow="public-key-credentials-get"` (rule passes, App is reached),
the other does not (denied before the App is ever contacted).

## `conditional`/`silent` mediation, multi-credential selection, UP/UV, Reprompt

- **`conditional`/`silent` mediation never reaches the bridge.** `page-webauthn.js`
  checks `options.mediation` before doing anything else and calls straight through to
  the native `navigator.credentials.get()` — the native port is never opened, the App
  is never woken, and no WinVaultWarden UI can appear. This is what lets autofill/
  passive UX keep working exactly as if the extension were not installed.
- **Multi-credential selection is always explicit.** When more than one stored passkey
  matches a `get()` request, `PasskeyApprovalPresentation.Build` sets
  `InitialSelection` to `null` — the WinUI approval dialog is built so nothing is
  pre-selected and the user must click a specific candidate. A lone matching credential
  may be pre-selected; more than one never is.
- **User Presence / User Verification**: the WebAuthn `userVerification` requirement
  (`required`/`preferred`/`discouraged`) from the page's `publicKey` options selects how
  hard `IUserVerificationService` tries: it first attempts a real Windows Hello gesture
  signed over a fresh random challenge (a non-null signature is the proof of gesture,
  never just "Hello is available"), and falls back to master-password verification if
  Hello is unavailable, fails, or is cancelled. `required` must produce UV=true or the
  ceremony fails `user_verification_failed`; `preferred`/`discouraged` still surface the
  same gesture UI but do not hard-fail on it.
- **Reprompt**: a login item flagged `Reprompt` in the vault gates passkey use through
  the same `IItemRepromptGate`/`ItemRepromptGate` used for reveal/copy/TOTP/edit —
  master-password re-entry is required before the credential can be used, and the
  authorization is invalidated the moment the vault session's lease changes (Lock,
  Logout, account switch).

## Fallback: only `client_not_handled` falls back to the browser

`protocol/passkey-errors.json` is the single source of truth, mirrored by
`error-map.js#shouldFallback` and `Core.Passkeys.PasskeyErrorCodes`:

| Code                              | `DOMException`      | Falls back to native? |
|------------------------------------|----------------------|------------------------|
| `client_not_handled`               | *(none — native call)* | **Yes — the only fallback-eligible code.** |
| `user_cancelled`                   | `NotAllowedError`    | No |
| `user_verification_failed`         | `NotAllowedError`    | No |
| `deadline_exceeded`                | `NotAllowedError`    | No |
| `rate_limited`                     | `NotAllowedError`    | No |
| `request_in_progress`              | `NotAllowedError`    | No |
| `credential_unusable`              | `NotAllowedError`    | No |
| `internal_error`                   | `NotAllowedError`    | No |
| `credential_excluded`              | `InvalidStateError`  | No |
| `unsupported_operation`            | `NotSupportedError`  | No |
| `unsupported_algorithm`            | `NotSupportedError`  | No |
| `security_insecure_origin`         | `SecurityError`      | No |
| `security_invalid_rp`              | `SecurityError`      | No |
| `security_public_suffix`           | `SecurityError`      | No |
| `security_idn`                     | `SecurityError`      | No |
| `security_frame`                   | `SecurityError`      | No |
| `security_permissions_policy`      | `SecurityError`      | No |
| `security_sender`                  | `SecurityError`      | No |
| `operation_aborted`                | `AbortError`         | No |

**用户拒绝及安全错误不得原生回退**: every code above `client_not_handled` throws a
stable, code-derived `DOMException` and stops — it never silently retries the page's
request against the platform authenticator. Falling back on, say, `user_cancelled`
would let a user's explicit "no" be reinterpreted by the browser as "try another
authenticator", which is exactly the confusion this table rules out. The message text
attached to each `DOMException` is fixed per name (see `error-map.js`), never the raw
host/App error string, so nothing internal (paths, stack traces, backend text) can
reach the page.

Cancellation, navigation, and every disconnect path resolve to one of the two
`AbortError`/`client_not_handled` outcomes above — see `abort-deadline.html` for a page
that exercises manual `AbortController.abort()` and the `timeout` deadline against a
live approval dialog: either path must close the App dialog immediately, save/sign
nothing, and never deliver a late result to the page.

## Scope: no popup, no options page, no password autofill

The extension is intentionally headless. Both `manifest.json` and `manifest.edge.json`
declare no `action`/`default_popup`, no `options_page`/`options_ui`, and no
`web_accessible_resources` (there is no dynamic script-injection surface to expose).
Nothing in the extension inspects password `<input>` fields, writes a username/password
pair, or implements autofill of any kind — its only job is the WebAuthn
`navigator.credentials.create()`/`.get()` bridge described above.

## Local smoke testing

1. Run the WinVaultWarden Debug app and open the demo vault (it includes a discoverable
   passkey for `rpId=localhost`).
2. Serve the test pages:

   ```powershell
   python -m http.server 8787 -d browser-extension\test-pages
   ```

3. Open `http://localhost:8787/passkey-get.html` (existing credential, `conditional`/
   `silent`/`required` mediation selector, Abort button),
   `http://localhost:8787/passkey-create.html` (registration), or
   `http://localhost:8787/abort-deadline.html` (cancellation/deadline) with the
   extension installed and click the request button.
4. For the cross-origin iframe Permissions Policy scenario, serve a second origin and
   open the top page from the **first** origin:

   ```powershell
   python -m http.server 8787 -d browser-extension\test-pages
   python -m http.server 8788 -d browser-extension\test-pages
   ```

   Then open `http://localhost:8787/iframe-top.html` — the iframe pointed at
   `http://127.0.0.1:8788/iframe-child.html` with `allow="public-key-credentials-get"`
   can reach the App; the one without it is denied `security_permissions_policy`
   before the App is ever contacted.

None of the test pages contain a password field, read a query/hash-supplied origin or
RP id, collect autofill data, or display anything beyond `error.name` and a fixed,
generic message per name — see `docs/browser-integration-acceptance.md` for the full
Chrome/Edge manual acceptance matrix these pages support.
