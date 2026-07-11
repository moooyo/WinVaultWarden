using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Abstractions;
using Core.Services;

namespace App.ViewModels;

public enum LoginStage
{
    Account,
    Password,
    TwoFactor,
    Unlock,
    PinUnlock,
}

public enum LoginFocusTarget
{
    ServerUrl,
    Email,
    MasterPassword,
    TwoFactorCode,
    Pin,
}

public partial class LoginViewModel : ObservableObject, ISensitiveState
{
    private const string BitwardenUsUrl = "https://vault.bitwarden.com";
    private const string BitwardenEuUrl = "https://vault.bitwarden.eu";

    private const int EmailTwoFactorProvider = 1;

    private readonly IAuthService _auth;
    private readonly IDemoVaultSessionService? _demoVault;
    private readonly IPinService? _pin;
    private readonly IWindowsHelloAuthenticator? _hello;
    private readonly IHelloUnlockService? _helloUnlock;
    private readonly IAppLifecycleCoordinator? _lifecycle;
    private Action? _onSuccess;
    private IReadOnlyList<int> _twoFactorProviders = Array.Empty<int>();

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = BitwardenUsUrl;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MasterPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Pin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TwoFactorCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<TwoFactorProviderOption> TwoFactorProviderOptions { get; set; } = Array.Empty<TwoFactorProviderOption>();

    [ObservableProperty]
    public partial int SelectedTwoFactorProviderIndex { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedServerOptionIndex { get; set; }

    [ObservableProperty]
    public partial bool RememberDevice { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial LoginStage Stage { get; set; } = LoginStage.Account;

    // 页面据 FocusRequestId 单调递增触发一次编程式焦点(而非直接绑定 FocusTarget,避免同值不触发)。
    [ObservableProperty]
    public partial LoginFocusTarget FocusTarget { get; set; }

    [ObservableProperty]
    public partial long FocusRequestId { get; set; }

    public LoginViewModel(IAuthService auth)
        : this(auth, null)
    {
    }

    public LoginViewModel(
        IAuthService auth,
        IDemoVaultSessionService? demoVault,
        IPinService? pin = null,
        AccountPrefill? prefill = null,
        IWindowsHelloAuthenticator? hello = null,
        IHelloUnlockService? helloUnlock = null,
        IAppLifecycleCoordinator? lifecycle = null)
    {
        _auth = auth;
        _demoVault = demoVault;
        _pin = pin;
        _hello = hello;
        _helloUnlock = helloUnlock;
        _lifecycle = lifecycle;

        // 注册成功后经 AccountPrefill(DI 单例)把服务器/邮箱预填带到新建的 LoginViewModel(Transient,
        // 注册页设置的实例会被登录页新建的实例取代,直接在旧实例上 PrepareAccount 会丢失,R10)。取用即清。
        if (prefill?.Take() is { } p)
            PrepareAccount(p.ServerUrl, p.Email);
    }

    public bool IsAccountStage => Stage == LoginStage.Account;

    public bool IsPasswordStage => Stage == LoginStage.Password;

    public bool IsTwoFactorStage => Stage == LoginStage.TwoFactor;

    public bool IsUnlockStage => Stage == LoginStage.Unlock;

    public bool IsPinUnlockStage => Stage == LoginStage.PinUnlock;

    public bool IsWindowsHelloAvailable { get; private set; }

    // 解锁阶段(主密码或 PIN)+ 已启用 Hello + 设备可用 → 显示 Hello 解锁入口。
    public bool ShowHelloUnlock => (IsUnlockStage || IsPinUnlockStage)
        && (_helloUnlock?.IsHelloSet ?? false) && IsWindowsHelloAvailable;

    public bool ShowResendEmailCode => IsTwoFactorStage && _twoFactorProviders.Contains(EmailTwoFactorProvider);

    public bool CanResendEmailCode => ShowResendEmailCode && !IsBusy;

    public bool ShowTwoFactorProviderPicker => TwoFactorProviderOptions.Count > 1;

    private static bool IsTypedCodeProvider(int provider) => provider is 0 or 1; // Authenticator / Email

    private static string ProviderDisplayName(int provider) => provider switch
    {
        0 => "身份验证器 App",
        1 => "电子邮件",
        _ => $"其他方式 ({provider})",
    };

    private int ResolveSelectedTwoFactorProvider()
    {
        var options = TwoFactorProviderOptions;
        if (options.Count == 0)
            return _twoFactorProviders.FirstOrDefault();
        var index = Math.Clamp(SelectedTwoFactorProviderIndex, 0, options.Count - 1);
        return options[index].Type;
    }

    public bool CanEditServer => IsAccountStage && !IsBusy;

    public bool CanUseDemoVault => _demoVault is not null;

    public bool CanGoBack => !IsBusy && (IsPasswordStage || IsTwoFactorStage);

    public bool CanUsePrimaryAction => !IsBusy;

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public bool ShowCustomServerUrl => IsAccountStage && SelectedServerOptionIndex == 2;

    public string PrimaryButtonText => IsBusy
        ? Stage switch
        {
            LoginStage.Password => "正在登录",
            LoginStage.TwoFactor => "正在验证",
            LoginStage.Unlock => "正在解锁",
            LoginStage.PinUnlock => "正在解锁",
            _ => "请稍候",
        }
        : Stage switch
        {
            LoginStage.Account => "继续",
            LoginStage.TwoFactor => "验证",
            LoginStage.Unlock => "解锁",
            LoginStage.PinUnlock => "解锁",
            _ => "登录",
        };

    public string FormTitle => Stage switch
    {
        LoginStage.Password => "输入主密码",
        LoginStage.TwoFactor => "两步验证",
        LoginStage.Unlock => "解锁保险库",
        LoginStage.PinUnlock => "用 PIN 解锁",
        _ => "登录",
    };

    public string FormSubtitle => Stage switch
    {
        LoginStage.Password => ServerSummary,
        LoginStage.TwoFactor => AccountSummary,
        LoginStage.Unlock => ServerSummary,
        LoginStage.PinUnlock => ServerSummary,
        _ => "连接到你的 Bitwarden / Vaultwarden 保险库",
    };

    public string StepText => Stage switch
    {
        LoginStage.Password => "第 2 步 / 3",
        LoginStage.TwoFactor => "第 3 步 / 3",
        LoginStage.Unlock => "已锁定",
        LoginStage.PinUnlock => "已锁定",
        _ => "第 1 步 / 3",
    };

    public string AccountSummary => string.IsNullOrWhiteSpace(Email) ? "邮箱地址" : Email.Trim();

    public string ServerSummary => ShortServer(ResolveServerUrl());

    public string AccountInitial
    {
        get
        {
            var trimmed = Email.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? "V" : trimmed[..1].ToUpperInvariant();
        }
    }

    public void SetSuccessCallback(Action callback) => _onSuccess = callback;

    public void PrepareUnlock(string serverUrl, string email)
    {
        ServerUrl = serverUrl;
        Email = email;
        MasterPassword = string.Empty;
        TwoFactorCode = string.Empty;
        Pin = string.Empty;
        Stage = (_pin?.IsPinSet ?? false) ? LoginStage.PinUnlock : LoginStage.Unlock;
        Status = IsPinUnlockStage ? "请输入 PIN 解锁。" : "请输入主密码解锁。";
    }

    public void PrepareAccount(string serverUrl, string email)
    {
        SelectedServerOptionIndex = serverUrl == BitwardenUsUrl ? 0
            : serverUrl == BitwardenEuUrl ? 1
            : 2;
        Email = email;
        MasterPassword = string.Empty;
        TwoFactorCode = string.Empty;
        Status = string.Empty;
        Stage = LoginStage.Account;
        // Set ServerUrl last so it wins over any placeholder written by
        // OnSelectedServerOptionIndexChanged (e.g. "https://vault.example.com").
        ServerUrl = serverUrl;
    }

    [RelayCommand]
    private async Task LoginAsync(CancellationToken ct)
    {
        if (IsBusy)
            return;

        if (IsAccountStage)
        {
            ContinueToPassword();
            return;
        }

        if (!ValidateSecretInput())
            return;

        try
        {
            IsBusy = true;
            var result = IsPinUnlockStage
                ? await _auth.UnlockWithPinAsync(Pin, ct)
                : IsUnlockStage
                    ? await _auth.UnlockAsync(MasterPassword, ct)
                    : IsTwoFactorStage
                        ? await _auth.SubmitTwoFactorAsync(TwoFactorCode.Trim(), ResolveSelectedTwoFactorProvider(), ct, rememberDevice: RememberDevice)
                        : await _auth.LoginAsync(ResolveServerUrl(), Email.Trim(), MasterPassword, ct);

            HandleResult(result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status = string.Empty;
        }
        catch (HttpRequestException)
        {
            Status = "无法连接服务器，请检查网络后重试。";
            RequestFocus(CurrentSecretFocusTarget());
        }
        catch (TaskCanceledException)
        {
            Status = "连接服务器超时，请稍后重试。";
            RequestFocus(CurrentSecretFocusTarget());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UnlockWithHelloAsync(CancellationToken ct)
    {
        if (IsBusy)
            return;

        if (_hello is null || _helloUnlock is null)
            return;

        var challenge = _helloUnlock.LoadChallenge();
        if (challenge is null)
            return;

        try
        {
            IsBusy = true;

            var signature = await _hello.GetProtectorAsync(challenge, ct);
            if (signature is null)
            {
                Status = "Windows Hello 未完成。";
                return;
            }

            var result = await _auth.UnlockWithHelloAsync(signature, ct);
            HandleResult(result); // 复用现有解锁结果处理(导航/提示),与 LoginAsync 一致
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status = string.Empty;
        }
        catch (HttpRequestException)
        {
            Status = "无法连接服务器，请检查网络后重试。";
            RequestFocus(CurrentSecretFocusTarget());
        }
        catch (TaskCanceledException)
        {
            Status = "连接服务器超时，请稍后重试。";
            RequestFocus(CurrentSecretFocusTarget());
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseDemoVault))]
    private async Task UseDemoVaultAsync()
    {
        if (_demoVault is null)
            return;

        await _demoVault.OpenDemoVaultAsync();
        Status = string.Empty;
        MasterPassword = string.Empty;
        TwoFactorCode = string.Empty;
        Stage = LoginStage.Account;
        _onSuccess?.Invoke();
    }

    [RelayCommand]
    private void Back()
    {
        if (IsBusy)
            return;

        Status = string.Empty;
        if (IsTwoFactorStage)
        {
            TwoFactorCode = string.Empty;
            Stage = LoginStage.Password;
        }
        else if (IsPasswordStage)
        {
            MasterPassword = string.Empty;
            Stage = LoginStage.Account;
        }
    }

    [RelayCommand(CanExecute = nameof(CanResendEmailCode))]
    private async Task ResendEmailCodeAsync()
    {
        var sent = await _auth.ResendTwoFactorEmailAsync();
        Status = sent ? "验证码已重新发送，请查收邮箱。" : "验证码发送失败，请稍后重试。";
    }

    [RelayCommand]
    private void UseMasterPassword()
    {
        Stage = LoginStage.Unlock;
        Pin = string.Empty;
        Status = "请输入主密码解锁。";
    }

    [RelayCommand]
    private async Task SwitchAccountAsync()
    {
        // 切换账户 = 登出当前会话:唯一经生命周期协调器请求 Logout(禁止旁路直接 auth.LogoutAsync)。
        // 协调器完成清理后 ShowLogin 会重建全新登录页,故此处不再自行复位表单字段。
        if (IsBusy || _lifecycle is null)
            return;
        await _lifecycle.RequestAsync(AppLifecycleIntent.Logout);
    }

    // 锁屏/登出时清空登录页所有机密输入:主密码、PIN、2FA 验证码与其可选项。幂等。
    public void ClearSensitive()
    {
        MasterPassword = string.Empty;
        Pin = string.Empty;
        TwoFactorCode = string.Empty;
        TwoFactorProviderOptions = [];
    }

    private void ContinueToPassword()
    {
        Status = string.Empty;

        var serverUrl = ResolveServerUrl();
        if (!IsValidServerUrl(serverUrl))
        {
            Status = "请输入有效的服务器地址。";
            RequestFocus(LoginFocusTarget.ServerUrl);
            return;
        }

        if (string.IsNullOrWhiteSpace(Email))
        {
            Status = "请输入邮箱地址。";
            RequestFocus(LoginFocusTarget.Email);
            return;
        }

        ServerUrl = serverUrl;
        Email = Email.Trim();
        MasterPassword = string.Empty;
        TwoFactorCode = string.Empty;
        Stage = LoginStage.Password;
    }

    private bool ValidateSecretInput()
    {
        Status = string.Empty;

        if ((IsPasswordStage || IsUnlockStage) && string.IsNullOrWhiteSpace(MasterPassword))
        {
            Status = "请输入主密码。";
            RequestFocus(LoginFocusTarget.MasterPassword);
            return false;
        }

        if (IsPinUnlockStage && string.IsNullOrWhiteSpace(Pin))
        {
            Status = "请输入 PIN。";
            RequestFocus(LoginFocusTarget.Pin);
            return false;
        }

        if (IsTwoFactorStage && string.IsNullOrWhiteSpace(TwoFactorCode))
        {
            Status = "请输入两步验证码。";
            RequestFocus(LoginFocusTarget.TwoFactorCode);
            return false;
        }

        return true;
    }

    // 页面在 Loaded 时调用:构造期 PrepareUnlock/PrepareAccount 的焦点请求早于页面订阅 PropertyChanged,
    // 会被漏掉,故加载完成后按当前阶段补发一次。
    public void RequestStageFocus() => RequestFocus(CurrentStageFocusTarget());

    private void RequestFocus(LoginFocusTarget target)
    {
        FocusTarget = target;
        checked { FocusRequestId++; }
    }

    private LoginFocusTarget CurrentStageFocusTarget() => Stage switch
    {
        // 自托管且 URL 尚不合法时先聚焦地址框,否则默认从邮箱开始。
        LoginStage.Account => SelectedServerOptionIndex == 2 && !IsValidServerUrl(ServerUrl.Trim())
            ? LoginFocusTarget.ServerUrl
            : LoginFocusTarget.Email,
        LoginStage.Password => LoginFocusTarget.MasterPassword,
        LoginStage.Unlock => LoginFocusTarget.MasterPassword,
        LoginStage.TwoFactor => LoginFocusTarget.TwoFactorCode,
        LoginStage.PinUnlock => LoginFocusTarget.Pin,
        _ => LoginFocusTarget.Email,
    };

    // 网络/超时失败后把焦点还给当前阶段的机密输入,方便用户直接重试。
    private LoginFocusTarget CurrentSecretFocusTarget() => Stage switch
    {
        LoginStage.TwoFactor => LoginFocusTarget.TwoFactorCode,
        LoginStage.PinUnlock => LoginFocusTarget.Pin,
        _ => LoginFocusTarget.MasterPassword,
    };

    private void HandleResult(AuthResult result)
    {
        switch (result)
        {
            case AuthResult.Success:
                var wasTwoFactor = IsTwoFactorStage;
                Status = string.Empty;
                MasterPassword = string.Empty;
                TwoFactorCode = string.Empty;
                Pin = string.Empty;
                if (wasTwoFactor)
                    Stage = LoginStage.Password;
                _onSuccess?.Invoke();
                break;
            case AuthResult.TwoFactorRequired twoFactor:
                _twoFactorProviders = twoFactor.Providers;
                var typed = twoFactor.Providers.Where(IsTypedCodeProvider).ToList();
                if (typed.Count == 0)
                {
                    // WebAuthn(7)/Duo(2,6) 等断言/重定向流程留待 P2;此处明确提示而非静默失败(停留在密码阶段)。
                    Status = "暂不支持该两步验证方式（如 WebAuthn / Duo），请使用官方客户端完成两步验证。";
                    break;
                }
                TwoFactorProviderOptions = typed
                    .Select(p => new TwoFactorProviderOption(p, ProviderDisplayName(p)))
                    .ToList();
                SelectedTwoFactorProviderIndex = 0;
                Stage = LoginStage.TwoFactor;
                MasterPassword = string.Empty;
                TwoFactorCode = string.Empty;
                Status = "请输入两步验证码。";
                OnPropertyChanged(nameof(ShowResendEmailCode));
                OnPropertyChanged(nameof(CanResendEmailCode));
                OnPropertyChanged(nameof(ShowTwoFactorProviderPicker));
                ResendEmailCodeCommand.NotifyCanExecuteChanged();
                break;
            case AuthResult.PinCleared pinCleared:
                Status = pinCleared.Message;
                Stage = LoginStage.Unlock;
                Pin = string.Empty;
                break;
            case AuthResult.Failure failure:
                Status = LocalizeAuthMessage(failure.Message);
                if (IsPinUnlockStage)
                    Pin = string.Empty;
                break;
        }
    }

    // 把认证失败消息本地化为中文(R20):AuthService/服务端多为英文,且限流时消息为空会让登录页
    // 毫无提示(疑似卡死)。空 → 中文兜底;已知英文 → 中文;其余(含服务端自定义)原样显示(总比空白好)。
    private static string LocalizeAuthMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "登录失败,请稍后重试。";

        if (message.Contains("Username or password is incorrect", StringComparison.OrdinalIgnoreCase))
            return "用户名或主密码错误,请重试。";

        return message switch
        {
            "No saved session." => "没有已保存的会话,请重新登录。",
            "Authentication failed." => "认证失败,请检查凭据。",
            "Two-factor authentication is still required." => "仍需完成两步验证。",
            "No two-factor challenge is pending." => "没有待处理的两步验证请求。",
            "Token response did not include a protected user key." => "服务器响应缺少用户密钥,登录失败。",
            _ => message,
        };
    }

    private string ResolveServerUrl()
    {
        if (!IsAccountStage)
            return ServerUrl.Trim();

        return SelectedServerOptionIndex switch
        {
            0 => BitwardenUsUrl,
            1 => BitwardenEuUrl,
            _ => ServerUrl.Trim(),
        };
    }

    private static bool IsValidServerUrl(string serverUrl) =>
        Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string ShortServer(string serverUrl)
    {
        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return uri.Host;

        return serverUrl;
    }

    partial void OnSelectedServerOptionIndexChanged(int value)
    {
        if (value == 0)
            ServerUrl = BitwardenUsUrl;
        else if (value == 1)
            ServerUrl = BitwardenEuUrl;
        else if (ServerUrl is BitwardenUsUrl or BitwardenEuUrl)
            ServerUrl = "https://vault.example.com";

        NotifyServerProperties();
    }

    partial void OnServerUrlChanged(string value) => NotifyServerProperties();

    partial void OnEmailChanged(string value)
    {
        OnPropertyChanged(nameof(AccountSummary));
        OnPropertyChanged(nameof(AccountInitial));
        OnPropertyChanged(nameof(FormSubtitle));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnIsBusyChanged(bool value) => NotifyStageProperties();

    partial void OnStageChanged(LoginStage value)
    {
        NotifyStageProperties();
        RequestFocus(CurrentStageFocusTarget());
        if (value is LoginStage.Unlock or LoginStage.PinUnlock)
            _ = RefreshWindowsHelloAvailabilityAsync();
    }

    // 进入解锁阶段(主密码/PIN)时探测本机 Hello 可用性;结果就绪后再通知一次 ShowHelloUnlock
    // (fake 场景下 IsAvailableAsync 已完成的 Task 会同步跑完,真实 WinRT 调用则是真异步)。
    private async Task RefreshWindowsHelloAvailabilityAsync()
    {
        IsWindowsHelloAvailable = _hello is not null && await _hello.IsAvailableAsync();
        OnPropertyChanged(nameof(ShowHelloUnlock));
    }

    private void NotifyServerProperties()
    {
        OnPropertyChanged(nameof(ServerSummary));
        OnPropertyChanged(nameof(ShowCustomServerUrl));
        OnPropertyChanged(nameof(FormSubtitle));
    }

    private void NotifyStageProperties()
    {
        OnPropertyChanged(nameof(IsAccountStage));
        OnPropertyChanged(nameof(IsPasswordStage));
        OnPropertyChanged(nameof(IsTwoFactorStage));
        OnPropertyChanged(nameof(IsUnlockStage));
        OnPropertyChanged(nameof(IsPinUnlockStage));
        OnPropertyChanged(nameof(ShowHelloUnlock));
        OnPropertyChanged(nameof(CanEditServer));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanUsePrimaryAction));
        OnPropertyChanged(nameof(ShowCustomServerUrl));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(FormSubtitle));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(ShowResendEmailCode));
        OnPropertyChanged(nameof(CanResendEmailCode));
        ResendEmailCodeCommand.NotifyCanExecuteChanged();
    }
}

public sealed record TwoFactorProviderOption(int Type, string DisplayName);
