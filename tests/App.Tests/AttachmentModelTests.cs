using App.Services;
using App.ViewModels.Models;
using Xunit;

namespace App.Tests;

public class AttachmentModelTests
{
    [Fact]
    public void AttachmentItem_Glyph_DerivedFromFileName()
    {
        var img = new AttachmentItem("1", "photo.png", "1 KB");
        Assert.Equal(AttachmentGlyph.ForFileName("photo.png"), img.Glyph);
        Assert.NotEqual(new AttachmentItem("2", "a.pdf", "1 KB").Glyph, img.Glyph);
    }

    [Fact]
    public void CipherDetail_Count_Header_HasAttachments()
    {
        var empty = new NoteDetail { Id = "1", Name = "n" };
        Assert.Equal(0, empty.AttachmentCount);
        Assert.False(empty.HasAttachments);
        Assert.Equal("附件", empty.AttachmentHeader);

        var two = new NoteDetail
        {
            Id = "2", Name = "n",
            Attachments = new[] { new AttachmentItem("a", "x.txt", "1 KB"), new AttachmentItem("b", "y.txt", "1 KB") },
        };
        Assert.Equal(2, two.AttachmentCount);
        Assert.True(two.HasAttachments);
        Assert.Equal("附件 (2)", two.AttachmentHeader);
    }
}
