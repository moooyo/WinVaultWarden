using App.Services;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.ViewModels;

/// <summary>
/// 设置页 ViewModel 的纯 C# 部分（无 WinUI 依赖）。
/// WinUI 相关属性（主题切换回调、AboutInfo、ThemeManager）在 SettingsViewModel.WinUI.cs 中定义。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAccountUiService? _accountUi;
    private readonly ITwoFactorUiService? _twoFactorUi;
    private readonly IPinService? _pin;
    private readonly IWindowsHelloAuthenticator? _hello;
    private readonly IHelloUnlockService? _helloUnlock;
    private readonly IAppLifecycleCoordinator? _lifecycle;

    // 是否支持浏览器集成：仅便携版(unpackaged)为 true；MSIX 打包时 DI 传 false。
    // 默认 true 使无参构造器（设计时 / 无注入测试）保持便携版语义。
    private bool _browserIntegrationSupported = true;

    /// <summary>当前运行环境是否支持浏览器集成（仅便携版为 true；MSIX packaged 为 false）。</summary>
    public bool BrowserIntegrationSupported => _browserIntegrationSupported;

    [ObservableProperty]
    public partial int SelectedSessionTimeoutIndex { get; set; } = AppPreferences.Current.SessionTimeoutIndex;

    [ObservableProperty]
    public partial int SelectedTimeoutActionIndex { get; set; } = AppPreferences.Current.TimeoutActionIndex;

    [ObservableProperty]
    public partial bool UsePinUnlock { get; set; }

    [ObservableProperty]
    public partial int SelectedClearClipboardIndex { get; set; } = AppPreferences.Current.ClearClipboardIndex;

    [ObservableProperty]
    public partial bool MinimizeOnCopy { get; set; }

    [ObservableProperty]
    public partial bool ShowTrayIcon { get; set; } = AppPreferences.Current.ShowTrayIcon;

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; } = AppPreferences.Current.MinimizeToTray;

    [ObservableProperty]
    public partial bool CloseToTray { get; set; } = AppPreferences.Current.CloseToTray;

    [ObservableProperty]
    public partial bool StartOnLogin { get; set; }

    [ObservableProperty]
    public partial bool AllowBrowserIntegration { get; set; } = AppPreferences.Current.AllowBrowserIntegration;

    [ObservableProperty]
    public partial bool UseHardwareAcceleration { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableSshAgent { get; set; } = true;

    [ObservableProperty]
    public partial int SelectedSshAuthorizationPromptIndex { get; set; }

    [ObservableProperty]
    public partial bool AllowScreenshots { get; set; } = AppPreferences.Current.AllowScreenshots;

    [ObservableProperty]
    public partial int SelectedLanguageIndex { get; set; }

    // ── 账户操作状态 ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationError))]
    public partial string OperationError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasOperationError => !string.IsNullOrEmpty(OperationError);

    partial void OnSelectedSessionTimeoutIndexChanged(int value)
    {
        AppPreferences.Current.SessionTimeoutIndex = value;
        AppPreferences.Save();
    }

    partial void OnSelectedTimeoutActionIndexChanged(int value)
    {
        AppPreferences.Current.TimeoutActionIndex = value;
        AppPreferences.Save();
    }

    partial void OnSelectedClearClipboardIndexChanged(int value)
    {
        AppPreferences.Current.ClearClipboardIndex = value;
        AppPreferences.Save();
    }

    /// <summary>ShowTrayIcon 变更时通知 UI（MainWindow）即时增删托盘图标。
    /// 纯 BCL 事件，不引入 WinUI，故 SettingsViewModel 仍可编入无 WinUI 的 App.Tests。</summary>
    public static event Action<bool>? ShowTrayIconSettingChanged;

    partial void OnShowTrayIconChanged(bool value)
    {
        AppPreferences.Current.ShowTrayIcon = value;
        AppPreferences.Save();
        ShowTrayIconSettingChanged?.Invoke(value);
    }

    /// <summary>AllowScreenshots 变更时通知 UI（MainWindow）即时对窗口应用/解除捕获保护。</summary>
    public static event Action<bool>? AllowScreenshotsSettingChanged;

    partial void OnAllowScreenshotsChanged(bool value)
    {
        AppPreferences.Current.AllowScreenshots = value;
        AppPreferences.Save();
        AllowScreenshotsSettingChanged?.Invoke(value);
    }

    /// <summary>AllowBrowserIntegration 变更时通知组合根（App）即时启动/停止 passkey 桥接服务器。</summary>
    public static event Action<bool>? AllowBrowserIntegrationSettingChanged;

    partial void OnAllowBrowserIntegrationChanged(bool value)
    {
        if (!_browserIntegrationSupported)
        {
            // Fail-closed：MSIX packaged 时绝不持久化 true、绝不触发 start 事件。
            // 若外部代码强制设为 true，立即回滚为 false（toggle 无法在 UI 上开启，此为防御层）。
            if (value) AllowBrowserIntegration = false;
            return;
        }
        AppPreferences.Current.AllowBrowserIntegration = value;
        AppPreferences.Save();
        AllowBrowserIntegrationSettingChanged?.Invoke(value);
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        AppPreferences.Current.MinimizeToTray = value;
        AppPreferences.Save();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        AppPreferences.Current.CloseToTray = value;
        AppPreferences.Save();
    }

    // ─────────────────────────────────────────────────────────────────────────

    // 无参构造器——供 XAML 设计时和 App.Tests 无注入场景使用
    public SettingsViewModel()
    {
    }

    // 供 App.Tests 直接测试浏览器集成门控行为，无需其他协作者
    public SettingsViewModel(bool browserIntegrationSupported)
    {
        ApplyBrowserIntegrationGate(browserIntegrationSupported);
    }

    public SettingsViewModel(IAccountUiService accountUi, IAppLifecycleCoordinator? lifecycle = null)
    {
        _accountUi = accountUi;
        _lifecycle = lifecycle;
    }

    public SettingsViewModel(ITwoFactorUiService twoFactorUi) => _twoFactorUi = twoFactorUi;

    public SettingsViewModel(IPinService pin) => _pin = pin;

    // 供 Windows Hello 相关测试直接注入两个协作者，无需 accountUi/twoFactorUi/pin 假件。
    public SettingsViewModel(IWindowsHelloAuthenticator hello, IHelloUnlockService helloUnlock)
    {
        _hello = hello;
        _helloUnlock = helloUnlock;
    }

    public SettingsViewModel(
        IAccountUiService accountUi,
        ITwoFactorUiService twoFactorUi,
        IPinService? pin = null,
        IWindowsHelloAuthenticator? hello = null,
        IHelloUnlockService? helloUnlock = null,
        IAppLifecycleCoordinator? lifecycle = null,
        bool browserIntegrationSupported = true)
    {
        _accountUi = accountUi;
        _twoFactorUi = twoFactorUi;
        _pin = pin;
        _hello = hello;
        _helloUnlock = helloUnlock;
        _lifecycle = lifecycle;
        ApplyBrowserIntegrationGate(browserIntegrationSupported);
    }

    // 统一入口：设置 _browserIntegrationSupported 并 fail-closed 修正已有偏好。
    private void ApplyBrowserIntegrationGate(bool supported)
    {
        _browserIntegrationSupported = supported;
        if (!supported && AllowBrowserIntegration)
        {
            // 若 MSIX 启动时偏好已存 true，立即归 false 并持久化修正（fail-closed）。
            AppPreferences.Current.AllowBrowserIntegration = false;
            AppPreferences.Save();
            AllowBrowserIntegration = false; // 触发 handler(false) → !supported → return early，不再发事件
        }
    }

    public string AccountEmail => _accountUi?.GetAccount().Email ?? string.Empty;
    public string AccountServer => _accountUi?.GetAccount().ServerUrl ?? string.Empty;
    public string AccountInitial => _accountUi?.GetAccount().Initial ?? string.Empty;
    public string KdfSummary => _accountUi?.GetAccount().KdfSummary ?? string.Empty;

    // ── PIN 解锁 ─────────────────────────────────────────────────────────────

    public bool IsPinSet => _pin?.IsPinSet ?? false;
    public void SetPin(string pin) => _pin?.SetPin(pin);
    public void ClearPin() => _pin?.ClearPin();

    // ── Windows Hello 解锁 ───────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool IsWindowsHelloAvailable { get; set; }

    public bool IsWindowsHelloEnabled => _helloUnlock?.IsHelloSet ?? false;

    public async Task RefreshWindowsHelloAvailabilityAsync()
        => IsWindowsHelloAvailable = _hello is not null && await _hello.IsAvailableAsync();

    // 启用:生成 32 字节随机挑战 → Hello 创建密钥并签名 → SetHello。取消/失败返回 false(开关回弹)。
    public async Task<bool> EnableWindowsHelloAsync()
    {
        if (_hello is null || _helloUnlock is null)
            return false;

        var challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var signature = await _hello.CreateProtectorAsync(challenge);
        if (signature is null)
            return false;

        _helloUnlock.SetHello(signature, challenge);
        OnPropertyChanged(nameof(IsWindowsHelloEnabled));
        return true;
    }

    public void DisableWindowsHello()
    {
        _helloUnlock?.ClearHello();
        OnPropertyChanged(nameof(IsWindowsHelloEnabled));
    }

    // ── 账户操作 ─────────────────────────────────────────────────────────────

    // 直接可 await 的方法（供 code-behind 调用）；RelayCommand 包装供 XAML 绑定。

    public async Task ChangePasswordAsync(string current, string next, string confirm, string? hint, CancellationToken ct = default)
    {
        if (_accountUi is null)
        {
            OperationError = "账户服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _accountUi.ChangePasswordAsync(current, next, confirm, hint, ct);
            // 改密成功后由 App 层统一登出:发 CredentialChanged intent,协调器清理并回登录页。
            if (_lifecycle is not null)
                await _lifecycle.RequestAsync(AppLifecycleIntent.CredentialChanged);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (AccountOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RenameAsync(string name, CancellationToken ct = default)
    {
        if (_accountUi is null)
        {
            OperationError = "账户服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _accountUi.RenameAsync(name, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (AccountOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ChangeIterationsAsync(string current, int iterations, CancellationToken ct = default)
    {
        if (_accountUi is null)
        {
            OperationError = "账户服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _accountUi.ChangeIterationsAsync(current, iterations, ct);
            // 改 KDF 成功后由 App 层统一登出:发 CredentialChanged intent,协调器清理并回登录页。
            if (_lifecycle is not null)
                await _lifecycle.RequestAsync(AppLifecycleIntent.CredentialChanged);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (AccountOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // RenameCommand 供 XAML 绑定（string 参数可正常传递）
    // ChangePassword/ChangeIterations 的 tuple 版本不能被 XAML CommandParameter 绑定，
    // 由 code-behind 直接 await 公开的 async 方法即可。
    [RelayCommand]
    private async Task Rename(string name) =>
        await RenameAsync(name);

    // ── 两步验证操作 ──────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial DataPageState TwoFactorState { get; set; } = DataPageState.Loading;

    public async Task<UiOperationResult<IReadOnlyList<TwoFactorProvider>>> LoadProvidersAsync(CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            TwoFactorState = DataPageState.Error;
            return UiOperationResult<IReadOnlyList<TwoFactorProvider>>.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult<IReadOnlyList<TwoFactorProvider>>.Busy();

        IsBusy = true;
        TwoFactorState = DataPageState.Loading;
        OperationError = string.Empty;
        try
        {
            var providers = await _twoFactorUi.ListProvidersAsync(ct);
            TwoFactorState = providers.Count == 0
                ? DataPageState.Empty
                : DataPageState.Content;
            return UiOperationResult<IReadOnlyList<TwoFactorProvider>>.Success(providers);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return UiOperationResult<IReadOnlyList<TwoFactorProvider>>.Cancelled();
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            TwoFactorState = DataPageState.Error;
            return UiOperationResult<IReadOnlyList<TwoFactorProvider>>.Failure(ex.Message);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            TwoFactorState = DataPageState.Error;
            return UiOperationResult<IReadOnlyList<TwoFactorProvider>>.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<(string secret, string otpauth)> BeginTotpSetupAsync(string pw, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return (string.Empty, string.Empty);
        }
        if (IsBusy)
            return (string.Empty, string.Empty);

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            return await _twoFactorUi.BeginTotpSetupAsync(pw, ct);
        }
        catch (OperationCanceledException)
        {
            return (string.Empty, string.Empty);
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return (string.Empty, string.Empty);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return (string.Empty, string.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return string.Empty;
        }
        if (IsBusy)
            return string.Empty;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            return await _twoFactorUi.EnableTotpAsync(pw, secret, code, ct);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return string.Empty;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SendEmailAsync(string pw, string email, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _twoFactorUi.SendEmailAsync(pw, email, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _twoFactorUi.EnableEmailAsync(pw, email, token, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisableTwoFactorAsync(string pw, int type, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _twoFactorUi.DisableAsync(pw, type, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
