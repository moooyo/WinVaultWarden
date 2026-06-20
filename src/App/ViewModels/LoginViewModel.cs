using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Services;

namespace App.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthService _auth;

    [ObservableProperty] private string _serverUrl = "https://vault.bitwarden.com";
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _masterPassword = string.Empty;
    [ObservableProperty] private string _status = string.Empty;

    public LoginViewModel(IAuthService auth) => _auth = auth;

    [RelayCommand]
    private async Task LoginAsync()
    {
        try
        {
            await _auth.LoginAsync(ServerUrl, Email, MasterPassword);
        }
        catch (NotImplementedException)
        {
            Status = "登录尚未实现(骨架阶段)";
        }
    }
}
