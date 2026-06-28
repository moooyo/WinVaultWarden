using App.Services;
using App.ViewModels.Models;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public class RealAttachmentUiServiceTests
{
    private static CipherAttachment Core(string id, string name, string sizeName) =>
        new(id, name, 1234, sizeName);

    [Fact]
    public async Task AddAttachmentAsync_DelegatesToServiceAndMapsResultToAttachmentItems()
    {
        var fake = new FakeAttachmentService
        {
            UploadResult = [Core("a1", "report.pdf", "1.2 KB")],
        };
        var svc = new AttachmentUiService(fake);
        var bytes = new byte[] { 1, 2, 3, 4 };

        var items = await svc.AddAttachmentAsync("cipher-1", bytes, "report.pdf", CancellationToken.None);

        Assert.Equal("cipher-1", fake.LastCipherId);
        Assert.Equal("report.pdf", fake.LastFileName);
        Assert.Same(bytes, fake.LastPlaintext);
        var item = Assert.Single(items);
        Assert.Equal("a1", item.Id);
        Assert.Equal("report.pdf", item.FileName);
        Assert.Equal("1.2 KB", item.SizeName);
    }

    [Fact]
    public async Task DownloadAttachmentAsync_DelegatesToServiceAndReturnsBytes()
    {
        var fake = new FakeAttachmentService { DownloadResult = new byte[] { 9, 8, 7 } };
        var svc = new AttachmentUiService(fake);

        var bytes = await svc.DownloadAttachmentAsync("cipher-1", "a1", CancellationToken.None);

        Assert.Equal("cipher-1", fake.LastCipherId);
        Assert.Equal("a1", fake.LastAttachmentId);
        Assert.Equal(new byte[] { 9, 8, 7 }, bytes);
    }

    [Fact]
    public async Task DeleteAttachmentAsync_DelegatesToServiceAndMapsRemainingAttachments()
    {
        var fake = new FakeAttachmentService
        {
            DeleteResult = [Core("a2", "photo.png", "3.4 MB")],
        };
        var svc = new AttachmentUiService(fake);

        var items = await svc.DeleteAttachmentAsync("cipher-1", "a1", CancellationToken.None);

        Assert.Equal("cipher-1", fake.LastCipherId);
        Assert.Equal("a1", fake.LastAttachmentId);
        var item = Assert.Single(items);
        Assert.Equal("a2", item.Id);
        Assert.Equal("photo.png", item.FileName);
        Assert.Equal("3.4 MB", item.SizeName);
    }

    private sealed class FakeAttachmentService : IAttachmentService
    {
        public string? LastCipherId { get; private set; }
        public string? LastFileName { get; private set; }
        public byte[]? LastPlaintext { get; private set; }
        public string? LastAttachmentId { get; private set; }

        public IReadOnlyList<CipherAttachment> UploadResult { get; set; } = Array.Empty<CipherAttachment>();
        public byte[] DownloadResult { get; set; } = Array.Empty<byte>();
        public IReadOnlyList<CipherAttachment> DeleteResult { get; set; } = Array.Empty<CipherAttachment>();

        public Task<IReadOnlyList<CipherAttachment>> UploadAsync(string cipherId, string fileName, byte[] plaintext, CancellationToken ct = default)
        {
            LastCipherId = cipherId;
            LastFileName = fileName;
            LastPlaintext = plaintext;
            return Task.FromResult(UploadResult);
        }

        public Task<byte[]> DownloadAsync(string cipherId, string attachmentId, CancellationToken ct = default)
        {
            LastCipherId = cipherId;
            LastAttachmentId = attachmentId;
            return Task.FromResult(DownloadResult);
        }

        public Task<IReadOnlyList<CipherAttachment>> DeleteAsync(string cipherId, string attachmentId, CancellationToken ct = default)
        {
            LastCipherId = cipherId;
            LastAttachmentId = attachmentId;
            return Task.FromResult(DeleteResult);
        }
    }
}
