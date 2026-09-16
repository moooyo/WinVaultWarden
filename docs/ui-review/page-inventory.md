# UI Audit Page Inventory

Source snapshot: `8cbbdbd218d029baf6acf3d385e3fdbeb95850cd` (2026-09-15).

This inventory covers **9 reachable pages**, **3 XAML dialog classes**, **5 reusable controls**, and **178 audit surfaces** when dialog variants, embedded editors, state families, native prompts, and developer fixtures are included. The surface count is not a page count. One additional `SimplePage` is unreachable from current navigation.

All entries are **source-inspected**. A listed surface is not evidence that it has been executed or captured. Runtime screenshots and generated schematics must retain their separate evidence status in the atlas. Titles below are English semantic translations of the Chinese UI labels; exact wording remains in the cited source.

## Page routes

| Stable ID | Page | Entry point | Main content |
| --- | --- | --- | --- |
| `page.vault` | My vault | vault:allitems | Search and sync; list; filters and sorting; row actions; detail or inline editor; global operation status |
| `page.send` | Send | send:all / send:text / send:file | Add Send; received-link expander; name/deletion-date/options list; copy-link and more menu |
| `page.reports` | Security reports | reports | Vault-health summary; reused, weak, HTTP, exposed password expanders; run exposure check |
| `page.import-export` | Import and export | io | Export card; plaintext warning; JSON/CSV format selectors; import file picker; cipher/folder preview; import/export actions |
| `page.devices` | Device management | devices | Device cards; current/trusted badges; last-active text; pending request list; fingerprint; refresh; approve/deny |
| `page.emergency` | Emergency access | Settings > Emergency access | Invite; trusted contacts; accounts trusting this account; per-status controls; recovered vault items |
| `page.login` | Login and vault unlock | Startup, lock, logout, account switch, or registration completion | Brand; contextual title and subtitle; step; back button; stage-specific form; status InfoBar; primary action; demo action |
| `page.register` | Registration | Login > Register | Create-account form; operation error; email-verification result; uncertain-result panel |
| `page.settings` | Settings | Main navigation > Settings | Account; Security; Preferences; Application; About; Danger zone in one vertically scrolling page |

## Coverage boundaries

- The application contains nine reachable Page classes, one unused SimplePage, three XAML ContentDialog classes, five reusable UserControl classes, and one MainWindow. Individual inventory records also include dialog variants, embedded editors, menu flyouts, shared controls, and developer test pages; surface count is not page count.
- Debug demo entry is registered only under DEBUG. Its session seeds five traditional cipher types, four folders, one device, and two Sends. It does not establish a real server-authenticated session or populate organization/collection membership.
- Demo VaultUiService and SendUiService implement local read/write behavior. External received Send links are explicitly unavailable in demo mode. Attachments, emergency access, two-factor management, registration, export verification, and account changes require appropriate live services for successful completion.
- Demo VaultSession capabilities default to None. Bank-account, driver's-license, and passport creation/editing require CanWriteNewCipherTypes. Archive requires server archive protocol plus entitlement; unarchive requires archive protocol. The resolver reads pm-32009-new-item-types and archive configuration/version as implemented in src/Vault/VaultCapabilityResolver.cs.
- Emergency access supports View and Takeover and status-dependent actions. The service exposes AcceptAsync, but there is no invitation-acceptance control on EmergencyAccessPage; do not invent a client acceptance page.
- Registration has no independent success page: completed registration returns to the login form with account prefill. Email verification uses mail/Web Vault continuation.
- Settings intentionally disable minimize-on-copy, start-on-login, hardware-acceleration selection, SSH-agent features, and language selection as coming-soon controls. These are visible controls, not functioning feature pages.
- Browser integration is limited to portable builds. The Chrome and Edge manifests declare background/content scripts and nativeMessaging, with no action popup, options page, or side panel. The five HTML pages are developer test harnesses, not extension product pages.
- Windows Hello/WebAuthn prompts, browser SSO, Windows file pickers, and third-party Duo pages are environment-owned surfaces. The atlas may show schematic placeholders and prerequisite labels when actual invocation is not available.
- Vault responsive split behavior switches at 800 DIP of VaultLayoutRoot width. Login/Register padding changes below 400 window DIP, Send hides deletion-date columns below 720 window DIP, and the security-report summary rearranges below 760 window DIP.
- AllowScreenshots=false applies WDA_EXCLUDEFROMCAPTURE to the native window. A black image under that setting is expected capture protection, not proof of a blank page.
- Third-party Vaultwarden Web Vault and admin pages are outside the native-client page inventory. Arbitrary combinations of theme, window width, transient error text, and backend data are grouped as states rather than enumerated as separate pages.

## Evidence and feasibility

`demo/live` means the surface can be rendered in the existing demo or a live session, with any successful network operation still subject to its prerequisites. `ui/live` and `offline-form/live-action` distinguish a renderable form from an operation that needs a real account. `conditional` and `controlled-fixture` require a specific persisted, policy, error, or protocol state. `native` and `external` identify UI controlled partly by Windows or another service. None of these labels asserts successful runtime verification.

The complete machine-readable inventory is [page-inventory.json](page-inventory.json). Its stable IDs are suitable for matching an atlas screenshot or schematic to source. Each record includes the primary source, optional additional sources, trigger, controls, prerequisites, and significant variants.

## Application shell

### shell.main — Main navigation shell

- Kind: `shell`; feasibility: `demo/live`.
- Source: [src/App/MainWindow.xaml:31](../../src/App/MainWindow.xaml#L31).
- Entry: Successful login or Debug demo vault.
- Content: Title bar; expandable vault and Send navigation; generator; security reports; import/export; settings; devices; lock; logout.
- Prerequisites: Unlocked session.
- Variants: Expanded navigation; Collapsed navigation; Light theme; Dark theme; Auto theme; Login overlay; Tray minimized.

### shell.leave-pending — Save result pending

- Kind: `dialog`; feasibility: `conditional`.
- Source: [src/App/MainWindow.xaml.cs:466](../../src/App/MainWindow.xaml.cs#L466).
- Entry: Navigate away from a vault editor with pending or in-flight creation.
- Content: Warning about unresolved save state; navigation acknowledgment.
- Prerequisites: Controlled pending/in-flight creation.

### shell.logout-confirm — Log out

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/MainWindow.xaml.cs:502](../../src/App/MainWindow.xaml.cs#L502).
- Entry: Navigation > Log out.
- Content: Confirmation; log out/cancel.
- Prerequisites: Unlocked session.

### shell.tray-menu — System tray menu

- Kind: `native`; feasibility: `native`.
- Source: [src/App/Services/TrayIconService.cs:107](../../src/App/Services/TrayIconService.cs#L107).
- Entry: Right-click WinVaultWarden tray icon.
- Content: Open; Lock; Exit.
- Prerequisites: Show tray icon enabled; Windows notification area.

## Navigable pages

### page.vault — My vault

- Kind: `page`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:170](../../src/App/Views/VaultPage.xaml#L170).
- Entry: vault:allitems.
- Content: Search and sync; list; filters and sorting; row actions; detail or inline editor; global operation status.
- Prerequisites: Unlocked session.
- Variants: Wide list/detail; Narrow list; Narrow detail; Busy; Offline cached data; Operation error; Committed but sync required; Skipped undecryptable items; Unsupported organization keys.

### page.send — Send

- Kind: `page`; feasibility: `demo/live`.
- Source: [src/App/Views/SendPage.xaml:14](../../src/App/Views/SendPage.xaml#L14).
- Entry: send:all | send:text | send:file.
- Content: Add Send; received-link expander; name/deletion-date/options list; copy-link and more menu.
- Prerequisites: Unlocked session.
- Variants: All; Text; File; Loading; Empty; Error; Narrow columns; Row hover/focus actions.

### page.reports — Security reports

- Kind: `page`; feasibility: `demo/live`.
- Source: [src/App/Views/SecurityReportPage.xaml:17](../../src/App/Views/SecurityReportPage.xaml#L17).
- Entry: reports.
- Content: Vault-health summary; reused, weak, HTTP, exposed password expanders; run exposure check.
- Prerequisites: Unlocked session; external HIBP service for live exposure results.
- Variants: Offline analysis progress; Offline error and retry; Health summary; Each expander open; Exposure not checked; Exposure running; Exposure success; Exposure partial/error; No findings; Narrow summary.

### page.import-export — Import and export

- Kind: `page`; feasibility: `ui/live`.
- Source: [src/App/Views/ImportExportPage.xaml:14](../../src/App/Views/ImportExportPage.xaml#L14).
- Entry: io.
- Content: Export card; plaintext warning; JSON/CSV format selectors; import file picker; cipher/folder preview; import/export actions.
- Prerequisites: Unlocked account; master password for export; local synthetic import file.
- Variants: JSON; CSV; File preview loaded; Busy; Succeeded; Committed import requiring sync; Cancelled; Failed.

### page.devices — Device management

- Kind: `page`; feasibility: `demo/live`.
- Source: [src/App/Views/DevicesPage.xaml:14](../../src/App/Views/DevicesPage.xaml#L14).
- Entry: devices.
- Content: Device cards; current/trusted badges; last-active text; pending request list; fingerprint; refresh; approve/deny.
- Prerequisites: Unlocked session; second device request for request content.
- Variants: Device list; No devices; Requests loading; No requests; Pending requests; Request error; Busy.

### page.emergency — Emergency access

- Kind: `page`; feasibility: `ui/live`.
- Source: [src/App/Views/EmergencyAccessPage.xaml:25](../../src/App/Views/EmergencyAccessPage.xaml#L25).
- Entry: Settings > Emergency access.
- Content: Invite; trusted contacts; accounts trusting this account; per-status controls; recovered vault items.
- Prerequisites: Live account and emergency-access-enabled server; additional synthetic accounts for relationships.
- Variants: Loading; Error; No relationships; Invited; Accepted; Confirmed; Recovery initiated; Recovery approved View; Recovery approved Takeover; Recovered read-only vault.

### page.login — Login and vault unlock

- Kind: `page`; feasibility: `offline/demo/live`.
- Source: [src/App/Views/LoginPage.xaml:22](../../src/App/Views/LoginPage.xaml#L22).
- Entry: Startup, lock, logout, account switch, or registration completion.
- Content: Brand; contextual title and subtitle; step; back button; stage-specific form; status InfoBar; primary action; demo action.
- Prerequisites: No account required for initial form; persisted or live authentication state for later stages.
- Variants: Account; Password; Login with device; New-device verification; Two-factor; Required password change; Password unlock; PIN unlock; Key Connector unlock; Trusted-device SSO; Trusted-device approval; TDE v2 JIT provisioning; Account-deletion recovery; Wide padding at 400 px and above; Narrow padding below 400 px.

### page.register — Registration

- Kind: `page`; feasibility: `offline-form/live-action`.
- Source: [src/App/Views/RegisterPage.xaml:18](../../src/App/Views/RegisterPage.xaml#L18).
- Entry: Login > Register.
- Content: Create-account form; operation error; email-verification result; uncertain-result panel.
- Prerequisites: No account required to open; server registration support for completion.
- Variants: Editing; Awaiting email verification; Outcome uncertain; Busy; Wide padding at 400 px and above; Narrow padding below 400 px.

### page.settings — Settings

- Kind: `page`; feasibility: `demo/live`.
- Source: [src/App/Views/SettingsPage.xaml:13](../../src/App/Views/SettingsPage.xaml#L13).
- Entry: Main navigation > Settings.
- Content: Account; Security; Preferences; Application; About; Danger zone in one vertically scrolling page.
- Prerequisites: Unlocked session.
- Variants: Six sections; About collapsed/expanded; Policy-constrained controls; Theme variants.

## Authentication and registration

### auth.login.account — Login: account

- Kind: `state`; feasibility: `offline`.
- Source: [src/App/Views/LoginPage.xaml:111](../../src/App/Views/LoginPage.xaml#L111).
- Entry: Startup or Switch account.
- Content: US/EU/self-host server selector; custom URL; email; saved accounts; enterprise SSO; registration link; demo vault.
- Prerequisites: Saved-account variant needs saved profiles; demo button requires registered demo service.
- Variants: US server; EU server; Self-hosted URL; No saved accounts; Saved account list.

### auth.login.sso-pending — Login: SSO in progress

- Kind: `state`; feasibility: `live`.
- Source: [src/App/Views/LoginPage.xaml:193](../../src/App/Views/LoginPage.xaml#L193); [src/App/ViewModels/LoginViewModel.cs:1605](../../src/App/ViewModels/LoginViewModel.cs#L1605).
- Entry: Account > Enterprise SSO.
- Content: Busy state; browser-completion status; Cancel SSO action.
- Prerequisites: Valid email/server; supported SSO authorization service; server and browser flow.
- Variants: Waiting for browser; Completing SSO; Cancelled; Error.

### auth.login.password — Enter master password

- Kind: `state`; feasibility: `offline-form/live-action`.
- Source: [src/App/Views/LoginPage.xaml:257](../../src/App/Views/LoginPage.xaml#L257); [src/App/ViewModels/LoginViewModel.cs:2917](../../src/App/ViewModels/LoginViewModel.cs#L2917).
- Entry: Account > Continue.
- Content: Account/server badge; master password; optional Log in with a device action.
- Prerequisites: Valid URL and nonempty email open form; device availability starts a live probe.
- Variants: Device login available; Device login unavailable; Busy; Error.

### auth.login.device-quarantine — Device login quarantined

- Kind: `state`; feasibility: `controlled-fixture/live-recovery`.
- Source: [src/App/Views/LoginPage.xaml:287](../../src/App/Views/LoginPage.xaml#L287); [src/App/ViewModels/LoginViewModel.cs:420](../../src/App/ViewModels/LoginViewModel.cs#L420).
- Entry: Password stage with device-login quarantine record.
- Content: Warning InfoBar; optional clear-quarantine action.
- Prerequisites: Persisted quarantine state; no ordinary demo path.
- Variants: Eligible to clear; Quarantine period active; Corrupt state; Unavailable state.

### auth.login.device-wait — Log in with a device

- Kind: `state`; feasibility: `live`.
- Source: [src/App/Views/LoginPage.xaml:322](../../src/App/Views/LoginPage.xaml#L322).
- Entry: Password > Log in with a device.
- Content: Waiting ring; fingerprint phrase; expiration timestamp; cancel action.
- Prerequisites: Auth-request capable server and another signed-in device.
- Variants: Waiting without fingerprint; Fingerprint available; Expiry available; Cancelled/denied/expired result returns status.

### auth.login.verify-device — Verify new device

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/LoginPage.xaml:389](../../src/App/Views/LoginPage.xaml#L389).
- Entry: Authentication returns DeviceVerificationRequired.
- Content: Device verification code; resend code; Verify device primary action.
- Prerequisites: Live new-device challenge and mailbox access.
- Variants: Code entry; Resending; Verifying; Error.

### auth.login.password-change-required — Update master password

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/LoginPage.xaml:410](../../src/App/Views/LoginPage.xaml#L410).
- Entry: Authentication requires a password change.
- Content: Policy warning; new password; confirmation; optional hint; switch account; Update master password.
- Prerequisites: Organization password policy or administrator temporary-password scenario.
- Variants: Policy requirements; Busy; Validation error.

### auth.login.2fa-code — Two-step login: code

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/LoginPage.xaml:444](../../src/App/Views/LoginPage.xaml#L444); [src/App/ViewModels/LoginViewModel.cs:591](../../src/App/ViewModels/LoginViewModel.cs#L591).
- Entry: Authentication returns a typed-code two-factor challenge.
- Content: Provider picker; code input; remember device; recovery-code switch; email resend when applicable.
- Prerequisites: Authenticator, email, or YubiKey OTP challenge; YubiKey needed to complete its OTP flow.
- Variants: Authenticator; Email; YubiKey OTP; One provider; Multiple providers; Device-login request expiry notice.

### auth.login.2fa-recovery — Two-step login: recovery code

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/LoginPage.xaml:477](../../src/App/Views/LoginPage.xaml#L477); [src/App/ViewModels/LoginViewModel.cs:497](../../src/App/ViewModels/LoginViewModel.cs#L497).
- Entry: Two-factor > Use recovery code.
- Content: Recovery-code input; warning that recovery removes all configured two-factor methods; alternate-method link.
- Prerequisites: Existing two-factor challenge; recovery code required for completion.
- Variants: Provider picker hidden; Remember-device hidden; Busy; Error.

### auth.login.2fa-webauthn — Security-key verification

- Kind: `state`; feasibility: `live/native`.
- Source: [src/App/Views/LoginPage.xaml:444](../../src/App/Views/LoginPage.xaml#L444); [src/App/ViewModels/LoginViewModel.cs:504](../../src/App/ViewModels/LoginViewModel.cs#L504).
- Entry: Select WebAuthn two-factor provider.
- Content: Use security key primary action; remember device; recovery switch; no code field.
- Prerequisites: Live WebAuthn challenge and Windows WebAuthn/Hello or security key.
- Variants: Idle; Waiting for security key; Error.

### auth.login.2fa-duo — Duo two-step login

- Kind: `state`; feasibility: `live`.
- Source: [src/App/Views/LoginPage.xaml:444](../../src/App/Views/LoginPage.xaml#L444); [src/App/ViewModels/LoginViewModel.cs:508](../../src/App/ViewModels/LoginViewModel.cs#L508).
- Entry: Select Duo or Organization Duo provider.
- Content: Open Duo primary action; remember device; recovery switch; no code field.
- Prerequisites: Supported live Duo challenge.
- Variants: Duo; Organization Duo; Waiting for Duo; Error.

### auth.login.unlock-password — Unlock vault

- Kind: `state`; feasibility: `persisted-session`.
- Source: [src/App/Views/LoginPage.xaml:581](../../src/App/Views/LoginPage.xaml#L581).
- Entry: Restore a locked persisted password session.
- Content: Account/server badge; master password; optional Windows Hello; switch account; Unlock.
- Prerequisites: Saved authenticated session; ordinary demo does not create a persisted authentication route.
- Variants: Password entry; Hello available; Busy; Error.

### auth.login.unlock-pin — Unlock with PIN

- Kind: `state`; feasibility: `persisted-session`.
- Source: [src/App/Views/LoginPage.xaml:632](../../src/App/Views/LoginPage.xaml#L632).
- Entry: Restore locked persisted session with PIN configured.
- Content: PIN; optional Hello; alternative unlock method; switch account.
- Prerequisites: Previously configured PIN and matching saved session.
- Variants: Password alternative; Key Connector alternative; SSO alternative; Hello available.

### auth.login.unlock-keyconnector — Unlock through Key Connector

- Kind: `state`; feasibility: `live`.
- Source: [src/App/Views/LoginPage.xaml:581](../../src/App/Views/LoginPage.xaml#L581); [src/App/ViewModels/LoginViewModel.cs:713](../../src/App/ViewModels/LoginViewModel.cs#L713).
- Entry: Restore saved Key Connector session.
- Content: Account/server badge; Key Connector primary action; optional Hello; switch account; no password field.
- Prerequisites: Enterprise Key Connector setup and persisted session.
- Variants: Idle; Connecting; Error.

### auth.login.unlock-trusted-sso — Trusted-device sign-in

- Kind: `state`; feasibility: `live`.
- Source: [src/App/Views/LoginPage.xaml:581](../../src/App/Views/LoginPage.xaml#L581); [src/App/ViewModels/LoginViewModel.cs:714](../../src/App/ViewModels/LoginViewModel.cs#L714).
- Entry: Restore saved trusted-device SSO session.
- Content: Account/server badge; enterprise SSO unlock; optional Hello; switch account; no password field.
- Prerequisites: Enterprise trusted-device setup and saved session.
- Variants: Idle; SSO pending; Error.

### auth.login.trusted-approval — Approve this device

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/LoginPage.xaml:501](../../src/App/Views/LoginPage.xaml#L501).
- Entry: Authentication requires trusted-device approval.
- Content: Account/server badge; device/admin method picker; fingerprint; trust-after-approval; cancel waiting; switch account.
- Prerequisites: Enterprise approval flow; another eligible device or administrator.
- Variants: Single approval method; Multiple approval methods; Request not sent; Waiting with fingerprint.

### auth.login.jit-encryption — Set up account encryption

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/LoginPage.xaml:566](../../src/App/Views/LoginPage.xaml#L566).
- Entry: Authentication returns TDE v2 JIT provisioning.
- Content: Initialization explanation; trust-device checkbox; account switch; Finish initialization.
- Prerequisites: First enterprise sign-in requiring encryption setup.
- Variants: Trust choice editable; Trust choice fixed by policy; Busy; Error.

### auth.login.deletion-recovery — Account-deletion recovery

- Kind: `state`; feasibility: `controlled-fixture/live-recovery`.
- Source: [src/App/Views/LoginPage.xaml:209](../../src/App/Views/LoginPage.xaml#L209); [src/App/ViewModels/LoginViewModel.cs:367](../../src/App/ViewModels/LoginViewModel.cs#L367).
- Entry: Startup or account switch detects deletion recovery state.
- Content: Contextual warning; retry cleanup; clear this device; other account; recheck security storage; full reset action.
- Prerequisites: Prior deletion ledger or recovery fixture; ordinary demo unavailable.
- Variants: Prepared/uncertain outcome; Server confirmed; Deleted-account local residue; Uncertain-account local residue; Unreadable ledger.

### auth.login.status-busy — Login status and busy feedback

- Kind: `state`; feasibility: `offline/live/controlled-fixture`.
- Source: [src/App/Views/LoginPage.xaml:105](../../src/App/Views/LoginPage.xaml#L105); [src/App/Views/LoginPage.xaml:690](../../src/App/Views/LoginPage.xaml#L690).
- Entry: Validation, authentication, recovery, or async stage transition.
- Content: InfoBar; contextual busy primary text and ProgressRing; action gating.
- Prerequisites: Validation can be exercised without credentials; other results need matching flow.
- Variants: Informational; Success; Error; Busy.

### dialog.login.clear-quarantine — Clear device-login quarantine?

- Kind: `dialog`; feasibility: `controlled-fixture/live-recovery`.
- Source: [src/App/Views/LoginPage.xaml.cs:61](../../src/App/Views/LoginPage.xaml.cs#L61).
- Entry: Password quarantine notice > Clear quarantine.
- Content: Warning to review/revoke suspicious sessions first; Clear quarantine; Cancel.
- Prerequisites: Eligible persisted quarantine record.
- Variants: Confirmation.

### dialog.login.clear-local-deletion-data — Clear data on this device only?

- Kind: `dialog`; feasibility: `controlled-fixture/live-recovery`.
- Source: [src/App/Views/LoginPage.xaml.cs:92](../../src/App/Views/LoginPage.xaml.cs#L92).
- Entry: Prepared account-deletion recovery > Clear this device.
- Content: Uncertain server-result and permanent local-isolation warning; confirm; Cancel.
- Prerequisites: Prepared deletion-recovery state.
- Variants: Confirmation.

### dialog.login.reset-local-data — Clear all local data and exit?

- Kind: `dialog`; feasibility: `controlled-fixture/live-recovery`.
- Source: [src/App/Views/LoginPage.xaml.cs:123](../../src/App/Views/LoginPage.xaml.cs#L123).
- Entry: Unreadable deletion ledger > Clear all local data and exit.
- Content: Reset consequences; exact RESET text field; gated destructive action; Cancel.
- Prerequisites: Unreadable recovery ledger and local-reset service.
- Variants: Empty confirmation; Invalid confirmation; Exact RESET enables action.

### auth.register.form — Create account

- Kind: `state`; feasibility: `offline-form/live-action`.
- Source: [src/App/Views/RegisterPage.xaml:61](../../src/App/Views/RegisterPage.xaml#L61).
- Entry: Open Registration or edit submitted details.
- Content: Server/URL; email; optional name; password; confirmation; hint; Create account; Back to login.
- Prerequisites: Valid inputs and registration-enabled server for completion.
- Variants: US/EU/self-hosted; Busy disables entire form; Validation/operation error.

### auth.register.email-verification — Check email to continue

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/RegisterPage.xaml:182](../../src/App/Views/RegisterPage.xaml#L182).
- Entry: Server accepts registration email-verification request.
- Content: Submitted email; Web Vault completion explanation; Log in after verification; Change email.
- Prerequisites: Server email-verification outcome; mailbox and Web Vault for completion.
- Variants: Awaiting email verification.

### auth.register.uncertain — Registration result pending

- Kind: `state`; feasibility: `controlled-fixture/live-failure`.
- Source: [src/App/Views/RegisterPage.xaml:212](../../src/App/Views/RegisterPage.xaml#L212).
- Entry: Connection interrupted during registration submission.
- Content: Submitted email; uncertainty explanation; Try login; Change information.
- Prerequisites: Ambiguous registration result.
- Variants: Outcome uncertain.

### auth.register.success-navigation — Registration completed: return to login

- Kind: `transition`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/RegisterPage.xaml.cs:96](../../src/App/Views/RegisterPage.xaml.cs#L96).
- Entry: Registration result is Registered.
- Content: Login page prefilled with registered server and email.
- Prerequisites: Successful registration result; no dedicated success page exists.
- Variants: Login prefill.

## Vault details, editors, views, and operations

### vault.favorites — Favorites

- Kind: `view`; feasibility: `demo/live`.
- Source: [src/App/MainWindow.xaml:47](../../src/App/MainWindow.xaml#L47).
- Entry: vault:favorites.
- Content: Vault shell filtered to favorite items.
- Prerequisites: At least one synthetic favorite.
- Variants: Populated; Empty result.

### vault.folders — Folders

- Kind: `view`; feasibility: `demo/live`.
- Source: [src/App/MainWindow.xaml:62](../../src/App/MainWindow.xaml#L62).
- Entry: vault:folder:<id>.
- Content: Dynamic sidebar folder name; filtered items; rename/delete folder actions.
- Prerequisites: Synthetic folders.
- Variants: Named folder; Empty folder.

### vault.organizations — Organizations

- Kind: `view`; feasibility: `live`.
- Source: [src/App/MainWindow.xaml:81](../../src/App/MainWindow.xaml#L81).
- Entry: vault:organization:<id>.
- Content: Dynamic organization navigation; shared items and permissions.
- Prerequisites: Organization membership and decryptable organization key.
- Variants: Writable organization; Read-only organization; Missing organization key.

### vault.collections — Collections

- Kind: `view`; feasibility: `live`.
- Source: [src/App/MainWindow.xaml:88](../../src/App/MainWindow.xaml#L88).
- Entry: vault:collection:<id>.
- Content: Dynamic collection navigation; shared items; collection placement.
- Prerequisites: Organization and collection membership.
- Variants: Writable collection; Read-only collection; Empty collection.

### vault.archive — Archive

- Kind: `view`; feasibility: `ui/live`.
- Source: [src/App/MainWindow.xaml:95](../../src/App/MainWindow.xaml#L95).
- Entry: vault:archive.
- Content: Archived list; detail; unarchive action.
- Prerequisites: Server/account archive capability for mutations.
- Variants: Populated; Empty; Selection.

### vault.trash — Recycle bin

- Kind: `view`; feasibility: `demo/live`.
- Source: [src/App/MainWindow.xaml:98](../../src/App/MainWindow.xaml#L98).
- Entry: vault:trash.
- Content: Deleted-item count; deleted list; restore/permanent-delete detail actions; empty-bin action.
- Prerequisites: Synthetic deleted items.
- Variants: Populated; Empty; Selection.

### vault.empty — Empty vault

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:741](../../src/App/Views/VaultPage.xaml#L741).
- Entry: Account with no active items.
- Content: Empty-vault glyph; first-item guidance; add action.
- Prerequisites: Disposable empty synthetic account.

### vault.no-results — No matching items

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:759](../../src/App/Views/VaultPage.xaml#L759).
- Entry: Search for a nonmatching value or combine incompatible filters.
- Content: Search term; filters; no-results message.
- Prerequisites: Any populated vault.

### vault.no-selection — No item selected

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:1376](../../src/App/Views/VaultPage.xaml#L1376).
- Entry: Open vault before selecting an item.
- Content: Right-pane no-selection glyph and guidance.
- Prerequisites: Unlocked session.

### vault.selection — Batch selection

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:391](../../src/App/Views/VaultPage.xaml#L391).
- Entry: Vault > Select > choose items.
- Content: Selected count; Done; select all; move folder; organization transfer; add/remove collection; archive/unarchive; trash/restore/delete.
- Prerequisites: Synthetic editable items; live organization data for placement actions.
- Variants: Normal items; Archived items; Deleted items; Mixed-permission selection warning.

### vault.pending-create — Pending item-creation reconciliation

- Kind: `state`; feasibility: `conditional`.
- Source: [src/App/Views/VaultPage.xaml:231](../../src/App/Views/VaultPage.xaml#L231).
- Entry: Create response interrupted or successful creation cannot be located.
- Content: Warning title; risk description; retained-draft note; resync check; abandon pending state.
- Prerequisites: Controlled ambiguous-create response; do not infer from happy-path live data.
- Variants: Commit unknown; Server accepted but item unresolved; Recoverable current draft; No recoverable draft; Abandoned warning.

### vault.item-warning — Vault operation and decryption warnings

- Kind: `state`; feasibility: `conditional`.
- Source: [src/App/Views/VaultPage.xaml:477](../../src/App/Views/VaultPage.xaml#L477).
- Entry: Offline unlock, partial sync, or failed mutation.
- Content: Offline warning; saved-needs-sync warning; operation error; skipped-item count; unavailable shared vault warning.
- Prerequisites: Controlled cached or partial-sync scenarios.
- Variants: Offline; Saved-needs-sync; Decrypt failures; Organization-key failures; Operation error.

### vault.detail.login — Login details

- Kind: `detail`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:16](../../src/App/Views/VaultPage.xaml#L16).
- Entry: vault:type:Login > item.
- Content: Username; masked password; TOTP; website; optional passkeys and password history; common name/folder/favorite; notes; attachments; custom fields; item history; actions.
- Prerequisites: Synthetic item of this type.
- Variants: Masked; Revealed where permitted; Reprompt locked; Attachments; Custom fields.

### vault.editor.login — Create or edit login

- Kind: `editor`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:906](../../src/App/Views/VaultPage.xaml#L906).
- Entry: Vault > Add > Login; or item > Edit.
- Content: Username; password; authenticator secret; primary URI; common name; save destination; folder; favorite; notes; master-password reprompt; custom fields; Save/Cancel.
- Prerequisites: Synthetic writable item.
- Variants: Create; Edit with fixed type; Invalid required fields; Saving; Saved-needs-sync warning.

### vault.detail.card — Payment card details

- Kind: `detail`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:68](../../src/App/Views/VaultPage.xaml#L68).
- Entry: vault:type:Card > item.
- Content: Cardholder; masked card number; expiry; brand; CVV; common name/folder/favorite; notes; attachments; custom fields; item history; actions.
- Prerequisites: Synthetic item of this type.
- Variants: Masked; Revealed where permitted; Reprompt locked; Attachments; Custom fields.

### vault.editor.card — Create or edit payment card

- Kind: `editor`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:927](../../src/App/Views/VaultPage.xaml#L927).
- Entry: Vault > Add > Payment card; or item > Edit.
- Content: Cardholder; card number; expiry month/year; security code; brand expander; common name; save destination; folder; favorite; notes; master-password reprompt; custom fields; Save/Cancel.
- Prerequisites: Synthetic writable item.
- Variants: Create; Edit with fixed type; Invalid required fields; Saving; Saved-needs-sync warning.

### vault.detail.identity — Identity details

- Kind: `detail`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:81](../../src/App/Views/VaultPage.xaml#L81).
- Entry: vault:type:Identity > item.
- Content: Full name; username; company; email; phone; ID number; address; common name/folder/favorite; notes; attachments; custom fields; item history; actions.
- Prerequisites: Synthetic item of this type.
- Variants: Masked; Revealed where permitted; Reprompt locked; Attachments; Custom fields.

### vault.editor.identity — Create or edit identity

- Kind: `editor`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:962](../../src/App/Views/VaultPage.xaml#L962).
- Entry: Vault > Add > Identity; or item > Edit.
- Content: First/last name; email; phone; address; more fields for username/company/ID/passport; common name; save destination; folder; favorite; notes; master-password reprompt; custom fields; Save/Cancel.
- Prerequisites: Synthetic writable item.
- Variants: Create; Edit with fixed type; Invalid required fields; Saving; Saved-needs-sync warning.

### vault.detail.note — Secure note details

- Kind: `detail`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:96](../../src/App/Views/VaultPage.xaml#L96).
- Entry: vault:type:Note > item.
- Content: Multiline note content; common name/folder/favorite; notes; attachments; custom fields; item history; actions.
- Prerequisites: Synthetic item of this type.
- Variants: Masked; Revealed where permitted; Reprompt locked; Attachments; Custom fields.

### vault.editor.note — Create or edit secure note

- Kind: `editor`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:1086](../../src/App/Views/VaultPage.xaml#L1086).
- Entry: Vault > Add > Secure note; or item > Edit.
- Content: Multiline secure-note content; common name; save destination; folder; favorite; notes; master-password reprompt; custom fields; Save/Cancel.
- Prerequisites: Synthetic writable item.
- Variants: Create; Edit with fixed type; Invalid required fields; Saving; Saved-needs-sync warning.

### vault.detail.ssh — SSH key details

- Kind: `detail`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:103](../../src/App/Views/VaultPage.xaml#L103).
- Entry: vault:type:Ssh > item.
- Content: Public key; masked private key; fingerprint; common name/folder/favorite; notes; attachments; custom fields; item history; actions.
- Prerequisites: Synthetic item of this type.
- Variants: Masked; Revealed where permitted; Reprompt locked; Attachments; Custom fields.

### vault.editor.ssh — Create or edit ssh key

- Kind: `editor`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:1008](../../src/App/Views/VaultPage.xaml#L1008).
- Entry: Vault > Add > SSH key; or item > Edit.
- Content: Public key; private key; fingerprint; common name; save destination; folder; favorite; notes; master-password reprompt; custom fields; Save/Cancel.
- Prerequisites: Synthetic writable item.
- Variants: Create; Edit with fixed type; Invalid required fields; Saving; Saved-needs-sync warning.

### vault.detail.bank — Bank account details

- Kind: `detail`; feasibility: `conditional`.
- Source: [src/App/Views/VaultPage.xaml:114](../../src/App/Views/VaultPage.xaml#L114).
- Entry: vault:type:BankAccount > item.
- Content: Bank; holder; account type; account/routing/branch numbers; PIN; SWIFT; IBAN; contact phone; common name/folder/favorite; notes; attachments; custom fields; item history; actions.
- Prerequisites: Server/account supporting this cipher type or source-derived schematic.
- Variants: Masked; Revealed where permitted; Reprompt locked; Attachments; Custom fields.

### vault.editor.bank — Create or edit bank account

- Kind: `editor`; feasibility: `conditional`.
- Source: [src/App/Views/VaultPage.xaml:1028](../../src/App/Views/VaultPage.xaml#L1028).
- Entry: Vault > Add > Bank account; or item > Edit.
- Content: Bank; holder; type; account/routing/branch numbers; PIN; SWIFT; IBAN; contact phone; common name; save destination; folder; favorite; notes; master-password reprompt; custom fields; Save/Cancel.
- Prerequisites: CanWriteNewCipherTypes capability required for create/edit.
- Variants: Create; Edit with fixed type; Invalid required fields; Saving; Saved-needs-sync warning.

### vault.detail.license — Driver's license details

- Kind: `detail`; feasibility: `conditional`.
- Source: [src/App/Views/VaultPage.xaml:132](../../src/App/Views/VaultPage.xaml#L132).
- Entry: vault:type:DriversLicense > item.
- Content: Name; birth date; license number; country/state; issue/expiry dates; issuer; class; common name/folder/favorite; notes; attachments; custom fields; item history; actions.
- Prerequisites: Server/account supporting this cipher type or source-derived schematic.
- Variants: Masked; Revealed where permitted; Reprompt locked; Attachments; Custom fields.

### vault.editor.license — Create or edit driver's license

- Kind: `editor`; feasibility: `conditional`.
- Source: [src/App/Views/VaultPage.xaml:1046](../../src/App/Views/VaultPage.xaml#L1046).
- Entry: Vault > Add > Driver's license; or item > Edit.
- Content: First/middle/last name; birth date; number; country/state; issue/expiry; issuer; class; common name; save destination; folder; favorite; notes; master-password reprompt; custom fields; Save/Cancel.
- Prerequisites: CanWriteNewCipherTypes capability required for create/edit.
- Variants: Create; Edit with fixed type; Invalid required fields; Saving; Saved-needs-sync warning.

### vault.detail.passport — Passport details

- Kind: `detail`; feasibility: `conditional`.
- Source: [src/App/Views/VaultPage.xaml:149](../../src/App/Views/VaultPage.xaml#L149).
- Entry: vault:type:Passport > item.
- Content: Name; birth date; sex; birth place; nationality; country; passport number/type; national ID; issuer; issue/expiry; common name/folder/favorite; notes; attachments; custom fields; item history; actions.
- Prerequisites: Server/account supporting this cipher type or source-derived schematic.
- Variants: Masked; Revealed where permitted; Reprompt locked; Attachments; Custom fields.

### vault.editor.passport — Create or edit passport

- Kind: `editor`; feasibility: `conditional`.
- Source: [src/App/Views/VaultPage.xaml:1065](../../src/App/Views/VaultPage.xaml#L1065).
- Entry: Vault > Add > Passport; or item > Edit.
- Content: Name parts; birth date; sex; birth place; nationality; country; number/type; national ID; issuer; issue/expiry; common name; save destination; folder; favorite; notes; master-password reprompt; custom fields; Save/Cancel.
- Prerequisites: CanWriteNewCipherTypes capability required for create/edit.
- Variants: Create; Edit with fixed type; Invalid required fields; Saving; Saved-needs-sync warning.

### vault.editor.options — Editor destination and extra fields

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:855](../../src/App/Views/VaultPage.xaml#L855).
- Entry: Any item editor > common fields / extra options.
- Content: Personal or organization destination; collection checkboxes; folder; favorite; notes; reprompt; custom text/hidden/boolean fields; remove/add field.
- Prerequisites: Organization destination and collections require live membership.
- Variants: Personal destination; Organization destination; Custom text; Custom hidden; Custom boolean; Linked/unsupported fields preserved and not editable.

### vault.login.passkeys — Stored passkey information

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:32](../../src/App/Views/VaultPage.xaml#L32).
- Entry: Login item containing passkeys.
- Content: RP ID; account display name; discoverability; creation date.
- Prerequisites: Synthetic login with passkey; Debug demo includes localhost credential.

### vault.login.history — Password history

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:47](../../src/App/Views/VaultPage.xaml#L47).
- Entry: Login details > expand password history.
- Content: Previous masked passwords; change dates; reveal/copy controls.
- Prerequisites: Synthetic login with password history.

### vault.attachments — Attachments

- Kind: `state`; feasibility: `ui/live`.
- Source: [src/App/Views/VaultPage.xaml:1208](../../src/App/Views/VaultPage.xaml#L1208).
- Entry: Item detail > attachments section.
- Content: Count; filename; size; download/delete icon buttons; add attachment; progress/operation status.
- Prerequisites: Live synthetic attachment and writable item for transfers.
- Variants: Empty; Populated; Upload/download/delete busy; Transfer failure.

### vault.add-menu — Add item menu

- Kind: `flyout`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:333](../../src/App/Views/VaultPage.xaml#L333).
- Entry: Vault > Add.
- Content: Eight item-type choices; new folder.
- Prerequisites: New types are hidden without capability.

### vault.filter-menu — Vault refinement filters

- Kind: `flyout`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:526](../../src/App/Views/VaultPage.xaml#L526).
- Entry: Vault > Filter.
- Content: Has TOTP; has attachments; has URI; favorite only; clear all.
- Prerequisites: Populated synthetic vault.
- Variants: No refinements; Active refinements.

### vault.sort-menu — Vault sort choices

- Kind: `flyout`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:505](../../src/App/Views/VaultPage.xaml#L505).
- Entry: Vault > sort ComboBox.
- Content: Name ascending; name descending; recent modification; creation time.
- Prerequisites: Any vault.

### vault.saved-views-menu — Saved views

- Kind: `flyout`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:573](../../src/App/Views/VaultPage.xaml#L573).
- Entry: Vault > Views.
- Content: Saved view names; delete each saved view; save current view.
- Prerequisites: Local saved view data.
- Variants: No saved views; Populated.

### vault.row-menu — Vault row actions

- Kind: `flyout`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml:670](../../src/App/Views/VaultPage.xaml#L670).
- Entry: Hover/focus item > More.
- Content: Edit; clone; favorite; archive/unarchive; move to trash; restore; permanent delete.
- Prerequisites: Synthetic item; capability/permission controls visibility.
- Variants: Active; Archived; Deleted; Read-only.

### vault.move-menu — Move selected items to folder

- Kind: `flyout`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml.cs:501](../../src/App/Views/VaultPage.xaml.cs#L501).
- Entry: Select items > Move.
- Content: No folder; each folder destination.
- Prerequisites: At least one selected item.

### vault.reprompt — Verify master password

- Kind: `dialog`; feasibility: `ui/live`.
- Source: [src/App/Views/VaultPage.xaml.cs:265](../../src/App/Views/VaultPage.xaml.cs#L265).
- Entry: Protected item reveal/copy/open/edit or unlock-sensitive-fields action.
- Content: PasswordBox; explanatory text; verify/cancel.
- Prerequisites: Real master password for successful verification; protected synthetic item.
- Variants: Initial; Incorrect password.

### vault.transfer — Transfer to organization

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Views/VaultPage.xaml.cs:674](../../src/App/Views/VaultPage.xaml.cs#L674).
- Entry: Personal item > Transfer to organization.
- Content: Organization ComboBox; writable collection checkboxes; transfer/cancel.
- Prerequisites: Writable organization collection.
- Variants: Single item; Batch selected items.

### vault.collection-select — Manage item collections

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Views/VaultPage.xaml.cs:774](../../src/App/Views/VaultPage.xaml.cs#L774).
- Entry: Organization item > Manage collections; selection > Add/remove collection.
- Content: Collection checkboxes; locked read-only assignments; save/cancel.
- Prerequisites: Shared synthetic items and collection permissions.
- Variants: Single-item membership; Batch add; Batch remove.

### vault.folder-create — New folder

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml.cs:858](../../src/App/Views/VaultPage.xaml.cs#L858).
- Entry: Vault > Add > New folder.
- Content: Folder-name TextBox; confirm/cancel.
- Prerequisites: Unlocked session.

### vault.folder-rename — Rename folder

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml.cs:865](../../src/App/Views/VaultPage.xaml.cs#L865).
- Entry: Select folder > Rename folder.
- Content: Prefilled folder-name TextBox; confirm/cancel.
- Prerequisites: Synthetic folder.

### vault.saved-view-create — Save current view

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml.cs:1103](../../src/App/Views/VaultPage.xaml.cs#L1103).
- Entry: Vault > Views > Save current view.
- Content: View-name TextBox; save/cancel.
- Prerequisites: Search/filter/sort state.

### vault.trash-confirm — Move item to recycle bin

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml.cs:828](../../src/App/Views/VaultPage.xaml.cs#L828).
- Entry: Item detail or row menu > Delete.
- Content: Named item; delete/cancel.
- Prerequisites: Disposable active item.

### vault.delete-confirm — Permanently delete item

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml.cs:854](../../src/App/Views/VaultPage.xaml.cs#L854).
- Entry: Deleted item detail or row menu > Permanent delete.
- Content: Named item; irreversible warning; delete/cancel.
- Prerequisites: Disposable deleted item.

### vault.delete-selected-confirm — Permanently delete selected items

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml.cs:524](../../src/App/Views/VaultPage.xaml.cs#L524).
- Entry: Recycle bin > selection > Permanent delete.
- Content: Selected count; irreversible warning; delete/cancel.
- Prerequisites: Disposable deleted items.

### vault.empty-bin-confirm — Empty recycle bin

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml.cs:889](../../src/App/Views/VaultPage.xaml.cs#L889).
- Entry: Recycle bin > Empty recycle bin.
- Content: Total deleted count; irreversible warning; delete/cancel.
- Prerequisites: Disposable deleted items.

### vault.folder-delete-confirm — Delete folder

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/VaultPage.xaml.cs:878](../../src/App/Views/VaultPage.xaml.cs#L878).
- Entry: Select folder > Delete folder.
- Content: Folder name; contained items move out; delete/cancel.
- Prerequisites: Disposable synthetic folder.

### vault.attachment-delete-confirm — Delete attachment

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Views/VaultPage.xaml.cs:1062](../../src/App/Views/VaultPage.xaml.cs#L1062).
- Entry: Attachment > Delete.
- Content: Filename; irreversible warning; delete/cancel.
- Prerequisites: Disposable attachment.

### vault.pending-abandon-confirm — Abandon pending item state

- Kind: `dialog`; feasibility: `conditional`.
- Source: [src/App/Views/VaultPage.xaml.cs:432](../../src/App/Views/VaultPage.xaml.cs#L432).
- Entry: Pending-creation warning > Abandon pending state.
- Content: Server commit status; duplicate risk; abandon/back to check.
- Prerequisites: Controlled pending-creation ledger.

### vault.grouping — Grouped vault list

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/ViewModels/VaultViewModel.cs:729](../../src/App/ViewModels/VaultViewModel.cs#L729).
- Entry: All items or type filter; change sort order.
- Content: Type headers for aggregate views; folder headers for a type; unassigned-folder group; no headers for date sorting and specific folder/organization/collection.
- Prerequisites: Multiple synthetic types and folders.
- Variants: Grouped by type; Grouped by folder; Flat date ordering.

## Send workflows

### send.editor.text — New or edit text Send

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/SendEditorDialog.xaml:7](../../src/App/Views/SendEditorDialog.xaml#L7).
- Entry: Send > Add Send; row > Edit.
- Content: Text/file type; name; text; hide text; deletion date; audience; maximum accesses; expiration; access password; disabled; hide email; private notes; Save/Cancel.
- Prerequisites: Live server for shareable result.
- Variants: New; Edit; Validation error; Saving; Custom deletion date; Custom expiration date.

### send.editor.file — New or edit file Send

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/SendEditorDialog.xaml:69](../../src/App/Views/SendEditorDialog.xaml#L69).
- Entry: Send editor > File.
- Content: File picker; filename; 500 MB copy; common restrictions and notes; Save/Cancel.
- Prerequisites: Synthetic file; live server for actual upload/download.
- Variants: No file selected; File selected; Replacement file; Oversized/unreadable file error.

### send.row-menu — Send row actions

- Kind: `flyout`; feasibility: `demo/live`.
- Source: [src/App/Views/SendPage.xaml:243](../../src/App/Views/SendPage.xaml#L243).
- Entry: Hover/focus Send row > More.
- Content: Copy link; edit; delete.
- Prerequisites: Synthetic Send.

### send.delete-confirm — Delete Send

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/SendPage.xaml.cs:137](../../src/App/Views/SendPage.xaml.cs#L137).
- Entry: Send row menu > Delete.
- Content: Named Send; irreversible warning; delete/cancel.
- Prerequisites: Disposable Send.

### send.receive — Open a received Send

- Kind: `state`; feasibility: `ui/live`.
- Source: [src/App/Views/SendPage.xaml:61](../../src/App/Views/SendPage.xaml#L61).
- Entry: Send > expand received-link section.
- Content: Send URL; optional access password; Open; received text; save file.
- Prerequisites: Valid live Send URL and matching key; cross-server URL support as configured.
- Variants: Initial; Receiving; Text result; File result; Wrong password/invalid link; Demo unavailable.

## Generator workflows

### generator.password — Password generator

- Kind: `dialog-tab`; feasibility: `demo/live`.
- Source: [src/App/Views/GeneratorDialog.xaml:33](../../src/App/Views/GeneratorDialog.xaml#L33).
- Entry: Navigation > Generator > Password.
- Content: Generated password; regenerate/copy; strength; length slider/number; uppercase/lowercase/digits/symbols; minimum counts; avoid ambiguous; history.
- Prerequisites: Unlocked session.
- Variants: Default options; Customized; Organization policy notice; Policy-constrained controls.

### generator.passphrase — Passphrase generator

- Kind: `dialog-tab`; feasibility: `demo/live`.
- Source: [src/App/Views/GeneratorDialog.xaml:171](../../src/App/Views/GeneratorDialog.xaml#L171).
- Entry: Generator > Passphrase.
- Content: Generated phrase; strength; regenerate/copy; word count; separator; capitalization; include number; history.
- Prerequisites: Unlocked session.
- Variants: Default; Customized.

### generator.username — Username generator

- Kind: `dialog-tab`; feasibility: `demo/live`.
- Source: [src/App/Views/GeneratorDialog.xaml:252](../../src/App/Views/GeneratorDialog.xaml#L252).
- Entry: Generator > Username.
- Content: Generated username; type; random-word options; plus-address email; catch-all domain; regenerate/copy; history.
- Prerequisites: Synthetic email/domain for respective modes.
- Variants: Random word; Plus-addressed email; Catch-all email; Invalid email/domain.

### generator.history — Generator history

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/Views/GeneratorHistoryDialog.xaml:7](../../src/App/Views/GeneratorHistoryDialog.xaml#L7).
- Entry: Generator > History.
- Content: Values and timestamps; row copy; clear history; close.
- Prerequisites: Generate at least one value.
- Variants: Empty; Populated.

### generator.clear-confirm — Clear generator history

- Kind: `dialog`; feasibility: `demo/live`.
- Source: [src/App/MainWindow.xaml.cs:541](../../src/App/MainWindow.xaml.cs#L541).
- Entry: History > Clear history.
- Content: Irreversible-clear warning; clear/cancel.
- Prerequisites: History entries; confirmation appears after history closes.

## Import and export workflows

### import.export-confirm — Confirm vault export

- Kind: `dialog`; feasibility: `ui/live`.
- Source: [src/App/Views/ImportExportPage.xaml.cs:84](../../src/App/Views/ImportExportPage.xaml.cs#L84).
- Entry: Import/export > Export.
- Content: Current master-password field; verify/cancel; inline wrong-password error.
- Prerequisites: Live master-password account for successful export.
- Variants: Initial; Wrong password.

## Devices and emergency access

### devices.approve-confirm — Confirm device login request

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Views/DevicesPage.xaml.cs:62](../../src/App/Views/DevicesPage.xaml.cs#L62).
- Entry: Pending request > Approve.
- Content: Fingerprint phrase; compare-device warning; confirm and approve/cancel.
- Prerequisites: Second synthetic device auth request.

### devices.deny-confirm — Deny login request

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Views/DevicesPage.xaml.cs:84](../../src/App/Views/DevicesPage.xaml.cs#L84).
- Entry: Pending request > Deny.
- Content: Request device type; deny/cancel.
- Prerequisites: Disposable pending request.

### emergency.invite — Invite emergency contact

- Kind: `dialog`; feasibility: `ui/live`.
- Source: [src/App/Views/EmergencyAccessPage.xaml.cs:108](../../src/App/Views/EmergencyAccessPage.xaml.cs#L108).
- Entry: Emergency access > Invite.
- Content: Contact email; View/Takeover type; wait days 1-90; invite/cancel; inline error.
- Prerequisites: Synthetic recipient account; mail sink if invitation delivery is exercised.
- Variants: Initial; Validation/server error.

### emergency.update — Edit emergency access

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Views/EmergencyAccessPage.xaml.cs:223](../../src/App/Views/EmergencyAccessPage.xaml.cs#L223).
- Entry: Trusted contact > Edit.
- Content: View/Takeover type; wait days; save/cancel; inline error.
- Prerequisites: Existing synthetic trusted relationship.

### emergency.reject-confirm — Reject emergency-access request

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Views/EmergencyAccessPage.xaml.cs:170](../../src/App/Views/EmergencyAccessPage.xaml.cs#L170).
- Entry: Trusted contact in recovery > Reject.
- Content: Contact email; reject/cancel.
- Prerequisites: RecoveryInitiated relationship.

### emergency.remove-confirm — Remove emergency contact

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Views/EmergencyAccessPage.xaml.cs:186](../../src/App/Views/EmergencyAccessPage.xaml.cs#L186).
- Entry: Trusted contact > Remove.
- Content: Contact email; irreversible warning; remove/cancel.
- Prerequisites: Disposable trusted relationship.

### emergency.recovered — Recovered vault

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Views/EmergencyAccessPage.xaml.cs:292](../../src/App/Views/EmergencyAccessPage.xaml.cs#L292).
- Entry: Granted View relationship > View vault.
- Content: Grantor email; item count; read-only name/subtitle list; close.
- Prerequisites: RecoveryApproved View relationship and encrypted grantor key.

### emergency.takeover — Take over account and reset master password

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Views/EmergencyAccessPage.xaml.cs:332](../../src/App/Views/EmergencyAccessPage.xaml.cs#L332).
- Entry: Granted Takeover relationship > Take over.
- Content: Grantor identity; new password; confirm password; irreversible warning; confirm/cancel; error.
- Prerequisites: Disposable RecoveryApproved Takeover relationship.
- Variants: Initial; Mismatch/error.

## Settings sections and workflows

### settings.account — Settings: Account

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/SettingsPage.xaml:23](../../src/App/Views/SettingsPage.xaml#L23).
- Entry: Settings top section.
- Content: Email/server; Edit Profile; Change Master Password; Encryption Settings and KDF summary.
- Prerequisites: Unlocked session.
- Variants: Account and KDF summary.

### settings.security — Settings: Security

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/SettingsPage.xaml:59](../../src/App/Views/SettingsPage.xaml#L59).
- Entry: Settings security section.
- Content: Session timeout/action; PIN; Windows Hello; trusted device; two-factor; Emergency Access navigation.
- Prerequisites: Unlocked session; device/account-specific eligibility for actions.
- Variants: Timeout; PIN; Hello; Trusted device; Two-factor.

### settings.preferences — Settings: Preferences

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/SettingsPage.xaml:172](../../src/App/Views/SettingsPage.xaml#L172).
- Entry: Settings preferences section.
- Content: Clipboard timeout; disabled minimize-after-copy toggle.
- Prerequisites: Local settings.
- Variants: Clipboard timeout options.

### settings.application — Settings: Application

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/SettingsPage.xaml:206](../../src/App/Views/SettingsPage.xaml#L206).
- Entry: Settings application section.
- Content: Tray behavior; startup; browser integration; acceleration; website icons; SSH; screenshots; theme; language.
- Prerequisites: Local settings; packaging-dependent browser integration.
- Variants: Tray on/off; Portable/MSIX; Screenshots allowed/blocked.

### settings.about — Settings: About

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/SettingsPage.xaml:321](../../src/App/Views/SettingsPage.xaml#L321).
- Entry: Settings > About expander.
- Content: Version summary; expanded app/Windows/.NET versions and architecture; Copy diagnostics.
- Prerequisites: Unlocked session.
- Variants: Collapsed; Expanded.

### settings.danger — Settings: Danger zone

- Kind: `state`; feasibility: `demo-form/live-action`.
- Source: [src/App/Views/SettingsPage.xaml:364](../../src/App/Views/SettingsPage.xaml#L364).
- Entry: Settings bottom section.
- Content: Delete account action.
- Prerequisites: Valid deletion flow requires unlocked matching persisted session.
- Variants: Action visible.

### settings.timeout-policy — Policy-constrained timeout

- Kind: `state`; feasibility: `policy-fixture/live`.
- Source: [src/App/Views/SettingsPage.xaml:64](../../src/App/Views/SettingsPage.xaml#L64); [src/App/ViewModels/SettingsViewModel.cs:95](../../src/App/ViewModels/SettingsViewModel.cs#L95).
- Entry: Settings with organization session policy.
- Content: Individually gated timeout options; policy-only option; policy hint; potentially disabled timeout-action selector.
- Prerequisites: Organization policy snapshot; ordinary demo has no such policy.
- Variants: Normal timeout options; Policy-only timeout; Forced timeout action.

### settings.pin-policy — PIN allowed or prohibited

- Kind: `state`; feasibility: `policy-fixture/live`.
- Source: [src/App/Views/SettingsPage.xaml:116](../../src/App/Views/SettingsPage.xaml#L116); [src/App/ViewModels/SettingsViewModel.cs:457](../../src/App/ViewModels/SettingsViewModel.cs#L457); [src/App/Views/SettingsPage.xaml.cs:38](../../src/App/Views/SettingsPage.xaml.cs#L38).
- Entry: Settings with PIN restriction policy.
- Content: PIN toggle; disabled prohibited state.
- Prerequisites: RemoveUnlockWithPin policy variant; existing PIN is cleared when prohibited.
- Variants: Allowed; Configured; Prohibited.

### settings.hello — Windows Hello availability

- Kind: `state`; feasibility: `native`.
- Source: [src/App/Views/SettingsPage.xaml:126](../../src/App/Views/SettingsPage.xaml#L126); [src/App/Views/SettingsPage.xaml.cs:70](../../src/App/Views/SettingsPage.xaml.cs#L70); [src/App/ViewModels/SettingsViewModel.cs:532](../../src/App/ViewModels/SettingsViewModel.cs#L532).
- Entry: Settings loads Hello availability.
- Content: Hello toggle and description; unavailable text.
- Prerequisites: Windows Hello availability/configuration.
- Variants: Available; Unavailable; Already configured.

### settings.trusted-device — Trusted-device eligibility and status

- Kind: `state`; feasibility: `live/persisted-session`.
- Source: [src/App/Views/SettingsPage.xaml:137](../../src/App/Views/SettingsPage.xaml#L137); [src/App/ViewModels/SettingsViewModel.cs:562](../../src/App/ViewModels/SettingsViewModel.cs#L562).
- Entry: Settings loads account device-trust eligibility.
- Content: Contextual device-trust card and action.
- Prerequisites: Matching persisted session and supported device-trust capability; ordinary demo cannot trust device.
- Variants: Hidden; Eligible untrusted; Trusted; Eligible card hidden while busy.

### settings.tray — Tray behavior

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/SettingsPage.xaml:211](../../src/App/Views/SettingsPage.xaml#L211); [src/App/ViewModels/SettingsViewModel.cs:194](../../src/App/ViewModels/SettingsViewModel.cs#L194).
- Entry: Settings > Application > Tray icon.
- Content: Tray icon toggle; minimize/close-to-tray child toggles.
- Prerequisites: Local preferences.
- Variants: Tray on enables child controls; Tray off disables child controls.

### settings.browser-integration — Portable and MSIX browser integration

- Kind: `state`; feasibility: `packaging-dependent`.
- Source: [src/App/Views/SettingsPage.xaml:240](../../src/App/Views/SettingsPage.xaml#L240); [src/App/ViewModels/SettingsViewModel.cs:407](../../src/App/ViewModels/SettingsViewModel.cs#L407).
- Entry: Settings > Application > Browser integration.
- Content: Browser integration toggle.
- Prerequisites: Only enabled in unpackaged build.
- Variants: Portable enabled; MSIX disabled.

### settings.theme — Application theme

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/SettingsPage.xaml:291](../../src/App/Views/SettingsPage.xaml#L291).
- Entry: Settings > Theme.
- Content: System, Light, and Dark options applied immediately.
- Prerequisites: Local preference.
- Variants: System; Light; Dark.

### settings.future-controls — Disabled future settings

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/SettingsPage.xaml:195](../../src/App/Views/SettingsPage.xaml#L195).
- Entry: Settings Preferences/Application sections.
- Content: Disabled minimize-after-copy, startup, acceleration, SSH expander, and language controls.
- Prerequisites: No implemented enabling path.
- Variants: Disabled.

### settings.trust.error — Unable to trust this device

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:88](../../src/App/Views/SettingsPage.xaml.cs#L88).
- Entry: Trust current device fails.
- Content: Error message; OK.
- Prerequisites: Eligible device-trust operation.
- Variants: Failure.

### settings.profile.edit — Edit Profile

- Kind: `dialog`; feasibility: `demo-form/live-action`.
- Source: [src/App/Views/SettingsPage.xaml.cs:130](../../src/App/Views/SettingsPage.xaml.cs#L130).
- Entry: Settings > Account > Edit Profile.
- Content: Display-name field; Save; Cancel; inline error.
- Prerequisites: Demo can open; save uses live API.
- Variants: Initial; Error.

### settings.password.change — Change Master Password

- Kind: `dialog`; feasibility: `demo-form/live-action`.
- Source: [src/App/Views/SettingsPage.xaml.cs:188](../../src/App/Views/SettingsPage.xaml.cs#L188).
- Entry: Settings > Account > Change Master Password.
- Content: Current/new/confirm passwords; optional hint; Change; Cancel; inline error.
- Prerequisites: Completion requires matching persisted authenticated session; success logs out.
- Variants: Initial; Validation/operation error.

### settings.kdf.change — Encryption Settings

- Kind: `dialog`; feasibility: `demo-form/live-action`.
- Source: [src/App/Views/SettingsPage.xaml.cs:269](../../src/App/Views/SettingsPage.xaml.cs#L269).
- Entry: Settings > Account > Encryption Settings.
- Content: Current password; PBKDF2 iterations NumberBox; Apply; Cancel; inline error; no KDF-type selector.
- Prerequisites: Completion requires matching persisted authenticated session.
- Variants: Initial; Error.

### settings.pin.setup — Set PIN

- Kind: `dialog`; feasibility: `demo-form/persisted-session-action`.
- Source: [src/App/Views/SettingsPage.xaml.cs:354](../../src/App/Views/SettingsPage.xaml.cs#L354).
- Entry: Settings > PIN toggle on.
- Content: PIN and confirmation; Set; Cancel.
- Prerequisites: Demo can open; completion requires persisted session (src/Vault/PinService.cs:38).
- Variants: Initial; Too short; Mismatch.

### settings.hello.failure — Windows Hello setup failure

- Kind: `dialog`; feasibility: `native/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:421](../../src/App/Views/SettingsPage.xaml.cs#L421).
- Entry: Enable Hello returns false.
- Content: Failure message; Close.
- Prerequisites: Native Hello setup attempt.
- Variants: Failure.

### settings.twofactor.menu — Two-Factor management

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:501](../../src/App/Views/SettingsPage.xaml.cs#L501); [src/App/Views/SettingsPage.xaml.cs:474](../../src/App/Views/SettingsPage.xaml.cs#L474).
- Entry: Settings > Two-Factor.
- Content: Provider status/empty summary; Enable TOTP; Enable Email; Manage Security Keys/Hello; Disable; Close.
- Prerequisites: Production provider service; successful live load required for management actions; no demo fixture.
- Variants: Loading; Error/retry; No enabled providers; Enabled provider summary.

### settings.twofactor.loading — Two-Factor loading

- Kind: `state`; feasibility: `demo/live`.
- Source: [src/App/Views/SettingsPage.xaml.cs:444](../../src/App/Views/SettingsPage.xaml.cs#L444).
- Entry: Open Two-Factor management while provider request is pending.
- Content: ProgressRing; loading text.
- Prerequisites: Provider request pending.
- Variants: Loading.

### settings.twofactor.error — Two-Factor load error

- Kind: `state`; feasibility: `demo/live-failure`.
- Source: [src/App/Views/SettingsPage.xaml.cs:458](../../src/App/Views/SettingsPage.xaml.cs#L458).
- Entry: Two-Factor provider request fails.
- Content: Error InfoBar; Retry; management content hidden.
- Prerequisites: Failed provider request.
- Variants: Error; Retrying.

### settings.totp.password — Enable TOTP: Step 1

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:604](../../src/App/Views/SettingsPage.xaml.cs#L604).
- Entry: Two-Factor management > Enable TOTP.
- Content: Current password; Next; Cancel; inline error.
- Prerequisites: Successfully loaded two-factor menu; live password verification for progression.
- Variants: Initial; Error.

### settings.totp.secret — Enable TOTP: Step 2

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:681](../../src/App/Views/SettingsPage.xaml.cs#L681).
- Entry: TOTP Step 1 succeeds.
- Content: Selectable Base32 secret and OTPAuth URI; code input; Enable; Cancel; inline error.
- Prerequisites: Live setup secret; authenticator code for completion.
- Variants: Secret and URI; Error.

### settings.totp.complete — TOTP setup success

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:770](../../src/App/Views/SettingsPage.xaml.cs#L770).
- Entry: TOTP activation succeeds.
- Content: Recovery code; Done; optional recovery-code fetch controls.
- Prerequisites: Successful TOTP activation.
- Variants: Recovery code available; Recovery code missing.

### settings.totp.recovery-missing — TOTP active but recovery code unavailable

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:745](../../src/App/Views/SettingsPage.xaml.cs#L745); [src/App/Views/SettingsPage.xaml.cs:756](../../src/App/Views/SettingsPage.xaml.cs#L756); [src/App/Views/SettingsPage.xaml.cs:774](../../src/App/Views/SettingsPage.xaml.cs#L774).
- Entry: Activation succeeds but recovery-code fetch fails.
- Content: Warning InfoBar; current password; Fetch Recovery Code; Done.
- Prerequisites: TOTP enabled with missing recovery code.
- Variants: Retry available; Retry failure; Fetch success replaces controls with code.

### settings.email.password — Enable Email Code: Step 1

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:851](../../src/App/Views/SettingsPage.xaml.cs#L851).
- Entry: Two-Factor management > Enable Email.
- Content: Current password; destination email; Send Code; Cancel; inline error.
- Prerequisites: Loaded menu; account and email delivery for progression.
- Variants: Initial; Error.

### settings.email.code — Enable Email Code: Step 2

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:899](../../src/App/Views/SettingsPage.xaml.cs#L899).
- Entry: Email verification code sent.
- Content: Sent-to-address text; token input; Enable; Cancel; inline error.
- Prerequisites: Successful email send and received code.
- Variants: Initial; Error.

### settings.email.complete — Email verification enabled

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:936](../../src/App/Views/SettingsPage.xaml.cs#L936).
- Entry: Email two-factor activation succeeds.
- Content: Success text; Done.
- Prerequisites: Successful activation.
- Variants: Success.

### settings.webauthn.verify — Verify security-key management

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1094](../../src/App/Views/SettingsPage.xaml.cs#L1094).
- Entry: Two-Factor management > Manage Security Keys/Hello.
- Content: Verification-method selector; secret input; contextual help; optional Send Code; Continue; Cancel; inline status.
- Prerequisites: Successfully loaded two-factor menu.
- Variants: Master password; Email OTP; Device-login access code; Missing input; Busy/load error.

### settings.webauthn.verify-email — Security-key management: email OTP verification

- Kind: `state`; feasibility: `live`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1058](../../src/App/Views/SettingsPage.xaml.cs#L1058); [src/App/Views/SettingsPage.xaml.cs:1072](../../src/App/Views/SettingsPage.xaml.cs#L1072).
- Entry: Select email OTP in security-key verification.
- Content: Send Code; OTP placeholder/help; send success/error InfoBar.
- Prerequisites: Account email delivery.
- Variants: Before send; Sent; Send error.

### settings.webauthn.verify-access-code — Security-key management: device-login access code

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1063](../../src/App/Views/SettingsPage.xaml.cs#L1063).
- Entry: Select device-login access code verification.
- Content: Access-code placeholder/help; Send Code hidden.
- Prerequisites: Other-device login session with access code.
- Variants: Access-code entry.

### settings.webauthn.manage — Security Keys

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1284](../../src/App/Views/SettingsPage.xaml.cs#L1284).
- Entry: Security-key management verification succeeds.
- Content: Scrollable credential rows; Delete; new-key name; Add Security Key; Done.
- Prerequisites: Successful live verification and list load.
- Variants: Empty; Populated; Migrated U2F suffix; Unsupported registration; Operation status.

### settings.webauthn.unsupported — Security Keys: registration unavailable

- Kind: `state`; feasibility: `native/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1167](../../src/App/Views/SettingsPage.xaml.cs#L1167); [src/App/Views/SettingsPage.xaml.cs:1289](../../src/App/Views/SettingsPage.xaml.cs#L1289).
- Entry: Security-key dialog when registration service is unsupported.
- Content: Warning; existing credential viewing/deletion; Add disabled.
- Prerequisites: Unsupported registration service with successfully loaded credential list.
- Variants: Warning.

### settings.webauthn.status — Security-key operation feedback

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1250](../../src/App/Views/SettingsPage.xaml.cs#L1250); [src/App/Views/SettingsPage.xaml.cs:1330](../../src/App/Views/SettingsPage.xaml.cs#L1330); [src/App/Views/SettingsPage.xaml.cs:1354](../../src/App/Views/SettingsPage.xaml.cs#L1354); [src/App/Views/SettingsPage.xaml.cs:1392](../../src/App/Views/SettingsPage.xaml.cs#L1392).
- Entry: Add/delete security-key operations.
- Content: Inline success, busy warning, invalid-name/error, or registration-error feedback.
- Prerequisites: Corresponding operation result.
- Variants: Success; Busy; Invalid name; Add error; Delete error.

### settings.webauthn.delete — Delete Security Key

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1307](../../src/App/Views/SettingsPage.xaml.cs#L1307).
- Entry: Security-key credential row > Delete.
- Content: Deletion confirmation; last-key disables-WebAuthn warning; Delete; Cancel.
- Prerequisites: Populated credential list.
- Variants: Normal key; Last key.

### settings.twofactor.disable-empty — Disable Two-Factor: none enabled

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1421](../../src/App/Views/SettingsPage.xaml.cs#L1421).
- Entry: Two-Factor management > Disable with no enabled provider.
- Content: Information text; Close.
- Prerequisites: Successfully loaded provider list with none enabled.
- Variants: Empty.

### settings.twofactor.disable — Disable Two-Factor

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1458](../../src/App/Views/SettingsPage.xaml.cs#L1458).
- Entry: Two-Factor management > Disable.
- Content: Enabled-provider selector; current password; Disable; Cancel; inline error.
- Prerequisites: At least one enabled provider.
- Variants: Provider selection; Error.

### settings.twofactor.disabled — Two-Factor disabled

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1500](../../src/App/Views/SettingsPage.xaml.cs#L1500).
- Entry: Provider disable succeeds.
- Content: Success text; Done.
- Prerequisites: Successful live disable.
- Variants: Success.

### settings.delete.consequences — Permanently delete account?

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1810](../../src/App/Views/SettingsPage.xaml.cs#L1810).
- Entry: Settings > Delete Account after successful preflight.
- Content: Irreversible-action Error InfoBar; optional organization-owner warning; Continue; Cancel.
- Prerequisites: Unlocked matching persisted account; demo cannot enter valid flow (src/Vault/AccountDeletionService.cs:1061).
- Variants: Standard warning; Organization-owner warning.

### settings.delete.owner-warning — Account deletion: organization ownership

- Kind: `state`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1795](../../src/App/Views/SettingsPage.xaml.cs#L1795).
- Entry: Deletion consequences with preflight ownership warnings.
- Content: Warning InfoBar; organization names.
- Prerequisites: Preflight reports owned organizations.
- Variants: Owner warning.

### settings.delete.email — Confirm Account Email

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1854](../../src/App/Views/SettingsPage.xaml.cs#L1854).
- Entry: Delete consequences > Continue.
- Content: Exact-email confirmation field; Continue; Cancel; mismatch error.
- Prerequisites: Valid preflight and exact normalized account email.
- Variants: Initial; Mismatch.

### settings.delete.credential-password — Delete account: final master-password confirmation

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1710](../../src/App/Views/SettingsPage.xaml.cs#L1710); [src/App/Views/SettingsPage.xaml.cs:1674](../../src/App/Views/SettingsPage.xaml.cs#L1674).
- Entry: Email confirmation succeeds for password account.
- Content: Master-password input; Permanently Delete Account; Cancel.
- Prerequisites: Password account and valid deletion preflight.
- Variants: Password entry.

### settings.delete.credential-otp — Delete account: final email-OTP confirmation

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1710](../../src/App/Views/SettingsPage.xaml.cs#L1710); [src/App/Views/SettingsPage.xaml.cs:1702](../../src/App/Views/SettingsPage.xaml.cs#L1702).
- Entry: Email confirmation succeeds for passwordless account.
- Content: Sent-to-email message; OTP PasswordBox; Permanently Delete Account; Cancel.
- Prerequisites: Passwordless account and successful deletion OTP request.
- Variants: OTP entry.

### settings.delete.message — Account-deletion outcome or blocked operation

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Views/SettingsPage.xaml.cs:1942](../../src/App/Views/SettingsPage.xaml.cs#L1942).
- Entry: Deletion preflight, OTP request, or deletion returns a blocked/error/outcome branch.
- Content: Dynamic title; wrapped explanation; OK.
- Prerequisites: Corresponding controlled branch; missing credential opens this dialog rather than inline final-confirmation error.
- Variants: Unable to delete account (:1530); Operation in progress (:1584); Unable to check deletion conditions (:1591); Organization account cannot be deleted here (:1604); Online sign-in required (:1607, :1667); Account deletion temporarily unavailable (:1610); Unable to send code (:1630, :1644); Verification request not sent (:1650); Verification-code request uncertain (:1656); Missing verification information (:1734); Unable to confirm deletion result (:1763); Account not deleted (:1901); Deletion request not sent (:1913).

## Passkeys and Duo

### dialog.passkey.use — Use a passkey to sign in

- Kind: `dialog`; feasibility: `browser-integration/controlled-fixture`.
- Source: [src/App/Services/PasskeyApprovalDialogService.cs:101](../../src/App/Services/PasskeyApprovalDialogService.cs#L101); [src/App/Controls/PasskeyApprovalView.xaml:7](../../src/App/Controls/PasskeyApprovalView.xaml#L7).
- Entry: Browser passkey assertion request.
- Content: Verified frame origin, RP ID and top origin in Unicode/ASCII; account picker; Use passkey; optional Verify and approve; Cancel.
- Prerequisites: Unlocked vault, validated request and credential candidates; real user verification when required.
- Variants: Single candidate; Multiple candidates unselected; Multiple candidates selected; Cross-origin warning; Preferred verification secondary action; Required verification.

### dialog.passkey.create — Create a passkey

- Kind: `dialog`; feasibility: `browser-integration/controlled-fixture`.
- Source: [src/App/Services/PasskeyApprovalDialogService.cs:257](../../src/App/Services/PasskeyApprovalDialogService.cs#L257).
- Entry: Browser passkey creation request.
- Content: RP ID; frame origin; optional reported site name; account; new-entry versus existing-entry radio choices; existing-entry picker; Create; Cancel.
- Prerequisites: Unlocked vault and validated browser creation request; live save for completion.
- Variants: Create new entry; Attach to existing login; No existing logins disables attach; Cross-origin warning.

### dialog.passkey.master-password — Verify master password for passkey

- Kind: `dialog`; feasibility: `live/controlled-fixture`.
- Source: [src/App/Services/PasskeyApprovalDialogService.cs:338](../../src/App/Services/PasskeyApprovalDialogService.cs#L338).
- Entry: Passkey item reprompt or user-verification fallback.
- Content: PasswordBox; reprompt explanation; Confirm; Cancel.
- Prerequisites: Selected item requiring reprompt, or Windows Hello unavailable/failed/cancelled.
- Variants: Master-password entry.

### dialog.duo.webview — Duo two-step verification

- Kind: `dialog`; feasibility: `live`.
- Source: [src/App/Services/DuoWebViewTwoFactorService.cs:97](../../src/App/Services/DuoWebViewTwoFactorService.cs#L97).
- Entry: Two-factor > Open Duo.
- Content: 480-wide WebView2 with minimum 360 x 540 content; live Duo authorization page; Cancel.
- Prerequisites: Supported parsed Duo challenge, network, Windows, WebView2 runtime; no offline demo page.
- Variants: Duo-hosted loading; Duo-hosted verification; Cancel; Failure closes dialog and returns error.

## Native operating-system surfaces

### native.file-open — Windows open-file picker

- Kind: `native`; feasibility: `native`.
- Source: [src/App/Views/FilePickerHelper.cs:1](../../src/App/Views/FilePickerHelper.cs#L1).
- Entry: Import > Choose file; Send file > Choose file; item > Add attachment.
- Content: Windows file navigation; filename; file-type filter; open/cancel.
- Prerequisites: Local synthetic files; OS-controlled surface.
- Variants: Import JSON/CSV; Send file; Attachment file.

### native.file-save — Windows save-file picker

- Kind: `native`; feasibility: `native`.
- Source: [src/App/Views/FilePickerHelper.cs:1](../../src/App/Views/FilePickerHelper.cs#L1).
- Entry: Verified export; received Send file > Save; attachment > Download.
- Content: Windows destination navigation; filename; type; save/cancel.
- Prerequisites: Live transfer or validated export; OS-controlled surface.
- Variants: Vault export; Send received file; Attachment download.

### native.user-verification — Windows Hello user verification

- Kind: `native`; feasibility: `native`.
- Source: [src/App/Services/UserVerificationService.cs:25](../../src/App/Services/UserVerificationService.cs#L25); [src/App/Services/WindowsHelloAuthenticator.cs:47](../../src/App/Services/WindowsHelloAuthenticator.cs#L47).
- Entry: Passkey required verification, preferred Verify and approve, or Hello vault unlock.
- Content: Windows-owned credential gesture prompt; application falls back to supplied password prompt when verification is unavailable or fails.
- Prerequisites: Windows Hello available and configured credential; OS prompt appearance is not app-owned.
- Variants: Hello gesture; Password fallback.

### native.login-hello — Windows Hello vault unlock

- Kind: `native`; feasibility: `native/persisted-session`.
- Source: [src/App/Views/LoginPage.xaml.cs:269](../../src/App/Views/LoginPage.xaml.cs#L269); [src/App/Services/WindowsHelloAuthenticator.cs:47](../../src/App/Services/WindowsHelloAuthenticator.cs#L47).
- Entry: Enter an unlock stage with Hello enabled, or click Unlock with Windows Hello.
- Content: Windows-owned Hello gesture prompt.
- Prerequisites: Persisted Hello protector and Windows Hello available; automatically prompted once per unlock stage.
- Variants: Automatic prompt; Button-triggered prompt.

### native.login-webauthn — Windows WebAuthn sign-in

- Kind: `native`; feasibility: `live/native`.
- Source: [src/App/Services/WindowsWebAuthnTwoFactorService.cs:115](../../src/App/Services/WindowsWebAuthnTwoFactorService.cs#L115).
- Entry: Two-factor > Use security key.
- Content: Windows-owned authenticator choice and verification prompt.
- Prerequisites: Live challenge; supported Windows WebAuthn; registered security key or platform authenticator.
- Variants: Authenticator choice; Security-key interaction; Platform verification.

### native.settings.webauthn-create — Windows WebAuthn credential creation

- Kind: `native`; feasibility: `live/native`.
- Source: [src/App/Services/WindowsWebAuthnTwoFactorRegistrationService.cs:115](../../src/App/Services/WindowsWebAuthnTwoFactorRegistrationService.cs#L115).
- Entry: Security Keys > Add Security Key.
- Content: Windows-owned authenticator choice and credential-creation prompt.
- Prerequisites: Verified management session; live server challenge; supported Windows WebAuthn and authenticator.
- Variants: Platform authenticator; External security key.

### native.settings.hello-setup — Windows Hello setup

- Kind: `native`; feasibility: `native/persisted-session`.
- Source: [src/App/Services/WindowsHelloAuthenticator.cs:24](../../src/App/Services/WindowsHelloAuthenticator.cs#L24).
- Entry: Settings > Enable Windows Hello.
- Content: Windows-owned Hello credential creation and verification prompt.
- Prerequisites: Native Windows Hello support and matching saved session for protector persistence.
- Variants: Create credential; Verify gesture.

## Reusable controls

### control.field — Field row

- Kind: `control`; feasibility: `demo/live`.
- Source: [src/App/Controls/FieldRow.xaml:7](../../src/App/Controls/FieldRow.xaml#L7).
- Entry: Any type detail.
- Content: Label; value; reveal/hide; optional open link; copy.
- Prerequisites: Relevant synthetic field.
- Variants: Plaintext; Masked; Revealed; Openable URI; Reprompt-protected.

### control.totp — TOTP field

- Kind: `control`; feasibility: `demo/live`.
- Source: [src/App/Controls/TotpField.xaml:8](../../src/App/Controls/TotpField.xaml#L8).
- Entry: Login with authenticator secret.
- Content: Code; 30-second ring; seconds remaining; copy.
- Prerequisites: Synthetic valid TOTP secret.
- Variants: Valid ticking code; Missing/invalid secret; Protected.

### control.favicon — Website icon

- Kind: `control`; feasibility: `demo/live`.
- Source: [src/App/Controls/FaviconView.xaml:1](../../src/App/Controls/FaviconView.xaml#L1).
- Entry: Login item list or detail.
- Content: Site icon with glyph fallback.
- Prerequisites: Live icon proxy for downloaded favicon.
- Variants: Fallback; Downloaded icon.

### control.detail-card — Detail section card

- Kind: `control`; feasibility: `demo/live`.
- Source: [src/App/Controls/DetailCard.xaml:1](../../src/App/Controls/DetailCard.xaml#L1).
- Entry: Item detail or editor.
- Content: Section title; card content.
- Prerequisites: Detail/editor.

### control.passkey-approval — Passkey approval identity and account picker

- Kind: `control`; feasibility: `browser-integration/controlled-fixture`.
- Source: [src/App/Controls/PasskeyApprovalView.xaml:7](../../src/App/Controls/PasskeyApprovalView.xaml#L7).
- Entry: Passkey use-approval dialog.
- Content: RP ID; frame and top origins; Unicode/ASCII identity lines; cross-origin warning; explicit credential picker; account names.
- Prerequisites: Validated browser request and candidate credentials.
- Variants: One credential; Multiple credentials; Cross-origin iframe.

## Browser developer test harnesses

### browser.test.get — Passkey assertion test

- Kind: `test-harness`; feasibility: `external`.
- Source: [browser-extension/test-pages/passkey-get.html:1](../../browser-extension/test-pages/passkey-get.html#L1).
- Entry: Open localhost test page.
- Content: RP ID; user verification; mediation; optional credential ID; request/abort; result log.
- Prerequisites: Extension and native host; matching localhost credential.
- Variants: Success; Failure/cancel as applicable.

### browser.test.create — Passkey registration test

- Kind: `test-harness`; feasibility: `external`.
- Source: [browser-extension/test-pages/passkey-create.html:1](../../browser-extension/test-pages/passkey-create.html#L1).
- Entry: Open localhost test page.
- Content: Create passkey button; result log.
- Prerequisites: Extension and native host; writable vault item destination.
- Variants: Success; Failure/cancel as applicable.

### browser.test.abort — Passkey cancellation test

- Kind: `test-harness`; feasibility: `external`.
- Source: [browser-extension/test-pages/abort-deadline.html:1](../../browser-extension/test-pages/abort-deadline.html#L1).
- Entry: Open localhost test page.
- Content: Start request; Abort; log.
- Prerequisites: Extension and native host; pending ceremony.
- Variants: Success; Failure/cancel as applicable.

### browser.test.iframe-top — Cross-origin iframe policy test

- Kind: `test-harness`; feasibility: `external`.
- Source: [browser-extension/test-pages/iframe-top.html:1](../../browser-extension/test-pages/iframe-top.html#L1).
- Entry: Open configured top-level test origin.
- Content: Allowed iframe; denied iframe; child assertion controls.
- Prerequisites: Two local origins/ports; extension and native host; secure-context policy.
- Variants: Success; Failure/cancel as applicable.

### browser.test.iframe-child — Passkey iframe child

- Kind: `test-harness`; feasibility: `external`.
- Source: [browser-extension/test-pages/iframe-child.html:1](../../browser-extension/test-pages/iframe-child.html#L1).
- Entry: Embedded by iframe-top.
- Content: Get passkey; result log.
- Prerequisites: Test harness child page; permissions policy determined by parent.
- Variants: Success; Failure/cancel as applicable.

## Unreachable placeholder

### page.placeholder — Generic unfinished-feature placeholder

- Kind: `unreachable`; feasibility: `unreachable`.
- Source: [src/App/Views/SimplePage.xaml:1](../../src/App/Views/SimplePage.xaml#L1).
- Entry: No current navigation route.
- Content: Icon; passed title; unfinished-feature subtitle.
- Prerequisites: Not referenced by MainWindow or FrameNavigationService.
- Variants: Unused.

## Deliberate omissions

Arbitrary permutations of every theme, screen size, backend value, timeout, and error message are grouped within each surface's variants. No client page is invented for unsupported or future features. Vaultwarden Web Vault and admin pages are third-party backend UI and are outside this native-client inventory. The developer HTML harnesses are listed separately so they are not mistaken for shipped extension settings or popup pages.
