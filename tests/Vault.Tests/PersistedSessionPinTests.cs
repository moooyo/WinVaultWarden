using Core.Enums;
using Core.Models;
using Xunit;

namespace Vault.Tests;

public class PersistedSessionPinTests
{
    private static PersistedSession Base() =>
        new("https://vault.example", "me@example.com", "device-id", "refresh",
            "2.protected-user-key", KdfType.Pbkdf2, 600_000, null, null);

    [Fact]
    public void Defaults_NoPin()
    {
        var s = Base();
        Assert.Null(s.PinProtectedUserKey);
        Assert.Equal(0, s.PinFailedAttempts);
    }

    [Fact]
    public void With_SetsPinFields_PreservesRest()
    {
        var s = Base() with { PinProtectedUserKey = "2.pin-enc", PinFailedAttempts = 3 };
        Assert.Equal("2.pin-enc", s.PinProtectedUserKey);
        Assert.Equal(3, s.PinFailedAttempts);
        Assert.Equal("me@example.com", s.Email);          // 位置字段不受影响
        Assert.Equal("2.protected-user-key", s.ProtectedUserKey);
    }
}
