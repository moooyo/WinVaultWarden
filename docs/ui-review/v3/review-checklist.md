# v3 design review ledger

This ledger describes the evidence actually gathered. `Native reviewed` means the linked application scene was inspected; `Source + automated` means implementation and relevant regression coverage were reviewed; `Not exercised` identifies an external or interactive path that was not run. These labels do not imply exhaustive acceptance of every variant. The user authorized local native Windows verification for this task.

Production source is `47197b31341808d9a7c1be28d0fb326de21684b6`. Debug review captures and the final Release artifact have separate identities in [the native manifest](native/manifest.json) and [delivery evidence](README.md). [Reference captures](reference/README.md) never count as application execution evidence. Earlier diagnostic rounds remain historical.

All 4,700 automated tests passed: 2,198 App, 1,688 Vault, 333 API, 349 Crypto and 132 Browser native host. They cover presentation state, lifecycle/authorization, request identity, clipboard policy, generator rules, API requests and real write/reconciliation behavior. Native screenshots add layout evidence; neither category substitutes for an unrun hardware or external-service workflow.

## Evidence matrix

| ID | Surface or behavior | Disposition and evidence |
| --- | --- | --- |
| SH-01 | Title bar, tabs, pane, content layer, status | Native reviewed at 1280: [Vault](native/round12-1280-vault-login-dark.png), [Generator](native/round12-1280-generator-dark.png), [Report](native/round12-1280-report-dark.png). |
| SH-02 | Search routing, clearing, Ctrl+F | Source + automated: actual query state clears across Send; Receive routes to Vault; modal/edit guards retained. Full shortcut sequence not separately recorded. |
| SH-03 | Sync/server/clipboard/grace | Source + automated; [native copy relay/countdown](native/round9-vault-copy-dark.png) observed. Sync failure/full grace expiration not manually timed. |
| SH-04 | Settings/auxiliary navigation | Native Account/Security observed; remaining sections source-reviewed. Centering correction tracked as V05. |
| SH-05 | Theme/type/icons/identity | Native Vault/Generator/Report light/dark reviewed. Black dynamic foreground fixed in round 12; request V02 corrected in round 13. |
| AD-01 | Responsive breakpoints | Native reviewed at 1280, 1024 and 760. Source breakpoints reviewed at 640/800/1000/1200/1280/1680; 640/900/1200/1680 not separate manual screenshots. |
| AD-02 | Compact pane/actions/overlay | Native review exposed V06 absent action header. Hamburger/New/overlay natively confirmed in rounds 13/14; exhaustive dismissal not exercised. |
| AD-03 | Single-column/back/search | Native reviewed: [Vault list](native/round12-760-vault-list-dark.png), [Send detail](native/round12-760-send-detail-dark.png), [editor](native/round12-760-send-editor-dark.png). Dirty/back/query guards source + automated. |
| VA-01 | List/header/rows/tiles | Native 1280/760 reviewed. Blank grouping/oversized title fixed: [latest wide list](native/round12-1280-vault-login-dark.png). |
| VA-02 | Recent-use snapshot/counters | Source + automated; copy updates metadata while stable snapshot prevents immediate row movement. |
| VA-03 | Copy relay/TOTP | Native [readable strip](native/round12-1280-vault-login-dark.png) and [copy/next state](native/round9-vault-copy-dark.png); actual TOTP/authorization covered by tests. |
| VA-04 | Other item types | Source + automated projections/copy mapping; identity seen in diagnostic capture. Every type not separately recaptured. |
| VA-05 | Property rows/no duplicate fields | Native login detail reviewed; 112/*/Auto columns and remaining groups source-reviewed. Final source includes minor group-spacing refinement. |
| VA-06 | Inline edit/new/generator handoff | Source + automated routing/draft/validation. Full manual create cycle for every type not performed. |
| VA-07 | Dirty confirmation/cancel/tab retention | Source + automated; cached draft and stale-callback guards independently reviewed. |
| VA-08 | Archive/trash/restore/delete | Source + automated write/reconciliation/authorization coverage. Destructive operations not repeatedly executed for screenshots. |
| VA-09 | Empty/search/loading/error states | Source + automated. Native populated list confirmed; every error not manually forced. |
| VA-10 | Reprompt/sensitive lifecycle | Source + automated: all fields authorized, rejected reads cannot reuse stale cleartext, lock cleanup retained. |
| VA-11 | Extended functions preserved | Source + automated types/organizations/folders/bulk/saved-view review. Native flat navigation observed; every command not replayed. |
| GE-01 | Layout/value/strength | Native matched [light](native/round12-1280-generator-light.png), [dark](native/round12-1280-generator-dark.png), [1024](native/round12-1024-generator-dark.png). |
| GE-02 | Modes/options/history | Source + automated; native generated value/copy observed. Username/advanced settings intentionally retained. |
| GE-03 | Wordlist/randomness/policy | Source + automated cryptographic generator/policy checks; no simulated random algorithm used. |
| RE-01 | Priority/categories/counts | Native [1280](native/round12-1280-report-dark.png)/[1024](native/round12-1024-report-dark.png); unique priority counts and independent HTTP source-reviewed. |
| RE-02 | Repair/session completion | Source + automated projections/routes including HTTP editor. Live website password-change workflow not exercised. |
| RE-03 | Honest exposure/2FA claims | Native exposure-not-checked state shown. No unsupported site-2FA claim; C02 applies. |
| SE-01 | Send list/detail/state | Native [1024](native/round12-1024-send-detail-dark.png)/[760](native/round12-760-send-detail-dark.png) reviewed. Real fixture data differs intentionally. |
| SE-02 | Inline draft/link copy | Native file editor reviewed; source + automated text/create/copy and late-load guards. |
| SE-03 | Date/access/password limits | Source + automated validation/service contracts; not every error manually forced. |
| SE-04 | Receive/hidden content/lock | Source + automated receive/decryption/cleanup; hidden text requires reveal. External received-link variants not all manually replayed. |
| SE-05 | Delete/replacement/undo | Source + automated transactions/reconciliation. Permanent deletion confirmed without fake Undo (C08). |
| ST-01 | Sections/settings rows | Source-reviewed; native Account/Security seen. V05 centering natively confirmed in round 14. |
| ST-02 | Account/KDF/delete safeguards | Source + automated real KDF, Argon2 gate, effective password policy and multistep delete. Account deletion not executed. |
| ST-03 | Local security settings | Source + automated and native availability text. Hardware enrollment/V2 limits under C13/C14. |
| ST-04 | Two-step/recovery/emergency | Source + automated state/API/auth; real QR/inline keys source-reviewed. External SMTP/FIDO2/emergency actions not exercised end to end. |
| ST-05 | Import/export | Source + automated actual JSON/CSV paths. Format/protection/folder limits explicit (C03). |
| ST-06 | About/logs/source/licenses | Real metadata/actions source-reviewed. Update action opens releases, no false latest claim (C12). |
| AU-01 | Auth/lock layouts | [Round-13 lock](native/round13-1280-unlock-dark.png) native reviewed; old 841x501 mislabeled image historical. Sign-in/register column source-reviewed. |
| AU-02 | Authentication flows | Source + automated real auth/hint/HIBP/Hello. External IdP/Duo/SMTP/security-key variants not all exercised. |
| AU-03 | PIN/restart/cancellation | Source + automated lifecycle/policy. PIN persists, minimum six, five failures clear PIN without full logout; reference restart gate unavailable (C13). |
| OV-01 | Dialog/menu/form states | Source + automated lifecycle/state; native edit/request surfaces observed. Every form variant not separately captured. |
| OV-02 | Notices/accessibility | Source-reviewed five/eight-second lifetimes and actual clipboard metadata; native copied state seen. Screen-reader cadence not manually audited. |
| EX-01 | Independent request window | Native sized surface observed; theme V02 natively confirmed corrected in round 13. Source prevents default approval. Locked/tray-hidden presentation unavailable (C11). |
| EX-02 | SSH signing | Capability C01: no signing-agent service or synthetic approval claimed. |
| EX-03 | Device request decisions | Source + automated identity/lease/decision checks; actual window observed. Final two-device successful approval not performed. |
| AC-01 | Keyboard/focus/selection | Source + automated guards, basic native navigation. Exhaustive assistive-technology/accelerator testing not performed. |
| AC-02 | System animations | Source-reviewed animation guards; native page/theme transitions. Reduced-motion setting not manually toggled. |
| AC-03 | Capture/clipboard policy | Source + automated ownership/history/lifecycle checks. Review-only capture allowance enabled for evidence; production policy explicit. |

## Outcome boundary

The practical review covers main layouts in both themes and three representative widths, with focused automated checks for affected security and transaction paths. It does not claim every external provider, hardware authenticator, destructive operation or assistive-technology permutation was exercised. Native recaptures close V02/V04/V05/V06/V07; their status and the Release binary identity are recorded in [delivery evidence](README.md).
