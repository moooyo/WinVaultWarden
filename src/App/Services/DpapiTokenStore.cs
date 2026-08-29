using System.Security.Cryptography;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

public sealed class DpapiTokenStore : ITokenStore
{
    private readonly string _path;
    private readonly Action<PersistedSession>? _migrationWriter;
    private readonly Func<string, byte[]>? _reader;

    public DpapiTokenStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden",
            "session.bin"))
    {
    }

    public DpapiTokenStore(string path) => _path = path;

    internal DpapiTokenStore(
        string path,
        Action<PersistedSession> migrationWriter)
    {
        ArgumentNullException.ThrowIfNull(migrationWriter);
        _path = path;
        _migrationWriter = migrationWriter;
    }

    internal DpapiTokenStore(
        string path,
        Func<string, byte[]> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _path = path;
        _reader = reader;
    }

    public bool TryLoad(out PersistedSession session)
    {
        session = null!;
        if (!OperatingSystem.IsWindows())
            return false;

        byte[]? jsonBytes = null;
        try
        {
            if (!File.Exists(_path))
                return false;

            var protectedBytes = _reader is null
                ? File.ReadAllBytes(_path)
                : _reader(_path);
            jsonBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var loaded = JsonSerializer.Deserialize(jsonBytes, AppJsonContext.Default.PersistedSession);
            if (loaded is null || !loaded.TryMigrate(out var normalized))
                return false;

            session = normalized;
            if (!ReferenceEquals(loaded, normalized))
                TryPersistMigration(normalized);
            return true;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (jsonBytes is not null)
                CryptographicOperations.ZeroMemory(jsonBytes);
        }
    }

    private void TryPersistMigration(PersistedSession session)
    {
        try
        {
            if (_migrationWriter is null)
                Save(session);
            else
                _migrationWriter(session);
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or CryptographicException)
        {
            // A validated migrated session remains usable when its best-effort
            // schema write-back cannot be persisted.
        }
    }

    public void Save(PersistedSession session)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI token storage is only supported on Windows.");
        if (!session.HasValidBinding)
            throw new InvalidDataException("Persisted session binding is invalid.");

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(session, AppJsonContext.Default.PersistedSession);
        try
        {
            var protectedBytes = ProtectedData.Protect(jsonBytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // 原子写:先写临时文件,再 File.Move 替换。避免写入中途崩溃/断电截断 session.bin,
            // 导致下次启动 TryLoad 失败、强制完整重新登录+全量重同步(R9)。与 FaviconCache.WriteAtomic 一致。
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jsonBytes);
        }
    }

    public void Clear()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
