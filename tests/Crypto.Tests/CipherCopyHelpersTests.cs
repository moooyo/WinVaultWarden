using Core.Enums;
using Core.Models;
using Xunit;

namespace Crypto.Tests;

public class CipherCopyHelpersTests
{
    private static Cipher Sample() => new()
    {
        Id = "c1", Type = CipherType.Login, Name = "N", FolderId = "f1",
        CreationDate = DateTimeOffset.UnixEpoch, RevisionDate = DateTimeOffset.UnixEpoch,
        Login = new CipherLogin("u", "p", null, System.Array.Empty<CipherLoginUri>()),
    };

    [Fact]
    public void WithDeletedDate_SetsAndClears_PreservesRest()
    {
        var d = new DateTimeOffset(2026, 6, 1, 0, 0, 0, System.TimeSpan.Zero);
        var deleted = Sample().WithDeletedDate(d);
        Assert.Equal(d, deleted.DeletedDate);
        Assert.True(deleted.IsDeleted);
        Assert.Equal("c1", deleted.Id);
        Assert.Equal("N", deleted.Name);
        Assert.Equal("p", deleted.Login!.Password);
        Assert.Null(deleted.WithDeletedDate(null).DeletedDate);
    }

    [Fact]
    public void WithMetadata_SetsIdAndDates_PreservesRest()
    {
        var now = new DateTimeOffset(2026, 7, 9, 12, 0, 0, System.TimeSpan.Zero);
        var c = Sample().WithMetadata("new-id", now, now);
        Assert.Equal("new-id", c.Id);
        Assert.Equal(now, c.CreationDate);
        Assert.Equal(now, c.RevisionDate);
        Assert.Equal("N", c.Name);
        Assert.Equal("f1", c.FolderId);
    }
}
