using Core.Models;
using Core.Services;
using Xunit;

namespace Vault.Tests;

public class AttachmentDomainTests
{
    [Fact]
    public void CipherAttachment_CarriesIdFileNameSizeAndSizeName()
    {
        var att = new CipherAttachment("att-1", "report.pdf", 12_345, "12.06 KB");

        Assert.Equal("att-1", att.Id);
        Assert.Equal("report.pdf", att.FileName);
        Assert.Equal(12_345, att.Size);
        Assert.Equal("12.06 KB", att.SizeName);
    }

    [Fact]
    public void Cipher_DefaultConstructs_WithEmptyAttachments()
    {
        var cipher = new Cipher();

        Assert.NotNull(cipher.Attachments);
        Assert.Empty(cipher.Attachments);
    }

    [Fact]
    public void Cipher_CanCarryAttachments()
    {
        var cipher = new Cipher
        {
            Id = "c1",
            Name = "with files",
            Attachments = new[]
            {
                new CipherAttachment("att-1", "a.txt", 10, "10 Bytes"),
                new CipherAttachment("att-2", "b.png", 2_048, "2 KB"),
            },
        };

        Assert.Equal(2, cipher.Attachments.Count);
        Assert.Equal("att-1", cipher.Attachments[0].Id);
        Assert.Equal("b.png", cipher.Attachments[1].FileName);
        Assert.Equal(2_048, cipher.Attachments[1].Size);
        Assert.Equal(10, cipher.Attachments[0].Size);
        Assert.Equal("10 Bytes", cipher.Attachments[0].SizeName);
    }

    [Fact]
    public void AttachmentTooLargeException_CarriesActualAndMaxBytes_AndMessageMentionsBoth()
    {
        const long actual = 150L * 1024 * 1024;
        const long max = 100L * 1024 * 1024;

        var ex = new AttachmentTooLargeException(actual, max);

        Assert.Equal(actual, ex.ActualBytes);
        Assert.Equal(max, ex.MaxBytes);
        Assert.Contains(actual.ToString(), ex.Message);
        Assert.Contains(max.ToString(), ex.Message);
    }
}
