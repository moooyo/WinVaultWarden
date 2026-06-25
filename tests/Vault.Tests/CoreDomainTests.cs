using Core.Enums;
using Core.Models;
using Core.Session;
using Xunit;

namespace Vault.Tests;

public class CoreDomainTests
{
    [Fact]
    public void Cipher_CanRepresentDecryptedLoginWithFieldsAndDeletionState()
    {
        var cipher = new Cipher
        {
            Id = "c1",
            Type = CipherType.Login,
            Name = "GitHub",
            Notes = "note",
            FolderId = "f1",
            Favorite = true,
            Reprompt = true,
            RevisionDate = DateTimeOffset.Parse("2026-06-24T00:00:00Z"),
            DeletedDate = DateTimeOffset.Parse("2026-06-25T00:00:00Z"),
            Login = new CipherLogin("octo", "secret", "totp", [new CipherLoginUri("https://github.com", null)])
            {
                Fido2Credentials =
                [
                    new CipherFido2Credential("credential-id", "public-key", "ECDSA", "P-256", "private-key",
                        "github.com", "user-handle", "octo", 1, "GitHub", "Octo Cat", true,
                        DateTimeOffset.Parse("2026-06-24T00:00:00Z")),
                ],
            },
            Fields = [new CipherField("Recovery", "secret", CipherFieldType.Hidden)],
        };

        Assert.True(cipher.IsDeleted);
        Assert.Equal("octo", cipher.Login!.Username);
        Assert.True(cipher.Login.HasFido2Credentials);
        Assert.Equal("github.com", Assert.Single(cipher.Login.Fido2Credentials).RpId);
        Assert.Equal(CipherFieldType.Hidden, cipher.Fields[0].Type);
    }

    [Fact]
    public void Snapshot_ExposesReadOnlyVaultState()
    {
        IVaultSnapshot snapshot = new TestSnapshot();

        Assert.Equal(VaultState.Unlocked, snapshot.State);
        Assert.Empty(snapshot.Ciphers);
        Assert.Empty(snapshot.Folders);
        Assert.Empty(snapshot.Devices);
        Assert.Equal("me@example.com", snapshot.Account.Email);
    }

    private sealed class TestSnapshot : IVaultSnapshot
    {
        public VaultState State => VaultState.Unlocked;
        public IReadOnlyList<Cipher> Ciphers { get; } = [];
        public IReadOnlyList<Folder> Folders { get; } = [];
        public IReadOnlyList<DeviceInfo> Devices { get; } = [];
        public AccountInfo Account { get; } = new("me@example.com", "https://vault.example", "M", "PBKDF2 600000");
    }
}
