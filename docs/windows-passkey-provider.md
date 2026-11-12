# Windows passkey provider

WinVaultWarden implements the Windows `IPluginAuthenticator` contract in the app process. This integration is separate from the optional browser extension. It uses platform-signed CTAP2 requests, encrypted vault storage, explicit approval, and real Windows Hello or master-password verification.

## Platform contract

The implementation was checked against these Microsoft source revisions:

- [`microsoft/webauthn`, `ef82c157125a0490e05f6ea82a7adb1b8e1bad08`](https://github.com/microsoft/webauthn/tree/ef82c157125a0490e05f6ea82a7adb1b8e1bad08): `pluginauthenticator.h` and `webauthnplugin.h`.
- [`Windows-classic-samples`, `434f6002bdf9cf9829406c3ff2b33387982d6168`](https://github.com/microsoft/Windows-classic-samples/tree/434f6002bdf9cf9829406c3ff2b33387982d6168/Samples/PasskeyManager): the Passkey Manager sample.

The [official sample requirements](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/passkeymanager/) specify Windows SDK 10.0.26100.7175 or later, and Windows 11 24H2/25H2 build 26100/26200 with revision 6725 or later. This project declares the required stable ABI explicitly in managed code instead of compiling the sample's C++ headers. Runtime availability is determined from the real System32 WebAuthn exports, not a hardcoded claim based on the OS marketing version.

Provider CLSID: `f8ad5a01-0d69-47ca-a928-358d16a07575`.

Authenticator AAGUID: `04d34d55-b26f-40f5-8064-a09360fbf623`.

## Registration and activation

The COM host runs on a dedicated MTA thread. Its vtables contain unmanaged callbacks and explicit reference counting, so it does not depend on reflection-based COM wrappers in a Native AOT release.

For MSIX, the package declares an out-of-process `windows.comServer` class pointing at `App.exe --windows-passkey-provider`. For portable distributions, explicit registration writes only this provider's per-user `LocalServer32` entry. Registering an already installed portable provider refreshes the executable path after a move or upgrade. Unregister removes the Windows authenticator and the owned portable COM entry.

`WebAuthNPluginAddAuthenticator` registers the provider. Registration does not enable it: the user enables WinVaultWarden under Windows Settings, Accounts, Passkeys, Advanced options. The UI queries `WebAuthNPluginGetAuthenticatorState` and reports the actual enabled state.

## Request security

- Every create/get operation is verified against the Windows operation-signing public key returned for this CLSID. Validation happens before decoding, displaying RP information, accessing the vault, or showing authentication UI.
- The signed bytes and signature are bounded. The native Windows decoder supplies the typed CTAP request. Unsupported algorithms, extensions and PIN-token protocols are rejected rather than advertised as implemented.
- The platform and originating client are responsible for WebAuthn origin validation. A CTAP authenticator receives an RP ID and a client-data hash; the approval UI does not invent a browser origin, top origin, or process identity. A valid Windows signature authenticates the platform request; it does not independently attest to an unseen browser origin.
- The normal application UI owns login and unlocking. A platform request can restore that UI and wait for an unlock within its two-minute deadline. Passwords and user keys do not cross the COM boundary.
- Every operation receives explicit approval and a fresh verification gesture. Multiple matching accounts require an explicit choice. Item reprompt policy remains enforced.
- The original vault lease controls candidate selection, credential creation and signing. Lock, logout, account changes, cancellation and expiration invalidate the operation. The host checks the same lease again before returning the encoded response.
- Windows cancellation identifies an unpredictable active transaction ID. Cancellation can only terminate that operation; it cannot access credentials or mutate persistent state.

## Supported credential operations

Registration creates a new login item containing a discoverable ES256/P-256 passkey, saves it through the ordinary encrypted vault write service, and returns a CTAP `none` attestation only after the write commits. New credential IDs use Bitwarden's UUID storage shape. Backup flags are consistent with the shared cross-client passkey policy.

Assertion selects an exact RP/credential match, confirms the chosen account, and signs `authenticatorData || clientDataHash`. The supplied hash is not hashed as if it were client-data JSON. Zero counters remain zero for synchronized passkeys. Existing positive counters require online synchronization, are incremented and persisted before signing, and are rejected if they cannot be safely advanced. Private-key bytes are cleared after use. The response is an integer-keyed CTAP map, not a browser JSON envelope.

Only implemented resident-key, user-presence, user-verification and ES256 capabilities are advertised. PRF, hmac-secret, large-blob and PIN-token protocols are not advertised or silently accepted. A relying party requiring one of those capabilities must use a provider that implements it.

Public credential metadata is synchronized through the Windows plugin APIs for autofill. It never contains private keys, passwords or notes. Metadata updates are serialized and tied to the active session; locking or changing accounts cancels in-flight operations and removes the old account's metadata. A metadata refresh failure is reported separately from an already committed credential operation.

## Integration lifecycle

The application registers `WindowsPasskeyApprovalService`, `WindowsPasskeyAuthenticator` and `WindowsPasskeyProviderService` as singletons. Start the COM host during application launch, including COM activation. Call `SynchronizeMetadata` after unlock and successful vault synchronization. Call `OnSessionLockedAsync` on lock, logout and account switching. Stop the host when the application exits.

The normal sign-in window must remain available during platform activation. A background operation must not create an alternate password cache, suppress verification, or assume a previously unlocked account is still current.

## Verification

Cross-platform service tests cover real ES256 signatures against the supplied client-data hash, committed credential creation and its COSE public key, excluded credentials, exact RP matching, approval and UV denial, forged selection, lock during approval, metadata projection and advertised capabilities.

The Win32 platform API and Native AOT COM-host files compile in an isolated remote .NET build. After explicit authorization for local Windows verification, `WindowsPlatform.Tests` also passed 13 synthetic x64 cases using real unmanaged COM callbacks, ephemeral Windows CNG keys and the actual Windows CTAP decoders. Read-only probing found the required exports in `System32\\webauthn.dll`; `WebAuthnPlugin.dll` was absent. The production provider was unregistered and disabled, and these tests did not change that state.

Microsoft documentation names `WebAuthnPlugin.dll`, while its current sample resolves the functions from `webauthn.dll`; the adapter selects the actual System32 module exporting the complete required ABI. Synthetic tests validate both owned-memory dispatch and the real native layouts without global class registration. They do not establish that a packaged application activates successfully. Because the provider was disabled, the forged operation was rejected before approval by the state gate; validation of an OS-issued operation-signing key remains a separate acceptance step.

Native acceptance remains required in an authorized Windows environment:

1. Install a signed x64 and arm64 package and verify COM activation with the application stopped.
2. Register, query state, enable through Windows Settings, and unregister. Confirm portable path refresh after a move and package registration cleanup on uninstall.
3. Use a real relying party for create/get, single and multiple accounts, a synchronized official Bitwarden credential, excluded credentials, required UV and declined verification.
4. Lock, logout, switch accounts, cancel from the Windows prompt, close the app and expire the operation while approval or unlock is pending. No response may escape the original session.
5. Attempt unsigned or altered requests against the COM object. They must fail before any vault read or prompt.
6. Confirm Windows metadata is removed on lock/account change and updated on create/delete/sync; verify credential use after restarting the app.
7. Exercise unavailable API/OS states and verify that settings accurately report the limitation without registration success.

This provider verification did not perform global COM activation, registration, hardware verification, Windows Settings interaction or real relying-party acceptance. Those operations were outside the explicitly authorized synthetic test scope. The tests did not use a real vault, a certificate or a persisted private key.
