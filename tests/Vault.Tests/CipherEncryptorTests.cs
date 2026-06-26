using System.Security.Cryptography;
using System.Text.Json;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public class CipherEncryptorTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly CryptoService _crypto = new();
    private readonly SymmetricCryptoKey _userKey = new(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
    private CipherEncryptor NewEncryptor() => new(_crypto);

    // Encrypt a domain cipher, push it across the wire shape, and decrypt it back.
    private Cipher RoundTrip(Cipher source)
    {
        var request = NewEncryptor().Encrypt(source, _userKey);
        var json = JsonSerializer.Serialize(request, Web);
        var dto = JsonSerializer.Deserialize<CipherDto>(json, Web)!;
        var sync = new SyncResponse("sync", new ProfileDto("u", "me@x", "Me", null, null), [], [dto]);
        return Assert.Single(new VaultDecryptor(_crypto).Decrypt(sync, _userKey, "https://x").Ciphers);
    }

    private static Cipher LoginSource() => new()
    {
        Type = CipherType.Login,
        Name = "GitHub",
        Notes = "note",
        FolderId = "f1",
        Favorite = true,
        Reprompt = true,
        Login = new CipherLogin("octo", "secret", "totp-seed",
            [new CipherLoginUri("https://github.com", 0)])
        {
            Fido2Credentials =
            [
                new CipherFido2Credential("cred-id", "public-key", "ECDSA", "P-256", "priv-val",
                    "github.com", "user-handle", "octo@x", 42, "GitHub", "Octo", true,
                    DateTimeOffset.Parse("2026-06-24T00:00:00Z")),
            ],
        },
        Fields =
        [
            new CipherField("Plain", "v1", CipherFieldType.Text),
            new CipherField("Secret", "v2", CipherFieldType.Hidden),
        ],
    };

    [Fact]
    public void Encrypt_Login_RoundTripsAllFields()
    {
        var result = RoundTrip(LoginSource());

        Assert.Equal(CipherType.Login, result.Type);
        Assert.Equal("GitHub", result.Name);
        Assert.Equal("note", result.Notes);
        Assert.Equal("f1", result.FolderId);
        Assert.True(result.Favorite);
        Assert.True(result.Reprompt);
        Assert.Equal("octo", result.Login!.Username);
        Assert.Equal("secret", result.Login.Password);
        Assert.Equal("totp-seed", result.Login.Totp);
        Assert.Equal("https://github.com", Assert.Single(result.Login.Uris).Uri);
        Assert.Equal(2, result.Fields.Count);
        Assert.Equal("Plain", result.Fields[0].Name);
        Assert.Equal(CipherFieldType.Hidden, result.Fields[1].Type);
    }

    [Fact]
    public void Encrypt_Login_RoundTripsFido2Credentials()
    {
        var credential = Assert.Single(RoundTrip(LoginSource()).Login!.Fido2Credentials);

        Assert.Equal("cred-id", credential.CredentialId);
        Assert.Equal("public-key", credential.KeyType);
        Assert.Equal("P-256", credential.KeyCurve);
        Assert.Equal("github.com", credential.RpId);
        Assert.Equal(42, credential.Counter);
        Assert.True(credential.Discoverable);
        Assert.Equal(DateTimeOffset.Parse("2026-06-24T00:00:00Z"), credential.CreationDate);
    }

    [Fact]
    public void Encrypt_Card_RoundTrips()
    {
        var source = new Cipher
        {
            Type = CipherType.Card,
            Name = "Visa",
            Card = new CipherCard("Jane", "4111", "12", "2030", "123", "Visa"),
        };

        var card = RoundTrip(source).Card!;
        Assert.Equal("Jane", card.CardholderName);
        Assert.Equal("4111", card.Number);
        Assert.Equal("Visa", card.Brand);
    }

    [Fact]
    public void Encrypt_Identity_RoundTrips()
    {
        var source = new Cipher
        {
            Type = CipherType.Identity,
            Name = "ID",
            Identity = new CipherIdentity("Mr", "Jane", null, "Doe", null, null, null, null, null,
                "jane@x", null, null, null, null, null, null, null, null),
        };

        var identity = RoundTrip(source).Identity!;
        Assert.Equal("Jane", identity.FirstName);
        Assert.Equal("Doe", identity.LastName);
        Assert.Equal("jane@x", identity.Email);
    }

    [Fact]
    public void Encrypt_SecureNote_RoundTrips()
    {
        var source = new Cipher
        {
            Type = CipherType.SecureNote,
            Name = "Note",
            Notes = "body",
            SecureNote = new CipherSecureNote(0),
        };

        var result = RoundTrip(source);
        Assert.Equal(0, result.SecureNote!.Type);
        Assert.Equal("body", result.Notes);
    }

    [Fact]
    public void Encrypt_Ssh_RoundTrips()
    {
        var source = new Cipher
        {
            Type = CipherType.SshKey,
            Name = "Key",
            Ssh = new CipherSsh("priv", "pub", "fp"),
        };

        var ssh = RoundTrip(source).Ssh!;
        Assert.Equal("priv", ssh.PrivateKey);
        Assert.Equal("pub", ssh.PublicKey);
        Assert.Equal("fp", ssh.Fingerprint);
    }

    [Fact]
    public void Encrypt_WrapsPerCipherKey_NotEncryptedUnderUserKey()
    {
        var request = NewEncryptor().Encrypt(LoginSource(), _userKey);

        Assert.False(string.IsNullOrEmpty(request.Key));
        var cipherKey = _crypto.DecryptItemKey(request.Key!, _userKey);
        Assert.Equal(64, cipherKey.FullKey.Length);
        // Name decrypts with the per-cipher key, and fails MAC under the user key.
        Assert.Equal("GitHub", _crypto.DecryptToString(request.Name, cipherKey));
        Assert.Throws<CryptographicException>(() => _crypto.DecryptToString(request.Name, _userKey));
    }

    [Fact]
    public void EncryptFolderName_EncryptsDirectlyUnderUserKey()
    {
        var name = NewEncryptor().EncryptFolderName("Work", _userKey);

        Assert.StartsWith("2.", name);
        Assert.Equal("Work", _crypto.DecryptToString(name, _userKey));
    }
}
