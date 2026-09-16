# Fluent redesign review

Open [the optimized review workspace](index.html). Start with the vault, switch between light and dark, and use Compare to view the original native capture alongside the proposal. The [overview image](overview.png) provides a quick cross-page review.

The default vault view is now [no item selected](index.html#screen=vault-no-selection&theme=light). Sign-in, unlock, and My vault navigation enter this state without selecting the first item. The populated list remains visible and the detail pane asks the user to choose an item. The selected-item compositions remain separately available in the scene browser.

The upper navigation label is a scope summary: **Vault scope / All vaults** means the personal and accessible organization vaults belonging to the current account. It is not an account-switch control. Account name and server host appear once in the lower account area. The misleading dropdown chevron was removed from the static scope summary.

This deliverable contains **98 representative compositions in two themes: 196 rendered PNGs**, plus editable HTML/CSS previews. The previous **178-entry inventory** is mapped completely: 93 direct representative compositions, 67 related interaction-family mappings, and 18 external/system/development boundaries. A mapping is a review reference, not a claim that every conditional state has been drawn independently or implemented.

These are UI/UX proposals. The production WinUI application and backend were not changed for this redesign. Original screenshots and the [runtime observations](../runtime-findings.md) remain the baseline.

Read the [page-by-page review and correction summary](review-summary.md) for the latest audit, per-page dispositions, and implementation boundaries.

The [third task-flow refinement](task-flow-refinement.md) preserves the scene count and adds contextual navigation, memory-only return state, visible copy actions, and clearer Send availability.

## What changed

- **Persistent context:** A compact navigation rail retains the account and exact synthetic server host. The detail breadcrumb identifies the selected item's vault and folder. Types and refinement belong to the local vault pane rather than expanding the primary navigation.
- **Predictable secret fields:** Password and TOTP rows remain present. Hidden values have explicit reveal/copy actions, readable labels, and a stable layout. A reprompt keeps the code masked until verification. Synthetic private keys and sensitive editor values remain masked by default.
- **Focused workspaces:** The generator becomes a dedicated workspace with an unclipped output and options. Settings gains local Security, Account, and Preferences navigation. Reports use actionable findings rather than a decorative security score.
- **Clear composition:** Neutral surfaces, restrained blue selection, Segoe UI Variable typography, Fluent System Icons, and consistent control geometry provide a Windows-native direction. Light and dark share the same information hierarchy.
- **Safer flows:** Dialogs separate consequences from execution, keep Cancel visible, focus it by default, trap keyboard focus, and support Escape. Account deletion retains its consequence → email confirmation → identity verification stages; the proposal shows the first stage, not a shortcut to deletion.
- **Useful states:** Empty, no-selection, loading, offline, and retryable error designs keep the user oriented. Secure notes render their content once.

See [design rationale and official references](design-rationale.md) for decisions and implementation acceptance criteria. [reference-summary.json](reference-summary.json) records the Microsoft documentation consulted. The icon license and provenance are under [assets](assets/LICENSE-icons.txt).

## Review the deliverables

| File | Purpose |
| --- | --- |
| [index.html](index.html) | Offline scene browser, before/after comparison, theme switching, review notes, and full inventory map |
| [overview.png](overview.png) | Optimized cross-page contact sheet |
| [design-manifest.json](design-manifest.json) | Scene metadata and all 178 source-to-proposal mappings |
| `frames/*.html` | Editable 1280 × 900 proposal frames; append `?theme=dark` for dark mode |
| `renders/*-light.png`, `renders/*-dark.png` | 196 static review images |
| [prototype.css](prototype.css) | Shared visual tokens, layout, controls, and interaction states |
| [render-validation.json](render-validation.json) | Full render, horizontal control-boundary, script-error, and scene-target checks |
| [review-validation.json](review-validation.json) | Current two-window-size, both-theme, interaction and offline-gallery verification |

The preview's sample navigation, search, reveal, and tab controls act locally. Copy feedback is explicitly a preview; it does not put secret data on the clipboard. Credential values, verification codes, key-like text, and identities are illustrative. Dialog actions do not register, delete, export, send, or update account data.

Long content stays in its intended scrollable body while editor/dialog actions remain visible. A PNG records the current viewport; the HTML preview exposes the complete proposed scroll region. Type-specific editor screens show representative fields. Implementing them must retain the complete existing data contract, including fields summarized under advanced options or the inventory mapping.

## Rebuild and render

From the repository root:

```powershell
node scripts/ui-review/build-redesign.mjs
node scripts/ui-review/render-redesign.mjs
node scripts/ui-review/verify-redesign.mjs
node scripts/ui-review/verify-vault-context.mjs
node scripts/ui-review/verify-task-refinements.mjs
```

The builder reads `scripts/ui-review/redesign-catalog.mjs`, `redesign-frame.mjs`, and `redesign-gallery.mjs`; the frame delegates to audited vault, authentication/settings, and tools modules. It writes the offline gallery, frames, and manifest. The renderer uses headless Edge with Playwright and Sharp from the bundled Windows runtime. Set `UI_REVIEW_NODE_MODULES` to another compatible package directory if needed.

The current review task carries explicit local-verification authorization. Future tasks must follow the repository's verification policy before running these checks locally.

## Implementation boundaries

- The CSS approximation of window material establishes the visual direction; native WinUI should use platform theme resources and Mica where appropriate.
- Generator workspace navigation is an intentional proposal change. A compact derivative can later be reused when generation begins from an item editor.
- Bank account, driver's license, and passport remain capability-dependent. No new server support is claimed.
- Windows Hello, external SSO, Duo, system security prompts, startup recovery, and development harnesses retain their ownership boundaries. Their provider-owned screens are not reconstructed.
- TOTP setup retains manual key/OTPAuth entry. The proposal does not invent a QR rendering capability or an emergency-invitation acceptance UI absent from source.
- The existing runtime defects still require implementation and regression verification. These images define expected presentation; they do not resolve the captured exceptions or missing-field bindings.
