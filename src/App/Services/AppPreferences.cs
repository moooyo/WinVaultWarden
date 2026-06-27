using System.Text.Json;

namespace App.Services;

/// <summary>
/// 非敏感的应用偏好(如主题)持久化到本地 JSON 文件。
/// 与 <see cref="DpapiTokenStore"/> 共用 LocalApplicationData/WinVaultWarden 目录,
/// 但主题无需加密,故用明文 JSON。任何读写失败都静默降级为默认值,绝不抛到 UI。
/// </summary>
public static class AppPreferences
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinVaultWarden",
        "preferences.json");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static AppPreferencesData? _data;

    public static AppPreferencesData Current => _data ??= Load();

    private static AppPreferencesData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppPreferencesData>(json, JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }

        return new AppPreferencesData();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class AppPreferencesData
{
    /// <summary>主题:0=跟随系统,1=浅色,2=深色。与设置页 ComboBox 索引一致。</summary>
    public int ThemeIndex { get; set; }
}
