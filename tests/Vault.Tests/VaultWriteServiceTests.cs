using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class VaultWriteServiceTests
{
    private sealed class FakeWriteApi : IVaultWriteApiClient
    {
        public List<string> Calls { get; } = new();
        public string? LastId { get; private set; }
        public CipherRequest? LastCipher { get; private set; }
        public FolderRequest? LastFolder { get; private set; }
        public IReadOnlyCollection<string>? LastIds { get; private set; }
        public string? LastFolderId { get; private set; }

        public void SetBaseAddress(string baseUrl) { }

        public Task CreateCipherAsync(CipherRequest request, CancellationToken ct = default)
        { Calls.Add("create"); LastCipher = request; return Task.CompletedTask; }

        public Task UpdateCipherAsync(string cipherId, CipherRequest request, CancellationToken ct = default)
        { Calls.Add("update"); LastId = cipherId; LastCipher = request; return Task.CompletedTask; }

        public Task SoftDeleteCipherAsync(string cipherId, CancellationToken ct = default)
        { Calls.Add("soft"); LastId = cipherId; return Task.CompletedTask; }

        public Task HardDeleteCipherAsync(string cipherId, CancellationToken ct = default)
        { Calls.Add("hard"); LastId = cipherId; return Task.CompletedTask; }

        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
        { Calls.Add("restore"); LastId = cipherId; return Task.CompletedTask; }

        public Task BulkSoftDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        { Calls.Add("bulk-soft"); LastIds = ids; return Task.CompletedTask; }

        public Task BulkHardDeleteCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        { Calls.Add("bulk-hard"); LastIds = ids; return Task.CompletedTask; }

        public Task BulkRestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        { Calls.Add("bulk-restore"); LastIds = ids; return Task.CompletedTask; }

        public Task BulkMoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default)
        { Calls.Add("bulk-move"); LastIds = ids; LastFolderId = folderId; return Task.CompletedTask; }

        public Task CreateFolderAsync(FolderRequest request, CancellationToken ct = default)
        { Calls.Add("folder-create"); LastFolder = request; return Task.CompletedTask; }

        public Task UpdateFolderAsync(string folderId, FolderRequest request, CancellationToken ct = default)
        { Calls.Add("folder-update"); LastId = folderId; LastFolder = request; return Task.CompletedTask; }

        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
        { Calls.Add("folder-delete"); LastId = folderId; return Task.CompletedTask; }
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

    private readonly FakeWriteApi _api = new();
    private readonly FakeSync _sync = new();

    private VaultWriteService NewUnlockedService()
    {
        var session = new VaultSession();
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray()));
        return new VaultWriteService(_api, new CipherEncryptor(new CryptoService()), _sync, session);
    }

    private static Cipher NewLogin(string id = "") => new()
    {
        Id = id,
        Type = CipherType.Login,
        Name = "Item",
        Login = new CipherLogin("user", "pw", null, Array.Empty<CipherLoginUri>()),
    };

    [Fact]
    public async Task SaveCipher_NewItem_CreatesThenResyncs()
    {
        await NewUnlockedService().SaveCipherAsync(NewLogin(), TestContext.Current.CancellationToken);

        Assert.Equal("create", Assert.Single(_api.Calls));
        Assert.False(string.IsNullOrEmpty(_api.LastCipher!.Key));
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_ExistingItem_UpdatesWithId()
    {
        await NewUnlockedService().SaveCipherAsync(NewLogin("c-9"), TestContext.Current.CancellationToken);

        Assert.Equal("update", Assert.Single(_api.Calls));
        Assert.Equal("c-9", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCipher_SoftByDefault()
    {
        await NewUnlockedService().DeleteCipherAsync("c-9", permanent: false, TestContext.Current.CancellationToken);

        Assert.Equal("soft", Assert.Single(_api.Calls));
        Assert.Equal("c-9", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCipher_PermanentHardDeletes()
    {
        await NewUnlockedService().DeleteCipherAsync("c-9", permanent: true, TestContext.Current.CancellationToken);

        Assert.Equal("hard", Assert.Single(_api.Calls));
        Assert.Equal("c-9", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task RestoreCipher_RestoresThenResyncs()
    {
        await NewUnlockedService().RestoreCipherAsync("c-9", TestContext.Current.CancellationToken);

        Assert.Equal("restore", Assert.Single(_api.Calls));
        Assert.Equal("c-9", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveFolder_New_EncryptsNameAndCreates()
    {
        await NewUnlockedService().SaveFolderAsync(null, "Work", TestContext.Current.CancellationToken);

        Assert.Equal("folder-create", Assert.Single(_api.Calls));
        Assert.StartsWith("2.", _api.LastFolder!.Name);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveFolder_Existing_Updates()
    {
        await NewUnlockedService().SaveFolderAsync("f-1", "Work", TestContext.Current.CancellationToken);

        Assert.Equal("folder-update", Assert.Single(_api.Calls));
        Assert.Equal("f-1", _api.LastId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteFolder_DeletesThenResyncs()
    {
        await NewUnlockedService().DeleteFolderAsync("f-1", TestContext.Current.CancellationToken);

        Assert.Equal("folder-delete", Assert.Single(_api.Calls));
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task SaveCipher_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(_api, new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveCipherAsync(NewLogin(), TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCipher_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(_api, new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteCipherAsync("c-9", permanent: false, TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task RestoreCipher_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(_api, new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreCipherAsync("c-9", TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task SaveFolder_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(_api, new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveFolderAsync(null, "Work", TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task DeleteFolder_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(_api, new CipherEncryptor(new CryptoService()), _sync, new VaultSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteFolderAsync("f-1", TestContext.Current.CancellationToken));

        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task MoveCiphers_CallsBulkMoveThenResyncs()
    {
        await NewUnlockedService().MoveCiphersAsync(new[] { "a", "b" }, "f1", TestContext.Current.CancellationToken);

        Assert.Equal("bulk-move", Assert.Single(_api.Calls));
        Assert.Equal(new[] { "a", "b" }, _api.LastIds);
        Assert.Equal("f1", _api.LastFolderId);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCiphers_Soft_CallsBulkSoft()
    {
        await NewUnlockedService().DeleteCiphersAsync(new[] { "a" }, permanent: false, TestContext.Current.CancellationToken);
        Assert.Equal("bulk-soft", Assert.Single(_api.Calls));
        Assert.Equal(new[] { "a" }, _api.LastIds);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCiphers_Permanent_CallsBulkHard()
    {
        await NewUnlockedService().DeleteCiphersAsync(new[] { "a" }, permanent: true, TestContext.Current.CancellationToken);
        Assert.Equal("bulk-hard", Assert.Single(_api.Calls));
        Assert.Equal(new[] { "a" }, _api.LastIds);
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task RestoreCiphers_CallsBulkRestore()
    {
        await NewUnlockedService().RestoreCiphersAsync(new[] { "a" }, TestContext.Current.CancellationToken);
        Assert.Equal("bulk-restore", Assert.Single(_api.Calls));
        Assert.Equal(1, _sync.Calls);
    }

    [Fact]
    public async Task DeleteCiphers_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(_api, new CipherEncryptor(new CryptoService()), _sync, new VaultSession());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteCiphersAsync(new[] { "a" }, permanent: false, TestContext.Current.CancellationToken));
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task RestoreCiphers_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(_api, new CipherEncryptor(new CryptoService()), _sync, new VaultSession());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreCiphersAsync(new[] { "a" }, TestContext.Current.CancellationToken));
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task MoveCiphers_EmptyIds_NoApiNoSync()
    {
        await NewUnlockedService().MoveCiphersAsync(Array.Empty<string>(), "f1", TestContext.Current.CancellationToken);
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }

    [Fact]
    public async Task MoveCiphers_WhenLocked_ThrowsAndDoesNotCallApi()
    {
        var service = new VaultWriteService(_api, new CipherEncryptor(new CryptoService()), _sync, new VaultSession());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MoveCiphersAsync(new[] { "a" }, null, TestContext.Current.CancellationToken));
        Assert.Empty(_api.Calls);
        Assert.Equal(0, _sync.Calls);
    }
}

