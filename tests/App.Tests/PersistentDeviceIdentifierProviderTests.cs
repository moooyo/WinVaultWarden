using App.Services;
using Core.Enums;
using Core.Models;
using Vault;
using Xunit;

namespace App.Tests;

public sealed class PersistentDeviceIdentifierProviderTests
{
    [Fact]
    public void GetDeviceIdentifier_IsStableAcrossProviderInstances()
    {
        var path = TemporaryPath();
        try
        {
            var first = new PersistentDeviceIdentifierProvider(new MemoryTokenStore(), path)
                .GetDeviceIdentifier();
            var second = new PersistentDeviceIdentifierProvider(new MemoryTokenStore(), path)
                .GetDeviceIdentifier();

            Assert.True(Guid.TryParseExact(first, "D", out _));
            Assert.Equal(first, second);
            Assert.Equal(first, File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void GetDeviceIdentifier_PreservesExistingFileIdentifier()
    {
        const string existing = "11111111-2222-3333-4444-555555555555";
        const string sessionIdentifier = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var path = TemporaryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, existing);
        var store = new MemoryTokenStore();
        store.Save(CreateSession(sessionIdentifier));
        try
        {
            var actual = new PersistentDeviceIdentifierProvider(store, path)
                .GetDeviceIdentifier();

            Assert.Equal(existing, actual);
            Assert.Equal(existing, File.ReadAllText(path));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void GetDeviceIdentifier_EmptyGuidFileIsReplaced()
    {
        var path = TemporaryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Guid.Empty.ToString("D"));
        try
        {
            var actual = new PersistentDeviceIdentifierProvider(
                new MemoryTokenStore(),
                path).GetDeviceIdentifier();

            Assert.True(Guid.TryParseExact(actual, "D", out var identifier));
            Assert.NotEqual(Guid.Empty, identifier);
            Assert.Equal(actual, File.ReadAllText(path));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void GetDeviceIdentifier_OversizedCorruptFileIsReplacedWithoutReadingIt()
    {
        var path = TemporaryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[1024]);
        try
        {
            var identifier = new PersistentDeviceIdentifierProvider(
                new MemoryTokenStore(),
                path).GetDeviceIdentifier();

            Assert.True(Guid.TryParseExact(identifier, "D", out _));
            Assert.Equal(36, new FileInfo(path).Length);
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void GetDeviceIdentifier_FirstUpgradeAdoptsExistingSessionIdentifier()
    {
        const string existing = "11111111-2222-3333-4444-555555555555";
        var path = TemporaryPath();
        var store = new MemoryTokenStore();
        store.Save(CreateSession(existing));
        try
        {
            var actual = new PersistentDeviceIdentifierProvider(store, path)
                .GetDeviceIdentifier();

            Assert.Equal(existing, actual);
            Assert.Equal(existing, File.ReadAllText(path));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void GetDeviceIdentifier_EmptyGuidTokenStoreIdentifierIsReplaced()
    {
        var path = TemporaryPath();
        var store = new MemoryTokenStore();
        store.Save(CreateSession(Guid.Empty.ToString("D")));
        try
        {
            var actual = new PersistentDeviceIdentifierProvider(store, path)
                .GetDeviceIdentifier();

            Assert.True(Guid.TryParseExact(actual, "D", out var identifier));
            Assert.NotEqual(Guid.Empty, identifier);
            Assert.Equal(actual, File.ReadAllText(path));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    [Fact]
    public void GetDeviceIdentifier_CorruptFileFallsBackToExistingSessionIdentifier()
    {
        const string existing = "11111111-2222-3333-4444-555555555555";
        var path = TemporaryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not-a-device-identifier");
        var store = new MemoryTokenStore();
        store.Save(CreateSession(existing));
        try
        {
            var actual = new PersistentDeviceIdentifierProvider(store, path)
                .GetDeviceIdentifier();

            Assert.Equal(existing, actual);
            Assert.Equal(existing, File.ReadAllText(path));
        }
        finally
        {
            DeleteIfPresent(path);
        }
    }

    private static PersistedSession CreateSession(string deviceIdentifier) => new(
        PersistedSession.CurrentSchemaVersion,
        "account-id",
        ServerEndpoints.FromWebVault("https://vault.example"),
        1,
        "me@example.com",
        deviceIdentifier,
        "refresh-token",
        "2.protected-key",
        KdfType.Pbkdf2,
        600_000,
        null,
        null);

    private static string TemporaryPath() => Path.Combine(
        Path.GetTempPath(),
        $"wvw-device-id-{Guid.NewGuid():N}",
        "device-id");

    private static void DeleteIfPresent(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
