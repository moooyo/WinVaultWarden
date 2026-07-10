using System.Linq;
using App.Services;
using App.ViewModels.Models;
using Core.Models;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace App.Tests;

// 演示/离线会话:VaultUiService 的写入/同步方法必须完全走 VaultSession 内存存储,绝不调用底层
// 网络写入/同步服务(否则会触发「An invalid request URI was provided…」,即用户报告的保险库写入报错)。
// 两个底层 fake 一旦被调用即抛异常,从而把「不触网」这一契约固化进测试。
public class VaultUiServiceDemoTests
{
    private static VaultSession DemoSessionWith(params Cipher[] ciphers)
    {
        var session = new VaultSession();
        session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray()));
        session.SetSnapshot(new DecryptedVault(
            AccountInfo.Empty,
            System.Array.Empty<Folder>(),
            ciphers,
            SkippedCipherCount: 0,
            SkippedFolderCount: 0));
        session.MarkDemo();
        return session;
    }

    private static VaultUiService DemoService(VaultSession session) =>
        new(new VaultService(session), new ThrowingWriteService(), new ThrowingSyncService(), session);

    private static Cipher LoginCipher(string id) => new()
    {
        Id = id,
        Type = Core.Enums.CipherType.Login,
        Name = "GitHub",
        Login = new CipherLogin("octo", "pw", null, System.Array.Empty<CipherLoginUri>()),
        CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
        RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
    };

    [Fact]
    public async Task SaveCipherAsync_InDemoMode_NewCipher_AddsToSessionStore_WithoutHittingNetwork()
    {
        var session = DemoSessionWith(LoginCipher("c1"));
        var svc = DemoService(session);
        var draft = CipherEditorDraft.CreateDefault(VaultItemKind.Login);
        draft.Name = "New";

        var id = await svc.SaveCipherAsync(draft, editingId: null);

        Assert.False(string.IsNullOrEmpty(id));
        Assert.Equal(2, session.Ciphers.Count);
        Assert.Contains(session.Ciphers, c => c.Id == id && c.Name == "New");
    }

    [Fact]
    public async Task DeleteCipherAsync_InDemoMode_SoftDelete_SetsDeletedDate_WithoutHittingNetwork()
    {
        var session = DemoSessionWith(LoginCipher("c1"));
        var svc = DemoService(session);

        await svc.DeleteCipherAsync("c1", permanent: false);

        Assert.True(session.Ciphers.First(c => c.Id == "c1").IsDeleted);
    }

    [Fact]
    public async Task RestoreCipherAsync_InDemoMode_ClearsDeletedDate_WithoutHittingNetwork()
    {
        var session = DemoSessionWith(LoginCipher("c1"));
        var svc = DemoService(session);
        await svc.DeleteCipherAsync("c1", permanent: false);

        await svc.RestoreCipherAsync("c1");

        Assert.False(session.Ciphers.First(c => c.Id == "c1").IsDeleted);
    }

    [Fact]
    public async Task DeleteCipherAsync_InDemoMode_Permanent_RemovesFromSessionStore()
    {
        var session = DemoSessionWith(LoginCipher("c1"));
        var svc = DemoService(session);

        await svc.DeleteCipherAsync("c1", permanent: true);

        Assert.DoesNotContain(session.Ciphers, c => c.Id == "c1");
    }

    [Fact]
    public async Task SaveFolderAsync_InDemoMode_AddsToSessionStore_WithoutHittingNetwork()
    {
        var session = DemoSessionWith(LoginCipher("c1"));
        var svc = DemoService(session);

        await svc.SaveFolderAsync(null, "工作");

        Assert.Contains(session.Folders, f => f.Name == "工作");
    }

    [Fact]
    public async Task DeleteFolderAsync_InDemoMode_RemovesFolderAndClearsReferences()
    {
        var session = DemoSessionWith(LoginCipher("c1").WithFolderId("f1"));
        session.UpsertFolder(new Folder { Id = "f1", Name = "工作", RevisionDate = DateTimeOffset.UtcNow });
        var svc = DemoService(session);

        await svc.DeleteFolderAsync("f1");

        Assert.DoesNotContain(session.Folders, f => f.Id == "f1");
        Assert.Null(session.Ciphers.First(c => c.Id == "c1").FolderId);
    }

    [Fact]
    public async Task MoveCiphersAsync_InDemoMode_UpdatesFolderIdWithoutHittingNetwork()
    {
        var session = DemoSessionWith(LoginCipher("c1"), LoginCipher("c2"));
        var svc = DemoService(session);

        await svc.MoveCiphersAsync(new[] { "c1", "c2" }, "f1");

        Assert.All(session.Ciphers, c => Assert.Equal("f1", c.FolderId));
    }

    [Fact]
    public async Task DeleteCiphersAsync_InDemoMode_Bulk_SoftAndPermanent()
    {
        var session = DemoSessionWith(LoginCipher("c1"), LoginCipher("c2"));
        var svc = DemoService(session);

        await svc.DeleteCiphersAsync(new[] { "c1" }, permanent: false);
        await svc.DeleteCiphersAsync(new[] { "c2" }, permanent: true);

        Assert.True(session.Ciphers.First(c => c.Id == "c1").IsDeleted);
        Assert.DoesNotContain(session.Ciphers, c => c.Id == "c2");
    }

    [Fact]
    public async Task RestoreCiphersAsync_InDemoMode_Bulk_ClearsDeletedDate()
    {
        var session = DemoSessionWith(LoginCipher("c1"));
        var svc = DemoService(session);
        await svc.DeleteCiphersAsync(new[] { "c1" }, permanent: false);

        await svc.RestoreCiphersAsync(new[] { "c1" });

        Assert.False(session.Ciphers.First(c => c.Id == "c1").IsDeleted);
    }

    [Fact]
    public async Task SyncAsync_InDemoMode_NoOp_WithoutHittingNetwork()
    {
        var session = DemoSessionWith(LoginCipher("c1"));
        var svc = DemoService(session);

        await svc.SyncAsync();
    }

    private sealed class ThrowingWriteService : IVaultWriteService
    {
        public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) =>
            throw new InvalidOperationException("network write must not run in demo mode");
        public Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default) =>
            throw new InvalidOperationException("network write must not run in demo mode");
        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) =>
            throw new InvalidOperationException("network write must not run in demo mode");
        public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) =>
            throw new InvalidOperationException("network write must not run in demo mode");
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) =>
            throw new InvalidOperationException("network write must not run in demo mode");
        public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) =>
            throw new InvalidOperationException("network write must not run in demo mode");
        public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) =>
            throw new InvalidOperationException("network write must not run in demo mode");
        public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
            throw new InvalidOperationException("network write must not run in demo mode");
    }

    private sealed class ThrowingSyncService : ISyncService
    {
        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("network sync must not run in demo mode");
    }
}
