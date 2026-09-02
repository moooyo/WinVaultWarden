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
    public void CipherRequest_archivedDate_uses_camelCase_in_source_generated_wire()
    {
        var request = new CipherRequest
        {
            Type = 1,
            Name = "n",
            ArchivedDate = new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero),
        };

        var generated = JsonSerializer.Serialize(request, ApiJsonContext.Default.CipherRequest);

        Assert.Contains("\"archivedDate\":\"2026-08-01T12:30:00+00:00\"", generated);
        Assert.Equal(JsonSerializer.Serialize(request, Web), generated);
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

    [Theory]
    [InlineData("securityStamp")]
    [InlineData("SecurityStamp")]
    public void SyncResponse_context_reads_security_stamp(string propertyName)
    {
        var json = $$"""
        {
          "object": "sync",
          "profile": {
            "id": "user-1",
            "{{propertyName}}": "stamp-1"
          }
        }
        """;

        var response = JsonSerializer.Deserialize(
            json,
            ApiJsonContext.Default.SyncResponse);

        Assert.Equal("stamp-1", response!.Profile!.SecurityStamp);
    }

    [Fact]
    public void CipherRequest_allNestedWriteTypes_context_matches_reflection_wire()
    {
        // 把 byte-lock 扩展到**全部**嵌套写类型的 camelCase + null 写入策略(此前仅锁 CipherRequest+Login,R21):
        // 任一 source-gen 与反射分歧都是真实线格契约 bug。do NOT weaken。
        var req = new CipherRequest
        {
            Type = 1,
            Name = "n",
            Notes = "note",
            FolderId = "f",
            Favorite = true,
            Reprompt = 1,
            Fields = new[] { new FieldRequest { Type = 1, Name = "fn", Value = "fv" } },
            Login = new LoginRequest
            {
                Username = "u",
                Password = "p",
                Totp = "t",
                Uris = new[] { new LoginUriRequest { Uri = "https://x", Match = 0 } },
                Fido2Credentials = new[] { new Fido2CredentialRequest { CredentialId = "c", KeyType = "public-key" } },
            },
            Card = new CardRequest { CardholderName = "cn", Number = "4111", ExpMonth = "1", ExpYear = "2030", Code = "123", Brand = "Visa" },
            Identity = new IdentityRequest { Title = "Mr", FirstName = "J", LastName = "D" },
            SecureNote = new SecureNoteRequest { Type = 0 },
            SshKey = new SshKeyRequest { PrivateKey = "pk", PublicKey = "pub", KeyFingerprint = "fp" },
            PasswordHistory = new[] { new PasswordHistoryRequest { Password = "old", LastUsedDate = "2026-01-01T00:00:00Z" } },
        };
        Assert.Equal(
            JsonSerializer.Serialize(req, Web),
            JsonSerializer.Serialize(req, ApiJsonContext.Default.CipherRequest));
    }
}
