using System.Security.Cryptography;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

/// <summary>ICacheStore 的 DPAPI(CurrentUser) 实现,原子写。缓存内容已是 EncString 密文,DPAPI 为第二层。</summary>
public sealed class DpapiVaultCacheStore : ICacheStore
{
    private readonly string _path;
    private readonly Func<string, byte[]>? _reader;

    public DpapiVaultCacheStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden", "vault-cache.bin"))
    {
    }

    public DpapiVaultCacheStore(string path) => _path = path;

    internal DpapiVaultCacheStore(
        string path,
        Func<string, byte[]> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _path = path;
        _reader = reader;
    }

    public void SaveVaultCache(VaultCacheEntry entry)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(entry, AppJsonContext.Default.VaultCacheEntry);
        try
        {
            var protectedBytes = ProtectedData.Protect(jsonBytes, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jsonBytes);
        }
    }

    public bool TryLoadVaultCache(out VaultCacheEntry entry)
    {
        entry = null!;
        if (!OperatingSystem.IsWindows() || !File.Exists(_path))
            return false;

        byte[]? jsonBytes = null;
        try
        {
            var protectedBytes = _reader is null
                ? File.ReadAllBytes(_path)
                : _reader(_path);
            jsonBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var loaded = JsonSerializer.Deserialize(jsonBytes, AppJsonContext.Default.VaultCacheEntry);
            if (loaded is null)
                return false;
            entry = loaded;
            return true;
        }
        catch (Exception ex) when (
            ex is CryptographicException
                or JsonException
                or PlatformNotSupportedException
                or IOException
                or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (jsonBytes is not null)
                CryptographicOperations.ZeroMemory(jsonBytes);
        }
    }

    public void ClearVaultCache()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
