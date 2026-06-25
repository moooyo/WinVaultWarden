using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Services;

namespace App.ViewModels;

public enum LoginStage
{
    Account,
    Password,
    TwoFactor,
    Unlock,
}

public partial class LoginViewModel : ObservableObject
{
    private const string BitwardenUsUrl = "https://vault.bitwarden.com";
    private const string BitwardenEuUrl = "https://vault.bitwarden.eu";

    private readonly IAuthService _auth;
    private readonly IDemoVaultSessionService? _demoVault;
    private Action? _onSuccess;

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = BitwardenUsUrl;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MasterPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TwoFactorCode { get; set; } = string.Empty;

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

    public LoginViewModel(IAuthService auth)
        : this(auth, null)
    {
    }

    public LoginViewModel(IAuthService auth, IDemoVaultSessionService? demoVault)
    {
        _auth = auth;
        _demoVault = demoVault;
    }

    public bool IsAccountStage => Stage == LoginStage.Account;

    public bool IsPasswordStage => Stage == LoginStage.Password;

    public bool IsTwoFactorStage => Stage == LoginStage.TwoFactor;

    public bool IsUnlockStage => Stage == LoginStage.Unlock;

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
            _ => "请稍候",
        }
        : Stage switch
        {
            LoginStage.Account => "继续",
            LoginStage.TwoFactor => "验证",
            LoginStage.Unlock => "解锁",
            _ => "登录",
        };

    public string FormTitle => Stage switch
    {
        LoginStage.Password => "输入主密码",
        LoginStage.TwoFactor => "两步验证",
        LoginStage.Unlock => "解锁保险库",
        _ => "登录",
    };

    public string FormSubtitle => Stage switch
    {
        LoginStage.Password => ServerSummary,
        LoginStage.TwoFactor => AccountSummary,
        LoginStage.Unlock => ServerSummary,
        _ => "连接到你的 Bitwarden / Vaultwarden 保险库",
    };

    public string StepText => Stage switch
    {
        LoginStage.Password => "第 2 步 / 3",
        LoginStage.TwoFactor => "第 3 步 / 3",
        LoginStage.Unlock => "已锁定",
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
        Stage = LoginStage.Unlock;
        Status = "请输入主密码解锁。";
    }

    [RelayCommand]
    private async Task LoginAsync()
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
            var result = IsUnlockStage
                ? await _auth.UnlockAsync(MasterPassword)
                : IsTwoFactorStage
                    ? await _auth.SubmitTwoFactorAsync(TwoFactorCode.Trim(), rememberDevice: RememberDevice)
                    : await _auth.LoginAsync(ResolveServerUrl(), Email.Trim(), MasterPassword);

            HandleResult(result);
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

    [RelayCommand]
    private async Task SwitchAccountAsync()
    {
        if (IsBusy)
            return;

        await _auth.LogoutAsync();
        SelectedServerOptionIndex = 0;
        ServerUrl = BitwardenUsUrl;
        Email = string.Empty;
        MasterPassword = string.Empty;
        TwoFactorCode = string.Empty;
        Status = string.Empty;
        Stage = LoginStage.Account;
    }

    private void ContinueToPassword()
    {
        Status = string.Empty;

        var serverUrl = ResolveServerUrl();
        if (!IsValidServerUrl(serverUrl))
        {
            Status = "请输入有效的服务器地址。";
            return;
        }

        if (string.IsNullOrWhiteSpace(Email))
        {
            Status = "请输入邮箱地址。";
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
            return false;
        }

        if (IsTwoFactorStage && string.IsNullOrWhiteSpace(TwoFactorCode))
        {
            Status = "请输入两步验证码。";
            return false;
        }

        return true;
    }

    private void HandleResult(AuthResult result)
    {
        switch (result)
        {
            case AuthResult.Success:
                var wasTwoFactor = IsTwoFactorStage;
                Status = string.Empty;
                MasterPassword = string.Empty;
                TwoFactorCode = string.Empty;
                if (wasTwoFactor)
                    Stage = LoginStage.Password;
                _onSuccess?.Invoke();
                break;
            case AuthResult.TwoFactorRequired:
                Stage = LoginStage.TwoFactor;
                MasterPassword = string.Empty;
                TwoFactorCode = string.Empty;
                Status = "请输入两步验证码。";
                break;
            case AuthResult.Failure failure:
                Status = failure.Message;
                break;
        }
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

    partial void OnStageChanged(LoginStage value) => NotifyStageProperties();

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
        OnPropertyChanged(nameof(CanEditServer));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanUsePrimaryAction));
        OnPropertyChanged(nameof(ShowCustomServerUrl));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(FormSubtitle));
        OnPropertyChanged(nameof(StepText));
    }
}
