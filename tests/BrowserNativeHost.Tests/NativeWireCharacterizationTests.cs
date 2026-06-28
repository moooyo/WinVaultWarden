using System.Text.Json;
using System.Text.Json.Serialization;
using BrowserNativeHost;
using Core.Passkeys;
using Xunit;

namespace BrowserNativeHost.Tests;

public class NativeWireCharacterizationTests
{
    private static readonly JsonSerializerOptions Proto = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void NativeError_response_omits_null_payload_camelCase()
    {
        var resp = new NativeResponse("id1", "error", false, Error: new NativeError("c", "m"));
        var json = JsonSerializer.Serialize(resp, Proto);
        Assert.Contains("\"id\":\"id1\"", json);
        Assert.Contains("\"ok\":false", json);
        Assert.Contains("\"error\":{\"code\":\"c\",\"message\":\"m\"}", json);
        Assert.DoesNotContain("payload", json); // WhenWritingNull 省略
    }

    [Fact]
    public void PasskeyGetPayload_reads_camelCase_case_insensitively()
    {
        const string json = """{"origin":"https://e.com","challenge":"AAAA","allowCredentials":[],"userVerification":"required"}""";
        var payload = JsonSerializer.Deserialize<PasskeyGetPayload>(json, Proto)!;
        Assert.Equal("https://e.com", payload.Origin);
        Assert.Equal("required", payload.UserVerification);
    }
}
