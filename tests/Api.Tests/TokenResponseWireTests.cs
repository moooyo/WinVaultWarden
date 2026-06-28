using System.Text.Json;
using Api.Dtos;
using Core.Enums;
using Xunit;

namespace Api.Tests;

public class TokenResponseWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Deserializes_bitwarden_cased_token_fields()
    {
        const string json = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,"token_type":"Bearer",
         "scope":"api offline_access","Key":"2.abc","PrivateKey":"2.def","Kdf":0,"KdfIterations":600000}
        """;
        var token = JsonSerializer.Deserialize<TokenResponse>(json, Web)!;
        Assert.Equal("at", token.AccessToken);
        Assert.Equal("rt", token.RefreshToken);
        Assert.Equal(3600, token.ExpiresIn);
        Assert.Equal("2.abc", token.Key);
        Assert.Equal("2.def", token.PrivateKey);
        Assert.Equal(KdfType.Pbkdf2, token.Kdf);
        Assert.Equal(600000, token.KdfIterations);
    }

    [Fact]
    public void Deserializes_argon2id_kdf_memory_and_parallelism_pascalcase()
    {
        const string json = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,"token_type":"Bearer",
         "scope":"api offline_access","Key":"2.abc","PrivateKey":"2.def","Kdf":1,"KdfIterations":3,
         "KdfMemory":65536,"KdfParallelism":4}
        """;
        var token = JsonSerializer.Deserialize<TokenResponse>(json, Web)!;
        Assert.Equal(KdfType.Argon2id, token.Kdf);
        Assert.Equal(65536, token.KdfMemory);
        Assert.Equal(4, token.KdfParallelism);
    }

    [Fact]
    public void TwoFactor_error_reads_providers_from_string_numbers()
    {
        const string json = """{"error":"invalid_grant","error_description":"Two factor required.","TwoFactorProviders":["0"]}""";
        var error = JsonSerializer.Deserialize<ConnectTokenErrorResponse>(json, Web)!;
        Assert.Equal("Two factor required.", error.ErrorDescription);
        Assert.Equal(new[] { 0 }, error.TwoFactorProviders);
    }
}
