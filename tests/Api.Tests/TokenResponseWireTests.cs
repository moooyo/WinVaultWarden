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

    [Fact]
    public void Device_error_reads_nested_error_model_message()
    {
        const string json = """
        {"error":"device_error","error_description":"New device verification required",
         "ErrorModel":{"Message":"new device verification required","Object":"error"}}
        """;

        var error = JsonSerializer.Deserialize<ConnectTokenErrorResponse>(json, Web)!;

        Assert.Equal("device_error", error.Error);
        Assert.Equal("New device verification required", error.ErrorDescription);
        Assert.Equal("new device verification required", error.ErrorModel!.Message);
    }

    [Fact]
    public void Deserializes_TwoFactorToken_pascalcase()
    {
        const string json = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,"token_type":"Bearer",
         "scope":"api offline_access","Key":"2.abc","Kdf":0,"KdfIterations":600000,"TwoFactorToken":"remember-jwt"}
        """;
        var token = JsonSerializer.Deserialize<TokenResponse>(json, Web)!;
        Assert.Equal("remember-jwt", token.TwoFactorToken);
    }

    [Fact]
    public void TwoFactorToken_absent_defaults_null()
    {
        const string json = """{"access_token":"at","refresh_token":"rt","expires_in":3600,"Kdf":0,"KdfIterations":600000}""";
        var token = JsonSerializer.Deserialize<TokenResponse>(json, Web)!;
        Assert.Null(token.TwoFactorToken);
    }

    [Fact]
    public void Deserializes_force_password_reset_and_master_password_policy()
    {
        const string json = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,"token_type":"Bearer",
         "scope":"api offline_access","Key":"2.abc","Kdf":0,"KdfIterations":600000,
         "ForcePasswordReset":true,"MasterPasswordPolicy":{"Object":"masterPasswordPolicy",
         "MinComplexity":3,"MinLength":14,"RequireUpper":true,"RequireLower":true,
         "RequireNumbers":true,"RequireSpecial":true,"EnforceOnLogin":true}}
        """;

        var token = JsonSerializer.Deserialize<TokenResponse>(json, Web)!;

        Assert.True(token.ForcePasswordReset);
        Assert.Equal(3, token.MasterPasswordPolicy!.MinComplexity);
        Assert.Equal(14, token.MasterPasswordPolicy.MinLength);
        Assert.True(token.MasterPasswordPolicy.RequireUpper);
        Assert.True(token.MasterPasswordPolicy.RequireLower);
        Assert.True(token.MasterPasswordPolicy.RequireNumbers);
        Assert.True(token.MasterPasswordPolicy.RequireSpecial);
        Assert.True(token.MasterPasswordPolicy.EnforceOnLogin);
        Assert.Equal("masterPasswordPolicy", token.MasterPasswordPolicy.Object);
    }

    [Fact]
    public void Empty_master_password_policy_preserves_unknown_fields_as_null()
    {
        const string json = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,"token_type":"Bearer",
         "scope":"api offline_access","Key":"2.abc","Kdf":0,"KdfIterations":600000,
         "MasterPasswordPolicy":{"Object":"masterPasswordPolicy"}}
        """;

        var token = JsonSerializer.Deserialize<TokenResponse>(json, Web)!;

        Assert.False(token.ForcePasswordReset);
        Assert.NotNull(token.MasterPasswordPolicy);
        Assert.Null(token.MasterPasswordPolicy!.MinComplexity);
        Assert.Null(token.MasterPasswordPolicy.MinLength);
        Assert.Null(token.MasterPasswordPolicy.EnforceOnLogin);
    }

    [Fact]
    public void Deserializes_master_password_sso_decryption_option()
    {
        const string json = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,"token_type":"Bearer",
         "scope":"api offline_access","Key":"2.legacy","Kdf":0,"KdfIterations":600000,
         "UserDecryptionOptions":{"HasMasterPassword":true,"MasterPasswordUnlock":{
           "Kdf":{"KdfType":1,"Iterations":3,"Memory":64,"Parallelism":4},
           "MasterKeyWrappedUserKey":"2.wrapped","Salt":"Case-Sensitive Salt",
           "ContainedKeyId":"0123456789abcdef0123456789abcdef"}}}
        """;

        var token = JsonSerializer.Deserialize<TokenResponse>(json, Web)!;

        Assert.True(token.UserDecryptionOptions!.HasMasterPassword);
        var unlock = token.UserDecryptionOptions.MasterPasswordUnlock!;
        Assert.Equal("2.wrapped", unlock.MasterKeyWrappedUserKey);
        Assert.Equal("Case-Sensitive Salt", unlock.Salt);
        Assert.Equal("0123456789abcdef0123456789abcdef", unlock.ContainedKeyId);
        Assert.Equal(KdfType.Argon2id, unlock.Kdf!.KdfType);
        Assert.Equal(3, unlock.Kdf.Iterations);
        Assert.Equal(64, unlock.Kdf.Memory);
        Assert.Equal(4, unlock.Kdf.Parallelism);
    }

    [Fact]
    public void Serializes_contained_key_id_with_exact_pascalcase_wire_name()
    {
        const string containedKeyId = "0123456789abcdef0123456789abcdef";
        var unlock = new MasterPasswordUnlockResponse(null, null, null, null, containedKeyId);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(unlock, Web));

        Assert.Equal(containedKeyId, document.RootElement.GetProperty("ContainedKeyId").GetString());
        Assert.False(document.RootElement.TryGetProperty("containedKeyId", out _));
    }

    [Fact]
    public void Missing_contained_key_id_deserializes_as_null()
    {
        const string json = """{"Salt":"Case-Sensitive Salt"}""";

        var unlock = JsonSerializer.Deserialize<MasterPasswordUnlockResponse>(json, Web)!;

        Assert.Null(unlock.ContainedKeyId);
    }

    [Fact]
    public void Missing_has_master_password_metadata_remains_unknown()
    {
        const string json = """
        {"access_token":"at","refresh_token":"rt","expires_in":3600,
         "Kdf":0,"KdfIterations":600000,"UserDecryptionOptions":{}}
        """;

        var token = JsonSerializer.Deserialize<TokenResponse>(json, Web)!;

        Assert.NotNull(token.UserDecryptionOptions);
        Assert.Null(token.UserDecryptionOptions!.HasMasterPassword);
    }

    [Fact]
    public void Deserializes_sso_two_factor_context()
    {
        const string json = """
        {"error":"invalid_grant","error_description":"Two factor required.",
         "TwoFactorProviders":["1","7"],"TwoFactorProviders2":{"1":{"Email":"m***@example.com"}},
         "SsoEmail2faSessionToken":"sso-session","Email":"me@example.com"}
        """;

        var error = JsonSerializer.Deserialize<ConnectTokenErrorResponse>(json, Web)!;

        Assert.Equal([1, 7], error.TwoFactorProviders!);
        Assert.Equal("sso-session", error.SsoEmail2FaSessionToken);
        Assert.Equal("me@example.com", error.Email);
        Assert.Equal("m***@example.com", error.TwoFactorProviders2!["1"]!.Value.GetProperty("Email").GetString());
    }
}
