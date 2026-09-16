# Page-by-page UI/UX review

The second review examined all 58 previous proposal scenes, then revised and expanded the library to **98 scenes / 196 light and dark images**. All 98 final light compositions were visually reviewed, with representative dark images inspected separately. This review concerns the design artifact, not the production application or its backend.

A subsequent [task-flow refinement](task-flow-refinement.md) improves contextual navigation, search-return continuity, frequent copy actions, Send availability, and progressive disclosure without adding scenes.

## Main corrections

| Area | What was wrong in the earlier proposal | Revised behavior |
| --- | --- | --- |
| Item identity | Different login rows opened the same Contoso details. | Unique item IDs retain the selected name, username, owner, folder, and current edit draft. |
| Creating and editing | Add opened a populated Edit form. Edit allowed changing the owner. | New items start with a blank draft and required-field guards. Editing preserves ownership; organization transfer is a separate operation. |
| State consistency | Empty and loading scenes retained populated counts. Offline details claimed to be synced. | Counts come from one synthetic fixture. Empty, loading, no selection, offline, pending save, archive, and trash have distinct wording and actions. |
| Sensitive fields | Empty values could appear as masked secrets. Protected editing and read-only organizations were ambiguous. | Empty fields say Not set with disabled secret actions. Reprompt gates sensitive access and protected editing. Organization restrictions explain hidden fields and disabled editing. |
| Form completeness | Several type-specific fields were omitted or flattened into a lossy name field. | Identity, bank, license, passport, and SSH fields retain their shape in grouped, scrollable sections. Private values stay masked. Lower-frequency fields use progressive disclosure. |
| Authentication | Self-hosted registration missed an address. Error wording inferred a correct password. Some actions jumped to a success view. | Server context, precise errors, required input, exact verification-code checks, alternate methods, and cancellation paths are explicit. Preview submissions do not authenticate a real account or fabricate service success. |
| Account protection | Some setup and deletion stages were collapsed or unclear. | Identity verification precedes setup. Account deletion retains consequences, exact email, and final credential stages. Policy and device availability explain disabled choices. |
| Send | File rows opened a new text form, initial receive state was missing, and lower controls were crowded. | Create and edit are separate. Row actions identify the correct Send. Received-link, result, empty, and invalid-input states are distinguished. Advanced restrictions are available without hiding the command footer. |
| Generator | Changing modes or options could retain an unrelated output. History sounded permanently stored. | Mode-specific preview generation honors the selected format. History is described as copied values in the unlocked session, cleared when the vault locks. |
| Reports and transfer | Results, failed checks, and empty states were conflated. Import was enabled without a file. Export scope was inconsistent. | Actionable report results and failed/clean states are separate. Import requires a valid preview and invalidates that preview on format change. Export names the personal-vault scope and plaintext consequence. |
| Devices and emergency access | A four-word phrase and Last active label disagreed with source. View and Takeover consequences were incomplete. | The fingerprint has five hyphen-separated words. Device dates are registration dates. Approval requires deliberate acknowledgement. Emergency permission, waiting periods, and takeover consequences remain distinct. |
| Visual polish | Some low-frequency content dominated the page; a few controls and empty-state actions were poorly aligned. | Keep the quiet three-panel foundation. Consolidate advanced options, align actions, improve empty-state recovery, and maintain anchored dialog/editor commands at both reviewed window sizes. |

## Evidence and per-page records

- [Vault review](audit-vault.json): 41 final scenes, including the original findings and their dispositions.
- [Authentication and Settings review](audit-auth-settings-dispositions.json): 29 final scenes, with [original findings](audit-auth-settings.json).
- [Tools and security-flow review](audit-tools-flows-dispositions.json): 28 final scenes, with [original findings](audit-tools-flows.json).
- [Rendered-image checks](render-validation.json): all 196 final PNGs, scene targets, and render-time errors.
- [Window-size and interaction verification](review-validation.json): per-scene checks at 1280 × 900 and 960 × 720, both themes, keyboard modal behavior, representative interactions, source mappings, local links, and image decoding.
- [Complete source-to-proposal mapping](design-manifest.json): all 178 original inventory entries, with direct, family, and boundary scope retained.

The three review groups account for every final scene: 41 + 29 + 28 = 98. A geometry check is not an aesthetic judgment; the visual inspections and automated checks are recorded separately. Long content is reviewed through its scrollable body, while each PNG represents its initial viewport.

## What remains outside this sign-off

- The original [native runtime defects](../runtime-findings.md) remain implementation work. A corrected design image does not fix a WinUI binding or process exit.
- The expanded synthetic proposal fixture has 14 active items, one archived item, and one deleted item. Its personal export contains 14 items after excluding organization ownership and Trash. This is a design fixture, distinct from the earlier live Vaultwarden seed. New item types still require a compatible server.
- Family and boundary mappings do not claim that every saved-view, policy, recovery, external-provider, or operating-system branch has its own image or was executed.
- Native text scaling, high contrast, screen readers, real clipboard behavior, cryptographic verification, and server-side outcomes must be validated when implementing the design. CSS masking in this synthetic artifact is not a replacement for native secure controls.
- The previews deliberately avoid real account operations. Their local draft, selection, filtering, and confirmation behavior exists to make the proposed UX reviewable.

The visual direction is ready for detailed implementation review at the tested sizes. Further changes should focus on demonstrated task friction, localization, accessibility, and native runtime behavior rather than adding decoration or broadening the product scope.
