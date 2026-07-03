using Core.Models;
using Xunit;

namespace Vault.Tests;

public class VaultCacheServiceTests
{
    [Fact]
    public void VaultCacheEntry_HoldsFields()
    {
        var e = new VaultCacheEntry(1, "me@x", "https://v", DateTimeOffset.UnixEpoch, "{}");
        Assert.Equal(1, e.SchemaVersion);
        Assert.Equal("me@x", e.Email);
    }
}
