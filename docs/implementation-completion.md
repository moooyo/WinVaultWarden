# Implementation completion ledger

This work follows the completeness review of source `e658374c11c3370d8f09b798a4b736d594ba6eb7` on 2026-11-12. The requested scope includes the twelve findings and the missing capabilities identified in that review. A feature is complete only when its service, user interaction, failure behavior and relevant regression coverage are connected. Source implementation, remote automated verification and Windows-native acceptance are separate states.

## Architecture and implementation order

1. Repair credential persistence, lock cleanup, V2 verification and backup restoration first.
2. Repair committed-operation reconciliation, Send presentation, recovered-vault access, password analysis and native navigation.
3. Repair browser origin/policy enforcement, passkey wire interoperability and reconnection.
4. Add encrypted backups, complete KDF transitions and local unlock, personal API-key login and account management.
5. Add opt-in SSH and Windows passkey services with real approval and platform registration, then connect startup, updates and localization.
6. Integrate and rerun the complete existing suites with the new regression cases. Keep unsupported server or operating-system contracts explicit.

## Finding remediation

| Review ID | Required behavior | Current state |
| --- | --- | --- |
| R01 | Refresh must not restore revoked PIN/Hello or overwrite concurrent credential changes | Implemented; remote credential regressions passed |
| R02 | Lock clears sensitive presentation before bounded network cleanup | Implemented; real unresponsive WebSocket and lifecycle regressions passed |
| R03 | V2 master-password confirmation validates the correct key transport | Implemented; remote account tests passed |
| R04 | V2 restores JSON backups with exported item IDs | Implemented; real V1/V2 import regressions passed |
| R05 | Every browser request respects policy and the complete trusted ancestor chain | Implemented; remote policy/extension tests passed |
| R06 | Passkey backup state interoperates while preserving existing registrations | Implemented with a documented legacy-credential boundary; remote wire tests passed |
| R07 | Cancellation after an import commits cannot repeat the import | Implemented; committed cancellation/timeout tests passed |
| R08 | Received text Send preserves hidden state and metadata | Implemented; real service-to-view-model tests passed |
| R09 | BFCache and worker reconnection do not reuse dead ports or stale responses | Implemented; remote extension lifecycle tests passed |
| R10 | Emergency view access exposes usable, gated recovered-item details | Implemented; session and late-response regressions passed |
| R11 | Password evaluation has a bounded cost and honest unassessed state | Implemented; remote crypto/report/generator tests passed |
| R12 | Folder controls observe the active view-model instance | Implemented; remote App suite passed |

## Capability expansion

| Capability | State and acceptance boundary |
| --- | --- |
| V1/V2 password and forced-password changes | Implemented and remotely tested |
| PBKDF2/Argon2id algorithm and parameter editing | Implemented; Settings integration added |
| V2 PIN/Hello protectors | Implemented and remotely tested; hardware Hello remains unverified |
| PIN master-password-first policy after restart | Implemented with a process-only, account/key/protector-bound verification marker and a persisted opt-in policy |
| Password-protected and account-restricted JSON backups | Implemented using the actual Bitwarden formats, authenticated-field validation and explicit legacy-key restrictions |
| Eight import formats and destination folders | Implemented: Bitwarden JSON/CSV, 1PUX, KeePass XML, Chrome, Edge, Firefox and LastPass; unsupported content is rejected before submission |
| Personal API-key login | Implemented; no persisted API secret or fabricated refresh token; real-server positive/negative authentication passed |
| Email verification/change and session revocation | Implemented; revocation explicitly affects all sessions, including this device |
| SSH agent | Real Windows pipe service and approval implemented; pure tests and real OpenSSH wire/signature interoperability passed; native pipe/UI acceptance pending |
| Windows passkey provider | Implemented; 13 synthetic Windows COM/ABI/CNG/decoder cases passed without changing registration; enabled-provider and hardware acceptance remain separate |
| Windows startup | Real packaged/portable backends implemented; pure tests and Windows-targeted cross-compilation passed; OS execution pending |
| Update checking | Stable-release checks, opt-in daily checks and real per-architecture MSIX AppInstaller feeds/enrollment implemented; portable installation remains manual |
| Localization | System preference plus Simplified Chinese, Traditional Chinese, English and Japanese; 2,237 typed keys, real MRT startup and localized responsive layouts verified |
| Locked device requests | Implemented as an opt-in metadata-only token scope, real unlock, fresh request revalidation and explicit revocation before account changes |
| Account-key rotation | Blocked for general safe use by missing server transaction/snapshot concurrency contract; do not enable a destructive partial implementation |
| V2 email change | The inspected Vaultwarden email endpoint does not update the V2 signed account state; gated until a supported server contract exists |
| MSIX browser bridge | Existing portable-only packaging boundary retained pending implementation and native acceptance |
| GPU preference | Requires a supported WinUI rendering control; no ineffective toggle will be represented as operational |

## Verification environment

Verification initially ran only through `ssh test-env`. Its Linux root filesystem became full during parallel work; isolated test work moved to its temporary filesystem without deleting unrelated tasks. Later local Windows execution followed the user's explicit authorization. Test logs and immutable native-review inputs are retained under ignored artifact directories.

The user explicitly authorized local Windows builds, tests and synthetic native UI verification during this task on 2026-11-12. This permission does not apply to future tasks. Native verification uses isolated build outputs and in-memory fixture services; it does not open a real saved vault, change provider registration, or publish a release. Windows-native results are recorded separately from remote and cross-compiled evidence. Hardware gestures, actual provider enablement and true Chrome/Edge/RP ceremonies require their own observed evidence. Existing historical captures are not counted as current acceptance.

## Server-side rotation constraint

The inspected Vaultwarden rotation route saves folders, emergency/recovery keys, Sends, ciphers and the user in separate steps. It has no encompassing transaction or expected snapshot precondition; cipher updates deliberately skip normal revision checking. A client journal alone cannot prevent another client's concurrent writes from being overwritten. A complete recovery journal and a supported exclusive-writer or server transaction/receipt contract are required before enabling rotation. This is a specific data-integrity dependency, not an approval requirement.

The upstream account source was rechecked on 2026-11-12 at [revision `17b159beff4b892f07f61f38ce2fe452e55251c1`](https://github.com/dani-garcia/vaultwarden/blob/17b159beff4b892f07f61f38ce2fe452e55251c1/src/api/core/accounts.rs). The rotation route still identifies the missing transaction; its email-change schema does not accept a replacement V2 signed account state. Neither feature is represented as successfully implemented through an incompatible client-only request.

## Executed integration evidence

The real-server suite passed 195 checks against a pinned Vaultwarden 1.37.2 image using a new read-only-root container, a tmpfs database and synthetic accounts. It covered password authentication, actual KDF changes, API-key authentication, vault mutations, Send, attachments, TOTP, device authorization, notifications, emergency access, all eight import sources and encrypted backup restoration. The task container and database were removed; existing services were not modified.

Windows testing includes the previously Linux-skipped DPAPI/path cases and the new synthetic native project. Native UI review uses immutable source snapshots, actual compiled XAML/PRI, six/eight viewport-theme combinations and separate language probes. Earlier failed discovery rounds remain recorded; a passing rerun does not retroactively mark them successful. Hardware gestures, actual provider registration/enabling, browser pairing and real relying-party ceremonies are not inferred from synthetic evidence.

The final verification report and exact suite counts are retained in `artifacts/implementation/20261112`. The real-server report is in `artifacts/reviews/completion-20261112/live-e2e`.

Final Windows suites passed 5,583/5,583 tests with no failures or skips; the extension suite passed 102/102 against identical current inputs. The normal native UI matrix passed 6,149 assertions across eight cells, with 147 additional three-language responsive assertions. VS MSBuild and Windows SDK MakeAppx also generated and validated a real unsigned MSIX without installing or registering it.

Synthetic optimized Native AOT startup passed 177 assertions with 12 captures across empty, single-login and full-vault cases, including real post-frame navigation refresh. Unoptimized Debug AOT had reproduced the known .NET 10 restricted-GC callback issue; supported AOT verification now explicitly enables optimization, keeping the synthetic Debug entry point isolated from normal Release output. These configuration and evidence boundaries are recorded rather than treating an unsupported debug configuration as a successful production test.
