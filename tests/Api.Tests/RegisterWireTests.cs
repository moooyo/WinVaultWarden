using System.Text.Json; using Api.Dtos; using Xunit;
namespace Api.Tests;
public class RegisterWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    [Fact] public void Register_is_camelCase_with_flattened_kdf_and_nested_keys()
    {
        var req = new RegisterRequest("a@b.com", "Bob", "mph", "hint", "2.k", 0, 600000, null, null,
            new RegisterKeys("2.epk", "spki"));
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"email\":\"a@b.com\"", json);
        Assert.Contains("\"masterPasswordHash\":\"mph\"", json);
        Assert.Contains("\"masterPasswordHint\":\"hint\"", json);
        Assert.Contains("\"key\":\"2.k\"", json);
        Assert.Contains("\"kdf\":0", json);
        Assert.Contains("\"kdfIterations\":600000", json);
        Assert.Contains("\"kdfMemory\":null", json);
        Assert.Contains("\"kdfParallelism\":null", json);
        Assert.Contains("\"name\":\"Bob\"", json);
        Assert.Contains("\"keys\":{", json);
        Assert.Contains("\"encryptedPrivateKey\":\"2.epk\"", json);
        Assert.Contains("\"publicKey\":\"spki\"", json);
    }
    [Fact] public void Register_context_matches_reflection()
    {
        var req = new RegisterRequest("a@b.com", null, "mph", null, "2.k", 0, 600000, null, null, new RegisterKeys("2.epk","spki"));
        Assert.Equal(JsonSerializer.Serialize(req, Web), JsonSerializer.Serialize(req, ApiJsonContext.Default.RegisterRequest));
    }
}
