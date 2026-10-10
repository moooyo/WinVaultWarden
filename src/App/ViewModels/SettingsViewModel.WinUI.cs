using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace App.ViewModels;

/// <summary>
/// Windows-specific settings bindings for theme, diagnostics, and verification.
/// </summary>
public partial class SettingsViewModel
{
    private int _selectedThemeIndex = LoadThemeIndex();

    public int SelectedThemeIndex
    {
        get => _selectedThemeIndex;
        set
        {
            if (AppPreferenceIndexPolicy.IsTransientNoSelection(value))
            {
                OnPropertyChanged(nameof(SelectedThemeIndex));
                return;
            }
            var normalized = AppPreferenceIndexPolicy.NormalizeThemeIndex(value);
            var changed = SetProperty(ref _selectedThemeIndex, normalized);
            SaveThemeIndex(normalized);
            if (changed)
                ThemeManager.Apply(normalized);
        }
    }

    private static int LoadThemeIndex()
    {
        var normalized = AppPreferenceIndexPolicy.NormalizeThemeIndex(
            AppPreferences.Current.ThemeIndex);
        SaveThemeIndex(normalized);
        return normalized;
    }

    private static void SaveThemeIndex(int value)
    {
        if (AppPreferences.Current.ThemeIndex == value)
            return;
        AppPreferences.Current.ThemeIndex = value;
        AppPreferences.Save();
    }

    [ObservableProperty]
    public partial bool ShowWebsiteIcons { get; set; } = AppPreferences.Current.ShowWebsiteIcons;

    partial void OnShowWebsiteIconsChanged(bool value)
    {
        AppPreferences.Current.ShowWebsiteIcons = value;
        AppPreferences.Save();
    }

    private int _selectedSensitiveActionVerificationIndex =
        AppPreferenceIndexPolicy.NormalizeSensitiveActionVerificationIndex(
            AppPreferences.Current.SensitiveActionVerificationIndex);

    public int SelectedSensitiveActionVerificationIndex
    {
        get => _selectedSensitiveActionVerificationIndex;
        set
        {
            if (AppPreferenceIndexPolicy.IsTransientNoSelection(value))
                return;
            var normalized = AppPreferenceIndexPolicy.NormalizeSensitiveActionVerificationIndex(value);
            if (!SetProperty(ref _selectedSensitiveActionVerificationIndex, normalized))
                return;
            AppPreferences.Current.SensitiveActionVerificationIndex = normalized;
            AppPreferences.Save();
            global::App.App.Services.GetRequiredService<ISensitiveActionVerificationService>().EndGracePeriod();
        }
    }

    [ObservableProperty]
    public partial bool ExcludeClipboardHistory { get; set; } = AppPreferences.Current.ExcludeClipboardHistory;

    partial void OnExcludeClipboardHistoryChanged(bool value)
    {
        AppPreferences.Current.ExcludeClipboardHistory = value;
        AppPreferences.Save();
    }

    // Display values come from the installed application and its actual runtime.
    public string AppVersion => AboutInfo.AppVersion;
    public string WindowsVersion => AboutInfo.WindowsVersion;
    public string DotNetVersion => AboutInfo.DotNetVersion;
    public string AppArchitecture => AboutInfo.Architecture;
    public string VersionSummary => $"版本 {AppVersion} · {AppArchitecture}";
    public string RuntimeSummary
    {
        get
        {
            var sdk = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .OfType<System.Reflection.AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "WindowsAppSDKVersion")?.Value;
            return $"{WindowsVersion} · {DotNetVersion}"
                + (string.IsNullOrWhiteSpace(sdk) ? string.Empty : $" · Windows App SDK {sdk}");
        }
    }

    public string KdfDescription
    {
        get
        {
            if (!TryReadCurrentKdf(out var type, out var iterations, out var memory, out var parallelism))
                return string.IsNullOrWhiteSpace(KdfSummary) ? "当前加密参数不可用" : KdfSummary;
            return type == Core.Enums.KdfType.Argon2id
                ? $"Argon2id · {iterations} 次迭代 · {memory} MiB · 并行度 {parallelism}"
                : $"PBKDF2-SHA256 · {iterations.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} 次迭代";
        }
    }

    public bool TryReadCurrentKdf(out Core.Enums.KdfType type, out int iterations, out int? memory, out int? parallelism)
    {
        type = default;
        iterations = 0;
        memory = null;
        parallelism = null;
        var account = _accountUi?.GetAccount();
        var store = global::App.App.Services.GetRequiredService<Core.Abstractions.ITokenStore>();
        if (account is null || account.Endpoints is null
            || !store.TryLoad(account.Id, account.Endpoints, out var session))
            return false;
        type = session.KdfType;
        iterations = session.KdfIterations;
        memory = session.KdfMemory;
        parallelism = session.KdfParallelism;
        return true;
    }
}
