using App.Services;
using Xunit;

namespace App.Tests;

public class AttachmentGlyphTests
{
    [Fact]
    public void SameCategory_ShareGlyph()
    {
        Assert.Equal(AttachmentGlyph.ForFileName("a.jpg"), AttachmentGlyph.ForFileName("b.png"));
        Assert.Equal(AttachmentGlyph.ForFileName("a.zip"), AttachmentGlyph.ForFileName("b.7z"));
        Assert.Equal(AttachmentGlyph.ForFileName("a.mp3"), AttachmentGlyph.ForFileName("b.flac"));
    }

    [Fact]
    public void DifferentCategories_DifferentGlyphs()
    {
        Assert.NotEqual(AttachmentGlyph.ForFileName("a.jpg"), AttachmentGlyph.ForFileName("a.pdf"));
        Assert.NotEqual(AttachmentGlyph.ForFileName("a.pdf"), AttachmentGlyph.ForFileName("a.mp4"));
        Assert.NotEqual(AttachmentGlyph.ForFileName("a.xlsx"), AttachmentGlyph.ForFileName("a.jpg"));
    }

    [Fact]
    public void CaseInsensitive()
    {
        Assert.Equal(AttachmentGlyph.ForFileName("a.jpg"), AttachmentGlyph.ForFileName("A.JPG"));
    }

    [Fact]
    public void UnknownOrNoExtension_UsesGeneric()
    {
        var generic = AttachmentGlyph.ForFileName("file.unknownext");
        Assert.Equal(generic, AttachmentGlyph.ForFileName("noextension"));
        Assert.Equal(generic, AttachmentGlyph.ForFileName(""));
        Assert.Equal(generic, AttachmentGlyph.ForFileName(null));
        Assert.Equal(generic, AttachmentGlyph.ForFileName("trailingdot."));
    }
}
