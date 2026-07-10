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
            // fail-closed:缺失 account/origin/generation 绑定的旧记录一律拒绝返回,迫使用户
            // 用下一次正常登录创建 schema 2 记录,而不是把未绑定身份的 session 当作当前账户使用。
            if (loaded is null || !loaded.HasValidBinding)
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
