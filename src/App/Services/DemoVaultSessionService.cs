using Core.Enums;
using Core.Models;
using Crypto;
using Vault;

namespace App.Services;

public interface IDemoVaultSessionService
{
    Task OpenDemoVaultAsync(CancellationToken ct = default);
}

public sealed class DemoVaultSessionService : IDemoVaultSessionService
{
    private readonly VaultSession _session;

    public DemoVaultSessionService(VaultSession session) => _session = session;

    public Task OpenDemoVaultAsync(CancellationToken ct = default)
    {
        _session.Clear();
        _session.SetUnlockedKey(new SymmetricCryptoKey(Enumerable.Range(1, 64).Select(i => (byte)i).ToArray()));
        _session.SetSnapshot(BuildVault());
        _session.SetDevices(
        [
            new DeviceInfo("demo-device", "WinVaultWarden Dev", 6, "demo-device", DemoDate(24, 9, 0), false),
        ]);
        return Task.CompletedTask;
    }

    private static DecryptedVault BuildVault()
    {
        var folder = new Folder
        {
            Id = "demo-folder-work",
            Name = "演示文件夹",
            RevisionDate = DemoDate(20, 10, 0),
        };

        return new DecryptedVault(
            new AccountInfo("demo@winvaultwarden.local", "local-demo", "D", "Demo"),
            [folder],
            BuildCiphers(folder.Id),
            0);
    }

    private static IReadOnlyList<Cipher> BuildCiphers(string folderId) =>
    [
        new Cipher
        {
            Id = "demo-login",
            Type = CipherType.Login,
            Name = "GitHub",
            FolderId = folderId,
            Favorite = true,
            Notes = "本地演示登录条目，不会上传到服务器。",
            CreationDate = DemoDate(18, 9, 30),
            RevisionDate = DemoDate(23, 15, 45),
            Login = new CipherLogin("octocat@example.com", "demo-password-123", "506999", [new CipherLoginUri("https://github.com", null)]),
            Fields =
            [
                new CipherField("Recovery code", "ABCD-EFGH", CipherFieldType.Hidden),
            ],
        },
        new Cipher
        {
            Id = "demo-card",
            Type = CipherType.Card,
            Name = "Demo Visa",
            CreationDate = DemoDate(17, 11, 0),
            RevisionDate = DemoDate(21, 14, 0),
            Card = new CipherCard("Demo User", "4111111111111111", "08", "2030", "123", "Visa"),
        },
        new Cipher
        {
            Id = "demo-identity",
            Type = CipherType.Identity,
            Name = "Demo Identity",
            CreationDate = DemoDate(16, 8, 15),
            RevisionDate = DemoDate(21, 8, 30),
            Identity = new CipherIdentity("Mr.", "Demo", null, "User", "demo-user", "WinVaultWarden",
                "110101199001011234", "P12345678", null, "demo@winvaultwarden.local", "13800000000",
                "Road 1", null, null, "Shanghai", null, "200000", "China"),
        },
        new Cipher
        {
            Id = "demo-note",
            Type = CipherType.SecureNote,
            Name = "Release checklist",
            Notes = "1. Build Debug\n2. Check vault layout\n3. Verify detail panes",
            CreationDate = DemoDate(15, 10, 0),
            RevisionDate = DemoDate(20, 16, 20),
            SecureNote = new CipherSecureNote(0),
        },
        new Cipher
        {
            Id = "demo-ssh",
            Type = CipherType.SshKey,
            Name = "Demo SSH Key",
            CreationDate = DemoDate(14, 7, 10),
            RevisionDate = DemoDate(20, 9, 50),
            Ssh = new CipherSsh(
                "-----BEGIN OPENSSH PRIVATE KEY-----\ndemo\n-----END OPENSSH PRIVATE KEY-----",
                "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDemoKey demo@winvaultwarden",
                "SHA256:demo1234567890"),
        },
        new Cipher
        {
            Id = "demo-deleted",
            Type = CipherType.Login,
            Name = "Deleted demo account",
            DeletedDate = DemoDate(19, 12, 0),
            CreationDate = DemoDate(10, 12, 0),
            RevisionDate = DemoDate(19, 12, 0),
            Login = new CipherLogin("old@example.com", "obsolete", null, []),
        },
    ];

    private static DateTimeOffset DemoDate(int day, int hour, int minute) =>
        new(2026, 6, day, hour, minute, 0, TimeSpan.Zero);
}
