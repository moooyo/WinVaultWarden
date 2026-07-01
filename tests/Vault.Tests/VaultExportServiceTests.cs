using Core.Enums;
using Core.Models;
using Core.Services;
using Vault;
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

        public Core.Session.IVaultSnapshot Snapshot => throw new NotSupportedException();
        public IReadOnlyList<Cipher> GetCiphers() => _ciphers;
        public IReadOnlyList<Folder> GetFolders() => _folders;
        public IReadOnlyList<DeviceInfo> GetDevices() => Array.Empty<DeviceInfo>();
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
}
