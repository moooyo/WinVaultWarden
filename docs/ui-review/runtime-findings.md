# Runtime UI Findings

These findings describe the native UI review session on 2026-09-15 against the isolated synthetic test environment. They record observed behavior and evidence. No application fixes are included in this review work. A screenshot documents one visible state; it does not establish complete behavior coverage or a root cause.

## 1. A login rate limit appeared as a credential failure

- **Observed:** The native login form displayed a credential-oriented authentication error when the dedicated backend returned HTTP 429.
- **Evidence:** [07-login-error.png](screenshots/07-login-error.png). The backend diagnosis and review-instance rate-limit change are recorded in [UiReviewSeed README](../../tools/UiReviewSeed/README.md).
- **Scope:** The initial fixture seeding exhausted the backend's default burst allowance. The dedicated review instance was reconfigured and restarted, after which sign-in continued. This observation does not establish that the submitted master password was incorrect.
- **Review impact:** The error presentation did not distinguish throttling from a credential problem in this observed response.

## 2. Password and TOTP remained absent after item reprompt

- **Observed:** A protected synthetic login initially hid sensitive fields, as shown in [09-login-detail-protected.png](screenshots/09-login-detail-protected.png). After the master-password prompt in [10-master-password-reprompt.png](screenshots/10-master-password-reprompt.png), notes, the password-history expander, and a masked custom recovery field became visible. The password row was still absent and TOTP still displayed a placeholder in [11-login-detail.png](screenshots/11-login-detail.png).
- **Source context:** `tools/UiReviewSeed/Program.cs:51` defines this fixture with a password and a valid-looking Base32 TOTP seed. `src/App/Services/VaultUiService.cs:603` and `:604` project the login password and TOTP when sensitive fields are authorized. `src/App/Controls/FieldRow.xaml.cs:115` collapses an empty field; `src/App/Controls/TotpField.xaml.cs:51` selects a code or placeholder according to whether the secret can be parsed.
- **Data verification:** The final read-only production-service check found `hasPassword=true`, `hasTotp=true`, `canViewPassword=true`, and `reprompt=true` for this item. It also confirmed username, password, and URI presence in the shared login whose fields appeared blank in [51-vault-organization.png](screenshots/51-vault-organization.png). See [backend-verification.json](backend-verification.json). Only field-presence booleans were emitted, never their contents.
- **Status:** The underlying data exists and decrypts successfully. The presentation failure is observed, but the exact projection, binding, or rendering cause remains unconfirmed.
- **Coverage impact:** These captures must not be described as successful password display or successful TOTP generation. The history expander is collapsed, so its records are not covered either.

## 3. Editing a login produced a blank right pane

- **Observed:** Selecting Edit for the protected Contoso login after reprompt left the right detail/editor pane blank. The vault list remained visible and responsive. No editor elements appeared in UI Automation during the observation, including after a wait of approximately ten seconds.
- **Evidence:** [13-editor-blank.png](screenshots/13-editor-blank.png).
- **Source context:** `src/App/Views/VaultPage.xaml:796` defines the deferred `CipherEditorPanel`; `src/App/ViewModels/VaultViewModel.cs:835` contains the edit-entry workflow. These identify the relevant code, not a confirmed cause.
- **Status:** Reproduction captured; root cause not established. This image is failure evidence related to `vault.editor.login`, not an image of a working editor.

## 4. Generator option controls were clipped

- **Observed:** At the captured default dialog size, the password tab cut off right-hand controls. The passphrase output and options were also clipped. The username tab showed option labels while the corresponding controls were outside the visible area.
- **Evidence:** [18-generator-password.png](screenshots/18-generator-password.png), [19-generator-passphrase.png](screenshots/19-generator-passphrase.png), and [20-generator-username.png](screenshots/20-generator-username.png).
- **Source context:** `src/App/Views/GeneratorDialog.xaml` defines the three tabs and their settings controls.
- **Scope:** The host window was 1165 by 745 pixels in these captures. Other window sizes, display scaling values, or themes were not established by these three screenshots. Generated values shown here are disposable samples, not account credentials.
- **Coverage impact:** All three tabs are visually represented, but their full option sets are not visible.

## 5. Expanding repeated-password results was followed by process exit

- **Observed:** The security-report overview rendered successfully. On one attempt to expand the repeated-password section, the native application process exited.
- **Evidence:** [22-security-report.png](screenshots/22-security-report.png) records the overview before that action. The review session recorded the following redacted diagnostic at **2026-09-15 12:25:19 UTC**: `UI: System.ArgumentException; HResult=0x80070057`.
- **Source context:** The repeated-password expander is defined at `src/App/Views/SecurityReportPage.xaml:182`.
- **Status:** One observed attempt. The screenshot does not show the exit itself, and the redacted exception does not establish the call stack or cause. No reproducibility rate or speculative implementation cause is claimed.
- **Coverage impact:** The repeated-password result body was not successfully captured in that attempt. The overview remains valid screenshot evidence.

## 6. Secure-note content was rendered twice

- **Observed:** The same synthetic note text appeared in the Content card and again in the common Notes section.
- **Evidence:** [45-note-detail.png](screenshots/45-note-detail.png).
- **Source context:** The secure-note projection assigns both `Notes` and `Content` from `cipher.Notes` in `src/App/Services/VaultUiService.cs:673` and `:686`. `src/App/Views/VaultPage.xaml:96` renders `NoteDetail.Content`, while the common notes section at `:1202` renders `Detail.Notes`.
- **Review impact:** The duplicate presentation increases page length and repeats identical information. This is visible presentation duplication, not evidence of duplicate stored records.

## 7. Entering Trash was followed by process exit

- **Observed:** After capturing the Archive view, selecting Trash caused the capture helper to report that the application had no screenshot target. A subsequent process check found no running `App` process. The client was restarted and returned to the locked account screen.
- **Evidence:** [53-vault-archive.png](screenshots/53-vault-archive.png) is the last captured state before the action. No successful Trash screenshot was obtained. The process check occurred at 2026-09-15 12:37:56 UTC, before restart.
- **Status:** One observed attempt. The redacted application log did not contain a new entry for this exit; no exception type or root cause is attributed to it.
- **Coverage impact:** Trash remains source-diagram coverage only. This observation is separate from the earlier security-report exit.

## Capture interpretation

The atlas includes initial registration and account-deletion dialogs without submitting those operations. Send-editor screenshots show only the upper viewport. Closed expanders, menus, and sidebar links do not count as views of their contents. The machine-readable [capture-coverage.json](capture-coverage.json) records visible coverage and related-but-failed or partial surfaces separately from the screenshot manifest.
