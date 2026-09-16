// UI proposals only. Authentication and account operations never contact a server.
const accountEmail = 'review@winvaultwarden.example';
const accountServer = 'http://127.0.0.1:18083';

function helpers(ui) {
  const { e, icon, button, ib, label } = ui;
  const password = (name, help = '', attrs = '', value = '') => `<div class="review-password">${label(name, value, 'password', help, attrs)}${ib('eye', 'Show ' + name.toLowerCase(), 'data-password-toggle aria-pressed="false"')}</div>`;
  const identity = () => `<div class="auth-account review-account"><span class="avatar">AR</span><div><strong>Alex Review</strong><span>${e(accountEmail)}</span><small>${e(accountServer)}</small></div></div>`;
  const command = (name, style = 'primary wide', attrs = '', target = '') => button(name, style, '', target, attrs);
  const submit = (name, style = 'primary wide') => command(name, style, 'data-review-submit');
  const readOnlyIdentity = () => `<div class="review-account-context"><strong>${e(accountEmail)}</strong><span>${e(accountServer)}</span></div>`;
  const code = (name = 'Verification code', help = 'Enter the current code. Spaces are accepted.') => label(name, '', 'text', help, 'required inputmode="numeric" autocomplete="one-time-code" pattern="[0-9]{6}" data-six-digit-code');
  return { password, identity, command, submit, readOnlyIdentity, code };
}

export function renderAuth(s, ui) {
  const { e, icon, button, note, label, select, titlebar } = ui;
  const { password, identity, command, submit, code } = helpers(ui);
  const v = s.variant;
  let title = 'Welcome back', subtitle = 'Sign in to your vault.', body = '', action = '', back = '', register = false;
  const serverFields = `<label class="form-field"><span>Server</span><select data-server-select aria-label="Server"><option value="self">Self-hosted</option><option value="us">Bitwarden.com (US)</option><option value="eu">Bitwarden.com (EU)</option></select></label><div data-server-address>${label('Server address', accountServer, 'url', '', 'required autocomplete="url"')}</div>`;
  const alternate = `<button class="text-button" data-go="login-methods">Use another verification method</button>`;
  const stage = (text, target = 'login-account') => { back = `<button class="auth-back text-button" data-go="${target}">← ${e(text)}</button>`; };
  if (s.page === 'register' && v === 'form') {
    register = true; title = 'Create your account'; subtitle = 'Choose your server, then protect your vault with a master password.';
    body = `<div class="review-register-grid">${serverFields}${label('Email address', accountEmail, 'email', '', 'required autocomplete="email"')}${label('Name (optional)', 'Alex Review', 'text', '', 'maxlength="50" autocomplete="name"')}${password('Master password', 'Use a unique passphrase you can remember.', 'required autocomplete="new-password" data-password-new')}${password('Confirm master password', '', 'required autocomplete="new-password" data-password-confirm')}<div class="review-span-all">${label('Password hint (optional)', '', 'text', 'Never include your password in the hint.', 'maxlength="50"')}</div></div>`;
    action = submit('Create account') + `<p class="auth-switch">Already have an account? <button class="text-button" data-go="login-account">Sign in</button></p>`;
  } else if (s.page === 'register' && v === 'verification') {
    title = 'Check your email'; subtitle = 'Continue registration in the Web Vault.'; body = identity() + note('Check the registration message', 'If this email can be registered, follow the email link to finish in the Web Vault, then return to sign in.');
    action = command('Sign in after verification', 'primary wide', '', 'login-account') + command('Change email', 'wide', '', 'register-account');
  } else if (s.page === 'register' && v === 'uncertain') {
    title = 'Registration result unknown'; subtitle = 'The connection ended before the result could be confirmed.'; body = identity() + note('Registration may have completed', 'Try signing in first. The app will not automatically submit another registration request.', 'warning');
    action = command('Try signing in', 'primary wide', '', 'login-account') + command('Edit registration details', 'wide', '', 'register-account');
  } else if (v === 'account') {
    body = `<details class="review-saved-accounts"><summary>Use a saved account</summary><button class="review-saved-account" data-go="login-locked"><span class="avatar">AR</span><span><strong>${e(accountEmail)}</strong><small>${e(accountServer)}</small></span>${icon('lock')}</button></details>${serverFields}${label('Email address', accountEmail, 'email', '', 'required autocomplete="username"')}`;
    action = command('Continue', 'primary wide', 'data-review-next="login-password"') + `<div class="or"><span>or</span></div>${command('Sign in with SSO', 'wide', 'data-review-submit')}<p class="auth-switch">New here? <button class="text-button" data-go="register-account">Create an account</button></p>`;
  } else if (v === 'password' || v === 'error') {
    title = 'Enter your master password'; subtitle = 'Sign in to the account below.'; stage('Change account');
    body = identity() + (v === 'error' ? note('Sign-in could not be completed', 'Check your connection and try again. Your vault remains locked.', 'warning') : '') + password('Master password', '', 'required autocomplete="current-password"') + `<button class="text-button" data-go="login-device-approval">Log in with a device</button>`;
    action = submit(v === 'error' ? 'Try again' : 'Sign in');
  } else if (v === 'locked' || v === 'unlock-pin') {
    const pin = v === 'unlock-pin'; title = 'Your vault is locked'; subtitle = 'Unlock the saved account on this device.'; stage('Switch account');
    body = identity() + password(pin ? 'PIN' : 'Master password', pin ? 'Use the PIN configured on this device.' : '', `required autocomplete="${pin ? 'off' : 'current-password'}"`) + `<div class="auth-help">${icon('lock')} Your vault contents are hidden while locked.</div>`;
    if (pin) body += `<button class="text-button" data-go="login-locked">Use master password</button>`;
    action = submit('Unlock vault');
  } else if (v === 'unlock-enterprise') {
    title = 'Unlock your enterprise vault'; subtitle = 'This account uses organization-managed sign-in.'; stage('Switch account'); body = identity() + note('Continue with your organization', 'Use your organization’s identity provider to unlock this account. No master password is available for this sign-in method.'); action = submit('Continue with SSO');
  } else if (v === 'two-step' || v === 'email-code') {
    const email = v === 'email-code'; title = 'Verify it’s you'; subtitle = email ? 'Enter the code sent to your email.' : 'Enter the code from your authenticator app.'; stage('Back to sign-in', 'login-password');
    body = identity() + code('Verification code', email ? 'Check the inbox for r•••••@winvaultwarden.example.' : 'Use the current six-digit code from your authenticator.') + `<label class="check-line"><input type="checkbox">Remember this device for two-step verification</label><p class="caption">Use this only on a device you trust. Your vault still locks normally.</p>${email ? command('Resend code', 'text-button', 'data-review-submit') : ''}${alternate}`; action = submit('Verify and sign in');
  } else if (v === 'methods') {
    title = 'Choose a verification method'; subtitle = 'Available methods are provided by your account.'; stage('Back', 'login-two-step'); body = identity() + `<div class="review-method-options"><button data-go="login-two-step"><span><strong>Authenticator app</strong><small>Use a current verification code</small></span>${icon('arrow')}</button><button data-review-method="email"><span><strong>Email</strong><small>Use a code sent to your registered email</small></span>${icon('chevron')}</button><button data-review-submit><span><strong>Security key</strong><small>Continue in Windows security</small></span>${icon('arrow')}</button><button data-go="login-recovery"><span><strong>Recovery code</strong><small>Removes all configured two-step methods</small></span>${icon('arrow')}</button></div><div class="review-method-detail" hidden>${code('Email verification code', 'Check r•••••@winvaultwarden.example.')}<div class="review-secondary-actions">${submit('Verify email code', 'primary')}${command('Resend code', 'text-button', 'data-review-submit')}</div></div>`;
  } else if (v === 'security-key') {
    title = 'Verify with a security key'; subtitle = 'Use a registered security key or Windows Hello.'; stage('Other methods', 'login-methods'); body = identity() + note('Windows will ask you to verify', 'Follow the Windows security prompt. Cancelling keeps this account locked.'); action = submit('Open Windows security');
  } else if (v === 'recovery') {
    title = 'Use your recovery code'; subtitle = 'Recover two-step verification access for this account.'; stage('Other methods', 'login-methods'); body = identity() + note('All two-step methods will be removed', 'Successful recovery removes every configured two-step verification method. Set up protection again after signing in.', 'warning') + password('Recovery code', 'Paste the recovery code you saved for this account.', 'required autocomplete="off"'); action = submit('Recover and sign in');
  } else if (v === 'device-approval' || v === 'trusted-approval') {
    const trusted = v === 'trusted-approval'; title = trusted ? 'Approve this trusted device' : 'Approve from another device'; subtitle = 'Open an unlocked vault on a device you trust.'; stage('Use another sign-in method', 'login-password'); body = identity();
    if (trusted) body += select('Approval method', ['Another trusted device', 'Organization administrator']);
    body += `<div class="approval-phrase"><span class="field-label">Match this phrase on both devices</span><p class="mono">amber-cedar-river-meadow-harbor</p></div><div class="auth-help">${icon('clock')} Waiting for approval · Request expires in 4:32</div>`;
    if (trusted) body += `<label class="check-line"><input type="checkbox">Trust this device after approval</label>`;
    action = command('Cancel request', 'wide', '', 'login-password');
  } else if (v === 'sso-pending') {
    title = 'Continue in your browser'; subtitle = 'Complete sign-in with your organization.'; body = identity() + `<div class="review-wait-status">${icon('clock')}<strong>Waiting for your identity provider</strong><p>Return here after browser sign-in. Your vault remains locked until verification completes.</p></div>`; action = command('Cancel SSO sign-in', 'wide', '', 'login-account');
  } else if (v === 'password-change-required') {
    title = 'Update your master password'; subtitle = 'Your account requires a new password before the vault opens.'; stage('Switch account'); body = identity() + note('Password policy update', 'Choose a new password that meets your organization’s current requirements.', 'warning') + password('New master password', '', 'required autocomplete="new-password" data-password-new') + password('Confirm new master password', '', 'required autocomplete="new-password" data-password-confirm') + label('Password hint (optional)', '', 'text', 'Never include your password in the hint.', 'maxlength="50"'); action = submit('Update password');
  } else if (v === 'jit-encryption') {
    title = 'Set up your enterprise vault'; subtitle = 'Finish account recovery registration before opening your vault.'; stage('Switch account'); body = identity() + note('Create your account encryption keys', 'This organization-managed account has not yet initialized its vault keys. Continue to finish account recovery registration.') + `<label class="check-line"><input type="checkbox">Trust this device after setup</label>`; action = submit('Complete setup');
  } else if (v === 'quarantine') {
    title = 'Device sign-in is isolated'; subtitle = 'Review the security state before using device sign-in.'; stage('Switch account'); body = identity() + note('Device sign-in is unavailable', 'Review and revoke suspicious sessions from another trusted device. Master-password sign-in remains available when permitted.', 'warning'); action = command('Use master password', 'primary wide', '', 'login-password') + command('Review isolation cleanup', 'wide', '', 'login-clear-quarantine');
  } else if (v === 'clear-quarantine') {
    title = 'Clear device sign-in isolation?'; subtitle = 'This only changes the device sign-in safety state.'; stage('Cancel', 'login-quarantine'); body = identity() + note('Check other sessions first', 'Review and revoke suspicious sessions on your other devices. Clearing isolation rechecks whether this device can use device sign-in; it does not approve a request.', 'warning'); action = submit('Clear isolation', 'wide');
  } else if (v === 'deletion-recovery' || v === 'local-cleanup') {
    const cleanup = v === 'local-cleanup'; title = cleanup ? 'Clear this device’s account data?' : 'Account deletion result unknown'; subtitle = cleanup ? 'This does not send another deletion request to the server.' : 'The earlier request may still complete on the server.'; stage('Use another account'); body = identity() + note('Old sessions cannot be used', 'Offline, PIN, Windows Hello and saved-token unlock stay disabled for this account.', 'warning');
    if (cleanup) body += note('This account stays isolated in this installation', 'Local account data will be removed and a permanent safety record will block this account/server pair. If the account still exists later, a full Windows app reset is required to use it in this installation.', 'danger');
    action = cleanup ? submit('Clear this device only', 'danger wide') : command('Review local cleanup', 'wide', '', 'login-local-cleanup');
  } else if (v === 'local-reset') {
    title = 'Reset all local app data?'; subtitle = 'Server accounts and vault data are not deleted.'; stage('Cancel'); body = note('All saved accounts on this installation', 'The app will exit. At the next launch, saved accounts, offline vaults, device keys, preferences and deletion-isolation records are removed before sign-in. Browser integration may need setup again.', 'danger') + label('Type RESET to confirm', '', 'text', '', 'required pattern="RESET" autocomplete="off"'); action = submit('Reset and exit', 'danger wide');
  } else {
    return null;
  }
  return `<div class="auth-stage"><div class="auth-window review-auth ${register ? 'register-window review-register' : ''}">${titlebar()}<main class="auth-body"><span class="auth-brand">${icon('shield')}</span><h1>${e(title)}</h1><p class="auth-subtitle">${e(subtitle)}</p>${back}<div class="auth-form" data-review-form>${body}${action}</div><footer class="auth-foot">${icon('globe')}<span data-auth-server>${e(accountServer)}</span></footer></main></div><div class="toast" role="status" aria-live="polite"></div></div>`;
}

export function renderSettings(s, ui) {
  const { button, pill, label, select, toggle, setting, section, header, note } = ui;
  const v = s.variant || 'security';
  let content = '';
  if (v === 'preferences') {
    content = section('Appearance', setting('Theme', 'Choose how the application looks.', '<select aria-label="Theme"><option>Use system setting</option><option>Light</option><option>Dark</option></select>') + toggle('Website icons', 'When off, no website-icon requests are sent to the server.', true)) + section('Window behavior', toggle('Show tray icon', 'Keep a visible entry in the Windows notification area.', true) + toggle('Close to tray', 'Lock the vault, then hide the window in the tray.', true) + toggle('Minimize to tray', 'Lock the vault, then hide the minimized window.')) + section('Browser integration', toggle('Connect to the browser extension', 'Portable edition only. Allow the extension to request saved passkeys.')) + `<details class="disclosure review-about"><summary><strong>About WinVaultWarden</strong><span>Version 0.1.0</span></summary><div class="padded"><p>Windows · .NET · x64</p>${button('Copy diagnostics', '', 'copy')}<p class="caption">Includes application, Windows, runtime and architecture details.</p></div></details>`;
  } else if (v === 'account') {
    content = section('Profile', setting('Alex Review', accountEmail, button('Edit profile', '', '', 'settings-profile')) + setting('Server', accountServer, pill('Self-hosted'))) + section('Account access', setting('Master password', 'Change the password that protects this account.', button('Change password', '', '', 'settings-change-password')) + setting('Encryption settings', 'PBKDF2 · 600,000 iterations', button('Manage encryption', '', '', 'settings-kdf'))) + section('Danger zone', `<div class="danger-row"><div><strong>Delete account</strong><p>Permanently delete this account, personal vault, attachments and Sends.</p></div>${button('Delete account', 'danger-quiet', '', 'settings-delete-account')}</div>`);
  } else if (v === 'policy') {
    content = note('Managed by your organization', 'Some security choices are restricted by Contoso Review.') + section('Unlock and timeout', setting('Maximum inactivity timeout', 'Your organization allows a timeout of up to 15 minutes.', '<select aria-label="Inactivity timeout"><option>15 minutes</option><option>5 minutes</option><option>1 minute</option></select>') + toggle('Unlock with a PIN', 'Disabled by your organization’s security policy.', false, true) + setting('Windows Hello', 'Set up Windows Hello in Windows Settings to enable local unlock.', pill('Not configured'))) + section('Trusted-device encryption', setting('This device is not trusted', 'Allow this device to decrypt your vault after enterprise SSO.', button('Trust this device', '', '', '', 'data-review-submit'))) + section('Browser integration', toggle('Connect to the browser extension', 'Unavailable in the MSIX edition. This feature requires the portable edition.', false, true));
  } else {
    content = section('Unlock and timeout', setting('Lock after inactivity', 'Applies to this device.', '<select aria-label="Inactivity timeout"><option>15 minutes</option><option>5 minutes</option><option>1 hour</option></select>') + setting('When the timeout is reached', 'Lock keeps the account saved. Sign out requires signing in again.', '<select aria-label="Timeout action"><option>Lock vault</option><option>Sign out</option></select>') + setting('Unlock with a PIN', 'Set a device-specific PIN after unlocking with your master password.', button('Set up PIN', '', '', 'settings-pin-setup')) + setting('Windows Hello', 'Set up Windows Hello in Windows Settings to enable local unlock.', pill('Not configured'))) + section('Account protection', setting('Two-step verification', 'Review enabled methods and recovery options.', button('Manage methods', '', '', 'settings-two-factor')) + setting('Emergency access', 'Choose and confirm trusted contacts.', button('Manage contacts', '', '', 'emergency-overview'))) + section('Privacy', setting('Clear copied secrets after', 'Clears a copied secret while it is still on the clipboard.', '<select aria-label="Clipboard timeout"><option>30 seconds</option><option>10 seconds</option><option>1 minute</option></select>') + toggle('Allow screen capture', 'Includes screenshots, recording and screen sharing.', true));
  }
  const nav = [['security', 'Security'], ['account', 'Account'], ['preferences', 'Preferences']];
  return `<div class="page-content settings-content review-settings">${header('Settings', 'Protection, account details and preferences.')}<div class="settings-layout"><nav class="settings-nav">${nav.map(([id,text]) => `<button class="${v === id || (v === 'policy' && id === 'security') ? 'active' : ''}" data-go="settings-${id}">${text}</button>`).join('')}<span class="settings-version">WinVaultWarden<br>Version 0.1.0</span></nav><div class="settings-body">${content}</div></div></div>`;
}

export function accountDialog(s, ui) {
  if (s.page !== 'settings') return null;
  const { button, ib, pill, note, label, select } = ui;
  const { password, readOnlyIdentity, code } = helpers(ui);
  const d = s.dialog;
  const config = (title, desc, body, primary, attrs = 'data-review-submit', tone = '') => ({ title, desc, body, primary, primaryAttrs: attrs, tone });
  if (d === 'profile') return config('Edit profile', 'Update your display name for this account.', readOnlyIdentity() + label('Display name', 'Alex Review', 'text', 'Up to 50 characters. Email and server are unchanged.', 'maxlength="50"'), 'Save name');
  if (d === 'pin-setup') return config('Set up a PIN', 'Unlock this saved account on this device.', readOnlyIdentity() + password('PIN', 'Use at least 6 characters. Letters and numbers are supported.', 'required minlength="6" autocomplete="new-password" data-password-new') + password('Confirm PIN', '', 'required autocomplete="new-password" data-password-confirm') + note('Keep your master password', 'A PIN is a local unlock method. You still need your master password to sign in again.'), 'Set up PIN');
  if (d === 'change-password') return config('Change master password', 'Use a unique password you can remember.', readOnlyIdentity() + password('Current master password', '', 'required autocomplete="current-password"') + password('New master password', '', 'required autocomplete="new-password" data-password-new') + password('Confirm new master password', '', 'required autocomplete="new-password" data-password-confirm') + label('Password hint (optional)', '', 'text', 'Never include your password in the hint.', 'maxlength="50"') + note('You will be signed out', 'After the change succeeds, sign in again with your new master password.'), 'Change password');
  if (d === 'kdf') return config('PBKDF2 encryption settings', 'Adjust the work required to derive this account’s key.', readOnlyIdentity() + note('You will be signed out', 'More iterations make password guessing harder, but also slow unlocking. Keep a recent vault backup.', 'warning') + '<div class="review-summary-line"><span>Current algorithm</span><strong>PBKDF2-SHA256</strong></div>' + label('PBKDF2 iterations', '600000', 'number', 'Supported write range: 600,000 to 2,000,000.', 'required min="600000" max="2000000" step="100000"') + password('Current master password', '', 'required autocomplete="current-password"'), 'Apply changes');
  if (d === 'two-factor') return { ...config('Two-step verification', 'Review the methods protecting account sign-in.', readOnlyIdentity() + `<div class="method-list">${[['Authenticator app', 'Not enabled · Verify identity before setup', 'Set up', 'totp'], ['Security keys', '1 registered key', 'Manage', 'keys'], ['Email verification', 'Not enabled · Verify identity before setup', 'Set up', 'email']].map(([name,state,action,kind]) => `<div class="method-row"><div><strong>${name}</strong><p>${state}</p></div>${button(action, 'small', '', kind === 'keys' ? 'settings-security-keys' : '', kind === 'keys' ? '' : `data-review-setup="${kind}"`)}</div>`).join('')}</div><div class="review-secondary-actions">${button('Disable a method', 'text-button', '', 'settings-disable-twofactor')}${button('Get recovery code', 'text-button', '', '', 'data-review-setup="recovery"')}</div><div class="review-verification-gate" hidden>${password('Current master password', 'Verify your identity before continuing.', 'required autocomplete="current-password"')}${button('Verify identity', 'primary', '', '', 'data-review-submit')}</div><p class="caption">Setup and recovery-code access require account verification.</p>`, 'Close', ''), secondary: null };
  if (d === 'twofactor-verify' || d === 'email-verify' || d === 'recovery-verify') {
    const next = d === 'twofactor-verify' ? 'settings-totp-setup' : d === 'email-verify' ? 'settings-email-code' : 'settings-recovery-code';
    const purpose = d === 'twofactor-verify' ? 'authenticator setup' : d === 'email-verify' ? 'email verification setup' : 'recovery-code access';
    return config('Verify your identity', `Your master password is required before ${purpose}.`, readOnlyIdentity() + password('Current master password', '', 'required autocomplete="current-password"'), 'Verify', `data-review-submit data-review-followup="${next}"`);
  }
  if (d === 'totp-setup') return config('Set up an authenticator', 'Step 2 of 3 · Add the key, then verify a code.', readOnlyIdentity() + `<div class="setup-step"><span>1</span><div><strong>Add this setup key</strong><p>Choose manual setup in your authenticator app.</p></div></div><div class="setup-key mono">JBSW Y3DP EHPK 3PXP ${ib('copy', 'Copy setup key', 'data-copy-label="Example setup key"')}</div><details class="review-technical"><summary>Advanced setup URI</summary><p class="setup-uri mono">otpauth://totp/WinVaultWarden:review@winvaultwarden.example?secret=JBSWY3DPEHPK3PXP</p></details><div class="setup-step"><span>2</span><div><strong>Enter the current code</strong><p>Use the code shown in your authenticator.</p></div></div>` + code() + '<p class="caption">After activation, save the recovery code outside this vault.</p>', 'Enable authenticator');
  if (d === 'totp-complete' || d === 'recovery-code') return { ...config(d === 'totp-complete' ? 'Authenticator enabled' : 'Your recovery code', d === 'totp-complete' ? 'Step 3 of 3 · Keep a recovery option outside this vault.' : 'Keep this code somewhere safe outside this vault.', readOnlyIdentity() + note('Store it securely', 'Using this code during sign-in removes all configured two-step methods.') + `<div class="setup-key mono">DEMO-RECOVERY-NOT-A-REAL-CODE ${ib('copy', 'Copy recovery code', 'data-copy-label="Example recovery code"')}</div><p class="caption">Recovery code is shown only after identity verification.</p>`, 'Done', ''), secondary: null };
  if (d === 'totp-recovery-missing') return config('Authenticator enabled', 'The method is active, but the recovery code could not be retrieved.', readOnlyIdentity() + note('Save a recovery code when available', 'Retry only fetches your recovery code. It will not enable the authenticator again.', 'warning') + password('Current master password', '', 'required autocomplete="current-password"'), 'Retry recovery code');
  if (d === 'email-code') return config('Verify your email', 'Enter the code sent to your registered address.', readOnlyIdentity() + code('Email verification code', 'Check your inbox and spam folder.') + button('Resend code', 'text-button', '', '', 'data-review-submit'), 'Enable email verification');
  if (d === 'disable-twofactor') return config('Disable a two-step method?', 'Other enabled methods will remain available.', readOnlyIdentity() + select('Method', ['Security keys'], 'Only currently enabled methods are listed.') + password('Current master password', '', 'required autocomplete="current-password"') + note('Removing your last method reduces protection', 'Keep another verified method or your recovery code available before continuing.', 'warning'), 'Disable method', 'data-review-submit', 'danger');
  if (d === 'security-keys') return config('Security keys', 'Manage credentials used for account two-step verification.', readOnlyIdentity() + `<div class="device-row"><div><strong>Work security key</strong><p>Registered security key</p></div>${button('Remove', 'danger-quiet small', '', 'settings-remove-security-key')}</div>` + label('New key name', '', 'text', 'Choose a name that helps you recognize the key.', 'required maxlength="50"') + note('Verify before registering a key', 'Account verification comes first, followed by the Windows security prompt.'), 'Verify and add key');
  if (d === 'remove-security-key') return config('Remove Work security key?', 'This key will no longer verify sign-in for this account.', readOnlyIdentity() + note('Keep another sign-in method available', 'Removing a key does not delete vault items. Verify your account before removing the credential.', 'warning') + password('Current master password', '', 'required autocomplete="current-password"'), 'Remove security key', 'data-review-submit', 'danger');
  if (d === 'delete-account') return config('Review account deletion', 'Step 1 of 3 · Understand what will be removed.', readOnlyIdentity() + note('This cannot be undone', 'This account, personal vault, attachments, Sends and server access will be permanently deleted.', 'danger') + note('Organization ownership', 'Review ownership of Contoso Review before deleting. The server may require ownership changes before it accepts deletion.', 'warning') + '<p class="caption">Next: confirm your email, then verify your identity. No deletion request is sent at this step.</p>', 'Continue to email confirmation', 'data-review-next="settings-delete-email"');
  if (d === 'delete-email') return config('Confirm the account', 'Step 2 of 3 · Check the account and server carefully.', readOnlyIdentity() + label('Type your email to continue', '', 'email', 'Enter the full email shown above.', `required data-exact-value="${accountEmail}" autocomplete="off"`) + '<p class="caption">This step only confirms the target account.</p>', 'Continue to verification', 'data-review-next="settings-delete-credential"');
  if (d === 'delete-credential' || d === 'delete-otp') return config('Permanently delete this account?', 'Step 3 of 3 · Final confirmation.', readOnlyIdentity() + note('Deletion is permanent', 'Your personal vault, attachments and Sends will be deleted after the server accepts this request.', 'danger') + (d === 'delete-otp' ? code('Email verification code', 'This account requires a one-time email code instead of a master password.') : password('Current master password', '', 'required autocomplete="current-password"')), 'Delete account permanently', 'data-review-submit', 'danger');
  if (d === 'delete-blocked') return { ...config('Account deletion is unavailable', 'This account is managed by an organization.', readOnlyIdentity() + note('Contact your organization administrator', 'The account’s email domain is claimed by an organization. The app will not send a deletion request.', 'warning'), 'Close', ''), secondary: null };
  return null;
}

const newScene = (id, title, page, variant, direct, dialog = null) => ({ id, title, page, variant, dialog, group: page === 'settings' ? (dialog ? 'Security dialogs' : 'Settings') : 'Authentication', direct, changes: ['Keep account and server identity visible.', 'Show the distinct source state with an explicit consequence and next action.'], notes: 'Design proposal only. Sensitive actions require real service results; the preview never authenticates, mutates an account, or fabricates a completed operation.', baselineFilename: null });
export const authSettingsScenes = [
  newScene('register-email-verification', 'Continue registration by email', 'register', 'verification', ['auth.register.email-verification']),
  newScene('register-outcome-uncertain', 'Registration result unknown', 'register', 'uncertain', ['auth.register.uncertain']),
  newScene('login-methods', 'Choose a verification method', 'login', 'methods', []),
  newScene('login-unlock-pin', 'Unlock with a device PIN', 'login', 'unlock-pin', ['auth.login.unlock-pin']),
  newScene('settings-managed-policy', 'Organization-managed security settings', 'settings', 'policy', ['settings.timeout-policy', 'settings.pin-policy', 'settings.trusted-device']),
  newScene('settings-profile', 'Edit the account display name', 'settings', 'account', ['settings.profile.edit'], 'profile'),
  newScene('settings-pin-setup', 'Configure local PIN unlock', 'settings', 'security', ['settings.pin.setup'], 'pin-setup'),
  newScene('settings-disable-twofactor', 'Disable a two-step method', 'settings', 'security', ['settings.twofactor.disable'], 'disable-twofactor'),
  newScene('settings-totp-complete', 'Authenticator enabled and recovery code', 'settings', 'security', ['settings.totp.complete'], 'totp-complete'),
  newScene('settings-remove-security-key', 'Confirm security-key removal', 'settings', 'security', ['settings.webauthn.delete'], 'remove-security-key'),
  newScene('settings-delete-email', 'Confirm email before account deletion', 'settings', 'account', ['settings.delete.email'], 'delete-email'),
  newScene('settings-delete-credential', 'Final account deletion with password', 'settings', 'account', ['settings.delete.credential-password'], 'delete-credential')
];

export const authSettingsCss = `
.review-auth [hidden], .app-dialog [hidden] { display: none !important; }
.review-password { position: relative; min-width: 0; }
.review-password input { padding-right: 43px; }
.review-password > .icon-button { position: absolute; right: 3px; top: 25px; height: 32px; width: 34px; }
.review-account > div { min-width: 0; display: grid; gap: 4px; }
.review-account span:not(.avatar), .review-account small { overflow-wrap: anywhere; }
.review-account span:not(.avatar) { font-size: 12px; color: var(--sub); }
.review-account small { font-size: 12px; }
.review-account-context { display: grid; gap: 4px; padding: 12px 14px; border: 1px solid var(--line); border-radius: 6px; margin-bottom: 18px; overflow-wrap: anywhere; }
.review-account-context strong { font-size: 13px; }
.review-account-context span { font-size: 12px; color: var(--sub); }
.review-auth .auth-foot { font-size: 12px; overflow-wrap: anywhere; }
.review-auth .auth-form > .button + .button { margin-top: 8px; }
.review-register { width: 640px !important; }
.review-register .auth-body { padding: 26px 32px 22px; }
.review-register .auth-brand { width: 42px; height: 42px; }
.review-register .auth-subtitle { margin-bottom: 24px; }
.review-register-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 2px 18px; }
.review-register-grid .review-span-all { grid-column: 1 / -1; }
.review-saved-accounts { border: 1px solid var(--line); border-radius: 6px; margin-bottom: 18px; }
.review-saved-accounts summary { padding: 12px 14px; cursor: pointer; font-size: 13px; }
.review-saved-account { display: flex; align-items: center; gap: 10px; padding: 12px; width: 100%; border: 0; background: transparent; color: inherit; text-align: left; }
.review-saved-account > span:nth-child(2) { display: grid; gap: 4px; min-width: 0; font-size: 12px; overflow-wrap: anywhere; }
.review-saved-account small { color: var(--sub); }
.review-method-options { border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }
.review-method-options > button { padding: 14px; display: flex; align-items: center; justify-content: space-between; width: 100%; background: transparent; border: 0; border-bottom: 1px solid var(--line); color: inherit; text-align: left; }
.review-method-options > button:last-child { border-bottom: 0; }
.review-method-options span { display: grid; gap: 5px; }
.review-method-options small { color: var(--sub); }
.review-wait-status { padding: 20px 0; display: grid; justify-items: center; gap: 12px; text-align: center; }
.review-wait-status p { color: var(--sub); font-size: 13px; line-height: 1.5; }
.review-settings { padding-top: 26px; padding-bottom: 24px; }
.review-settings .page-heading { margin-bottom: 22px; }
.review-settings .settings-body > .section { margin-bottom: 16px; }
.review-settings .section-title { padding: 15px 18px 10px; }
.review-settings .setting-row { min-height: 60px; padding: 12px 18px; }
.review-settings .setting-row p { line-height: 1.35; }
.review-settings .setting-row > div { max-width: calc(100% - 150px); }
.review-settings .review-about { margin-top: 14px; }
.review-secondary-actions { display: flex; gap: 16px; flex-wrap: wrap; margin-top: 18px; }
.review-technical { margin: 10px 0 20px; color: var(--sub); font-size: 12px; }
.review-technical summary { cursor: pointer; }
.review-technical .setup-uri { overflow-wrap: anywhere; }
.review-summary-line { display: flex; align-items: center; justify-content: space-between; font-size: 13px; margin: 18px 0; }
.review-field-error { color: var(--danger); font-size: 12px; margin: 8px 0; }
@media(max-width: 680px) { .review-register-grid { grid-template-columns: 1fr; } .review-register { width: min(100%, 480px) !important; } .review-settings .setting-row > div { max-width: 70%; } }
`;

export const authSettingsScript = `
function reviewMessage(message) { let n=document.querySelector('.toast'); if(!n){n=document.createElement('div');n.className='toast';n.setAttribute('role','status');document.body.append(n);} n.textContent=message;n.classList.add('visible');setTimeout(()=>n.classList.remove('visible'),4000); }
function reviewRoute(id) { if(parent!==window) parent.postMessage({type:'wvw-review-navigate',id},'*'); else location.href=id+'.html?theme='+document.documentElement.dataset.theme; }
document.addEventListener('click', event => {
  const button=event.target.closest('button'); if(!button)return;
  if(button.hasAttribute('data-review-setup')){event.preventDefault();const dialog=button.closest('.app-dialog');const gate=dialog?.querySelector('.review-verification-gate');if(gate){dialog.querySelector('.method-list').hidden=true;dialog.querySelector('.review-secondary-actions').hidden=true;gate.hidden=false;dialog.querySelector('h2').textContent='Verify your identity';gate.querySelector('input')?.focus();}return;}
  if(button.hasAttribute('data-review-method')){event.preventDefault();const panel=document.querySelector('.review-method-detail');if(panel){panel.hidden=false;panel.querySelector('input')?.focus();}return;}
  if(button.hasAttribute('data-password-toggle')) { event.preventDefault();const input=button.parentElement.querySelector('input');const showing=input.type==='password';input.type=showing?'text':'password';button.setAttribute('aria-pressed',String(showing));button.setAttribute('aria-label',(showing?'Hide ':'Show ')+input.closest('label').querySelector('span').textContent.toLowerCase());return; }
  if(!button.hasAttribute('data-review-submit')&&!button.hasAttribute('data-review-next'))return;
  event.preventDefault();const scope=button.closest('.app-dialog')||button.closest('[data-review-form]')||button.parentElement;
  const fields=[...scope.querySelectorAll('input,select')].filter(x=>!x.disabled&&x.getClientRects().length);
  for(const input of fields){input.setCustomValidity('');if(input.hasAttribute('data-six-digit-code')){input.value=input.value.replace(/\\s/g,'');if(!/^[0-9]{6}$/.test(input.value))input.setCustomValidity('Enter exactly six digits.');}if(input.dataset.exactValue&&input.value!==input.dataset.exactValue)input.setCustomValidity('Enter the exact account email shown above.');}
  const newPassword=scope.querySelector('[data-password-new]'), confirm=scope.querySelector('[data-password-confirm]');if(newPassword&&confirm&&newPassword.value!==confirm.value)confirm.setCustomValidity('The confirmation does not match.');
  const invalid=fields.find(x=>!x.checkValidity());if(invalid){invalid.reportValidity();return;}
  if(button.dataset.reviewNext){reviewRoute(button.dataset.reviewNext);return;}
  reviewMessage('Design preview: no server request was sent. Review the next state using the gallery.');
}, true);
document.addEventListener('input', event=>{const input=event.target;if(input.matches('.review-password input,[data-exact-value]'))input.setCustomValidity('');if(input.matches('[data-six-digit-code]')){input.value=input.value.replace(/\\s/g,'');input.setCustomValidity('');}if(input.closest('[data-server-address]')){const footer=document.querySelector('[data-auth-server]');if(footer)footer.textContent=input.value;}});
document.addEventListener('change', event=>{
  if(event.target.matches('[data-server-select]')){const scope=event.target.closest('.auth-form');const custom=scope.querySelector('[data-server-address]');const input=custom?.querySelector('input');const isSelf=event.target.value==='self';if(custom)custom.hidden=!isSelf;if(input)input.disabled=!isSelf;const address=event.target.value==='eu'?'https://vault.bitwarden.eu':event.target.value==='us'?'https://vault.bitwarden.com':input?.value;const footer=document.querySelector('[data-auth-server]');if(footer)footer.textContent=address||'';}
  const input=event.target;if(input.type==='checkbox'&&input.getAttribute('aria-label')==='Show tray icon'){document.querySelectorAll('input[aria-label="Close to tray"],input[aria-label="Minimize to tray"]').forEach(x=>{x.disabled=!input.checked;const p=x.closest('.setting-row')?.querySelector('p');if(p){if(!p.dataset.enabledText)p.dataset.enabledText=p.textContent;p.textContent=input.checked?p.dataset.enabledText:'Turn on Show tray icon to configure this option.';}});}
});
`;
