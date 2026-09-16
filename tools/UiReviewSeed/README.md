# UI review fixture

This tool populates an isolated Vaultwarden instance with synthetic, encrypted data for reviewing the Windows app. It reuses the production Crypto, API, Vault, attachment, Send, and emergency-access services. It does not alter the application or existing user accounts.

## Backend

The review instance is on `test-env`:

- Container: `winvaultwarden-ui-review`
- Volume: `winvaultwarden_ui_review_data`
- Listener: `127.0.0.1:18083`
- Image: `ghcr.io/dani-garcia/vaultwarden@sha256:094b5689ed81549bd293418395c7cf495ae9d960fc2d4928cef2083ef913d912`
- Version: `1.37.2`
- Source commit: `46d71107f5094460dd5ecbe1dbac6e6c71e5189a`

The existing Bitwarden Lite service on port 18082 is independent and was left running. SMTP is not configured, so emergency invitations create review records without sending email.

The dedicated instance uses a login rate limit of one request per second with a burst of 200. This permits fixture setup and repeated UI sign-ins from the same SSH tunnel address. The default burst of 10 was exhausted during initial seeding and caused one UI sign-in to receive HTTP 429; the native UI displayed it as a credential error. The review instance was reconfigured and restarted. Existing services retain their original settings.

Provision or start the dedicated backend:

```powershell
pwsh -File scripts/Start-UiReviewBackend.ps1
```

Keep an SSH tunnel open in a separate terminal:

```powershell
ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:18083:127.0.0.1:18083 test-env
```

Seed and verify using the production client services:

```powershell
dotnet run --project tools/UiReviewSeed -- http://127.0.0.1:18083
```

The current task explicitly authorized local verification. In future tasks, follow `AGENTS.md` before running this command locally.

Verify the existing fixture without seeding or registration fallback:

```powershell
dotnet run --project tools/UiReviewSeed -- --verify-only http://127.0.0.1:18083
```

This mode requires the existing DPAPI credential, signs in, and reads ciphers, folders, Sends, emergency-access records, and devices. It writes only the redacted report `docs/ui-review/backend-verification.json`. Field-presence booleans confirm that sensitive login fields decrypt without printing their contents. Authentication failures stop verification instead of attempting registration.

## Account and credentials

Use server `http://127.0.0.1:18083` and account `review@winvaultwarden.example`.

The tool generates random master passwords in memory and stores only Windows DPAPI CurrentUser ciphertext under `.secrets/ui-review/*.password.dpapi`. This directory is ignored by Git. The main account uses `review.password.dpapi`. No password, master key, access token, or Send share URL appears in the fixture report.

Only the current Windows user can decrypt these credential files. Automation may use `ProtectedData.Unprotect` and pass the resulting text directly to the local password control in memory. Do not print the result, put it in command-line arguments, or write it to plaintext files. The seed tool itself reads the ciphertext without an interactive prompt.

## Coverage

The main vault contains 13 ciphers: 9 logins, 1 card, 1 identity, 1 secure note, and 1 SSH key. It includes:

- Four folders, including one empty folder.
- Five favorites, one archived login, and one deleted login.
- TOTP, multiple URIs, password reprompt, password history, and text/hidden/boolean fields.
- Weak and reused sample passwords, a login without TOTP, and an HTTP URL.
- One encrypted text attachment.
- Two text Sends, including a disabled password-protected Send, and one encrypted file Send.
- The `Contoso Review` organization, the `Engineering` collection, and one shared login.
- Emergency contacts in Invited, Accepted, and Confirmed states.
- One incoming RecoveryApproved emergency vault containing a synthetic secure note; decryption is verified.
- Additional named devices and a pending device approval request.

Named ciphers, folders, Sends, organizations, and contacts are reused on subsequent runs. A seed run may create another session device. Pending device requests expire according to the server's normal lifetime; rerun the tool when fresh approval-state evidence is needed.

The machine-readable result is `artifacts/ui-review/backend-fixture.json`. It records counts, supported fixture states, and failures without secrets. The original seed and the extended seed both completed successfully on 2026-09-15.

## Known backend limits

Vaultwarden 1.37.2 accepts only cipher types 1 through 5 (`src/api/core/ciphers.rs`, `update_cipher_from_data`, lines 507-513). BankAccount, DriversLicense, and Passport are implemented in this client but cannot be seeded on this server. Review those screens as implementation-derived diagrams or use a compatible server separately.

This version hardcodes `isTrusted` to `false` in its device response (`src/db/models/device.rs`, line 111). A genuine trusted-device fixture cannot be created through this server's API. SSO, external identity providers, and real hardware security keys are not configured by this seed tool.

The source checkout at `D:\Code\vaultwarden` was absent initially. It was restored from the official `1.37.2` tag, matching the pinned container commit, before the bootstrap API contract was used. Organization creation follows `src/api/core/organizations.rs`: the request includes `planType: 0`, an RSA-wrapped organization key, and a collection name encrypted with that key.
