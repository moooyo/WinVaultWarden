using Api;
using Api.Dtos;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultImportServiceTests
{
    private sealed class CapturingImportApi : IVaultWriteApiClient
    {
        public ImportRequest? Last { get; private set; }

        public Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkSoftDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkHardDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkRestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BulkMoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ImportCiphersAsync(ImportRequest request, CancellationToken ct = default)
        {
            Last = request;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSync : ISyncService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<Cipher>>(Array.Empty<Cipher>());
        }

        public Task<IReadOnlyList<Cipher>> SyncAsync(
            Core.Session.VaultSessionLease lease, CancellationToken ct = default) => SyncAsync(ct);
    }

    private sealed class ThrowingSync : ISyncService
    {
        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("sync boom");

        public Task<IReadOnlyList<Cipher>> SyncAsync(
            Core.Session.VaultSessionLease lease, CancellationToken ct = default) => SyncAsync(ct);
    }

    [Fact]
    public async Task ImportAsync_EncryptsAndSendsImport_WithRelations_ThenResyncs()
    {
        var api = new CapturingImportApi();
        var sync = new FakeSync();
        var session = new VaultSession();
        session.BeginAccountSession("test-account", "test@test.com", new Core.Models.ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var svc = new VaultImportService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(api), new CipherEncryptor(new CryptoService()), sync, session);

        // 1 folder + 1 login in that folder
        var json = "{\"encrypted\":false,\"folders\":[{\"id\":\"f0\",\"name\":\"Work\"}]," +
                   "\"items\":[{\"type\":1,\"name\":\"A\",\"folderId\":\"f0\",\"login\":{\"username\":\"u\",\"password\":\"p\",\"uris\":[]}}]}";
        var n = await svc.ImportAsync(ImportFormat.Json, json, TestContext.Current.CancellationToken);

        Assert.Equal(1, n);
        Assert.Single(api.Last!.Ciphers);
        Assert.Single(api.Last.Folders);
        Assert.Equal(0, api.Last.FolderRelationships[0].Key);   // cipher0
        Assert.Equal(0, api.Last.FolderRelationships[0].Value); // folder0
        // 名称必须真正加密成 EncString(AES-256-CBC-HMAC,"2." 前缀)而非明文——仅断言非空会让明文回归漏网(R22)。
        var encName = api.Last.Ciphers[0].Name;
        Assert.StartsWith("2.", encName);
        Assert.NotEqual("A", encName);
        Assert.Equal(1, sync.Calls);
    }

    [Fact]
    public async Task ImportAsync_WriteSucceedsButResyncFails_ThrowsVaultRefreshException()
    {
        // R22:写入已在服务端成功,但写后重同步失败 → 抛 VaultRefreshException(区分"写失败"与"写成功但刷新失败")。
        var api = new CapturingImportApi();
        var session = new VaultSession();
        session.BeginAccountSession("test-account", "test@test.com", new Core.Models.ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var svc = new VaultImportService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(api), new CipherEncryptor(new CryptoService()), new ThrowingSync(), session);

        var json = "{\"encrypted\":false,\"folders\":[],\"items\":[{\"type\":1,\"name\":\"A\",\"login\":{\"username\":\"u\",\"password\":\"p\",\"uris\":[]}}]}";
        await Assert.ThrowsAsync<VaultRefreshException>(() =>
            svc.ImportAsync(ImportFormat.Json, json, TestContext.Current.CancellationToken));

        Assert.NotNull(api.Last); // 写已成功(异常仅来自写后刷新)
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsError() =>
        Assert.NotNull(new VaultImportService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(new CapturingImportApi()), new CipherEncryptor(new CryptoService()), new FakeSync(), new VaultSession())
            .Parse(ImportFormat.Json, "{bad").Error);

    [Fact]
    public async Task ImportAsync_WhenLocked_Throws() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VaultImportService(FakeVaultApiClientProvider.ForAuthenticated<IVaultWriteApiClient>(new CapturingImportApi()), new CipherEncryptor(new CryptoService()), new FakeSync(), new VaultSession())
                .ImportAsync(ImportFormat.Json, "{\"items\":[]}", TestContext.Current.CancellationToken));
}
