using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Core.Services;

namespace Vault;

public sealed class FaviconCache : IFaviconCache
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly VaultSession _session;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _inflight = new(StringComparer.Ordinal);

    public FaviconCache(HttpClient http, VaultSession session, string? cacheDir = null)
    {
        _http = http;
        _session = session;
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden", "IconCache");
    }

    public Task<byte[]?> GetAsync(string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return Task.FromResult<byte[]?>(null);
        return _inflight.GetOrAdd(domain, d => LoadAsync(d, ct))
            .ContinueWith(t => { _inflight.TryRemove(domain, out _); return t.GetAwaiter().GetResult(); }, ct);
    }

    private async Task<byte[]?> LoadAsync(string domain, CancellationToken ct)
    {
        var hash = Hash(domain);
        var png = Path.Combine(_cacheDir, hash + ".png");
        var none = Path.Combine(_cacheDir, hash + ".none");

        // 1. 命中未过期缓存
        if (Fresh(png, PositiveTtl)) { try { return await File.ReadAllBytesAsync(png, ct); } catch { } }
        if (Fresh(none, NegativeTtl)) return null;

        // 2. 拉取
        var serverUrl = _session.Account.ServerUrl;
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;
        var url = serverUrl.TrimEnd('/') + "/icons/" + domain + "/icon.png";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var ctType = resp.Content.Headers.ContentType?.MediaType;
            if (resp.IsSuccessStatusCode && ctType is not null && ctType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > 0) { WriteAtomic(png, bytes); TryDelete(none); return bytes; }
            }
            WriteAtomic(none, Array.Empty<byte>()); // 负缓存
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // 拉取失败:若有旧 .png(即便过期)则用旧的
            if (File.Exists(png)) { try { return await File.ReadAllBytesAsync(png, ct); } catch { } }
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

    private static string Hash(string domain)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(domain.ToLowerInvariant())));
}
