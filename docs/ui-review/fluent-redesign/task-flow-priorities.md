# Task-flow priorities after the page review

This assessment records the state before the third refinement. See [implemented changes and verification](task-flow-refinement.md) for the resulting design.

This is an independent review of the existing design artifact, not the native application. The six light-theme renders reviewed were `send-overview`, `vault-no-selection`, `vault-detail-login`, `vault-editor-login`, `generator-password`, and `settings-security`. The review also read `review-summary.md` and the shared shell, vault, and tools source modules. No runtime verification was performed for this review.

The visual foundation is mature enough to keep. The remaining opportunities are mainly about finding, using, and returning to credentials with less effort. A larger screen library would not resolve those issues.

## 1. Preserve the user's place across vault navigation

**Priority: high.** A user searches for a login, opens it, edits it, and returns to the results. Their query, type/favorite filter, sort order, list position, and keyboard focus should survive that round trip.

**Evidence:** Vault row clicks call `navigateSafely`, which calls the shared `navigate` function. That function changes the scene/frame. Search and filtering live in the previous frame's DOM, and no cross-scene restoration is present in the inspected modules. The existing detail-to-editor-to-detail operation instead replaces only `.v-pane-host`, which already preserves the list and should be retained.

**Change:** Keep an in-memory vault navigation context for the unlocked account. Restore the same list and selected row when entering details or returning from editing. Returning from Send or Settings should resume that workspace. An explicit fresh entry after unlocking should still show no selected item. Clear account-specific context on account change and clear sensitive search/selection on locking. Do not persist credential values to implement view restoration.

**Benefit:** The user stops repeating a search or finding their place after a routine action. This improves everyday work more than another visual variation.

**Tradeoff:** State restoration needs explicit reset rules. If an edit removes an item from the active filter, show a short explanation and preserve the filter instead of silently displaying an unrelated list.

**Review criterion:** Search, scroll, open the correct item, edit, cancel or save, and return. The query, visible result set, and scroll position remain stable; focus returns to a meaningful control. A fresh unlock still opens the no-selection view.

## 2. Make vault navigation contextual

**Priority: high.** Send, Generator, and Settings currently display `Vault scope / All vaults`, Favorites, Folders, and Organizations in their navigation rail. These controls actually lead back to vault views; they do not refine the page currently being viewed.

**Change:** Keep the primary destinations and account/lock area stable. Show vault scope, Favorites, Folders, and Organizations as the expanded context of My vault only while that workspace is active. Preserve their saved state when the user leaves. Outside the vault, remove the scope summary rather than replacing it with another explanation block. Settings should continue to use its existing local Security, Account, and Preferences navigation.

**Benefit:** Users can tell which controls change their current view. Send and Settings look calmer without losing access to the vault.

**Tradeoff:** Opening a specific folder from another workspace takes one additional navigation step. Retaining the last vault view makes that step predictable. Keep the main destination order and the bottom account area anchored so contextual content does not cause unrelated navigation to jump.

**Review criterion:** A user entering Send can identify the current workspace without encountering a vault scope that appears to affect Sends. Returning to My vault restores the prior folder or results rather than resetting to All items.

## 3. Make the most frequent credential action easier to find

**Priority: high.** The login header has a clearly labelled Edit button, while copying the password is represented by a small icon at the distant right edge of a wide field. The icons are already correctly labelled for assistive technology in source; the concern is visual discoverability and pointer effort.

**Change:** Give the password row a visible `Copy password` action and retain a quiet, separate reveal control. Use the same aligned action region for username and verification-code copying. Keep Edit in the header as a secondary action and keep destructive commands in the overflow menu. Do not duplicate a full credential toolbar in both the header and every row. On narrower views, icon-only controls can retain tooltips and accessible names. A copy action should confirm what was copied and the configured clipboard clearing behavior without exposing the value.

**Benefit:** The interface emphasizes using a saved credential, which is the repeated task. Users can copy without first revealing or editing the secret.

**Tradeoff:** A labelled action uses more horizontal space. The detail pane has enough space at the reviewed desktop size; narrower layouts should adapt the action presentation, not shrink field text or hit targets. Reprompt and organization restrictions must apply identically to the labelled action.

**Review criterion:** A first-time viewer can find password copying immediately, while a keyboard user can reach the same action without revealing the secret. The field remains masked after copying.

## 4. Show when a Send can still be used

**Priority: high.** The overview gives deletion its own column and shows Active or Disabled under Access. The editor already distinguishes `Delete after` from `Stop access after`, but the overview does not expose the latter. The current example happens to stop access when deleted; the hierarchy does not scale well to an earlier expiry or an access limit.

**Change:** Present `Available until` as the primary time value, with a quieter deletion value or detail in the same cell. Availability must derive from disabled state, expiration, deletion, and an exhausted access limit. When a limit exists, show a useful count such as `3 of 10 accesses`; otherwise retain a simple access count. Keep password protection as a separate attribute. Use an explicit `Copy link` action; leave Edit and Delete in the row menu. Inactive links can still be copied if useful, but the current unavailable status must remain visible.

**Benefit:** Before sharing, the sender can answer the important question: can the recipient open this link, and for how long? Deletion remains discoverable without being confused with earlier access expiry.

**Tradeoff:** The row needs two distinct time concepts, so use hierarchy rather than adding several full columns. Relative times should have an exact local date/time available in secondary text or a tooltip.

**Review criterion:** The existing overview can represent an active Send, an earlier expiry, an exhausted access limit, and a disabled Send with correct wording. These can be fixture variations of the same composition; they do not require new pages.

## 5. Improve density selectively in routine editing

**Priority: medium.** The login editor gives a full `Match detection` control to every website in its default view. Those advanced controls and repeated section framing push everyday fields below the initial viewport. The generator's right column contains mostly ongoing educational copy. The settings rows, by contrast, already have an appropriate label/action hierarchy and should retain their comfortable interaction areas.

**Change:** Keep website addresses immediately editable, but put a website's matching rule behind a small advanced disclosure; show its non-default value in the collapsed summary. Reduce redundant explanatory chrome in the generator to one concise, relevant note near the output/history controls. Preserve field labels, normal text sizes, and comfortable hit targets. Do not impose a global compact mode or compress every settings row to fill whitespace.

**Benefit:** Common login edits need less scrolling, and the generator keeps attention on output, options, and copying. Expert controls remain discoverable when needed.

**Tradeoff:** A matching rule takes an extra step to change. Showing non-default rules in the summary prevents configuration from becoming invisible. Text scaling may legitimately require scrolling; density must not override accessibility.

**Review criterion:** Name, username, password, verification key, and the main website form a coherent editing sequence. Save and Cancel stay anchored. Advanced settings remain reachable and their existing values are preserved.

## Keep these decisions

- Preserve the quiet Fluent surfaces, restrained blue selection, Segoe typography, and current light/dark hierarchy. There is no demonstrated reason for another palette or typography redesign.
- Preserve the three-pane vault layout and the no-selection state. Its empty detail pane explains how to proceed and avoids exposing a credential automatically. Do not fill it with a dashboard or statistics merely to use space.
- Preserve masked secrets, explicit reveal/copy separation, item identity, read-only explanations, and distinct offline or uncertain-save states.
- Preserve anchored editor/dialog actions, discard protection for dirty drafts, and separate verification stages for consequential account operations.
- Preserve the generator's clear output and Copy action, progressive disclosure of uncommon options, and the Settings Security/Account/Preferences structure.

## Stop condition

After these refinements, use the existing representative scenes to review the full search-to-copy, search-to-edit, create-to-share, and settings-return journeys. Further redesign should follow observed task failures, text-scaling/localization issues, or native implementation evidence. More decorative changes or additional near-identical scenarios would increase review effort without a comparable user benefit.
