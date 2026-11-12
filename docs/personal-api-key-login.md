# Personal API key sign-in

The sign-in page offers a personal API key flow after the account email and server
are selected. It requires the `user.<account UUID>` client ID, its client secret,
and the account's master password. Organization API keys are not vault sign-in
credentials and are rejected before making a request.

## Server contract

The implementation was checked against Vaultwarden commit
`fa2566d14fc745937ce104011475eca9e6c7a6f6`, `src/api/identity.rs`,
`api_key_login` and `user_api_key_login` (lines 584-725 in that snapshot).

The identity request is form encoded at `POST /identity/connect/token`:

- `grant_type=client_credentials`
- `scope=api`
- `client_id=user.<account UUID>`
- `client_secret=<personal API secret>`
- `deviceIdentifier`, `deviceName`, and `deviceType=6`

No username, master password, master-password hash, or refresh token is sent in
this request. The server validates the API secret and returns the access token,
encrypted account keys, and KDF/unlock data. Personal API keys bypass the server's
two-step login challenge by design; protecting the API secret is therefore
essential. This flow does not claim organization-key support.

The authenticated response's subject must equal the account UUID in `client_id`,
its email must match the selected account, and its scope must be exactly `api`.
The response's master-password unlock data drives local key derivation and
decryption. The normal V1/V2 cryptographic validation, downgrade protection,
account-deletion coordination, authentication-attempt binding, encrypted cache,
and vault bootstrap remain in effect. Accounts without a master-password unlock
method require their supported SSO/device flow instead.

## Token lifetime and saved state

Vaultwarden deliberately does not issue a refresh token for this grant. The
application does not invent one, persist the API secret, or silently repeat the
API-key exchange. A successful record carries `IsApiKeySession=true` and an empty
refresh token; that combination is accepted only for a valid master-password
unlock record. Ordinary password/SSO records still require their refresh token.

The access token stays in session memory. After it expires, the user must sign in
again to perform online operations. Locking or restarting clears the access token.
The saved encrypted cache may still be unlocked offline with an eligible local
unlock method. Returning online requires explicit API-key sign-in again.

The API secret is cleared from the sign-in form after every submitted attempt,
when navigating back, when the page is deactivated, and during lifecycle-sensitive
state cleanup. Credential request diagnostics are redacted. As with other .NET
password inputs, clearing a form reference does not promise immediate erasure of
immutable managed strings.

## Verification

Formal tests cover the actual form mapper, authentication response binding,
successful local decryption/bootstrap without a refresh token, rejected keys and
passwords, abandoned/cancelled attempts, offline cache unlock, UI routing and
secret cleanup, and source-generated saved-record serialization. All execution
for this implementation occurs on the designated remote test environment; no
local verification is authorized by this document.
