# WinVaultWarden v3 implementation specification

## Authority and scope

The authoritative handoff is `D:/Code/design_handoff_winvaultwarden_v3/README.md` (1,238 lines) and `WinVaultWarden Prototype v3.dc.html` (1,918 lines). The HTML is a visual and interaction reference, not application code. Rebuild with WinUI 3, XAML, MVVM, and existing services. Its 1 px equals 1 effective pixel. The latest sections of the handoff override older descriptions within the same document: section 7.14 implements master-password changes, section 7.17 defines the 640 epx minimum width, and section 12.4 standardizes terminology.

Do not copy demonstration data, simulated login or verification, fake TOTP or exposure counts, fixed operation delays, file examples, a fake Windows Hello dialog, preview-only controls, window chrome, or title-bar caption buttons. Real server responses, OS APIs, account policy, capabilities, and cryptographic services remain authoritative.

Final design choices: inline editors; verification on every sensitive reveal/edit by default with an optional five-minute grace setting; copy actions on row hover; website favicons with deterministic colored initials as fallback; animation follows `UISettings.AnimationsEnabled`; system/light/dark themes. Use the real application icon and OS caption buttons.

## Shell and navigation

| Element | Required implementation |
| --- | --- |
| Title bar | 48 epx, application icon 18, application name 12/16, four tabs: Vault, Generator, Send, Security report. Selected tab semibold with centered 16 x 3 accent indicator 6 epx above the bottom. |
| Global search | 360 x 32, centered in the full window at widths of at least 1280. Search icon 16 or clear action 24 x 24. Ctrl+F focuses and selects all. Send list searches Send; other contexts route text to Vault. Crossing into or out of Send clears the query. |
| Title actions | Theme, lock, settings, account avatar; action target 40 x 32; avatar 28. Only theme remains when signed out or locked. |
| Pane | 232 wide, padding 4/8/10, transparent over Mica. Navigation rows 34 high, 16 icon, 12 gap, padding 0/10/0/14, radius 4, 3 x 16 accent indicator at x=8. Counts use 12/16 tabular numerals. |
| Content layer | Theme layer fill, top and left 1 epx surface stroke, top-left radius 8, clipped. |
| Status bar | 28 high, top 1 epx stroke, padding 0/6/0/10, gap 16, 12/16 secondary text. Hidden while locked/signed out. |
| Status contents | Actionable sync state and elapsed time; real server hostname; SSH state/action if supported; verification grace timer and end action; clipboard label, remaining time, history policy and clear action. Cleared feedback lasts 3 seconds. |

Each tab has its own pane. Preserve per-page state when switching top tabs, including live drafts.

- Vault: accent split New item button (default Login; menu Login, Card, Identity, Secure note, SSH key), All items, Favorites, Verification codes, real folders, real organizations/collections, Archive and Trash anchored at the bottom. Counts exclude archived/deleted items except their dedicated scopes.
- Generator: Random password, Passphrase, recent copied history (up to five displayed entries, value in 12 epx monospace, time and copy/check icon). Existing username generation and full history remain available as preserved capabilities.
- Send: accent split New Send button (default Text; Text/File menu), All, Text, File, then Receive group with Open received link.
- Security report: Recheck action; Overview; Needs attention categories; Session progress. Preserve the existing HTTP URL category and exposure-check lifecycle.
- Settings: account summary header, Account, Security, SSH agent, Devices, Import and export, Appearance and behavior, About. Pending counts reflect real requests only. Subpages remain inside the settings content area.

## Adaptive behavior

| Window width | Pane | Vault/Send content | Title/search | Status |
| --- | --- | --- | --- | --- |
| At least 1680 | 232 | List 380 plus detail | Centered 360 search | Full |
| 1280-1679 | 232 | List 344 plus detail | Centered 360 search | Full |
| 1200-1279 | 232 | List 344 plus detail | Search follows tabs, margin 16, width 160-360 | Full |
| 1000-1199 | 48 compact | List 344 plus detail | Search follows tabs | Full |
| 800-999 | 48 compact | List 300 plus detail | Hide application name | Hide hostname and history-policy text |
| 640-799 | 48 compact | One column; list or detail | 40 x 32 search action; expanded search overlays tab area | SSH icon only |

Minimum window width is 640. Compact pane rows are 40 wide with tooltips and no labels/counts; section headings become dividers; recent generator history is hidden. A 40 x 36 hamburger opens a 232-wide pane overlay. Outside click, navigation selection, and Esc close it. New becomes an icon action opening its menu. The compact selected indicator moves to x=4.

Single-column detail has a 44-high return strip with the current list name and a back arrow. Returning during dirty editing requires discard confirmation. New/edit opens detail directly. At narrow widths, Ctrl+F or the search action replaces tabs with search between x=54 and the action area. Empty blur, clear, and Esc collapse it. Tabs never wrap or compress.

## Visual tokens

Prefer WinUI theme resources and Mica; explicit colors below define the solid fallback and reference comparison. Accent follows the user's system accent.

| Role | Light | Dark | Native role |
| --- | --- | --- | --- |
| Chrome | #F3F3F3 | #202020 | Mica / solid base |
| Layer | #FFFFFF | #282828 | Layer fill |
| Divider | #EBEBEB | #333333 | Divider stroke |
| Surface stroke | #E0E0E0 | #3B3B3B | Card/surface stroke |
| Primary text | #1B1B1B | #FFFFFF | Primary text |
| Secondary text | #555555 | #C8C8C8 | Secondary text |
| Tertiary text | #6B6B6B | #A0A0A0 | Tertiary text |
| Accent | #005FB8 | #60CDFF | Accent fill/text |
| Text on accent | #FFFFFF | #00263B | Text on accent |
| Selected fill | Black 5.8% | White 7% | Native selected fill |
| Hover fill | Black 3.4% | White 4.5% | Subtle secondary fill |
| Neutral tile | #EFEFEF | #363636 | Dedicated theme resource |
| Control fill | #FFFFFF | #2D2D2D | Default control fill |
| Control stroke | #E5E5E5 | #3A3A3A | Default control stroke |
| Control bottom | #CCCCCC | #303030 | Secondary control stroke |
| Input underline | #8A8A8A | #9A9A9A | Strong control stroke |
| Success | #0F7B0F | #6CCB5F | Success fill |
| Caution | #9D5D00 | #FCE100 | Caution fill |
| Critical | #C42B1C | #FF99A4 | Critical fill |
| Highlight | Accent 16% | Accent 24% | Search/copy overlay |
| Scrim | Black 30% | Black 50% | Smoke fill |

Text: Segoe UI Variable Text / Segoe UI / Microsoft YaHei UI below 20; Segoe UI Variable Display for headings; Cascadia Mono / Cascadia Code / Consolas for secrets, keys, TOTP, links and commands. Use tabular numerals for counts and timers, character ellipsis where specified, and wrapping for descriptions.

| Role | Size / line height | Weight |
| --- | --- | --- |
| Caption | 12 / 16 | Normal or semibold |
| Body | 14 / 20 | Normal |
| Body strong | 14 / 20 | Semibold |
| Subtitle | 20 / 28 | Semibold |
| Page/detail title | 28 / 36 | Semibold; tracking -0.01 em |
| Note/Send body | 15 / 26 | Normal |
| Copy value | 20 / 32 | Normal |
| Revealed secret | 18 / 32 monospace | Tracking 0.02 em |
| TOTP | 26 / 32 monospace | Semibold |
| Generator result | 32 / 42 monospace | Tracking 0.02 em |
| Fingerprint phrase | 18 / 26 monospace | Semibold |

Controls are 32 high with radius 4, icons 16, 8 epx icon/text gaps. Icon actions are 32 square; row actions 28 square. Accent split-button secondary segment is 32 wide. Menus are 180/220/260 wide with 4 padding, 32-high rows, radius 8 and native ThemeShadow. Fields use 2 epx accent focus/error underlines. Large editor name input is 44 high, 20 semibold. Toggle target is the whole row; visual track 40 x 20 with left-side state text.

Tiles: list 32/radius 6/letter 14; request 40/radius 8/icon 20; report focus 48/radius 8/letter 20; detail 56/radius 10/letter 24. Login fallback uses a deterministic name-derived hue: FNV-1a(name) modulo 360, chroma factor 0.8. Light Oklch background `(0.935, 0.045*c, hue)` and foreground `(0.45, 0.13*c, hue)`; dark `(0.37, 0.07*c, hue)` / `(0.9, 0.075*c, hue)`. Convert to sRGB or use an equivalent precomputed palette. Monochrome mode uses neutral tile and secondary/primary text. Icons use Fluent System regular for actions and filled for status.

## Vault list and detail

List header uses 20/28 title plus count, padding 16/16/4/20. Toolbar is 40 high, padding 0/10/0/12. Type menu: all, login, card, identity, secure note, SSH. Sort: recently used, name, recently modified. Trash adds Empty trash when nonempty. Scroll padding 0/6/8.

List rows are 52 high, padding 0/8/0/12, gap 12, radius 4. A 32 tile precedes title and 12/16 subtitle. Selected title is semibold. Search highlights the first title match. Status icons precede favorite: exposure, reuse, weak, missing site 2FA where supported. Hover replaces them with type-specific copy actions: username/password/TOTP; card number; identity email; SSH public key. Trash rows expose no copy actions. New draft sits at the top with selected treatment and unsaved subtitle.

Recently-used ordering is a stable snapshot: copying updates usage metadata but cannot move the hovered row. Refresh the snapshot on navigation, return to Vault, sort/search change, and report navigation. Group only recently used without a query into Today, Previous seven days, Earlier. Name sort uses locale-aware Chinese ordering. Missing selected result falls back to the first result. Search covers title and subtitle case-insensitively. Empty search offers clear and create-with-query; other empty scopes use specific messages.

Detail scroll padding is 32/40/32, inner max width 920 aligned left. Header: 56 tile, gap 16, title 28/36, location 12/18, favorite, Edit, Open website when applicable, More. Trash uses Permanent delete and Restore. Status line aligns at x=72 below header with 12 top margin. Facts wrap, use secondary text, and precede the repair action.

The copy strip replaces duplicate card fields. It has equal-width columns, gap 4, margin 28/-12/0. Cells have minimum height 92, padding 12/12/20, radius 6, a 24-high caption, 6 gap, and 32-high value. Whole-cell click copies. Hover shows copy, pressed shows selection, and successful copy flashes highlight then shows a check, copied label and countdown. The following field becomes the next step; Open website primes the first field. Switching items clears relay state.

| Type | Copy sequence | Remaining groups |
| --- | --- | --- |
| Login | Username, password, verification code | Websites/passkeys; custom fields/attachments/notes |
| Card | Card number, expiry, security code | Cardholder; notes |
| Identity | Name, phone, email | Address |
| SSH key | Public key, fingerprint, private key | Other existing metadata |
| Secure note | None | Bordered prose with copy-all action |

Masked number keeps the last four digits; security code uses three bullets; other secrets use twelve. Eye actions request real verification. Reveal formats secret monospace 18; password digits accent, letters primary, symbols caution. TOTP displays grouped digits, copies raw digits, and has a 16 ring with a two-ePX stroke and remaining seconds; caution at five seconds. The bottom copy progress is 2 high, inset 12, bottom 8. Missing site 2FA add-code cell requires authoritative capability data.

Property groups start with a 1 epx divider, have 24 separation, and use columns `112, *, Auto`, gap 16, minimum row height 48 (56 for two lines), and a bottom divider. Empty values/groups disappear. Existing fields already in the strip do not repeat. URLs distinguish hostname/path and have open/copy actions. Multiline notes/address align top with padding 14/0 and max width 560. Passkeys and attachments use two-line rows; attachments retain download. Footer shows real modified/created timestamps in 12/16.

Inline editor keeps the tile, replaces the name with the large input, and exposes Cancel/Save. Hide copy strip and status. Editable property rows are at least 52 high. Include copy-strip fields, folder selector width 240, actual custom fields and existing extended item types. Secret fields have reveal; password has regenerate using current generator settings. Existing passkeys remain read-only. Repair password enters verified editing with a generated visible password and a reminder to update the website first. New requires no reveal/edit verification, inherits the folder, and can accept query name or generator password. Save validates trimmed name, remasks secrets and reports actual outcome. Explicit Cancel discards without a second confirmation.

## Generator and security report

Generator is a centered max-720 page with padding 40/40/48. Title and description lead to a top-divided result area (top margin 28, padding 28/0/14). The result wraps in balanced lines. Strength has a 136-wide four-segment bar, 4 high with gaps 4, semantic label, entropy, regenerate, Use for new login, and Copy. Bands: under 50, 50-74, 75-99, at least 100 bits. Options are line-separated rows of at least 56: length 8-64 default 20, phrase count 3-10 default 4, four character-set buttons, last selected set cannot be disabled, ambiguity exclusion default off, phrase separators dash/dot/underscore/space, capitalization and digit inclusion default on. Option changes regenerate immediately; explicit regeneration/mode change may play the 360 ms decode effect. Use actual cryptographic random selection, the complete EFF 7,776-word list, and existing policy restrictions. History records distinct copies, newest first.

Report is max 880 with padding 40/40/48, title 28/36, meaningful loading/checking/failed/never-checked/clean states, and a summary timestamp. Overview has a first-priority action row: 48 tile, 20/28 title, colored reason, metadata, View and Repair. Below, category sections show 32-tile rows of at least 56 with View and repair actions. Priority is exposure, reuse, weak, missing site 2FA; one primary issue per active login in the reference. Preserve HTTP findings rather than dropping an existing check. The clean state must not imply an online exposure check ran when it did not. Session completion comes from a snapshot at application launch/unlock and records actual removal of initial issues. View/Repair navigates to Vault, clears query/type, selects the item; Repair enters verified inline editing and pre-fills only when changing password.

## Send

Reuse the Vault pane/list geometry. Sort by newest or next deletion. Row secondary text shows real deletion time and access count; disabled/expired/limit-reached states replace it with their reason and muted title/icon. Hover offers Copy link. Search includes title/text/file name. Empty states provide New Send.

Detail uses a 56 tile, 28/36 name, created subtitle, Edit/More. The status line exposes access state and relevant Enable/Extend/Increase-limit action. Facts include auto-delete, access count/limit, password requirement, and hidden text. The link copy cell follows the copy-strip pattern and uses the actual `https://server/#/send/accessId/key` link. Text body uses 15/26 and copy; file has a 40 tile, name/size, Download. Metadata columns are 112/*, minimum row 48: deletion, expiration, maximum access, password-set state, hidden email, optional private note, created/modified.

New/edit is inline with Cancel and Create and copy link / Save. Name is required. New-only type choice supports Text/File. Text is a five-line required input with hide-content toggle. File uses real picker, actual size limits, filename-to-name default, and existing replacement/reconciliation behavior. Deletion options: preserve current when editing, 1 hour, 1/2/3/7/30 days (new default 7). Expiration: preserve current, never, 1 hour, 1/2/3/7 days. Expiration must not exceed deletion; deletion must respect server maximum. Access limit is optional numeric input. Password blank means preserve existing; explicit removal has undo before save. Retain hide-email, disable, note fields. Actual successful creation selects the item and copies the actual link. A successful server deletion cannot be presented as reversible unless supported.

Receive is max 640 with padding 40/40/48. Explain local decryption and no vault storage. Link uses monospace 13, optional password width 280, Open action, inline validation and real busy state. Enter submits. Successful content has name/source, bordered prose/copy or real file result, and any server-provided remaining access/deletion information. Clear received content on lock. No example-link action or fabricated result.

## Settings and authentication

Settings is max 760 with padding 40/40/48. Sections have 28/36 title; Account/About add a bordered header with 56 tile and 20/28 title. Group spacing 32. Settings rows use `*, Auto`, gap 24, minimum height 52/64 with description, padding 8/0 and divider. List groups use `32, *, Auto`, gap 14, min height 60. Form subpages use a wrapping breadcrumb title and retain the parent pane selection.

| Section | Required content |
| --- | --- |
| Account | Actual avatar/name/email/server/version; edit name; KDF summary/change; master password subpage; logout; existing multi-step account deletion. |
| Security | Hello; PIN, restart policy and change PIN; idle timeout/action with actual organization policy; account two-step login and emergency access subpages; reveal/edit verification frequency; clipboard interval/history; capture policy. |
| SSH agent | Actual status/pipe; enable; prompt policy; Hello while unlocked; forwarded-request policy; loaded keys; remembered authorized executables; eight recent requests. If no service exists, describe unavailability rather than reporting active keys or success. |
| Devices | Actual pending request and expiry; registered devices; current/trusted identity; supported revoke operation. |
| Import/export | Real supported formats and picker; import target folder; preview/outcome; export format and protection; master-password gate; system save picker. |
| Appearance | Theme; icons; language if implemented; close behavior; startup if implemented; minimize after copy; supported browser integration; hardware acceleration if implemented. |
| About | Actual app/architecture/OS/runtime values; redacted diagnostic copy; real update mechanism if present; logs folder; source and licenses. |

Security reference defaults are Hello available/enrolled, PIN off, restart PIN policy on, idle 15 minutes, lock action lock, verify every reveal/edit, clipboard 30 seconds, history excluded, capture disallowed. Existing server and device policies take precedence. Do not enable enrollment-dependent features merely to match a prototype toggle.

Password subpage: current/new/confirm/hint, strength estimate, optional rotate-account-key if supported, Save/Cancel. Validate actual policy and never replace the real password-change transaction. Two-step login: master-password verification, real status/providers, TOTP setup QR/secret/code, email code setup, WebAuthn keys/system registration, Duo web management, recovery code. Emergency access: real trusted/granted relationships, state-specific invite/confirm/remove/approve/reject/revoke/request/cancel actions, fingerprint verification, and takeover-specific warnings. Preserve server capability/SMTP errors.

Sign-in uses a centered 360-wide scrollable column (padding vertical 32), 48 app/state icon, 20/28 heading, 14/20 subtitle, form margin 28 and gap 16. Inputs 32 high; primary/secondary actions full width; optional separator/link/footer. Screens: email plus server choice; master password; available two-step provider; provider picker; device approval; SSO browser wait; registration; Hello enrollment. Data validation and workflow must remain real, including provider availability, server policy, crypto transitions, trust, and errors. No automatic simulated approval.

Locked screen is a 320-wide centered column with 64 avatar, 20/28 title, account/server, method action area after 28, lock metadata after 28. Preferred usable method is Hello, then PIN, then master password. Alternate usable methods appear as equal text actions. Failed PIN clears input and reports real remaining attempts; five failures trigger the established logout path. Keep session cleanup and restart security guarantees.

## Overlays, verification and external requests

Confirmation/forms use native ContentDialog: width 480 (TOTP setup 520), radius 8, padding 24, title 20/28, body secondary, two equal footer buttons with gap 8 on chrome fill. Danger button uses critical fill. Typed confirmations disable submission until correct; validation must cancel dialog closing. Supported forms include master-password verification, name, PIN, KDF, TOTP, email verification, recovery code, emergency invitation, and protected export.

Toasts are centered 44 epx above the window bottom, max width 600, min height 44, padding 6/6/6/14, gap 10, radius 8, native shadow. Success/info icon 16; optional Undo; Close action. Ordinary lifetime 5 seconds, undo 8 seconds; replacement is latest-wins. Announce meaningful status accessibly without reading timers every second.

External requests are independent top-level 460-wide windows, never an in-window modal. They work when the main window is minimized/tray-hidden, cannot resize/maximize/minimize, use real system shadows, and obey capture policy. Their title bar is 32; body padding 20/24; 40 icon; heading 20/28; footer has equal Allow/Reject actions, padding 20/24. No default affirmative button, and Enter must not accidentally approve.

SSH request shows authenticated executable/source/PID, command, host confidence, actual key/fingerprint, forwarding warning, lock state, optional remember-to-lock checkbox, remaining 60-second deadline and queue count. Close/Esc rejects; OS verification pauses timeout handling. New-device request shows actual device/client/context, matching fingerprint phrase, actual expiry (reference 15 minutes), Approve/Reject; close/Esc defers and leaves a pending entry. Only the first queued request displays. Never infer trustworthy executable/host information from untrusted request labels.

Revealing a secret or entering existing-item editing uses real Windows user verification. Optional five-minute grace applies only to reveal/edit, not unlock or external approvals. Cancel leaves content hidden and editing unopened. Remask on item switch/save/lock. Existing Bitwarden reprompt must still guard copying. Account operations requiring a password hash continue to demand the master password; Hello is not a substitute. Preserve the established multi-step account deletion flow.

Clipboard writes exclude history/roaming according to the supported policy, track ownership, and clear only if still owned. Copy does not normally require verification except reprompt items. Lock clears revealed data, grace, drafts, received Send content, remembered SSH grants, menus/dialogs/verification operations. Logout also terminates session-bound features and queues and applies existing cache/key cleanup.

## Keyboard and motion

Ctrl+F focuses/selects search. Up/Down moves list selection including from search and scrolls it into view with 8 epx margin. Enter in search selects the first result. Ctrl+C copies the primary current-item secret or Send link only when no text is selected and no editing input owns focus. F2 edits and Delete requests supported deletion, excluding trash. Modal/verification/request/locked states suppress list accelerators.

Esc precedence: clear active search text; close menu; cancel verification; close confirmation/form; reject SSH or defer device request; collapse overlay pane/search; step back in auth; exit dirty edit through confirmation; leave settings subpage. Changes of item/navigation/new action prompt for dirty edits. Top tab switches retain drafts; lock discards them.

| Motion | Duration | Transform |
| --- | --- | --- |
| Page / lock transition | 333 ms | Fade, Y 16 to 0 |
| Detail / edit transition | 250 ms | Fade, Y 8 to 0 |
| Menu | 167 ms | Fade, Y -6 to 0 |
| Dialog | 250 ms | Fade, scale 1.05 to 1; scrim 167 ms linear |
| Request | 333 ms | Fade, Y 16 to 0, scale 0.98 to 1 |
| Toast | 200 ms | Fade, Y 6 to 0 |
| New TOTP | 400 ms | Fade, Y 55% to 0 |
| Navigation indicator | 300 ms | Position; cubic-bezier(0.3,0,0,1) |
| Row indicator | 200 ms | ScaleY 0 to 1 |
| Copy highlight | 650 ms | Fade out, ease-out |
| Toggle / charset | 120 ms | Position / color |
| Meter color | 200 ms | Color |
| Generator decode | About 360 ms | Nine 40 ms frames, settle left-to-right |
| Progress/ring | 1 second | Linear |

Default easing is cubic-bezier(0,0,0,1). Disable all decorative motion when system animations are disabled; timers and state correctness continue.

## Preserved surfaces and decision boundaries

The handoff explicitly excludes visual redesign of Key Connector, SSO trust, registration-disabled errors, post-deletion recovery, Duo/YubiKey OTP setup; organization/collection/batch/saved-view/advanced filters; bank/license/passport types; custom-field add/remove, password history, attachment upload/delete, folder CRUD, transfers, offline/sync failure; HTTP report and exposure progress; browser passkey request, tray, licenses and emergency-access vault viewing. Preserve these capabilities and apply the same tokens.

Source inspection identified these pre-existing mismatches that require explicit capability reporting at delivery:

| Capability | Source evidence | Decision needed if still absent |
| --- | --- | --- |
| SSH agent | Settings exposes a disabled future-control group; key storage is present but no signing-agent service was located. | Implement a separately scoped security-sensitive agent, or retain honest unavailable settings. Host/forwarding inference additionally needs session binding and known-host mapping. |
| Site 2FA support | Current report provides exposure/reuse/weak/HTTP, with no site-capability dataset. | Integrate and maintain a source or omit missing-site-2FA claims, as the handoff permits. |
| Import/export expansion | `IVaultImportService` / `IVaultExportService` enums and current view model provide JSON/CSV. | Add and validate eight importers and account/password-protected exports, or expose only working formats. |
| Language, GPU, startup preferences | Some existing properties are demonstration/future controls, not evidence of platform functionality. | Implement platform-backed behavior/localization or keep these unavailable with a reason. |
| Browser integration packaging | `SettingsViewModel.BrowserIntegrationSupported` documents unpackaged-only support. | Preserve MSIX restriction unless registration architecture is implemented and verified. |
| Send undo | Existing writes use server operations and reconciliation; prototype undo reinserts local demo data. | Do not promise recovery after irreversible deletion; use a real reversible transaction or retain confirmation. |
| Report counting | Existing total sums reuse groups and overlapping findings, including HTTP. | Reconcile to unique-item priority counting with transparent HTTP preservation before displaying the reference number. |
| Verification UX | Existing service tries actual Hello challenge-signing and then master password. | Keep a safe fallback when native Hello is unavailable; report visual/interaction differences rather than drawing a fake system prompt. |

This specification is source-derived. It does not claim the application was built, executed, or visually verified. Native Windows evidence is required for final fidelity acceptance.
