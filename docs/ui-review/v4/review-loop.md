# v4 review loop

Status: complete. Two consecutive complete native/design rounds produced zero new actionable suggestions after the final production change.

The stopping rule is two consecutive completed native/design review rounds with no new actionable suggestions. Any subsequent production UI change resets that count.

## Verification evidence

Build identity, test results, native screenshots, and the completed round ledger are recorded below. Historical v3 binaries and screenshots are not evidence for this change. The interrupted original session's partial rounds are discovery evidence only. Generated evidence remains local under the ignored artifacts directory; the scripts reproduce it in an authorized verification environment.

## Inherited discovery findings

The original session produced an incomplete native matrix and identified the following items. All were checked on the final build:

- Center the password-history dialog and use a neutral Close button.
- Label historical-password clipboard feedback as an old password rather than its replacement date.
- Show the positive status icon for an analyzed login without an active finding.
- Remove spacing reserved for unused Send editor grid rows.
- Indicate the selected Send draft row.
- Preserve the reference checkbox text spacing.

## Continuation findings

- Keep two-factor provider status text immediately after its title while retaining long-title truncation.
- Offer folder creation from the cipher editor, preserve the draft, and select the returned server folder ID.
- Apply the configured verification policy to each historical-password reveal. A master-password fallback must not open a nested ContentDialog.

## Native review environment

Local Windows verification continues the explicit authorization recorded in the original task. Native visual verification uses an opt-in, isolated test host with synthetic fixtures and the production XAML/control code. It does not automate the user's password manager window, system authentication, or real account data. Its build provenance and limitations must be recorded with the actual results.

## Discovery results

The bootstrap runs exposed additional layout problems: clipped navigation counts, a Send name field squeezed by header actions, and a password settings form constrained to zero width. These were fixed before the complete discovery matrix.

`discovery-1-20261110T043641625Z` completed normally with stable source fingerprints. It produced 306 client captures, 85 native popup layers, and 2,512 passing semantic assertions across three viewports and two themes. Independent visual review still found six actionable deviations:

1. A website path accumulated again after detail/editor/history transitions. Both field components now retain their Run objects and update their text without mixing TextBlock.Text with inline collection replacement.
2. Password-history dates used a generic slash format with seconds. They now follow the localized reference presentation and retain timestamp precision to the minute.
3. The server selector lost its selected appearance when focus moved to the email field.
4. The empty registration password hid its strength placeholders and minimum-length hint.
5. Authentication retained an earlier blue logo instead of the reference's theme-aware lock tile.
6. A new file Send placed its size limit before the file-selection action.

This discovery run does not count as a clean round. The final matrix adds a 720 × 720 viewport below the workspace's 800-pixel breakpoint, a healthy login, CopyStrip reveal/hide/clear checks, and report ignore/restore states.

`round-1-20261110T045317199Z` then completed the expanded matrix with 488 client captures and 4,462 assertions. Its 456 failures all identified one further presentation defect: whitespace between adjacent XAML Run elements appeared in URLs and secret masks. The elements are now adjacent without implicit text nodes. The independent CopyStrip, healthy-login, report, and single-column checks passed in that run. It does not count as a clean round.

An initial `clean-1` attempt stopped after 87 captures and 885 passing assertions when a closing popup detached during capture. This was an incomplete fixture run. The fixture now distinguishes retired popup objects and content-free scrims from active controls; active dialog/menu capture errors remain failures. Only complete executions and completed visual reviews can be entered in the final ledger.

## Regression verification

The normal Debug application build passed. All five suites passed: App.Tests 2,218; Vault.Tests 1,698; Api.Tests 346; Crypto.Tests 349; BrowserNativeHost.Tests 132. The total is 4,743 passed, zero failed, and zero skipped. After the discovery corrections, the normal build and all 2,218 App.Tests passed again. Final build identity and exact log links are recorded in the verification summary under `artifacts/ui-review/v4`.

The final normal x64 Debug build reports zero warnings and zero errors. Binary metadata confirms zero native-review fixture types or methods, no review marker, and the packaged icon license is present. A pre-existing randomized word-count test occasionally split a legitimate hyphenated dictionary word into two parts; count-focused tests now use a space separator without changing production generation or weakening the expected word counts.

## Final round ledger

Passing semantic assertions alone do not make a visual round clean. Each accepted round requires full native execution and completed independent visual review.

| Round | Evidence directory | Coverage | Native assertions | Independent visual suggestions | Clean count |
| --- | --- | --- | --- | --- | --- |
| Discovery A | `valid-candidate-1-20261110T073452663Z` | Four viewports, two themes, 518 client captures, 72 XAML popup layers, 24 same-process native menu layers | 4,866 passed; native and controller exit code 0 | A selected-Send indicator omission was confirmed in the next review | 0 |
| Discovery B | `valid-candidate-2-20261110T074919860Z` | The identical compiled build and full eight-cell profile | 4,863 passed; native and controller exit code 0 | 1: a real selected Send row lacked the reference's accent bar | 0 |
| Final 1 | `selection-final-1-20261110T083728818Z` | Four viewports, two themes, 518 client captures, 72 XAML popup layers; selected Send marker verified through focus, back, reorder, refresh, widen, and draft states | 4,950 passed; native and controller exit code 0 | 0 | 1 |
| Final 2 | `selection-final-2-20261110T085353188Z` | The identical compiled binary, full eight-cell profile, 518 client captures, 72 XAML popup layers | 4,950 passed; native and controller exit code 0 | 0 | 2 |

These two discovery runs have identical assembly MVID `6baf9348-c7d8-4f92-809f-ee9c7bf21760` and assembly SHA-256 `8bc007f2cddf098e03e7f81186180e04069eb63c8ecce173a3f96cca2ce5aefc`. Their source fingerprint files have identical SHA-256 `569f98eee556604647e93fdf1140139d94d6d94fdf0e2d66cca5f68e4c305a7a`. Independent second-round review found that real selected Send rows had a persistent selected background but lacked the 3 × 16 DIP accent bar required by the reference; only drafts had that bar. The earlier acceptance was withdrawn, and both clean rounds must be completed again after the correction.

Both final rounds have assembly MVID `5aa4e7c1-e9fc-45d9-9861-709dadb8c124`, assembly SHA-256 `2102f089d516b05078d6620a2e3e90d527daf70b0c2dcb9406ca67ea0e3e6443`, and identical before/after/run source fingerprint SHA-256 `0ccd107090c3dd9e5df83f9b7e93e49c43ec46f1a1c07206ee298fcc89c21a19`. Process records verify native exit code 0 for PID 49404 and PID 65704, with handles retained before waiting. The second round used Run mode without recompilation. No production source changed between these two rounds.

Final evidence is retained under `artifacts/ui-review/v4/native-selftest`:

- [Round 1 manifest](../../../artifacts/ui-review/v4/native-selftest/selection-final-1-20261110T083728818Z/manifest.json) and [native process result](../../../artifacts/ui-review/v4/native-selftest/selection-final-1-20261110T083728818Z/process-result.json).
- [Round 2 manifest](../../../artifacts/ui-review/v4/native-selftest/selection-final-2-20261110T085353188Z/manifest.json) and [native process result](../../../artifacts/ui-review/v4/native-selftest/selection-final-2-20261110T085353188Z/process-result.json).
- [Round 1 contact index](../../../artifacts/ui-review/v4/native-selftest/selection-final-1-20261110T083728818Z/contact-sheets-native-harness/index.json) and [round 2 contact index](../../../artifacts/ui-review/v4/native-selftest/selection-final-2-20261110T085353188Z/contact-sheets-native-harness/index.json). These are labeled thumbnails; original PNG hashes remain authoritative.
- [Round 1 Vault/auth visual review](../../../artifacts/ui-review/v4/native-selftest/selection-final-1-20261110T083728818Z/review-selection-vault-auth-crops/index.json), [round 2 Vault/auth visual review](../../../artifacts/ui-review/v4/native-selftest/selection-final-2-20261110T085353188Z/review-selection-round2-vault-crops/index.json), and [round 2 tools/shell visual review](../../../artifacts/ui-review/v4/readonly-review/selection-final2-tools-20261110/review.json).
- [Normal build and unit-test summary](../../../artifacts/ui-review/v4/verification-summary.json). The normal x64 Debug build contains no fixture entry points or marker.

The stopping rule is satisfied within this recorded scope: four client sizes (720 × 720, 920 × 720, 1280 × 800, 1440 × 900), both themes, the listed native states and interactions, and independent comparison with the actual v4 reference. Static capture does not validate real Windows Hello hardware, server integration, DWM caption controls, system Mica, accessibility speech, or animation timing.

The first technically complete clean candidate, `clean-1-20261110T050508737Z`, had 488 client captures and 4,462 passing assertions. Independent visual review identified remaining editor density and note-copy presentation differences, an outline rather than filled authentication lock, and incorrect narrow Send empty-state captures. The editor now uses compact URI matching menus while retaining all matching rules; note copying uses an icon with its full accessible action name; authentication uses the licensed Fluent filled lock; and an empty Send refresh clears obsolete detail selection. The fixture now exercises the production search/refresh paths and device-login eligibility flow. This candidate is discovery evidence and does not count toward the stopping rule.

SSO is an existing application capability retained outside the prototype's example flow. The fixture explicitly records its missing SSO authorization provider; a disabled SSO control in that fixture is not evidence that production SSO was removed or verified. Device-login capture, in contrast, must show the supported fixture's normal eligibility result rather than bypassing prelogin by assigning a stage.

Later complete image reviews found two final details: the device-login action lacked its 16-pixel device icon, and narrow Send back navigation discarded the list selection. These are fixed. Narrow Send pane activation is independent of its retained selection; clicking the same row or pressing Enter reopens it, while same-ID refreshes update hidden detail data without leaving the list. Empty results still reset detail selection.

Native execution also exposed an authentication-window resize exception when a hidden NavigationView was opened automatically. Automatic pane changes now require an unlocked, loaded navigation view, with a deferred update on Loaded. The fixture explicitly resizes authentication from 720 to 1280 and back to verify this path.

A later native resize briefly reported a negative physical title-bar inset. Caption padding clamps this transient value to zero before constructing GridLength. The final matrix retains the immediate caption-padding update that originally exposed the exception.

Orchestration failures, including incomplete popup capture, menu-open timing, an empty progress-file read, and a process exit without a retained OS handle, were not counted as clean evidence. The final controller records the actual process exit code and source fingerprints; menu captures wait for real native loading and closing states.

## Folder response contract

The designated local Vaultwarden checkout was unavailable during the continuation. The existing folder creation response was confirmed against the official implementation: [folder routes](https://github.com/dani-garcia/vaultwarden/blob/main/src/api/core/folders.rs) and [folder JSON model](https://github.com/dani-garcia/vaultwarden/blob/main/src/db/models/folder.rs). The editor consumes the returned `id`; it does not infer a folder ID from its name.
