using Core.Enums;
using Core.Models;
using Core.Services;
using Vault;
using Vault.Porting;
using Xunit;

namespace Vault.Tests;

public class VaultExportServiceTests
{
    private sealed class FakeVaultService : IVaultService
    {
        private readonly IReadOnlyList<Cipher> _ciphers;
        private readonly IReadOnlyList<Folder> _folders;

        public FakeVaultService(IReadOnlyList<Cipher> ciphers, IReadOnlyList<Folder>? folders = null)
        {
            _ciphers = ciphers;
            _folders = folders ?? Array.Empty<Folder>();
        }

        public Core.Session.IVaultSnapshot Snapshot { get; } = new EmptySnapshot();
        public IReadOnlyList<Cipher> GetCiphers() => _ciphers;
        public IReadOnlyList<Folder> GetFolders() => _folders;
        public IReadOnlyList<DeviceInfo> GetDevices() => Array.Empty<DeviceInfo>();
        public Core.Session.VaultSessionLease CaptureLease() =>
            throw new InvalidOperationException("This fake has no vault session.");
        public bool IsCurrent(Core.Session.VaultSessionLease lease) => false;
        public void ThrowIfExpired(Core.Session.VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);
        public IReadOnlyList<Cipher> GetCiphers(Core.Session.VaultSessionLease lease) =>
            throw new OperationCanceledException(lease.CancellationToken);

        private sealed class EmptySnapshot : Core.Session.IVaultSnapshot
        {
            public Core.Session.VaultState State => Core.Session.VaultState.Unlocked;
            public IReadOnlyList<Cipher> Ciphers => Array.Empty<Cipher>();
            public IReadOnlyList<Folder> Folders => Array.Empty<Folder>();
            public IReadOnlyList<DeviceInfo> Devices => Array.Empty<DeviceInfo>();
            public AccountInfo Account => AccountInfo.Empty;
            public int SkippedCipherCount => 0;
            public int SkippedFolderCount => 0;
            public bool DataStale => false;
            public long Generation => 1;
        }
    }

    private static Cipher[] ActiveAndTrashed() => new[]
    {
        new Cipher { Id = "1", Type = CipherType.Login, Name = "Active", Login = new CipherLogin("u", "p", null, Array.Empty<CipherLoginUri>()) },
        new Cipher { Id = "2", Type = CipherType.Login, Name = "Trashed", DeletedDate = DateTimeOffset.UtcNow, Login = new CipherLogin("u", "p", null, Array.Empty<CipherLoginUri>()) },
    };

    [Fact]
    public void Export_Json_ExcludesTrash_ContainsActive()
    {
        var vault = new FakeVaultService(ActiveAndTrashed());
        var svc = new VaultExportService(vault);
        var json = svc.Export(ExportFormat.Json);
        Assert.Contains("Active", json);
        Assert.DoesNotContain("Trashed", json);
    }

    [Fact]
    public void PersonalExport_ExcludesOrganizationOwnedItems()
    {
        var personal = new Cipher
        {
            Id = "personal",
            Type = CipherType.Login,
            Name = "Personal",
            Login = new CipherLogin(null, null, null, []),
        };
        var organization = new Cipher
        {
            Id = "shared",
            Type = CipherType.Login,
            OrganizationId = "org-1",
            Name = "Shared",
            Login = new CipherLogin(null, null, null, []),
        };
        var service = new VaultExportService(new FakeVaultService([personal, organization]));

        var json = service.Export(ExportFormat.Json);

        Assert.Contains("Personal", json);
        Assert.DoesNotContain("Shared", json);
    }

    [Fact]
    public void Export_DisablePersonalVaultExportPolicyFailsBeforePlaintextSerialization()
    {
        var session = new VaultSession();
        session.BeginAccountSession(
            "account",
            "me@example.com",
            new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new Crypto.SymmetricCryptoKey(new byte[64]));
        session.SetSnapshot(new DecryptedVault(session.Account, [], ActiveAndTrashed(), 0, 0)
        {
            Organizations = [new VaultOrganization("org-1", "Team", 2, 2, true)],
            Policies =
            [
                new VaultOrganizationPolicy(
                    "policy-1",
                    "org-1",
                    (int)OrganizationPolicyType.DisablePersonalVaultExport,
                    true,
                    "null"),
            ],
        });
        var service = new VaultExportService(new VaultService(session));

        Assert.False(service.CanExport);
        Assert.Throws<OrganizationPolicyException>(() => service.Export(ExportFormat.Json));
    }

    [Fact]
    public void Export_Json_IncludesFido2CredentialWireFields()
    {
        var created = new DateTimeOffset(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);
        var credential = new CipherFido2Credential(
            "credential-id", "public-key", "ECDSA", "P-256", "private-key-pkcs8",
            "example.com", "user-handle", "user@example.com", 42,
            "Example", "Example User", true, created);
        var cipher = new Cipher
        {
            Id = "1", Type = CipherType.Login, Name = "Passkey",
            Login = new CipherLogin("user@example.com", null, null, Array.Empty<CipherLoginUri>())
            {
                Fido2Credentials = [credential],
            },
        };
        var vault = new FakeVaultService(new[] { cipher });
        var svc = new VaultExportService(vault);

        var json = svc.Export(ExportFormat.Json);

        // 导出是用户显式选择的未加密备份内容：私钥（keyValue）以明文写入；
        // counter/discoverable 在 wire 上是字符串，creationDate 精确保留。
        Assert.Contains("\"keyValue\":\"private-key-pkcs8\"", json);
        Assert.Contains("\"counter\":\"42\"", json);
        Assert.Contains("\"discoverable\":\"true\"", json);
        Assert.Contains("\"creationDate\":\"2026-06-24T00:00:00", json);
    }

    [Fact]
    public void Export_Csv_ExcludesTrash_ContainsActiveRow()
    {
        var vault = new FakeVaultService(ActiveAndTrashed());
        var svc = new VaultExportService(vault);
        var csv = svc.Export(ExportFormat.Csv);

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',');
        Assert.Equal(11, header.Length);

        Assert.Contains(lines, l => l.Contains("Active"));
        Assert.DoesNotContain(lines, l => l.Contains("Trashed"));
    }

    [Theory]
    [InlineData(CipherType.Card)]
    [InlineData(CipherType.Identity)]
    [InlineData(CipherType.SshKey)]
    [InlineData(CipherType.BankAccount)]
    [InlineData(CipherType.DriversLicense)]
    [InlineData(CipherType.Passport)]
    [InlineData((CipherType)999)]
    public void Export_Csv_TypeWithoutOfficialCsvShape_RejectsEntireExport(CipherType type)
    {
        var service = new VaultExportService(new FakeVaultService(
        [
            ActiveAndTrashed()[0],
            new Cipher { Id = "unsupported", Type = type, Name = "Unsupported" },
        ]));

        var exception = Assert.Throws<UnsupportedCipherCsvExportException>(() =>
            service.Export(ExportFormat.Csv));

        Assert.Equal(type, exception.CipherType);
        Assert.Contains("JSON", exception.Message, StringComparison.Ordinal);
    }
}
