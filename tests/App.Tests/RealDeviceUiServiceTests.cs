using App.Services;
using Core.Models;
using Core.Services;
using Core.Session;
using Xunit;

namespace App.Tests;

public class RealDeviceUiServiceTests
{
    [Fact]
    public void GetDevices_MapsCurrentDeviceAndAccount()
    {
        var vault = new TestVaultService();
        var devices = new DeviceUiService(vault, "d1").GetDevices();
        var account = new AccountUiService(vault).GetAccount();

        Assert.Equal("Desktop", devices[0].Name);
        Assert.True(devices[0].IsCurrent);
        Assert.False(devices[1].IsCurrent);
        Assert.NotEmpty(devices[0].Glyph);
        Assert.Equal("me@example.com", account.Email);
        Assert.Equal("https://vault.example", account.ServerUrl);
        Assert.Equal("M", account.Initial);
        Assert.Equal("PBKDF2 600000", account.KdfSummary);
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
        public IReadOnlyList<Cipher> Ciphers { get; } = [];
        public IReadOnlyList<Folder> Folders { get; } = [];
        public AccountInfo Account { get; } = new("me@example.com", "https://vault.example", "M", "PBKDF2 600000");
        public IReadOnlyList<DeviceInfo> Devices { get; } =
        [
            new DeviceInfo("d1", "Desktop", 6, "d1", DateTimeOffset.Parse("2026-06-24T00:00:00Z"), true),
            new DeviceInfo("d2", "Phone", 8, "d2", DateTimeOffset.Parse("2026-06-23T00:00:00Z"), false),
        ];
    }
}
