using System.Text.Json;
using Api.Dtos;
using Xunit;

namespace Api.Tests;

public class AccountWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Profile_is_camelCase()
    {
        var json = JsonSerializer.Serialize(new ProfileUpdateRequest("Bob", "en-US"), Web);
        Assert.Contains("\"name\":\"Bob\"", json);
        Assert.Contains("\"culture\":\"en-US\"", json);
    }

    [Fact]
    public void ChangePassword_camelCase_keeps_key_and_hashes()
    {
        var json = JsonSerializer.Serialize(new ChangePasswordRequest("old", "new", "hint", "2.k"), Web);
        Assert.Contains("\"masterPasswordHash\":\"old\"", json);
        Assert.Contains("\"newMasterPasswordHash\":\"new\"", json);
        Assert.Contains("\"masterPasswordHint\":\"hint\"", json);
        Assert.Contains("\"key\":\"2.k\"", json);
    }

    [Fact]
    public void ChangePassword_keeps_legacy_fields_and_adds_official_nested_payloads()
    {
        var kdf = new KdfParams(1, 3, 64, 4);
        var request = new ChangePasswordRequest(
            "old",
            "new",
            "hint",
            "2.k",
            new AuthData("Case-Sensitive Salt", kdf, "new"),
            new UnlockData("Case-Sensitive Salt", kdf, "2.k"));

        var json = JsonSerializer.Serialize(request, Web);

        Assert.Contains("\"newMasterPasswordHash\":\"new\"", json);
        Assert.Contains("\"key\":\"2.k\"", json);
        Assert.Contains("\"authenticationData\"", json);
        Assert.Contains("\"unlockData\"", json);
        Assert.Contains("\"kdfType\":1", json);
        Assert.Contains("\"salt\":\"Case-Sensitive Salt\"", json);
    }

    [Fact]
    public void ChangeKdf_pbkdf2_nulls_memory_and_parallelism()
    {
        var kdf = new KdfParams(0, 700000, null, null);
        var req = new ChangeKdfRequest("nh", "2.k",
            new AuthData("a@b.com", kdf, "nh"), new UnlockData("a@b.com", kdf, "2.k"), "oh");
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"kdfType\":0", json);
        Assert.Contains("\"iterations\":700000", json);
        Assert.Contains("\"memory\":null", json);
        Assert.Contains("\"parallelism\":null", json);
        Assert.DoesNotContain("\"kdfIterations\"", json);
        Assert.Contains("\"masterPasswordAuthenticationHash\":\"nh\"", json);
        Assert.Contains("\"masterKeyWrappedUserKey\":\"2.k\"", json);
        Assert.Contains("\"salt\":\"a@b.com\"", json);
    }
}
