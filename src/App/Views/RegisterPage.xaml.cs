using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class RegisterPage : Page
{
    public RegisterViewModel ViewModel { get; }

    public RegisterPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<RegisterViewModel>();
        InitializeComponent();
    }

    // x:Bind 函数绑定辅助方法（必须是实例方法，不能是 static）
    private bool Not(bool value) => !value;

    private string RegisterButtonText(bool isBusy) => isBusy ? "正在注册" : "创建账户";

    private async void OnRegisterClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RegisterAsync();

        if (ViewModel.Registered)
        {
            // 注册成功：跳转到登录页，并预填服务器 + 邮箱
            var loginVm = global::App.App.Services.GetRequiredService<LoginViewModel>();
            loginVm.PrepareAccount(ViewModel.RegisteredServerUrl, ViewModel.RegisteredEmail);
            Frame.Navigate(typeof(LoginPage));
        }
    }

    private void OnBackToLoginClick(object sender, RoutedEventArgs e)
    {
        Frame.Navigate(typeof(LoginPage));
    }
}
