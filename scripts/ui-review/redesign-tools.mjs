// Audited Fluent proposal surfaces for tools and security-sensitive workflows.
// All data is synthetic. Interactions never contact an account or a remote service.

const fingerprint = 'amber-cedar-river-meadow-harbor';
const extra = (id, title, group, page, variant, dialog, related, notes) => ({
  id, title, group, page, variant, dialog: dialog || null, direct: [], related,
  changes: ['Expose the distinct state and its available actions.', 'Keep account identity, limits, and consequences explicit.'],
  notes, baselineFilename: null,
});

export const toolsScenes = [
  extra('send-existing-text', 'Edit an existing text Send', 'Send', 'send', 'overview', 'existing-text', ['send.editor.text'], 'Existing content uses Save changes. The type is fixed and the current access settings are preserved.'),
  extra('send-existing-file', 'Edit an existing file Send', 'Send', 'send', 'overview', 'existing-file', ['send.editor.file'], 'Existing files retain their identity. Replacing a file is a distinct operation with its existing service safeguards.'),
  extra('send-delete-confirm', 'Confirm permanent Send deletion', 'Send', 'send', 'overview', 'delete-send', ['send.delete-confirm'], 'Deleting a Send permanently invalidates its link. No deletion is executed in the review.'),
  extra('send-received-empty', 'Enter a received Send link', 'Send', 'send', 'received-empty', null, ['send.receive'], 'The initial received-Send state contains no decrypted content. A valid share URL includes both access ID and key.'),
  extra('send-received-file', 'Review a received file Send', 'Send', 'send', 'received-file', null, ['send.receive'], 'The file result offers the existing Windows Save file flow. It does not render arbitrary file content or claim a download occurred.'),
  extra('generator-history', 'Review copied values in this session', 'Generator', 'generator', 'password', 'generator-history', ['generator.history', 'generator.clear-confirm'], 'Only explicitly copied values enter the in-memory history, capped at 20. Locking or signing out clears it.'),
  extra('reports-weak', 'Review weak passwords', 'Security reports', 'reports', 'weak', null, ['page.reports'], 'Weak-password results are separate from reused groups. Item actions preserve the selected item identity.'),
  extra('reports-unsafe', 'Review unencrypted websites', 'Security reports', 'reports', 'unsafe', null, ['page.reports'], 'HTTP findings identify the affected website. HTTPS support must be verified before changing the saved URL.'),
  extra('reports-exposure-failed', 'Retry an incomplete exposure check', 'Security reports', 'reports', 'exposure-failed', null, ['page.reports'], 'Failure means the exposure result is unknown. Local findings remain visible and no zero-result claim is made.'),
  extra('import-preview', 'Review parsed import data', 'Import and export', 'import-export', 'preview', null, ['page.import-export'], 'The parsed preview enables import only for the current file and format. Import adds items and does not resolve duplicates.'),
  extra('emergency-recovered', 'Review a recovered vault', 'Emergency access', 'emergency', 'overview', 'recovered', ['emergency.recovered'], 'Approved View access opens a read-only vault for the named grantor. It does not grant account takeover.'),
  extra('emergency-takeover', 'Confirm an emergency account takeover', 'Emergency access', 'emergency', 'overview', 'takeover', ['emergency.takeover'], 'Approved Takeover access can reset the named grantor master password. The illustration does not submit the reset.'),
];

function toolButton(ui, text, action, style = '', icon = '', attrs = '') {
  return ui.button(text, style, icon, '', `data-tool="${action}" ${attrs}`);
}

function sendPage(s, ui) {
  const { e, icon, header, button, ib, pill, section, label } = ui;
  if (s.variant.startsWith('received')) {
    const initial = s.variant === 'received-empty';
    const file = s.variant === 'received-file';
    const form = section('Open a Send', `<div class="form-section">${label('Send link', '', 'url', 'Paste the complete link, including the part after #.', 'data-send-link placeholder="https://vault.example/#/send/access-id/key"')}${label('Access password (if required)', '', 'password', '', 'autocomplete="off"')}${toolButton(ui, 'Open Send', 'received-open', 'primary', 'arrow', 'disabled data-send-open')}</div>`);
    const result = `<div class="received-source"><div>${icon('lock')}<span>Opened from <strong>vault.example</strong></span></div>${button('Open another link', '', '', 'send-received-empty')}</div>${section(file ? 'Review checklist.txt' : 'Review handoff', `<div class="tool-result-body">${pill(file ? 'File Send' : 'Text Send')}<p>${file ? 'Save this shared file to a location you choose.' : 'The updated recovery checklist is ready for review.'}</p>${!file ? '<p>Please confirm the account and vault before making changes.</p>' : ''}</div><footer class="received-result-actions">${toolButton(ui, file ? 'Save file' : 'Copy text', file ? 'save-file' : 'copy-content', 'primary', file ? 'download' : 'copy')}<span class="muted">Shared content · Read only</span></footer>`)}`;
    return `<div class="page-content tools-page"><div class="page-context">${button('Send', '', 'back', 'send-overview')}<span>/</span>Received Send</div>${header('Received Send', 'Open a shared link without adding it to your vault.')}<div class="received-layout">${initial ? form : result}<div class="tool-state-link"><small>${initial ? 'Ask the sender for the complete link and any required access password.' : 'Access can end when the sender expires, disables, or deletes the Send.'}</small></div></div></div>`;
  }
  const rows = [
    { type: 'text', icon: 'note', name: 'Review handoff', metadata: 'Text · 3 accesses', audience: 'Anyone with the link', disabled: false, editor: 'send-existing-text' },
    { type: 'file', icon: 'attachment', name: 'Review checklist.txt', metadata: 'File · 32 KB · 1 access', audience: 'Anyone with the link', disabled: false, editor: 'send-existing-file' },
    { type: 'text', icon: 'note', name: 'Contract draft', metadata: 'Text · 0 accesses', audience: 'Password protected', disabled: true, editor: null },
  ];
  const list = rows.map(row => {
    const name = e(row.name);
    const copyAttrs = `data-send-target="${name}" data-send-disabled="${row.disabled}" aria-label="Copy link for ${name}"${row.disabled ? ' title="This Send is disabled. Recipients cannot open its link."' : ''}`;
    const edit = row.editor
      ? button('Edit Send', '', 'note', row.editor)
      : toolButton(ui, 'Edit Send', 'specific-send-edit', '', 'note', `data-send-target="${name}"`);
    const remove = row.name === 'Review handoff'
      ? button('Delete Send', 'danger-quiet', 'trash', 'send-delete-confirm')
      : toolButton(ui, 'Delete Send', 'specific-send-delete', 'danger-quiet', 'trash', `data-send-target="${name}"`);
    // Both active fixtures expire at deletion; a disabled Send has no current access window.
    const availability = row.disabled
      ? '<strong class="tool-send-unavailable">Not available</strong><small>Deletes 22 Sep 2026</small>'
      : '<strong>22 Sep 2026</strong><small>14:00 · then deleted</small>';
    return `<div class="data-row tool-send-grid" data-send-type="${row.type}"><div class="entity"><span class="item-avatar">${icon(row.icon)}</span><div><strong>${name}</strong><p>${e(row.metadata)}</p></div></div><div class="tool-send-access">${pill(row.disabled ? 'Disabled' : 'Active', row.disabled ? '' : 'success')}<small>${e(row.audience)}</small></div><div class="tool-send-availability">${availability}</div><div class="row-actions tool-send-actions">${toolButton(ui, 'Copy link', 'copy-send', 'tool-send-copy', 'copy', copyAttrs)}<details class="tool-row-menu"><summary aria-label="More actions for ${name}">${icon('more')}</summary><div class="tool-menu-content">${edit}${remove}</div></details></div></div>`;
  }).join('');
  return `<div class="page-content tools-page">${header('Send', 'Share encrypted text or files with a link you control.', button('New Send', 'primary', 'add', 'send-edit-text'))}<div class="page-toolbar"><div class="segmented" role="tablist" aria-label="Send type">${[['all', 'All Sends · 3'], ['text', 'Text · 2'], ['file', 'Files · 1']].map(([v, t]) => `<button role="tab" aria-selected="${v === 'all'}" class="${v === 'all' ? 'active' : ''}" data-tool="send-filter" data-filter="${v}">${t}</button>`).join('')}</div>${button('Open a received Send', '', 'download', 'send-received-empty')}</div><section class="data-table tool-send-table"><div class="data-header tool-send-grid"><span>Name</span><span>Access</span><span>Available until</span><span>Actions</span></div>${list}</section><div class="page-footnote tool-send-footnote">${icon('clock')}Access ends at the configured limit, expiry, or deletion. Disabling a Send stops access without deleting it.</div></div>`;
}

function generator(s, ui) {
  const { header, button, icon, section, setting } = ui;
  const mode = ['password', 'passphrase', 'username'].includes(s.variant) ? s.variant : 'password';
  const isUser = mode === 'username';
  const output = mode === 'passphrase' ? 'amber-cedar7-river-meadow' : isUser ? 'cedar4826' : 'N7#qR!p2@xK8vT4$mL9z';
  const check = (key, title, checked = true) => `<label><input type="checkbox" data-gen="${key}" aria-label="${title === 'Enabled' ? key === 'capitalize' ? 'Capitalize' : mode === 'username' ? 'Include four digits' : 'Include a number' : title}" ${checked ? 'checked' : ''}>${title}</label>`;
  let options = '';
  if (mode === 'password') {
    options = setting('Length', 'Choose 5 to 128 characters.', `<div class="range-field"><input aria-label="Password length" data-gen="length" type="range" min="5" max="128" value="20"><input class="small-number" aria-label="Password length number" data-gen="length-number" type="number" min="5" max="128" value="20"></div>`) + `<div class="setting-row"><div><strong>Characters</strong><p>Choose the characters the website allows.</p></div><div class="character-choices">${check('upper', 'A–Z')}${check('lower', 'a–z')}${check('digits', '0–9')}${check('symbols', '!@#$')}</div></div><details class="disclosure tool-generator-advanced"><summary><strong>Advanced options</strong><span>Minimum counts · Ambiguous characters</span>${icon('chevron')}</summary>${setting('Minimum digits', '', '<input class="small-number" type="number" data-gen="min-digits" min="0" max="128" value="2" aria-label="Minimum digits">')}${setting('Minimum symbols', '', '<input class="small-number" type="number" data-gen="min-symbols" min="0" max="128" value="2" aria-label="Minimum symbols">')}<div class="setting-row"><div><strong>Avoid ambiguous characters</strong><p>Exclude I, l, O and 0.</p></div><label class="switch"><input type="checkbox" data-gen="ambiguous" aria-label="Avoid ambiguous characters" checked><span></span></label></div></details>`;
  } else if (mode === 'passphrase') {
    options = setting('Words', 'Use 3 to 20 unrelated words.', '<input class="small-number" type="number" data-gen="words" min="3" max="20" value="4" aria-label="Word count">') + setting('Separator', 'Between each word.', '<input class="small-number" value="-" data-gen="separator" aria-label="Word separator">') + `<div class="setting-row"><div><strong>Capitalize words</strong></div>${check('capitalize', 'Enabled', false)}</div><div class="setting-row"><div><strong>Include a number</strong><p>Add a digit to one word.</p></div>${check('number', 'Enabled')}</div>`;
  } else {
    options = setting('Username type', 'Match the format your account needs.', '<select aria-label="Username type" data-gen="username-type"><option value="word">Random word</option><option value="plus">Plus-addressed email</option><option value="catchall">Catch-all email</option></select>') + `<div data-gen-word><div class="setting-row"><div><strong>Capitalize</strong></div>${check('capitalize', 'Enabled', false)}</div><div class="setting-row"><div><strong>Include four digits</strong></div>${check('number', 'Enabled')}</div></div><div class="padded" data-gen-email hidden><label class="form-field"><span>Email address</span><input type="email" data-gen="email" placeholder="name@example.com"><small>Your email provider must support plus addressing.</small></label></div><div class="padded" data-gen-domain hidden><label class="form-field"><span>Catch-all domain</span><input data-gen="domain" placeholder="example.com"><small>Use a domain where you have configured a catch-all mailbox.</small></label></div>`;
  }
  return `<div class="page-content generator-content tools-page" data-generator-mode="${mode}">${header('Generator', 'Create a new credential for the account you are using.')}<div class="generator-layout"><section class="generator-form"><div class="segmented full" role="tablist" aria-label="Generator mode">${[['password', 'Password'], ['passphrase', 'Passphrase'], ['username', 'Username']].map(([id, t]) => `<button role="tab" aria-selected="${mode === id}" class="${mode === id ? 'active' : ''}" data-go="generator-${id}">${t}</button>`).join('')}</div><div class="generated-value"><span class="eyebrow">${isUser ? 'USERNAME' : 'GENERATED ' + mode.toUpperCase()}</span><p class="mono" data-tool-generated>${output}</p><div><span class="muted" data-gen-status>${isUser ? 'Random word + four digits' : mode === 'password' ? '20 characters' : '4 words'}</span><span class="caption">Illustrative value</span></div></div><div class="generator-actions">${toolButton(ui, 'Regenerate', 'regenerate', '', 'generator')}${toolButton(ui, 'Copy ' + mode, 'copy-generated', 'primary', 'copy', 'data-gen-copy')}</div>${section('Options', options)}<button class="history-link" data-go="generator-history">${icon('clock')}<strong>Generator history</strong><span>Copied this session</span>${icon('arrow')}</button></section><aside class="context-panel"><span class="context-icon">${icon(isUser ? 'person' : 'key')}</span><h3>${isUser ? 'A name for this account' : 'One account, one password'}</h3><p>${isUser ? 'A generated username is a suggestion. The website still needs to confirm it is available.' : 'Save the value with its account after changing the password on the website.'}</p><p>${isUser ? 'Email formats require an address or domain you control.' : 'History contains only values you copy, and clears when your vault locks.'}</p><div class="context-tip">${icon('lock')} Generated locally</div></aside></div></div>`;
}

const reportData = {
  results: { title: 'Reused passwords', note: 'These two accounts use the same password. Give each account a unique password.', rows: [['Amazon', 'alex@example.com', 'Shares a password with Reddit'], ['Reddit', 'alex-review', 'Shares a password with Amazon']] },
  weak: { title: 'Weak passwords', note: 'Change the password on each website, then update the matching saved login.', rows: [['Amazon', 'alex@example.com', 'Weak password'], ['Reddit', 'alex-review', 'Weak password'], ['Router', 'admin', 'Weak password']] },
  unsafe: { title: 'Unencrypted websites', note: 'Confirm the website supports HTTPS before changing the saved address.', rows: [['Router', 'admin', 'http://router.local']] },
};

function reports(s, ui) {
  const { header, button, icon, pill, section, note } = ui;
  const expanded = reportData[s.variant];
  const failure = s.variant === 'exposure-failed';
  const overview = `<div class="report-list">${[['key', 'Reused passwords', '1 group · 2 accounts share a password', 'reports-results'], ['lock', 'Weak passwords', '3 saved logins need a stronger password', 'reports-weak'], ['globe', 'Unencrypted websites', '1 login has an HTTP address', 'reports-unsafe']].map(([i, t, d, go]) => `<div class="report-row">${icon(i)}<div><strong>${t}</strong><p>${d}</p></div>${button('Review', '', 'arrow', go)}</div>`).join('')}</div>`;
  const results = expanded ? section(expanded.title, `<p class="section-description">${expanded.note}</p>${expanded.rows.map(([n, user, finding]) => `<div class="result-row"><span class="item-avatar">${icon('key')}</span><div><strong>${n}</strong><p>${user}</p><small>${finding}</small></div>${toolButton(ui, 'Review item', 'review-report-item', '', 'arrow', `data-report-name="${n}" data-report-user="${user}"`)}</div>`).join('')}<div class="padded tool-report-detail" data-report-detail hidden></div>`) : overview;
  return `<div class="page-content tools-page">${header('Security report', 'Review findings from the items in your unlocked vault.', toolButton(ui, 'Refresh local report', 'refresh-report', '', 'generator'))}<div class="tool-report-summary"><div><h2>5 findings to review</h2><p>1 reused group + 3 weak passwords + 1 HTTP address</p><small>A login may appear in more than one category.</small></div>${pill('Local check complete')}</div><div class="report-tabs" role="tablist" aria-label="Security report category">${[['overview', 'Overview', 'reports-overview'], ['results', 'Reused · 1 group', 'reports-results'], ['weak', 'Weak · 3', 'reports-weak'], ['unsafe', 'HTTP · 1', 'reports-unsafe']].map(([v, t, go]) => `<button role="tab" aria-selected="${s.variant === v}" class="${s.variant === v ? 'active' : ''}" data-go="${go}">${t}</button>`).join('')}</div>${results}<section class="section tool-exposure"><div><div class="tool-section-inline"><h3>Exposed passwords</h3>${pill(failure ? 'Check failed' : 'Not checked', failure ? 'warning' : '')}</div><p>Look for saved passwords in known data breaches.</p><small>Only a hash prefix is sent to Have I Been Pwned. Your passwords are never sent.</small>${failure ? note('Exposure status is unknown', 'The service could not be reached. Retry when you are online. Local findings are still available.', 'warning') : ''}</div>${toolButton(ui, failure ? 'Retry check' : 'Run check', 'exposure-check', '', 'report')}</section><p class="page-footnote">${icon('info')}Confirm the new credential works on the website before updating your saved item.</p></div>`;
}

function importExport(s, ui) {
  const { header, icon, button, note, select, pill } = ui;
  const exportCount = ui.summary?.personalExportCount ?? 14;
  const preview = s.variant === 'preview';
  return `<div class="page-content tools-page">${header('Import and export', 'Move personal vault items with a clear view of what is included.')}<div class="transfer-grid"><section class="transfer-card"><span class="transfer-icon">${icon('upload')}</span><h2>Import items</h2><p>Add items from a Bitwarden JSON or CSV file.</p><label class="form-field"><span>File format</span><select data-import-format><option>Bitwarden JSON</option><option>Bitwarden CSV</option></select></label><div class="file-drop compact">${icon('note')}<strong>${preview ? 'review-import.json' : 'Choose an export file'}</strong><span>${preview ? 'Parsed · Bitwarden JSON' : 'JSON or CSV'}</span>${toolButton(ui, preview ? 'Choose another file' : 'Choose file', 'choose-import', '', 'folder')}</div><div class="transfer-preview">${preview ? `<div class="tool-section-inline"><strong>Ready to import</strong>${pill('Preview complete', 'success')}</div><p>3 items · 1 folder</p><small>Imports add new items. Existing duplicates are not merged.</small>` : '<strong>No file selected</strong><small>Select a file to review item and folder counts.</small>'}</div>${toolButton(ui, preview ? 'Import 3 items' : 'Import items', 'import-items', preview ? 'primary wide' : 'wide', '', preview ? 'data-import-submit' : 'disabled data-import-submit')}</section><section class="transfer-card"><span class="transfer-icon">${icon('download')}</span><h2>Export personal items</h2><p>Save a portable copy of your personal vault.</p>${note('The file will contain readable secrets', 'Exported passwords and notes are not encrypted. Choose a private destination.', 'warning')}<label class="form-field"><span>Export format</span><select data-export-format><option>Bitwarden JSON</option><option>Bitwarden CSV</option></select></label><div class="export-scope"><strong>Personal vault · ${exportCount} items</strong><span>Includes archived items. Excludes organization items, trash, and attachments.</span><small data-export-help>JSON preserves the supported item fields.</small></div>${button('Continue to export', 'wide', '', 'export-confirm', 'data-export-continue')}</section></div></div>`;
}

function devices(s, ui) {
  const { header, icon, button, pill, section } = ui;
  return `<div class="page-content tools-page">${header('Devices', 'Recognize registered devices and review new sign-in requests.', toolButton(ui, 'Refresh', 'refresh-devices', '', 'generator'))}<section class="pending-request"><div class="pending-head">${icon('device')}<div><h2>New device requests access</h2><p>Windows · Requested 2 minutes ago · 192.0.2.24</p></div>${pill('Action required', 'warning')}</div><div class="pending-body"><div><span class="field-label">Compare all five words on both devices</span><p class="mono tool-fingerprint">${fingerprint}</p><small>Approve only if you initiated this sign-in.</small></div><div>${button('Review request', 'primary', '', 'device-approve')}${toolButton(ui, 'Deny', 'deny-device')}</div></div></section>${section('Registered devices', `<div class="device-list">${[['WinVaultWarden', true, '15 Sep 2026, 14:00'], ['Review workstation', false, '12 Sep 2026, 10:42'], ['Review laptop', false, '08 Sep 2026, 18:15']].map(([name, current, date]) => `<div class="device-row"><span class="item-avatar">${icon('device')}</span><div><strong>${name} ${current ? pill('This device') : ''}</strong><p>Windows</p></div><div class="device-last"><span>${date}</span><small>Registered</small></div></div>`).join('')}</div>`)}<div class="page-footnote">${icon('info')}Registration dates do not indicate when a device last accessed your vault.</div></div>`;
}

function emergency(s, ui) {
  const { header, button, icon, pill, section } = ui;
  const contacts = [['JR', 'Jordan Review', 'trusted-contact@winvaultwarden.example', 'Confirmed', 'View', 'Manage'], ['SR', 'Sam Review', 'pending-contact@winvaultwarden.example', 'Invited', 'Takeover', 'Resend'], ['MR', 'Morgan Review', 'accepted-contact@winvaultwarden.example', 'Needs confirmation', 'View', 'Confirm']];
  return `<div class="page-content tools-page"><div class="page-context">${button('Settings', '', 'back', 'settings-security')}<span>/</span>Emergency access</div>${header('Emergency access', 'Choose trusted people and control when they may receive access.', button('Invite contact', 'primary', 'add', 'emergency-invite'))}<div class="emergency-intro">${icon('shield')}<p>After an accepted invitation is confirmed, the contact may request access. Each request must be approved or complete its waiting period.</p></div>${section('People who can request my vault', `<div class="tool-emergency-list">${contacts.map(([a, name, email, status, type, action]) => `<div class="tool-contact-row"><span class="avatar">${a}</span><div><strong>${name}</strong><p>${email}</p><small>${type} access · 7-day wait</small></div>${pill(status, status === 'Needs confirmation' ? 'warning' : '')}${toolButton(ui, action, 'emergency-contact', action === 'Confirm' ? 'primary small' : 'small', '', `data-contact="${name}" data-contact-action="${action}"`)}</div>`).join('')}</div>`)}${section('Accounts that trust me', `<div class="tool-contact-row"><span class="avatar">JR</span><div><strong>Jordan Review</strong><p>trusted-contact@winvaultwarden.example</p><small>View access · Read only</small></div>${pill('Access approved', 'success')}${button('View vault', '', 'arrow', 'emergency-recovered')}</div><div class="tool-contact-row"><span class="avatar">CR</span><div><strong>Casey Review</strong><p>casey@winvaultwarden.example</p><small>Takeover access · Resets their master password</small></div>${pill('Access approved', 'warning')}${button('Review takeover', '', 'arrow', 'emergency-takeover')}</div>`)}<p class="page-footnote">${icon('clock')}Waiting periods start when a contact requests access, not when the invitation is accepted.</p></div>`;
}

function systemPage(s, ui) {
  const { header, icon, button, section, note } = ui;
  return `<div class="page-content tools-page">${header('Use a passkey', 'Review the website and choose the account you want to use.')}<div class="tool-passkey-layout"><section class="section"><div class="section-title"><h3>Sign-in request</h3></div><div class="padded"><div class="tool-origin"><span class="field-label">Website requesting access</span><strong>https://login.example.com</strong></div><div class="tool-origin"><span class="field-label">Passkey domain (RP ID)</span><strong>example.com</strong></div><details class="disclosure"><summary><strong>Request details</strong>${icon('chevron')}</summary><div class="padded"><p>Frame origin: https://login.example.com</p><p>Top-level origin: https://login.example.com</p><small>Unicode and ASCII names match for this request.</small></div></details><label class="form-field tool-passkey-choice"><span>Account</span><select data-passkey-choice><option value="">Choose an account</option value="work">Alex Review · Work account</option><option value="personal">Alex Review · Personal account</option></select></label>${note('Approve only a request you recognize', 'Use a passkey only for the website you intended to sign in to.')}<div class="tool-passkey-actions">${toolButton(ui, 'Use passkey', 'passkey', 'primary', 'key', 'disabled data-passkey-submit')}${button('Cancel','','','vault-no-selection')}</div></div></section><aside class="tool-boundary-note"><span class="context-icon">${icon('device')}</span><h3>Verification may follow</h3><p>Windows may ask you to use Windows Hello or a security key. Canceling returns you to this request.</p><div class="notice"><div><strong>Review boundary</strong><p>The operating system and provider screens remain external to this proposal.</p></div></div></aside></div></div>`;
}

export function renderToolPage(s, ui) {
  if (s.page === 'send') return sendPage(s, ui);
  if (s.page === 'generator') return generator(s, ui);
  if (s.page === 'reports') return reports(s, ui);
  if (s.page === 'import-export') return importExport(s, ui);
  if (s.page === 'devices') return devices(s, ui);
  if (s.page === 'emergency') return emergency(s, ui);
  if (s.page === 'system') return systemPage(s, ui);
  return null;
}

export function toolDialog(s, ui) {
  const { e, icon, button, label, note, section, select, setting, toggle, pill } = ui;
  const exportCount = ui.summary?.personalExportCount ?? 14;
  const d = s.dialog || '';
  if (['edit-text', 'edit-file', 'existing-text', 'existing-file'].includes(d)) {
    const file = d.endsWith('file');
    const existing = d.startsWith('existing');
    const type = file ? 'file' : 'text';
    const typeTabs = existing ? `<div class="tool-section-inline">${pill(file ? 'File Send' : 'Text Send')}<small>The Send type cannot be changed.</small></div>` : `<div class="segmented">${[['text', 'Text'], ['file', 'File']].map(([v, t]) => `<button class="${type === v ? 'active' : ''}" data-go="send-edit-${v}">${t}</button>`).join('')}</div>`;
    const content = file ? `<div class="tool-file-chip">${icon('attachment')}<div><strong>review-checklist.txt</strong><small>32 KB · ${existing ? 'Current file' : 'Ready to encrypt'}</small></div>${toolButton(ui, existing ? 'Replace file' : 'Choose file', 'choose-send-file')}</div>` : `<label class="form-field"><span>Text to share</span><textarea data-send-content rows="3">The updated recovery checklist is ready for review.</textarea></label><label class="check-line"><input type="checkbox">Hide text by default when opened</label>`;
    const body = `<div class="tools-dialog" data-send-editor>${typeTabs}${label('Name', file ? 'Review checklist' : 'Review handoff', 'text', '', 'data-send-name')}${content}<div class="form-columns"><label class="form-field"><span>Delete after</span><select data-send-deletion><option>7 days</option><option>1 day</option><option>30 days</option><option>Custom</option></select></label><label class="form-field"><span>Stop access after</span><select data-send-expiration><option>When deleted</option><option>1 hour</option><option>1 day</option><option>7 days</option><option>Custom</option></select></label></div><div class="form-columns" data-send-custom hidden><div data-custom-delete hidden>${label('Deletion date', '2026-09-22', 'date', 'Within the server limit.', 'min="2026-09-16" max="2026-10-15"')}</div><div data-custom-expiry hidden>${label('Access expiry', '2026-09-21T14:00', 'datetime-local', 'Your local time. Must precede deletion.')}</div></div><p class="tool-inline-help">Deletion permanently removes the content. An earlier access expiry keeps it saved but prevents opening the link.</p><details class="disclosure"><summary><strong>Access and privacy</strong><span>Anyone with the link</span>${icon('chevron')}</summary><div class="padded">${label('Access password (optional)', '', 'password', existing ? 'Leave unchanged unless you intend to update the access password.' : 'Send the password through a separate channel.', 'autocomplete="off"')}${setting('Maximum accesses', 'Leave empty for no limit.', '<input class="small-number" type="number" min="0" placeholder="No limit" aria-label="Maximum accesses">')}${toggle('Hide my email', 'Recipients will not see your email address.')}${toggle('Disable this Send', 'Stop access without deleting the content.')}${label('Private notes', '', 'text', 'Visible only to you.')}</div></details></div>`;
    return { title: existing ? `Edit ${type} Send` : `New ${type} Send`, desc: existing ? 'Update this Send while preserving its sharing settings.' : 'Choose the content and when access should end.', body, primary: existing ? 'Save changes' : 'Create Send', width: 'wide-dialog', primaryAttrs: 'data-tool="save-send" data-send-submit' };
  }
  if (d === 'delete-send') return { title: 'Delete this Send permanently?', desc: 'Review handoff · Text Send', body: note('The shared link will stop working', 'The content will be permanently removed from Send. This cannot be undone.', 'danger'), primary: 'Delete Send', tone: 'danger', primaryAttrs: 'data-tool="delete-send"' };
  if (d === 'export') return { title: 'Export your personal vault', desc: 'Verify your identity before choosing a destination.', body: note('This file will contain readable secrets', 'Passwords, notes, and supported fields will be saved without encryption.', 'warning') + '<div class="summary-lines"><div><span>Format</span><strong>Bitwarden JSON</strong></div><div><span>Included</span><strong>' + exportCount + ' personal items, including archive</strong></div><div><span>Excluded</span><strong>Organizations, trash, attachments</strong></div></div>' + label('Master password', '', 'password', '', 'data-export-password autocomplete="off"'), primary: 'Verify and choose destination', primaryAttrs: 'data-tool="export" disabled data-export-submit' };
  if (d === 'approve-device') return { title: 'Approve this sign-in request?', desc: 'This grants the requesting device access to your unlocked vault.', body: `<div class="modal-item">${icon('device')}<div><strong>Windows device · 192.0.2.24</strong><span>Requested 2 minutes ago</span></div></div><div class="approval-phrase"><span class="field-label">Both devices must show all five words</span><p class="mono tool-fingerprint">${fingerprint}</p></div><label class="check-line"><input type="checkbox" data-device-verified>I initiated this sign-in and the phrases match.</label>${note('Do not recognize this request?', 'Cancel, then deny the request on the Devices page.', 'warning')}`, primary: 'Confirm and approve', primaryAttrs: 'data-tool="approve-device" disabled data-device-submit' };
  if (d === 'invite') return { title: 'Invite an emergency contact', desc: 'They must accept your invitation and you must confirm the contact.', body: `<div data-emergency-form>${label('Contact email', 'jordan@example.com', 'email', '', 'data-emergency-email')}<label class="form-field"><span>Access type</span><select data-emergency-type><option>View vault</option><option>Take over account</option></select><small data-emergency-access-help>View allows reading your personal vault after access is granted.</small></label>${label('Waiting period (days)', '7', 'number', '1–90 days, beginning when the contact requests access.', 'data-emergency-wait min="1" max="90"')}${note('Access is not immediate', 'After you confirm the contact, each access request must be approved by you or complete the waiting period.')}</div>`, primary: 'Send invitation', primaryAttrs: 'data-tool="invite" data-invite-submit' };
  if (d === 'generator-history') return { title: 'Generator history', desc: 'Copied values in this unlocked session. Cleared when the vault locks.', body: `<div class="tool-history-list" data-tool-history>${[['N7#qR!p2@xK8vT4$mL9z', 'Password · Copied 2 minutes ago'], ['amber-cedar7-river-meadow', 'Passphrase · Copied 5 minutes ago']].map(([v, t]) => `<div class="tool-history-row"><div><strong class="mono">${e(v)}</strong><small>${t}</small></div>${toolButton(ui, 'Copy', 'copy-history', '', 'copy')}</div>`).join('')}</div><div data-history-confirm hidden>${note('Clear this session history?', 'These copied values will be removed from history. Saved vault items are unaffected.', 'warning')}${toolButton(ui, 'Clear history', 'confirm-history-clear', 'danger')}${toolButton(ui, 'Keep history', 'cancel-history-clear')}</div>`, primary: 'Clear history', primaryAttrs: 'data-tool="clear-history"' };
  if (d === 'recovered') return { title: 'Jordan Review’s vault', desc: 'trusted-contact@winvaultwarden.example · Approved View access', body: `${note('Read-only emergency access', 'You can review this vault. Account takeover and editing are not granted.')}${section('2 recovered items', '<div class="tool-recovered-row"><strong>Personal email</strong><small>jordan@example.com</small></div><div class="tool-recovered-row"><strong>Recovery instructions</strong><small>Secure note</small></div>')}`, primary: 'Done', secondary: null };
  if (d === 'takeover') return { title: 'Take over Casey Review’s account?', desc: 'casey@winvaultwarden.example · Approved Takeover access', body: `${note('This resets their master password', 'Their existing password will stop working. Vaultwarden also removes their two-step verification and organization memberships where they are not an owner.', 'danger')}${label('New master password for Casey', '', 'password', '', 'data-takeover-password autocomplete="new-password"')}${label('Confirm new master password', '', 'password', '', 'data-takeover-confirm autocomplete="new-password"')}`, primary: 'Confirm takeover', tone: 'danger', primaryAttrs: 'data-tool="takeover" disabled data-takeover-submit' };
  return null;
}

export const toolsCss = `
.tools-page .page-context .button{height:28px;padding:0;border:0;box-shadow:none;color:var(--accent-text);background:transparent}
.tools-page .page-context{margin-bottom:12px}.tools-page .data-row{min-height:96px;padding-top:16px;padding-bottom:16px}
.tool-send-grid{display:grid;grid-template-columns:minmax(210px,1fr) 174px 150px 154px;align-items:center;gap:18px}
.tool-send-table .data-row{min-height:88px;padding-top:18px;padding-bottom:18px}.tool-send-table .entity strong{font-size:14px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.tool-send-table .entity p{font-size:12px;white-space:nowrap}.tool-send-table .tool-send-access>small,.tool-send-table .tool-send-availability>small{font-size:12px;margin-top:6px;line-height:18px}.tool-send-table .tool-send-availability>strong{font-size:13px;font-weight:500;font-variant-numeric:tabular-nums}.tool-send-table .tool-send-unavailable{color:var(--sub)}.tool-send-table .tool-send-actions{align-items:center;justify-content:flex-end;gap:8px}.tool-send-copy{height:36px;min-width:110px;padding:0 12px}.tool-send-table .tool-row-menu>summary{width:36px;height:36px}.tool-send-footnote{max-width:790px;line-height:20px}
@media(max-width:1100px){.tool-send-table .tool-send-grid{grid-template-columns:minmax(0,1fr) 142px 132px 144px;gap:12px;padding-left:16px;padding-right:16px}.tool-send-table .entity{gap:9px}.tool-send-table .item-avatar{width:30px;height:30px;flex-basis:30px}.tool-send-table .entity strong{font-size:13px}.tool-send-table .entity p,.tool-send-table .tool-send-access>small,.tool-send-table .tool-send-availability>small{font-size:11px}.tool-send-table .tool-send-copy{min-width:102px;padding:0 10px}.tool-send-table .tool-send-actions{gap:6px}}
.tool-row-menu{position:relative}.tool-row-menu>summary{display:grid;place-items:center;list-style:none;width:32px;height:32px;border-radius:5px;color:var(--sub);cursor:pointer}.tool-row-menu>summary::-webkit-details-marker{display:none}.tool-row-menu[open]>summary{background:var(--tint)}.tool-menu-content{position:absolute;z-index:5;right:0;top:34px;width:155px;display:grid;gap:4px;padding:5px;background:var(--surface);border:1px solid var(--line);box-shadow:var(--shadow);border-radius:7px}.tool-menu-content .button{justify-content:flex-start;border:0;box-shadow:none}.tool-send-table{overflow:visible}
.tools-dialog>.segmented,.tools-dialog>.tool-section-inline{margin-bottom:18px}.tool-file-chip{display:flex;align-items:center;gap:12px;padding:16px;border:1px solid var(--line);border-radius:7px;margin-bottom:20px;background:var(--surface-alt)}.tool-file-chip>div{flex:1}.tool-file-chip strong,.tool-file-chip small{display:block}.tool-file-chip small{margin-top:4px}.tool-inline-help{font-size:12px;color:var(--sub);margin:0 0 17px;line-height:19px}.tools-dialog .disclosure .setting-row{padding-left:0;padding-right:0}.tools-dialog .disclosure .form-field{margin-top:12px}.tools-dialog .disclosure .padded{padding-top:3px}.tools-dialog .form-columns .form-field{margin-bottom:10px}
.received-source{display:flex;align-items:center;justify-content:space-between;margin-bottom:22px;gap:20px}.received-source>div{display:flex;align-items:center;gap:8px;font-size:12px;color:var(--sub)}.tool-result-body{padding:10px 20px 28px}.tool-result-body p{margin-top:20px;font-size:14px;line-height:23px}.received-result-actions{display:flex;align-items:center;justify-content:space-between;padding:17px 20px;border-top:1px solid var(--line);font-size:12px}.tool-state-link{display:flex;gap:10px;margin-top:18px}.tool-state-link .button{font-size:12px;color:var(--sub)}
.tools-page .generated-value>p{overflow-wrap:anywhere;max-height:108px;overflow:auto}.tools-page .generated-value>div{display:flex;justify-content:space-between}.tool-generator-advanced{margin:0;border-left:0;border-right:0;border-bottom:0;border-radius:0}.tool-generator-advanced>summary{padding:14px 18px}.tool-generator-advanced>summary span:not(.icon){font-size:11px;color:var(--sub);margin-left:auto}.tools-page .range-field{display:flex;align-items:center;gap:12px}.tools-page .range-field input[type=range]{width:145px}.tools-page .range-field .small-number{width:62px}.tools-page .setting-row>label:not(.switch){display:flex;align-items:center;gap:8px;font-size:12px;color:var(--sub)}
.tool-report-summary{display:flex;align-items:center;justify-content:space-between;gap:20px;padding:4px 0 24px}.tool-report-summary p{margin-top:6px;color:var(--sub);font-size:13px}.tool-report-summary small{display:block;margin-top:4px}.tools-page .report-tabs{margin-bottom:24px}.tools-page .result-row>div{flex:1}.tool-exposure{padding:20px;display:flex;gap:22px;align-items:center;justify-content:space-between}.tool-exposure>div{flex:1}.tool-exposure p{margin:8px 0;color:var(--sub);font-size:13px}.tool-exposure small{font-size:12px;line-height:19px;display:block}.tool-section-inline{display:flex;align-items:center;gap:12px}.tool-report-detail{border-top:1px solid var(--line);background:var(--surface-alt)}.tool-report-detail p{margin:9px 0;color:var(--sub)}
.tools-page .transfer-card .file-drop{min-height:144px}.tools-page .transfer-preview{min-height:106px}.tools-page .transfer-preview p{margin:8px 0}.tools-page .transfer-preview small{display:block;line-height:19px}.tools-page .export-scope span{display:block;line-height:21px}.tools-page .export-scope small{display:block;margin-top:12px}.tools-page .transfer-card>p{min-height:40px}.tools-page .transfer-card>h2{font-size:22px}
.tool-fingerprint{font-size:17px!important;letter-spacing:0;overflow-wrap:anywhere}.tools-page .pending-body{gap:18px}.tools-page .pending-body>div:last-child{display:flex;gap:8px}.tools-page .device-row{min-height:76px}.tool-contact-row{display:flex;align-items:center;gap:14px;padding:12px 20px;border-top:1px solid var(--line)}.tool-contact-row>div{flex:1;min-width:0}.tool-contact-row strong{font-size:14px}.tool-contact-row p{font-size:12px;color:var(--sub);margin-top:3px}.tool-contact-row small{display:block;margin-top:6px}.tools-page .emergency-intro{margin-bottom:22px}.tools-page .emergency-intro p{line-height:20px}.tools-page .tool-contact-row>.pill{font-size:11px}
.tool-history-row{display:flex;align-items:center;gap:12px;padding:15px 0;border-bottom:1px solid var(--line)}.tool-history-row>div{flex:1;min-width:0}.tool-history-row strong{display:block;font-size:14px;overflow-wrap:anywhere}.tool-history-row small{display:block;margin-top:6px}.tool-recovered-row{display:flex;flex-direction:column;gap:6px;padding:16px 20px;border-top:1px solid var(--line)}
.tool-passkey-layout{display:grid;grid-template-columns:minmax(0,620px) minmax(190px,1fr);gap:28px}.tool-passkey-layout>.section{margin:0}.tool-origin{display:flex;flex-direction:column;gap:6px;padding-bottom:20px}.tool-origin strong{font-size:18px}.tool-passkey-choice{margin-top:22px}.tool-boundary-note{padding:18px 0;border-left:1px solid var(--line);padding-left:24px}.tool-boundary-note h3{margin:20px 0 12px}.tool-boundary-note>p{font-size:13px;line-height:22px;color:var(--sub)}.tool-boundary-note .notice{margin-top:24px}.tool-passkey-layout .disclosure p{font-size:12px;line-height:22px;overflow-wrap:anywhere}
`;

export const toolsScript = String.raw`
(() => {
  let generatorStep = 0;
  const by = key => document.querySelector('[data-gen="' + key + '"]');
  const generatorRoot = document.querySelector('[data-generator-mode]');
  const getValue = key => by(key)?.value || '';
  const checked = key => Boolean(by(key)?.checked);
  function generatePreview() {
    if (!generatorRoot) return;
    const mode = generatorRoot.dataset.generatorMode;
    const out = document.querySelector('[data-tool-generated]');
    const status = document.querySelector('[data-gen-status]');
    const copy = document.querySelector('[data-gen-copy]');
    const words = ['amber', 'cedar', 'river', 'meadow', 'harbor', 'maple', 'sunset', 'ocean'];
    let value = '';
    let problem = '';
    if (mode === 'password') {
      const length = Math.min(128, Math.max(5, Number(getValue('length')) || 20));
      const groups = [checked('upper') ? 'ABCDEFGHJKLMNPQRSTUVWXYZ' : '', checked('lower') ? 'abcdefghijkmnopqrstuvwxyz' : '', checked('digits') ? '23456789' : '', checked('symbols') ? '!@#$%&*?' : ''].filter(Boolean);
      if (!groups.length) problem = 'Choose at least one character type.';
      else {
        let minDigits = Math.max(0, Number(getValue('min-digits')) || 0);
        let minSymbols = Math.max(0, Number(getValue('min-symbols')) || 0);
        if (!checked('digits')) minDigits = 0;
        if (!checked('symbols')) minSymbols = 0;
        const requiredLetters = Number(checked('upper')) + Number(checked('lower'));
        if (minDigits + minSymbols + requiredLetters > length) problem = 'Minimum counts and required character types must fit within the length.';
        else {
          const all = groups.join('') + (checked('ambiguous') ? '' : (checked('upper') ? 'IO' : '') + (checked('lower') ? 'l' : '') + (checked('digits') ? '01' : ''));
          const prefix = (checked('upper') ? 'ABCDEFGH'[(generatorStep + 2) % 8] : '') + (checked('lower') ? 'abcdefgh'[(generatorStep + 5) % 8] : '') + Array.from({length: minDigits}, (_, i) => '23456789'[(i + generatorStep) % 8]).join('') + Array.from({length: minSymbols}, (_, i) => '!@#$%&*?'[(i + generatorStep) % 8]).join('');
          value = prefix + Array.from({length: length - prefix.length}, (_, i) => all[(i * 17 + generatorStep * 11) % all.length]).join('');
        }
      }
      status.textContent = problem || length + ' characters';
    } else if (mode === 'passphrase') {
      const count = Math.min(20, Math.max(3, Number(getValue('words')) || 4));
      const parts = Array.from({length: count}, (_, i) => words[(i + generatorStep) % words.length]);
      if (checked('capitalize')) for (let i = 0; i < parts.length; i++) parts[i] = parts[i][0].toUpperCase() + parts[i].slice(1);
      if (checked('number')) parts[1] += String((7 + generatorStep) % 10);
      value = parts.join(getValue('separator'));
      status.textContent = count + ' words';
    } else {
      const type = getValue('username-type');
      document.querySelector('[data-gen-word]').hidden = type !== 'word';
      document.querySelector('[data-gen-email]').hidden = type !== 'plus';
      document.querySelector('[data-gen-domain]').hidden = type !== 'catchall';
      const word = words[(1 + generatorStep) % words.length];
      const suffix = String(4826 + generatorStep).padStart(4, '0').slice(-4);
      if (type === 'word') value = (checked('capitalize') ? word[0].toUpperCase() + word.slice(1) : word) + (checked('number') ? suffix : '');
      else if (type === 'plus') {
        const email = getValue('email').trim();
        if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email)) problem = 'Enter your email address to generate an alias.';
        else { const at = email.lastIndexOf('@'); value = email.slice(0, at) + '+' + word + suffix + email.slice(at); }
      } else {
        const domain = getValue('domain').trim();
        if (!/^[a-z0-9.-]+\.[a-z]{2,}$/i.test(domain)) problem = 'Enter your configured catch-all domain.';
        else value = word + suffix + '@' + domain;
      }
      status.textContent = problem || (type === 'word' ? 'Random word' + (checked('number') ? ' + four digits' : '') : type === 'plus' ? 'Plus-addressed email' : 'Catch-all email');
    }
    out.textContent = value || '—';
    copy.disabled = Boolean(problem);
  }
  function checkForms() {
    const has = selector => Boolean(document.querySelector(selector)?.value.trim());
    const exportButton = document.querySelector('[data-export-submit]');
    if (exportButton) exportButton.disabled = !has('[data-export-password]');
    const approval = document.querySelector('[data-device-submit]');
    if (approval) approval.disabled = !document.querySelector('[data-device-verified]')?.checked;
    const open = document.querySelector('[data-send-open]');
    if (open) {
      let valid = false;
      try { const url = new URL(document.querySelector('[data-send-link]')?.value || ''); valid = ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password && /^#\/send\/[^/\s]+\/[^/\s]+$/.test(url.hash); } catch {}
      open.disabled = !valid;
    }
    const save = document.querySelector('[data-send-submit]');
    if (save) save.disabled = !has('[data-send-name]') || (document.querySelector('[data-send-content]') && !has('[data-send-content]'));
    const takeover = document.querySelector('[data-takeover-submit]');
    if (takeover) { const password = document.querySelector('[data-takeover-password]').value; takeover.disabled = !password || password !== document.querySelector('[data-takeover-confirm]').value; }
    const invite = document.querySelector('[data-invite-submit]');
    if (invite) { const email = document.querySelector('[data-emergency-email]'); const wait = Number(document.querySelector('[data-emergency-wait]')?.value); invite.disabled = !email?.validity.valid || !email.value.trim() || wait < 1 || wait > 90; }
    const passkey = document.querySelector('[data-passkey-submit]');
    if (passkey) passkey.disabled = !has('[data-passkey-choice]');
  }
  document.addEventListener('input', event => {
    if (event.target.matches('[data-gen]')) {
      if (event.target.dataset.gen === 'length') by('length-number').value = event.target.value;
      if (event.target.dataset.gen === 'length-number') by('length').value = event.target.value;
      generatePreview();
    }
    checkForms();
  });
  document.addEventListener('change', event => {
    if (event.target.matches('[data-gen]')) generatePreview();
    if (event.target.matches('[data-send-deletion],[data-send-expiration]')) {
      const deletion = document.querySelector('[data-send-deletion]').value === 'Custom';
      const expiration = document.querySelector('[data-send-expiration]').value === 'Custom';
      document.querySelector('[data-send-custom]').hidden = !deletion && !expiration;
      document.querySelector('[data-custom-delete]').hidden = !deletion;
      document.querySelector('[data-custom-expiry]').hidden = !expiration;
    }
    if (event.target.matches('[data-emergency-type]')) document.querySelector('[data-emergency-access-help]').textContent = event.target.value === 'View vault' ? 'View allows reading your personal vault after access is granted.' : 'Takeover allows resetting your master password and taking control of your account.';
    if (event.target.matches('[data-import-format]')) { const submit = document.querySelector('[data-import-submit]'); submit.disabled = true; document.querySelector('.transfer-preview').innerHTML = '<strong>Preview required</strong><small>Choose and preview a file for the selected format.</small>'; }
    if (event.target.matches('[data-export-format]')) {
      const unsupported = event.target.value === 'Bitwarden CSV';
      document.querySelector('[data-export-help]').textContent = unsupported ? 'This vault contains cards, identities, and SSH keys that CSV cannot export. Choose JSON to include them.' : 'JSON preserves the supported item fields.';
      document.querySelector('[data-export-continue]').disabled = unsupported;
    }
    checkForms();
  });
  document.addEventListener('click', event => {
    const b = event.target.closest('button[data-tool]');
    if (!b) return;
    event.preventDefault();
    const action = b.dataset.tool;
    if (action === 'send-filter') { for (const tab of b.parentElement.children) { tab.classList.toggle('active', tab === b); tab.setAttribute('aria-selected', String(tab === b)); } document.querySelectorAll('[data-send-type]').forEach(row => row.hidden = b.dataset.filter !== 'all' && row.dataset.sendType !== b.dataset.filter); return; }
    if (action === 'copy-send') { toast('Preview: copy link for ' + b.dataset.sendTarget + '. ' + (b.dataset.sendDisabled === 'true' ? 'This Send is disabled, so recipients cannot open the link. ' : '') + 'Your clipboard was not changed.'); return; }
    if (action === 'specific-send-edit' || action === 'specific-send-delete') { toast('Preview: ' + (action === 'specific-send-edit' ? 'open the editor for ' : 'confirm permanent deletion of ') + b.dataset.sendTarget + '. No Send was changed.'); return; }
    if (action === 'regenerate') { generatorStep++; generatePreview(); toast('Illustrative value updated. Do not use review values as real credentials.'); return; }
    if (action === 'clear-history') { document.querySelector('[data-history-confirm]').hidden = false; b.disabled = true; return; }
    if (action === 'cancel-history-clear') { document.querySelector('[data-history-confirm]').hidden = true; document.querySelector('[data-tool="clear-history"]').disabled = false; return; }
    if (action === 'confirm-history-clear') { document.querySelector('[data-tool-history]').innerHTML = '<div class="padded"><strong>No copied values in this preview</strong><p>Your saved vault items are unchanged.</p></div>'; document.querySelector('[data-history-confirm]').hidden = true; toast('Preview session history cleared.'); return; }
    if (action === 'review-report-item') { const panel = document.querySelector('[data-report-detail]'); panel.hidden = false; panel.replaceChildren(); const title = document.createElement('strong'); title.textContent = b.dataset.reportName; const user = document.createElement('p'); user.textContent = b.dataset.reportUser; const instruction = document.createElement('p'); instruction.textContent = 'Change the credential on this account’s website, then update this saved login. The production action opens this exact item.'; panel.append(title, user, instruction); return; }
    if (action.startsWith('copy')) { toast('Preview: copy the displayed synthetic value. Your clipboard was not changed.'); return; }
    if (action === 'choose-import') { toast('Preview: Windows opens the file picker. Use the Import preview scene to review a parsed file.'); return; }
    if (action === 'exposure-check') { toast('Preview: an explicit online exposure check starts here. No service was contacted.'); return; }
    if (action === 'emergency-contact') { toast('Preview: ' + b.dataset.contactAction + ' for ' + b.dataset.contact + '. No relationship was changed.'); return; }
    if (action === 'deny-device') { toast('Preview: confirm denial for Windows at 192.0.2.24. No request was denied.'); return; }
    const messages = {
      'received-open': 'Preview: the complete share link is verified before any content is revealed.',
      'save-file': 'Preview: Windows opens Save file. No file was downloaded.',
      'choose-send-file': 'Preview: Windows opens the file picker. Existing-file replacement preserves the service safeguards.',
      'save-send': 'Preview: validate, encrypt, and save this Send. No content was shared.',
      'delete-send': 'Preview: permanently delete Review handoff. No Send was deleted.',
      'export': 'Preview: verify the master password, then open the save picker. No vault was exported.',
      'import-items': 'Preview: import the three reviewed items. No items were imported.',
      'approve-device': 'Preview: approve this verified request. No device access was granted.',
      'invite': 'Preview: invite this contact with the selected access and waiting period. No invitation was sent.',
      'takeover': 'Preview: reset the named grantor’s password. No account was changed.',
      'passkey': 'Preview: use the selected credential for this verified website. No credential was sent.',
      'refresh-report': 'Preview: refresh local findings. No new report was run.',
      'refresh-devices': 'Preview: refresh registered devices and requests. No server was contacted.',
    };
    toast(messages[action] || 'Design preview: no account data changed.');
  });
  checkForms();
})();
`;
