using System.Text.Json;
using App.Services;
using Core.Enums;
using Core.Models;
using Xunit;

namespace App.Tests;

public class AppJsonContextTests
{
    [Fact]
    public void PersistedSession_roundtrips_via_context()
    {
        var s = new PersistedSession("https://srv", "a@b.com", "dev", "rt", "2.key", KdfType.Pbkdf2, 600000, null, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(s, AppJsonContext.Default.PersistedSession);
        var back = JsonSerializer.Deserialize(bytes, AppJsonContext.Default.PersistedSession)!;
        Assert.Equal(s, back);
    }

    [Fact]
    public void AppPreferencesData_roundtrips_via_context()
    {
        var p = new AppPreferencesData { ThemeIndex = 2 };
        var json = JsonSerializer.Serialize(p, AppJsonContext.Default.AppPreferencesData);
        var back = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppPreferencesData)!;
        Assert.Equal(2, back.ThemeIndex);
    }
}
