# Account email and session management

The account-management page exposes email verification, email changes and all-session revocation.
It uses the active account's exact endpoint binding. Master passwords stay in memory and only the
derived authentication hash is sent to the server.

## Server contract

The implementation was checked against Vaultwarden commit
`fa2566d14fc745937ce104011475eca9e6c7a6f6`, `src/api/core/accounts.rs`, routes:

- `POST /api/accounts/email-token`: `masterPasswordHash`, `newEmail`.
- `POST /api/accounts/email`: `masterPasswordHash`, `newEmail`, `key`,
  `newMasterPasswordHash`, `token`.
- `POST /api/accounts/verify-email`: authenticated, no request body.
- `POST /api/accounts/verify-email-token`: `userId`, `token`.
- `POST /api/accounts/security-stamp`: `masterPasswordHash`.

Email changes derive a new master key using the normalized new email address and unchanged KDF,
then re-protect the same user key. The current master-password hash uses the existing explicit
salt, which may differ from the displayed email address.

The checked server email route does not update V2 signed account-security state. V2 email changes
are consequently unavailable in this client until a compatible contract is negotiated. Verification
and master-password-authorized session revocation remain available for V2 accounts.

## Security-changing outcomes

Email changes and security-stamp revocation invalidate every server session, including the current
device. Offline devices cannot be remotely erased; they learn about revocation when reconnecting.
The UI describes this scope explicitly.

Once dispatched, a security-changing request is not automatically retried and uses the bounded HTTP
control-plane deadline rather than a caller cancellation token. A lost response or server error can
mean that the operation committed. The result is then explicitly uncertain, and the original local
session is invalidated and its saved credentials/cache removed when that lease is still current.
An old response never clears a subsequently activated account. The application lifecycle performs
the corresponding view/clipboard cleanup using the receipt's bound invalidated session.

Verification mail delivery is controlled by the server's SMTP configuration. A successful request
means the server accepted the request; it does not prove that the mail was delivered. Verification
can be completed by following the email link, or by pasting its token/link into the page. Links are
parsed locally; the client never follows a pasted URL or sends a token to the pasted URL's host.
