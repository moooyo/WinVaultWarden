using System.Security.Cryptography;
using System.Threading.Channels;
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

ITwoFactorApiClient twoFactorApi = api;
var twoFactorService = new TwoFactorService(crypto, twoFactorApi, tokenStore);

IAuthRequestApiClient authRequestApi = api;
var authRequestService = new AuthRequestService(authRequestApi, crypto, session, tokenStore);

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

    // ── 9b. Bulk operations ─────────────────────────────────────────────────
    Console.WriteLine("[9b] Bulk operations");
    {
        var bulkFolderName = $"WVW-Bulk-{run}";
        await writeService.SaveFolderAsync(null, bulkFolderName);
        var bulkFolder = session.Folders.FirstOrDefault(x => x.Name == bulkFolderName);

        var bulkIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var c = new Cipher
            {
                Type = CipherType.Login,
                Name = $"WVW-Bulk-Item-{run}-{i}",
                Login = new CipherLogin($"user{i}", "pw", null, Array.Empty<CipherLoginUri>()),
            };
            await writeService.SaveCipherAsync(c);
        }
        var afterCreate = await sync.SyncAsync();
        bulkIds.AddRange(afterCreate
            .Where(c => c.Name.StartsWith($"WVW-Bulk-Item-{run}-", StringComparison.Ordinal))
            .Select(c => c.Id));
        Step("bulk: 3 items created", bulkIds.Count == 3, $"{bulkIds.Count} ids");

        // 批量移动到文件夹
        await writeService.MoveCiphersAsync(bulkIds, bulkFolder?.Id);
        var afterMove = await sync.SyncAsync();
        var moved = afterMove.Where(c => bulkIds.Contains(c.Id)).ToList();
        var movedOk = moved.Count == 3 && moved.All(c => c.FolderId == bulkFolder?.Id);
        Step("bulk: moved to folder", movedOk, bulkFolder?.Id);

        // 批量软删
        await writeService.DeleteCiphersAsync(bulkIds, permanent: false);
        var afterSoft = await sync.SyncAsync();
        var soft = afterSoft.Where(c => bulkIds.Contains(c.Id)).ToList();
        var softOk = soft.Count == 3 && soft.All(c => c.IsDeleted);
        Step("bulk: soft-deleted to trash", softOk);

        // 批量恢复
        await writeService.RestoreCiphersAsync(bulkIds);
        var afterRestore = await sync.SyncAsync();
        var restored = afterRestore.Where(c => bulkIds.Contains(c.Id)).ToList();
        var restoreOk = restored.Count == 3 && restored.All(c => !c.IsDeleted);
        Step("bulk: restored", restoreOk);

        // 批量硬删清理 + 删文件夹
        await writeService.DeleteCiphersAsync(bulkIds, permanent: true);
        var afterHard = await sync.SyncAsync();
        var hardOk = afterHard.All(c => !bulkIds.Contains(c.Id));
        Step("bulk: hard-deleted cleanup", hardOk);

        if (bulkFolder is not null)
        {
            await writeService.DeleteFolderAsync(bulkFolder.Id);
            await sync.SyncAsync();
            Step("bulk: folder cleanup", session.Folders.All(x => x.Id != bulkFolder.Id));
        }
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

    // ── 18. Two-Factor: TOTP enable → verify → disable (account stays clean) ─
    Console.WriteLine("[18] Two-Factor: TOTP enable → verify → disable");
    await TwoFactorTotpRoundTripAsync();

    // ── 19. Auth-request: RSA round-trip ────────────────────────────────────
    Console.WriteLine("[19] Auth-request: RSA round-trip");
    await AuthRequestRoundTripAsync();

    // ── 20. Notifications: WebSocket push round-trip ─────────────────────────
    Console.WriteLine("[20] Notifications: WebSocket push round-trip");
    await NotificationsPushRoundTripAsync();

    // ── 21. Emergency Access: dual-account E2E ───────────────────────────────
    Console.WriteLine("[21] Emergency Access: dual-account E2E");
    await EmergencyAccessRoundTripAsync();

    // ── pwh. Password history: two password changes → 2 entries, newest-first ──
    Console.WriteLine("[pwh] Password history");
    {
        var name = $"WVW-PwHist-{run}";
        await writeService.SaveCipherAsync(new Cipher
        {
            Type = CipherType.Login, Name = name,
            Login = new CipherLogin("u", "passA", null, Array.Empty<CipherLoginUri>()),
        });
        var c1 = (await sync.SyncAsync()).First(c => c.Name == name);

        // 改密码 A→B：前插旧密码 A
        await writeService.SaveCipherAsync(new Cipher
        {
            Id = c1.Id, Type = CipherType.Login, Name = name,
            CreationDate = c1.CreationDate, RevisionDate = c1.RevisionDate,
            Login = new CipherLogin("u", "passB", null, Array.Empty<CipherLoginUri>()),
            PasswordHistory = new[] { new PasswordHistoryEntry("passA", DateTimeOffset.UtcNow) },
        });
        var c2 = (await sync.SyncAsync()).First(c => c.Id == c1.Id);
        Step("pwh: 1 entry after first change", c2.PasswordHistory.Count == 1 && c2.PasswordHistory[0].Password == "passA");

        // 改密码 B→C：前插 B，保留 A
        await writeService.SaveCipherAsync(new Cipher
        {
            Id = c1.Id, Type = CipherType.Login, Name = name,
            CreationDate = c1.CreationDate, RevisionDate = c2.RevisionDate,
            Login = new CipherLogin("u", "passC", null, Array.Empty<CipherLoginUri>()),
            PasswordHistory = new[]
            {
                new PasswordHistoryEntry("passB", DateTimeOffset.UtcNow),
                new PasswordHistoryEntry("passA", c2.PasswordHistory[0].LastUsedDate ?? DateTimeOffset.UtcNow),
            },
        });
        var c3 = (await sync.SyncAsync()).First(c => c.Id == c1.Id);
        Step("pwh: 2 entries newest-first after second change",
            c3.PasswordHistory.Count == 2 && c3.PasswordHistory[0].Password == "passB" && c3.PasswordHistory[1].Password == "passA");

        await writeService.DeleteCipherAsync(c1.Id, permanent: true);  // 清理(方法名以现有为准)
        Step("pwh: cleanup", (await sync.SyncAsync()).All(c => c.Id != c1.Id));
    }

    // ── 22. Vault health ──────────────────────────────────────────────────────
    Console.WriteLine("[health] Vault health reports");
    {
        var evaluator = new Crypto.PasswordStrength.PasswordStrengthEvaluator(
            new Crypto.PasswordStrength.Omnimatch(
                new Crypto.PasswordStrength.DictionaryMatcher(Crypto.PasswordStrength.FrequencyDictionaries.Load())));
        var pwned = new PwnedPasswordsClient(new HttpClient());
        IVaultService vaultService = new VaultService(session);
        var health = new VaultHealthService(vaultService, evaluator, pwned);

        await sync.SyncAsync();
        var report = health.AnalyzeOffline();
        Step("health: AnalyzeOffline ran", report is not null,
            $"reused={report.Reused.Count} weak={report.Weak.Count} unsecured={report.Unsecured.Count}");

        var leaked = await pwned.GetBreachCountAsync("password");
        Step("health: HIBP flags known-exposed 'password'", leaked > 0, $"count={leaked}");

        var clean = await pwned.GetBreachCountAsync($"WVW-unique-{run}-{Guid.NewGuid():N}");
        Step("health: HIBP clears a random strong password", clean == 0);
    }

    // ── 23. Website icons ─────────────────────────────────────────────────────
    Console.WriteLine("[icons] Website favicon");
    {
        var domain = Core.IconDomain.Extract("https://github.com");
        Step("icons: IconDomain.Extract", domain == "github.com", domain);

        var favicons = new FaviconCache(new HttpClient(), session);
        byte[]? bytes = null;
        var ok = true;
        try { bytes = await favicons.GetAsync(domain!); }
        catch { ok = false; }
        Step("icons: /icons request completes without throwing", ok, bytes is null ? "no image (glyph fallback)" : $"{bytes.Length} bytes");
    }

    // ── 24. Import / Export ───────────────────────────────────────────────────
    Console.WriteLine("[io] Import / Export");
    {
        var importSvc = new VaultImportService(api, encryptor, sync, session);
        var exportSvc = new VaultExportService(new VaultService(session));
        var ioFolderName = $"WVW-IO-{run}";
        var ioLoginName = $"WVW-IO-Login-{run}";
        var ioJson = "{\"encrypted\":false,\"folders\":[{\"id\":\"f0\",\"name\":\"" + ioFolderName + "\"}],\"items\":[" +
                   "{\"type\":1,\"name\":\"" + ioLoginName + "\",\"folderId\":\"f0\",\"login\":{\"username\":\"u\",\"password\":\"io-pass\",\"uris\":[]}}]}";

        var ioImportedCount = await importSvc.ImportAsync(Core.Services.ImportFormat.Json, ioJson);
        Step("io: import returned 1", ioImportedCount == 1, $"n={ioImportedCount}");

        var ioAfter = await sync.SyncAsync();
        var ioImported = ioAfter.FirstOrDefault(c => c.Name == ioLoginName);
        var ioFolder = session.Folders.FirstOrDefault(f => f.Name == ioFolderName);
        Step("io: imported login decrypts + in folder",
            ioImported is not null && ioImported.Login?.Password == "io-pass"
            && ioFolder is not null && ioImported.FolderId == ioFolder.Id,
            ioImported?.Login?.Password);

        var ioExport = exportSvc.Export(Core.Services.ExportFormat.Json);
        var ioReparsed = Vault.Porting.BitwardenJsonCodec.Parse(ioExport);
        Step("io: export round-trips through codec", ioReparsed.Ciphers.Any(c => c.Name == ioLoginName));

        // cleanup: hard-delete imported cipher + delete folder
        if (ioImported is not null) await writeService.DeleteCipherAsync(ioImported.Id, permanent: true);
        if (ioFolder is not null) await writeService.DeleteFolderAsync(ioFolder.Id);
        Step("io: cleanup", true);
    }

    // ── 25. PIN unlock: set PIN → lock → unlock with PIN → wrong PIN fails ────
    Console.WriteLine("[pin] PIN unlock");
    {
        var pinSvc = new PinService(crypto, session, tokenStore);
        var beforeKey = session.UserKey!.FullKey.ToArray();   // snapshot the unlocked UserKey bytes

        pinSvc.SetPin("smoke-pin-1234");
        Step("pin: IsPinSet after SetPin", pinSvc.IsPinSet);

        session.Lock();
        Step("pin: locked before unlock", session.UserKey is null);

        var pinUnlock = await auth.UnlockWithPinAsync("smoke-pin-1234");
        Step("pin: UnlockWithPin succeeds", pinUnlock is AuthResult.Success, pinUnlock.GetType().Name +
            (pinUnlock is AuthResult.Failure pf ? $": {pf.Message}" : ""));
        Step("pin: same UserKey recovered",
            session.UserKey is not null && session.UserKey.FullKey.AsSpan().SequenceEqual(beforeKey));

        // Verify a real cipher decrypts under the PIN-recovered UserKey (re-sync + read).
        var afterPinUnlock = await sync.SyncAsync();
        Step("pin: sync works after PIN unlock", true, $"{afterPinUnlock.Count} ciphers");

        session.Lock();
        var wrongPin = await auth.UnlockWithPinAsync("wrong-pin-9999");
        Step("pin: wrong PIN fails", wrongPin is AuthResult.Failure, wrongPin.GetType().Name);
        Step("pin: still locked after wrong PIN", session.UserKey is null);

        // Re-unlock with the correct PIN so the account/session are left usable, then clear.
        var relock = await auth.UnlockWithPinAsync("smoke-pin-1234");
        Step("pin: re-unlock with correct PIN after failure", relock is AuthResult.Success, relock.GetType().Name);

        pinSvc.ClearPin();
        Step("pin: cleared", !pinSvc.IsPinSet);
    }
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

// ── Registration helper: uses production Vault.RegisterService path ──────────
async Task RegisterAsync()
{
    var registerService = new Vault.RegisterService(crypto, api);
    try
    {
        await registerService.RegisterAsync(serverUrl, email, "WVW Smoke", password, null);
        Step("account registered", true);
    }
    catch (Core.Services.RegistrationException ex)
        when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
    {
        Step("account already exists (reuse)", true);
    }
    catch (Core.Services.RegistrationException ex)
    {
        Step("account registered", false, ex.Message);
    }
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

// ── Two-Factor: TOTP setup → enable → verify providers list → disable → clean ─
// Account must be left without TOTP so subsequent test runs start clean.
async Task TwoFactorTotpRoundTripAsync()
{
    bool totpEnabled = false;
    try
    {
        // Step 1: Begin TOTP setup — get secret from server
        var (secret, otpauth) = await twoFactorService.BeginTotpSetupAsync(password);
        Step("2fa: begin TOTP setup returns secret", !string.IsNullOrEmpty(secret),
            $"secret len={secret.Length}");
        Step("2fa: otpauth URI contains secret", otpauth.Contains(secret, StringComparison.Ordinal),
            otpauth.Length > 20 ? otpauth[..40] + "…" : otpauth);

        // Step 2: Generate a valid TOTP code using the secret
        var unixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var code = TotpGenerator.Generate(secret, unixSeconds);
        Step("2fa: TotpGenerator produces 6-digit code",
            code.Length == 6 && code.All(char.IsDigit), code);

        // Step 3: Enable TOTP — sends secret + code + masterPasswordHash to server
        var recoveryCode = await twoFactorService.EnableTotpAsync(password, secret, code);
        totpEnabled = true;
        Step("2fa: enable TOTP succeeds", true);
        Step("2fa: recovery code non-empty", !string.IsNullOrEmpty(recoveryCode),
            recoveryCode.Length > 0 ? recoveryCode[..Math.Min(8, recoveryCode.Length)] + "…" : "(empty)");

        // Step 4: List providers — type 0 (Authenticator) must be enabled
        var providers = await twoFactorService.ListProvidersAsync();
        var totp = providers.FirstOrDefault(p => p.Type == 0);
        Step("2fa: list providers contains type 0 (TOTP)", totp is not null,
            $"{providers.Count} provider(s)");
        Step("2fa: type 0 is enabled", totp?.Enabled == true,
            totp is null ? "not found" : $"enabled={totp.Enabled}");

        // Step 5: Disable TOTP — account must end clean
        await twoFactorService.DisableAsync(password, 0);
        totpEnabled = false;
        Step("2fa: disable TOTP succeeds", true);

        // Step 6: List providers again — type 0 must be gone or disabled
        var afterDisable = await twoFactorService.ListProvidersAsync();
        var totpAfter = afterDisable.FirstOrDefault(p => p.Type == 0);
        var isGone = totpAfter is null || !totpAfter.Enabled;
        Step("2fa: type 0 not enabled after disable", isGone,
            totpAfter is null ? "not in list" : $"enabled={totpAfter.Enabled}");
    }
    catch (Exception ex)
    {
        Step("2fa: TOTP round-trip", false, $"{ex.GetType().Name}: {ex.Message}");
        // Best-effort cleanup: if TOTP was enabled, try to disable
        if (totpEnabled)
        {
            try
            {
                await twoFactorService.DisableAsync(password, 0);
                Console.WriteLine("  [INFO] 2fa: TOTP disabled in cleanup after failure");
            }
            catch (Exception cleanupEx)
            {
                Console.WriteLine($"  [WARN] 2fa: could not disable TOTP in cleanup: {cleanupEx.Message}");
            }
        }
    }
}

// ── Auth-request: RSA round-trip
//    1. 生成 RSA-2048 密钥对；用公钥 + 随机 accessCode 创建 auth-request。
//    2. 调用 ListPendingAsync 断言新 id 在列表中。
//    3. 调用 ApproveAsync(id, pubKey) → 用私钥解密返回的 EncString Key。
//    4. 断言解密结果等于会话 UserKey.FullKey（64 字节）。
//    5. 再次 ListPendingAsync 断言 id 已不在列表中（已批准，移出 pending）。
async Task AuthRequestRoundTripAsync()
{
    // 取持久化设备标识符（登录时保存的那个）
    if (!tokenStore.TryLoad(out var persisted))
    {
        Step("auth-request: load persisted session", false, "TryLoad returned false");
        return;
    }

    string createdId;
    try
    {
        using var rsa = RSA.Create(2048);
        var pub = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        var accessCode = Convert.ToBase64String(RandomNumberGenerator.GetBytes(25));

        // 1. 创建 auth-request
        var resp = await authRequestApi.CreateAsync(new Api.Dtos.AuthRequestRequest(
            AccessCode: accessCode,
            DeviceIdentifier: persisted.DeviceIdentifier,
            Email: email,
            PublicKey: pub));
        createdId = resp.Id;
        Step("auth-request: create", !string.IsNullOrEmpty(createdId), createdId);

        // 2. 列出 pending，断言 id 在其中
        var pending = await authRequestService.ListPendingAsync();
        var found = pending.Any(r => r.Id == createdId);
        Step("auth-request: id present in ListPending", found, $"{pending.Count} pending");

        // 3. 批准（用发起方公钥加密会话 UserKey）
        await authRequestService.ApproveAsync(createdId, pub);
        Step("auth-request: ApproveAsync succeeded", true);

        // 4. 发起方轮询响应，解密 Key，与会话 UserKey 比对
        var getResp = await authRequestApi.GetResponseAsync(createdId, accessCode);
        Step("auth-request: GetResponse key non-null", getResp.Key is not null, getResp.Key?[..Math.Min(20, getResp.Key?.Length ?? 0)]);
        if (getResp.Key is not null)
        {
            var decrypted = crypto.DecryptRsa(
                EncString.Parse(getResp.Key),
                rsa.ExportPkcs8PrivateKey());
            var sessionKey = session.UserKey!.FullKey;
            var match = decrypted.AsSpan().SequenceEqual(sessionKey);
            Step("auth-request: decrypted key equals session UserKey", match,
                $"decrypted={decrypted.Length}B vs session={sessionKey.Length}B");
        }

        // 5. 再次 ListPending，断言 id 已不在（approved → 移出 pending）
        var pendingAfter = await authRequestService.ListPendingAsync();
        var stillPending = pendingAfter.Any(r => r.Id == createdId);
        Step("auth-request: no longer in ListPending after approval", !stillPending,
            $"{pendingAfter.Count} pending remaining");
    }
    catch (Exception ex)
    {
        Step("auth-request: round-trip", false, $"{ex.GetType().Name}: {ex.Message}");
    }
}

// ── Notifications: WebSocket push round-trip ─────────────────────────────────
//    1. 连接到 WS 通知 hub，断言握手成功（{}{0x1e}）。
//    2. 启动后台读取任务，把收到的消息写入 Channel，超时 12s。
//    3. 通过 VaultWriteService 创建文件夹（触发服务端推送）。
//    4. 等待最多 10s，收到 Type==7 (SyncFolderCreate) 且 EntityId == 新文件夹 id 的消息。
//    5. 从 session 中先移除该文件夹（使断言有意义），再调用 NotificationDispatcher.DispatchAsync，
//       断言 session.Folders 现在包含该文件夹 id（由 dispatcher 触发 GET /api/folders/{id} 补回）。
//    6. 清理：删除文件夹；优雅关闭 WebSocket。
async Task NotificationsPushRoundTripAsync()
{
    var conn = new NotificationsConnection();
    var wsFolderName = $"WVW-WS-{run}";
    string? wsFolderId = null;

    bool handshakeOk = false;
    string? handshakeError = null;
    try
    {
        // Step 1: 连接并断言握手（非空洞式：捕获异常并 Step false）
        try
        {
            await conn.ConnectAsync(serverUrl, session.AccessToken!, CancellationToken.None);
            handshakeOk = true;
        }
        catch (Exception ex)
        {
            handshakeError = $"{ex.GetType().Name}: {ex.Message}";
        }
        Step("ws handshake ok", handshakeOk, handshakeOk ? null : handshakeError);

        if (!handshakeOk)
        {
            Step("ws push SyncFolderCreate received", false, "skipped (handshake failed)");
            Step("ws: folder removed before dispatch", false, "skipped (handshake failed)");
            Step("ws dispatcher re-adds folder", false, "skipped (handshake failed)");
            return;
        }

        // Step 2: 后台读取任务，把消息写入 Channel
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var channel = System.Threading.Channels.Channel.CreateUnbounded<NotificationMessage>();

        var readTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var m in conn.ReadAsync(cts.Token))
                    channel.Writer.TryWrite(m);
            }
            catch (OperationCanceledException) { /* 超时，正常结束 */ }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        // Step 3: 创建文件夹（服务端将推送 SyncFolderCreate Type=7）
        await writeService.SaveFolderAsync(null, wsFolderName);
        wsFolderId = session.Folders.FirstOrDefault(x => x.Name == wsFolderName)?.Id;
        Step("ws: folder created for push test", wsFolderId is not null, wsFolderId);

        if (wsFolderId is null)
        {
            Step("ws push SyncFolderCreate received", false, "folder id unknown, cannot match push");
            Step("ws dispatcher re-adds folder", false, "skipped");
        }
        else
        {
            // Step 4: 等待最多 10s 收到匹配的推送消息
            NotificationMessage? received = null;
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await foreach (var msg in channel.Reader.ReadAllAsync(waitCts.Token))
                {
                    if (msg.Type == (int)Core.Enums.UpdateType.SyncFolderCreate && msg.EntityId == wsFolderId)
                    {
                        received = msg;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* 10s 超时 */ }

            Step("ws push SyncFolderCreate received", received is not null,
                received is null ? "timeout / not received" : $"type=7 id={received.EntityId}");

            // Step 5: 先从 session 移除该文件夹，断言已缺失，再通过 dispatcher 触发 GET 补回
            if (received is not null)
            {
                session.RemoveFolder(wsFolderId);
                var beforeDispatch = session.Folders.Any(x => x.Id == wsFolderId);
                Step("ws: folder removed before dispatch", !beforeDispatch,
                    beforeDispatch ? "RemoveFolder had no effect" : "confirmed absent");

                var dispatcher = new NotificationDispatcher(
                    cipherApi: attachmentApi,
                    readApi: api,
                    decryptor: decryptor,
                    session: session,
                    sync: sync);

                await dispatcher.DispatchAsync(received, CancellationToken.None);

                var afterDispatch = session.Folders.Any(x => x.Id == wsFolderId);
                Step("ws dispatcher re-adds folder", afterDispatch,
                    afterDispatch ? $"id={wsFolderId} present" : "not found after dispatch");
            }
            else
            {
                Step("ws dispatcher re-adds folder", false, "skipped (no push received)");
            }
        }

        // 停止后台读取
        cts.Cancel();
        try { await readTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
    }
    catch (Exception ex)
    {
        Step("ws: notifications round-trip", false, $"{ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        // Step 6: 清理文件夹 + 优雅关闭 WS
        if (wsFolderId is not null)
        {
            try
            {
                await writeService.DeleteFolderAsync(wsFolderId);
                var wsGone = session.Folders.All(x => x.Id != wsFolderId);
                Step("ws: folder cleaned up", wsGone);
            }
            catch (Exception ex)
            {
                Step("ws: folder cleaned up", false, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        await conn.DisposeAsync();
    }
}

// ── BuildStack: construct an independent service graph for one account. ──────
// Returns an anonymous-record-style tuple of the pieces needed for the EA test.
(ApiClient api, VaultSession session, CryptoService crypto, AuthService auth,
 SyncService sync, VaultWriteService write, AccountService account,
 EmergencyAccessService ea)
BuildStack(string url)
{
    var s = new VaultSession();
    var c = new CryptoService();
    var ts = new MemoryTokenStore();
    ITokenRefresher? ref2 = null;
    var handler = new AuthHeaderHandler(
        () => s.AccessToken,
        ct2 => ref2!.TryRefreshAsync(ct2))
    { InnerHandler = new HttpClientHandler() };
    var h = new HttpClient(handler);
    var a = new ApiClient(h);
    ref2 = new TokenRefreshService(a, s, ts);
    var dec = new VaultDecryptor(c);
    var boot = new VaultBootstrapper(a, dec, s);
    var auth2 = new AuthService(a, c, s, ts, boot);
    var sync2 = new SyncService(a, dec, s);
    var enc2 = new CipherEncryptor(c);
    var write2 = new VaultWriteService(a, enc2, sync2, s);
    IAccountApiClient acctApi = a;
    var acct2 = new AccountService(c, acctApi, s, ts, auth2);
    IEmergencyAccessApiClient eaApi = a;
    var ea2 = new EmergencyAccessService(eaApi, c, dec, s);
    a.SetBaseAddress(url);
    return (a, s, c, auth2, sync2, write2, acct2, ea2);
}

// ── Emergency Access: two throwaway accounts (grantor A, grantee B) ──────────
// Flow: register → A creates cipher → A invites B (Takeover, wait=0) →
//       auto-accepted → A confirms → B initiates → A approves →
//       B views (decrypt assert) → B takeover → login-as-A(new-pw) → cleanup.
async Task EmergencyAccessRoundTripAsync()
{
    const string eaPassword = "Test-Master-Password-1!";
    const string newEaPassword = "New-EA-Pass-1!";
    var emailA = $"ea-grantor-{run}@winvaultwarden.local";
    var emailB = $"ea-grantee-{run}@winvaultwarden.local";

    // ── Step 1: Register A and B ────────────────────────────────────────────
    {
        var regSvc = new Vault.RegisterService(crypto, api);
        foreach (var (em, label) in new[] { (emailA, "A"), (emailB, "B") })
        {
            try
            {
                await regSvc.RegisterAsync(serverUrl, em, $"EA {label}", eaPassword, null);
                Step($"ea: registered {label}", true, em);
            }
            catch (Core.Services.RegistrationException ex)
                when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                   || ex.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
            {
                Step($"ea: {label} already exists (reuse)", true, em);
            }
            catch (Exception ex)
            {
                Step($"ea: register {label}", false, $"{ex.GetType().Name}: {ex.Message}");
                return; // cannot continue without both accounts
            }
        }
    }

    // Build independent stacks for A and B
    var stackA = BuildStack(serverUrl);
    var stackB = BuildStack(serverUrl);

    string eaCipherNameA = $"WVW-EA-Login-{run}";

    try
    {
        // ── Step 2: A logs in ─────────────────────────────────────────────────
        var loginA = await stackA.auth.LoginAsync(serverUrl, emailA, eaPassword);
        Step("ea: A login", loginA is AuthResult.Success, loginA.GetType().Name);
        if (loginA is not AuthResult.Success)
            throw new InvalidOperationException("EA: A login failed, aborting.");

        // ── Step 3: A creates a Login cipher ─────────────────────────────────
        var cipherA = new Cipher
        {
            Type = CipherType.Login,
            Name = eaCipherNameA,
            Login = new CipherLogin($"ea-user-{run}", $"ea-pass-{run}!", null,
                Array.Empty<CipherLoginUri>()),
        };
        await stackA.write.SaveCipherAsync(cipherA);
        var syncedA = await stackA.sync.SyncAsync();
        var foundA = syncedA.FirstOrDefault(c => c.Name == eaCipherNameA);
        Step("ea: A cipher created", foundA is not null, foundA?.Id);

        // ── Step 4: B logs in — BootstrapAsync populates EncryptedPrivateKey ──
        var loginB = await stackB.auth.LoginAsync(serverUrl, emailB, eaPassword);
        Step("ea: B login", loginB is AuthResult.Success, loginB.GetType().Name);
        if (loginB is not AuthResult.Success)
            throw new InvalidOperationException("EA: B login failed, aborting.");

        // Local helper: run a full grant cycle (invite → auto-accept → confirm →
        // initiate → approve) for a given access type, returning the grant id from
        // B's (grantee) perspective. Step-asserts the key transitions along the way.
        async Task<string> RunGrantToRecoveryApprovedAsync(EmergencyAccessType type, string label)
        {
            await stackA.ea.InviteAsync(emailB, type, 0);
            await Task.Delay(500); // small pause for server processing (mail off → auto-accept)

            var trusted = await stackA.ea.GetTrustedAsync();
            var bRec = trusted.FirstOrDefault(r =>
                string.Equals(r.Email, emailB, StringComparison.OrdinalIgnoreCase)
                && r.Type == type);
            Step($"ea[{label}]: A trusted list contains B (Accepted)",
                bRec is not null && bRec.Status == EmergencyAccessStatus.Accepted,
                bRec is null ? "not found" : $"id={bRec.Id} status={bRec.Status} granteeId={bRec.GranteeId}");
            if (bRec is null || string.IsNullOrEmpty(bRec.GranteeId))
                throw new InvalidOperationException($"EA[{label}]: B not Accepted with granteeId after invite.");

            var gid = bRec.Id;

            // A confirms (encrypts A's userKey to B's public key)
            await stackA.ea.ConfirmAsync(gid, bRec.GranteeId);
            var confirmed = (await stackA.ea.GetTrustedAsync()).FirstOrDefault(r => r.Id == gid);
            Step($"ea[{label}]: status=Confirmed after confirm",
                confirmed?.Status == EmergencyAccessStatus.Confirmed, $"status={confirmed?.Status}");

            // B sees the grant in its granted list; B initiates recovery
            var granted = await stackB.ea.GetGrantedAsync();
            var aRec = granted.FirstOrDefault(r =>
                string.Equals(r.Email, emailA, StringComparison.OrdinalIgnoreCase) && r.Id == gid);
            Step($"ea[{label}]: B granted list contains A",
                aRec is not null, aRec is null ? "not found" : $"id={aRec.Id} status={aRec.Status}");
            if (aRec is null)
                throw new InvalidOperationException($"EA[{label}]: A not in B's granted list.");

            await stackB.ea.InitiateAsync(gid);
            // A approves (wait=0 → immediately RecoveryApproved)
            await stackA.ea.ApproveAsync(gid);
            Step($"ea[{label}]: initiate + approve (RecoveryApproved)", true);
            return gid;
        }

        // ── Step 5: View-type grant — exercise the PRODUCTION ViewAsync ──────
        // This RSA-recovers A's user key inside ViewAsync and decrypts A's ciphers
        // with that recovered key (NOT A's own session) — the real recovery path.
        {
            var viewGrantId = await RunGrantToRecoveryApprovedAsync(EmergencyAccessType.View, "view");

            RecoveredVault recovered = await stackB.ea.ViewAsync(viewGrantId, emailA);
            Step("ea: ViewAsync returned recovered ciphers", recovered.Ciphers.Count > 0,
                $"{recovered.Ciphers.Count} cipher(s), grantor={recovered.GrantorEmail}");
            var recoveredCipher = recovered.Ciphers.FirstOrDefault(c => c.Name == eaCipherNameA);
            Step("ea: ViewAsync decrypts A's cipher (recovered key)",
                recoveredCipher is not null, recoveredCipher?.Name ?? "not found");

            // Remove the View grant so the next invite (Takeover) is not rejected as a
            // duplicate grantor→grantee pair.
            await stackA.ea.RemoveAsync(viewGrantId);
            var afterRemove = (await stackA.ea.GetTrustedAsync()).Any(r => r.Id == viewGrantId);
            Step("ea: View grant removed", !afterRemove, afterRemove ? "still present" : "gone");
        }

        // ── Step 6: Takeover-type grant — takeover + reset A's password ──────
        {
            var takeoverGrantId = await RunGrantToRecoveryApprovedAsync(EmergencyAccessType.Takeover, "takeover");

            await stackB.ea.TakeoverAndResetPasswordAsync(takeoverGrantId, emailA, newEaPassword);
            Step("ea: takeover + reset A's password", true);

            // Login as A with NEW password (fresh stack → genuine prelogin+token).
            var verifyA = BuildStack(serverUrl);
            var loginNewA = await verifyA.auth.LoginAsync(serverUrl, emailA, newEaPassword);
            Step("ea: login-as-A with new password succeeds", loginNewA is AuthResult.Success,
                loginNewA.GetType().Name + (loginNewA is AuthResult.Failure fa ? $": {fa.Message}" : ""));
            Step("ea: A's user key decrypts after takeover", verifyA.session.UserKey is not null);
        }
    }
    catch (Exception ex)
    {
        Step("ea: E2E flow", false, $"{ex.GetType().Name}: {ex.Message}");
        Console.WriteLine($"  [ea ABORT] {ex.StackTrace?.Split('\n').FirstOrDefault()}");
    }
    finally
    {
        // ── Step 12: Cleanup (best-effort) ───────────────────────────────────
        Console.WriteLine("  [ea cleanup] beginning best-effort cleanup…");

        // B removes any leftover EA record (best-effort). The View grant is removed
        // inline mid-flow and the Takeover grant is consumed by takeover, so normally
        // nothing remains; this catches a grant left dangling by an early abort.
        try
        {
            // B needs to be logged in for this; re-login if session expired
            if (string.IsNullOrEmpty(stackB.session.AccessToken))
                await stackB.auth.LoginAsync(serverUrl, emailB, eaPassword);

            // Find any record from B's granted perspective and remove it
            var grantedFinal = await stackB.ea.GetGrantedAsync();
            var aFinal = grantedFinal.FirstOrDefault(r =>
                string.Equals(r.Email, emailA, StringComparison.OrdinalIgnoreCase));
            if (aFinal is not null)
            {
                await stackB.ea.RemoveAsync(aFinal.Id);
                Step("ea: cleanup B removes leftover EA record (best-effort)", true, aFinal.Id);
            }
            else
            {
                Step("ea: cleanup no leftover EA record (best-effort)", true, "none");
            }
        }
        catch (Exception ex)
        {
            Step("ea: cleanup B removes leftover EA record (best-effort)", false,
                $"{ex.GetType().Name}: {ex.Message}");
        }

        // Delete account A (using new password now, since takeover reset it)
        try
        {
            // Build a fresh stack for A with the new password to get an access token
            var cleanA = BuildStack(serverUrl);
            var loginCleanA = await cleanA.auth.LoginAsync(serverUrl, emailA, newEaPassword);
            if (loginCleanA is AuthResult.Success && cleanA.session.UserKey is not null)
            {
                // Compute master password hash with NEW password
                var masterKeyA = cleanA.crypto.DeriveMasterKey(
                    newEaPassword, emailA, Core.Enums.KdfType.Pbkdf2, 600_000, null, null);
                var hashA = cleanA.crypto.ComputeMasterPasswordHash(masterKeyA, newEaPassword);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(masterKeyA);
                IEmergencyAccessApiClient eaApiA = cleanA.api;
                await eaApiA.DeleteAccountAsync(new Api.Dtos.DeleteAccountRequest { MasterPasswordHash = hashA });
                Step("ea: cleanup delete account A (best-effort)", true, emailA);
            }
            else
            {
                // Try original password in case takeover didn't happen
                var cleanA2 = BuildStack(serverUrl);
                var loginCleanA2 = await cleanA2.auth.LoginAsync(serverUrl, emailA, eaPassword);
                if (loginCleanA2 is AuthResult.Success && cleanA2.session.UserKey is not null)
                {
                    var masterKeyA2 = cleanA2.crypto.DeriveMasterKey(
                        eaPassword, emailA, Core.Enums.KdfType.Pbkdf2, 600_000, null, null);
                    var hashA2 = cleanA2.crypto.ComputeMasterPasswordHash(masterKeyA2, eaPassword);
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(masterKeyA2);
                    IEmergencyAccessApiClient eaApiA2 = cleanA2.api;
                    await eaApiA2.DeleteAccountAsync(new Api.Dtos.DeleteAccountRequest { MasterPasswordHash = hashA2 });
                    Step("ea: cleanup delete account A (best-effort)", true, emailA + " (orig pw)");
                }
                else
                {
                    Step("ea: cleanup delete account A (best-effort)", false, "both passwords failed login");
                }
            }
        }
        catch (Exception ex)
        {
            Step("ea: cleanup delete account A (best-effort)", false,
                $"{ex.GetType().Name}: {ex.Message}");
        }

        // Delete account B (using original password)
        try
        {
            var cleanB = BuildStack(serverUrl);
            var loginCleanB = await cleanB.auth.LoginAsync(serverUrl, emailB, eaPassword);
            if (loginCleanB is AuthResult.Success && cleanB.session.UserKey is not null)
            {
                var masterKeyB = cleanB.crypto.DeriveMasterKey(
                    eaPassword, emailB, Core.Enums.KdfType.Pbkdf2, 600_000, null, null);
                var hashB = cleanB.crypto.ComputeMasterPasswordHash(masterKeyB, eaPassword);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(masterKeyB);
                IEmergencyAccessApiClient eaApiB = cleanB.api;
                await eaApiB.DeleteAccountAsync(new Api.Dtos.DeleteAccountRequest { MasterPasswordHash = hashB });
                Step("ea: cleanup delete account B (best-effort)", true, emailB);
            }
            else
            {
                Step("ea: cleanup delete account B (best-effort)", false, "login failed");
            }
        }
        catch (Exception ex)
        {
            Step("ea: cleanup delete account B (best-effort)", false,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
