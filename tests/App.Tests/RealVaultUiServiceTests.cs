using App.Services;
using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Xunit;

namespace App.Tests;

public class RealVaultUiServiceTests
{
    [Fact]
    public void GetItemsAndFilters_MapSnapshotCounts()
    {
        var service = new VaultUiService(new TestVaultService());

        var items = service.GetItems();
        var filters = service.GetFilters();

        Assert.Contains(items, item => item.Kind == VaultItemKind.Login && item.Name == "GitHub" && item.Favorite);
        Assert.Contains(items, item => item.Kind == VaultItemKind.Card);
        Assert.Contains(items, item => item.Kind == VaultItemKind.Identity);
        Assert.Contains(items, item => item.Kind == VaultItemKind.Note);
        Assert.Contains(items, item => item.Kind == VaultItemKind.Ssh);
        Assert.Contains(items, item => item.IsDeleted);

        Assert.Equal(5, filters.Single(f => f.Kind == FilterKind.AllItems).Count);
        Assert.Equal(1, filters.Single(f => f.Kind == FilterKind.Favorites).Count);
        Assert.Equal(1, filters.Single(f => f.Kind == FilterKind.Trash).Count);
        Assert.Equal(1, filters.Single(f => f.Kind == FilterKind.Folder && f.FolderId == "f1").Count);
        Assert.Equal(1, filters.Single(f => f.Kind == FilterKind.Type && f.TypeFilter == VaultItemKind.Ssh).Count);
    }

    [Fact]
    public void GetDetail_MapsAllCipherTypes()
    {
        var service = new VaultUiService(new TestVaultService());

        var login = Assert.IsType<LoginDetail>(service.GetDetail("login"));
        Assert.Equal("octo", login.Username);
        Assert.Equal("pw", login.Password);
        Assert.Equal("otpauth", login.TotpSecret);
        Assert.Equal("https://github.com", login.Uri);
        Assert.Equal("Work", login.FolderName);
        Assert.True(login.Reprompt);
        Assert.True(Assert.Single(login.CustomFields).IsSecret);
        var passkey = Assert.Single(login.Passkeys);
        Assert.Equal("github.com", passkey.RpId);
        Assert.Equal("Octo Cat", passkey.DisplayName);
        Assert.Equal("是", passkey.DiscoverableText);

        var card = Assert.IsType<CardDetail>(service.GetDetail("card"));
        Assert.Equal("Jane", card.Cardholder);
        Assert.Equal("4111", card.Number);
        Assert.Equal("12/2030", card.Expiry);
        Assert.Equal("Visa", card.Brand);
        Assert.Equal("123", card.Cvv);

        var identity = Assert.IsType<IdentityDetail>(service.GetDetail("identity"));
        Assert.Equal("Jane Doe", identity.FullName);
        Assert.Equal("jane@example.com", identity.Email);
        Assert.Equal("123-45-6789", identity.IdNumber);
        Assert.Equal("Road 1, Beijing, China", identity.Address);

        var note = Assert.IsType<NoteDetail>(service.GetDetail("note"));
        Assert.Equal("note body", note.Content);

        var ssh = Assert.IsType<SshDetail>(service.GetDetail("ssh"));
        Assert.Equal("public", ssh.PublicKey);
        Assert.Equal("private", ssh.PrivateKey);
        Assert.Equal("SHA256:fp", ssh.Fingerprint);
    }

    private sealed class TestVaultService : IVaultService
    {
        public IVaultSnapshot Snapshot { get; } = new TestSnapshot();
        public IReadOnlyList<Cipher> GetCiphers() => Snapshot.Ciphers;
        public IReadOnlyList<Folder> GetFolders() => Snapshot.Folders;
        public IReadOnlyList<DeviceInfo> GetDevices() => Snapshot.Devices;
    }

    private sealed class TestSnapshot : IVaultSnapshot
    {
        public VaultState State => VaultState.Unlocked;
        public AccountInfo Account { get; } = new("me@example.com", "https://vault.example", "M", "PBKDF2 600000");
        public IReadOnlyList<Folder> Folders { get; } =
        [
            new Folder { Id = "f1", Name = "Work", RevisionDate = DateTimeOffset.Parse("2026-06-24T00:00:00Z") },
        ];
        public IReadOnlyList<DeviceInfo> Devices { get; } = [];
        public IReadOnlyList<Cipher> Ciphers { get; } =
        [
            new Cipher
            {
                Id = "login",
                Type = CipherType.Login,
                Name = "GitHub",
                Notes = "login notes",
                FolderId = "f1",
                Favorite = true,
                Reprompt = true,
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                Login = new CipherLogin("octo", "pw", "otpauth", [new CipherLoginUri("https://github.com", null)])
                {
                    Fido2Credentials =
                    [
                        new CipherFido2Credential("credential-id", "public-key", "ECDSA", "P-256", "private-key",
                            "github.com", "user-handle", "octo@example.com", 1, "GitHub", "Octo Cat", true,
                            DateTimeOffset.Parse("2026-06-01T00:00:00Z")),
                    ],
                },
                Fields = [new CipherField("Recovery", "secret", CipherFieldType.Hidden)],
            },
            new Cipher
            {
                Id = "card",
                Type = CipherType.Card,
                Name = "Visa",
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                Card = new CipherCard("Jane", "4111", "12", "2030", "123", "Visa"),
            },
            new Cipher
            {
                Id = "identity",
                Type = CipherType.Identity,
                Name = "Identity",
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                Identity = new CipherIdentity(null, "Jane", null, "Doe", null, null, "123-45-6789", null, null,
                    "jane@example.com", null, "Road 1", null, null, "Beijing", null, null, "China"),
            },
            new Cipher
            {
                Id = "note",
                Type = CipherType.SecureNote,
                Name = "Note",
                Notes = "note body",
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                SecureNote = new CipherSecureNote(0),
            },
            new Cipher
            {
                Id = "ssh",
                Type = CipherType.SshKey,
                Name = "SSH",
                CreationDate = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
                RevisionDate = DateTimeOffset.Parse("2026-06-02T00:00:00Z"),
                Ssh = new CipherSsh("private", "public", "SHA256:fp"),
            },
            new Cipher
            {
                Id = "deleted",
                Type = CipherType.Login,
                Name = "Deleted",
                DeletedDate = DateTimeOffset.Parse("2026-06-10T00:00:00Z"),
                Login = new CipherLogin("old", null, null, []),
            },
        ];
    }
}
