using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Core.Models;
using Core.Services;

namespace Vault;

public sealed class FaviconCache : IFaviconCache
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly VaultSession _session;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<CacheKey, Task<byte[]?>> _inflight = [];

    public FaviconCache(HttpClient http, VaultSession session, string? cacheDir = null)
    {
        _http = http;
        _session = session;
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden", "IconCache");
    }

    public async Task<byte[]?> GetAsync(string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        ServerEndpoint iconsEndpoint;
        try
        {
            iconsEndpoint = _session.CaptureLease().Endpoints.Icons;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var key = new CacheKey(
            iconsEndpoint,
            domain.Trim().ToLowerInvariant());
        var shared = _inflight.GetOrAdd(key, LoadAsync);
        try
        {
            return await shared.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null; // 绝不抛:调用方取消只影响自己的等待,不影响共享抓取
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shared.IsCompleted)
                _inflight.TryRemove(new KeyValuePair<CacheKey, Task<byte[]?>>(key, shared));
        }
    }

    private async Task<byte[]?> LoadAsync(CacheKey key)
    {
        var hash = Hash(key);
        var png = Path.Combine(_cacheDir, hash + ".png");
        var none = Path.Combine(_cacheDir, hash + ".none");

        // 1. 命中未过期缓存
        if (Fresh(png, PositiveTtl)) { try { return await File.ReadAllBytesAsync(png, CancellationToken.None); } catch { } }
        if (Fresh(none, NegativeTtl)) return null;

        var url = key.Endpoint.Resolve(
            $"{Uri.EscapeDataString(key.Domain)}/icon.png");

        try
        {
            using var resp = await _http.GetAsync(url, CancellationToken.None);
            var ctType = resp.Content.Headers.ContentType?.MediaType;
            if (resp.IsSuccessStatusCode && ctType is not null && ctType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync(CancellationToken.None);
                if (bytes.Length > 0) { WriteAtomic(png, bytes); TryDelete(none); return bytes; }
            }
            WriteAtomic(none, Array.Empty<byte>()); // 负缓存
            return null;
        }
        catch
        {
            // 拉取失败:若有旧 .png(即便过期)则用旧的
            if (File.Exists(png)) { try { return await File.ReadAllBytesAsync(png, CancellationToken.None); } catch { } }
            return null;
        }
    }

    private static bool Fresh(string path, TimeSpan ttl)
        => File.Exists(path) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)) < ttl;

    private void WriteAtomic(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var tmp = path + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* 缓存写失败不致命 */ }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    // Clear all cached icons on logout. Failures are deliberately non-fatal.
    public void Clear()
    {
        try
        {
            if (!Directory.Exists(_cacheDir))
                return;
            foreach (var file in Directory.EnumerateFiles(_cacheDir))
                TryDelete(file);
        }
        catch { /* 目录枚举失败(不存在/占用)忽略 */ }
    }

    private static string Hash(CacheKey key)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{key.Endpoint.Value}\n{key.Domain}")));

    private readonly record struct CacheKey(ServerEndpoint Endpoint, string Domain);
}
