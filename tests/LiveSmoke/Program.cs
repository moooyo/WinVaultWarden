using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;

// ─────────────────────────────────────────────────────────────────────────────
// WinVaultWarden 实时冒烟测试：针对真实 Vaultwarden 服务端跑通
// 注册 → 登录 → 同步 → 建文件夹/条目 → 往返解密校验 → 更新 → 删除清理。
// 用我们自己的 Crypto/Api/Vault 代码完成全部步骤，验证协议与加密互通。
//
// 用法: dotnet run --project tests/LiveSmoke -- [serverUrl] [email] [password]
// 默认: http://10.0.1.20:8080  test@winvaultwarden.local  Test-Master-Password-1!
// ─────────────────────────────────────────────────────────────────────────────

string serverUrl = args.Length > 0 ? args[0]
    : Environment.GetEnvironmentVariable("WVW_LIVE_SERVER") ?? "http://10.0.1.20:8080";
string email = args.Length > 1 ? args[1] : "test@winvaultwarden.local";
string password = args.Length > 2 ? args[2] : "Test-Master-Password-1!";
const int Iterations = 600_000;

var run = "S" + DateTime.UtcNow.ToString("HHmmss");
int passed = 0, failed = 0;
var failures = new List<string>();

void Step(string name, bool ok, string? detail = null)
{
    if (ok) { passed++; Console.WriteLine($"  [PASS] {name}{(detail is null ? "" : $" — {detail}")}"); }
    else { failed++; failures.Add(name); Console.WriteLine($"  [FAIL] {name}{(detail is null ? "" : $" — {detail}")}"); }
}

Console.WriteLine($"=== WinVaultWarden Live Smoke ===");
Console.WriteLine($"server={serverUrl}  email={email}  run={run}");
Console.WriteLine();

// ── Build the production object graph (mirrors App ServiceConfiguration) ──────
var session = new VaultSession();
var crypto = new CryptoService();
var tokenStore = new MemoryTokenStore();

ITokenRefresher? refresher = null;
var authHandler = new AuthHeaderHandler(
    () => session.AccessToken,
    ct => refresher!.TryRefreshAsync(ct))
{
    InnerHandler = new HttpClientHandler(),
};
var http = new HttpClient(authHandler);
var api = new ApiClient(http);
refresher = new TokenRefreshService(api, session, tokenStore);

var decryptor = new VaultDecryptor(crypto);
var bootstrapper = new VaultBootstrapper(api, decryptor, session);
var auth = new AuthService(api, crypto, session, tokenStore, bootstrapper);
var sync = new SyncService(api, decryptor, session);
var encryptor = new CipherEncryptor(crypto);
var writeService = new VaultWriteService(api, encryptor, sync, session);

var sendCrypto = new SendCryptoService(crypto);
ISendApiClient sendApi = api;
var sendService = new SendService(sendApi, sendCrypto, session);
var sendWriteService = new SendWriteService(sendApi, sendCrypto, session);
var sendAccessService = new SendAccessService(sendApi, sendCrypto);

var attachmentCrypto = new AttachmentCryptoService(crypto);
IAttachmentApiClient attachmentApi = api;
var attachmentService = new AttachmentService(attachmentApi, attachmentCrypto, decryptor, session, sync);

IAccountApiClient accountApi = api;
var accountService = new AccountService(crypto, accountApi, session, tokenStore, auth);

try
{
    api.SetBaseAddress(serverUrl);

    // ── 1. Server config (deserializes ConfigResponse) ───────────────────────
    Console.WriteLine("[1] GET /api/config");
    try
    {
        var cfg = await api.GetConfigAsync();
        Step("config fetched", cfg is not null, $"version={cfg?.Version}");
    }
    catch (Exception ex) { Step("config fetched", false, ex.Message); }

    // ── 2. Register the test account (idempotent) ────────────────────────────
    Console.WriteLine("[2] POST /identity/accounts/register");
    await RegisterAsync();

    // ── 3. Login (prelogin + connect/token + bootstrap sync/devices) ─────────
    Console.WriteLine("[3] Login");
    var login = await auth.LoginAsync(serverUrl, email, password);
    Step("login success", login is AuthResult.Success, login.GetType().Name +
        (login is AuthResult.Failure f ? $": {f.Message}" : ""));
    if (login is not AuthResult.Success)
        throw new InvalidOperationException("Login failed; aborting remaining steps.");
    Step("session has user key", session.UserKey is not null);
    Step("session has access token", !string.IsNullOrEmpty(session.AccessToken));
    Step("account email matches", string.Equals(session.Account.Email, email, StringComparison.OrdinalIgnoreCase),
        session.Account.Email);

    // ── 4. Sync (authenticated GET /api/sync) ────────────────────────────────
    Console.WriteLine("[4] Sync");
    var ciphers0 = await sync.SyncAsync();
    Step("sync returned", true, $"{ciphers0.Count} ciphers, {session.Folders.Count} folders, {session.Devices.Count} devices");

    // ── 5. Create folder ─────────────────────────────────────────────────────
    Console.WriteLine("[5] Create folder");
    var folderName = $"WVW-Folder-{run}";
    await writeService.SaveFolderAsync(null, folderName);
    var folder = session.Folders.FirstOrDefault(x => x.Name == folderName);
    Step("folder created + decrypts round-trip", folder is not null, folder?.Id);

    // ── 6. Create login cipher with full field set ───────────────────────────
    Console.WriteLine("[6] Create login cipher");
    var cipherName = $"WVW-Login-{run}";
    var u = $"user-{run}";
    var p = $"pass-{run}!";
    var totp = "JBSWY3DPEHPK3PXP";
    var note = $"note-{run}";
    var fieldVal = $"field-{run}";
    var newCipher = new Cipher
    {
        Type = CipherType.Login,
        Name = cipherName,
        Notes = note,
        FolderId = folder?.Id,
        Login = new CipherLogin(u, p, totp, new[] { new CipherLoginUri("https://example.com", null) }),
        Fields = new[] { new CipherField("smoke-field", fieldVal, CipherFieldType.Text) },
    };
    await writeService.SaveCipherAsync(newCipher);

    // ── 7. Verify round-trip via a fresh sync ────────────────────────────────
    Console.WriteLine("[7] Verify cipher round-trip");
    var ciphers1 = await sync.SyncAsync();
    var created = ciphers1.FirstOrDefault(c => c.Name == cipherName);
    Step("cipher present after create", created is not null);
    if (created is not null)
    {
        Step("name round-trip", created.Name == cipherName, created.Name);
        Step("notes round-trip", created.Notes == note, created.Notes);
        Step("username round-trip", created.Login?.Username == u, created.Login?.Username);
        Step("password round-trip", created.Login?.Password == p, created.Login?.Password);
        Step("totp round-trip", created.Login?.Totp == totp, created.Login?.Totp);
        Step("uri round-trip", created.Login?.Uris.FirstOrDefault()?.Uri == "https://example.com",
            created.Login?.Uris.FirstOrDefault()?.Uri);
        Step("custom field round-trip", created.Fields.FirstOrDefault()?.Value == fieldVal,
            created.Fields.FirstOrDefault()?.Value);
        Step("folder assignment round-trip", created.FolderId == folder?.Id, created.FolderId);
    }

    // ── 8. Update the cipher (change password) ───────────────────────────────
    Console.WriteLine("[8] Update cipher");
    string? createdId = created?.Id;
    if (createdId is not null)
    {
        var p2 = $"pass2-{run}!";
        var updated = new Cipher
        {
            Id = createdId,
            Type = CipherType.Login,
            Name = cipherName,
            Notes = note,
            FolderId = folder?.Id,
            Login = new CipherLogin(u, p2, totp, new[] { new CipherLoginUri("https://example.com", null) }),
            Fields = newCipher.Fields,
        };
        await writeService.SaveCipherAsync(updated);
        var afterUpdate = (await sync.SyncAsync()).FirstOrDefault(c => c.Id == createdId);
        Step("password updated round-trip", afterUpdate?.Login?.Password == p2, afterUpdate?.Login?.Password);
    }
    else Step("password updated round-trip", false, "no cipher id");

    // ── 8b. Other cipher types (each has its own write/read DTO surface) ──────
    Console.WriteLine("[8b] Other cipher types");
    await TestCipherType("card",
        new Cipher
        {
            Type = CipherType.Card,
            Name = $"WVW-Card-{run}",
            Card = new CipherCard("John Doe", "4111111111111111", "12", "2030", "123", "Visa"),
        },
        c => (c.Card?.Number == "4111111111111111" && c.Card?.Code == "123" && c.Card?.CardholderName == "John Doe",
              $"num={c.Card?.Number} code={c.Card?.Code}"));
    await TestCipherType("identity",
        new Cipher
        {
            Type = CipherType.Identity,
            Name = $"WVW-Id-{run}",
            Identity = new CipherIdentity("Mr", "John", null, "Doe", "jdoe", null, null, null, null,
                "j@example.com", null, null, null, null, null, null, null, null),
        },
        c => (c.Identity?.FirstName == "John" && c.Identity?.LastName == "Doe" && c.Identity?.Email == "j@example.com",
              $"name={c.Identity?.FirstName} {c.Identity?.LastName}"));
    await TestCipherType("secure-note",
        new Cipher
        {
            Type = CipherType.SecureNote,
            Name = $"WVW-Note-{run}",
            Notes = $"secret-{run}",
            SecureNote = new CipherSecureNote(0),
        },
        c => (c.Notes == $"secret-{run}" && c.SecureNote is not null, c.Notes));
    await TestCipherType("ssh-key",
        new Cipher
        {
            Type = CipherType.SshKey,
            Name = $"WVW-Ssh-{run}",
            Ssh = new CipherSsh("-----PRIVATE-----", "ssh-ed25519 AAAAC3Nz", "SHA256:abc123"),
        },
        c => (c.Ssh?.PublicKey == "ssh-ed25519 AAAAC3Nz" && c.Ssh?.PrivateKey == "-----PRIVATE-----"
              && c.Ssh?.Fingerprint == "SHA256:abc123", $"pub={c.Ssh?.PublicKey}"));

    // ── 9. Soft delete + restore ─────────────────────────────────────────────
    Console.WriteLine("[9] Soft delete + restore");
    if (createdId is not null)
    {
        await writeService.DeleteCipherAsync(createdId, permanent: false);
        var soft = (await sync.SyncAsync()).FirstOrDefault(c => c.Id == createdId);
        Step("soft-deleted (IsDeleted)", soft?.IsDeleted == true);
        await writeService.RestoreCipherAsync(createdId);
        var restored = (await sync.SyncAsync()).FirstOrDefault(c => c.Id == createdId);
        Step("restored (not deleted)", restored is not null && !restored.IsDeleted);
    }

    // ── 10. Cleanup: hard delete cipher + delete folder ──────────────────────
    Console.WriteLine("[10] Cleanup");
    if (createdId is not null)
    {
        await writeService.DeleteCipherAsync(createdId, permanent: true);
        var gone = (await sync.SyncAsync()).All(c => c.Id != createdId);
        Step("cipher hard-deleted", gone);
    }
    if (folder is not null)
    {
        await writeService.DeleteFolderAsync(folder.Id);
        var folderGone = session.Folders.All(x => x.Id != folder.Id);
        Step("folder deleted", folderGone);
    }

    // ── 11. Send: text create → list → access → update → remove-password ──────
    Console.WriteLine("[11] Send (text)");
    string serverForShare = session.Account.ServerUrl;
    var sendName = $"WVW-Send-{run}";
    var sendNotes = $"send-notes-{run}";
    var sendBody = $"send-secret-text-{run}";
    var sendPassword = $"send-pw-{run}!";
    var sendDeletion = DateTimeOffset.UtcNow.AddDays(7);

    var textDraft = new SendDraftModel
    {
        Id = null,
        Type = SendType.Text,
        Name = sendName,
        Notes = sendNotes,
        TextContent = sendBody,
        TextHidden = false,
        MaxAccessCount = null,
        ExpirationDate = null,
        DeletionDate = sendDeletion,
        Disabled = false,
        HideEmail = false,
        Password = sendPassword,
    };
    await sendWriteService.SaveTextSendAsync(textDraft);

    var sendsAfterCreate = await sendService.GetSendsAsync();
    var createdSend = sendsAfterCreate.FirstOrDefault(s => s.Name == sendName);
    Step("send: text create + list shows it", createdSend is not null, createdSend?.Id);

    if (createdSend is not null)
    {
        Step("send: type is Text", createdSend.Type == SendType.Text, createdSend.Type.ToString());
        Step("send: name round-trip", createdSend.Name == sendName, createdSend.Name);
        Step("send: notes round-trip", createdSend.Notes == sendNotes, createdSend.Notes);
        Step("send: has password flag", createdSend.HasPassword, $"hasPassword={createdSend.HasPassword}");

        // Access through the access service using a canonical share URL built from the
        // Send's accessId + its wrapped seed (see BuildShareUrlFromSession at end of file).
        var accessed = await sendAccessService.AccessAsync(
            BuildShareUrlFromSession(createdSend, serverForShare, sendCrypto, sendService, session),
            sendPassword);
        Step("send: access text round-trip", accessed.TextContent == sendBody, accessed.TextContent);
        Step("send: access name round-trip", accessed.Name == sendName, accessed.Name);
        // Notes は to_json_access では返却されない(オーナー向け to_json のみ)。
        // アクセスレスポンスは null になる — ここでは省略してテスト対象外とする。

        // Update: change the name.
        var newName = sendName + "-v2";
        var updateDraft = new SendDraftModel
        {
            Id = createdSend.Id,
            Type = SendType.Text,
            Name = newName,
            Notes = sendNotes,
            TextContent = sendBody,
            TextHidden = false,
            MaxAccessCount = null,
            ExpirationDate = null,
            DeletionDate = sendDeletion,
            Disabled = false,
            HideEmail = false,
            Password = null, // keep existing password
        };
        await sendWriteService.SaveTextSendAsync(updateDraft);
        var afterUpdate = (await sendService.GetSendsAsync()).FirstOrDefault(s => s.Id == createdSend.Id);
        Step("send: name updated round-trip", afterUpdate?.Name == newName, afterUpdate?.Name);

        // Remove password, then re-access WITHOUT a password.
        var depassed = await sendWriteService.RemovePasswordAsync(createdSend.Id);
        Step("send: password removed flag", !depassed.HasPassword, $"hasPassword={depassed.HasPassword}");
        var reaccessed = await sendAccessService.AccessAsync(
            BuildShareUrlFromSession(depassed, serverForShare, sendCrypto, sendService, session),
            password: null);
        Step("send: access without password OK", reaccessed.TextContent == sendBody, reaccessed.TextContent);
    }

    // ── 12. Send: file create → upload → access + download → decrypt → delete ─
    Console.WriteLine("[12] Send (file)");
    var fileSendName = $"WVW-SendFile-{run}";
    var fileName = $"smoke-{run}.bin";
    var filePayload = RandomNumberGenerator.GetBytes(2048);
    var fileDraft = new SendDraftModel
    {
        Id = null,
        Type = SendType.File,
        Name = fileSendName,
        Notes = null,
        TextContent = null,
        TextHidden = false,
        FileName = fileName,
        MaxAccessCount = null,
        ExpirationDate = null,
        DeletionDate = sendDeletion,
        Disabled = false,
        HideEmail = false,
        Password = null,
    };
    await sendWriteService.SaveFileSendAsync(fileDraft, filePayload);

    var fileSend = (await sendService.GetSendsAsync()).FirstOrDefault(s => s.Name == fileSendName);
    Step("send: file create + list shows it", fileSend is not null, fileSend?.Id);

    if (fileSend is not null)
    {
        Step("send: file type is File", fileSend.Type == SendType.File, fileSend.Type.ToString());
        var accessedFile = await sendAccessService.AccessAsync(
            BuildShareUrlFromSession(fileSend, serverForShare, sendCrypto, sendService, session),
            password: null);
        var downloaded = await sendAccessService.DownloadFileAsync(accessedFile);
        Step("send: file bytes round-trip", downloaded.AsSpan().SequenceEqual(filePayload),
            $"{downloaded.Length} bytes vs {filePayload.Length}");
    }

    // ── 13. Cleanup: delete both sends ───────────────────────────────────────
    Console.WriteLine("[13] Send cleanup");
    if (createdSend is not null)
    {
        await sendWriteService.DeleteSendAsync(createdSend.Id);
        var textGone = (await sendService.GetSendsAsync()).All(s => s.Id != createdSend.Id);
        Step("send: text deleted", textGone);
    }
    if (fileSend is not null)
    {
        await sendWriteService.DeleteSendAsync(fileSend.Id);
        var fileGone = (await sendService.GetSendsAsync()).All(s => s.Id != fileSend.Id);
        Step("send: file deleted", fileGone);
    }

    // ── 14. Attachment: create cipher → upload (中文 name) → sync → name decrypts
    //        → download byte-for-byte → delete → cleanup ───────────────────────
    Console.WriteLine("[14] Attachment (file)");
    var attCipherName = $"WVW-Att-{run}";
    var attCipher = new Cipher
    {
        Type = CipherType.Login,
        Name = attCipherName,
        Login = new CipherLogin($"att-user-{run}", $"att-pass-{run}!", null,
            new[] { new CipherLoginUri("https://attachments.example.com", null) }),
    };
    await writeService.SaveCipherAsync(attCipher);

    var attHost = (await sync.SyncAsync()).FirstOrDefault(c => c.Name == attCipherName);
    Step("attachment: host cipher created", attHost is not null, attHost?.Id);

    if (attHost is not null)
    {
        var attFileName = $"附件-测试-{run}.bin";
        var attPayload = RandomNumberGenerator.GetBytes(4096);

        var afterUpload = await attachmentService.UploadAsync(attHost.Id, attFileName, attPayload);
        var uploaded = afterUpload.FirstOrDefault(a => a.FileName == attFileName);
        Step("attachment: upload + list shows it", uploaded is not null,
            $"{afterUpload.Count} attachment(s), id={uploaded?.Id}");

        // Re-sync整库,确认附件随 GET /api/sync 回到 session,且文件名解密一致。
        var resynced = (await sync.SyncAsync()).FirstOrDefault(c => c.Id == attHost.Id);
        var synced = resynced?.Attachments.FirstOrDefault(a => a.Id == uploaded?.Id);
        Step("attachment: present after full re-sync", synced is not null,
            $"{resynced?.Attachments.Count ?? 0} on cipher");
        Step("attachment: filename decrypts round-trip (中文)", synced?.FileName == attFileName,
            synced?.FileName);
        Step("attachment: size > 0", synced is not null && synced.Size > 0, synced?.Size.ToString());

        if (uploaded is not null)
        {
            // Download → must equal the original plaintext byte-for-byte.
            var downloaded = await attachmentService.DownloadAsync(attHost.Id, uploaded.Id);
            Step("attachment: download byte-for-byte equal",
                downloaded.AsSpan().SequenceEqual(attPayload),
                $"{downloaded.Length} bytes vs {attPayload.Length}");

            // Delete the attachment → it disappears from the cipher.
            var afterDelete = await attachmentService.DeleteAsync(attHost.Id, uploaded.Id);
            Step("attachment: deleted (not listed)", afterDelete.All(a => a.Id != uploaded.Id),
                $"{afterDelete.Count} remaining");
        }

        // Cleanup: hard-delete the host cipher.
        await writeService.DeleteCipherAsync(attHost.Id, permanent: true);
        var hostGone = (await sync.SyncAsync()).All(c => c.Id != attHost.Id);
        Step("attachment: host cipher hard-deleted", hostGone);
    }

    // ── 15. Account: rename → revert ─────────────────────────────────────────
    Console.WriteLine("[15] Account: rename → revert");
    await AccountRenameAndRevertAsync();

    // ── 16. Account: change password → re-login → change back → re-login ─────
    Console.WriteLine("[16] Account: change password → revert");
    await AccountChangePasswordAndRevertAsync();

    // ── 17. Account: change KDF iterations 600000→700000 → re-login → revert ─
    Console.WriteLine("[17] Account: change KDF iterations → revert");
    await AccountChangeKdfAndRevertAsync();
}
catch (Exception ex)
{
    failed++;
    Console.WriteLine();
    Console.WriteLine($"[ABORT] Unhandled: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}

Console.WriteLine();
Console.WriteLine($"=== Summary: {passed} passed, {failed} failed ===");
if (failures.Count > 0)
    Console.WriteLine("Failed: " + string.Join("; ", failures));
return failed == 0 ? 0 : 1;

// ── Create a cipher of a given type, re-sync, find it, verify, then hard-delete.
async Task TestCipherType(string label, Cipher cipher, Func<Cipher, (bool ok, string? detail)> verify)
{
    await writeService.SaveCipherAsync(cipher);
    var found = (await sync.SyncAsync()).FirstOrDefault(c => c.Name == cipher.Name);
    if (found is null) { Step($"{label} round-trip", false, "not found after create"); return; }
    var (ok, detail) = verify(found);
    Step($"{label} round-trip", ok, detail);
    await writeService.DeleteCipherAsync(found.Id, permanent: true);
}

// ── Registration helper (client has no register feature; build the payload
//    with our own crypto, exactly as the Bitwarden web vault would). ──────────
async Task RegisterAsync()
{
    var masterKey = crypto.DeriveMasterKey(password, email, KdfType.Pbkdf2, Iterations, null, null);
    var passwordHash = crypto.ComputeMasterPasswordHash(masterKey, password);
    var stretched = crypto.StretchMasterKey(masterKey);

    var userKeyBytes = RandomNumberGenerator.GetBytes(64);
    var userKey = new SymmetricCryptoKey(userKeyBytes);
    var protectedKey = crypto.Encrypt(userKeyBytes, stretched).ToString();

    using var rsa = RSA.Create(2048);
    var publicKeyB64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    var encryptedPrivateKey = crypto.Encrypt(rsa.ExportPkcs8PrivateKey(), userKey).ToString();

    var payload = new Dictionary<string, object?>
    {
        ["email"] = email,
        ["name"] = "WVW Smoke",
        ["masterPasswordHash"] = passwordHash,
        ["key"] = protectedKey,
        ["kdf"] = 0,
        ["kdfIterations"] = Iterations,
        ["keys"] = new Dictionary<string, object?>
        {
            ["publicKey"] = publicKeyB64,
            ["encryptedPrivateKey"] = encryptedPrivateKey,
        },
    };
    var json = JsonSerializer.Serialize(payload);

    using var plain = new HttpClient();
    using var req = new HttpRequestMessage(HttpMethod.Post, serverUrl.TrimEnd('/') + "/identity/accounts/register")
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    var resp = await plain.SendAsync(req);
    var body = await resp.Content.ReadAsStringAsync();

    if (resp.IsSuccessStatusCode)
        Step("account registered", true);
    else if (body.Contains("already exists", StringComparison.OrdinalIgnoreCase)
             || body.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        Step("account already exists (reuse)", true, $"{(int)resp.StatusCode}");
    else
        Step("account registered", false, $"{(int)resp.StatusCode}: {body}");
}

// ── Account: rename to "WinVault SmokeTest" then revert to a stable name. ───
async Task AccountRenameAndRevertAsync()
{
    const string stableName = "WVW Smoke";
    const string tempName = "WinVault SmokeTest";
    try
    {
        await accountService.UpdateNameAsync(tempName);
        Step("account: rename to temp name", true, tempName);
        await accountService.UpdateNameAsync(stableName);
        Step("account: revert to stable name", true, stableName);
    }
    catch (Exception ex)
    {
        Step("account: rename round-trip", false, $"{ex.GetType().Name}: {ex.Message}");
        // best-effort revert
        try { await accountService.UpdateNameAsync(stableName); } catch { /* ignore */ }
    }
}

// ── Account: change password → re-login with temp → change back → re-login. ─
// On any failure, always attempt to restore the original password so the account
// stays reusable for subsequent test runs.
async Task AccountChangePasswordAndRevertAsync()
{
    const string tempPassword = "Temp-Smoke-Password-2!";
    bool changedToTemp = false;
    try
    {
        // Change to temp password (forces logout)
        await accountService.ChangePasswordAsync(password, tempPassword, null);
        changedToTemp = true;
        Step("account: change to temp password (forced logout)", true);

        // Re-login with temp password
        var loginTemp = await auth.LoginAsync(serverUrl, email, tempPassword);
        Step("account: re-login with temp password", loginTemp is AuthResult.Success, loginTemp.GetType().Name);
        if (loginTemp is not AuthResult.Success)
            throw new InvalidOperationException("Re-login with temp password failed");

        // Change back to original password (forces logout again)
        await accountService.ChangePasswordAsync(tempPassword, password, null);
        changedToTemp = false;
        Step("account: revert to original password (forced logout)", true);

        // Re-login with original password to confirm
        var loginOrig = await auth.LoginAsync(serverUrl, email, password);
        Step("account: re-login with original password", loginOrig is AuthResult.Success, loginOrig.GetType().Name);
        if (loginOrig is not AuthResult.Success)
            throw new InvalidOperationException("Re-login with original password failed");
    }
    catch (Exception ex)
    {
        Step("account: change-password round-trip", false, $"{ex.GetType().Name}: {ex.Message}");
        // Attempt to restore: if we changed to temp, we need to re-authenticate and change back.
        if (changedToTemp)
        {
            try
            {
                var loginTemp2 = await auth.LoginAsync(serverUrl, email, tempPassword);
                if (loginTemp2 is AuthResult.Success)
                {
                    await accountService.ChangePasswordAsync(tempPassword, password, null);
                    await auth.LoginAsync(serverUrl, email, password);
                    Console.WriteLine("  [INFO] account: original password restored after failure");
                }
            }
            catch (Exception restoreEx)
            {
                Console.WriteLine($"  [WARN] account: could not restore original password: {restoreEx.Message}");
            }
        }
    }
}

// ── Account: change KDF iterations 600000→700000, re-login, revert to 600000. ─
async Task AccountChangeKdfAndRevertAsync()
{
    const int origIterations = 600_000;
    const int newIterations = 700_000;
    bool changedKdf = false;
    try
    {
        // Change KDF to 700000 (forces logout)
        await accountService.ChangeKdfAsync(password, newIterations);
        changedKdf = true;
        Step("account: change KDF iterations to 700000 (forced logout)", true);

        // Re-login with original password (server now uses 700000 iterations for key derivation)
        var loginNew = await auth.LoginAsync(serverUrl, email, password);
        Step("account: re-login after KDF change", loginNew is AuthResult.Success, loginNew.GetType().Name);
        if (loginNew is not AuthResult.Success)
            throw new InvalidOperationException("Re-login after KDF change failed");

        // Revert KDF to 600000 (forces logout again)
        await accountService.ChangeKdfAsync(password, origIterations);
        changedKdf = false;
        Step("account: revert KDF iterations to 600000 (forced logout)", true);

        // Re-login with original password at original KDF to confirm
        var loginOrig = await auth.LoginAsync(serverUrl, email, password);
        Step("account: re-login after KDF revert", loginOrig is AuthResult.Success, loginOrig.GetType().Name);
        if (loginOrig is not AuthResult.Success)
            throw new InvalidOperationException("Re-login after KDF revert failed");
    }
    catch (Exception ex)
    {
        Step("account: change-KDF round-trip", false, $"{ex.GetType().Name}: {ex.Message}");
        // Attempt to restore KDF if we changed it but failed to revert.
        if (changedKdf)
        {
            try
            {
                // Try logging in (KDF is at 700000), then revert
                var loginNew2 = await auth.LoginAsync(serverUrl, email, password);
                if (loginNew2 is AuthResult.Success)
                {
                    await accountService.ChangeKdfAsync(password, origIterations);
                    await auth.LoginAsync(serverUrl, email, password);
                    Console.WriteLine("  [INFO] account: KDF iterations restored after failure");
                }
            }
            catch (Exception restoreEx)
            {
                Console.WriteLine($"  [WARN] account: could not restore KDF iterations: {restoreEx.Message}");
            }
        }
    }
}

// ── Build a canonical Send share URL from a Send the smoke test just created.
//    Recovers the 16-byte seed by unwrapping the Send's `key` field with the
//    session user key, exactly as a real client opening its own Send would. ──
string BuildShareUrlFromSession(
    Core.Models.Send send, string serverBase, SendCryptoService sc, ISendService svc, VaultSession sess)
{
    // Re-read the raw DTO so we can access the wrapped `key` (the Core.Models.Send
    // projection intentionally drops it). The sendApi field is in scope here.
    var raw = sendApi.GetSendsAsync().GetAwaiter().GetResult();
    var dto = raw.Data.First(d => d.Id == send.Id);
    var seed = sc.UnwrapSeed(dto.Key!, sess.UserKey!);
    return sc.BuildShareUrl(serverBase, send.AccessId, seed);
}
