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
    private const string LocalhostPasskeyCredentialId = "CQgHBg";
    private const string LocalhostPasskeyPrivateKey =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgfXz874GHUc-96SruIPvps7It_3vkLr_ip9gWl-QSu3WhRANCAARrUjrSufadd_VXs_3EfmvqIK1EFblpeKrR25ehyTmgVdpHGiEVsdrqvcSSuwtwSyLEh5LXjJMsTWOnvptRsy7L";
    private const string LocalhostPasskeyUserHandle = "CgsMDQ";

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
        // 演示 Send 存入会话内存,并把会话标记为演示/离线——SendUiService 据此从内存取值,
        // 避免在未配置服务端地址的演示会话里对 /api/sends 发起相对 URI 请求而抛异常。
        _session.SetSends(BuildSends());
        _session.MarkDemo();
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
        var folderWork = new Folder
        {
            Id = "demo-folder-work2",
            Name = "工作",
            RevisionDate = DemoDate(21, 10, 0),
        };
        var folderPersonal = new Folder
        {
            Id = "demo-folder-personal",
            Name = "个人",
            RevisionDate = DemoDate(22, 10, 0),
        };
        var folderFinance = new Folder
        {
            Id = "demo-folder-finance",
            Name = "财务",
            RevisionDate = DemoDate(23, 10, 0),
        };

        return new DecryptedVault(
            new AccountInfo("demo-account", "demo@winvaultwarden.local", new ServerOrigin("http://localhost"), "D", "Demo"),
            [folder, folderWork, folderPersonal, folderFinance],
            BuildCiphers(folder.Id, folderWork.Id, folderPersonal.Id, folderFinance.Id),
            0);
    }

    private static IReadOnlyList<Cipher> BuildCiphers(string folderId, string workFolderId, string personalFolderId, string financeFolderId) =>
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
            Id = "demo-local-passkey",
            Type = CipherType.Login,
            Name = "Local WebAuthn test",
            FolderId = folderId,
            Favorite = true,
            Notes = "Local-only demo passkey for browser bridge testing.",
            CreationDate = DemoDate(18, 10, 30),
            RevisionDate = DemoDate(23, 16, 15),
            Login = new CipherLogin("tester@localhost", null, null, [new CipherLoginUri("http://localhost:8787", null)])
            {
                Fido2Credentials =
                [
                    new CipherFido2Credential(
                        LocalhostPasskeyCredentialId,
                        "public-key",
                        "ECDSA",
                        "P-256",
                        LocalhostPasskeyPrivateKey,
                        "localhost",
                        LocalhostPasskeyUserHandle,
                        "tester@localhost",
                        0,
                        "Localhost",
                        "Local Demo User",
                        true,
                        DemoDate(18, 10, 30)),
                ],
            },
            Fields =
            [
                new CipherField("Purpose", "Browser passkey bridge smoke test", CipherFieldType.Text),
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

        // 以下为丰富演示数据新增的条目，用于点亮安全报告(密码重用/弱密码/不安全 URI/后续任务的已泄露检测)。
        new Cipher
        {
            Id = "demo-login-reddit",
            Type = CipherType.Login,
            Name = "Reddit",
            FolderId = personalFolderId,
            CreationDate = DemoDate(3, 9, 0),
            RevisionDate = DemoDate(3, 9, 0),
            Login = new CipherLogin("reddituser", "Summer2024", null, [new CipherLoginUri("https://reddit.com", null)]),
        },
        new Cipher
        {
            Id = "demo-login-ig",
            Type = CipherType.Login,
            Name = "Instagram",
            FolderId = personalFolderId,
            CreationDate = DemoDate(3, 9, 30),
            RevisionDate = DemoDate(3, 9, 30),
            Login = new CipherLogin("demo.ig", "Summer2024", null, [new CipherLoginUri("https://instagram.com", null)]),
        },
        new Cipher
        {
            Id = "demo-login-forum",
            Type = CipherType.Login,
            Name = "老论坛账号",
            FolderId = personalFolderId,
            CreationDate = DemoDate(3, 10, 0),
            RevisionDate = DemoDate(3, 10, 0),
            Login = new CipherLogin("oldforum", "Summer2024", null, [new CipherLoginUri("https://bbs.example.com", null)]),
        },
        new Cipher
        {
            Id = "demo-login-router",
            Type = CipherType.Login,
            Name = "家用路由器",
            FolderId = personalFolderId,
            CreationDate = DemoDate(4, 11, 0),
            RevisionDate = DemoDate(4, 11, 0),
            Login = new CipherLogin("admin", "admin", null, [new CipherLoginUri("http://192.168.1.1", null)]),
        },
        new Cipher
        {
            Id = "demo-login-nas",
            Type = CipherType.Login,
            Name = "NAS",
            FolderId = personalFolderId,
            CreationDate = DemoDate(4, 11, 30),
            RevisionDate = DemoDate(4, 11, 30),
            Login = new CipherLogin("nas-admin", "123456", null, [new CipherLoginUri("https://nas.local", null)]),
        },
        new Cipher
        {
            Id = "demo-login-oldmail",
            Type = CipherType.Login,
            Name = "旧邮箱",
            FolderId = personalFolderId,
            CreationDate = DemoDate(4, 12, 0),
            RevisionDate = DemoDate(4, 12, 0),
            Login = new CipherLogin("me@old.com", "qwerty", null, [new CipherLoginUri("https://mail.old.com", null)]),
        },
        new Cipher
        {
            Id = "demo-login-linkedin",
            Type = CipherType.Login,
            Name = "LinkedIn",
            FolderId = workFolderId,
            CreationDate = DemoDate(6, 13, 0),
            RevisionDate = DemoDate(6, 13, 0),
            Login = new CipherLogin("demo@work.com", "linkedin123", null, [new CipherLoginUri("https://linkedin.com", null)]),
        },
        new Cipher
        {
            Id = "demo-login-adobe",
            Type = CipherType.Login,
            Name = "Adobe",
            FolderId = workFolderId,
            CreationDate = DemoDate(6, 13, 30),
            RevisionDate = DemoDate(6, 13, 30),
            Login = new CipherLogin("demo@work.com", "adobe123", null, [new CipherLoginUri("https://adobe.com", null)]),
        },
        new Cipher
        {
            Id = "demo-login-google",
            Type = CipherType.Login,
            Name = "Google",
            FolderId = workFolderId,
            CreationDate = DemoDate(6, 14, 0),
            RevisionDate = DemoDate(6, 14, 0),
            Login = new CipherLogin("demo.user@gmail.com", "Qx7$mZ2!vKp9", null, [new CipherLoginUri("https://accounts.google.com", null)]),
        },
        new Cipher
        {
            Id = "demo-login-cmb",
            Type = CipherType.Login,
            Name = "招商银行",
            FolderId = financeFolderId,
            CreationDate = DemoDate(7, 15, 0),
            RevisionDate = DemoDate(7, 15, 0),
            Login = new CipherLogin("demo_cmb", "T8w#nL5^rQ2q", null, [new CipherLoginUri("https://cmbchina.com", null)]),
        },
        new Cipher
        {
            Id = "demo-login-netflix",
            Type = CipherType.Login,
            Name = "Netflix",
            FolderId = personalFolderId,
            CreationDate = DemoDate(7, 15, 30),
            RevisionDate = DemoDate(7, 15, 30),
            Login = new CipherLogin("demo@home.com", "Zj4!pW9&xM1c", null, [new CipherLoginUri("https://netflix.com", null)]),
        },
        new Cipher
        {
            Id = "demo-card-cmb",
            Type = CipherType.Card,
            Name = "招商信用卡",
            FolderId = financeFolderId,
            CreationDate = DemoDate(8, 10, 0),
            RevisionDate = DemoDate(8, 10, 0),
            Card = new CipherCard("Demo User", "5200000000000007", "11", "2029", "456", "Mastercard"),
        },
        new Cipher
        {
            Id = "demo-identity-work",
            Type = CipherType.Identity,
            Name = "工作身份",
            FolderId = workFolderId,
            CreationDate = DemoDate(9, 11, 0),
            RevisionDate = DemoDate(9, 11, 0),
            Identity = new CipherIdentity("Ms.", "Wei", null, "Zhang", "zhang-wei", "WinVaultWarden Inc.",
                null, null, null, "wei.zhang@work.example.com", "13900001111",
                "Floor 8, Tower A", null, null, "Beijing", null, "100000", "China"),
        },
        new Cipher
        {
            Id = "demo-note-wifi",
            Type = CipherType.SecureNote,
            Name = "家庭 WiFi 密码",
            FolderId = personalFolderId,
            Notes = "SSID: DemoHome-5G\n密码: wifi-demo-8888",
            CreationDate = DemoDate(10, 12, 0),
            RevisionDate = DemoDate(10, 12, 0),
            SecureNote = new CipherSecureNote(0),
        },
        new Cipher
        {
            Id = "demo-note-safe",
            Type = CipherType.SecureNote,
            Name = "保险柜密码",
            FolderId = personalFolderId,
            Notes = "客厅保险柜: 12-34-56",
            CreationDate = DemoDate(10, 12, 30),
            RevisionDate = DemoDate(10, 12, 30),
            SecureNote = new CipherSecureNote(0),
        },
    ];

    private static DateTimeOffset DemoDate(int day, int hour, int minute) =>
        new(2026, 6, day, hour, minute, 0, TimeSpan.Zero);

    // 本地演示 Send(一条文本 + 一条文件)。ShareUrl 为占位链接(仅用于「复制链接」演示,不可访问),
    // 删除日期设在演示叙事的近未来。均只存内存,不上传服务器。
    private static IReadOnlyList<Send> BuildSends() =>
    [
        new Send
        {
            Id = "demo-send-text",
            AccessId = "demo-send-text",
            Type = SendType.Text,
            Name = "周会纪要",
            Notes = "本地演示 Send，不会上传到服务器。",
            Text = new SendText("周会要点：1. 发布计划 2. 演示评审 3. 下周排期", false),
            AccessCount = 2,
            DeletionDate = DemoSendDeletionDate,
            ShareUrl = "https://demo.local/#/send/demo-send-text/demoAccessKey",
        },
        new Send
        {
            Id = "demo-send-file",
            AccessId = "demo-send-file",
            Type = SendType.File,
            Name = "设计稿.pdf",
            File = new SendFile("设计稿.pdf", 348_160, "340 KB", "demo-file-1"),
            HasPassword = true,
            DeletionDate = DemoSendDeletionDate,
            ShareUrl = "https://demo.local/#/send/demo-send-file/demoAccessKey",
        },
    ];

    private static DateTimeOffset DemoSendDeletionDate =>
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
}
