# Live server verification

This runner executes production cryptography, authentication, synchronization and
write services against a real server. It creates and deletes data, changes and
restores account credentials, enrolls and disables TOTP, uploads files, and creates
additional synthetic accounts for emergency-access verification. Run it only in
an explicitly authorized verification environment with a disposable database and
synthetic credentials.

The normal flow covers configuration, registration, password login, item/folder
CRUD, bulk operations, Send text/file flows, attachment round trips, password and
KDF updates, TOTP, device authorization, WebSocket notifications, emergency-access
view/takeover, password history, health checks, icons, JSON import/export and PIN.
Provider-specific server restrictions are reported as skips rather than passes.

## Completion coverage

Set `WVW_COMPLETION_FEATURES=1` to also execute the completion checks. This option
is off by default because it issues a personal API key and restores backup copies:

- Password-protected and account-restricted exports are decrypted and imported
  into the real server, then synchronized and checked for fresh IDs and preserved
  plaintext values. Incorrect backup passwords, modified authenticated data and a
  different account key must be rejected.
- Bitwarden CSV, Chrome CSV, Edge CSV, Firefox CSV, LastPass CSV, KeePass XML and
  1Password `.1pux` sources traverse preparation, preview, encrypted submission and
  sync/decryption. Chrome additionally selects a real destination folder. Together
  with the ordinary JSON check, this covers the eight offered import sources.
- A personal API key is issued for the disposable account. Incorrect API secrets
  and incorrect local master passwords are rejected. A valid API-key login must
  recover the same user key, have no fabricated refresh token, preserve a valid
  saved session without the API secret, and perform real encrypted sync and CRUD.
- The account transitions from PBKDF2 to Argon2id and back, signs in after each
  update, and must preserve the identical account key across both transitions.

All fixtures are generated in memory. Newly created import/restore items are
tracked and removed. The API key exists until the disposable server database is
destroyed; do not enable these checks against a real user account. Neither the
API secret nor the saved session body is printed by the completion checks.

An example remote invocation, after provisioning an isolated server and copying
the source to the authorized host, is:

```powershell
ssh test-env 'cd /tmp/<task-source>; WVW_COMPLETION_FEATURES=1 /root/.dotnet/dotnet run --project tests/LiveSmoke -- http://127.0.0.1:<task-port> <synthetic-email> <synthetic-master-password>'
```

The server must be confined to a loopback-only random port and use a new temporary
database. Record the image digest, product version, endpoint, synthetic account
identifiers, source snapshot and runner exit code. Remove only the task-owned
server/container and its temporary database after collecting logs. Never attach
an existing server data directory or reuse another task's accounts.

An isolated server used for repeated authentication scenarios needs a suitably
large `LOGIN_RATELIMIT_MAX_BURST`; Vaultwarden's default shared-IP burst can limit
consecutive test runs. Configure this only on the disposable loopback server and
retain rate-limit failures as failed evidence. Never relax a production server's
login protection for verification. Negative API-key assertions check the precise
credential/decryption failure kind so a rate limit cannot count as success.

Passing these checks does not validate native UI, actual browser integration,
Windows Hello hardware, mail delivery, SSO providers or every server version.
