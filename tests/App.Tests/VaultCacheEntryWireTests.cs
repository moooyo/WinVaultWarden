using System.Text.Json;
using App.Services;
using Core.Models;
using Xunit;

namespace App.Tests;

public class VaultCacheEntryWireTests
{
    [Fact]
    public void VaultCacheEntry_context_matches_reflection_wire()
    {
        var e = new VaultCacheEntry(1, "me@x", "https://v", DateTimeOffset.UnixEpoch, "{}");
        // AppJsonContext 对整个 partial class 设置了 WriteIndented=true(供 session.bin/preferences.json 复用),
        // 反射基线须带同一开关才能字节级对齐,否则纯格式差异会掩盖真正的线格(字段名/大小写)回归。
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        Assert.Equal(
            JsonSerializer.Serialize(e, web),
            JsonSerializer.Serialize(e, AppJsonContext.Default.VaultCacheEntry));
    }
}
