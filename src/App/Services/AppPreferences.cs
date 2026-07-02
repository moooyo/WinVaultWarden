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

    private static readonly Lock InitLock = new();
    private static volatile AppPreferencesData? _data;

    public static AppPreferencesData Current
    {
        get
        {
            if (_data is not null)
                return _data;
            lock (InitLock)
            {
                return _data ??= Load();
            }
        }
    }

    private static AppPreferencesData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppPreferencesData);
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
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, AppJsonContext.Default.AppPreferencesData));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class AppPreferencesData
{
    /// <summary>主题:0=跟随系统,1=浅色,2=深色。与设置页 ComboBox 索引一致。</summary>
    public int ThemeIndex { get; set; }

    /// <summary>是否显示网站 favicon。默认开。</summary>
    public bool ShowWebsiteIcons { get; set; } = true;

    /// <summary>本地保存的命名筛选视图(不同步服务端)。</summary>
    public List<SavedSearchViewData> SavedSearchViews { get; set; } = new();

    /// <summary>会话超时 ComboBox 索引(0重启时..7永不)。默认 3=15分钟。</summary>
    public int SessionTimeoutIndex { get; set; } = 3;

    /// <summary>超时动作:0=锁定(默认),1=登出。</summary>
    public int TimeoutActionIndex { get; set; }

    /// <summary>清空剪贴板 ComboBox 索引(0永不..6=5分)。默认 3=30秒。</summary>
    public int ClearClipboardIndex { get; set; } = 3;
}
