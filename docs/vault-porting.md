# Vault import and encrypted backups

## Supported sources

The import screen offers eight explicit source formats. A CSV extension alone cannot identify its column schema; select the source application before choosing a CSV file.

| Source | Input | Preserved data and boundary |
| --- | --- | --- |
| Bitwarden | JSON | Supported native item types, folders, custom fields, password history and passkeys; encrypted JSON is decrypted in memory before preview. |
| Bitwarden | CSV | Login and secure-note records represented by the official CSV schema. Use JSON for other native types. |
| 1Password | `.1pux` version 3 | Bounded ZIP decoding entirely in memory, supported native categories, vault folders, extra fields, tags and history. Documents, file attachments, custom icons, archived items and unsupported value shapes reject the entire import. |
| KeePass 2 | Plaintext XML | Group paths, login fields, hidden custom fields, OTP parameters and password history. The recycle bin is excluded by UUID. Internal encrypted values, active binary attachments and unresolved references reject the entire import. |
| Chrome | CSV | The documented Chromium four- or five-column schema, including notes and exact credential whitespace. |
| Edge | CSV | The Chromium column family used by the official Bitwarden Edge importer. |
| Firefox | CSV | Credentials and the exported realm, action, GUID and timestamps, retained as native metadata or hidden custom fields. |
| LastPass | CSV | Credentials, notes, TOTP, folders, favorites and supported CLI metadata. Structured notes retain their complete original content as secure notes. Missing attachments or unknown nonempty columns reject the entire import. |

Files are limited to 64 MiB, and decoded text to 32 Mi characters. Individual codecs additionally bound entries, fields, nesting and archive expansion. XML DTD and external entity resolution are disabled. Archives are never extracted to disk. Unsupported data does not silently disappear and a parse failure makes no network request.

Preview and import add independent entries. Source item identifiers, encrypted item-key wrappers and permissions are not reused. This is necessary for restoring both legacy and V2 accounts, especially JSON backups that contain old item identifiers.

## Destination folders

The default preserves source folder relationships. Selecting a destination uses an existing folder from the currently unlocked personal vault and assigns every imported entry to it; source folders are then not created. The selected ID is frozen with the preview and revalidated against the current account immediately before submission.

Vaultwarden's `POST /api/ciphers/import` consumes an optional folder `id` and `folderRelationships` indexes. Existing owned IDs are reused by that endpoint. The implementation uses this actual server contract rather than putting an arbitrary destination directly on each cipher. Contract reference: Vaultwarden commit `fa2566d14fc745937ce104011475eca9e6c7a6f6`, `src/api/core/ciphers.rs`, `post_ciphers_import`.

After the server acknowledges the import, a refresh failure, cancellation, timeout or account switch retains the committed outcome. If a dispatched request instead loses its response, times out, is cancelled, or returns an HTTP failure, the result is explicitly uncertain: Vaultwarden creates folders and then writes entries sequentially, so even a validation error can follow a partial write. Both outcomes consume the preview; an uncertain result is not reported as success and cannot be submitted again from that preview. Pre-dispatch cancellation makes no request. Users must synchronize and inspect the vault before explicitly choosing the source file again. This does not make the server import endpoint idempotent.

## Encrypted exports

The export screen retains plaintext JSON and CSV and adds two official Bitwarden encrypted JSON formats:

- **Password protected:** portable across accounts, using a separate backup password. The complete plaintext JSON is encrypted with authenticated AES-256-CBC-HMAC. New exports use PBKDF2-SHA256 with 600,000 iterations and a random salt. The codec also reads supported Argon2id parameters and historical PBKDF2 backups from 5,000 iterations, subject to resource limits. A backup password must contain 12–1,024 characters for creation; reading accepts nonempty passwords up to 1,024 characters. Losing the password prevents recovery.
- **Account restricted:** encrypted fields and an account-key validation marker remain bound to the original account encryption key. Rotating that key can make old account-restricted exports unusable. Field ciphertext, folder names, key wrappers and blob envelopes must authenticate; unauthenticated legacy type 0 account backups are explicitly rejected. Restore such a legacy backup through the original client and create a password-protected backup before importing here.

Account-restricted files retain public item identifiers, dates and folder relationships as metadata. Their official per-field format does not provide a whole-document signature over that public metadata. Password-protected exports authenticate the complete encrypted JSON payload.

A legacy 32-byte account key has no MAC key. Account-restricted export rejects that key before producing a file, rather than writing a backup that authenticated import cannot restore. Password-protected export remains available for content already decrypted with such a legacy key.

Both export modes include active personal entries and archived entries. Organization entries, trash, attachment file contents and Send are excluded. Export policy restrictions apply to encrypted and plaintext formats alike. Master-password confirmation, session leases and transactional file writes remain required. Backup passwords and decrypted backup contents remain in process memory and are cleared from controls when the operation completes or is cancelled.

Format implementation references are pinned to Bitwarden clients commit `752719b07c6326f5cf8ea297573f2094f60d13e3`: `libs/tools/export-vault-core/src/services/base-vault-export.service.ts`, `individual-vault-export.service.ts`, the matching Bitwarden JSON importers and `libs/legacy-crypto/src/services/default-key-generation.service.ts`. The tests include an independently generated Node.js PBKDF2/HKDF/AES/HMAC fixture and reject field authentication downgrades.

## Verification

Codec, service and view-model tests use synthetic vaults. Verification is run on the designated remote environment. Real Windows file pickers, dialogs, XAML compilation and Native AOT publishing are separate platform checks; pure .NET tests are not evidence of those UI interactions.
