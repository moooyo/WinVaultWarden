using System.Security.Cryptography;
using System.Text.Json;
using Core.Abstractions;
using Core.Models;

namespace App.Services;

public sealed class DpapiTokenStore : ITokenStore
{
    private readonly string _path;

    public DpapiTokenStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden",
            "session.bin"))
    {
    }

    public DpapiTokenStore(string path) => _path = path;

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

            var protectedBytes = File.ReadAllBytes(_path);
            jsonBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var loaded = JsonSerializer.Deserialize(jsonBytes, AppJsonContext.Default.PersistedSession);
            if (loaded is null)
                return false;

            session = loaded;
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
        finally
        {
            if (jsonBytes is not null)
                CryptographicOperations.ZeroMemory(jsonBytes);
        }
    }

    public void Save(PersistedSession session)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI token storage is only supported on Windows.");

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(session, AppJsonContext.Default.PersistedSession);
        var protectedBytes = ProtectedData.Protect(jsonBytes, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, protectedBytes);
        CryptographicOperations.ZeroMemory(jsonBytes);
    }

    public void Clear()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
