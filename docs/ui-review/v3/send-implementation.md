# Send v3 implementation notes

The Send page uses native WinUI controls and compiled bindings. It does not embed the handoff HTML.

## Implemented behavior

- A 344 epx list, 380 epx at window widths of at least 1680, and 300 epx between 800 and 999.
- A single list or detail pane below 800, with a 44 epx back bar and unsaved-change confirmation.
- Inline text and file editors, draft list rows, relative deletion and expiration choices, password removal with undo before saving, and real file picking.
- Tab changes retain the editor. Locking or signing out cancels work and releases the draft, original-value snapshot, decrypted details, and clipboard feedback.
- Dirty detection compares the current fields with the original snapshot. Returning to an existing date's Keep selection restores its exact original absolute timestamp.
- Name, decrypted text, and file-name search; keyboard list selection, editing, copying, deletion, and guarded Escape navigation.
- Link and text copies use the protected clipboard service. The link countdown and progress use the actual clipboard ownership and expiry metadata.
- Received links use the existing authenticated or anonymous access implementation. File saving retains download leases, authenticated decryption, cancellation, atomic sensitive-file commits, and the single-operation gate.
- Receiver attribution and expiration are copied from the actual `creatorIdentifier` and `expirationDate` response fields.
- The receiver honors `text.hidden`: the text and copy action are withheld until the user explicitly chooses Show. Locking clears the reveal state.
- Existing offline-read-only restrictions, session leases, mutation gates, and uncertain-result reconciliation remain in place.

## Protocol and service differences requiring product decisions

| Handoff behavior | Current implementation | Evidence / reason |
| --- | --- | --- |
| Sort and display by creation time | Sort by recent modification and show the known revision time | Vaultwarden `src/db/models/send.rs` returns `revisionDate` but no creation timestamp. The client does not invent one. |
| Undo deletion | Confirm permanent deletion before sending it | Vaultwarden `DELETE /sends/<id>` permanently removes the Send. The current service has no restore operation preserving the same share URL. |
| Download an owned file directly from its detail | Open its share link in the received-link page, then authenticate and download | The existing file-download service is tied to a public Send access lease. This path follows the link's access rules and may consume an access count. |
| Receiver sees remaining access count and deletion time | Show only the actual sender attribution and known expiration | The anonymous `send-access` response does not include `accessCount`, `maxAccessCount`, or `deletionDate`. |
| Replace a file without changing the existing link | The editor explains the link change, confirms before saving, and copies the replacement link after a confirmed success | The existing write service creates a new Send and deletes the original. Protected replacements require password re-entry, a new password, or explicit password removal. Unknown deletion outcomes keep both affected rows locked until reconciliation. |

## Verification scope

The shared verification owner runs builds, tests, Native AOT verification, and native UI inspection. Send-focused tests were updated for the v3 layout while preserving readonly, reconciliation, session-ownership, and received-file cancellation checks. Added behavior tests cover search across decrypted content, password-removal mapping, and receiver metadata propagation.

A build result alone does not constitute a completed visual review. Native screenshots and any remaining findings are recorded in the shared v3 delivery report.
