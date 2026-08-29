using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using App.Services;
using Core.Enums;
using Core.Models;
using Xunit;

namespace App.Tests;

public class DpapiTokenStoreTests
{
    [WindowsFact]
    public void SaveLoadAndClear_RoundTripsWithoutPlaintextJson()
    {
        var path = Path.Combine(Path.GetTempPath(), "WinVaultWarden.Tests", Guid.NewGuid().ToString("N"), "session.bin");
        var store = new DpapiTokenStore(path);
        var session = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-1",
            new ServerOrigin("https://vault.example"),
            7,
            "me@example.com",
            "device-id",
            "refresh-token",
            "protected-user-key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);

        store.Save(session);

        var fileText = File.ReadAllText(path);
        Assert.DoesNotContain("refresh-token", fileText);
        Assert.DoesNotContain("protected-user-key", fileText);
        Assert.True(store.TryLoad(out var loaded));
        Assert.Equal(session, loaded);

        store.Clear();

        Assert.False(File.Exists(path));
        Assert.False(store.TryLoad(out _));
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTempAndReplacesExisting()
    {
        // R9:原子写(temp+move)——保存后不残留 .tmp,且再次保存原子替换、可正确读回最新值。
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), "WinVaultWarden.Tests", Guid.NewGuid().ToString("N"), "session.bin");
        var store = new DpapiTokenStore(path);
        var first = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-1",
            new ServerOrigin("https://vault.example"),
            7,
            "me@example.com",
            "device-id",
            "refresh-1",
            "puk",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);

        store.Save(first);
        store.Save(first with { RefreshToken = "refresh-2" }); // 覆盖

        Assert.False(File.Exists(path + ".tmp"), "临时文件应已被 Move,不应残留");
        Assert.True(store.TryLoad(out var loaded));
        Assert.Equal("refresh-2", loaded.RefreshToken);
    }

    [Fact]
    public void SaveLoad_RoundTripsTwoFactorToken_Encrypted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), "WinVaultWarden.Tests", Guid.NewGuid().ToString("N"), "session.bin");
        var store = new DpapiTokenStore(path);
        var session = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-1",
            new ServerOrigin("https://vault.example"),
            7,
            "me@example.com",
            "device-id",
            "refresh",
            "puk",
            KdfType.Pbkdf2,
            600_000,
            null,
            null)
        {
            TwoFactorToken = "remember-jwt",
        };

        store.Save(session);

        Assert.True(store.TryLoad(out var loaded));
        Assert.Equal("remember-jwt", loaded.TwoFactorToken);
        Assert.DoesNotContain("remember-jwt", File.ReadAllText(path)); // DPAPI 加密,不留明文
    }

    [Fact]
    public void SaveLoad_RoundTripsHelloFields_Encrypted()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), "WinVaultWarden.Tests", Guid.NewGuid().ToString("N"), "session.bin");
        var store = new DpapiTokenStore(path);
        var session = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-1",
            new ServerOrigin("https://vault.example"),
            7,
            "me@example.com",
            "device-id",
            "refresh",
            "puk",
            KdfType.Pbkdf2,
            600_000,
            null,
            null)
        {
            HelloProtectedUserKey = "2.hellowrapped==",
            HelloChallenge = "Y2hhbGxlbmdl",
        };

        store.Save(session);

        Assert.True(store.TryLoad(out var loaded));
        Assert.Equal("2.hellowrapped==", loaded.HelloProtectedUserKey);
        Assert.Equal("Y2hhbGxlbmdl", loaded.HelloChallenge);
        Assert.DoesNotContain("2.hellowrapped==", File.ReadAllText(path)); // DPAPI 加密,不留明文
    }

    [Fact]
    public void TryLoad_LegacySessionWithoutBinding_ReturnsFalse()
    {
        // fail-closed 回归:schema 1(旧字段名 serverUrl,无 accountId/generation)的记录必须被拒绝,
        // 不能被静默当作当前账户的会话使用——即便 DPAPI 解密本身成功。
        if (!OperatingSystem.IsWindows())
            return;
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.bin");
        Directory.CreateDirectory(directory);
        var legacyJson = Encoding.UTF8.GetBytes(
            """
            {
              "serverUrl":"https://vault.example",
              "email":"me@example.com",
              "deviceIdentifier":"device-id",
              "refreshToken":"refresh",
              "protectedUserKey":"2.key",
              "kdfType":0,
              "kdfIterations":600000
            }
            """);
        try
        {
            File.WriteAllBytes(
                path,
                ProtectedData.Protect(
                    legacyJson,
                    null,
                    DataProtectionScope.CurrentUser));
            var store = new DpapiTokenStore(path);
            Assert.False(store.TryLoad(out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacyJson);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_SchemaTwo_MigratesAndPersistsSchemaThreeEndpoints()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.bin");
        Directory.CreateDirectory(directory);
        var legacy = new PersistedSession(
            PersistedSession.LegacySchemaVersion,
            "account-1",
            new ServerOrigin("https://vault.bitwarden.com"),
            7,
            "me@example.com",
            "device-id",
            "refresh",
            "2.key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);
        var legacyJson = JsonSerializer.SerializeToUtf8Bytes(
            legacy,
            AppJsonContext.Default.PersistedSession);
        try
        {
            File.WriteAllBytes(
                path,
                ProtectedData.Protect(
                    legacyJson,
                    null,
                    DataProtectionScope.CurrentUser));

            var store = new DpapiTokenStore(path);
            Assert.True(store.TryLoad(out var migrated));
            Assert.Equal(PersistedSession.CurrentSchemaVersion, migrated.SchemaVersion);
            Assert.Equal(ServerEndpoints.BitwardenUs, migrated.Endpoints);

            var persistedJson = ProtectedData.Unprotect(
                File.ReadAllBytes(path),
                null,
                DataProtectionScope.CurrentUser);
            try
            {
                var persisted = JsonSerializer.Deserialize(
                    persistedJson,
                    AppJsonContext.Default.PersistedSession)!;
                Assert.True(persisted.HasValidBinding);
                Assert.Equal(ServerEndpoints.BitwardenUs, persisted.Endpoints);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(persistedJson);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacyJson);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_SchemaTwo_WhenMigrationWriteThrowsIOException_ReturnsMigratedSession()
    {
        AssertMigrationWriteFailureDoesNotBlockLoad(
            new IOException("Migration write failed."));
    }

    [Fact]
    public void TryLoad_SchemaTwo_WhenMigrationWriteThrowsUnauthorizedAccess_ReturnsMigratedSession()
    {
        AssertMigrationWriteFailureDoesNotBlockLoad(
            new UnauthorizedAccessException("Migration write failed."));
    }

    [Fact]
    public void TryLoad_SchemaTwo_WhenMigrationWriteThrowsCryptographicException_ReturnsMigratedSession()
    {
        AssertMigrationWriteFailureDoesNotBlockLoad(
            new CryptographicException("Migration write failed."));
    }

    [Fact]
    public void TryLoad_CorruptProtectedPayload_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.bin");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);

            var store = new DpapiTokenStore(path);

            Assert.False(store.TryLoad(out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_ProtectedInvalidJson_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.bin");
        Directory.CreateDirectory(directory);
        var invalidJson = Encoding.UTF8.GetBytes("{not-json");
        try
        {
            File.WriteAllBytes(
                path,
                ProtectedData.Protect(
                    invalidJson,
                    null,
                    DataProtectionScope.CurrentUser));

            var store = new DpapiTokenStore(path);

            Assert.False(store.TryLoad(out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(invalidJson);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryLoad_WhenReadThrowsIOException_ReturnsFalse()
    {
        AssertReadFailureReturnsFalse(new IOException("read failed"));
    }

    [Fact]
    public void TryLoad_WhenReadThrowsUnauthorizedAccess_ReturnsFalse()
    {
        AssertReadFailureReturnsFalse(
            new UnauthorizedAccessException("read denied"));
    }

    private static void AssertReadFailureReturnsFalse(Exception readFailure)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.bin");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(path, [0]);
            Func<string, byte[]> reader = _ => throw readFailure;
            var store = new DpapiTokenStore(path, reader);

            Assert.False(store.TryLoad(out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertMigrationWriteFailureDoesNotBlockLoad(Exception writeFailure)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(
            Path.GetTempPath(),
            "WinVaultWarden.Tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.bin");
        Directory.CreateDirectory(directory);
        var legacy = new PersistedSession(
            PersistedSession.LegacySchemaVersion,
            "account-1",
            new ServerOrigin("https://vault.bitwarden.com"),
            7,
            "me@example.com",
            "device-id",
            "refresh",
            "2.key",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);
        var legacyJson = JsonSerializer.SerializeToUtf8Bytes(
            legacy,
            AppJsonContext.Default.PersistedSession);
        try
        {
            File.WriteAllBytes(
                path,
                ProtectedData.Protect(
                    legacyJson,
                    null,
                    DataProtectionScope.CurrentUser));
            PersistedSession? attemptedWrite = null;
            var store = new DpapiTokenStore(
                path,
                migrated =>
                {
                    attemptedWrite = migrated;
                    throw writeFailure;
                });

            Assert.True(store.TryLoad(out var loaded));
            Assert.Equal(PersistedSession.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(ServerEndpoints.BitwardenUs, loaded.Endpoints);
            Assert.Same(loaded, attemptedWrite);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(legacyJson);
            Directory.Delete(directory, recursive: true);
        }
    }
}
