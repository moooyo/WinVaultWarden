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
        var service = new VaultUiService(new TestVaultService(), new NoopWriteService(), new NoopSyncService());

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
        var service = new VaultUiService(new TestVaultService(), new NoopWriteService(), new NoopSyncService());

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

public class RealVaultUiServiceWriteTests
{
    [Fact]
    public async Task SaveCipherAsync_Create_SendsEmptyIdAndReturnsServerAssignedId()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());
        var draft = new CipherEditorDraft { Type = VaultItemKind.Login, Name = "New" };

        var id = await service.SaveCipherAsync(draft, editingId: null);

        Assert.Equal("", write.LastSavedCipher!.Id);   // create = empty id on the wire
        Assert.Equal("created-1", id);                 // located via id-set diff after re-sync
    }

    [Fact]
    public async Task SaveCipherAsync_Edit_PreservesPasskeysAndReturnsEditingId()
    {
        var vault = new MutableVaultService();
        var passkey = new CipherFido2Credential("cred", "kt", "alg", "P-256", "kv", "github.com", "uh", "u", 1, "GitHub", "Octo", true, DateTimeOffset.UnixEpoch);
        vault.Ciphers.Add(new Cipher
        {
            Id = "edit-1", Type = CipherType.Login, Name = "Old",
            Login = new CipherLogin("octo", null, null, System.Array.Empty<CipherLoginUri>()) { Fido2Credentials = new[] { passkey } },
        });
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());
        var draft = new CipherEditorDraft { Type = VaultItemKind.Login, Name = "Renamed" };

        var id = await service.SaveCipherAsync(draft, editingId: "edit-1");

        Assert.Equal("edit-1", id);
        Assert.Equal("edit-1", write.LastSavedCipher!.Id);
        Assert.Equal("Renamed", write.LastSavedCipher.Name);
        Assert.Equal("cred", Assert.Single(write.LastSavedCipher.Login!.Fido2Credentials).CredentialId);
    }

    [Fact]
    public async Task DeleteRestoreFolderSync_DelegateToWriteService()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var sync = new NoopSyncService();
        var service = new VaultUiService(vault, write, sync);

        await service.DeleteCipherAsync("x", permanent: true);
        await service.RestoreCipherAsync("y");
        await service.SaveFolderAsync(null, "Docs");
        await service.DeleteFolderAsync("f1");
        await service.SyncAsync();

        Assert.Equal(("x", true), write.LastDelete);
        Assert.Equal("y", write.LastRestore);
        Assert.Equal((null, "Docs"), write.LastFolderSave);
        Assert.Equal("f1", write.LastFolderDelete);
        Assert.Equal(1, sync.SyncCount);
    }

    [Fact]
    public async Task MoveCiphersAsync_DelegatesToWriteService()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());

        await service.MoveCiphersAsync(new[] { "a", "b" }, "f1");

        Assert.Equal("move", write.LastOp);
        Assert.Equal(new[] { "a", "b" }, write.LastIds);
        Assert.Equal("f1", write.LastFolderId);
    }

    [Fact]
    public async Task DeleteCiphersAsync_Permanent_DelegatesWithFlag()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());

        await service.DeleteCiphersAsync(new[] { "a" }, permanent: true);

        Assert.Equal("delete", write.LastOp);
        Assert.Equal(new[] { "a" }, write.LastIds);
        Assert.True(write.LastPermanent);
    }

    [Fact]
    public async Task RestoreCiphersAsync_Delegates()
    {
        var vault = new MutableVaultService();
        var write = new RecordingWriteService(vault);
        var service = new VaultUiService(vault, write, new NoopSyncService());

        await service.RestoreCiphersAsync(new[] { "a" });

        Assert.Equal("restore", write.LastOp);
        Assert.Equal(new[] { "a" }, write.LastIds);
    }

}

file sealed class MutableVaultService : IVaultService
{
    public List<Cipher> Ciphers { get; } = new();
    public List<Folder> Folders { get; } = new();
    public Core.Session.IVaultSnapshot Snapshot => throw new NotSupportedException();
    public IReadOnlyList<Cipher> GetCiphers() => Ciphers;
    public IReadOnlyList<Folder> GetFolders() => Folders;
    public IReadOnlyList<DeviceInfo> GetDevices() => System.Array.Empty<DeviceInfo>();
}

file sealed class RecordingWriteService : IVaultWriteService
{
    private readonly MutableVaultService _vault;
    public RecordingWriteService(MutableVaultService vault) => _vault = vault;

    public Cipher? LastSavedCipher { get; private set; }
    public (string Id, bool Permanent)? LastDelete { get; private set; }
    public string? LastRestore { get; private set; }
    public (string? Id, string Name)? LastFolderSave { get; private set; }
    public string? LastFolderDelete { get; private set; }

    // Bulk operation recording
    public string? LastOp { get; private set; }
    public IReadOnlyCollection<string>? LastIds { get; private set; }
    public string? LastFolderId { get; private set; }
    public bool? LastPermanent { get; private set; }

    public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default)
    {
        LastSavedCipher = cipher;
        if (string.IsNullOrEmpty(cipher.Id))
            _vault.Ciphers.Add(new Cipher { Id = "created-1", Type = cipher.Type, Name = cipher.Name });
        return Task.CompletedTask;
    }

    public Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default)
    {
        LastDelete = (cipherId, permanent);
        return Task.CompletedTask;
    }

    public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default)
    {
        LastRestore = cipherId;
        return Task.CompletedTask;
    }

    public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default)
    {
        LastFolderSave = (folderId, name);
        return Task.CompletedTask;
    }

    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default)
    {
        LastFolderDelete = folderId;
        return Task.CompletedTask;
    }

    public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default)
    {
        LastOp = "move";
        LastIds = ids;
        LastFolderId = folderId;
        return Task.CompletedTask;
    }

    public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default)
    {
        LastOp = "delete";
        LastIds = ids;
        LastPermanent = permanent;
        return Task.CompletedTask;
    }

    public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        LastOp = "restore";
        LastIds = ids;
        return Task.CompletedTask;
    }
}

file sealed class NoopWriteService : IVaultWriteService
{
    public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => Task.CompletedTask;
    public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) => Task.CompletedTask;
    public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => Task.CompletedTask;
}

file sealed class NoopSyncService : ISyncService
{
    public int SyncCount { get; private set; }
    public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
    {
        SyncCount++;
        return Task.FromResult<IReadOnlyList<Cipher>>(System.Array.Empty<Cipher>());
    }
}
