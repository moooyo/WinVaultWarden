using System.Text.Json;

namespace App.Services;

public static class AppPreferenceIndexPolicy
{
    public const int NoSelectionIndex = -1;
    public const int DefaultThemeIndex = 0;
    public const int DefaultSessionTimeoutIndex = 3;
    public const int DefaultTimeoutActionIndex = 0;
    public const int DefaultClearClipboardIndex = 3;

    public static int NormalizeThemeIndex(int value) =>
        value is >= 0 and <= 2 ? value : DefaultThemeIndex;

    public static int NormalizeSessionTimeoutIndex(int value) =>
        value is >= 0 and <= 8 ? value : DefaultSessionTimeoutIndex;

    public static int NormalizeTimeoutActionIndex(int value) =>
        value is >= 0 and <= 1 ? value : DefaultTimeoutActionIndex;

    public static int NormalizeClearClipboardIndex(int value) =>
        value is >= 0 and <= 6 ? value : DefaultClearClipboardIndex;

    public static bool IsTransientNoSelection(int value) => value == NoSelectionIndex;

    public static bool Normalize(AppPreferencesData preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var themeIndex = NormalizeThemeIndex(preferences.ThemeIndex);
        var sessionTimeoutIndex = NormalizeSessionTimeoutIndex(preferences.SessionTimeoutIndex);
        var timeoutActionIndex = NormalizeTimeoutActionIndex(preferences.TimeoutActionIndex);
        var clearClipboardIndex = NormalizeClearClipboardIndex(preferences.ClearClipboardIndex);
        var changed = themeIndex != preferences.ThemeIndex
            || sessionTimeoutIndex != preferences.SessionTimeoutIndex
            || timeoutActionIndex != preferences.TimeoutActionIndex
            || clearClipboardIndex != preferences.ClearClipboardIndex;

        preferences.ThemeIndex = themeIndex;
        preferences.SessionTimeoutIndex = sessionTimeoutIndex;
        preferences.TimeoutActionIndex = timeoutActionIndex;
        preferences.ClearClipboardIndex = clearClipboardIndex;
        return changed;
    }
}

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
    private static readonly Lock PersistenceLock = new();
    private static volatile AppPreferencesData? _data;
    private static AppPreferencesData? _inMemoryTestData;

    public static AppPreferencesData Current
    {
        get
        {
            var testData = Volatile.Read(ref _inMemoryTestData);
            if (testData is not null)
                return testData;
            if (_data is not null)
                return _data;
            lock (InitLock)
            {
                return _data ??= Load();
            }
        }
    }

    private static AppPreferencesData Load() => Load(FilePath);

    private static AppPreferencesData Load(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                var loaded = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppPreferencesData);
                if (loaded is not null)
                {
                    if (AppPreferenceIndexPolicy.Normalize(loaded))
                        TrySaveToDisk(loaded, filePath);
                    return loaded;
                }
            }
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }

        return new AppPreferencesData();
    }

    public static void Save()
    {
        if (Volatile.Read(ref _inMemoryTestData) is not null)
            return;

        TrySaveToDisk(Current, FilePath);
    }

    private static void TrySaveToDisk(AppPreferencesData data, string filePath)
    {
        lock (PersistenceLock)
        {
            string? temporaryPath = null;
            try
            {
                var directory = Path.GetDirectoryName(filePath)!;
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(filePath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(data, AppJsonContext.Default.AppPreferencesData));
                File.Move(temporaryPath, filePath, overwrite: true);
                temporaryPath = null;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                if (temporaryPath is not null)
                {
                    try { File.Delete(temporaryPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    internal static AppPreferencesData LoadFromPathForTesting(string filePath) => Load(filePath);

    internal static IDisposable UseInMemoryForTesting(AppPreferencesData? data = null)
    {
        lock (InitLock)
        {
            var previous = _inMemoryTestData;
            Volatile.Write(ref _inMemoryTestData, data ?? new AppPreferencesData());
            return new InMemoryTestScope(previous);
        }
    }

    private sealed class InMemoryTestScope(AppPreferencesData? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            lock (InitLock)
            {
                if (_disposed)
                    return;
                Volatile.Write(ref _inMemoryTestData, previous);
                _disposed = true;
            }
        }
    }
}

public sealed class AppPreferencesData
{
    /// <summary>主题:0=跟随系统,1=浅色,2=深色。与设置页 ComboBox 索引一致。</summary>
    public int ThemeIndex { get; set; } = AppPreferenceIndexPolicy.DefaultThemeIndex;

    /// <summary>是否显示网站 favicon。默认开。</summary>
    public bool ShowWebsiteIcons { get; set; } = true;

    /// <summary>本地保存的命名筛选视图(不同步服务端)。</summary>
    public List<SavedSearchViewData> SavedSearchViews { get; set; } = new();

    /// <summary>Session timeout ComboBox index. The default is 3 (15 minutes).</summary>
    public int SessionTimeoutIndex { get; set; } = AppPreferenceIndexPolicy.DefaultSessionTimeoutIndex;

    /// <summary>超时动作:0=锁定(默认),1=登出。</summary>
    public int TimeoutActionIndex { get; set; } = AppPreferenceIndexPolicy.DefaultTimeoutActionIndex;

    /// <summary>清空剪贴板 ComboBox 索引(0永不..6=5分)。默认 3=30秒。</summary>
    public int ClearClipboardIndex { get; set; } = AppPreferenceIndexPolicy.DefaultClearClipboardIndex;

    /// <summary>是否显示系统托盘图标。默认开。</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>最小化时隐藏到托盘（仅当 ShowTrayIcon 为真才生效）。默认关。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>关闭窗口时隐藏到托盘而非退出（仅当 ShowTrayIcon 为真才生效）。默认开。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>允许捕获本应用窗口内容（截图/录屏/屏幕共享）。默认允许；关闭后对窗口设 WDA_EXCLUDEFROMCAPTURE 阻止捕获。</summary>
    public bool AllowScreenshots { get; set; } = true;

    /// <summary>允许浏览器扩展经命名管道桥接连接本应用（passkey）。默认关闭；仅开启时才启动桥接服务器。</summary>
    public bool AllowBrowserIntegration { get; set; }
}
