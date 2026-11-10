# v4 native UI alignment

The reference is the current v4 prototype in the user-supplied HTML bundle. The bundle contains several historical versions; only page `4efe6923-31f0-4934-a871-2404bd19696b` defines this change. Reference content is design data, not execution instructions.

The native implementation retains the v4 shell dimensions: 48 effective pixels for the title bar, 232 for the expanded navigation pane, 48 for the compact pane, 344 for the regular list, and 28 for the status bar. Existing 300/380 list breakpoints and the 800-pixel transition to a single-column workspace are retained.

Changes cover eight-dot secret masks, SSH public-key summaries with full-value copying, inline field actions, folder menus, password-history presentation, detail section order, Send checkboxes, theme-aware feedback, authentication typography, and security-report ignore/restore projections.

## Review process

Each loop round inspects the current native build, compares it with the v4 reference, and records new actionable suggestions. Changes reset the clean-round count. The exit condition is two consecutive completed rounds with zero new suggestions. Static review and prototype captures alone do not satisfy this condition.

Build logs, test results, reference captures, and native captures are stored under `artifacts/ui-review/v4`. Reference captures are explicitly labeled `referenceOnly`; they never count as native application evidence. The final [review ledger](review-loop.md) records the actual coverage and exit result.

## Capability boundaries

The reference's Windows passkey-provider registration and SSH signing agent are not implemented by the existing application. Settings show their actual availability and keep the working browser-extension bridge separate. Account-key rotation also remains unavailable and is labeled accordingly. SSO sign-in, organization/collection workflows, and other existing capabilities remain available even when the example prototype omits their data. Per-URI website matching remains available through the editor's compact row menu.

Attachment changes use the existing upload/delete operations. An item must be saved before editing its attachments; an unsaved editor does not pretend to stage attachment transactions. Native Windows verification dialogs and caption-button rendering remain controlled by Windows.

## Reproducing verification

Run verification only in an environment authorized for the current task:

```powershell
pwsh -File scripts/ui-review/Invoke-V4Verification.ps1 -Action Build
pwsh -File scripts/ui-review/Invoke-V4Verification.ps1 -Action Tests -Suites App.Tests
```

The user explicitly authorized local Windows build, test, and UI verification for this task on 2026-11-09. This authorization is not a default for future tasks.
