using System.Security.Cryptography;
using System.Formats.Cbor;
using System.Text.Json;
using Api.Dtos;
using Api;
using Core.Enums;
using Core.Models;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class V2CipherBlobTests
{
    private const string HistoricalWrappingKey =
        "z27dMz/RK4wboY/Ako0YVFr9jaiSjgQQyGkTZ4LIuNrOXyeDAjeD41qbhVKl0OSjP3QuN9xmAJQE8+V5/Tl7ig==";

    private const string HistoricalBlob =
        "{\"format_version\":1,\"wrapped_cek\":\"2.LQJf2BbznXX+NelBY4pSJg==|txMmjZEOhSMA7Jrm+rZt1LDfA6s3G2QU5Z8MqO4nG9s2ZXuzSLU/iYOUXD8xw+eHVSu7IUHu1LsCm4SLf+ZhkX5QIo4hJT3DHSbgu6VPUC0=|yuU/EWQWyihf2Yh9lQ1NP+zTROEpnXoRS//GfxDgC4k=\",\"envelope\":\"g1hLpQE6AAERbwN4I2FwcGxpY2F0aW9uL3guYml0d2FyZGVuLmNib3ItcGFkZGVkBFBoHnjLne8MPV72YPXuskd6OgABOIECOgABOIABoQVYGA00vxb7gF7Y3SUyoCMy34C1HrB3fSY3jVhxZXQmmotGEIwwRlG+SpTcyTl5m4lUnozWrjAYfWitl1+cz457Wq3iDW/MvrHE7c1g38QJxY6t1yhQL0dQy9DyDXQDiWGPtYzic2Ay+GtrlIERN37wOdhQ1HZDeoobHL+aKomvPTems/Ta2SqWC9HfE38=\"}";

    private const string OfficialAesGcmCek =
        "pQEEAlDLIx+izSLk9h9sVjHFzpKoAwMEgQQgWCDWM3iwTX2/LHTIaXS0cPIKCYFZethtKyD6Pucdt4fkGQQEBAQ=";

    private const string OfficialAesGcmEnvelope =
        "g1hHpQEDA3gjYXBwbGljYXRpb24veC5iaXR3YXJkZW4uY2Jvci1wYWRkZWQEUMsjH6LNIuT2H2xWMcXOkqg6AAE4gQI6AAE4gCChBUyoF/oGEm+lJYrjjgdYUGIH5LnQjqFMWo2BJORVPYH2+hEWkxIn3tRgAMHNwIr0nTXMVD1EyVGZOsHSDMPqn2HaYrDeR5s+Rg0ezZ2WLh8n2FbdC44A/ExOms4IHcyT";

    private readonly CryptoService _crypto = new();

    [Fact]
    public void OfficialHistoricalXChaChaBlob_Decrypts()
    {
        using var key = new SymmetricCryptoKey(
            Convert.FromBase64String(HistoricalWrappingKey));
        var dto = Dto(
            CipherType.SecureNote,
            name: null,
            key: null,
            data: HistoricalBlob);

        var cipher = new VaultDecryptor(_crypto).DecryptCipher(dto, key);

        Assert.True(cipher.UsesBlobEncryption);
        Assert.Equal("Test Cipher", cipher.Name);
        Assert.Equal("Some notes", cipher.Notes);
        Assert.Equal(0, cipher.SecureNote?.Type);
    }

    [Fact]
    public void MalformedCborBlob_IsIsolatedAsSkippedCipher()
    {
        using var key = new SymmetricCryptoKey(
            Convert.FromBase64String(HistoricalWrappingKey));
        using var historical = JsonDocument.Parse(HistoricalBlob);
        var malformed = JsonSerializer.Serialize(new
        {
            format_version = 1,
            wrapped_cek = historical.RootElement.GetProperty("wrapped_cek").GetString(),
            envelope = "gA==",
        });
        var sync = new SyncResponse(
            "sync",
            new ProfileDto("account-1", "alice@example.com", "Alice", null, null),
            Ciphers: [Dto(CipherType.SecureNote, null, null, malformed)]);

        var vault = new VaultDecryptor(_crypto).Decrypt(
            sync,
            key,
            ServerEndpoints.ForSelfHosted("https://vault.example"));

        Assert.Empty(vault.Ciphers);
        Assert.Equal(1, vault.SkippedCipherCount);
    }

    [Fact]
    public void OfficialAesGcmDataEnvelope_Decrypts()
    {
        var cek = Convert.FromBase64String(OfficialAesGcmCek);
        byte[]? content = null;
        try
        {
            content = CoseDataEnvelopeCodec.UnsealWithCek(
                cek,
                OfficialAesGcmEnvelope,
                expectedContentNamespace: -1);
            var reader = new CborReader(content, CborConformanceMode.Lax);
            Assert.Equal(2, reader.ReadStartMap());
            Assert.Equal("version", reader.ReadTextString());
            Assert.Equal("1", reader.ReadTextString());
            Assert.Equal("content", reader.ReadTextString());
            Assert.Equal(1, reader.ReadStartMap());
            Assert.Equal("field", reader.ReadTextString());
            Assert.Equal(123UL, reader.ReadUInt64());
            reader.ReadEndMap();
            reader.ReadEndMap();
            Assert.Equal(CborReaderState.Finished, reader.PeekState());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(cek);
            if (content is not null)
                CryptographicOperations.ZeroMemory(content);
        }
    }

    [Fact]
    public void V2PersonalCipher_EncryptsAsBlobAndRoundTrips()
    {
        using var userKey = V2UserKey.Generate();
        var source = new Cipher
        {
            Type = CipherType.Login,
            Name = "Example",
            Notes = "Private note",
            Favorite = true,
            Reprompt = true,
            Login = new CipherLogin(
                "alice@example.com",
                "correct horse",
                "JBSWY3DPEHPK3PXP",
                [new CipherLoginUri("https://example.com", 0)])
            {
                PasswordRevisionDate = DateTimeOffset.Parse("2026-08-30T10:00:00Z"),
                AutofillOnPageLoad = false,
            },
            Fields = [new CipherField("Environment", "Production", CipherFieldType.Text)],
            PasswordHistory =
            [
                new PasswordHistoryEntry(
                    "old password",
                    DateTimeOffset.Parse("2026-08-29T10:00:00Z")),
            ],
        };

        var request = new CipherEncryptor(_crypto).Encrypt(
            source,
            userKey,
            VaultCapabilities.None);

        Assert.Null(request.Name);
        Assert.Null(request.Login);
        Assert.Null(request.Fields);
        Assert.Null(request.PasswordHistory);
        Assert.StartsWith("7.", request.Key);
        Assert.NotNull(request.Data);
        using (var document = JsonDocument.Parse(request.Data))
        {
            Assert.Equal(1, document.RootElement.GetProperty("format_version").GetInt32());
            Assert.StartsWith(
                "2.",
                document.RootElement.GetProperty("wrapped_cek").GetString());
            var envelopeBytes = Convert.FromBase64String(
                document.RootElement.GetProperty("envelope").GetString()!);
            try
            {
                var envelopeReader = new CborReader(envelopeBytes, CborConformanceMode.Lax);
                Assert.Equal(3, envelopeReader.ReadStartArray());
                var protectedHeader = envelopeReader.ReadByteString();
                var protectedReader = new CborReader(protectedHeader, CborConformanceMode.Lax);
                Assert.Equal(5, protectedReader.ReadStartMap());
                Assert.Equal(1, protectedReader.ReadInt64());
                Assert.Equal(3, protectedReader.ReadInt64());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
            }
        }

        var restored = new VaultDecryptor(_crypto).DecryptCipher(
            Dto(source.Type, null, request.Key, request.Data),
            userKey);

        Assert.True(restored.UsesBlobEncryption);
        Assert.Equal(source.Name, restored.Name);
        Assert.Equal(source.Notes, restored.Notes);
        Assert.Equal(source.Login!.Username, restored.Login!.Username);
        Assert.Equal(source.Login.Password, restored.Login.Password);
        Assert.Equal(source.Login.Totp, restored.Login.Totp);
        Assert.Equal(source.Login.PasswordRevisionDate, restored.Login.PasswordRevisionDate);
        Assert.Equal(source.Login.AutofillOnPageLoad, restored.Login.AutofillOnPageLoad);
        Assert.Equal(source.Login.Uris.ToArray(), restored.Login.Uris.ToArray());
        Assert.Equal(source.Fields.ToArray(), restored.Fields.ToArray());
        Assert.Equal(source.PasswordHistory.ToArray(), restored.PasswordHistory.ToArray());
    }

    [Fact]
    public void V2PersonalLegacyFields_WithType7ItemKey_Decrypt()
    {
        using var userKey = V2UserKey.Generate();
        using var itemKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var wrappedItemKey = userKey.Encrypt(
            itemKey.FullKey,
            CoseContentFormat.BitwardenLegacyKey);
        var encryptedName = EncryptText("Legacy item", itemKey);
        var encryptedUsername = EncryptText("alice", itemKey);
        var dto = Dto(
            CipherType.Login,
            encryptedName,
            wrappedItemKey,
            data: null,
            login: new LoginDto(
                encryptedUsername,
                null,
                null,
                Array.Empty<LoginUriDto>()));

        var cipher = new VaultDecryptor(_crypto).DecryptCipher(dto, userKey);

        Assert.False(cipher.UsesBlobEncryption);
        Assert.Equal("Legacy item", cipher.Name);
        Assert.Equal("alice", cipher.Login?.Username);
    }

    [Fact]
    public void V2PersonalLegacyFields_WithoutItemKey_FailsClosed()
    {
        using var userKey = V2UserKey.Generate();
        using var oldUserKey = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var dto = Dto(
            CipherType.SecureNote,
            EncryptText("Old unkeyed item", oldUserKey),
            key: null,
            data: null,
            secureNote: new SecureNoteDto(0));

        var exception = Assert.Throws<CryptographicException>(
            () => new VaultDecryptor(_crypto).DecryptCipher(dto, userKey));

        Assert.Contains("without an item key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V2ExistingCipherWithoutItemKey_CannotBeRewritten()
    {
        using var userKey = V2UserKey.Generate();
        var cipher = new Cipher
        {
            Id = "cipher-1",
            Type = CipherType.SecureNote,
            Name = "Unkeyed",
            SecureNote = new CipherSecureNote(0),
        };

        var exception = Assert.Throws<CryptographicException>(() =>
            new CipherEncryptor(_crypto).Encrypt(
                cipher,
                userKey,
                VaultCapabilities.None));

        Assert.Contains("without an item key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyWriter_CannotDowngradeBlobCipher()
    {
        using var key = new SymmetricCryptoKey(RandomNumberGenerator.GetBytes(64));
        var cipher = new Cipher
        {
            Id = "cipher-1",
            Type = CipherType.SecureNote,
            Name = "Blob",
            SecureNote = new CipherSecureNote(0),
            UsesBlobEncryption = true,
        };

        var exception = Assert.Throws<InvalidDataException>(() =>
            new CipherEncryptor(_crypto).Encrypt(
                cipher,
                key,
                VaultCapabilities.None));

        Assert.Contains("cannot be downgraded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlobAndLegacySensitiveFields_CannotBeMixed()
    {
        using var userKey = V2UserKey.Generate();
        var source = new Cipher
        {
            Type = CipherType.SecureNote,
            Name = "Blob",
            SecureNote = new CipherSecureNote(0),
        };
        var request = new CipherEncryptor(_crypto).Encrypt(
            source,
            userKey,
            VaultCapabilities.None);
        var dto = Dto(
            source.Type,
            name: "2.invalid-legacy-field",
            request.Key,
            request.Data);

        var exception = Assert.Throws<InvalidDataException>(() =>
            new VaultDecryptor(_crypto).DecryptCipher(dto, userKey));

        Assert.Contains("obsolete sensitive fields", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void V2BlobTransferToOrganization_PreservesLegacyLoginMetadata()
    {
        using var userKey = V2UserKey.Generate();
        using var organizationKey = new SymmetricCryptoKey(
            RandomNumberGenerator.GetBytes(64));
        var source = new Cipher
        {
            Type = CipherType.Login,
            Name = "Login",
            Login = new CipherLogin("alice", "secret", null, [])
            {
                PasswordRevisionDate = DateTimeOffset.Parse("2026-08-30T10:00:00Z"),
                AutofillOnPageLoad = false,
            },
        };
        var blobRequest = new CipherEncryptor(_crypto).Encrypt(
            source,
            userKey,
            VaultCapabilities.None);
        var personal = new VaultDecryptor(_crypto).DecryptCipher(
            Dto(source.Type, null, blobRequest.Key, blobRequest.Data),
            userKey);

        var transfer = new CipherEncryptor(_crypto).EncryptForOrganizationTransfer(
            personal,
            userKey,
            organizationKey,
            "org-1",
            VaultCapabilities.None);

        Assert.Null(transfer.Data);
        Assert.NotNull(transfer.Login);
        Assert.Equal(source.Login.PasswordRevisionDate, transfer.Login.PasswordRevisionDate);
        Assert.Equal(source.Login.AutofillOnPageLoad, transfer.Login.AutofillOnPageLoad);
        var restored = new VaultDecryptor(_crypto).DecryptCipher(
            Dto(
                source.Type,
                transfer.Name,
                transfer.Key,
                data: null,
                login: new LoginDto(
                    transfer.Login.Username,
                    transfer.Login.Password,
                    transfer.Login.Totp,
                    transfer.Login.Uris?.Select(uri => new LoginUriDto(
                        uri.Uri,
                        uri.Match)).ToArray(),
                    PasswordRevisionDate: transfer.Login.PasswordRevisionDate,
                    AutofillOnPageLoad: transfer.Login.AutofillOnPageLoad),
                organizationId: "org-1"),
            organizationKey);

        Assert.Equal(source.Login.PasswordRevisionDate, restored.Login?.PasswordRevisionDate);
        Assert.Equal(source.Login.AutofillOnPageLoad, restored.Login?.AutofillOnPageLoad);
    }

    [Fact]
    public void V2Folder_RoundTripsType7Utf8()
    {
        using var userKey = V2UserKey.Generate();
        var encryptor = new CipherEncryptor(_crypto);
        var encrypted = encryptor.EncryptFolderName("Production", userKey);

        var folder = new VaultDecryptor(_crypto).DecryptFolder(
            new FolderDto("folder-1", encrypted, DateTimeOffset.UnixEpoch),
            userKey);

        Assert.StartsWith("7.", encrypted);
        Assert.Equal("Production", folder.Name);
    }

    [Fact]
    public void VaultSession_V2Key_IsDiscriminatedAndCopied()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession(
            "account-1",
            "alice@example.com",
            ServerEndpoints.ForSelfHosted("https://vault.example"));
        session.SetUnlockedKey(V2UserKey.Generate());

        Assert.Null(session.UserKey);
        Assert.True(session.UsesV2UserKey);
        Assert.Equal(2, session.SecurityVersion);
        Assert.True(session.TryCopyUserKeyMaterial(lease, out var copy));
        using (copy)
        {
            Assert.True(copy!.IsV2);
            Assert.Equal(2, copy.SecurityVersion);
        }

        session.Lock();
        Assert.False(session.UsesV2UserKey);
        Assert.Equal(0, session.SecurityVersion);
    }

    [Fact]
    public void V2SyncProfile_DecryptsFolderAndPersonalBlobEndToEnd()
    {
        using var accountKeys = V2AccountKeyMaterial.Generate();
        using var sessionKey = VaultUserKey.TakeOwnership(
            V2UserKey.ImportBase64(accountKeys.UserKey.ExportBase64()));
        var cipher = new Cipher
        {
            Type = CipherType.SecureNote,
            Name = "V2 note",
            Notes = "sealed",
            SecureNote = new CipherSecureNote(0),
        };
        var encryptedCipher = new CipherEncryptor(_crypto).Encrypt(
            cipher,
            accountKeys.UserKey,
            VaultCapabilities.None);
        var encryptedFolder = new CipherEncryptor(_crypto).EncryptFolderName(
            "V2 folder",
            accountKeys.UserKey);
        var responseAccountKeys = new AccountKeysResponse(
            new PublicKeyEncryptionKeyPairResponse(
                accountKeys.WrappedPrivateKey,
                accountKeys.AccountPublicKey,
                accountKeys.SignedPublicKey),
            new SignatureKeyPairResponse(
                accountKeys.WrappedSigningKey,
                accountKeys.VerifyingKey),
            new AccountSecurityState(accountKeys.SecurityState, accountKeys.SecurityVersion));
        var profile = new ProfileDto(
            "account-1",
            "alice@example.com",
            "Alice",
            null,
            null)
        {
            AccountKeys = JsonSerializer.SerializeToElement(
                responseAccountKeys,
                ApiJsonContext.Default.AccountKeysResponse),
        };
        var sync = new SyncResponse(
            "sync",
            profile,
            [new FolderDto("folder-1", encryptedFolder, DateTimeOffset.UnixEpoch)],
            [Dto(cipher.Type, null, encryptedCipher.Key, encryptedCipher.Data)]);

        var vault = new VaultDecryptor(_crypto).Decrypt(
            sync,
            sessionKey,
            ServerEndpoints.ForSelfHosted("https://vault.example"));

        Assert.Equal("V2 folder", Assert.Single(vault.Folders).Name);
        Assert.Equal("V2 note", Assert.Single(vault.Ciphers).Name);
        Assert.Equal(accountKeys.WrappedPrivateKey, vault.WrappedAccountPrivateKey);
        Assert.Equal(0, vault.SkippedCipherCount);
        Assert.Equal(0, vault.SkippedFolderCount);
    }

    [Fact]
    public void V2SyncProfile_DecryptsLegacyOrganizationCipherAndCollection()
    {
        using var accountKeys = V2AccountKeyMaterial.Generate();
        using var sessionKey = VaultUserKey.TakeOwnership(
            V2UserKey.ImportBase64(accountKeys.UserKey.ExportBase64()));
        using var organizationKey = new SymmetricCryptoKey(
            RandomNumberGenerator.GetBytes(64));
        var encryptedOrganizationKey = _crypto.EncryptRsa(
            organizationKey.FullKey,
            Convert.FromBase64String(accountKeys.AccountPublicKey)).ToString();
        var organizationCipher = new Cipher
        {
            Type = CipherType.SecureNote,
            OrganizationId = "org-1",
            CollectionIds = ["collection-1"],
            Name = "Organization note",
            Notes = "legacy fields",
            SecureNote = new CipherSecureNote(0),
        };
        var encryptedCipher = new CipherEncryptor(_crypto).Encrypt(
            organizationCipher,
            organizationKey,
            VaultCapabilities.None);
        var responseAccountKeys = new AccountKeysResponse(
            new PublicKeyEncryptionKeyPairResponse(
                accountKeys.WrappedPrivateKey,
                accountKeys.AccountPublicKey,
                accountKeys.SignedPublicKey),
            new SignatureKeyPairResponse(
                accountKeys.WrappedSigningKey,
                accountKeys.VerifyingKey),
            new AccountSecurityState(accountKeys.SecurityState, 2));
        var profile = new ProfileDto(
            "account-1",
            "alice@example.com",
            "Alice",
            null,
            null,
            [new ProfileOrganizationDto(
                "org-1",
                "Organization",
                encryptedOrganizationKey,
                2,
                0)])
        {
            AccountKeys = JsonSerializer.SerializeToElement(
                responseAccountKeys,
                ApiJsonContext.Default.AccountKeysResponse),
        };
        var collectionName = EncryptText("Shared", organizationKey);
        var sync = new SyncResponse(
            "sync",
            profile,
            Ciphers:
            [
                Dto(
                    organizationCipher.Type,
                    encryptedCipher.Name,
                    encryptedCipher.Key,
                    data: null,
                    secureNote: new SecureNoteDto(0),
                    organizationId: "org-1"),
            ],
            Collections:
            [
                new CollectionDto(
                    "collection-1",
                    "org-1",
                    collectionName,
                    false,
                    false,
                    true),
            ]);

        var vault = new VaultDecryptor(_crypto).Decrypt(
            sync,
            sessionKey,
            ServerEndpoints.ForSelfHosted("https://vault.example"));

        Assert.Equal("Organization note", Assert.Single(vault.Ciphers).Name);
        Assert.Equal("Shared", Assert.Single(vault.Collections).Name);
        Assert.Equal(0, vault.UnsupportedOrganizationCipherCount);
    }

    private string EncryptText(string value, SymmetricCryptoKey key) =>
        _crypto.Encrypt(System.Text.Encoding.UTF8.GetBytes(value), key).ToString();

    private static CipherDto Dto(
        CipherType type,
        string? name,
        string? key,
        string? data,
        LoginDto? login = null,
        SecureNoteDto? secureNote = null,
        string? organizationId = null) => new(
            "cipher-1",
            (int)type,
            name,
            null,
            key,
            organizationId,
            null,
            false,
            0,
            login,
            null,
            null,
            secureNote,
            null,
            null,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            null,
            Data: data);
}
