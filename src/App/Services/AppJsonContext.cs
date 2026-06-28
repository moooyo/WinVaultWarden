using System.Text.Json.Serialization;
using Core.Models;

namespace App.Services;

// session.bin(DPAPI 内层)与 preferences.json 的本地持久化。
// 沿用历史 Web 语义;偏好文件额外缩进(WriteIndented)。
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(PersistedSession))]
[JsonSerializable(typeof(AppPreferencesData))]
public partial class AppJsonContext : JsonSerializerContext;
