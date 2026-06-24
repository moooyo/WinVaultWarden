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
            Login = new CipherLogin("octo", "secret", "totp", [new CipherLoginUri("https://github.com", null)]),
            Fields = [new CipherField("Recovery", "secret", CipherFieldType.Hidden)],
        };

        Assert.True(cipher.IsDeleted);
        Assert.Equal("octo", cipher.Login!.Username);
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
