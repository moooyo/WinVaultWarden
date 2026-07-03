namespace Core.Services;

public interface IFaviconCache
{
    // 命中返回 favicon PNG 字节;无图标/失败/未登录 返回 null。绝不抛。
    Task<byte[]?> GetAsync(string domain, CancellationToken ct = default);

    // 登出时清空本地图标缓存(删除缓存目录下所有条目)。best-effort,绝不抛。
    void Clear();
}
