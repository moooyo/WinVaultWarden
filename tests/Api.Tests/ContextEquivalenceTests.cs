using System.Text.Json;
using Api;
using Api.Dtos;
using Xunit;

namespace Api.Tests;

public class ContextEquivalenceTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void CipherRequest_context_matches_reflection_wire()
    {
        var req = new CipherRequest { Type = 1, Name = "n" };
        Assert.Equal(
            JsonSerializer.Serialize(req, Web),
            JsonSerializer.Serialize(req, ApiJsonContext.Default.CipherRequest));
    }

    [Fact]
    public void CipherRequest_with_nested_LoginRequest_context_matches_reflection_wire()
    {
        // Nested types rely on the camelCase POLICY (no explicit [JsonPropertyName]).
        // This assertion locks nested camelCase + null-write behaviour: if source-gen
        // ever diverges from reflection the bytes will differ and the test will fail
        // (do NOT weaken — a mismatch here is a real wire-contract bug).
        var req = new CipherRequest
        {
            Type = 1,
            Name = "n",
            Login = new LoginRequest { Username = "u", Password = "p" },
        };
        Assert.Equal(
            JsonSerializer.Serialize(req, Web),
            JsonSerializer.Serialize(req, ApiJsonContext.Default.CipherRequest));
    }

    [Fact]
    public void TokenResponse_context_roundtrip_keeps_pascalcase_keys()
    {
        const string json = """{"access_token":"at","refresh_token":"rt","expires_in":1,"Key":"2.k","Kdf":0,"KdfIterations":5}""";
        var token = JsonSerializer.Deserialize(json, ApiJsonContext.Default.TokenResponse)!;
        Assert.Equal("2.k", token.Key);
        Assert.Equal(5, token.KdfIterations);
    }
}
