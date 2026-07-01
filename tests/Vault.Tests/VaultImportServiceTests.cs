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

        public void SetBaseAddress(string baseUrl) { }

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
    }

    [Fact]
    public async Task ImportAsync_EncryptsAndSendsImport_WithRelations_ThenResyncs()
    {
        var api = new CapturingImportApi();
        var sync = new FakeSync();
        var session = new VaultSession();
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        var svc = new VaultImportService(api, new CipherEncryptor(new CryptoService()), sync, session);

        // 1 folder + 1 login in that folder
        var json = "{\"encrypted\":false,\"folders\":[{\"id\":\"f0\",\"name\":\"Work\"}]," +
                   "\"items\":[{\"type\":1,\"name\":\"A\",\"folderId\":\"f0\",\"login\":{\"username\":\"u\",\"password\":\"p\",\"uris\":[]}}]}";
        var n = await svc.ImportAsync(ImportFormat.Json, json, TestContext.Current.CancellationToken);

        Assert.Equal(1, n);
        Assert.Single(api.Last!.Ciphers);
        Assert.Single(api.Last.Folders);
        Assert.Equal(0, api.Last.FolderRelationships[0].Key);   // cipher0
        Assert.Equal(0, api.Last.FolderRelationships[0].Value); // folder0
        Assert.False(string.IsNullOrEmpty(api.Last.Ciphers[0].Name)); // 已加密(2.xxx)
        Assert.Equal(1, sync.Calls);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsError() =>
        Assert.NotNull(new VaultImportService(new CapturingImportApi(), new CipherEncryptor(new CryptoService()), new FakeSync(), new VaultSession())
            .Parse(ImportFormat.Json, "{bad").Error);

    [Fact]
    public async Task ImportAsync_WhenLocked_Throws() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new VaultImportService(new CapturingImportApi(), new CipherEncryptor(new CryptoService()), new FakeSync(), new VaultSession())
                .ImportAsync(ImportFormat.Json, "{\"items\":[]}", TestContext.Current.CancellationToken));
}
