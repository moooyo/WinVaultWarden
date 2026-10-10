# WinVaultWarden v3 delivery

The native UI redesign is delivered with the practical review scope documented below. Open [the comparison gallery](index.html) to review the implemented screens beside the supplied design, or download [the Windows x64 portable ZIP](../../../artifacts/delivery/WinVaultWarden-ui-v3-win-x64.zip) (62,909,931 bytes, about 60 MiB). Extract the entire archive and run `App.exe`.

## Artifact identity

| Field | Value |
| --- | --- |
| Production source | `47197b31341808d9a7c1be28d0fb326de21684b6` |
| Branch | `codex/ui-v3-handoff` |
| Build | Release, Native AOT, self-contained, unsigned Windows x64 portable |
| Framework | .NET 10; Windows App SDK 2.5.1 |
| App.exe SHA-256 | `14a8b656ebb09fbeeba390d6fd04ae25766c4659c7b754cc378f5ded886bbf6c` |
| ZIP SHA-256 | `b8c58199102a12e376a37dc57a492c0ee124c51b642e4e473bdceb7186e75619` |

The [checksum file](../../../artifacts/delivery/WinVaultWarden-ui-v3-win-x64.zip.sha256), [package verification](../../../artifacts/delivery/package-verification.json), [build metadata](../../../artifacts/delivery/ui-v3-portable/BUILD.json), and [portable instructions](../../../artifacts/delivery/ui-v3-portable/README.md) identify the exact deliverable. Archive verification passed for 311 entries: the executable hash matches the staged and executed binary; resources, browser native host, six PNG assets, instructions and licenses are included; PDBs, logs and secret review artifacts are excluded. Browser integration is included but is not registered automatically.

The later documentation commit is separate from the production source identity. Debug rounds 13/14 used temporary review-only sizing controls absent from production source; their layout captures are labeled as Debug, not Release proof.

## Verification

All 4,700 tests passed with no failures or skips:

| Suite | Passed | Log |
| --- | --- | --- |
| App | 2,198 | [App.Tests-final.log](../../../artifacts/ui-review/v3/App.Tests-final.log) |
| Vault | 1,688 | [Vault.Tests-final.log](../../../artifacts/ui-review/v3/Vault.Tests-final.log) |
| API | 333 | [Api.Tests-final.log](../../../artifacts/ui-review/v3/Api.Tests-final.log) |
| Crypto | 349 | [Crypto.Tests-final.log](../../../artifacts/ui-review/v3/Crypto.Tests-final.log) |
| Browser native host | 132 | [BrowserNativeHost.Tests-final.log](../../../artifacts/ui-review/v3/BrowserNativeHost.Tests-final.log) |

The [final Release publish](../../../artifacts/ui-review/v3/publish-release-delivery.log) succeeded with zero errors and 49 IL3058 dependency compatibility-annotation warnings. Those warnings were not suppressed. Actual Native AOT execution was verified separately.

The [native results](native-results.json) record the exact scope, image hashes, environment and cleanup. Main-page layout was reviewed in both themes and representative 1280/1024/760 client sizes. Follow-up captures verified dark copy-strip contrast, independent request contrast, Send field/toggle alignment, Settings centering, compact navigation actions and restoration of the wide pane. The final published executable authenticated the isolated synthetic account, displayed the application logo, loaded 12 actual fixture items with masked password and live TOTP, and locked to an empty authentication view. Earlier Release execution additionally exercised Send list/editor navigation. Final capture and [native manifest](native/manifest.json) distinguish these binaries precisely.

The final application and owned review tunnel were stopped after locking. No real vault credentials are present in the screenshots.

## Review coverage and limits

[The gallery](index.html) contains nine accepted native evidence images, including two from the exact final Release executable. Six earlier Generator/Report captures are displayed in a separate supplemental section; old diagnostic rounds and the superseded Release without assets are historical only. [Seventy reference captures](reference/README.md) are prototype images, never application execution evidence. Native window-capture boundaries differ from simulated reference frames; fixture names, counts, timestamps and available server capabilities also differ intentionally.

The [acceptance ledger](review-checklist.md) separates native review, source/automated checks and unexercised paths. Native SSO/2FA completion, hardware Hello/security-key/PIN enrollment, destructive account operations, browser pairing, every error state and every possible window size were not exercised. Passing unit tests do not claim those external workflows were manually validated.

## Capability and policy decisions

The [full decision table](design-differences.md) provides current behavior, reasons and concrete alternatives. The main remaining choices are:

- Keep unsupported SSH signing, website-2FA detection, extended imports/encrypted exports, Argon2id editing and key rotation unavailable until their real services are implemented.
- Retain the existing device-request unlocked-session gate; locked/tray-hidden approval needs a separate account-bound metadata lifecycle. Device location/activity and per-device session revocation remain limited by current contracts.
- Retain permanent Send deletion confirmation. Replacing a Send file creates a new link; the editor explains and confirms that effect. Send ordering uses actual revision time.
- PIN protection persists across restart, requires at least six characters and is cleared after five failures; other eligible unlock methods can remain available. The reference's master-password-first-after-restart setting is unavailable. V2 accounts currently use the master password because local Hello/PIN protectors are unsupported.
- Keep inactive language/startup/GPU/update controls honest. Copy-then-minimize is unavailable because the current minimize-to-lock policy clears clipboard data. Packaged browser integration remains restricted.

All observed fixable native and packaging defects were corrected and recaptured. These capability choices are not described as impossible future features. The [design specification](design-spec.md), [difference history](design-differences.md), and [verification plan](verification-plan.md) provide detailed context.
