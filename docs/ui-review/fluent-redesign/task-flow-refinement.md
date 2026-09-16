# Third refinement: reduce repeated work

This iteration keeps the 98 review scenes and both existing themes. It does not add another visual style or expand the product. The [priority review](task-flow-priorities.md) identified a smaller set of changes with a direct benefit to everyday tasks.

## Implemented changes

| Change | Why it helps | Tradeoff |
| --- | --- | --- |
| Contextual vault navigation | Primary destinations keep the same position. Vault scope, favorites, folders, and organizations appear only in the vault workspace, so they no longer look like Send or Settings filters. | A specific folder takes an additional step from another workspace. Returning to My vault restores the previous workspace to avoid repeating the search. |
| In-memory return context | Search, type/favorite filters, sort, list position, origin scope and keyboard focus survive opening an item and returning to results. A visible Back to results action makes the path explicit. | The gallery needs explicit reset rules. Manual scene selection restores its representative fixture, and authentication/locking clears context. |
| Labelled Copy password | The most frequent credential action is recognizable without interpreting an icon. Reveal remains separate, and copying a protected password does not automatically unmask it. | The label uses more row width. It was checked at both review window sizes instead of shrinking secret text or hiding the action. |
| Clear Send availability | Copy link stays visible. Available until describes recipient access, while the secondary line preserves deletion/retention information. Disabled links remain copyable, with an explicit unavailable status. | Two related time concepts share one column; precise wording matters more than adding another full column. See [Send refinement](send-flow-refinement.md). |
| Progressive website matching | Website addresses remain immediately editable; matching rules move behind a small disclosure. The current rule, including nondefault values, stays visible in its summary. | Changing the rule takes one extra step, reducing visual weight for a less common task. Existing values and field contracts are retained. |

## State and privacy rules

The context feature stores only view state in the current gallery's JavaScript memory. It does not use localStorage, sessionStorage, cookies, files, or account credentials. It never captures edit drafts or password values. Closing the gallery, selecting an independent review scene, or navigating to login/lock resets the relevant context.

Scopes are distinct: all items, individual folders, favorites, organizations, archive, and trash do not silently share filters. Returning from another workspace resumes the most recent vault scope. Returning to results restores focus without automatically displaying a credential. Theme changes retain the same current view.

These interactions are part of the offline review prototype. Implementing the same behavior in WinUI should use the existing in-memory account/session lifecycle and authorization rules. The feature does not establish real authentication, clipboard, server, or encryption behavior.

## Verification

- [Vault context flows](vault-context-validation.json): search/open/back, scrolling, focus, scope boundaries, cross-workspace return, theme switching, lock cleanup and manual-scene resets.
- [Focused UI refinements](task-flow-refinement-validation.json): contextual navigation, stable main-navigation positions, copy without reveal, matching disclosure, and small-window Lock access.
- [Send checks](send-flow-refinement-validation.json): labelled copy controls, inactive-link feedback, menus, filtering and both review sizes.
- [Full render checks](render-validation.json) and [general regression checks](review-validation.json) retain the complete scene and source-mapping audit.

## What to keep next

Keep the quiet Fluent palette, typography, three-pane vault, no-selection prompt, and anchored dialog/editor actions. More decorative changes have diminishing value. The next useful evidence should come from native implementation, Chinese text layout, text scaling, assistive technology, and timed real-task trials.
