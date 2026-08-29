using System.Text.Json;
using Api.Dtos;
using Core.Enums;
using Xunit;

namespace Api.Tests;

public sealed class PreloginWireTests
{
    [Fact]
    public void OfficialNestedShape_PreservesExplicitSaltExactly()
    {
        const string json =
            """{"kdfSettings":{"kdfType":1,"iterations":3,"memory":64,"parallelism":4},"salt":" Case-Sensitive Salt "}""";

        var response = JsonSerializer.Deserialize(json, ApiJsonContext.Default.PreloginResponse)!;
        var resolved = response.Resolve(" Mixed@Example.COM ");

        Assert.Equal(KdfType.Argon2id, resolved.KdfType);
        Assert.Equal(3, resolved.Iterations);
        Assert.Equal(64, resolved.Memory);
        Assert.Equal(4, resolved.Parallelism);
        Assert.Equal(" Case-Sensitive Salt ", resolved.MasterPasswordSalt);
    }

    [Fact]
    public void VaultwardenLegacyShape_FallsBackToNormalizedEmail()
    {
        const string json =
            """{"kdf":0,"kdfIterations":600000,"kdfMemory":null,"kdfParallelism":null}""";

        var response = JsonSerializer.Deserialize(json, ApiJsonContext.Default.PreloginResponse)!;
        var resolved = response.Resolve(" Mixed@Example.COM ");

        Assert.Equal(KdfType.Pbkdf2, resolved.KdfType);
        Assert.Equal(600_000, resolved.Iterations);
        Assert.Equal("mixed@example.com", resolved.MasterPasswordSalt);
    }

    [Fact]
    public void MatchingLegacyAndNestedShapes_UseNestedSettings()
    {
        const string json =
            """{"kdf":0,"kdfIterations":600000,"kdfMemory":null,"kdfParallelism":null,"kdfSettings":{"kdfType":0,"iterations":600000,"memory":null,"parallelism":null},"salt":null}""";

        var response = JsonSerializer.Deserialize(json, ApiJsonContext.Default.PreloginResponse)!;
        var resolved = response.Resolve("USER@EXAMPLE.COM");

        Assert.Equal(KdfType.Pbkdf2, resolved.KdfType);
        Assert.Equal(600_000, resolved.Iterations);
        Assert.Equal("user@example.com", resolved.MasterPasswordSalt);
    }

    [Fact]
    public void ConflictingCompleteShapes_AreRejected()
    {
        const string json =
            """{"kdf":0,"kdfIterations":600000,"kdfSettings":{"kdfType":0,"iterations":700000}}""";

        var response = JsonSerializer.Deserialize(json, ApiJsonContext.Default.PreloginResponse)!;

        Assert.Throws<InvalidDataException>(() => response.Resolve("user@example.com"));
    }

    [Fact]
    public void PresentButIncompleteNestedShape_DoesNotFallBackToLegacy()
    {
        const string json =
            """{"kdf":0,"kdfIterations":600000,"kdfSettings":{"kdfType":0}}""";

        var response = JsonSerializer.Deserialize(json, ApiJsonContext.Default.PreloginResponse)!;

        Assert.Throws<InvalidDataException>(() => response.Resolve("user@example.com"));
    }

    [Fact]
    public void CompleteNestedShape_IgnoresIncompleteLegacyShape()
    {
        const string json =
            """{"kdf":1,"kdfSettings":{"kdfType":0,"iterations":600000}}""";

        var response = JsonSerializer.Deserialize(json, ApiJsonContext.Default.PreloginResponse)!;
        var resolved = response.Resolve("user@example.com");

        Assert.Equal(KdfType.Pbkdf2, resolved.KdfType);
        Assert.Equal(600_000, resolved.Iterations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExplicitEmptyOrWhitespaceSalt_IsRejected(string salt)
    {
        var response = new PreloginResponse(
            KdfType.Pbkdf2,
            600_000,
            null,
            null,
            Salt: salt);

        Assert.Throws<InvalidDataException>(() => response.Resolve("user@example.com"));
    }

    [Fact]
    public void ExplicitSaltLongerThanProtocolLimit_IsRejected()
    {
        var response = new PreloginResponse(
            KdfType.Pbkdf2,
            600_000,
            null,
            null,
            Salt: new string('s', 257));

        Assert.Throws<InvalidDataException>(() => response.Resolve("user@example.com"));
    }
}
