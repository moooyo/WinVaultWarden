using System.Security.Cryptography;
using Api;
using Core.Models;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public partial class VaultWriteServiceTests
{
    [Fact]
    public async Task SaveCipher_V2Session_PostsBlobInsteadOfLegacyFields()
    {
        var (service, userKey) = NewV2UnlockedService();
        using (userKey)
        {
            var id = await service.SaveCipherAndGetIdAsync(
                NewLogin(),
                TestContext.Current.CancellationToken);

            Assert.Equal("synced-created-1", id);
            Assert.Equal("create", Assert.Single(_api.Calls));
            var request = Assert.IsType<Api.Dtos.CipherRequest>(_api.LastCipher);
            Assert.Equal("test-account", request.EncryptedFor);
            Assert.Equal(userKey.KeyIdHex, request.EncryptedByKeyId);
            Assert.StartsWith("7.", request.Key);
            Assert.NotNull(request.Data);
            Assert.Null(request.Name);
            Assert.Null(request.Login);
            Assert.Null(request.Fields);

            var restored = new VaultDecryptor(new CryptoService()).DecryptCipher(
                new Api.Dtos.CipherDto(
                    id!,
                    request.Type,
                    null,
                    null,
                    request.Key,
                    null,
                    null,
                    request.Favorite,
                    request.Reprompt,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    null,
                    Data: request.Data),
                userKey);
            Assert.Equal("Item", restored.Name);
            Assert.Equal("user", restored.Login?.Username);
        }
    }

    [Fact]
    public async Task SaveFolder_V2Session_PostsType7Name()
    {
        var (service, userKey) = NewV2UnlockedService();
        using (userKey)
        {
            await service.SaveFolderAsync(
                null,
                "Production",
                TestContext.Current.CancellationToken);

            var request = Assert.IsType<Api.Dtos.FolderRequest>(_api.LastFolder);
            Assert.StartsWith("7.", request.Name);
            var plaintext = userKey.Decrypt(request.Name, CoseContentFormat.Utf8);
            try
            {
                Assert.Equal("Production", System.Text.Encoding.UTF8.GetString(plaintext));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private (VaultWriteService Service, V2UserKey KeyCopy) NewV2UnlockedService()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "test-account",
            "test@test.com",
            ServerEndpoints.ForSelfHosted("https://vault.example"));
        var sessionKey = V2UserKey.Generate();
        var transport = sessionKey.ExportTransport();
        V2UserKey keyCopy;
        try
        {
            keyCopy = V2UserKey.ImportTransport(transport);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transport);
        }
        session.SetUnlockedKey(sessionKey);
        Assert.True(session.TrySetCapabilities(lease, VaultCapabilities.None));
        var service = new VaultWriteService(
            FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(_api),
            new CipherEncryptor(new CryptoService()),
            _sync,
            session,
            cipherCreateStore: _pendingStore);
        return (service, keyCopy);
    }
}
