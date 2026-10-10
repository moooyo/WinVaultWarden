# V3 verification plan

## Authorization and environment

The user explicitly authorized local Windows builds, tests, and native UI walkthroughs during the current 2026-10-10 task. Earlier task authorizations were not reused. Initial remote discovery confirmed that `test-env` is Debian 13.7 x64, so native WinUI rendering would have been blocked there. The project-specific PowerToys Windows worker exception was not applied to this repository.

Read-only discovery observed:

| Component | Available version or path |
| --- | --- |
| Local .NET SDK | 10.0.301 |
| Visual Studio | Community 2026, 18.7.3 |
| MSBuild | `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe` |
| Windows SDK | 10.0.26100.0 |
| App target | `net10.0-windows10.0.26100.0`, x64 |
| Windows App SDK used for V3 integration | 2.5.1; upgraded from 2.2.0 after the official stable package index check |
| Pinned CsWinRT | 2.3.1 |
| Native capture/control | Computer Use `@oai/sky` through `mcp__node_repl__js` |
| Isolated backend | Existing `winvaultwarden-ui-review` container on `test-env`, healthy |
| Client endpoint | `http://127.0.0.1:18083` through a loopback SSH tunnel |
| Synthetic account | `review@winvaultwarden.example` |
| Remote fallback | .NET SDK 10.0.301 and 10.0.400 at `/root/.dotnet`; Node 22.23.2 at `/opt/node22/bin/node` |

The existing fixture was authenticated and read successfully on 2026-10-10 using the previous built `UiReviewSeed.exe` in `--verify-only` mode. The fixture report is under `artifacts/ui-review/v3/docs/ui-review/backend-verification.json`; this discovery run does not establish that the redesigned application works. The container and its data were not reprovisioned.

## Build and regression commands

Run only after the implementation tree is ready for an integrated build. The script records complete logs under `artifacts/ui-review/v3` and fails on unsuccessful exit codes.

```powershell
pwsh -NoProfile -File scripts/ui-review/Invoke-V3Verification.ps1 -Action Build
pwsh -NoProfile -File scripts/ui-review/Invoke-V3Verification.ps1 -Action Tests
pwsh -NoProfile -File scripts/ui-review/Invoke-V3Verification.ps1 -Action Build -Configuration Release
```

The native UI review build omits browser native-host packaging by setting `ShipBrowserNativeHost=false`. The legacy `BuildBrowserNativeHost=false` property does not disable the current target. A delivery build that includes the browser bridge uses `-IncludeBrowserNativeHost`; the resulting deliverable must state which packaging mode was verified.

The native executable is expected at:

```text
src/App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/App.exe
```

`App.Tests` compiles linked presentation and service source against `net10.0`; it covers both behavioral regressions and source-level XAML invariants. `Vault.Tests` covers the preserved vault operations. These suites do not replace native XAML compilation, screenshot review, or real window interaction.

## Fixture connection

Keep the isolated loopback SSH tunnel open while using the synthetic account:

```powershell
ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=30 -L 127.0.0.1:18083:127.0.0.1:18083 test-env
pwsh -NoProfile -File scripts/ui-review/Invoke-V3Verification.ps1 -Action Fixture
```

The review credential remains Windows DPAPI CurrentUser ciphertext in `.secrets/ui-review/review.password.dpapi`. Consume its plaintext only in memory for the synthetic login control. Never print a password, place it in a command argument, save plaintext credentials, or capture memory dumps. Fixture verification does not register or seed accounts when authentication fails.

`Send-V3ReviewCredential.ps1` can pass the existing synthetic password to the UI automation process over a random, single-client, current-user-only named pipe. The helper does not print the plaintext, clears its byte buffer after the pipe write, and expires after 30 seconds. The receiving automation must pass the value directly to the observed password control and clear its byte buffer after use.

## Native walkthrough

Use Computer Use after loading its skill, API, guidance, and confirmation documentation. The explicit user instruction to inspect this application's UI applies to the synthetic review application. No unrelated password manager, user account, or Windows security dialog is in scope.

Initialize `@oai/sky`, launch the verified executable, list returned app/window objects, and require one exact app window before acting. Observe a fresh screenshot and accessibility tree before every state-dependent action; take one action, then refresh. Persist screenshots as review deliverables rather than treating prototype or old screenshots as evidence.

Required representative walkthrough coverage:

1. Login, self-hosted setup, unlock, and registration navigation where implemented.
2. Vault overview and all-items selection, search, favorites, folder, organization, archive, and trash states.
3. Login detail with masked fields, protected-item verification, copy feedback, TOTP, secondary metadata, and editor open/cancel.
4. Card, identity, secure-note, and SSH-key details; backend-gated types recorded separately.
5. Send overview, text/file editor open/cancel, expiration states, and receiver entry.
6. Password, passphrase, and username generator modes, options, and history.
7. Security report summary and expandable actionable findings.
8. Import/export cards and native file dialog boundaries without destructive operations.
9. Device list, pending request presentation, emergency-access sections, and safe dialog cancellation.
10. Settings navigation, security/account/preferences/about presentation, and safe non-destructive dialogs.
11. Light and dark themes; a standard desktop size and a narrow window; keyboard focus and scrolling where layout changes.
12. Lock and return navigation, including the absence of exposed credential content after lock.

Compare screenshots with the corresponding V3 handoff state. Record each discrepancy as an implementation defect, a data/environment difference, or a product capability decision. Fix implementation defects and recapture affected states. Do not claim coverage of hidden dialogs, external-provider enrollment, hardware authentication, or destructive operations that were not exercised.

## Delivery evidence

The review is complete for the scope recorded in [native-results.json](native-results.json) and [README.md](README.md). Those files identify final build/test outcomes, source/executable/archive hashes, native screenshot mappings, corrected discrepancies, remaining capability decisions and packaging mode. This plan is retained as the execution procedure; unexercised external workflows are explicitly listed in the final results.
