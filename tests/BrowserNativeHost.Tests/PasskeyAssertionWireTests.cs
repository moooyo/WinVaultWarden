using System.Text.Json;
using Core.Passkeys;
using Xunit;

namespace BrowserNativeHost.Tests;

/// <summary>
/// Locks the on-wire field spellings of PasskeyGetAssertionPayload.
/// The browser extension reads these names with EXACT casing (e.g. "clientDataJSON" not "clientDataJson").
/// If any [JsonPropertyName] drifts or is removed, these assertions catch it independently of round-trip tests.
/// </summary>
public class PasskeyAssertionWireTests
{
    [Fact]
    public void PasskeyGetAssertionPayload_wire_fields_have_exact_casing()
    {
        var payload = new PasskeyGetAssertionPayload(
            CredentialId: "cid",
            AuthenticatorData: "authData",
            ClientDataJson: "cdj",
            Signature: "sig",
            UserHandle: "uh");

        var json = JsonSerializer.Serialize(
            payload,
            PasskeyJsonContext.Default.PasskeyGetAssertionPayload);

        // Exact wire spellings — browser extension is case-sensitive on all five.
        Assert.Contains("\"credentialId\"", json);
        Assert.Contains("\"authenticatorData\"", json);
        Assert.Contains("\"clientDataJSON\"", json); // capital JSON — not clientDataJson
        Assert.Contains("\"signature\"", json);
        Assert.Contains("\"userHandle\"", json);

        // Values are present too (sanity).
        Assert.Contains("\"cid\"", json);
        Assert.Contains("\"authData\"", json);
        Assert.Contains("\"cdj\"", json);
        Assert.Contains("\"sig\"", json);
        Assert.Contains("\"uh\"", json);
    }
}
