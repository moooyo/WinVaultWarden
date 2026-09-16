import { vaultScenes } from './redesign-vault.mjs';
import { authSettingsScenes } from './redesign-auth-settings.mjs';
import { toolsScenes } from './redesign-tools.mjs';

// Review proposals only. These records do not claim that the application was changed.
// A direct mapping identifies a representative visible composition, not every source state.
// A family mapping reuses a reviewed interaction pattern without claiming visual execution.
// A boundary mapping records an operating-system, external-service, or developer-only surface.

const conditionalTypes = 'Source guard: bank account, driver\'s license, and passport are implemented cipher types, but creation and editing require CanWriteNewCipherTypes. Their proposal screens are conditional and do not establish server/account support. The composition shows representative fields, not every field in the source model; implementation must retain the remaining fields and their masking semantics.';
const detailChanges = [
  'Use a stable list/detail layout with one primary Edit action and a quieter overflow menu.',
  'Align field labels, values, reveal controls, and copy controls; retain masking for sensitive values.',
  'Separate frequently used credentials from notes, attachments, custom fields, and history.'
];
const editorChanges = [
  'Keep the item type fixed when editing and give the save destination its own group.',
  'Use clear field groups and an anchored Save/Cancel action area; reserve nearby space for inline validation.',
  'Use progressive disclosure for additional options and retain master-password reprompt.'
];

const definitions = [
  {
    id: 'login-account', title: 'Connect to your vault', group: 'Authentication', page: 'login', variant: 'account',
    direct: ['page.login', 'auth.login.account'],
    family: ['auth.register.success-navigation'],
    changes: ['Make the selected server explicit before entering account information.', 'Use a compact, centered sign-in form with a clear Continue action and secondary enterprise sign-in.'],
    notes: 'Saved accounts and self-hosted server entry remain supported. Debug demo access is not a production sign-in option.',
    familyNote: 'Successful registration returns to this existing login form with the account and server prefilled; no separate success page is introduced.',
    baselineFilename: '05-login-self-hosted.png'
  },
  {
    id: 'login-password', title: 'Sign in with a master password', group: 'Authentication', page: 'login', variant: 'password',
    direct: ['auth.login.password'], family: ['auth.login.password-change-required', 'auth.login.jit-encryption'],
    changes: ['Preserve account and server identity across steps.', 'Keep password entry, reveal, and the sign-in action together; place device sign-in as a secondary route.'],
    notes: 'The form is a sign-in proposal; no password or master key is sent to the review artifact.',
    familyNote: 'The related source flow has distinct policy or initialization content. Only its account-identity, form, and primary-action pattern is mapped here; that branch is not visibly shown.',
    baselineFilename: '06-login-password.png'
  },
  {
    id: 'register-account', title: 'Create an account', group: 'Authentication', page: 'register', variant: 'form',
    direct: ['page.register', 'auth.register.form'], family: ['auth.register.email-verification', 'auth.register.uncertain'],
    changes: ['Group server and identity separately from the master-password fields.', 'Explain the password hint briefly and keep field errors next to the affected input.'],
    notes: 'Registration completion and email verification continue to follow the existing server responses and Web Vault continuation.',
    familyNote: 'Email-verification and uncertain-result branches reuse the account identity and action hierarchy. Their distinct status content is not depicted by the initial form.',
    baselineFilename: '02-register.png'
  },
  {
    id: 'login-two-step', title: 'Verify two-step login', group: 'Authentication', page: 'login', variant: 'two-step',
    direct: ['auth.login.2fa-code'], family: ['auth.login.verify-device', 'auth.login.2fa-webauthn', 'auth.login.2fa-duo'],
    changes: ['Name the active verification method and account before the code field.', 'Keep alternate methods, recovery, and resend actions secondary to verification.'],
    notes: 'The representative screen depicts a code-based challenge. Security keys and Duo use their existing method-specific action without a code field.',
    familyNote: 'This is a related verification family, not a capture of this method. Preserve method-specific controls: resend for email, native security-key action for WebAuthn, and external Duo continuation.',
    baselineFilename: null
  },
  {
    id: 'login-recovery', title: 'Use a recovery code', group: 'Authentication', page: 'login', variant: 'recovery',
    direct: ['auth.login.2fa-recovery'],
    changes: ['State the consequence of recovery before the input: configured two-factor methods are removed.', 'Keep the return-to-another-method action easy to find and avoid making destructive recovery the default route.'],
    notes: 'Recovery is a separate existing authentication branch with higher consequence than a normal one-time code.', baselineFilename: null
  },
  {
    id: 'login-locked', title: 'Unlock your vault', group: 'Authentication', page: 'login', variant: 'locked',
    direct: ['auth.login.unlock-password'], family: ['auth.login.unlock-pin', 'auth.login.unlock-keyconnector', 'auth.login.unlock-trusted-sso', 'shell.logout-confirm'],
    changes: ['Show the locked account and server while keeping vault contents out of the composition.', 'Distinguish unlocking from signing out; preserve available Hello, PIN, and enterprise unlock paths.'],
    notes: 'Hello and alternative unlock availability depend on the persisted session and device configuration.',
    familyNote: 'The locked account shell is shared, but PIN, Key Connector, and trusted SSO have distinct controls. Logout uses a separate confirmation; none of these alternate states is claimed as visibly shown.',
    baselineFilename: '23-vault-locked.png'
  },
  {
    id: 'login-device-approval', title: 'Approve this device', group: 'Authentication', page: 'login', variant: 'device-approval',
    direct: ['auth.login.device-wait'], family: ['auth.login.trusted-approval', 'auth.login.sso-pending'],
    changes: ['Make the fingerprint phrase the visual focus of device approval.', 'Show waiting status, expiration, and cancellation in a stable layout with no sensitive vault data.'],
    notes: 'Other-device approval, administrator approval, and browser SSO remain distinct service-dependent flows.',
    familyNote: 'Only the waiting-state hierarchy and cancellation pattern are shared. Enterprise trust controls and browser SSO content are not shown in the device-request composition.',
    baselineFilename: null
  },
  {
    id: 'login-error', title: 'Authentication feedback and recovery', group: 'Authentication', page: 'login', variant: 'error',
    direct: ['auth.login.status-busy'], family: ['auth.login.device-quarantine', 'auth.login.deletion-recovery', 'dialog.login.clear-quarantine', 'dialog.login.clear-local-deletion-data', 'dialog.login.reset-local-data'],
    changes: ['Place actionable status near the primary control without shifting the identity header.', 'Separate retryable authentication failure from local-data cleanup and irreversible reset actions.'],
    notes: 'The visible proposal is a retryable error. Busy controls remain disabled while a request is pending; local deletion and quarantine recovery keep their existing distinct confirmations.',
    familyNote: 'This mapping reviews the status hierarchy only. Quarantine, deletion recovery, and local-reset confirmations require their own consequence text and are not depicted as a normal login error.',
    baselineFilename: '07-login-error.png'
  },
  {
    id: 'vault-overview', title: 'Vault overview and navigation', group: 'Vault', page: 'vault', variant: 'overview',
    direct: ['shell.main', 'page.vault', 'control.favicon'],
    family: ['vault.favorites', 'vault.folders', 'vault.organizations', 'vault.collections', 'vault.archive', 'vault.trash', 'vault.grouping', 'vault.add-menu', 'vault.filter-menu', 'vault.sort-menu', 'vault.saved-views-menu', 'vault.row-menu', 'vault.folder-create', 'vault.folder-rename', 'vault.saved-view-create'],
    changes: ['Use a calm NavigationView-like rail, a compact command area, and clear list/detail boundaries.', 'Make search persistent, show active filters, and keep sync status readable without competing with Add item.', 'Use row selection, focus, and hover as separate states; reserve accent color for the active destination and primary action.'],
    notes: 'Archive mutations and organization actions remain gated by the capabilities and permissions already present in source. Navigation labels are existing destinations.',
    familyNote: 'This source surface shares vault navigation or refinement patterns. Its selected destination, open flyout, naming dialog, or permission-specific state is not individually depicted here.',
    baselineFilename: '08-vault-live.png'
  },
  {
    id: 'vault-no-selection', title: 'Vault with no item selected', group: 'Vault states', page: 'vault', variant: 'no-selection',
    direct: ['vault.no-selection'], related: ['page.vault'],
    changes: ['Keep a populated list available while the detail pane gives a quiet selection prompt.', 'Avoid showing credentials until the user has chosen an item.'],
    notes: 'This composition is the initial state after sign-in, unlock, or returning to My vault: a populated vault with no selected item. It is distinct from the selected-login showcase and the empty-account state.', baselineFilename: '08-vault-live.png'
  },
  ...[
    ['login', 'Login', '11-login-detail.png', ['vault.login.passkeys', 'vault.login.history']],
    ['card', 'Payment card', '43-card-detail.png', []],
    ['identity', 'Identity', '44-identity-detail.png', []],
    ['note', 'Secure note', '45-note-detail.png', []],
    ['ssh', 'SSH key', '46-ssh-detail.png', []],
    ['bank', 'Bank account', null, []],
    ['license', "Driver's license", null, []],
    ['passport', 'Passport', null, []]
  ].map(([itemType, label, baselineFilename, family]) => ({
    id: `vault-detail-${itemType}`, title: `${label} details`, group: 'Item details', page: 'vault', variant: 'detail', itemType,
    direct: [`vault.detail.${itemType}`, ...(itemType === 'login' ? ['control.field', 'control.totp', 'control.detail-card', 'vault.attachments'] : [])], family,
    changes: detailChanges,
    notes: ['bank', 'license', 'passport'].includes(itemType) ? conditionalTypes : 'Synthetic data only. The detail composition reviews representative type-specific fields and common item actions; it is not an exhaustive rendering of every source field or disclosure. Secret reveal and clipboard feedback are separate interaction states.',
    familyNote: 'The related passkey or password-history subsection belongs to this detail family. Its expanded content is not claimed as individually visible.',
    baselineFilename
  })),
  ...[
    ['login', 'login'], ['card', 'payment card'], ['identity', 'identity'], ['note', 'secure note'],
    ['ssh', 'SSH key'], ['bank', 'bank account'], ['license', "driver's license"], ['passport', 'passport']
  ].map(([itemType, label]) => ({
    id: `vault-editor-${itemType}`, title: `Edit ${label}`, group: 'Item editors', page: 'vault', variant: 'editor', itemType,
    direct: [`vault.editor.${itemType}`], family: itemType === 'login' ? ['vault.editor.options'] : [],
    changes: editorChanges,
    notes: ['bank', 'license', 'passport'].includes(itemType) ? conditionalTypes : 'The proposal illustrates a subset of the existing fields and disclosure hierarchy, not a complete editable field contract. Implementation must retain omitted fields, validation, permissions, and masking. Create and edit share a form, but editing cannot silently change the cipher type.',
    familyNote: 'The editor shows the placement of destination and additional options. It does not render the complete custom-field editor, every destination option, or all validation states.',
    baselineFilename: null
  })),
  {
    id: 'vault-batch', title: 'Select and organize items', group: 'Vault states', page: 'vault', variant: 'batch',
    direct: ['vault.selection'], family: ['vault.move-menu', 'vault.transfer', 'vault.collection-select'],
    changes: ['Replace scattered batch commands with one contextual selection bar and an explicit selected count.', 'Explain unavailable actions for mixed ownership or permissions before submission.'],
    notes: 'Folder moves, ownership transfer, and collection assignment remain separate existing operations; the proposal does not merge their permissions.',
    familyNote: 'The source operation is represented by the contextual selection model. Its open destination picker or transfer confirmation is not individually shown.',
    baselineFilename: '49-vault-batch-selection.png'
  },
  {
    id: 'vault-empty', title: 'Empty vault onboarding', group: 'Vault states', page: 'vault', variant: 'empty',
    direct: ['vault.empty'],
    changes: ['Give a true empty vault one clear Add item action and a secondary route to existing import.', 'Keep the empty illustration small and the next step concrete.'],
    notes: 'A true empty account is distinct from an empty folder or filtered result. This is a proposed empty-account composition, not the captured empty-folder state.', baselineFilename: null
  },
  {
    id: 'vault-no-results', title: 'No matching items', group: 'Vault states', page: 'vault', variant: 'no-results',
    direct: ['vault.no-results'],
    changes: ['Keep the search query and active filters visible when no results match.', 'Offer a clear recovery action to remove filters without implying that stored vault items were deleted.'],
    notes: 'The baseline depicts an empty folder in a populated account; the optimized proposal generalizes the source no-results state without claiming a backend empty account.',
    baselineFilename: '50-vault-empty-folder.png'
  },
  {
    id: 'vault-loading', title: 'Loading without losing context', group: 'Vault states', page: 'vault', variant: 'loading',
    direct: [], related: ['page.vault'],
    changes: ['Keep the page title and existing navigation stable while content loads.', 'Use calm progress feedback and prevent duplicate commands during a pending operation.'],
    notes: 'The busy state is included under page.vault in the inventory. This is an additional representative state of that page, not a new product surface.', baselineFilename: null
  },
  {
    id: 'vault-sync-error', title: 'Offline and uncertain operation status', group: 'Vault states', page: 'vault', variant: 'sync-error',
    direct: ['vault.item-warning'], family: ['vault.pending-create', 'vault.pending-abandon-confirm', 'shell.leave-pending'],
    changes: ['Place a persistent status banner above the list with a specific explanation and contextual recovery action.', 'Keep cached vault contents usable where the existing service permits, and distinguish offline data from an uncertain save result.'],
    notes: 'The visible composition is offline cached-data feedback. Ambiguous creation must preserve its draft and resync-first behavior; retrying creation is not presented as safe.',
    familyNote: 'The pending-commit family requires distinct retained-draft and duplicate-risk wording. This offline composition reviews hierarchy only; pending-create and abandon/leave confirmations are not individually visible.',
    baselineFilename: null
  },
  {
    id: 'vault-reprompt', title: 'Verify access to a protected item', group: 'Security dialogs', page: 'vault', variant: 'detail', itemType: 'login', dialog: 'reprompt',
    direct: ['vault.reprompt'], family: ['dialog.passkey.master-password'],
    changes: ['Identify the protected action in the dialog title and preserve the masked background detail.', 'Use one password field, inline feedback, and a clear Verify action.'],
    notes: 'Master-password reprompt is retained as an access boundary. The schematic uses no real secret.',
    familyNote: 'Passkey reprompt shares the verification pattern, but its credential operation and source dialog remain distinct.',
    baselineFilename: '10-master-password-reprompt.png'
  },
  {
    id: 'vault-permanent-delete', title: 'Confirm permanent item deletion', group: 'Security dialogs', page: 'vault', variant: 'detail', itemType: 'login', dialog: 'permanent-delete',
    direct: ['vault.delete-confirm'], family: ['vault.trash-confirm', 'vault.delete-selected-confirm', 'vault.empty-bin-confirm', 'vault.folder-delete-confirm', 'vault.attachment-delete-confirm'],
    changes: ['Name the exact target and irreversible consequence above the final action.', 'Keep Cancel visually safe and use error color only for the destructive confirmation.'],
    notes: 'Moving an item to trash is reversible and requires different wording from permanent deletion. Folder deletion must preserve its actual source semantics.',
    familyNote: 'This is the confirmation hierarchy for a related destructive action, not the literal dialog. Preserve the correct target, count, reversibility, and consequences for each operation.',
    baselineFilename: null
  },
  {
    id: 'send-overview', title: 'Manage secure Sends', group: 'Send', page: 'send', variant: 'overview',
    direct: ['page.send'], family: ['send.row-menu', 'send.delete-confirm'],
    changes: ['Give text and file Sends recognizable type icons and readable expiry/access metadata.', 'Keep Copy link as the frequent row action and move edit/delete to a consistent overflow menu.'],
    notes: 'Sharing, access counts, expiry, and deletion remain subject to the source Send service. This review introduces no link-preview or recipient-tracking feature.',
    familyNote: 'The list shows where row actions belong; the open menu and irreversible Send-deletion dialog are related patterns, not individually rendered states.',
    baselineFilename: '14-send-list.png'
  },
  {
    id: 'send-edit-text', title: 'Create a text Send', group: 'Send', page: 'send', variant: 'overview', dialog: 'edit-text',
    direct: ['send.editor.text'],
    changes: ['Put the name and text content first, followed by grouped lifetime and access restrictions.', 'Use a single Save action and explicit labels for deletion date, expiry, and optional access password.'],
    notes: 'The composition shows content, lifetime, and selected access restrictions. Disabled state, private notes, audience details, and custom date inputs are not all depicted; implementation must retain those existing options. No share operation is performed.',
    baselineFilename: '15-send-text-editor.png'
  },
  {
    id: 'send-edit-file', title: 'Create a file Send', group: 'Send', page: 'send', variant: 'overview', dialog: 'edit-file',
    direct: ['send.editor.file'],
    changes: ['Show the selected filename and a clear file-picker action before optional sharing restrictions.', 'Use the same lifetime/access structure as text Send to reduce relearning.'],
    notes: 'The file picker remains Windows-owned and upload limits remain server/service dependent. This is a representative file-send form, not every restriction or date-picker state. Drag-and-drop is not claimed as implemented.',
    baselineFilename: '16-send-file-editor.png'
  },
  {
    id: 'send-received', title: 'Open a received Send', group: 'Send', page: 'send', variant: 'received',
    direct: ['send.receive'],
    changes: ['Keep the received-link flow distinct from the account\'s outbound Send list.', 'Show link and optional access password before Open, then reveal received content or the existing Save file action.'],
    notes: 'The representative result is a text Send; file-download results and error states are not depicted. External received links require the live service and valid link key. The demo service cannot complete this flow.', baselineFilename: '17-send-access.png'
  },
  {
    id: 'generator-password', title: 'Generate a strong password', group: 'Generator', page: 'generator', variant: 'password',
    direct: ['generator.password'], family: ['generator.history', 'generator.clear-confirm'],
    changes: ['Make the generated value the focus and keep Regenerate and Copy adjacent.', 'Group length and character controls below the result; place minimum counts and ambiguous-character settings in a quieter advanced group.'],
    notes: 'The displayed password is synthetic and is not intended for reuse. History remains an existing sensitive-content dialog.',
    familyNote: 'History uses the same readable generated-value rows and copy pattern; its open dialog and clear-history confirmation are not shown.',
    baselineFilename: '18-generator-password.png'
  },
  {
    id: 'generator-passphrase', title: 'Generate a memorable passphrase', group: 'Generator', page: 'generator', variant: 'passphrase',
    direct: ['generator.passphrase'],
    changes: ['Keep result placement and actions consistent with password generation.', 'Prioritize word count and separator; group capitalization and number options below.'],
    notes: 'The proposal preserves existing passphrase options and does not introduce a custom word-list service.', baselineFilename: '19-generator-passphrase.png'
  },
  {
    id: 'generator-username', title: 'Generate a username', group: 'Generator', page: 'generator', variant: 'username',
    direct: ['generator.username'],
    changes: ['Make the username method explicit and show only its relevant options.', 'Reuse the generated-value, Regenerate, and Copy arrangement from the other generator modes.'],
    notes: 'Random words, plus-addressing, and catch-all remain the implemented methods; no third-party relay provider integration is added.', baselineFilename: '20-generator-username.png'
  },
  {
    id: 'reports-overview', title: 'Understand vault health', group: 'Security reports', page: 'reports', variant: 'overview',
    direct: ['page.reports'],
    changes: ['Prioritize actionable categories with counts rather than inventing a composite security score.', 'Separate local weak/reused/HTTP findings from the explicit external exposed-password check.'],
    notes: 'The existing report categories and HIBP check remain unchanged. A not-yet-run exposure check must not be presented as a clean result.', baselineFilename: '22-security-report.png'
  },
  {
    id: 'reports-results', title: 'Review security findings', group: 'Security reports', page: 'reports', variant: 'results',
    direct: [], related: ['page.reports'],
    changes: ['Expand one finding category into a readable affected-item list with an explicit next action.', 'Retain checked, unchecked, and partial/error distinctions for external exposure results.'],
    notes: 'This is an additional review state included under page.reports. Counts and item names are synthetic; no exposure service was queried for this proposal.', baselineFilename: '22-security-report.png'
  },
  {
    id: 'import-export-overview', title: 'Import and export vault data', group: 'Import and export', page: 'import-export', variant: 'overview',
    direct: ['page.import-export'],
    changes: ['Give import and export separate sections with clear purpose and file-format controls.', 'Place the plaintext warning directly beside the export action and give the import preview a clear position before submission.'],
    notes: 'Only the existing JSON/CSV flows are proposed. The initial view shows the preview position before a file is selected; loaded preview counts are not shown. No encrypted-export format, duplicate resolver, or import rollback is claimed.', baselineFilename: '41-import-export.png'
  },
  {
    id: 'export-confirm', title: 'Confirm a plaintext export', group: 'Security dialogs', page: 'import-export', variant: 'overview', dialog: 'export',
    direct: ['import.export-confirm'],
    changes: ['Explain that the exported file can expose vault contents before requesting the master password.', 'Keep filename selection delegated to the Windows save picker after verification.'],
    notes: 'This is the existing export-verification boundary. The artifact contains no exported vault and performs no operation.', baselineFilename: null
  },
  {
    id: 'devices-overview', title: 'Review devices and sign-in requests', group: 'Devices', page: 'devices', variant: 'overview',
    direct: ['page.devices'],
    changes: ['Separate pending sign-in requests from the known-device list.', 'Make current/trusted status, last activity, and fingerprint verification readable without relying only on color.'],
    notes: 'Approve and deny retain their existing source flow. Remote device revocation is not introduced as a new capability.', baselineFilename: '35-devices-pending-request.png'
  },
  {
    id: 'device-approve', title: 'Confirm a device sign-in request', group: 'Security dialogs', page: 'devices', variant: 'overview', dialog: 'approve-device',
    direct: ['devices.approve-confirm'], family: ['devices.deny-confirm'],
    changes: ['Show the device identity and matching fingerprint immediately before approval.', 'Use an explicit Approve action and a clear explanation of what access is being granted.'],
    notes: 'The review data describes a synthetic request. Fingerprint matching remains a human verification step.',
    familyNote: 'Deny uses the same identity context but a distinct consequence and final action; the denial confirmation is not shown.', baselineFilename: '35-devices-pending-request.png'
  },
  {
    id: 'emergency-overview', title: 'Manage emergency access', group: 'Emergency access', page: 'emergency', variant: 'overview',
    direct: ['page.emergency'], family: ['emergency.update', 'emergency.reject-confirm', 'emergency.remove-confirm', 'emergency.recovered', 'emergency.takeover'],
    changes: ['Separate contacts who can request your vault from accounts that trust you.', 'Present relationship status, access level, and waiting period together with only status-valid actions.'],
    notes: 'View and Takeover remain distinct. Source has no invitation-acceptance control on this page, so the proposal does not invent one.',
    familyNote: 'This related emergency-access flow is linked to the relationship model. Edit, reject/remove confirmation, recovered read-only vault, and takeover password reset have distinct compositions not individually shown here.',
    baselineFilename: '32-emergency-granted.png'
  },
  {
    id: 'emergency-invite', title: 'Invite an emergency contact', group: 'Emergency access', page: 'emergency', variant: 'overview', dialog: 'invite',
    direct: ['emergency.invite'],
    changes: ['Explain View and Takeover beside the access-level selection.', 'Keep the waiting period next to its consequence and show the invited email before Send invitation.'],
    notes: 'An invitation is not sent by generating this review artifact. The existing live server and relationship confirmation requirements remain.', baselineFilename: '31-emergency-invite.png'
  },
  {
    id: 'settings-security', title: 'Security and vault locking', group: 'Settings', page: 'settings', variant: 'security',
    direct: ['page.settings', 'settings.security', 'settings.hello', 'settings.preferences'],
    family: ['settings.timeout-policy', 'settings.pin-policy', 'settings.trusted-device', 'settings.pin.setup', 'settings.trust.error', 'settings.hello.failure'],
    changes: ['Organize locking, device unlock, and account protection as separate setting groups.', 'Show policy or availability explanations directly under disabled settings instead of hiding the reason.'],
    notes: 'The screen reviews the security settings hierarchy. PIN, Hello, timeout, and device trust remain gated by existing policies, platform support, and persisted-session eligibility.',
    familyNote: 'Policy-constrained options, device-trust eligibility, setup, and failure dialogs reuse the setting context. Their distinct disabled controls, inputs, or outcomes are not individually shown.', baselineFilename: '28-settings-security.png'
  },
  {
    id: 'settings-preferences', title: 'Application and preferences', group: 'Settings', page: 'settings', variant: 'preferences',
    direct: ['settings.application', 'settings.tray', 'settings.browser-integration', 'settings.theme'],
    family: ['settings.future-controls', 'settings.about'],
    changes: ['Use aligned settings rows for tray behavior, theme, website icons, and browser integration.', 'Explain unavailable future settings and keep their status separate from usable options.'],
    notes: 'Clipboard timeout and screenshot protection are grouped under Security in this proposal. Start on login, minimize on copy, acceleration, SSH-agent settings, and language selection remain unavailable in source. Browser integration remains limited to portable builds; the proposal does not make unavailable options functional.',
    familyNote: 'Future controls and the expanded diagnostics/about section belong to this settings structure but are not all expanded in the composition.', baselineFilename: '36-settings-integration.png'
  },
  {
    id: 'settings-account', title: 'Account and encryption settings', group: 'Settings', page: 'settings', variant: 'account',
    direct: ['settings.account', 'settings.danger'], family: ['settings.profile.edit'],
    changes: ['Make the account email and server the leading identity summary.', 'Group profile, master password, and encryption settings together; separate account deletion with explicit consequence language.'],
    notes: 'The existing encryption dialog edits PBKDF2 iterations and does not expose a KDF-type selector. This proposal does not add Argon2 switching or migration controls.',
    familyNote: 'Profile editing shares the account identity context and form hierarchy. Its dedicated display-name dialog is not shown by the settings overview.', baselineFilename: '24-settings-account.png'
  },
  {
    id: 'settings-change-password', title: 'Change the master password', group: 'Security dialogs', page: 'settings', variant: 'account', dialog: 'change-password',
    direct: ['settings.password.change'],
    changes: ['Separate current-password verification from the new-password and confirmation pair.', 'Show the existing sign-out consequence before the final change action and keep validation inline.'],
    notes: 'Changing the master password requires a matching persisted session and logs out on success. This review performs no credential change.', baselineFilename: '26-settings-password.png'
  },
  {
    id: 'settings-kdf', title: 'Review encryption work factor', group: 'Security dialogs', page: 'settings', variant: 'account', dialog: 'kdf',
    direct: ['settings.kdf.change'],
    changes: ['Explain the existing PBKDF2 iteration setting before the numeric input.', 'Keep current-password verification and the Apply action together with inline validation.'],
    notes: 'Source guard: this dialog only edits PBKDF2 iterations. Do not add a KDF-type selector, Argon2 parameters, benchmark results, or an unsupported migration path.', baselineFilename: '27-settings-kdf.png'
  },
  {
    id: 'settings-two-factor', title: 'Manage two-step protection', group: 'Security dialogs', page: 'settings', variant: 'security', dialog: 'two-factor',
    direct: ['settings.twofactor.menu'],
    family: ['settings.twofactor.loading', 'settings.twofactor.error', 'settings.totp.password', 'settings.email.password', 'settings.email.code', 'settings.email.complete', 'settings.twofactor.disable-empty', 'settings.twofactor.disable', 'settings.twofactor.disabled'],
    changes: ['Give each existing provider a readable status and an explicit setup/manage action.', 'Keep loading and errors local to provider management and require a successful load before exposing provider mutations.'],
    notes: 'The proposal is the provider overview, not completed enrollment. TOTP setup retains its secret/OTPAuth URI and code; recovery-code retrieval failure must remain a warning even after activation succeeds.',
    familyNote: 'This provider flow shares the reviewed provider-management hierarchy but has distinct step-specific inputs, recovery-code handling, or disable consequences. It is not individually depicted and not claimed as executed.',
    baselineFilename: '29-settings-two-factor.png'
  },
  {
    id: 'settings-totp-setup', title: 'Set up an authenticator', group: 'Security dialogs', page: 'settings', variant: 'security', dialog: 'totp-setup',
    direct: ['settings.totp.secret'], family: ['settings.totp.complete', 'settings.totp.recovery-missing'],
    changes: ['Use a clear setup sequence: add the provided secret to an authenticator, then enter its code.', 'Keep the secret and OTPAuth URI selectable and the confirmation action below the code field.'],
    notes: 'The setup data is synthetic. Source exposes a Base32 secret and OTPAuth URI, so a scannable QR code is not claimed as implemented. Recovery code is shown only after successful activation.',
    familyNote: 'Completion and missing-recovery-code states occur after setup. Preserve recovery-code display or Fetch Recovery Code with a warning; neither completion state is shown by this setup step.', baselineFilename: null
  },
  {
    id: 'settings-security-keys', title: 'Manage security keys', group: 'Security dialogs', page: 'settings', variant: 'security', dialog: 'security-keys',
    direct: ['settings.webauthn.manage'],
    family: ['settings.webauthn.verify', 'settings.webauthn.verify-email', 'settings.webauthn.verify-access-code', 'settings.webauthn.unsupported', 'settings.webauthn.status', 'settings.webauthn.delete'],
    changes: ['Use a compact credential list with clear names and a separate Add security key area.', 'Keep registration availability, operation status, and last-key deletion consequences visible at the affected action.'],
    notes: 'Management verification can use the existing master-password, email-OTP, or device-login access-code path. Registration itself remains Windows-owned.',
    familyNote: 'This related verification, availability, feedback, or deletion branch is reviewed through the security-key management family. The specific branch is not individually visible.', baselineFilename: null
  },
  {
    id: 'settings-delete-account', title: 'Review permanent account deletion', group: 'Security dialogs', page: 'settings', variant: 'account', dialog: 'delete-account',
    direct: ['settings.delete.consequences', 'settings.delete.owner-warning'],
    family: ['settings.delete.email', 'settings.delete.credential-password', 'settings.delete.credential-otp', 'settings.delete.message'],
    changes: ['Lead with irreversible consequences and any organization ownership impact.', 'Keep the existing staged confirmation: consequences, exact account email, then the applicable final credential.'],
    notes: 'The visible proposal is the first consequences step. It does not collapse the existing preflight, email, password/OTP, or uncertain-result safeguards into a one-click deletion.',
    familyNote: 'The later confirmation and outcome branches remain separate stages. This first-step composition does not visually represent email entry, final credentials, or an operation result.', baselineFilename: '39-delete-account-warning.png'
  },
  {
    id: 'system-boundaries', title: 'System and integration boundaries', group: 'Review boundaries', page: 'system', variant: 'boundary',
    direct: [],
    boundary: ['page.placeholder', 'native.file-open', 'native.file-save', 'browser.test.get', 'browser.test.create', 'browser.test.abort', 'browser.test.iframe-top', 'browser.test.iframe-child', 'shell.tray-menu', 'dialog.passkey.use', 'dialog.passkey.create', 'native.user-verification', 'native.login-hello', 'native.login-webauthn', 'dialog.duo.webview', 'native.settings.webauthn-create', 'native.settings.hello-setup', 'control.passkey-approval'],
    changes: ['Keep the application request context clear before crossing into Windows or a third-party verification surface.', 'Preserve relying-party identity, origin, and explicit credential choice for browser passkey approval.'],
    notes: 'This is a review boundary diagram, not a redesign of Windows prompts, external Duo/SSO, or browser test harness pages. SimplePage is unreachable. The extension has no product popup, options page, or side panel.',
    boundaryNote: 'Logical boundary coverage only. This source surface is system-owned, integration-specific, developer-only, or unreachable; its literal visual state is not represented and no runtime capture is claimed.', baselineFilename: null
  }
];

const additions = [...vaultScenes, ...authSettingsScenes, ...toolsScenes];
const allDefinitions = [...definitions, ...additions];

export const scenarios = allDefinitions.map(({ direct = [], family = [], boundary = [], related = [], familyNote, boundaryNote, ...scenario }) => ({
  itemType: null,
  dialog: null,
  ...scenario,
  sourceIds: [...direct, ...family, ...boundary, ...related]
}));

const initialCoverage = definitions.flatMap(({ id: scenarioId, direct = [], family = [], boundary = [], familyNote, boundaryNote, notes }) => [
  ...direct.map(inventoryId => ({ inventoryId, scenarioId, scope: 'direct', notes: `Representative proposal composition only; arbitrary source-state variants are not all shown. ${notes}` })),
  ...family.map(inventoryId => ({ inventoryId, scenarioId, scope: 'family', notes: familyNote })),
  ...boundary.map(inventoryId => ({ inventoryId, scenarioId, scope: 'boundary', notes: boundaryNote }))
]);

const coverageById = new Map(initialCoverage.map(entry => [entry.inventoryId, entry]));
for (const scene of additions) {
  for (const inventoryId of scene.direct || []) coverageById.set(inventoryId, {inventoryId,scenarioId:scene.id,scope:'direct',notes:scene.notes});
}
const promoted = {
  'vault.favorites':'vault-favorites',
  'vault.folders':'vault-folder-work',
  'vault.organizations':'vault-organization',
  'vault.collections':'vault-organization',
  'vault.archive':'vault-archive',
  'vault.trash':'vault-trash',
  'vault.pending-create':'vault-pending-save',
  'send.delete-confirm':'send-delete-confirm',
};
for (const [inventoryId, scenarioId] of Object.entries(promoted)) {
  const scene = scenarios.find(entry => entry.id === scenarioId);
  if (scene && coverageById.has(inventoryId)) {
    coverageById.set(inventoryId,{inventoryId,scenarioId,scope:'direct',notes:scene.notes});
    if (!scene.sourceIds.includes(inventoryId)) scene.sourceIds.push(inventoryId);
  }
}
export const coverage = [...coverageById.values()];
