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
    public void ChangeKdf_pbkdf2_nulls_memory_and_parallelism()
    {
        var kdf = new KdfParams(0, 700000, null, null);
        var req = new ChangeKdfRequest("nh", "2.k",
            new AuthData("a@b.com", kdf, "nh"), new UnlockData("a@b.com", kdf, "2.k"), "oh");
        var json = JsonSerializer.Serialize(req, Web);
        Assert.Contains("\"kdfMemory\":null", json);
        Assert.Contains("\"kdfParallelism\":null", json);
        Assert.Contains("\"masterPasswordAuthenticationHash\":\"nh\"", json);
        Assert.Contains("\"masterKeyWrappedUserKey\":\"2.k\"", json);
        Assert.Contains("\"salt\":\"a@b.com\"", json);
    }
}
