// Synthetic, memory-only UI proposals. No vault service or real clipboard is used.
const types = { login: 'Login', card: 'Payment card', identity: 'Identity', note: 'Secure note', ssh: 'SSH key', bank: 'Bank account', license: 'Driver’s license', passport: 'Passport' };
const icons = { login: 'key', card: 'card', identity: 'person', note: 'note', ssh: 'code', bank: 'card', license: 'person', passport: 'globe' };
const base = { owner: 'Personal vault', folder: 'Personal', favorite: false, canEdit: true, canViewPassword: true, created: '15 Aug 2026', edited: '13 Sep 2026', notes: '', custom: [] };
const records = [
  { id: 'contoso', type: 'login', name: 'Contoso Cloud', username: 'alex.review@example.com', folder: 'Work', favorite: true, website: 'https://cloud.example.com', website2: 'https://console.example.com', password: 'Synthetic!Cloud#2026', totp: 'JBSWY3DPEHPK3PXP', notes: 'Review environment only. Contact the Platform team for access recovery.', passkey: true, attachment: true, history: true, custom: [{ name: 'Environment', value: 'Review', type: 'Text' }, { name: 'Recovery code', value: 'DEMO-RECOVERY-ONLY', type: 'Hidden' }] },
  { id: 'github', type: 'login', name: 'GitHub', username: 'alex.review', folder: 'Work', favorite: true, website: 'https://github.com', password: 'Synthetic!GitHub#2026', notes: 'Synthetic development account.' },
  { id: 'google', type: 'login', name: 'Google', username: 'alex@example.com', favorite: true, website: 'https://accounts.google.com', password: 'Synthetic!Google#2026' },
  { id: 'visa', type: 'card', name: 'Visa · 1111', folder: 'Finance', cardholder: 'Alex Review', number: '4111 1111 1111 1111', brand: 'Visa', month: '08', year: '2030', code: '123', notes: 'Synthetic card for interface review.' },
  { id: 'ssh', type: 'ssh', name: 'Production SSH', folder: 'Work', publicKey: 'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAISyntheticReviewOnly review@example.com', privateKey: '-----BEGIN OPENSSH PRIVATE KEY-----\nSYNTHETIC-REVIEW-KEY-NOT-VALID\n-----END OPENSSH PRIVATE KEY-----', fingerprint: 'SHA256:ReviewFixtureNonfunctionalKey', notes: 'Deployment key for the review environment. This sample is nonfunctional.' },
  { id: 'recovery', type: 'note', name: 'Recovery plan', attachment: true, notes: 'Keep a copy of this plan in a safe place.\n\n1. Contact the designated emergency contact.\n2. Review the attached recovery checklist.\n3. Confirm access to the correct vault.\n\nNo operational credentials are included.' },
  { id: 'identity', type: 'identity', name: 'Alex Review', title: 'Mx', firstName: 'Alex', middleName: 'Morgan', lastName: 'Review', username: 'alex.review', company: 'Contoso Review', ssn: '000-00-0000', passportNumber: 'TEST000000', licenseNumber: 'DEMO-000000', email: 'alex@example.com', phone: '+1 202 555 0100', address1: '100 Example Avenue', address2: 'Suite 10', address3: '', city: 'Example City', state: 'California', postalCode: '00000', country: 'United States' },
  { id: 'amazon', type: 'login', name: 'Amazon', username: 'alex@example.com', website: 'https://amazon.com', password: 'Demo-Reused-Only', notes: 'Synthetic account used in the reused-password report.' },
  { id: 'shared', type: 'login', name: 'Shared console', username: 'review-operator@example.com', owner: 'Contoso Review', collection: 'Engineering', folder: null, canEdit: false, canViewPassword: false, website: 'https://shared.example.com', password: '', notes: 'Shared by your organization. Your role allows viewing this item, with passwords hidden.' },
  { id: 'reddit', type: 'login', name: 'Reddit', username: 'alex-review', website: 'https://reddit.com', password: 'Demo-Reused-Only', notes: 'Synthetic account used in the reused-password report.' },
  { id: 'router', type: 'login', name: 'Router', username: 'admin', website: 'http://192.0.2.1', password: 'demo', notes: 'Review-only address from the documentation range.' },
  { id: 'bank', type: 'bank', name: 'Everyday checking', folder: 'Finance', bank: 'Example Credit Union', holder: 'Alex Review', accountType: 'Checking', accountNumber: '1234567890', routingNumber: '000000000', branchNumber: '000', pin: '0000', swift: 'EXAMPLE0XXX', iban: 'XX00 0000 0000 0000', bankPhone: '+1 202 555 0100' },
  { id: 'license', type: 'license', name: 'Alex’s driver’s license', firstName: 'Alex', middleName: 'Morgan', lastName: 'Review', birthDate: '1990-01-01', licenseNumber: 'DEMO-000000', issuingCountry: 'United States', issuingState: 'California', issueDate: '2024-06-12', expiryDate: '2030-06-12', authority: 'Example licensing authority', licenseClass: 'C' },
  { id: 'passport', type: 'passport', name: 'Alex’s passport', surname: 'Review', givenName: 'Alex Morgan', birthDate: '1990-01-01', sex: 'X', birthPlace: 'Example City', nationality: 'United States', issuingCountry: 'United States', passportNumber: 'TEST000000', passportType: 'P', nationalId: 'DEMO-000000', authority: 'Example passport authority', issueDate: '2024-03-18', expiryDate: '2034-03-18' },
  { id: 'archived-email', type: 'login', name: 'Legacy email', username: 'alex.legacy@example.com', website: 'https://mail.example.com', password: 'Synthetic!Legacy#2026', archived: true, archiveDate: '1 Sep 2026', notes: 'An archived item stays available here until you unarchive it.' },
  { id: 'trash-vpn', type: 'login', name: 'Old VPN', username: 'alex.review', folder: 'Work', website: 'https://vpn-old.example.com', password: 'Synthetic!OldVpn#2026', deleted: true, deleteDate: '9 Sep 2026', notes: 'A deleted review item. Restore it to use it again.' },
].map(x => ({ ...base, ...x }));
const active = records.filter(x => !x.archived && !x.deleted);
const recordForType = type => active.find(x => x.type === type) || active[0];
const route = record => record.deleted ? 'vault-trash' : record.archived ? 'vault-archive' : record.type === 'login' && record.id !== 'contoso' ? `vault-detail-${record.id}` : `vault-detail-${record.type}`;
const fullName = r => r.type === 'passport' ? [r.givenName, r.surname].filter(Boolean).join(' ') : [r.firstName, r.middleName, r.lastName].filter(Boolean).join(' ');
const subtitle = r => r.type === 'login' ? r.username : r.type === 'card' ? r.cardholder : r.type === 'ssh' ? 'Ed25519 · deployment' : r.type === 'identity' ? 'Personal identity' : r.type === 'note' ? 'Updated 13 Sep 2026' : 'Personal record';
const scene = (id, title, variant, more = {}) => ({ id, title, page: 'vault', group: 'Vault review additions', variant, direct: [], related: ['page.vault'], baselineFilename: null, changes: ['Keep item identity, permissions and navigation state consistent throughout the proposal.'], notes: 'Synthetic review composition. Interactions update the current preview only and never write to a vault server.', ...more });
export const vaultScenes = [
  ...['github', 'google', 'amazon', 'shared', 'reddit', 'router'].map(id => scene(`vault-detail-${id}`, `${records.find(x => x.id === id).name} details`, 'detail', { itemType: 'login', itemId: id })),
  scene('vault-new-login', 'Create a new vault item', 'new', { itemType: 'login', changes: ['Start with a blank draft; item type and writable destination are chosen before creation.', 'Require a name and the type-specific required fields before enabling Create item.'] }),
  scene('vault-favorites', 'Favorite items', 'favorites'),
  scene('vault-folder-work', 'Work folder', 'folder', { folder: 'Work' }),
  scene('vault-folder-personal', 'Personal folder', 'folder', { folder: 'Personal' }),
  scene('vault-folder-finance', 'Finance folder', 'folder', { folder: 'Finance' }),
  scene('vault-organization', 'Organization collection', 'organization'),
  scene('vault-archive', 'Archived item', 'archive', { itemType: 'login', itemId: 'archived-email' }),
  scene('vault-trash', 'Deleted item and restore', 'trash', { itemType: 'login', itemId: 'trash-vpn' }),
  scene('vault-pending-save', 'An unconfirmed creation keeps its draft', 'pending-save', { itemType: 'login', changes: ['Retain the draft when a create result is unknown.', 'Check synchronization before attempting another creation; do not enable duplicate saves.'] }),
  scene('vault-new-folder', 'Create a folder', 'no-selection', { dialog: 'new-folder', direct: ['vault.folder-create'], changes: ['Name the folder before creation and keep the action disabled until a name is entered.'], notes: 'Creates a folder in the local proposal view only. It does not make a server request.' }),
];

function chosen(s) {
  if (s.dialog === 'permanent-delete') return records.find(x => x.id === 'trash-vpn');
  const byId = records.find(x => x.id === s.itemId || `vault-detail-${x.id}` === s.id);
  if (byId) return byId;
  if (s.variant === 'archive') return records.find(x => x.archived);
  if (s.variant === 'trash') return records.find(x => x.deleted);
  return recordForType(s.itemType || 'login');
}
function scopeRows(s) {
  if (s.dialog === 'permanent-delete' || s.variant === 'trash') return records.filter(x => x.deleted);
  if (s.variant === 'archive') return records.filter(x => x.archived);
  if (s.variant === 'favorites') return active.filter(x => x.favorite);
  if (s.variant === 'folder') return active.filter(x => x.folder === (s.folder || 'Work'));
  if (s.variant === 'organization') return active.filter(x => x.owner !== 'Personal vault');
  if (s.variant === 'empty') return [];
  return active;
}
export function vaultNavigation(s) {
  const blank = s.variant === 'empty', loading = s.variant === 'loading';
  return { scopeLabel: s.variant === 'organization' ? 'Contoso Review' : 'All vaults', folderCounts: Object.fromEntries(['Work', 'Personal', 'Finance'].map(f => [f, loading ? null : blank ? 0 : active.filter(x => x.folder === f).length])), orgVisible: !blank && !loading, reportCount: loading ? null : blank ? 0 : 5, trashCount: loading ? null : blank ? 0 : 1, archiveCount: loading ? null : blank ? 0 : 1, activeCount: loading ? null : blank ? 0 : active.length, totalCount: loading ? null : blank ? 0 : records.length, personalExportCount: loading ? null : blank ? 0 : records.filter(x => !x.deleted && x.owner === 'Personal vault').length, canWriteNewTypes: true, activeFolder: s.variant === 'folder' ? s.folder || 'Work' : undefined, activeOrg: s.variant === 'organization' ? 'Contoso Review' : undefined };
}

function makeUi(ui) {
  const { e, icon, button, ib, pill, note, section } = ui;
  const action = (text, code, style = '', symbol = '', attrs = '') => button(text, style, symbol, '', `data-v-action="${e(code)}" ${attrs}`);
  const disclose = (title, body, summary = '', open = false) => `<details class="disclosure v-disclosure" ${open ? 'open' : ''}><summary><strong>${e(title)}</strong>${summary ? `<span>${e(summary)}</span>` : ''}${icon('chevron')}</summary><div class="v-disclosure-body">${body}</div></details>`;
  function field(name, value, key = '', options = {}) {
    const secret = options.secret, blocked = options.blocked;
    const empty = value === null || value === undefined || value === '';
    return `<div class="field-row v-field" data-v-field="${e(key)}"><div class="field-value"><span class="field-label">${e(name)}</span><span class="${secret || options.mono ? 'mono ' : ''}value" data-v-bind="${e(key)}" ${secret && !blocked ? `data-v-secret="${e(value || '')}"` : ''}>${blocked ? e(options.blockedText || 'Hidden by organization') : secret ? empty ? 'Not set' : '••••••••' : e(value || '—')}</span>${options.help ? `<small>${e(options.help)}</small>` : ''}</div><div class="field-actions">${options.open ? ib('arrow', 'Open ' + name, `data-v-action="website" data-v-url="${e(value)}" ${empty ? 'disabled' : ''}`) : ''}${secret && !blocked ? ib('eye', 'Show ' + name, `data-v-action="reveal" aria-pressed="false" data-v-label="${e(name)}" ${empty ? 'disabled' : ''}`) : ''}${options.copy !== false && !blocked ? key==='password' ? action('Copy password','copy','primary v-password-copy','copy',`data-v-label="${e(name)}" ${empty ? 'disabled' : ''}`) : ib('copy', 'Copy ' + name, `data-v-action="copy" data-v-label="${e(name)}" ${empty ? 'disabled' : ''}`) : ''}</div></div>`;
  }
  function input(name, key, value = '', options = {}) {
    const attrs = `name="${e(key)}" data-v-key="${e(key)}" ${options.required ? 'required' : ''} ${options.attrs || ''}`;
    let control;
    if (options.secret) control = `<div class="v-secret-input">${options.multiline ? `<textarea rows="4" class="mono v-secret-textarea" ${attrs} data-v-secret-textarea>${e(value)}</textarea>` : `<input type="password" value="${e(value)}" ${attrs} autocomplete="off">`}${ib('eye', 'Show ' + name, 'data-v-action="input-reveal" aria-pressed="false"')}</div>`;
    else if (options.multiline) control = `<textarea rows="${options.rows || 3}" ${attrs} class="${options.mono ? 'mono' : ''}">${e(value)}</textarea>`;
    else control = `<input type="${options.type || 'text'}" value="${e(value)}" ${attrs} class="${options.mono ? 'mono' : ''}">`;
    return `<label class="form-field v-input"><span>${e(name)}${options.required ? ' <span class="v-required">(required)</span>' : ''}</span>${control}${options.help ? `<small>${e(options.help)}</small>` : ''}</label>`;
  }
  const dropdown = (name, key, values, selected, attrs = '') => `<label class="form-field"><span>${e(name)}</span><select name="${e(key)}" data-v-key="${e(key)}" ${attrs}>${values.map(([value, text]) => `<option value="${e(value)}" ${value === selected ? 'selected' : ''}>${e(text)}</option>`).join('')}</select></label>`;
  return { ...ui, action, disclose, field, input, dropdown };
}

function detailMarkup(r, s, ui, preview = false) {
  const u = makeUi(ui), { e, icon, ib, pill, note, section, field, action, disclose } = u;
  const protectedItem = s.dialog === 'reprompt';
  const secretOptions = { secret: true, blocked: !r.canViewPassword };
  let body = '';
  if (r.type === 'login') {
    body = section('Credentials', field('Username', r.username, 'username') + field('Password', r.password, 'password', { ...secretOptions, help: r.canViewPassword ? 'Updated 3 Sep 2026' : 'Your organization role does not permit password access.' }) + (r.totp ? `<div class="field-row v-field"><div class="field-value"><span class="field-label">Verification code</span><strong class="totp mono" data-v-totp>${protectedItem ? '••• •••' : '482 916'}</strong><small>${protectedItem ? 'Verify your master password to show this code.' : 'Refreshes every 30 seconds'}</small></div><div class="field-actions">${protectedItem ? action('Verify', 'reprompt') : `<span class="countdown" aria-label="18 seconds remaining">18</span>${ib('copy', 'Copy verification code', 'data-v-action="copy" data-v-label="Verification code"')}`}</div></div>` : ''));
    body += section('Websites', field('Website', r.website, 'website', { open: true }) + (r.website2 ? field('Management portal', r.website2, 'website2', { open: true }) : ''));
    if (r.passkey) body += disclose('Passkeys', `<div class="v-passkey">${icon('key')}<div><strong>Contoso Cloud</strong><p>alex.review@example.com</p><small>cloud.example.com · Discoverable · Created 28 Aug 2026</small></div></div>`, '1 passkey');
  } else if (r.type === 'card') {
    body = `<div class="v-card-summary"><div>${icon('card')}<strong data-v-bind="brand">${e(r.brand)}</strong><span class="mono">•••• 1111</span></div><small>Payment card</small></div>` + section('Card details', field('Cardholder', r.cardholder, 'cardholder') + field('Card number', r.number, 'number', secretOptions) + field('Expiry date', `${r.month || '—'} / ${r.year || '—'}`, 'expiry') + field('Security code', r.code, 'code', secretOptions));
  } else if (r.type === 'identity') {
    body = section('Identity', field('Full name', fullName(r), 'fullName') + field('Username', r.username, 'username') + field('Company', r.company, 'company') + field('Identification number', r.ssn, 'ssn', secretOptions));
    body += section('Contact details', field('Email', r.email, 'email') + field('Phone', r.phone, 'phone') + field('Address', [r.address1, r.address2, r.address3, [r.city, r.state, r.postalCode].filter(Boolean).join(', '), r.country].filter(Boolean).join('\n'), 'fullAddress'));
    body += disclose('More identity details', field('Title', r.title, 'title') + field('Passport number', r.passportNumber, 'passportNumber', secretOptions) + field('License number', r.licenseNumber, 'licenseNumber', secretOptions));
  } else if (r.type === 'note') {
    body = section('Secure note', `<div class="v-note-content" data-v-bind="notes">${e(r.notes)}</div>`, action('Copy note', 'copy', 'small', 'copy', 'data-v-label="Note"'));
  } else if (r.type === 'ssh') {
    body = section('Key details', field('Key type', 'Ed25519', '', { copy: false }) + field('Fingerprint', r.fingerprint, 'fingerprint', { mono: true }) + field('Public key', r.publicKey, 'publicKey', { mono: true }));
    body += section('Private key', field('Private key', r.privateKey, 'privateKey', { ...secretOptions, help: 'Reveal only when you need to inspect it.' }));
  } else if (r.type === 'bank') {
    body = section('Bank details', field('Bank', r.bank, 'bank') + field('Account holder', r.holder, 'holder') + field('Account type', r.accountType, 'accountType') + field('Account number', r.accountNumber, 'accountNumber', secretOptions) + field('Routing number', r.routingNumber, 'routingNumber', secretOptions) + field('IBAN', r.iban, 'iban', secretOptions));
    body += disclose('Additional bank details', field('Branch number', r.branchNumber, 'branchNumber', secretOptions) + field('PIN', r.pin, 'pin', secretOptions) + field('SWIFT / BIC', r.swift, 'swift', secretOptions) + field('Bank contact phone', r.bankPhone, 'bankPhone'));
  } else if (r.type === 'license') {
    body = section('License details', field('Full name', fullName(r), 'fullName') + field('License number', r.licenseNumber, 'licenseNumber', secretOptions) + field('Issuing state / region', r.issuingState, 'issuingState') + field('Issuing country', r.issuingCountry, 'issuingCountry') + field('Issue date', r.issueDate, 'issueDate', secretOptions) + field('Expiry date', r.expiryDate, 'expiryDate', secretOptions));
    body += disclose('More license details', field('Date of birth', r.birthDate, 'birthDate', secretOptions) + field('Issuing authority', r.authority, 'authority') + field('License class', r.licenseClass, 'licenseClass'));
  } else {
    body = section('Passport details', field('Full name', fullName(r), 'fullName') + field('Passport number', r.passportNumber, 'passportNumber', secretOptions) + field('Issuing country', r.issuingCountry, 'issuingCountry') + field('Nationality', r.nationality, 'nationality') + field('Issue date', r.issueDate, 'issueDate', secretOptions) + field('Expiry date', r.expiryDate, 'expiryDate', secretOptions));
    body += disclose('More passport details', field('Date of birth', r.birthDate, 'birthDate', secretOptions) + field('Sex', r.sex, 'sex', secretOptions) + field('Birthplace', r.birthPlace, 'birthPlace', secretOptions) + field('Passport type', r.passportType, 'passportType') + field('National identification number', r.nationalId, 'nationalId', secretOptions) + field('Issuing authority', r.authority, 'authority'));
  }
  if (r.notes && r.type !== 'note') body += disclose('Notes', `<p class="v-note-content" data-v-bind="notes">${e(r.notes)}</p>`, 'Saved note');
  if (r.attachment) body += disclose('Attachments', `<div class="compact-row">${icon('note')}<div><strong>review-checklist.txt</strong><p>129 bytes · Encrypted attachment</p></div>${ib('download', 'Download review-checklist.txt', 'data-v-action="download"')}${r.canEdit ? ib('trash', 'Delete review-checklist.txt', 'data-v-action="attachment-delete"') : ''}</div>${r.canEdit ? action('Add attachment', 'attachment-add', 'small', 'add') : ''}`, '1 file');
  const customs = r.custom || [];
  if (customs.length) body += disclose('Custom fields', customs.map((f, index) => field(f.name, f.value, `custom${index}`, { secret: f.type !== 'Text' && f.type !== 'Boolean' })).join(''), `${customs.length} field${customs.length === 1 ? '' : 's'}`);
  if (r.history) body += disclose('Password history', field('3 Sep 2026', 'Synthetic!Previous#2026', '', { secret: true, blocked: protectedItem, blockedText: 'Verify to view previous passwords' }), '1 previous');
  const state = s.variant === 'sync-error' ? `<span class="v-offline">${icon('cloud')} Offline · Last synced today, 10:42</span>` : preview ? pill('Local preview', 'warning') : `<span class="sync-status">${icon('check')} Synced today, 10:42</span>`;
  const menu = `<details class="v-menu v-item-menu"><summary aria-label="More item actions" title="More item actions">${icon('more')}</summary><div class="v-menu-panel">${action('Move to folder', 'move-folder', '', 'folder')}${r.owner === 'Personal vault' ? action('Transfer to organization', 'transfer', '', 'person') : action('Manage collections', 'collections', '', 'folder')}${action('Move to trash', 'soft-delete', 'danger-quiet', 'trash')}</div></details>`;
  const toolbar = r.deleted ? action('Restore', 'restore', '', 'back') + action('Delete permanently', 'permanent-delete', 'danger-quiet', 'trash') : r.archived ? action('Unarchive', 'unarchive', '', 'archive') : `<button class="icon-button ${r.favorite ? 'v-favorite-on' : ''}" aria-pressed="${r.favorite}" aria-label="${r.favorite ? 'Remove from' : 'Add to'} favorites" title="${r.favorite ? 'Remove from' : 'Add to'} favorites" data-v-action="favorite">${icon('star')}</button>${r.canEdit ? action('Edit', 'edit') + menu : `<span class="pill">Read-only</span>${action('Edit', 'edit', '', '', 'disabled')}`}`;
  return `<section class="detail-pane v-detail" data-v-record="${e(r.id)}" data-v-type="${e(r.type)}" data-v-protected="${protectedItem}" data-v-model="${e(JSON.stringify(r))}"><div class="detail-top"><span class="breadcrumb"><span data-v-bind="owner">${e(r.owner)}</span><span>/</span><span data-v-bind="folder">${e(r.collection || r.folder || 'No folder')}</span></span>${state}</div><div class="detail-heading"><span class="detail-avatar ${r.type}">${icon(icons[r.type])}</span><div><h2 data-v-title>${e(r.name)}</h2><p>${e(types[r.type])}</p></div><div class="detail-toolbar">${toolbar}</div></div>${r.deleted ? note('This item is in Trash', `Deleted ${r.deleteDate}. Restore it, or permanently delete it.`, 'warning') : r.archived ? note('Archived item', `Archived ${r.archiveDate}. Unarchive to include it in your active vault.`) : !r.canEdit ? note('Shared with limited permissions', 'Your organization permits viewing this item. Passwords are hidden and editing is unavailable.') : ''}${body}<div class="detail-end"><span data-v-timeline>Created ${e(r.created)} · Edited ${e(r.edited)}</span>${action('Item information', 'information', '', 'info')}</div></section>`;
}

function formFields(r, ui, creating = false) {
  const { input, dropdown, disclose, action } = makeUi(ui);
  const v = key => creating ? '' : r[key] || '';
  const field = (name, key, options = {}) => input(name, key, v(key), options);
  const secret = (name, key, options = {}) => field(name, key, { ...options, secret: true });
  const date = (name, key) => secret(name, key, { help: 'Format: YYYY-MM-DD' });
  const cols = body => `<div class="form-columns v-two-columns">${body}</div>`;
  if (r.type === 'login') return field('Username', 'username') + `<div class="v-password-label">${secret('Password', 'password')}${action('Generate', 'generate', 'small', 'generator')}</div><div class="v-generator-inline" hidden><span class="field-label">Generated in this preview</span><strong class="mono" data-v-generated>R7!mQ2#zP9@vK4$wX6</strong><div>${action('Use password', 'use-password', 'primary small')}${action('Regenerate', 'regenerate-password', 'small')}${action('Cancel', 'cancel-generator', 'small')}</div></div>` + secret('Verification key (TOTP)', 'totp', { help: 'The setup key used to generate one-time verification codes.' }) + disclose('Websites', `<div data-v-uri-list>${field('Website', 'website')}${dropdown('Match detection', 'match', [['', 'Default'], ['0', 'Base domain'], ['1', 'Host'], ['2', 'Starts with'], ['3', 'Exact'], ['4', 'Regular expression'], ['5', 'Never']], '')}${!creating && r.website2 ? field('Additional website', 'website2') + dropdown('Match detection', 'match2', [['', 'Default'], ['0', 'Base domain'], ['1', 'Host'], ['2', 'Starts with'], ['3', 'Exact'], ['4', 'Regular expression'], ['5', 'Never']], '') : ''}</div>${action('Add website', 'add-uri', 'small', 'add')}`, creating ? 'Optional' : r.website2 ? '2 websites' : '1 website', true);
  if (r.type === 'card') return field('Cardholder', 'cardholder') + secret('Card number', 'number') + cols(field('Expiry month', 'month', { help: '01–12' }) + field('Expiry year', 'year', { help: 'Four-digit year' })) + secret('Security code', 'code') + field('Brand', 'brand');
  if (r.type === 'identity') return cols(field('First name', 'firstName') + field('Last name', 'lastName')) + field('Email', 'email', { type: 'email' }) + field('Phone', 'phone') + field('Address line 1', 'address1') + disclose('More identity fields', field('Title', 'title') + field('Middle name', 'middleName') + field('Username', 'username') + field('Company', 'company') + secret('Identification number', 'ssn') + secret('Passport number', 'passportNumber') + secret('License number', 'licenseNumber') + field('Address line 2', 'address2') + field('Address line 3', 'address3') + cols(field('City', 'city') + field('State / region', 'state')) + cols(field('Postal code', 'postalCode') + field('Country', 'country')), 'Name · Identification · Address');
  if (r.type === 'note') return field('Content', 'notes', { multiline: true, rows: 9 });
  if (r.type === 'ssh') return secret('Private key', 'privateKey', { required: true, multiline: true, help: creating ? 'Paste the complete private key, including its header and footer.' : 'The existing private key is retained. Reveal only to inspect or replace it.' }) + field('Public key', 'publicKey', { required: true, multiline: true, mono: true }) + field('Fingerprint', 'fingerprint', { required: true, mono: true });
  if (r.type === 'bank') return field('Bank', 'bank') + field('Account holder', 'holder') + field('Account type', 'accountType') + secret('Account number', 'accountNumber') + secret('Routing number', 'routingNumber') + secret('IBAN', 'iban') + disclose('Additional bank fields', secret('Branch number', 'branchNumber') + secret('PIN', 'pin') + secret('SWIFT / BIC', 'swift') + field('Bank contact phone', 'bankPhone'));
  if (r.type === 'license') return cols(field('First name', 'firstName') + field('Last name', 'lastName')) + field('Middle name', 'middleName') + secret('License number', 'licenseNumber') + cols(field('Issuing country', 'issuingCountry') + field('Issuing state / region', 'issuingState')) + disclose('Personal and issuing details', date('Date of birth', 'birthDate') + date('Issue date', 'issueDate') + date('Expiry date', 'expiryDate') + field('Issuing authority', 'authority') + field('License class', 'licenseClass'), 'Birth date · Dates · Authority');
  return cols(field('Surname', 'surname') + field('Given names', 'givenName')) + secret('Passport number', 'passportNumber') + cols(field('Nationality', 'nationality') + field('Issuing country', 'issuingCountry')) + disclose('Personal and issuing details', date('Date of birth', 'birthDate') + secret('Sex', 'sex') + secret('Birthplace', 'birthPlace') + field('Passport type', 'passportType') + secret('National identification number', 'nationalId') + field('Issuing authority', 'authority') + date('Issue date', 'issueDate') + date('Expiry date', 'expiryDate'), 'Birth details · Identification · Dates');
}

function editorMarkup(r, s, ui, creating = false) {
  const { e, icon, section, input, dropdown, disclose, action, pill, note } = makeUi(ui);
  const pending = s.variant === 'pending-save';
  const typeChoices = Object.entries(types);
  const customFields = (creating ? [] : r.custom || []).map((f, index) => `<div class="v-custom-editor">${input('Field name', `customName${index}`, f.name)}${dropdown('Field type', `customType${index}`, [['Text', 'Text'], ['Hidden', 'Hidden'], ['Boolean', 'Boolean'], ...(f.type === 'Linked' ? [['Linked', 'Linked (preserved)']] : [])], f.type, 'data-v-custom-type')}${f.type === 'Boolean' ? `<label class="check-line"><input type="checkbox" name="customValue${index}" ${f.value === 'true' ? 'checked' : ''}> Value</label>` : input('Value', `customValue${index}`, f.value, { secret: f.type === 'Hidden', attrs: f.type === 'Linked' ? 'readonly aria-readonly="true"' : '' })}</div>`).join('');
  const owner = creating ? dropdown('Save to', 'owner', [['Personal vault', 'Personal vault'], ['Contoso Review', 'Contoso Review']], 'Personal vault', 'data-v-owner') + `<div class="v-collections" hidden><span class="field-label">Collections (choose at least one)</span><label class="check-line"><input name="collection" type="checkbox" value="Engineering"> Engineering</label><small>Only collections you can write to are listed.</small></div>` : `<div class="v-owner-readonly"><span class="field-label">Owner</span><strong>${e(r.owner)}</strong>${r.collection ? `<small>${e(r.collection)}</small>` : ''}<small>Ownership transfer is a separate action.</small></div>`;
  const name = pending ? 'New service login' : creating ? '' : r.name;
  return `<form class="editor-pane v-editor-form" data-v-record="${e(r.id)}" data-v-type="${e(r.type)}" data-v-new="${creating}" data-v-pending="${pending}" data-dirty="false" novalidate><div class="editor-heading"><div><span class="eyebrow">${creating ? 'NEW ITEM' : e(r.owner + ' / ' + (r.folder || r.collection || 'No folder'))}</span><h2 data-v-editor-title>${creating ? 'New ' : 'Edit '}${e(types[r.type] === 'SSH key' ? 'SSH key' : types[r.type].toLowerCase())}</h2></div><span class="v-dirty-label" aria-live="polite">${pending ? 'Waiting for confirmation' : creating ? 'Blank draft' : 'No unsaved changes'}</span></div><div class="editor-scroll"><div class="v-form-error" role="alert" hidden></div>${pending ? note('Save result not confirmed', 'The connection ended before the server confirmed creation. Your draft is retained. Check sync status before creating another item.', 'warning') : ''}<fieldset ${pending ? 'disabled' : ''}>${creating ? dropdown('Item type', 'type', typeChoices, r.type, 'data-v-create-type') : ''}${input('Name', 'name', name, { required: true })}<section class="section"><div class="section-title"><h3 data-v-form-title>${e(types[r.type])} information</h3></div><div class="form-section" data-v-type-fields>${formFields(r, ui, creating && !pending)}</div></section>${disclose(creating ? 'Save destination' : 'Folder and ownership', owner + dropdown('Folder', 'folder', [['', 'No folder'], ['Work', 'Work'], ['Personal', 'Personal'], ['Finance', 'Finance']], creating ? '' : r.folder || ''), '', true)}${disclose('Additional options', `<label class="check-line"><input name="favorite" type="checkbox" ${!creating && r.favorite ? 'checked' : ''}> Favorite</label><div data-v-notes-field ${r.type === 'note' ? 'hidden' : ''}>${input('Notes', 'notesExtra', creating ? '' : r.notes, { multiline: true, rows: 3 })}</div><label class="check-line"><input name="reprompt" type="checkbox"> Ask for master password before sensitive actions</label>`, r.type === 'note' ? 'Favorite · Reprompt' : 'Favorite · Notes · Reprompt')}${disclose('Custom fields', `<div data-v-custom-list>${customFields}</div>${action('Add field', 'add-custom', 'small', 'add')}`, creating ? 'Optional' : `${r.custom?.length || 0} fields`)}</fieldset></div><footer class="editor-footer"><span>${icon('lock')} ${pending ? 'Draft retained on this device' : 'Encrypted on your device'}</span><div>${pending ? action('Check sync status', 'check-pending', 'primary', 'cloud') : `<button type="submit" class="button primary" data-v-action="save" disabled>${creating ? 'Create item' : 'Save changes'}</button>`}${action(pending ? 'Keep draft' : 'Cancel', pending ? 'keep-pending' : 'cancel-edit')}</div></footer></form>`;
}

function listMarkup(s, ui, r) {
  const { e, icon, ib, action, note } = makeUi(ui);
  const empty = s.variant === 'empty', loading = s.variant === 'loading', batch = s.variant === 'batch';
  const title = s.dialog === 'permanent-delete' || s.variant === 'trash' ? 'Trash' : s.variant === 'archive' ? 'Archive' : s.variant === 'favorites' ? 'Favorites' : s.variant === 'folder' ? s.folder || 'Work' : s.variant === 'organization' ? 'Engineering' : 'All items';
  const unselected = ['no-selection', 'empty', 'no-results', 'loading', 'favorites', 'folder', 'organization', 'new', 'pending-save', 'batch'].includes(s.variant);
  const rows = scopeRows(s), count = s.variant === 'no-results' ? 0 : rows.length;
  const row = x => `<div class="v-row-wrap" data-v-row="${e(x.id)}" data-v-kind="${e(x.type)}" data-v-name="${e(x.name)}" data-v-folder="${e(x.folder || x.collection || '')}" data-v-owner="${e(x.owner)}" data-v-favorite="${x.favorite}">${batch ? `<input class="v-row-check" aria-label="Select ${e(x.name)}" type="checkbox" ${['contoso', 'github', 'google'].includes(x.id) ? 'checked' : ''}>` : ''}<button class="vault-list-row ${!unselected && x.id === r.id ? 'selected' : ''}" data-v-action="${batch ? 'select-row' : 'row'}" data-v-target="${e(route(x))}" aria-current="${!unselected && x.id === r.id ? 'true' : 'false'}"><span class="item-avatar ${x.type}">${icon(icons[x.type])}</span><span class="item-copy"><strong>${e(x.name)}</strong><small>${e(subtitle(x))}</small></span>${x.owner !== 'Personal vault' ? icon('person', 'v-org-mark') : x.favorite ? icon('star', 'favorite') : ''}<span class="v-search-extra" hidden>${e(x.website || '')}</span></button></div>`;
  const loadingRows = Array.from({ length: 7 }, (_, index) => `<div class="skeleton-row"><div class="skeleton square"></div><div><div class="skeleton line" style="width:${165 - index % 3 * 20}px"></div><div class="skeleton line short"></div></div></div>`).join('');
  const sortMenu = `<details class="v-menu"><summary aria-label="Sort and filter" title="Sort and filter">${icon('more')}</summary><div class="v-menu-panel"><label class="form-field"><span>Sort by</span><select data-v-sort><option value="default">Default order</option><option value="name">Name A–Z</option><option value="name-desc">Name Z–A</option></select></label><label class="check-line"><input type="checkbox" data-v-favorite-filter> Favorites only</label>${action('Open Favorites', 'navigate', '', 'star', 'data-v-target="vault-favorites"')}${action('Open Work folder', 'navigate', '', 'folder', 'data-v-target="vault-folder-work"')}</div></details>`;
  return `<section class="vault-list v-list" data-v-initial-state="${e(s.variant)}"><div class="list-heading"><h1>${e(title)}</h1>${action('Add', 'navigate', unselected?'primary small':'small', 'add', `data-v-target="vault-new-login" ${loading ? 'disabled' : ''}`)}</div>${s.variant === 'organization' ? '<p class="v-list-context">Contoso Review · Shared collection</p>' : ''}<div class="search"><span>${icon('search')}</span><input aria-label="Search vault" placeholder="Search names, websites, usernames" ${s.variant === 'no-results' ? 'value="not-a-saved-item"' : ''} ${empty || loading ? 'disabled' : ''} data-v-search><kbd>Ctrl K</kbd></div><div class="list-filter"><select aria-label="Item type" data-v-filter ${empty || loading ? 'disabled' : ''}><option value="">All types</option>${Object.entries(types).map(([value, text]) => `<option value="${value}">${e(text)}</option>`).join('')}</select>${sortMenu}</div><div class="list-meta"><span data-v-list-count>${loading ? 'Loading items…' : batch ? '3 selected' : `${count} ${s.variant === 'no-results' ? 'results' : count === 1 ? 'item' : 'items'}`}</span>${action(batch ? 'Done' : 'Select', batch ? 'batch-done' : 'batch-start', 'v-text-action', '', `${empty || loading || s.variant === 'no-results' ? 'disabled' : ''}`)}</div>${s.variant === 'sync-error' ? `<div class="v-sync-banner">${note('Sync unavailable', 'Showing saved items from today, 10:42.', 'warning')}${action('Retry sync', 'retry-sync', 'small', 'cloud')}</div>` : ''}<div class="vault-rows" data-v-rows>${loading ? loadingRows : rows.map(row).join('')}<div class="v-list-empty" ${empty ? '' : 'hidden'} data-v-empty>${empty ? 'No items yet' : 'No matching items'}${empty ? '' : action('Clear search and filters', 'clear-search', 'small')}</div></div><div class="list-footer">${action(`Archive ${empty ? '0' : loading ? '—' : '1'}`, 'navigate', '', 'archive', `data-v-target="vault-archive" ${empty || loading ? 'disabled' : ''}`)}${action(`Trash ${empty ? '0' : loading ? '—' : '1'}`, 'navigate', '', 'trash', `data-v-target="vault-trash" ${empty || loading ? 'disabled' : ''}`)}</div></section>`;
}

function emptyMarkup(s, ui) {
  const { icon, action } = makeUi(ui);
  const v = s.variant;
  const title = v === 'empty' ? 'Add your first item' : v === 'loading' ? 'Opening your vault' : v === 'no-results' ? 'No matching items' : 'Choose an item';
  const desc = v === 'empty' ? 'Keep logins, cards, notes and keys together in your encrypted vault.' : v === 'loading' ? 'Loading your encrypted items. This should only take a moment.' : v === 'no-results' ? 'Try another name, website or username. Your saved items have not changed.' : v === 'organization' ? 'Choose an item shared with you in the Engineering collection.' : 'Select a login, card or key to see its details.';
  return `<section class="detail-pane empty-pane v-detail-empty"><div class="empty-emblem">${icon(v === 'loading' ? 'cloud' : v === 'no-results' ? 'search' : 'shield')}</div><h2>${title}</h2><p>${desc}</p>${v === 'empty' ? `<div class="v-empty-actions">${action('Add an item', 'navigate', 'primary', 'add', 'data-v-target="vault-new-login"')}${action('Import existing items', 'navigate', '', 'download', 'data-v-target="import-export-overview"')}</div>` : v === 'no-results' ? action('Clear search and filters', 'clear-search', '', 'search') : ''}</section>`;
}
function batchMarkup(ui) {
  const { e, icon, action, section } = makeUi(ui);
  return `<section class="detail-pane v-batch"><span class="eyebrow">SELECTION</span><h2 data-v-batch-title>3 items selected</h2><p class="v-batch-context" data-v-batch-context>Personal vault · 2 folders</p>${section('Selected items', `<div data-v-batch-items>${active.slice(0, 3).map(r => `<div class="compact-row">${icon('key')}<div><strong>${e(r.name)}</strong><p>${e(r.folder)}</p></div>${icon('check')}</div>`).join('')}</div>`)}<div class="action-grid">${action('Move to folder', 'batch-operation', '', 'folder', 'data-v-operation="Move to folder"')}${action('Remove from favorites', 'batch-favorite', '', 'star')}${action('Transfer to organization', 'batch-operation', '', 'person', 'data-v-operation="Transfer to organization"')}${action('Archive', 'batch-operation', '', 'archive', 'data-v-operation="Archive"')}</div><p class="caption v-batch-permission" data-v-batch-permission>All selected items are personal and can be edited.</p><div class="danger-separator">${action('Move to trash', 'batch-operation', 'danger-quiet', 'trash', 'data-v-operation="Move to trash"')}</div><div data-v-batch-flow></div></section>`;
}

export function renderVault(s, ui) {
  const r = chosen(s), isNew = s.variant === 'new', pending = s.variant === 'pending-save';
  const noSelection = ['no-selection', 'empty', 'no-results', 'loading', 'favorites', 'folder', 'organization'].includes(s.variant);
  const draft = pending ? { ...recordForType('login'), id: 'preview-new', name: 'New service login', username: 'alex.review@example.com', website: 'https://service.example.com', password: 'Synthetic!Pending#2026', website2: '', passkey: false, attachment: false, history: false, custom: [], notes: '' } : r;
  const pane = s.variant === 'batch' ? batchMarkup(ui) : s.variant === 'editor' || isNew || pending ? editorMarkup(draft, s, ui, isNew || pending) : noSelection ? emptyMarkup(s, ui) : detailMarkup(r, s, ui);
  const templates = `<template id="v-detail-template">${detailMarkup(r, { ...s, dialog: null }, ui)}</template><template id="v-editor-template">${editorMarkup(r, { ...s, variant: 'editor' }, ui)}</template>${isNew ? Object.keys(types).map(type => { const blank = Object.fromEntries(Object.entries(recordForType(type)).map(([key, value]) => [key, typeof value === 'string' ? '' : value])); const n = { ...blank, id: 'preview-new', type, name: '', owner: 'Personal vault', folder: '', favorite: false, notes: '', custom: [], passkey: false, history: false, attachment: false }; return `<template data-v-fields-template="${type}">${formFields(n, ui, true)}</template><template data-v-new-detail="${type}">${detailMarkup(n, { ...s, dialog: null }, ui, true)}</template>`; }).join('') : ''}`;
  return `<div class="v-surface" data-v-scene="${ui.e(s.id)}">${listMarkup(s, ui, r)}<div class="v-pane-host">${pane}</div>${templates}</div>`;
}

export function vaultDialog(s, ui) {
  const { e, icon, input, note } = makeUi(ui);
  if (s.dialog === 'new-folder') return { title: 'New folder', desc: 'Create a folder to organize items in your personal vault.', body: input('Folder name', 'folderName', '', { required: true, attrs: 'data-v-folder-name maxlength="100"' }), primary: 'Create folder', primaryAttrs: 'data-v-action="create-folder" disabled' };
  if (s.dialog === 'reprompt') return { title: 'Verify your master password', desc: 'Verify access to the sensitive fields in this item.', body: `<div class="modal-item">${icon('key')}<div><strong>Contoso Cloud</strong><span>Personal vault / Work</span></div></div>${input('Master password', 'vaultMasterPassword', '', { secret: true, required: true, attrs: 'data-v-master-password autocomplete="off"' })}<p class="v-reprompt-error" role="alert" hidden></p>`, primary: 'Verify and reveal', primaryAttrs: 'data-v-action="verify-reprompt" disabled' };
  if (s.dialog === 'permanent-delete') {
    const r = records.find(x => x.deleted);
    return { title: 'Permanently delete this item?', desc: 'This action cannot be undone.', body: `<div class="modal-item">${icon('key')}<div><strong>${e(r.name)}</strong><span>Login · Personal vault / Work · In Trash</span></div></div>${note('The item will be removed permanently', 'Old VPN and its saved fields will be deleted. Other items are not affected.', 'danger')}`, primary: 'Delete permanently', tone: 'danger', primaryAttrs: 'data-v-action="confirm-permanent-delete"' };
  }
  return null;
}

export const vaultCss = `
.v-detail .v-field .totp{font-size:26px;letter-spacing:3px;line-height:34px}.v-detail .v-field .value:not([data-v-secret]){font-weight:400}
.v-secret-input>.v-secret-textarea{border:0;resize:vertical;min-height:104px;flex:1;background:transparent;-webkit-text-security:disc;padding:9px 10px}.v-secret-input>.v-secret-textarea.v-unmasked{-webkit-text-security:none}.v-secret-input:has(.v-secret-textarea)>.icon-button{align-self:flex-start;margin-top:5px}
.v-surface{display:flex;width:100%;height:100%;min-width:0;min-height:0}.v-pane-host{display:flex;flex:1;min-width:0;min-height:0}.v-pane-host>.detail-pane,.v-pane-host>.editor-pane{width:100%;min-width:0}.v-list{padding-top:23px}.v-list .list-heading{gap:8px;margin-bottom:17px}.v-list .list-heading h1{font-size:23px}.v-list .list-heading .button{padding:0 10px}.v-list .list-filter{align-items:center}.v-list .v-list-context{font-size:12px;color:var(--sub);margin:-9px 4px 14px}.v-list .search input{font-size:11px}.v-text-action{border:0!important;background:transparent!important;color:var(--accent-text);height:26px!important;padding:0 2px!important;font-size:12px;box-shadow:none!important}.v-list .list-footer .button{font-size:12px;padding:0;border:0;background:none;height:35px;box-shadow:none}.v-row-wrap{position:relative;display:flex;align-items:center;min-width:0}.v-row-wrap>.vault-list-row{flex:1;min-width:0}.v-row-check{margin:0 8px 0 7px;accent-color:var(--accent);width:17px;height:17px;flex:0 0 17px}.v-row-wrap:has(.v-row-check:checked){background:var(--tint);border-radius:7px}.v-list-empty{text-align:center;padding:66px 16px;font-size:13px;line-height:24px;color:var(--sub)}.v-list-empty .button{margin-top:15px;font-size:11px}.v-sync-banner{padding-bottom:14px}.v-sync-banner>.notice{margin:0 0 8px}.v-sync-banner .button{width:100%;height:30px;font-size:12px}.v-menu{position:relative;flex:0 0 auto}.v-menu>summary{list-style:none;display:grid;place-items:center;cursor:pointer;width:32px;height:32px;color:var(--sub);border-radius:5px}.v-menu>summary::-webkit-details-marker{display:none}.v-menu>summary:hover{background:var(--tint)}.v-menu-panel{position:absolute;right:0;top:38px;width:230px;padding:12px;background:var(--surface);border:1px solid var(--line-strong);border-radius:9px;box-shadow:var(--shadow);z-index:20;display:grid;gap:6px}.v-menu-panel>.button{justify-content:flex-start;border:0;box-shadow:none;font-size:12px;padding:0 8px}.v-menu-panel .form-field{margin:0 0 8px}.v-menu-panel .check-line{font-size:12px;margin:6px 0}.v-item-menu .v-menu-panel{width:235px}.v-org-mark{color:var(--sub);width:16px;flex:0 0 16px}.v-detail>.detail-top{align-items:flex-start;gap:12px}.v-detail .breadcrumb{flex-wrap:wrap;line-height:18px;max-width:60%;display:flex;gap:8px}.v-detail .sync-status,.v-offline{font-size:11px;text-align:right;display:flex;align-items:center;gap:5px;line-height:18px}.v-offline{color:var(--warning)}.v-detail .detail-toolbar{gap:5px}.v-detail .detail-heading{gap:12px}.v-detail .detail-heading h2{font-size:24px;line-height:31px}.v-detail .detail-heading>div:nth-child(2){min-width:0}.v-detail .detail-heading h2{overflow-wrap:anywhere}.v-favorite-on{color:var(--accent)!important;background:var(--tint)}.v-favorite-on .icon svg{fill:currentColor}.v-card-summary{margin:0 0 18px;padding:18px 20px;background:var(--surface-alt);border:1px solid var(--line);border-radius:9px;display:flex;justify-content:space-between;align-items:center;gap:18px}.v-card-summary>div{display:flex;gap:12px;align-items:center}.v-card-summary .mono{margin-left:12px;letter-spacing:2px}.v-field .value{white-space:pre-wrap;overflow-wrap:anywhere;line-height:21px}.v-field .mono{font-size:13px}.v-disclosure{margin-bottom:13px}.v-disclosure-body{padding:0 20px 17px}.v-disclosure-body>.field-row:first-child{border-top:0}.v-disclosure-body .field-row{padding-left:0;padding-right:0}.v-disclosure-body>.compact-row{padding-left:0;padding-right:0}.v-disclosure-body>.compact-row+.button{margin-top:8px}.v-disclosure summary{font-size:13px}.v-passkey{display:flex;align-items:flex-start;gap:13px;padding-top:8px}.v-passkey p{font-size:13px;margin:5px 0}.v-passkey small{display:block;line-height:20px}.v-note-content{white-space:pre-wrap;font-size:14px;line-height:24px;padding:5px 20px 22px;overflow-wrap:anywhere}.v-disclosure-body .v-note-content{padding:5px 0 0}.v-detail .detail-end{font-size:11px;gap:12px;align-items:center}.v-detail .detail-end .button{font-size:11px;height:28px;border:0;box-shadow:none;padding:0 4px}.v-detail-empty{padding:30px}.v-detail-empty>p{max-width:360px}.v-empty-actions{display:flex;gap:10px;flex-wrap:wrap;justify-content:center;margin-top:24px}.v-editor-form{margin:0}.v-editor-form fieldset{border:0;padding:0;margin:0;min-width:0}.v-editor-form .editor-heading{min-height:101px;gap:18px;align-items:center}.v-editor-form .editor-heading h2{font-size:23px}.v-dirty-label{font-size:11px;color:var(--sub);line-height:17px;text-align:right;max-width:115px}.v-editor-form[data-dirty=true] .v-dirty-label{color:var(--warning)}.v-editor-form .editor-scroll{padding-top:22px;padding-bottom:22px}.v-editor-form .form-section{padding:4px 20px 8px}.v-editor-form .v-disclosure-body{padding-top:15px}.v-editor-form .v-two-columns{grid-template-columns:1fr 1fr}.v-editor-form .form-field{margin-bottom:16px}.v-editor-form .form-field>span,.v-editor-form .field-label{font-size:12px}.v-editor-form .form-field small{font-size:11px;line-height:17px}.v-editor-form .form-field input{height:37px;min-height:37px}.v-editor-form textarea{font-size:13px;line-height:21px;resize:vertical;min-height:80px}.v-required{font-size:11px;color:var(--sub);font-weight:400}.v-secret-input{display:flex;align-items:stretch;border:1px solid var(--line-strong);border-bottom-color:var(--sub);border-radius:5px;background:var(--surface)}.v-secret-input>input[type=password],.v-secret-input>input[type=text]{border:0;border-radius:0;background:transparent;width:100%;flex:1;min-width:0}.v-secret-input:focus-within{outline:2px solid var(--accent);outline-offset:2px}.v-secret-input>input:focus-visible{outline:0}.v-secret-input>.icon-button{align-self:center;margin-right:3px;height:30px;width:30px;flex-basis:30px}.v-owner-readonly{display:grid;gap:5px;margin-bottom:17px;border-left:2px solid var(--line-strong);padding-left:12px}.v-owner-readonly strong{font-size:13px}.v-owner-readonly small{font-size:11px}.v-collections{padding:12px 13px;background:var(--surface-alt);border:1px solid var(--line);border-radius:6px;margin:-4px 0 16px}.v-collections>small{font-size:11px;display:block;margin-top:6px}.v-editor-form .check-line{font-size:12px;margin-bottom:15px;align-items:center;line-height:20px}.v-editor-form .check-line input{accent-color:var(--accent);flex:0 0 16px;width:16px;height:16px}.v-password-label{position:relative}.v-password-label>.button{position:absolute;right:0;top:-7px;height:25px;font-size:11px}.v-password-label .form-field{padding-top:0}.v-generator-inline{padding:15px;border:1px solid var(--line-strong);background:var(--surface-alt);border-radius:7px;margin-bottom:18px}.v-generator-inline>strong{display:block;overflow-wrap:anywhere;font-size:17px;margin:10px 0 14px}.v-generator-inline>div{display:flex;gap:6px;flex-wrap:wrap}.v-generator-inline .button{font-size:11px}.v-custom-editor{padding:13px 0 3px;border-bottom:1px solid var(--line);margin-bottom:15px}.v-custom-editor:first-child{padding-top:0}.v-form-error,.v-reprompt-error{padding:12px;background:var(--danger-bg);color:var(--danger);font-size:12px;border-radius:6px;margin-bottom:15px}.v-editor-form .editor-footer{flex-shrink:0}.v-batch h2{font-size:25px;margin-top:9px}.v-batch-context{color:var(--sub);margin:8px 0 26px;font-size:13px}.v-batch .compact-row p{font-size:12px}.v-batch-permission{margin-top:16px}.v-batch .action-grid .button{font-size:12px}.v-batch-flow{border:1px solid var(--line-strong);border-radius:8px;background:var(--surface-alt);padding:18px;margin-top:18px}.v-batch-flow h3{font-size:15px;margin-bottom:13px}.v-batch-flow .button{margin-top:10px}.v-local-dialog{color:var(--text);background:var(--surface);border:1px solid var(--line-strong);border-radius:12px;width:460px;max-width:90vw;padding:25px;box-shadow:var(--dialog-shadow)}.v-local-dialog::backdrop{background:#10203966}.v-local-dialog h2{font-size:22px;line-height:30px;margin-bottom:12px}.v-local-dialog p{font-size:13px;color:var(--sub);line-height:21px;white-space:pre-wrap;margin:10px 0 20px}.v-local-dialog .v-dialog-actions{display:flex;gap:8px;justify-content:flex-end}.v-local-dialog .form-field{margin:15px 0}.v-editor-form fieldset:disabled{opacity:.72}.v-editor-form fieldset:disabled .icon-button{pointer-events:none}
`;

export const vaultScript = String.raw`
(function () {
  const surface = document.querySelector('.v-surface');
  if (!surface) return;
  const host = surface.querySelector('.v-pane-host');
  const list = surface.querySelector('.v-list');
  const typeNames = { login: 'Login', card: 'Payment card', identity: 'Identity', note: 'Secure note', ssh: 'SSH key', bank: 'Bank account', license: 'Driver’s license', passport: 'Passport' };
  let savedDetail = surface.querySelector('#v-detail-template')?.innerHTML || '';
  let pendingReveal = null;
  const esc = value => String(value ?? '').replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
  const getForm = () => host.querySelector('.v-editor-form');
  const getDetail = () => host.querySelector('.v-detail');
  const consume = event => { event.preventDefault(); event.stopImmediatePropagation(); };
  const localToast = text => toast(text + ' · Preview only');
  const paneEmpty = (title, body) => '<section class="detail-pane empty-pane v-detail-empty"><h2>' + esc(title) + '</h2><p>' + esc(body) + '</p></section>';
  function localDialog(title, message, primary, onAccept, options = {}) {
    const dialog = document.createElement('dialog');
    dialog.className = 'v-local-dialog';
    dialog.innerHTML = '<h2>' + esc(title) + '</h2><p>' + esc(message) + '</p>' + (options.fields || '') + '<div class="v-dialog-actions"><button type="button" class="button ' + (options.danger ? 'danger' : 'primary') + '" data-v-dialog-accept>' + esc(primary) + '</button><button type="button" class="button" data-v-dialog-cancel>Cancel</button></div>';
    document.body.append(dialog);
    const accept = dialog.querySelector('[data-v-dialog-accept]');
    const valid = () => !options.required || [...dialog.querySelectorAll('[required]')].every(input => input.value.trim());
    accept.disabled = !valid();
    dialog.addEventListener('input', () => { accept.disabled = !valid(); });
    dialog.querySelector('[data-v-dialog-cancel]').addEventListener('click', event => { consume(event); dialog.close(); });
    accept.addEventListener('click', event => { consume(event); if (!valid()) return; onAccept(dialog); dialog.close(); });
    dialog.addEventListener('close', () => dialog.remove());
    dialog.showModal();
    (options.required ? dialog.querySelector('[required]') : dialog.querySelector('[data-v-dialog-cancel]'))?.focus();
  }
  function guardNavigation(callback) {
    const form = getForm();
    if (form?.dataset.vPending === 'true') {
      localDialog('Keep this draft until the result is known', 'The last create request may have reached your server. Check sync status before leaving this draft or creating another item.', 'Return to draft', () => form.querySelector('[data-v-action="check-pending"]')?.focus());
      return;
    }
    if (form?.dataset.dirty === 'true') localDialog('Discard unsaved changes?', 'Your saved item will stay unchanged. The edits in this preview will be discarded.', 'Discard changes', callback);
    else callback();
  }
  function navigateSafely(target) { guardNavigation(() => navigate(target)); }
  function requiredValid(form) {
    const fields = [...form.querySelectorAll('[required]')].filter(input => !input.disabled);
    if (!fields.every(input => input.value.trim())) return false;
    if (form.querySelector('[data-v-owner]')?.value === 'Contoso Review' && !form.querySelector('input[name="collection"]:checked')) return false;
    return [...form.querySelectorAll('input[type="email"]')].every(input => !input.value || input.validity.valid);
  }
  function refineMatching(form) {
    if(!form)return;
    form.querySelectorAll('select[name^="match"]').forEach(select=>{
      const label=select.closest('.form-field');
      let details=select.closest('.v-match-details');
      if(!details){details=document.createElement('details');details.className='disclosure v-disclosure v-match-details';details.innerHTML='<summary><strong>Website matching</strong><span data-v-match-summary></span></summary><div class="v-disclosure-body"></div>';label.before(details);details.lastElementChild.append(label);}
      details.querySelector('[data-v-match-summary]').textContent=select.selectedOptions[0]?.textContent||'Default';
    });
  }
  function refreshForm(form, dirty = true) {
    refineMatching(form);
    if (!form || form.dataset.vPending === 'true') return;
    if (dirty) form.dataset.dirty = 'true';
    form.querySelector('.v-dirty-label').textContent = form.dataset.dirty === 'true' ? 'Unsaved changes' : form.dataset.vNew === 'true' ? 'Blank draft' : 'No unsaved changes';
    const save = form.querySelector('[data-v-action="save"]');
    if (save) save.disabled = !requiredValid(form) || form.dataset.dirty !== 'true';
    const collection = form.querySelector('.v-collections');
    if (collection) collection.hidden = form.querySelector('[data-v-owner]')?.value !== 'Contoso Review';
  }
  function initializeForm() {
    const form = getForm();
    if (!form) return;
    form.addEventListener('submit', event => { consume(event); saveForm(form); });
    refreshForm(form, false);
  }
  function collectForm(form) {
    const creating = form.dataset.vNew === 'true';
    const source = surface.querySelector('#v-detail-template')?.content.querySelector('.v-detail');
    let data = creating ? {} : JSON.parse(source?.dataset.vModel || '{}');
    for (const input of form.querySelectorAll('[data-v-key]')) data[input.dataset.vKey] = input.value;
    data.id = creating ? 'preview-new' : form.dataset.vRecord;
    data.type = form.dataset.vType;
    data.favorite = !!form.querySelector('[name="favorite"]:checked');
    data.reprompt = !!form.querySelector('[name="reprompt"]:checked');
    data.owner = creating ? form.querySelector('[name="owner"]').value : data.owner;
    data.collection = creating && data.owner === 'Contoso Review' ? 'Engineering' : data.collection;
    data.notes = data.type === 'note' ? form.querySelector('[name="notes"]')?.value || '' : form.querySelector('[name="notesExtra"]')?.value || '';
    data.fullName = data.type === 'passport' ? [data.givenName, data.surname].filter(Boolean).join(' ') : [data.firstName, data.middleName, data.lastName].filter(Boolean).join(' ');
    data.fullAddress = [data.address1, data.address2, data.address3, [data.city, data.state, data.postalCode].filter(Boolean).join(', '), data.country].filter(Boolean).join('\n');
    data.expiry = [data.month || '—', data.year || '—'].join(' / ');
    data.custom = [...form.querySelectorAll('.v-custom-editor')].map(row => ({ name: row.querySelector('[name^="customName"]')?.value || '', type: row.querySelector('[name^="customType"]')?.value || 'Text', value: row.querySelector('[name^="customValue"]')?.type === 'checkbox' ? String(row.querySelector('[name^="customValue"]').checked) : row.querySelector('[name^="customValue"]')?.value || '' })).filter(field => field.name.trim());
    return data;
  }
  function saveForm(form) {
    if (!requiredValid(form)) {
      const error = form.querySelector('.v-form-error');
      error.hidden = false;
      error.textContent = form.querySelector('[data-v-owner]')?.value === 'Contoso Review' && !form.querySelector('[name="collection"]:checked') ? 'Choose at least one collection for this organization item.' : 'Complete the required fields before saving.';
      form.querySelector('[required]:invalid')?.focus();
      return;
    }
    if (form.dataset.dirty !== 'true') return;
    const data = collectForm(form);
    const creating = form.dataset.vNew === 'true';
    const editClone = form.cloneNode(true);
    const sourceInputs = [...form.querySelectorAll('input,select,textarea')];
    [...editClone.querySelectorAll('input,select,textarea')].forEach((copy, index) => { const original = sourceInputs[index]; if (copy.tagName === 'TEXTAREA') copy.textContent = original.value; else if (copy.tagName === 'SELECT') [...copy.options].forEach(option => { option.selected = option.value === original.value; option.toggleAttribute('selected', option.selected); }); else { copy.setAttribute('value', original.value); copy.toggleAttribute('checked', original.checked); } });
    editClone.dataset.dirty = 'false'; editClone.dataset.vNew = 'false'; editClone.dataset.vRecord = data.id;
    editClone.querySelector('[data-v-create-type]')?.closest('.form-field').remove();
    editClone.querySelector('.v-dirty-label').textContent = 'No unsaved changes';
    editClone.querySelector('[data-v-editor-title]').textContent = 'Edit ' + (data.type === 'ssh' ? 'SSH key' : typeNames[data.type].toLowerCase());
    editClone.querySelector('.eyebrow').textContent = data.owner + ' / ' + (data.collection || data.folder || 'No folder');
    const editableOwner = editClone.querySelector('[data-v-owner]');
    if (editableOwner) { const owner = document.createElement('div'); owner.className = 'v-owner-readonly'; owner.innerHTML = '<span class="field-label">Owner</span><strong>' + esc(data.owner) + '</strong><small>Ownership transfer is a separate action.</small>'; editableOwner.closest('.form-field').replaceWith(owner); editClone.querySelector('.v-collections')?.remove(); }
    editClone.querySelector('[data-v-action="save"]').textContent = 'Save changes'; editClone.querySelector('[data-v-action="save"]').disabled = true;
    surface.querySelector('#v-editor-template').innerHTML = editClone.outerHTML;
    const template = creating ? surface.querySelector('[data-v-new-detail="' + data.type + '"]') : surface.querySelector('#v-detail-template');
    if (!template) return;
    host.innerHTML = template.innerHTML;
    const detail = getDetail();
    detail.dataset.vModel = JSON.stringify(data);
    detail.dataset.vRecord = data.id;
    detail.querySelector('[data-v-title]').textContent = data.name;
    for (const span of detail.querySelectorAll('[data-v-bind]')) {
      const key = span.dataset.vBind;
      const value = key === 'folder' ? data.collection || data.folder || 'No folder' : data[key];
      if (value === undefined) continue;
      if (span.hasAttribute('data-v-secret')) { span.dataset.vSecret = value; span.textContent = value ? '••••••••' : 'Not set'; }
      else span.textContent = value || '—';
      span.closest('.v-field')?.querySelectorAll('.field-actions button').forEach(button => { button.disabled = !value; });
    }
    const websites = [...detail.querySelectorAll('.section')].find(section => section.querySelector('h3')?.textContent === 'Websites');
    if (websites) for (const key of Object.keys(data).filter(key => /^website\d+$/.test(key) && data[key])) { if (!websites.querySelector('[data-v-bind="' + key + '"]')) { const row = document.createElement('div'); row.className = 'field-row v-field'; row.innerHTML = '<div class="field-value"><span class="field-label">Additional website</span><span class="value" data-v-bind="' + key + '">' + esc(data[key]) + '</span></div><div class="field-actions"><button type="button" class="button small" data-v-action="copy" data-v-label="Website">Copy</button></div>'; websites.append(row); } }
    const code = detail.querySelector('[data-v-totp]');
    if (code && !data.totp) code.closest('.field-row').hidden = true;
    const cardTail = detail.querySelector('.v-card-summary .mono');
    if (cardTail) cardTail.textContent = '•••• ' + String(data.number || '').replace(/\s/g, '').slice(-4);
    const notes = detail.querySelector('[data-v-bind="notes"]');
    if (!notes && data.notes) {
      const block = document.createElement('details');
      block.className = 'disclosure v-disclosure';
      block.innerHTML = '<summary><strong>Notes</strong></summary><div class="v-disclosure-body"><p class="v-note-content" data-v-bind="notes">' + esc(data.notes) + '</p></div>';
      detail.querySelector('.detail-end').before(block);
    }
    let customDetails = [...detail.querySelectorAll('details')].find(block => block.querySelector('summary strong')?.textContent === 'Custom fields');
    if (!customDetails && data.custom.length) { customDetails = document.createElement('details'); customDetails.className = 'disclosure v-disclosure'; customDetails.innerHTML = '<summary><strong>Custom fields</strong><span></span></summary><div class="v-disclosure-body"></div>'; detail.querySelector('.detail-end').before(customDetails); }
    if (customDetails && !data.custom.length) { customDetails.remove(); customDetails = null; }
    if (customDetails) {
      customDetails.querySelector('.v-disclosure-body').innerHTML = data.custom.length ? data.custom.map(field => '<div class="field-row v-field"><div class="field-value"><span class="field-label">' + esc(field.name) + '</span><span class="value" ' + (field.type === 'Hidden' || field.type === 'Linked' ? 'data-v-secret="' + esc(field.value) + '"' : '') + '>' + (field.type === 'Hidden' || field.type === 'Linked' ? '••••••••' : esc(field.value || '—')) + '</span></div></div>').join('') : '<p class="caption">No custom fields</p>';
      const count = customDetails.querySelector('summary>span:not(.icon)');
      if (count) count.textContent = data.custom.length + ' fields';
    }
    detail.querySelector('[data-v-timeline]').textContent = creating ? 'Created just now · Local preview' : 'Created 15 Aug 2026 · Edited just now · Local preview';
    const sync = detail.querySelector('.sync-status');
    if (sync) { sync.textContent = 'Local preview'; sync.className = 'pill warning'; }
    const favorite = detail.querySelector('[data-v-action="favorite"]');
    if (favorite) { favorite.classList.toggle('v-favorite-on', data.favorite); favorite.setAttribute('aria-pressed', String(data.favorite)); }
    savedDetail = host.innerHTML;
    surface.querySelector('#v-detail-template').innerHTML = savedDetail;
    const row = surface.querySelector('[data-v-row="' + data.id + '"] .item-copy strong');
    if (row) row.textContent = data.name;
    if (creating) {
      const row = document.createElement('div');
      row.className = 'v-row-wrap'; row.dataset.vRow = data.id; row.dataset.vKind = data.type; row.dataset.vName = data.name; row.dataset.vOwner = data.owner; row.dataset.vFolder = data.folder;
      row.innerHTML = '<button class="vault-list-row selected" data-v-action="saved-preview"><span class="item-copy"><strong>' + esc(data.name) + '</strong><small>Created in this preview</small></span></button>';
      list.querySelector('[data-v-rows]').prepend(row);
      list.querySelectorAll('.vault-list-row').forEach(button => { if (!row.contains(button)) button.classList.remove('selected'); });
      filterRows();
    }
    localToast(creating ? 'Item created in the local preview. No server request was made' : 'Changes applied to the local preview. No server request was made');
  }
  function filterRows() {
    const search = list.querySelector('[data-v-search]');
    if (!search || search.disabled) return;
    if (list.dataset.vLocalFolder) return;
    const query = search.value.trim().toLowerCase();
    const type = list.querySelector('[data-v-filter]').value;
    const favorite = !!list.querySelector('[data-v-favorite-filter]')?.checked;
    const rows = [...list.querySelectorAll('[data-v-row]')];
    for (const row of rows) row.hidden = !!((query && !row.textContent.toLowerCase().includes(query)) || (type && row.dataset.vKind !== type) || (favorite && row.dataset.vFavorite !== 'true'));
    const visible = rows.filter(row => !row.hidden);
    const batch = !!host.querySelector('.v-batch');
    if (!batch) list.querySelector('[data-v-list-count]').textContent = visible.length + ((query || type || favorite) ? (visible.length === 1 ? ' result' : ' results') : (visible.length === 1 ? ' item' : ' items'));
    list.querySelector('[data-v-empty]').hidden = visible.length > 0;
    const select = list.querySelector('[data-v-action="batch-start"]');
    if (select) select.disabled = visible.length === 0;
    if (list.dataset.vInitialState === 'no-results' && !query && !type && !favorite) host.innerHTML = paneEmpty('Choose an item', 'Select a login, card or key to see its details.');
    else if (!visible.length && !getForm() && !batch) host.innerHTML = paneEmpty('No matching items', 'Clear search or change your filters. Your saved items have not changed.');
  }
  function showDetails() { host.innerHTML = savedDetail || surface.querySelector('#v-detail-template').innerHTML; }
  function revealField(button) {
    const detail = getDetail();
    if (detail?.dataset.vProtected === 'true') {
      pendingReveal = button;
      localDialog('Verify your master password', 'This item requires verification before showing sensitive fields.', 'Verify and reveal', dialog => {
        dialog.querySelector('input').value = '';
        detail.dataset.vProtected = 'false';
        const totp = detail.querySelector('[data-v-totp]');
        if (totp) { totp.textContent = '482 916'; totp.nextElementSibling.textContent = 'Refreshes every 30 seconds'; }
        const pending = pendingReveal; pendingReveal = null;
        if (pending) revealField(pending);
        localToast('Synthetic verification completed; no account authentication was performed');
      }, { fields: '<label class="form-field"><span>Master password</span><input type="password" required autocomplete="off"></label>', required: true });
      return;
    }
    const value = button.closest('.field-row')?.querySelector('[data-v-secret]');
    if (!value) return;
    const showing = button.getAttribute('aria-pressed') === 'true';
    value.textContent = showing ? '••••••••' : value.dataset.vSecret || '—';
    button.setAttribute('aria-pressed', String(!showing));
    const label = button.dataset.vLabel || 'secret';
    button.setAttribute('aria-label', (showing ? 'Show ' : 'Hide ') + label);
    button.title = button.getAttribute('aria-label');
  }
  function updateBatch() {
    const pane = host.querySelector('.v-batch');
    if (!pane) return;
    const selected = [...list.querySelectorAll('[data-v-row]')].filter(row => row.querySelector('.v-row-check')?.checked);
    const count = selected.length;
    list.querySelector('[data-v-list-count]').textContent = count + ' selected';
    pane.querySelector('[data-v-batch-title]').textContent = count + ' items selected';
    const owners = new Set(selected.map(row => row.dataset.vOwner));
    const folders = new Set(selected.map(row => row.dataset.vFolder));
    pane.querySelector('[data-v-batch-context]').textContent = count ? (owners.size === 1 ? [...owners][0] : owners.size + ' owners') + ' · ' + folders.size + ' folders' : 'Select items from the list to continue.';
    pane.querySelector('[data-v-batch-items]').innerHTML = selected.map(row => '<div class="compact-row"><div><strong>' + esc(row.dataset.vName) + '</strong><p>' + esc(row.dataset.vFolder || 'No folder') + '</p></div></div>').join('');
    const personal = count > 0 && selected.every(row => row.dataset.vOwner === 'Personal vault');
    pane.querySelector('[data-v-batch-permission]').textContent = personal ? 'All selected items are personal and can be edited.' : count ? 'This selection includes a read-only organization item. Remove it to change personal items.' : 'No items are selected.';
    pane.querySelectorAll('button[data-v-action]').forEach(button => { button.disabled = !personal; });
    const favorites = count > 0 && selected.every(row => row.dataset.vFavorite === 'true');
    const favoriteButton = pane.querySelector('[data-v-action="batch-favorite"]');
    favoriteButton.textContent = favorites ? 'Remove from favorites' : 'Add to favorites';
    favoriteButton.dataset.vRemoveFavorite = String(favorites);
  }
  function operationDialog(operation, itemName, count = 1) {
    const isFolder = operation === 'Move to folder';
    const isTransfer = operation === 'Transfer to organization';
    const fields = isFolder ? '<label class="form-field"><span>Folder</span><select data-v-operation-folder><option>Work</option><option>Personal</option><option>Finance</option><option>No folder</option></select></label>' : isTransfer ? '<label class="form-field"><span>Organization</span><select><option>Contoso Review</option></select></label><label class="form-field"><span>Collection</span><select><option>Engineering</option></select></label>' : '';
    const message = operation === 'Move to trash' ? itemName + '\nYou can restore the item from Trash.' : isTransfer ? itemName + '\nThe organization will own the selected item. Its policies and permissions will apply.' : operation === 'Archive' ? itemName + '\nArchived items remain available in Archive.' : itemName;
    localDialog(operation + '?', message, operation, dialog => { const destination = dialog.querySelector('[data-v-operation-folder]')?.value; localToast(operation + (destination ? ' → ' + destination : '') + ' reviewed for ' + count + ' item' + (count === 1 ? '' : '')); }, { fields, danger: operation === 'Move to trash' });
  }
  function createLocalFolder(name) {
    const folder = document.createElement('button'); folder.type = 'button'; folder.className = 'nav-item folder is-current'; folder.dataset.vAction = 'local-folder'; folder.dataset.vFolderName = name;
    folder.innerHTML = (list.querySelector('[data-v-target="vault-folder-work"] .icon')?.outerHTML || '') + '<span>' + esc(name) + '</span><small>0</small>';
    document.querySelector('.folder-nav')?.append(folder);
    list.dataset.vLocalFolder = name;
    list.querySelector('.list-heading h1').textContent = name;
    list.querySelectorAll('[data-v-row]').forEach(row => { row.hidden = true; });
    list.querySelector('[data-v-list-count]').textContent = '0 items';
    list.querySelector('[data-v-empty]').hidden = false; list.querySelector('[data-v-empty]').textContent = 'No items in this folder';
    const select = list.querySelector('[data-v-action="batch-start"]'); if (select) select.disabled = true;
    host.innerHTML = paneEmpty('Your folder is ready', 'Move an existing item here, or add a new item to this folder.');
    localToast('Folder created in the local preview; no server request was made');
  }
  function targetForRootButton(button) {
    if (button.dataset.go) return button.dataset.go;
    if (button.dataset.page) return scenes.find(scene => scene.page === button.dataset.page && !scene.dialog && scene.variant === (button.dataset.page === 'vault' ? 'no-selection' : button.dataset.page === 'settings' ? 'security' : button.dataset.page === 'generator' ? 'password' : button.dataset.page === 'login' ? 'locked' : 'overview'))?.id;
    return null;
  }
  document.addEventListener('click', event => {
    const button = event.target.closest('button');
    if (!button) return;
    if (button.closest('.v-local-dialog')) return;
    const action = button.dataset.vAction;
    if (!action) {
      if (current === 'vault-reprompt' && button.closest('.app-dialog') && button.textContent.trim() === 'Cancel') {
        consume(event); document.querySelector('.modal-scrim')?.remove(); document.querySelector('.app-window').inert = false; getDetail()?.querySelector('[data-v-action="reveal"]')?.focus(); return;
      }
      if (current === 'vault-permanent-delete' && button.closest('.app-dialog') && button.textContent.trim() === 'Cancel') { consume(event); navigate('vault-trash'); return; }
      const form = getForm();
      if (form && (form.dataset.dirty === 'true' || form.dataset.vPending === 'true')) {
        const target = targetForRootButton(button);
        if (target) { consume(event); navigateSafely(target); }
      }
      return;
    }
    consume(event);
    if (button.disabled) return;
    const form = getForm(), detail = getDetail();
    if (action === 'navigate' || action === 'row') { navigateSafely(button.dataset.vTarget); return; }
    if (action === 'new-folder') { guardNavigation(() => localDialog('New folder', 'Create a folder to organize items in your personal vault.', 'Create folder', dialog => createLocalFolder(dialog.querySelector('input').value.trim()), { fields: '<label class="form-field"><span>Folder name (required)</span><input type="text" required maxlength="100"></label>', required: true })); return; }
    if (action === 'saved-preview') { guardNavigation(showDetails); return; }
    if (action === 'edit') {
      const openEditor = () => { savedDetail = detail?.dataset.vProtected === 'true' ? surface.querySelector('#v-detail-template').innerHTML : host.innerHTML; host.innerHTML = surface.querySelector('#v-editor-template').innerHTML; initializeForm(); host.querySelector('[name="name"]')?.focus(); };
      if (detail?.dataset.vProtected === 'true') localDialog('Verify your master password', 'Verify access before editing this protected item.', 'Verify and edit', dialog => { dialog.querySelector('input').value = ''; openEditor(); localToast('Synthetic verification completed; no account authentication was performed'); }, { fields: '<label class="form-field"><span>Master password</span><input type="password" required autocomplete="off"></label>', required: true });
      else openEditor();
      return;
    }
    if (action === 'cancel-edit') { guardNavigation(() => { if (form.dataset.vNew === 'true') navigate('vault-no-selection'); else showDetails(); }); return; }
    if (action === 'save') { if (form) saveForm(form); return; }
    if (action === 'input-reveal') {
      const input = button.closest('.v-secret-input').querySelector('input,textarea');
      const revealed = button.getAttribute('aria-pressed') === 'true';
      if (input.tagName === 'TEXTAREA') input.classList.toggle('v-unmasked', !revealed); else input.type = revealed ? 'password' : 'text';
      button.setAttribute('aria-pressed', String(!revealed));
      const label = button.closest('.form-field').querySelector('span')?.textContent || 'secret';
      button.setAttribute('aria-label', (revealed ? 'Show ' : 'Hide ') + label); button.title = button.getAttribute('aria-label'); return;
    }
    if (action === 'reveal') { revealField(button); return; }
    if (action === 'copy') {
      if (detail?.dataset.vProtected === 'true') {
        localDialog('Verify before copying', 'Copying does not reveal the password on screen.', 'Verify and copy preview', dialog=>{dialog.querySelector('input').value='';localToast((button.dataset.vLabel||'Field')+' copy preview completed; the value stays hidden and the clipboard was not changed');},{fields:'<label class="form-field"><span>Master password</span><input type="password" required autocomplete="off"></label>',required:true});
        return;
      }
      localToast('Copy feedback: ' + (button.dataset.vLabel || 'field') + ' · clipboard clears after 30 seconds'); return;
    }
    if (action === 'favorite') {
      const on = button.getAttribute('aria-pressed') !== 'true'; button.setAttribute('aria-pressed', String(on)); button.classList.toggle('v-favorite-on', on); button.title = (on ? 'Remove from' : 'Add to') + ' favorites'; button.setAttribute('aria-label', button.title);
      const row = list.querySelector('[data-v-row="' + detail.dataset.vRecord + '"]'); if (row) { row.dataset.vFavorite = String(on); const star = row.querySelector('.favorite'); if (star) star.hidden = !on; }
      localToast(on ? 'Favorite added' : 'Favorite removed'); return;
    }
    if (action === 'clear-search') { list.querySelector('[data-v-search]').value = ''; list.querySelector('[data-v-filter]').value = ''; list.querySelector('[data-v-favorite-filter]').checked = false; filterRows(); list.querySelector('[data-v-search]').focus(); return; }
    if (action === 'batch-start') { navigateSafely('vault-batch'); return; }
    if (action === 'batch-done') { navigate('vault-no-selection'); return; }
    if (action === 'select-row') { const check = button.closest('[data-v-row]').querySelector('.v-row-check'); check.checked = !check.checked; updateBatch(); return; }
    if (action === 'batch-favorite') {
      const remove = button.dataset.vRemoveFavorite === 'true'; list.querySelectorAll('[data-v-row]').forEach(row => { if (row.querySelector('.v-row-check')?.checked) { row.dataset.vFavorite = String(!remove); const star = row.querySelector('.favorite'); if (star) star.hidden = remove; } }); updateBatch(); localToast(remove ? 'Favorites removed from this selection' : 'Favorites added to this selection'); return;
    }
    if (action === 'batch-operation') { const rows = [...list.querySelectorAll('[data-v-row]')].filter(row => row.querySelector('.v-row-check')?.checked); operationDialog(button.dataset.vOperation, rows.map(row => row.dataset.vName).join('\n'), rows.length); return; }
    if (action === 'move-folder' || action === 'transfer' || action === 'soft-delete') { operationDialog(action === 'move-folder' ? 'Move to folder' : action === 'transfer' ? 'Transfer to organization' : 'Move to trash', detail.querySelector('[data-v-title]').textContent); return; }
    if (action === 'permanent-delete') { navigate('vault-permanent-delete'); return; }
    if (action === 'confirm-permanent-delete') { localToast('Permanent deletion reviewed for Old VPN. No account data was changed'); return; }
    if (action === 'restore' || action === 'unarchive') { operationDialog(action === 'restore' ? 'Restore item' : 'Unarchive item', detail.querySelector('[data-v-title]').textContent); return; }
    if (action === 'website') { localDialog('Open this website?', button.dataset.vUrl, 'Open website preview', () => localToast('Website destination reviewed; no browser tab was opened')); return; }
    if (action === 'information') { const r = JSON.parse(detail.dataset.vModel); localDialog('Item information', r.name + '\nOwner: ' + r.owner + '\nFolder: ' + (r.folder || 'No folder') + '\nCreated: ' + (r.created || 'Just now') + '\nEdited: ' + (r.edited || 'Just now'), 'Done', () => {}); return; }
    if (action === 'attachment-delete') { localDialog('Delete this attachment?', 'review-checklist.txt\nThe vault item will remain. This attachment cannot be restored after deletion.', 'Delete attachment', () => localToast('Attachment deletion reviewed; no file was changed'), { danger: true }); return; }
    if (action === 'attachment-add' || action === 'download') { localToast(action === 'attachment-add' ? 'Choose an attachment preview' : 'Download feedback for review-checklist.txt'); return; }
    if (action === 'generate') { form.querySelector('.v-generator-inline').hidden = false; return; }
    if (action === 'regenerate-password') { form.querySelector('[data-v-generated]').textContent = form.querySelector('[data-v-generated]').textContent.startsWith('R7') ? 'P8@cW3!nZ6#rT9$mK2' : 'R7!mQ2#zP9@vK4$wX6'; return; }
    if (action === 'cancel-generator') { form.querySelector('.v-generator-inline').hidden = true; return; }
    if (action === 'use-password') { form.querySelector('[name="password"]').value = form.querySelector('[data-v-generated]').textContent; form.querySelector('.v-generator-inline').hidden = true; refreshForm(form); return; }
    if (action === 'add-uri') { const container = form.querySelector('[data-v-uri-list]'); const count = container.querySelectorAll('[name^="website"]').length + 1; const block = document.createElement('div'); block.innerHTML = '<label class="form-field"><span>Additional website</span><input name="website' + count + '" data-v-key="website' + count + '"></label><label class="form-field"><span>Match detection</span><select name="match' + count + '" data-v-key="match' + count + '"><option value="">Default</option><option value="0">Base domain</option><option value="1">Host</option><option value="2">Starts with</option><option value="3">Exact</option><option value="4">Regular expression</option><option value="5">Never</option></select></label>'; container.append(block); block.querySelector('input').focus(); refreshForm(form); return; }
    if (action === 'add-custom') { const container = form.querySelector('[data-v-custom-list]'); const index = container.children.length; const row = document.createElement('div'); row.className = 'v-custom-editor'; row.innerHTML = '<label class="form-field"><span>Field name</span><input name="customName' + index + '"></label><label class="form-field"><span>Field type</span><select name="customType' + index + '" data-v-custom-type><option>Text</option><option>Hidden</option><option>Boolean</option></select></label><label class="form-field"><span>Value</span><input name="customValue' + index + '"></label>'; container.append(row); row.querySelector('input').focus(); refreshForm(form); return; }
    if (action === 'retry-sync') { button.disabled = true; button.textContent = 'Retrying…'; setTimeout(() => { if (!button.isConnected) return; button.disabled = false; button.textContent = 'Retry sync'; localToast('Still offline. Saved items remain available'); }, 800); return; }
    if (action === 'check-pending') { button.disabled = true; button.textContent = 'Checking…'; setTimeout(() => { if (!button.isConnected) return; button.disabled = false; button.textContent = 'Check sync status'; localToast('Result still unconfirmed. Keep the draft and check again when connected'); }, 800); return; }
    if (action === 'keep-pending') { localToast('The unconfirmed draft is retained; no duplicate creation was attempted'); return; }
    if (action === 'verify-reprompt') {
      const input = document.querySelector('[data-v-master-password]'); if (!input?.value) return; input.value = '';
      document.querySelector('.modal-scrim')?.remove(); document.querySelector('.app-window').inert = false;
      if (detail) { detail.dataset.vProtected = 'false'; const code = detail.querySelector('[data-v-totp]'); if (code) { code.textContent = '482 916'; code.nextElementSibling.textContent = 'Refreshes every 30 seconds'; } }
      localToast('Synthetic verification completed; no account authentication was performed'); return;
    }
    if (action === 'create-folder') {
      const name = document.querySelector('[data-v-folder-name]')?.value.trim(); if (!name) return;
      document.querySelector('.modal-scrim')?.remove(); document.querySelector('.app-window').inert = false;
      createLocalFolder(name); return;
    }
    if (action === 'local-folder') { host.innerHTML = paneEmpty('Your folder is ready', 'This folder is empty in the current preview.'); return; }
    if (action === 'reprompt') { const reveal = detail.querySelector('[data-v-action="reveal"]'); if (reveal) revealField(reveal); return; }
    localToast('Action reviewed without changing account data');
  }, true);
  document.addEventListener('input', event => {
    const input = event.target;
    if (input.matches('[data-v-search]')) { consume(event); filterRows(); return; }
    if (input.matches('[data-v-master-password]')) { const button = document.querySelector('[data-v-action="verify-reprompt"]'); if (button) button.disabled = !input.value; return; }
    if (input.matches('[data-v-folder-name]')) { const button = document.querySelector('[data-v-action="create-folder"]'); if (button) button.disabled = !input.value.trim(); return; }
    const form = input.closest('.v-editor-form'); if (form) refreshForm(form);
  }, true);
  document.addEventListener('change', event => {
    const input = event.target;
    if (input.matches('[data-v-filter],[data-v-favorite-filter]')) { consume(event); filterRows(); return; }
    if (input.matches('[data-v-sort]')) { const rows = [...list.querySelectorAll('[data-v-row]')]; if (input.value !== 'default') rows.sort((a, b) => a.dataset.vName.localeCompare(b.dataset.vName) * (input.value === 'name-desc' ? -1 : 1)); rows.forEach(row => list.querySelector('[data-v-rows]').append(row)); return; }
    if (input.matches('.v-row-check')) { updateBatch(); return; }
    const form = input.closest('.v-editor-form'); if (!form) return;
    if (input.matches('[data-v-create-type]')) {
      form.dataset.vType = input.value;
      form.querySelector('[data-v-type-fields]').innerHTML = surface.querySelector('[data-v-fields-template="' + input.value + '"]').innerHTML;
      form.querySelector('[data-v-editor-title]').textContent = 'New ' + (input.value === 'ssh' ? 'SSH key' : typeNames[input.value].toLowerCase());
      form.querySelector('[data-v-form-title]').textContent = typeNames[input.value] + ' information';
      form.querySelector('[data-v-notes-field]').hidden = input.value === 'note';
    }
    if (input.matches('[data-v-custom-type]')) {
      const value = input.closest('.v-custom-editor').querySelector('[name^="customValue"]');
      if (value) {
        value.type = input.value === 'Hidden' ? 'password' : input.value === 'Boolean' ? 'checkbox' : 'text'; value.readOnly = input.value === 'Linked';
        const previous = value.closest('.v-secret-input');
        if (input.value === 'Hidden' && !previous) { const wrapper = document.createElement('div'); wrapper.className = 'v-secret-input'; value.before(wrapper); wrapper.append(value); const reveal = document.createElement('button'); reveal.type = 'button'; reveal.className = 'button small'; reveal.dataset.vAction = 'input-reveal'; reveal.setAttribute('aria-label', 'Show field value'); reveal.textContent = 'Show'; wrapper.append(reveal); }
        if (input.value !== 'Hidden' && previous) previous.replaceWith(value);
      }
    }
    refreshForm(form);
  }, true);
  document.addEventListener('keydown', event => {
    if (event.key !== 'Escape' || !document.querySelector('.app-dialog')) return;
    if (current === 'vault-reprompt') { consume(event); document.querySelector('.modal-scrim')?.remove(); document.querySelector('.app-window').inert = false; getDetail()?.querySelector('[data-v-action="reveal"]')?.focus(); }
    if (current === 'vault-permanent-delete') { consume(event); navigate('vault-trash'); }
  }, true);
  initializeForm();
  if (list.dataset.vInitialState === 'no-results') filterRows();
  if (list.dataset.vInitialState === 'batch') updateBatch();
  if (list.dataset.vInitialState === 'pending-save') list.querySelectorAll('button,select,input').forEach(input => { input.disabled = true; });
  if (current === 'vault-reprompt') setTimeout(() => document.querySelector('[data-v-master-password]')?.focus(), 0);
  if (current === 'vault-new-folder') setTimeout(() => document.querySelector('[data-v-folder-name]')?.focus(), 0);
  list.querySelector('.vault-list-row.selected')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
})();
`;
