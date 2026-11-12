# Locked device sign-in request monitoring

This optional feature displays confirmed device-request metadata while the vault is locked, including minimize-to-lock and tray-hidden states. It is disabled by default. A locked notification can restore the real unlock interface; it cannot approve, deny, decrypt, or release a user key.

## Credential scope

`LockedDeviceRequestMonitor.ArmFromUnlocked` must be called with a current unlocked lease and an explicit enabled preference. It captures the current access-token binding and account routing metadata while the session is still unlocked. It retains no refresh token, user key, private key, full persisted session, or reusable approval evidence. Its access token exists only in memory and expires at the earlier of the token's expiry and two hours after capture. Offline unlock without a valid access token cannot arm monitoring.

The monitor creates a separate authenticated client constrained by the existing API-audience and redirect policy. It calls only `GET /api/auth-requests/pending` and never refreshes authentication. A failed request, unauthorized response, cancellation, expired authorization or mismatched active account stops the scope and clears its token reference, pending metadata and unlock intent. The next real online unlock may arm a new scope.

Ordinary lock invalidates the vault lease and erases its key and tokens. The explicitly enabled metadata credential scope is separate and can survive that transition. Sensitive-state cleanup still clears its displayed metadata, windows and pending unlock intent. An in-flight response from before cleanup cannot restore that presentation.

The host must synchronously call `LockedDeviceRequestWindowService.Stop` before opt-out, logout, remote logout, credential change, account switching or process exit. The vault session has no general account-change event; periodic account checks are defense in depth and do not replace these host lifecycle calls.

## Unlock and review

The locked window contains only server-confirmed device information, the account and server, the RSA-public-key fingerprint phrase, and expiry. It opens without activation and offers **Unlock and review** and **Later**. It has no approval action or selected fingerprint-confirmation checkbox.

The unlock button records a one-use intent bound to the monitoring epoch, account, complete server endpoints and reviewed request. The host restores its real unlock interface for that account. It must not simulate unlock, automatically submit a master password, approve while locked, or restore a different saved account.

After the real unlock completes, `OnUnlockedAsync` consumes the intent before replacing the old monitoring scope. Using the new unlocked lease, it reloads both the pending list and the authenticated single-request endpoint. Request ID, canonical RSA-2048 SPKI, derived fingerprint, creation and expiration, device type and IP must agree with the reviewed metadata. Missing, duplicate, malformed, processed, expired or changed requests fail closed. Cleanup, another account, another monitoring epoch or cancellation while either read is pending revokes the result.

Only then does the service open the ordinary unlocked approval window. The user must review the fingerprint and confirm again. `DeviceApprovalDecisionService` reloads the pending request again immediately before the existing key-wrapping approval operation. No fingerprint checkbox state or prior approval grant is reused.

Vaultwarden's approving-device API does not expose the request's `accessCode`. It is supplied by the requesting device and used on its anonymous response endpoint. This implementation therefore does not invent an access code or claim to compare one during approval. The source contract was checked against Vaultwarden snapshot `fa2566d14fc745937ce104011475eca9e6c7a6f6`, `src/api/core/accounts.rs` and `src/db/models/auth_request.rs`.

## Host integration

- Register the monitor with the real `VaultSession`, `IApiClientFactory`, `IVaultApiClientProvider`, `FingerprintService` and a `canPresent` callback that rejects lifecycle transitions.
- Construct `LockedDeviceRequestWindowService` on the UI thread and register its sensitive-state cleanup. Its windows also register with the existing transient-operation registry.
- Configure `UnlockRequested` to restore and activate the actual unlock page for the supplied account binding. It must not call an approval method.
- After successful `ShowVault`, call `OnUnlockedAsync(lease, enabled)`. This performs fresh review first, then captures a new scope if enabled and online.
- After an ordinary lock completes, call `OnLockedAsync` to refresh metadata immediately; periodic polling also handles new requests.
- Apply the explicit stop calls listed above before destructive account or application lifecycle transitions.

## Verification

The remote test suite uses real `ApiClientFactory` and authorization handling with a controlled HTTP transport. It covers opt-in, token and audience binding, lock retention, failure cleanup, metadata validation, unlock revalidation, request changes, expiry, cancellation and stale-response races. All observed watcher requests must remain GET requests; no write or token-refresh endpoint is permitted.

Headless tests do not establish native WinUI presentation, physical Windows Hello behavior or the end-to-end two-device approval ceremony. Those remain Windows-native acceptance requirements.
