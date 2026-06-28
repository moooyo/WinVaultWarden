using App.Services;
using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Xunit;

namespace App.Tests;

public class RealVaultUiServiceAttachmentTests
{
    [Fact]
    public void GetDetail_WithAttachments_MapsCoreAttachmentsToAttachmentItems()
    {
        var service = new VaultUiService(new AttachmentVaultService(), new NoopWrite(), new NoopSync());

        var login = Assert.IsType<LoginDetail>(service.GetDetail("withatt"));

        Assert.Equal(2, login.Attachments.Count);
        Assert.Equal("a1", login.Attachments[0].Id);
        Assert.Equal("report.pdf", login.Attachments[0].FileName);
        Assert.Equal("1.2 KB", login.Attachments[0].SizeName);
        Assert.Equal("a2", login.Attachments[1].Id);
        Assert.Equal("photo.png", login.Attachments[1].FileName);
        Assert.Equal("3.4 MB", login.Attachments[1].SizeName);
    }

    [Fact]
    public void GetDetail_WithoutAttachments_ReturnsEmptyAttachmentList()
    {
        var service = new VaultUiService(new AttachmentVaultService(), new NoopWrite(), new NoopSync());

        var login = Assert.IsType<LoginDetail>(service.GetDetail("noatt"));

        Assert.Empty(login.Attachments);
    }

    private sealed class AttachmentVaultService : IVaultService
    {
        private static readonly IReadOnlyList<Cipher> _ciphers =
        [
            new Cipher
            {
                Id = "withatt",
                Type = CipherType.Login,
                Name = "Has Attachments",
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                Login = new CipherLogin("u", null, null, []),
                Attachments =
                [
                    new CipherAttachment("a1", "report.pdf", 1234, "1.2 KB"),
                    new CipherAttachment("a2", "photo.png", 3_500_000, "3.4 MB"),
                ],
            },
            new Cipher
            {
                Id = "noatt",
                Type = CipherType.Login,
                Name = "No Attachments",
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                Login = new CipherLogin("u", null, null, []),
            },
        ];

        public IReadOnlyList<Cipher> GetCiphers() => _ciphers;
        public IReadOnlyList<Folder> GetFolders() => Array.Empty<Folder>();
        public IReadOnlyList<DeviceInfo> GetDevices() => Array.Empty<DeviceInfo>();
        public IVaultSnapshot Snapshot => throw new NotSupportedException();
    }

    private sealed class NoopWrite : IVaultWriteService
    {
        public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopSync : ISyncService
    {
        public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Cipher>>(Array.Empty<Cipher>());
    }
}
