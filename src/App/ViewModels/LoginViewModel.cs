using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Services;

namespace App.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthService _auth;
    private Action? _onSuccess;

    [ObservableProperty] private string _serverUrl = "https://vault.bitwarden.com";
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _masterPassword = string.Empty;
    [ObservableProperty] private string _twoFactorCode = string.Empty;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isTwoFactorStage;
    [ObservableProperty] private bool _isUnlockStage;

    public LoginViewModel(IAuthService auth) => _auth = auth;

    public bool CanEditServer => !IsTwoFactorStage && !IsUnlockStage;

    public string PrimaryButtonText => IsUnlockStage ? "解锁" : IsTwoFactorStage ? "验证" : "登录";

    public void SetSuccessCallback(Action callback) => _onSuccess = callback;

    public void PrepareUnlock(string serverUrl, string email)
    {
        ServerUrl = serverUrl;
        Email = email;
        MasterPassword = string.Empty;
        TwoFactorCode = string.Empty;
        IsTwoFactorStage = false;
        IsUnlockStage = true;
        Status = "请输入主密码解锁。";
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        var result = IsUnlockStage
            ? await _auth.UnlockAsync(MasterPassword)
            : IsTwoFactorStage
                ? await _auth.SubmitTwoFactorAsync(TwoFactorCode)
                : await _auth.LoginAsync(ServerUrl, Email, MasterPassword);

        HandleResult(result);
    }

    private void HandleResult(AuthResult result)
    {
        switch (result)
        {
            case AuthResult.Success:
                Status = string.Empty;
                MasterPassword = string.Empty;
                TwoFactorCode = string.Empty;
                IsTwoFactorStage = false;
                _onSuccess?.Invoke();
                break;
            case AuthResult.TwoFactorRequired:
                IsTwoFactorStage = true;
                MasterPassword = string.Empty;
                Status = "请输入两步验证码。";
                break;
            case AuthResult.Failure failure:
                Status = failure.Message;
                break;
        }
    }

    partial void OnIsTwoFactorStageChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditServer));
        OnPropertyChanged(nameof(PrimaryButtonText));
    }

    partial void OnIsUnlockStageChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditServer));
        OnPropertyChanged(nameof(PrimaryButtonText));
    }
}
