using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;

internal static class CompletionFeatureSmoke
{
    public static async Task RunAsync(
        string serverUrl,
        string email,
        string masterPassword,
        AuthService auth,
        IVaultApiClientProvider clients,
        VaultSession session,
        CryptoService crypto,
        MemoryTokenStore tokenStore,
        SyncService sync,
        VaultWriteService writer,
        CipherEncryptor encryptor,
        VaultCacheService cache,
        string prefix,
        Action<string, bool, string?> step)
    {
        var imports = new VaultImportService(clients, encryptor, sync, session);
        var exports = new VaultExportService(new VaultService(session), session);
        await KdfRoundTripAsync(serverUrl, email, masterPassword, auth, clients,
            session, crypto, tokenStore, cache, step);
        await BackupRoundTripAsync(imports, exports, writer, sync, session, prefix, step);
        await CompletionImportSmoke.RunAsync(imports, writer, sync, session, prefix + "Source-", step);
        await ApiKeyRoundTripAsync(
            serverUrl, email, masterPassword, auth, session, crypto, tokenStore, sync, writer, prefix, step);
    }

    private static async Task KdfRoundTripAsync(
        string serverUrl,
        string email,
        string masterPassword,
        AuthService auth,
        IVaultApiClientProvider clients,
        VaultSession session,
        CryptoService crypto,
        MemoryTokenStore tokenStore,
        VaultCacheService cache,
        Action<string, bool, string?> step)
    {
        Console.WriteLine("[completion] PBKDF2 to Argon2id and back on the real server");
        if (!tokenStore.TryLoad(out var before))
            throw new InvalidOperationException("The synthetic account must be authenticated before changing its KDF.");
        var original = new AccountKdfParameters(before.KdfType, before.KdfIterations, before.KdfMemory, before.KdfParallelism);
        var expectedUserKey = session.UserKey!.FullKey.ToArray();
        try
        {
            var account = new AccountService(crypto, clients, session, tokenStore, auth, cache);
            await account.ChangeKdfAsync(masterPassword, AccountKdfParameters.Argon2id());
            step("completion KDF: Argon2id parameters accepted by the real server", true, null);
            var argonLogin = await auth.LoginAsync(serverUrl, email, masterPassword);
            step("completion KDF: Argon2id login succeeds", argonLogin is AuthResult.Success,
                argonLogin is AuthResult.Failure failure ? failure.Kind.ToString() : argonLogin.GetType().Name);
            if (argonLogin is not AuthResult.Success)
                throw new InvalidOperationException("Argon2id re-authentication failed.");
            step("completion KDF: Argon2id preserves the account key and parameters",
                session.UserKey is { } argonKey
                    && CryptographicOperations.FixedTimeEquals(argonKey.FullKey, expectedUserKey)
                    && tokenStore.TryLoad(out var argonState)
                    && argonState.KdfType == KdfType.Argon2id
                    && argonState.KdfIterations == 3 && argonState.KdfMemory == 64 && argonState.KdfParallelism == 4, null);
            await account.ChangeKdfAsync(masterPassword, original);
            step("completion KDF: original parameters restored", true, null);
            var originalLogin = await auth.LoginAsync(serverUrl, email, masterPassword);
            step("completion KDF: original KDF login succeeds", originalLogin is AuthResult.Success,
                originalLogin is AuthResult.Failure originalFailure ? originalFailure.Kind.ToString() : originalLogin.GetType().Name);
            step("completion KDF: account key unchanged after both transitions",
                session.UserKey is { } restoredKey
                    && CryptographicOperations.FixedTimeEquals(restoredKey.FullKey, expectedUserKey), null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedUserKey);
        }
    }

    private static async Task BackupRoundTripAsync(
        VaultImportService imports,
        VaultExportService exports,
        VaultWriteService writer,
        SyncService sync,
        VaultSession session,
        string prefix,
        Action<string, bool, string?> step)
    {
        Console.WriteLine("[completion] Protected backup export, unlock, and server restore");
        var name = prefix + "Protected-Backup";
        var syntheticPassword = prefix + "Synthetic-Item-Secret";
        var backupPassword = prefix + "Synthetic-Backup-Password";
        try
        {
            await writer.SaveCipherAsync(new Cipher
            {
                Type = CipherType.Login,
                Name = name,
                Login = new CipherLogin("synthetic-backup-user", syntheticPassword, null, []),
                Notes = "Synthetic completion verification item.",
            });
            var original = (await sync.SyncAsync()).Single(cipher => cipher.Name == name);
            step("completion backup: original item created on the real server", true, original.Id);

            var passwordExport = await exports.ExportProtectedAsync(ExportFormat.PasswordProtectedJson, backupPassword);
            step("completion backup: password-protected envelope", imports.InspectJsonProtection(passwordExport) == ImportProtection.Password, null);
            step("completion backup: plaintext absent from encrypted export", !passwordExport.Contains(syntheticPassword, StringComparison.Ordinal), null);
            var wrongPasswordRejected = false;
            try { await imports.UnlockJsonAsync(passwordExport, backupPassword + "-wrong"); }
            catch (CryptographicException) { wrongPasswordRejected = true; }
            step("completion backup: incorrect backup password rejected", wrongPasswordRejected, null);

            var tamperRejected = false;
            var tampered = TamperAuthenticatedData(passwordExport);
            try { await imports.UnlockJsonAsync(tampered, backupPassword); }
            catch (CryptographicException) { tamperRejected = true; }
            step("completion backup: authenticated payload tampering rejected", tamperRejected, null);

            var plaintext = await imports.UnlockJsonAsync(passwordExport, backupPassword);
            var importedCount = await imports.ImportAsync(ImportFormat.Json, plaintext);
            var afterPasswordRestore = (await sync.SyncAsync()).Where(cipher => cipher.Name == name).ToArray();
            step("completion backup: password export restored through real import", importedCount == 1, null);
            step("completion backup: restored IDs are fresh and secrets decrypt",
                afterPasswordRestore.Length == 2
                    && afterPasswordRestore.Select(cipher => cipher.Id).Distinct().Count() == 2
                    && afterPasswordRestore.All(cipher => cipher.Login?.Password == syntheticPassword), null);

            var accountExport = await exports.ExportProtectedAsync(ExportFormat.AccountRestrictedJson, password: null);
            step("completion backup: account-restricted envelope", imports.InspectJsonProtection(accountExport) == ImportProtection.Account, null);
            step("completion backup: account export contains no item plaintext", !accountExport.Contains(syntheticPassword, StringComparison.Ordinal), null);
            using (var wrongKey = VaultUserKey.TakeOwnership(new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64))))
            {
                var wrongAccountRejected = false;
                try { Vault.Porting.EncryptedBitwardenJsonCodec.DecryptAccountRestricted(accountExport, wrongKey); }
                catch (CryptographicException) { wrongAccountRejected = true; }
                step("completion backup: a different account key is rejected", wrongAccountRejected, null);
            }
            var accountPlaintext = await imports.UnlockJsonAsync(accountExport, password: null);
            var accountImportedCount = await imports.ImportAsync(ImportFormat.Json, accountPlaintext);
            var afterAccountRestore = (await sync.SyncAsync()).Where(cipher => cipher.Name == name).ToArray();
            step("completion backup: account export restored through real import", accountImportedCount == 2, null);
            step("completion backup: every restored item remains decryptable",
                afterAccountRestore.Length == 4
                    && afterAccountRestore.Select(cipher => cipher.Id).Distinct().Count() == 4
                    && afterAccountRestore.All(cipher => cipher.Login?.Password == syntheticPassword), null);
        }
        finally
        {
            var own = (await sync.SyncAsync()).Where(cipher => cipher.Name == name).Select(cipher => cipher.Id).ToArray();
            foreach (var id in own)
                await writer.DeleteCipherAsync(id, permanent: true);
            step("completion backup: synthetic original and restored items removed",
                session.Ciphers.All(cipher => cipher.Name != name), null);
        }
    }

    private static async Task ApiKeyRoundTripAsync(
        string serverUrl,
        string email,
        string masterPassword,
        AuthService auth,
        VaultSession session,
        CryptoService crypto,
        MemoryTokenStore tokenStore,
        SyncService sync,
        VaultWriteService writer,
        string prefix,
        Action<string, bool, string?> step)
    {
        Console.WriteLine("[completion] Personal API key authentication against the real server");
        var lease = session.CaptureLease();
        if (!tokenStore.TryLoad(out var current)
            || !session.TryGetTokenSnapshot(lease, out var accessToken, out _)
            || string.IsNullOrEmpty(accessToken))
            throw new InvalidOperationException("The synthetic account must be authenticated before API-key verification.");

        var masterKey = crypto.DeriveMasterKey(masterPassword, current.EffectiveMasterPasswordSalt,
            current.KdfType, current.KdfIterations, current.KdfMemory, current.KdfParallelism);
        string masterHash;
        try { masterHash = crypto.ComputeMasterPasswordHash(masterKey, masterPassword); }
        finally { CryptographicOperations.ZeroMemory(masterKey); }

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 32 * 1024,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            current.Endpoints!.Api.Value.TrimEnd('/') + "/accounts/api-key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Device-Type", "6");
        request.Content = new StringContent(JsonSerializer.Serialize(new { masterPasswordHash = masterHash }),
            Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var apiSecret = document.RootElement.GetProperty("apiKey").GetString();
        if (string.IsNullOrEmpty(apiSecret))
            throw new InvalidDataException("The synthetic account did not receive an API key.");
        step("completion API key: server issued a personal key", true, null);
        var clientId = "user." + lease.AccountId;

        var badSecret = await auth.LoginWithApiKeyAsync(auth.BeginAuthenticationAttempt(),
            serverUrl, email, clientId, "invalid-" + apiSecret, masterPassword);
        step("completion API key: incorrect API secret rejected",
            badSecret is AuthResult.Failure { Kind: AuthFailureKind.InvalidCredentials },
            badSecret is AuthResult.Failure secretFailure ? secretFailure.Kind.ToString() : badSecret.GetType().Name);
        var badPassword = await auth.LoginWithApiKeyAsync(auth.BeginAuthenticationAttempt(),
            serverUrl, email, clientId, apiSecret, masterPassword + "-wrong");
        step("completion API key: correct API secret cannot bypass local password decryption",
            badPassword is AuthResult.Failure { Kind: AuthFailureKind.InvalidResponse },
            badPassword is AuthResult.Failure passwordFailure ? passwordFailure.Kind.ToString() : badPassword.GetType().Name);

        byte[] expectedUserKey = session.UserKey!.FullKey.ToArray();
        try
        {
            var result = await auth.LoginWithApiKeyAsync(auth.BeginAuthenticationAttempt(),
                serverUrl, email, clientId, apiSecret, masterPassword);
            step("completion API key: real client_credentials sign-in succeeds", result is AuthResult.Success,
                result is AuthResult.Failure failure ? failure.Kind.ToString() : result.GetType().Name);
            if (result is not AuthResult.Success)
                return;
            step("completion API key: same user key decrypts locally", session.UserKey is { } key
                && CryptographicOperations.FixedTimeEquals(key.FullKey, expectedUserKey), null);
            step("completion API key: response has no fabricated refresh token", string.IsNullOrEmpty(session.RefreshToken), null);
            var saved = tokenStore.TryLoad(out var persisted);
            step("completion API key: saved API session binding is valid", saved && persisted.IsApiKeySession && persisted.HasValidBinding, null);
            step("completion API key: secret absent from saved session", saved
                && !JsonSerializer.Serialize(persisted).Contains(apiSecret, StringComparison.Ordinal), null);
            await sync.SyncAsync();
            step("completion API key: authenticated sync succeeds", session.IsUnlocked(session.CaptureLease()), null);
            var name = prefix + "Api-Key-Write";
            await writer.SaveCipherAsync(new Cipher
            {
                Type = CipherType.Login,
                Name = name,
                Login = new CipherLogin("synthetic-api-user", "synthetic-api-write-password", null, []),
            });
            var written = (await sync.SyncAsync()).Single(cipher => cipher.Name == name);
            step("completion API key: encrypted CRUD works with the API access token",
                written.Login?.Password == "synthetic-api-write-password", null);
            await writer.DeleteCipherAsync(written.Id, permanent: true);
            step("completion API key: synthetic written item removed", session.Ciphers.All(cipher => cipher.Id != written.Id), null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedUserKey);
        }
    }

    private static string TamperAuthenticatedData(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var encrypted = root["data"]!.GetValue<string>().Split('|');
        var mac = Convert.FromBase64String(encrypted[^1]);
        try
        {
            mac[0] ^= 0x80;
            encrypted[^1] = Convert.ToBase64String(mac);
            root["data"] = string.Join('|', encrypted);
            return root.ToJsonString();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mac);
        }
    }
}
