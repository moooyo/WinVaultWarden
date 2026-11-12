using System.Reflection;
using System.Text.Json;
using Core.Passkeys;
using Xunit;

namespace BrowserNativeHost.Tests;

public sealed class ProtocolContractTests
{
    [Fact]
    public void ErrorEnvelope_ContainsCodeOnly()
    {
        var response = BrowserPasskeyResponse.Failure(
            "00000000000000000000000000000001",
            PasskeyErrorCodes.SecurityInvalidRp);
        var json = JsonSerializer.Serialize(
            response,
            PasskeyJsonContext.Default.BrowserPasskeyResponse);

        Assert.Contains("\"v\":1", json);
        Assert.Contains("\"code\":\"security_invalid_rp\"", json);
        Assert.DoesNotContain("\"message\"", json);
        Assert.DoesNotContain("exception", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeResponse_EmitsProtocolVersion()
    {
        var response = new BrowserNativeHost.NativeResponse("00000000000000000000000000000001", "error", false,
            Error: new BrowserNativeHost.NativeError("invalid_request"));
        var json = JsonSerializer.Serialize(response, BrowserNativeHost.NativeMessageJsonContext.Default.NativeResponse);

        Assert.Contains("\"v\":1", json);
        Assert.DoesNotContain("message", json);
    }

    [Fact]
    public void CoreParser_RejectsUnsupportedProtocolVersion()
    {
        var request = new BrowserPasskeyRequest("00000000000000000000000000000001", "passkey.get", Version: 2);

        Assert.False(PasskeyRequestParser.TryParse(request, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.Equal("invalid_request", error!.Code);
    }

    [Fact]
    public void ProtocolSchema_ContainsNoCredentialOrSessionSecrets()
    {
        var schema = File.ReadAllText(FindRepoFile("protocol", "browser-passkey-v1.schema.json"));

        Assert.DoesNotContain("accessToken", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("masterPassword", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtocolSchema_PinsV1RequestCancelResponseEnvelope()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(FindRepoFile("protocol", "browser-passkey-v1.schema.json")));
        var defs = doc.RootElement.GetProperty("$defs");

        Assert.True(defs.TryGetProperty("request", out _));
        Assert.True(defs.TryGetProperty("cancel", out _));
        Assert.True(defs.TryGetProperty("response", out _));
        Assert.True(defs.TryGetProperty("ping", out _));
        Assert.True(defs.TryGetProperty("pong", out _));

        var requestVersion = defs
            .GetProperty("request").GetProperty("properties")
            .GetProperty("v").GetProperty("const").GetInt32();
        Assert.Equal(1, requestVersion);

        // The wire payload must never let the page assert its own origin/frame context.
        var forbidden = defs.GetProperty("pagePayload").GetProperty("not").GetProperty("anyOf")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("required")[0].GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("origin", forbidden);
        Assert.Contains("crossOrigin", forbidden);
        Assert.Contains("frameOrigin", forbidden);
        Assert.Contains("topOrigin", forbidden);
        Assert.Contains("sameOriginWithAncestors", forbidden);

        var nativeProperties = defs.GetProperty("payload").GetProperty("properties");
        Assert.True(nativeProperties.TryGetProperty("origin", out _));
        Assert.True(nativeProperties.TryGetProperty("crossOrigin", out _));
        Assert.True(nativeProperties.TryGetProperty("topOrigin", out _));
        var contextRequired = defs.GetProperty("context").GetProperty("required")
            .EnumerateArray().Select(property => property.GetString()).ToArray();
        Assert.Contains("sameOriginWithAncestors", contextRequired);
        Assert.Contains("publicKeyCredentialsCreateAllowed", contextRequired);
    }

    [Theory]
    [InlineData("sameOriginWithAncestors")]
    [InlineData("publicKeyCredentialsCreateAllowed")]
    public void TrustedContext_RejectsMissingRequiredPolicyEvidence(string missingProperty)
    {
        var context = new BrowserRequestContext("https://example.com", "https://example.com", 1, 0,
            "document-1", true, true, true);
        var encoded = JsonSerializer.SerializeToElement(context, PasskeyJsonContext.Default.BrowserRequestContext);
        var values = encoded.EnumerateObject()
            .Where(property => property.Name != missingProperty)
            .ToDictionary(property => property.Name, property => property.Value);
        var json = JsonSerializer.Serialize(values);

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, PasskeyJsonContext.Default.BrowserRequestContext));
    }

    [Fact]
    public void ErrorTable_MatchesErrorCodeConstants()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(FindRepoFile("protocol", "passkey-errors.json")));
        var tableCodes = doc.RootElement.GetProperty("errors")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("code").GetString())
            .Where(code => code is not null)
            .Select(code => code!)
            .ToHashSet(StringComparer.Ordinal);

        var constantCodes = typeof(PasskeyErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(constantCodes, tableCodes);
    }

    [Fact]
    public void ErrorTable_MapsOnlyClientNotHandledToFallback()
    {
        using var doc = JsonDocument.Parse(
            File.ReadAllText(FindRepoFile("protocol", "passkey-errors.json")));

        foreach (var entry in doc.RootElement.GetProperty("errors").EnumerateArray())
        {
            var code = entry.GetProperty("code").GetString();
            var fallback = entry.GetProperty("fallback").GetBoolean();
            var domException = entry.GetProperty("domException");

            if (code == PasskeyErrorCodes.ClientNotHandled)
            {
                Assert.True(fallback);
                Assert.Equal(JsonValueKind.Null, domException.ValueKind);
            }
            else
            {
                Assert.False(fallback);
                Assert.Equal(JsonValueKind.String, domException.ValueKind);
                Assert.False(string.IsNullOrWhiteSpace(domException.GetString()));
            }
        }
    }

    private static string FindRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', segments)} from {AppContext.BaseDirectory}.");
    }
}
