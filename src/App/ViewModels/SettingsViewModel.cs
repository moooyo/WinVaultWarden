using Api;
using App.Services;
using App.ViewModels.Models;
using Core.Abstractions;
using Core.Enums;
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
    private readonly IVaultService? _vault;
    private readonly IDeviceTrustService? _deviceTrust;
    private readonly IWebAuthnTwoFactorRegistrationService? _webAuthnRegistration;
    private readonly IAccountDeletionService? _accountDeletion;

    // 是否支持浏览器集成：仅便携版(unpackaged)为 true；MSIX 打包时 DI 传 false。
    // 默认 true 使无参构造器（设计时 / 无注入测试）保持便携版语义。
    private bool _browserIntegrationSupported = true;

    /// <summary>当前运行环境是否支持浏览器集成（仅便携版为 true；MSIX packaged 为 false）。</summary>
    public bool BrowserIntegrationSupported => _browserIntegrationSupported;

    private int _selectedSectionIndex;

    public int SelectedSectionIndex
    {
        get => _selectedSectionIndex;
        set
        {
            if (value is < 0 or > 3)
            {
                OnPropertyChanged(nameof(SelectedSectionIndex));
                return;
            }
            if (!SetProperty(ref _selectedSectionIndex, value))
                return;

            OnPropertyChanged(nameof(IsSecuritySection));
            OnPropertyChanged(nameof(IsAccountSection));
            OnPropertyChanged(nameof(IsPreferencesSection));
            OnPropertyChanged(nameof(IsAboutSection));
        }
    }

    public bool IsSecuritySection => SelectedSectionIndex == 0;
    public bool IsAccountSection => SelectedSectionIndex == 1;
    public bool IsPreferencesSection => SelectedSectionIndex == 2;
    public bool IsAboutSection => SelectedSectionIndex == 3;

    public string BrowserIntegrationDescription => BrowserIntegrationSupported
        ? "允许已配对的浏览器扩展请求填充，保险库需保持解锁。"
        : "此安装方式暂不支持浏览器集成；仅便携版可用。";

    public string TrayBehaviorDescription => CanConfigureTrayBehavior
        ? "保留系统托盘入口，可从托盘重新打开窗口。"
        : "先启用显示托盘图标，再配置此行为。";

    private int _selectedSessionTimeoutIndex = LoadSessionTimeoutIndex();

    public int SelectedSessionTimeoutIndex
    {
        get => _selectedSessionTimeoutIndex;
        set
        {
            if (AppPreferenceIndexPolicy.IsTransientNoSelection(value))
            {
                OnPropertyChanged(nameof(SelectedSessionTimeoutIndex));
                return;
            }

            if (_vault is not null
                && value == IdleTimeoutPolicy.PolicySpecificMinutesIndex
                && IdleTimeoutPolicy.IsSessionTimeoutIndexAllowed(_vault.Snapshot, value))
            {
                ApplyTimeoutPolicyRestrictions();
                return;
            }

            var normalized = AppPreferenceIndexPolicy.NormalizeSessionTimeoutIndex(value);
            if (_vault is not null
                && !IdleTimeoutPolicy.IsSessionTimeoutIndexAllowed(_vault.Snapshot, normalized))
            {
                ApplyTimeoutPolicyRestrictions();
                return;
            }

            SetProperty(ref _selectedSessionTimeoutIndex, normalized);
            SaveSessionTimeoutIndex(normalized);
        }
    }

    private int _selectedTimeoutActionIndex = LoadTimeoutActionIndex();

    public int SelectedTimeoutActionIndex
    {
        get => _selectedTimeoutActionIndex;
        set
        {
            if (AppPreferenceIndexPolicy.IsTransientNoSelection(value))
            {
                OnPropertyChanged(nameof(SelectedTimeoutActionIndex));
                return;
            }
            var normalized = AppPreferenceIndexPolicy.NormalizeTimeoutActionIndex(value);
            if (_vault is not null && !CanSelectTimeoutAction)
            {
                ApplyTimeoutPolicyRestrictions();
                return;
            }

            SetProperty(ref _selectedTimeoutActionIndex, normalized);
            SaveTimeoutActionIndex(normalized);
        }
    }

    public bool CanSelectRestartTimeout => IsTimeoutOptionAllowed(IdleTimeoutPolicy.RestartIndex);
    public bool CanSelectOneMinuteTimeout => IsTimeoutOptionAllowed(1);
    public bool CanSelectFiveMinuteTimeout => IsTimeoutOptionAllowed(2);
    public bool CanSelectFifteenMinuteTimeout => IsTimeoutOptionAllowed(3);
    public bool CanSelectThirtyMinuteTimeout => IsTimeoutOptionAllowed(4);
    public bool CanSelectOneHourTimeout => IsTimeoutOptionAllowed(5);
    public bool CanSelectFourHourTimeout => IsTimeoutOptionAllowed(6);
    public bool CanSelectNeverTimeout => IsTimeoutOptionAllowed(IdleTimeoutPolicy.NeverIndex);
    public bool CanSelectSystemLockTimeout => IsTimeoutOptionAllowed(IdleTimeoutPolicy.OnSystemLockIndex);

    public bool HasPolicySpecificTimeoutOption => _vault is not null
        && IdleTimeoutPolicy.HasPolicySpecificMinutes(_vault.Snapshot);

    public string PolicySpecificTimeoutOptionText
    {
        get
        {
            var constraint = CurrentTimeoutConstraint();
            return constraint.Mode switch
            {
                MaximumVaultTimeoutMode.Immediately => "立即",
                MaximumVaultTimeoutMode.Minutes => FormatTimeout(constraint.Minutes),
                _ => string.Empty,
            };
        }
    }

    public bool CanSelectTimeoutAction =>
        CurrentTimeoutConstraint().RequiredAction is null;

    public bool HasMaximumVaultTimeoutPolicy
    {
        get
        {
            var constraint = CurrentTimeoutConstraint();
            return constraint.Applies
                && (constraint.Mode != MaximumVaultTimeoutMode.Never
                    || constraint.RequiredAction is not null);
        }
    }

    public string MaximumVaultTimeoutPolicyHint
    {
        get
        {
            var constraint = CurrentTimeoutConstraint();
            if (!constraint.Applies)
                return string.Empty;

            var timeout = constraint.Mode switch
            {
                MaximumVaultTimeoutMode.Immediately =>
                    "组织策略要求密码库立即超时。",
                MaximumVaultTimeoutMode.Minutes when constraint.UsedFailClosedFallback =>
                    $"组织策略数据无效；当前安全上限为 {FormatTimeout(constraint.Minutes)}。",
                MaximumVaultTimeoutMode.Minutes =>
                    $"组织策略将密码库超时上限设为 {FormatTimeout(constraint.Minutes)}。",
                MaximumVaultTimeoutMode.OnSystemLock => "组织策略要求在系统锁定时锁定密码库。",
                MaximumVaultTimeoutMode.OnAppRestart => "组织策略要求应用重启后锁定密码库。",
                _ => string.Empty,
            };
            var action = constraint.RequiredAction switch
            {
                MaximumVaultTimeoutAction.Lock => " 超时动作由策略设为锁定。",
                MaximumVaultTimeoutAction.Logout => " 超时动作由策略设为登出。",
                _ => string.Empty,
            };
            return (timeout + action).Trim();
        }
    }

    [ObservableProperty]
    public partial bool UsePinUnlock { get; set; }

    private int _selectedClearClipboardIndex = LoadClearClipboardIndex();

    public int SelectedClearClipboardIndex
    {
        get => _selectedClearClipboardIndex;
        set
        {
            if (AppPreferenceIndexPolicy.IsTransientNoSelection(value))
            {
                OnPropertyChanged(nameof(SelectedClearClipboardIndex));
                return;
            }
            var normalized = AppPreferenceIndexPolicy.NormalizeClearClipboardIndex(value);
            SetProperty(ref _selectedClearClipboardIndex, normalized);
            SaveClearClipboardIndex(normalized);
        }
    }

    [ObservableProperty]
    public partial bool MinimizeOnCopy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigureTrayBehavior))]
    [NotifyPropertyChangedFor(nameof(TrayBehaviorDescription))]
    public partial bool ShowTrayIcon { get; set; } = AppPreferences.Current.ShowTrayIcon;

    public bool CanConfigureTrayBehavior => ShowTrayIcon;

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

    private static int LoadSessionTimeoutIndex()
    {
        var normalized = AppPreferenceIndexPolicy.NormalizeSessionTimeoutIndex(
            AppPreferences.Current.SessionTimeoutIndex);
        SaveSessionTimeoutIndex(normalized);
        return normalized;
    }

    private static void SaveSessionTimeoutIndex(int value)
    {
        if (AppPreferences.Current.SessionTimeoutIndex == value)
            return;
        AppPreferences.Current.SessionTimeoutIndex = value;
        AppPreferences.Save();
    }

    private static int LoadTimeoutActionIndex()
    {
        var normalized = AppPreferenceIndexPolicy.NormalizeTimeoutActionIndex(
            AppPreferences.Current.TimeoutActionIndex);
        SaveTimeoutActionIndex(normalized);
        return normalized;
    }

    private static void SaveTimeoutActionIndex(int value)
    {
        if (AppPreferences.Current.TimeoutActionIndex == value)
            return;
        AppPreferences.Current.TimeoutActionIndex = value;
        AppPreferences.Save();
    }

    private static int LoadClearClipboardIndex()
    {
        var normalized = AppPreferenceIndexPolicy.NormalizeClearClipboardIndex(
            AppPreferences.Current.ClearClipboardIndex);
        SaveClearClipboardIndex(normalized);
        return normalized;
    }

    private static void SaveClearClipboardIndex(int value)
    {
        if (AppPreferences.Current.ClearClipboardIndex == value)
            return;
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

    public SettingsViewModel(
        IAccountDeletionService accountDeletion,
        IAppLifecycleCoordinator? lifecycle = null,
        IVaultService? vault = null)
    {
        _accountDeletion = accountDeletion;
        _lifecycle = lifecycle;
        _vault = vault;
    }

    public SettingsViewModel(
        ITwoFactorUiService twoFactorUi,
        IWebAuthnTwoFactorRegistrationService? webAuthnRegistration = null)
    {
        _twoFactorUi = twoFactorUi;
        _webAuthnRegistration = webAuthnRegistration;
    }

    public SettingsViewModel(IPinService pin) => _pin = pin;

    internal SettingsViewModel(IVaultService vault) => _vault = vault;

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
        bool browserIntegrationSupported = true,
        IVaultService? vault = null,
        IDeviceTrustService? deviceTrust = null,
        IWebAuthnTwoFactorRegistrationService? webAuthnRegistration = null,
        IAccountDeletionService? accountDeletion = null)
    {
        _accountUi = accountUi;
        _twoFactorUi = twoFactorUi;
        _pin = pin;
        _hello = hello;
        _helloUnlock = helloUnlock;
        _lifecycle = lifecycle;
        _vault = vault;
        _deviceTrust = deviceTrust;
        _webAuthnRegistration = webAuthnRegistration;
        _accountDeletion = accountDeletion;
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

    public bool CanRegisterWebAuthn => _webAuthnRegistration?.IsSupported == true;

    public static TwoFactorUserVerification CreateWebAuthnVerification(
        WebAuthnVerificationMethod method,
        string value) => method switch
    {
        WebAuthnVerificationMethod.MasterPassword =>
            TwoFactorUserVerification.FromMasterPassword(value),
        WebAuthnVerificationMethod.EmailOtp =>
            TwoFactorUserVerification.FromOtp(value),
        WebAuthnVerificationMethod.AuthRequestAccessCode =>
            TwoFactorUserVerification.FromAuthRequestAccessCode(value),
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    public static string GetTwoFactorProviderDisplayName(int type) =>
        (TwoFactorProviderType)type switch
        {
            TwoFactorProviderType.Authenticator => "TOTP 验证器",
            TwoFactorProviderType.Email => "邮箱验证码",
            TwoFactorProviderType.Duo => "Duo",
            TwoFactorProviderType.YubiKey => "YubiKey OTP",
            TwoFactorProviderType.U2f => "U2F 安全密钥",
            TwoFactorProviderType.Remember => "已记住的设备",
            TwoFactorProviderType.OrganizationDuo => "组织 Duo",
            TwoFactorProviderType.WebAuthn => "安全密钥 / Windows Hello",
            TwoFactorProviderType.RecoveryCode => "恢复代码",
            _ => $"未知（类型 {type}）",
        };

    // ── PIN 解锁 ─────────────────────────────────────────────────────────────

    public bool IsPinSet => _pin?.IsPinSet ?? false;
    public bool CanUsePinUnlock => _vault is null || !VaultPolicyEvaluator.IsEnabledForUser(
        _vault.Snapshot,
        Core.Enums.OrganizationPolicyType.RemoveUnlockWithPin);

    public string PinUnlockDescription => !CanUsePinUnlock
        ? "组织策略不允许使用 PIN 解锁。"
        : "使用独立 PIN 解锁此设备上已保存的保险库。";

    public void ApplyPolicyRestrictions()
    {
        ApplyTimeoutPolicyRestrictions();
        if (!CanUsePinUnlock && IsPinSet)
            ClearPin();
        OnPropertyChanged(nameof(CanUsePinUnlock));
        OnPropertyChanged(nameof(PinUnlockDescription));
    }

    private void ApplyTimeoutPolicyRestrictions()
    {
        if (_vault is null)
            return;

        var timeoutIndex = IdleTimeoutPolicy.NormalizeSessionTimeoutIndexForPolicy(
            _vault.Snapshot,
            AppPreferences.Current.SessionTimeoutIndex);
        SetProperty(ref _selectedSessionTimeoutIndex, timeoutIndex, nameof(SelectedSessionTimeoutIndex));

        var actionIndex = IdleTimeoutPolicy.NormalizeTimeoutActionIndexForPolicy(
            _vault.Snapshot,
            AppPreferences.Current.TimeoutActionIndex);
        SetProperty(ref _selectedTimeoutActionIndex, actionIndex, nameof(SelectedTimeoutActionIndex));

        NotifyTimeoutPolicyProperties();
    }

    private bool IsTimeoutOptionAllowed(int index) => _vault is null
        || IdleTimeoutPolicy.IsSessionTimeoutIndexAllowed(_vault.Snapshot, index);

    private MaximumVaultTimeoutConstraint CurrentTimeoutConstraint() => _vault is null
        ? MaximumVaultTimeoutConstraint.None
        : MaximumVaultTimeoutPolicyResolver.Resolve(_vault.Snapshot);

    private void NotifyTimeoutPolicyProperties()
    {
        OnPropertyChanged(nameof(CanSelectRestartTimeout));
        OnPropertyChanged(nameof(CanSelectOneMinuteTimeout));
        OnPropertyChanged(nameof(CanSelectFiveMinuteTimeout));
        OnPropertyChanged(nameof(CanSelectFifteenMinuteTimeout));
        OnPropertyChanged(nameof(CanSelectThirtyMinuteTimeout));
        OnPropertyChanged(nameof(CanSelectOneHourTimeout));
        OnPropertyChanged(nameof(CanSelectFourHourTimeout));
        OnPropertyChanged(nameof(CanSelectNeverTimeout));
        OnPropertyChanged(nameof(CanSelectSystemLockTimeout));
        OnPropertyChanged(nameof(HasPolicySpecificTimeoutOption));
        OnPropertyChanged(nameof(PolicySpecificTimeoutOptionText));
        OnPropertyChanged(nameof(CanSelectTimeoutAction));
        OnPropertyChanged(nameof(HasMaximumVaultTimeoutPolicy));
        OnPropertyChanged(nameof(MaximumVaultTimeoutPolicyHint));
    }

    private static string FormatTimeout(int minutes)
    {
        if (minutes < 60)
            return $"{minutes} 分钟";

        var hours = minutes / 60;
        var remainingMinutes = minutes % 60;
        return remainingMinutes == 0
            ? $"{hours} 小时"
            : $"{hours} 小时 {remainingMinutes} 分钟";
    }

    public void SetPin(string pin) => _pin?.SetPin(pin);
    public void ClearPin() => _pin?.ClearPin();

    // ── Windows Hello 解锁 ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleWindowsHello))]
    [NotifyPropertyChangedFor(nameof(WindowsHelloDescription))]
    public partial bool IsWindowsHelloAvailable { get; set; }

    public bool IsWindowsHelloEnabled => _helloUnlock?.IsHelloSet ?? false;
    public bool CanToggleWindowsHello => IsWindowsHelloAvailable || IsWindowsHelloEnabled;

    public string WindowsHelloDescription => IsWindowsHelloAvailable
        ? "使用 Windows Hello 验证身份并解锁此设备上的保险库。"
        : IsWindowsHelloEnabled
            ? "Windows Hello 当前不可用，仍可关闭已保存的解锁设置。"
            : "请先在 Windows 设置中配置 Windows Hello。";

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
        OnPropertyChanged(nameof(CanToggleWindowsHello));
        OnPropertyChanged(nameof(WindowsHelloDescription));
        return true;
    }

    public void DisableWindowsHello()
    {
        _helloUnlock?.ClearHello();
        OnPropertyChanged(nameof(IsWindowsHelloEnabled));
        OnPropertyChanged(nameof(CanToggleWindowsHello));
        OnPropertyChanged(nameof(WindowsHelloDescription));
    }

    public bool IsCurrentDeviceTrusted => _deviceTrust?.IsCurrentDeviceTrusted ?? false;

    public bool CanTrustCurrentDevice =>
        !IsBusy && (_deviceTrust?.CanTrustCurrentDevice ?? false);

    public bool ShowTrustedDeviceSettings =>
        IsCurrentDeviceTrusted || (_deviceTrust?.CanTrustCurrentDevice ?? false);

    public string TrustedDeviceStatus => IsCurrentDeviceTrusted
        ? "此设备已受信任；锁定后将重新通过企业 SSO 验证信任状态。"
        : "允许此设备在企业 SSO 后解密保险库。";

    public string TrustedDeviceActionText => IsBusy
        ? "正在建立信任"
        : IsCurrentDeviceTrusted
            ? "已信任"
            : "信任此设备";

    public async Task<UiOperationResult> TrustCurrentDeviceAsync(
        CancellationToken ct = default)
    {
        if (IsBusy)
            return UiOperationResult.Busy();
        if (_deviceTrust is null || !CanTrustCurrentDevice)
            return UiOperationResult.Failure("当前会话不支持可信设备加密");

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _deviceTrust.TrustCurrentDeviceAsync(ct);
            NotifyDeviceTrustProperties();
            return UiOperationResult.Success();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult.Failure(OperationError);
        }
        catch (VaultApiException)
        {
            OperationError = "服务器拒绝了可信设备请求";
            return UiOperationResult.Failure(OperationError);
        }
        catch (HttpRequestException)
        {
            OperationError = "无法连接服务器，请稍后重试";
            return UiOperationResult.Failure(OperationError);
        }
        catch (DeviceTrustException ex)
        {
            OperationError = ex.Message;
            return UiOperationResult.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyDeviceTrustProperties()
    {
        OnPropertyChanged(nameof(IsCurrentDeviceTrusted));
        OnPropertyChanged(nameof(CanTrustCurrentDevice));
        OnPropertyChanged(nameof(ShowTrustedDeviceSettings));
        OnPropertyChanged(nameof(TrustedDeviceStatus));
        OnPropertyChanged(nameof(TrustedDeviceActionText));
    }

    partial void OnIsBusyChanged(bool value) => NotifyDeviceTrustProperties();

    // ── 账户操作 ─────────────────────────────────────────────────────────────

    // 直接可 await 的方法（供 code-behind 调用）；RelayCommand 包装供 XAML 绑定。

    public async Task<UiOperationResult> ChangePasswordAsync(string current, string next, string confirm, string? hint, CancellationToken ct = default)
    {
        if (_accountUi is null)
        {
            OperationError = "账户服务不可用";
            return UiOperationResult.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _accountUi.ChangePasswordAsync(current, next, confirm, hint, ct);
            // 改密成功后由 App 层统一登出:发 CredentialChanged intent,协调器清理并回登录页。
            if (_lifecycle is not null)
                await _lifecycle.RequestAsync(AppLifecycleIntent.CredentialChanged);
            return UiOperationResult.Success();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult.Failure(OperationError);
        }
        catch (AccountOperationException ex)
        {
            OperationError = ex.Message;
            return UiOperationResult.Failure(OperationError);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return UiOperationResult.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<UiOperationResult> RenameAsync(string name, CancellationToken ct = default)
    {
        if (_accountUi is null)
        {
            OperationError = "账户服务不可用";
            return UiOperationResult.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _accountUi.RenameAsync(name, ct);
            return UiOperationResult.Success();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult.Failure(OperationError);
        }
        catch (AccountOperationException ex)
        {
            OperationError = ex.Message;
            return UiOperationResult.Failure(OperationError);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return UiOperationResult.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<UiOperationResult> ChangeIterationsAsync(string current, int iterations, CancellationToken ct = default)
    {
        if (_accountUi is null)
        {
            OperationError = "账户服务不可用";
            return UiOperationResult.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _accountUi.ChangeIterationsAsync(current, iterations, ct);
            // 改 KDF 成功后由 App 层统一登出:发 CredentialChanged intent,协调器清理并回登录页。
            if (_lifecycle is not null)
                await _lifecycle.RequestAsync(AppLifecycleIntent.CredentialChanged);
            return UiOperationResult.Success();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult.Failure(OperationError);
        }
        catch (AccountOperationException ex)
        {
            OperationError = ex.Message;
            return UiOperationResult.Failure(OperationError);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return UiOperationResult.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Account deletion

    public UiOperationResult<AccountDeletionPreflight> GetAccountDeletionPreflight()
    {
        if (_accountDeletion is null)
        {
            OperationError = "账户删除服务不可用";
            return UiOperationResult<AccountDeletionPreflight>.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult<AccountDeletionPreflight>.Busy();

        OperationError = string.Empty;
        try
        {
            return UiOperationResult<AccountDeletionPreflight>.Success(
                _accountDeletion.GetPreflight());
        }
        catch
        {
            OperationError = "无法检查账户删除条件，请重新在线登录后重试";
            return UiOperationResult<AccountDeletionPreflight>.Failure(OperationError);
        }
    }

    public async Task<UiOperationResult<AccountDeletionOtpRequestResult>> RequestAccountDeletionOtpAsync(
        CancellationToken ct = default)
    {
        if (_accountDeletion is null)
        {
            OperationError = "账户删除服务不可用";
            return UiOperationResult<AccountDeletionOtpRequestResult>.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult<AccountDeletionOtpRequestResult>.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            var result = await _accountDeletion.RequestOtpAsync(ct);
            return UiOperationResult<AccountDeletionOtpRequestResult>.Success(result);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult<AccountDeletionOtpRequestResult>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult<AccountDeletionOtpRequestResult>.Failure(OperationError);
        }
        catch
        {
            OperationError = "无法请求账户删除验证码，请稍后重试";
            return UiOperationResult<AccountDeletionOtpRequestResult>.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<UiOperationResult<AccountDeletionResult>> DeleteAccountAsync(
        AccountDeletionCredential credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        try
        {
            if (_accountDeletion is null)
            {
                OperationError = "账户删除服务不可用";
                return UiOperationResult<AccountDeletionResult>.Failure(OperationError);
            }
            if (IsBusy)
                return UiOperationResult<AccountDeletionResult>.Busy();

            IsBusy = true;
            OperationError = string.Empty;
            try
            {
                var result = await _accountDeletion.DeleteAsync(credential, ct);
                return UiOperationResult<AccountDeletionResult>.Success(result);
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
            {
                return UiOperationResult<AccountDeletionResult>.Cancelled();
            }
            catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
            {
                OperationError = "无法确认账户是否已删除。请勿自动重试。";
                return UiOperationResult<AccountDeletionResult>.Failure(OperationError);
            }
            catch
            {
                OperationError = "账户删除失败，请稍后重试";
                return UiOperationResult<AccountDeletionResult>.Failure(OperationError);
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            credential.Dispose();
        }
    }

    public async Task<UiOperationResult> TransitionAfterAccountDeletionAsync(
        AccountDeletionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var intent = result switch
        {
            AccountDeletionResult.Confirmed => AppLifecycleIntent.Lock,
            AccountDeletionResult.OutcomeUncertain => AppLifecycleIntent.Lock,
            AccountDeletionResult.OutcomeUncertainLocalCleanupComplete => AppLifecycleIntent.Lock,
            AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete => AppLifecycleIntent.Lock,
            AccountDeletionResult.ConfirmedLocalCleanupIncomplete => AppLifecycleIntent.Lock,
            AccountDeletionResult.Rejected { Pending: not null } => AppLifecycleIntent.Lock,
            AccountDeletionResult.CancelledBeforeDispatch { Pending: not null } => AppLifecycleIntent.Lock,
            _ => (AppLifecycleIntent?)null,
        };
        if (intent is null)
            return UiOperationResult.Success();
        var binding = GetAccountDeletionBinding(result);
        if (_lifecycle is null)
        {
            OperationError = "无法完成账户删除后的安全清理";
            return UiOperationResult.Failure(OperationError);
        }

        try
        {
            await _lifecycle.RequestIfAsync(
                intent.Value,
                () => !ShouldSkipAccountDeletionTransition(binding));
            return UiOperationResult.Success();
        }
        catch
        {
            _lifecycle.FailClosedSynchronously();
            OperationError = "账户状态已改变，应用已进入安全锁定状态";
            return UiOperationResult.Failure(OperationError);
        }
    }

    private static AccountDeletionBinding? GetAccountDeletionBinding(
        AccountDeletionResult result) => result switch
    {
        AccountDeletionResult.Confirmed { Preflight: not null } confirmed =>
            confirmed.Preflight.Binding,
        AccountDeletionResult.Rejected { Preflight: not null } rejected =>
            rejected.Preflight.Binding,
        AccountDeletionResult.Rejected { Pending: not null } rejected =>
            rejected.Pending.Binding,
        AccountDeletionResult.CancelledBeforeDispatch cancelled =>
            cancelled.Pending?.Binding ?? cancelled.Preflight.Binding,
        AccountDeletionResult.OutcomeUncertain uncertain => uncertain.Pending.Binding,
        AccountDeletionResult.OutcomeUncertainLocalCleanupComplete complete =>
            complete.Pending.Binding,
        AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete incomplete =>
            incomplete.Pending.Binding,
        AccountDeletionResult.ConfirmedLocalCleanupIncomplete incomplete =>
            incomplete.Pending.Binding,
        _ => null,
    };

    private bool ShouldSkipAccountDeletionTransition(AccountDeletionBinding? binding)
    {
        if (_vault is null)
            return false;
        if (binding is null)
            return true;

        try
        {
            var account = _vault.Snapshot.Account;
            return !string.IsNullOrWhiteSpace(account.Id)
                && account.Endpoints is not null
                && (!string.Equals(account.Id, binding.AccountId, StringComparison.Ordinal)
                    || account.Endpoints != binding.Endpoints);
        }
        catch
        {
            return true;
        }
    }

    public static string GetAccountDeletionRejectionMessage(
        AccountDeletionRejectionReason reason) => reason switch
    {
        AccountDeletionRejectionReason.RequiresUnlockedSession => "请先解锁密码库后重试。",
        AccountDeletionRejectionReason.SessionBindingMismatch => "当前会话已变化，请重新登录后重试。",
        AccountDeletionRejectionReason.FreshOnlineAuthenticationRequired => "需要重新在线登录后才能删除账户。",
        AccountDeletionRejectionReason.ClaimedByOrganization => "此账户域名由组织认领，不能从客户端删除。请联系组织管理员。",
        AccountDeletionRejectionReason.WrongCredentialKind => "所需的验证方式已变化，请重新开始。",
        AccountDeletionRejectionReason.PendingDeletionExists => "此账户已有未确认的删除请求，请勿重复提交。",
        AccountDeletionRejectionReason.LocalPersistenceUnavailable => "无法安全记录删除状态，未发送删除请求。",
        AccountDeletionRejectionReason.LocalPersistenceCorrupt => "本地删除状态损坏，请重新在线登录。",
        AccountDeletionRejectionReason.ConcurrentStateChange => "账户状态已变化，请重新开始。",
        AccountDeletionRejectionReason.InvalidCredential => "验证信息不正确。",
        AccountDeletionRejectionReason.ServerRejected => "服务器拒绝了账户删除请求。",
        _ => "无法删除账户。",
    };

    // RenameCommand 供 XAML 绑定（string 参数可正常传递）
    // ChangePassword/ChangeIterations 的 tuple 版本不能被 XAML CommandParameter 绑定，
    // 由 code-behind 直接 await 公开的 async 方法即可。
    [RelayCommand]
    private async Task Rename(string name) =>
        await RenameAsync(name);

    // ── 两步验证操作 ──────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial DataPageState TwoFactorState { get; set; } = DataPageState.Loading;

    [ObservableProperty]
    public partial DataPageState WebAuthnState { get; set; } = DataPageState.Loading;

    public Task<UiOperationResult<WebAuthnManagementSnapshot>> LoadWebAuthnAsync(
        string pw,
        CancellationToken ct = default) =>
        LoadWebAuthnAsync(TwoFactorUserVerification.FromMasterPassword(pw), ct);

    public async Task<UiOperationResult<WebAuthnManagementSnapshot>> LoadWebAuthnAsync(
        TwoFactorUserVerification verification,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            WebAuthnState = DataPageState.Error;
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult<WebAuthnManagementSnapshot>.Busy();

        IsBusy = true;
        WebAuthnState = DataPageState.Loading;
        OperationError = string.Empty;
        try
        {
            var details = await _twoFactorUi.GetWebAuthnAsync(verification, ct);
            var snapshot = ToWebAuthnSnapshot(details);
            WebAuthnState = snapshot.Credentials.Count == 0
                ? DataPageState.Empty
                : DataPageState.Content;
            return UiOperationResult<WebAuthnManagementSnapshot>.Success(snapshot);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult<WebAuthnManagementSnapshot>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            WebAuthnState = DataPageState.Error;
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        catch (TwoFactorOperationException)
        {
            OperationError = "无法验证所选信息或加载安全密钥";
            WebAuthnState = DataPageState.Error;
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        catch
        {
            OperationError = "无法加载安全密钥，请稍后重试";
            WebAuthnState = DataPageState.Error;
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<UiOperationResult> RequestWebAuthnVerificationOtpAsync(
        CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return UiOperationResult.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _twoFactorUi.RequestProtectedActionOtpAsync(ct);
            return UiOperationResult.Success();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult.Failure(OperationError);
        }
        catch (TwoFactorOperationException)
        {
            OperationError = "无法发送验证码，请稍后重试";
            return UiOperationResult.Failure(OperationError);
        }
        catch
        {
            OperationError = "无法发送验证码，请稍后重试";
            return UiOperationResult.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task<UiOperationResult> AddWebAuthnCredentialAsync(
        string pw,
        int id,
        string name,
        CancellationToken ct = default) =>
        AddWebAuthnCredentialAsync(
            TwoFactorUserVerification.FromMasterPassword(pw),
            id,
            name,
            ct);

    public async Task<UiOperationResult> AddWebAuthnCredentialAsync(
        TwoFactorUserVerification verification,
        int id,
        string name,
        CancellationToken ct = default)
    {
        var result = await AddWebAuthnCredentialWithDetailsAsync(
            verification,
            id,
            name,
            ct);
        return new UiOperationResult(result.Status, result.Error);
    }

    public async Task<UiOperationResult<WebAuthnManagementSnapshot>> AddWebAuthnCredentialWithDetailsAsync(
        TwoFactorUserVerification verification,
        int id,
        string name,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (_twoFactorUi is null || _webAuthnRegistration is null)
        {
            OperationError = "此设备无法注册安全密钥";
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        if (!_webAuthnRegistration.IsSupported)
        {
            OperationError = "此设备不支持 Windows WebAuthn";
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult<WebAuthnManagementSnapshot>.Busy();
        if (id <= 0)
        {
            OperationError = "安全密钥标识无效";
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }

        var normalizedName = name?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(normalizedName))
        {
            OperationError = "请输入安全密钥名称";
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            var challenge = await _twoFactorUi.GetWebAuthnChallengeAsync(verification, ct);
            var deviceResponse = await _webAuthnRegistration.CreateCredentialAsync(challenge, ct);
            var outcome = await _twoFactorUi.CompleteWebAuthnRegistrationAsync(
                verification,
                id,
                normalizedName,
                challenge,
                deviceResponse,
                ct);

            if (outcome is WebAuthnRegistrationOutcome.OutcomeUncertain uncertain)
            {
                OperationError = "无法确认安全密钥是否已添加。请刷新列表检查，不要立即重复添加。";
                var observed = uncertain.ObservedDetails is null
                    ? null
                    : ToWebAuthnSnapshot(uncertain.ObservedDetails);
                return new UiOperationResult<WebAuthnManagementSnapshot>(
                    UiOperationStatus.Failed,
                    observed,
                    OperationError);
            }

            var confirmed = (WebAuthnRegistrationOutcome.Confirmed)outcome;
            var snapshot = ToWebAuthnSnapshot(confirmed.Details);
            WebAuthnState = snapshot.Credentials.Count == 0
                ? DataPageState.Empty
                : DataPageState.Content;
            return UiOperationResult<WebAuthnManagementSnapshot>.Success(snapshot);
        }
        catch (WebAuthnTwoFactorCancelledException)
        {
            return UiOperationResult<WebAuthnManagementSnapshot>.Cancelled();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult<WebAuthnManagementSnapshot>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        catch (TwoFactorOperationException)
        {
            OperationError = "无法完成安全密钥注册，请检查验证信息后重试";
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        catch (WebAuthnTwoFactorException)
        {
            OperationError = "Windows 无法完成安全密钥注册，请重试";
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        catch
        {
            OperationError = "无法添加安全密钥，请稍后重试";
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public Task<UiOperationResult<WebAuthnManagementSnapshot>> DeleteWebAuthnCredentialAsync(
        string pw,
        int id,
        CancellationToken ct = default) =>
        DeleteWebAuthnCredentialAsync(
            TwoFactorUserVerification.FromMasterPassword(pw),
            id,
            ct);

    public async Task<UiOperationResult<WebAuthnManagementSnapshot>> DeleteWebAuthnCredentialAsync(
        TwoFactorUserVerification verification,
        int id,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult<WebAuthnManagementSnapshot>.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            var details = await _twoFactorUi.DeleteWebAuthnCredentialAsync(verification, id, ct);
            var snapshot = ToWebAuthnSnapshot(details);
            WebAuthnState = snapshot.Credentials.Count == 0
                ? DataPageState.Empty
                : DataPageState.Content;
            return UiOperationResult<WebAuthnManagementSnapshot>.Success(snapshot);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult<WebAuthnManagementSnapshot>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        catch (TwoFactorOperationException)
        {
            OperationError = "无法删除安全密钥，请检查验证信息后重试";
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        catch
        {
            OperationError = "无法删除安全密钥，请稍后重试";
            return UiOperationResult<WebAuthnManagementSnapshot>.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static WebAuthnManagementSnapshot ToWebAuthnSnapshot(
        WebAuthnTwoFactorDetails details) =>
        new(
            details.Enabled,
            details.Credentials
                .Select(static credential => new WebAuthnCredentialListItem(
                    credential.Id,
                    credential.Name,
                    credential.Migrated))
                .ToArray());

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
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult<IReadOnlyList<TwoFactorProvider>>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            TwoFactorState = DataPageState.Error;
            return UiOperationResult<IReadOnlyList<TwoFactorProvider>>.Failure(
                UiExceptionPolicy.RequestTimeoutMessage);
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

    public async Task<UiOperationResult<(string secret, string otpauth)>> BeginTotpSetupAsync(string pw, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return UiOperationResult<(string secret, string otpauth)>.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult<(string secret, string otpauth)>.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            var setup = await _twoFactorUi.BeginTotpSetupAsync(pw, ct);
            if (string.IsNullOrWhiteSpace(setup.secret) || string.IsNullOrWhiteSpace(setup.otpauth))
            {
                OperationError = "服务器返回的验证器设置无效，请稍后重试";
                return UiOperationResult<(string secret, string otpauth)>.Failure(OperationError);
            }
            return UiOperationResult<(string secret, string otpauth)>.Success(setup);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult<(string secret, string otpauth)>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult<(string secret, string otpauth)>.Failure(OperationError);
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return UiOperationResult<(string secret, string otpauth)>.Failure(OperationError);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return UiOperationResult<(string secret, string otpauth)>.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<UiOperationResult<TotpActivationResult>> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return UiOperationResult<TotpActivationResult>.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult<TotpActivationResult>.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            var activation = await _twoFactorUi.EnableTotpAsync(pw, secret, code, ct);
            return UiOperationResult<TotpActivationResult>.Success(activation);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult<TotpActivationResult>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult<TotpActivationResult>.Failure(OperationError);
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return UiOperationResult<TotpActivationResult>.Failure(OperationError);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return UiOperationResult<TotpActivationResult>.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<UiOperationResult<string>> GetRecoveryCodeAsync(string pw, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return UiOperationResult<string>.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult<string>.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            var recoveryCode = await _twoFactorUi.GetRecoveryCodeAsync(pw, ct);
            if (string.IsNullOrWhiteSpace(recoveryCode))
            {
                OperationError = "服务器暂未返回恢复码，请稍后重试";
                return UiOperationResult<string>.Failure(OperationError);
            }
            return UiOperationResult<string>.Success(recoveryCode);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult<string>.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult<string>.Failure(OperationError);
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return UiOperationResult<string>.Failure(OperationError);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return UiOperationResult<string>.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<UiOperationResult> SendEmailAsync(string pw, string email, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return UiOperationResult.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _twoFactorUi.SendEmailAsync(pw, email, ct);
            return UiOperationResult.Success();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult.Failure(OperationError);
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return UiOperationResult.Failure(OperationError);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return UiOperationResult.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<UiOperationResult> EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return UiOperationResult.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _twoFactorUi.EnableEmailAsync(pw, email, token, ct);
            return UiOperationResult.Success();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult.Failure(OperationError);
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return UiOperationResult.Failure(OperationError);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return UiOperationResult.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<UiOperationResult> DisableTwoFactorAsync(string pw, int type, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return UiOperationResult.Failure(OperationError);
        }
        if (IsBusy)
            return UiOperationResult.Busy();

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _twoFactorUi.DisableAsync(pw, type, ct);
            return UiOperationResult.Success();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            return UiOperationResult.Cancelled();
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            OperationError = UiExceptionPolicy.RequestTimeoutMessage;
            return UiOperationResult.Failure(OperationError);
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return UiOperationResult.Failure(OperationError);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return UiOperationResult.Failure(OperationError);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
