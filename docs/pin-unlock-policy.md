# PIN unlock and the restart policy

PIN enrollment supports both legacy and V2 account keys. A PIN-derived authenticated
envelope protects the complete account key representation, including the COSE key
identifier for V2 accounts. The saved session and its PIN envelope are protected by
Windows DPAPI. The application does not store the PIN itself.

The account-specific **Require master password after restart** option adds a service
gate to the saved PIN. When enabled, a new application session owner cannot unlock
that account with a PIN until a master password has been verified in the current
process. The check is enforced before PIN derivation and again when committing an
unlock or failure count, so changing the policy during an operation takes effect
immediately. A policy rejection does not count as an incorrect PIN.

The in-process verification marker contains only fingerprints bound to the exact
account, server endpoints, account-key version and identifier, master-key wrapper,
KDF parameters, authenticated account-key state, and current PIN protector. Nothing
from that marker is persisted. Ordinary lock retains the marker; application restart
does not. Logout, PIN removal, and the five-failure PIN lockout remove it. Changed
account encryption material invalidates the old marker. A newly enrolled PIN may
inherit an existing verification of the same master key within the current process;
enrollment from a Hello-only session cannot create that verification.

Successful password sign-in, password unlock, and an actual successful master-password
verification for a sensitive operation establish the marker. Personal API-key sign-in
also qualifies only after the supplied master password has decrypted and authenticated
the account key locally. Possessing an API key alone does not qualify. Windows Hello,
trusted-device approval, and Key Connector do not substitute for the required master
password. Accounts without a master-password unlock path cannot enable this option.

When the policy is disabled, the saved PIN retains the existing restart-persistent
behavior. When it is enabled, the PIN wrapper still exists in the DPAPI-protected saved
record: the wrapper is not memory-only. This is an application-enforced restart policy,
not a claim that the persisted cryptographic envelope can only be decrypted after a
master password has been supplied.

Five incorrect PIN attempts remove the PIN protector and its in-process authorization.
They do not remove an independently configured Windows Hello unlock method. An
organization policy that disables PIN unlock continues to take precedence.

Regression coverage includes simulated restarts with a new vault-session owner,
master-password and Hello unlock paths, policy changes, account/endpoint/key/protector
binding changes, PIN removal, and five-failure revocation.
