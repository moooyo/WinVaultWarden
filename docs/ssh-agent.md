# Windows SSH signing agent

WinVaultWarden can expose SSH keys from an unlocked vault to the Windows OpenSSH client. The agent is an explicit opt-in feature. It listens on `\\.\pipe\openssh-ssh-agent` only while the selected vault session remains unlocked.

The agent implements the identity-list and signature-request operations from [RFC 9987](https://www.rfc-editor.org/rfc/rfc9987.html). The private key remains inside the application; the pipe returns public keys or signatures, never private material. Each signature requires a separate approval followed by fresh Windows Hello or master-password verification. A key with master-password reprompt always requires a fresh master-password check. Approvals are not remembered for a process or host.

## Supported keys and operations

- Ed25519, RSA with SHA-256 or SHA-512, and ECDSA on NIST P-256, P-384 and P-521.
- One unencrypted OpenSSH private-key container, PKCS#8 private key, RSA PKCS#1 PEM or EC SEC1 PEM per vault entry. Vault encryption already protects the stored key. Encrypted PEM/OpenSSH containers cannot be unlocked by this agent.
- Exact matching between the requested public-key blob, the stored public key and the parsed private key.
- At most 256 public identities in a response, with malformed, deleted, archived and password-hidden items excluded. Duplicate public keys retain the strongest reprompt requirement.
- RSA SHA-1, DSA, security-key/certificate algorithms, unknown flags, private-key insertion/removal, lock/unlock protocol requests and agent extensions are rejected.

SSH packet lengths and key sizes are bounded before allocation. Identity replies fit OpenSSH's 256 KiB response budget, with the encoded count adjusted to the complete identities that fit. Approval has a two-minute deadline. A connection has a three-minute upper lifetime, and disconnected clients or exited opening processes cancel pending approval. Each connection is served in order. The initial implementation accepts one pipe client at a time.

## Windows and session boundaries

The server creates the first pipe instance exclusively. If the Windows OpenSSH agent or another application already owns that name, activation reports that the pipe is in use. WinVaultWarden does not stop a service, replace another agent, edit SSH configuration or change an environment variable.

The native pipe handle has an explicit current-user SID-only ACL, noninheritable server handle, and `PIPE_REJECT_REMOTE_CLIENTS`. The server retrieves the opening process ID with [GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid), retains its process handle, and rechecks the pipe and process before signing or replying. A retained handle avoids treating a recycled PID as the originally approved process. [CreateNamedPipe](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea) defines the first-instance and remote-client rejection flags used here.

The displayed executable is the process that opened the pipe. It is not proof of which process performed each later write through a duplicated handle. The approval dialog states this distinction. Neither the destination hostname nor the forwarding path is authenticated. The agent therefore does not display a claimed trusted host, offer host-specific remembered grants, or advertise [OpenSSH session-binding extensions](https://github.com/openssh/openssh-portable/blob/master/PROTOCOL.agent). Do not interpret an unsupported extension response as verified forwarding or destination restrictions.

The service captures an account-bound vault lease for each request and checks the selected key again after approval. Lock, logout, account switching or disabling the agent cancels the listener and pending approvals. Signing keys are parsed only after approval and are disposed immediately after the signature. RSA and ECDSA use disposable platform cryptographic providers; the owned Ed25519 seed and temporary decoded private-key buffers are cleared.

## Integration

Register `SshAgentApprovalDialogService` as `ISshAgentApprovalService`, `SshAgentService` as `ISshAgentRequestHandler`, and `SshAgentServer` as a singleton. The settings toggle persists only the opt-in preference; it calls `Start` only after the active vault is unlocked and handles `PipeInUse`, `VaultLocked` and `Unavailable` explicitly. Add `StopAsync` to session background shutdown. The lease cancellation also closes the native pipe independently of normal shutdown ordering.

The agent does not expose a private-key export command. `ssh-add -L` may list the advertised public keys while the feature is enabled. `ssh-add` mutation commands fail because the vault is the source of key ownership.

## Verification

Pure protocol and signing-service regression tests cover framing, malformed and unsupported operations, exact key lookup, per-use approval, key replacement during approval, client identity loss, cancellation, timeout, hidden/deleted/archived filtering and reprompt policy. Key tests verify real signatures for each supported algorithm and rejection of malformed containers and flags.

The implementation was verified through `ssh test-env` on Linux. Independent fixtures generated by the real `ssh-keygen` program covered OpenSSH Ed25519, RSA and all three ECDSA curves. An additional temporary Unix transport ran the same production protocol and signing service with synthetic test keys and a test-only approval adapter: the real `ssh-add -L` listed all five public keys, and `ssh-keygen -Y sign` plus `-Y verify` succeeded for each algorithm. The temporary server then stopped normally. These results validate protocol and cryptographic behavior; they do not validate native Windows pipe activation or WinUI/Hello execution.

Before a Windows release, record these checks in an authorized Windows environment:

1. Enable the feature with no competing pipe owner, verify `ssh-add -L`, and authenticate to a controlled SSH server with each supported algorithm.
2. With the system SSH agent already running, verify activation reports the conflict and leaves that agent untouched.
3. Confirm a denied signature, cancelled verification and wrong master password produce no signature. Confirm every signature prompts again.
4. Lock, log out, switch accounts, disable the feature, close the client or terminate the opening process while approval is visible. Verify the prompt closes and no signature is returned.
5. Test a process owned by a different user, a remote named-pipe client and a malformed/oversized request. Verify access or the request is rejected.
6. Confirm the UI accurately labels the opening process and unverified destination/forwarding state. Confirm no request history or diagnostic output contains private-key material, passwords or raw signed data.

Windows-native acceptance remains a separate release requirement.
