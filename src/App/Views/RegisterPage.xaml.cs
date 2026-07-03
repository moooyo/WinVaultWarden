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
            // 注册成功：把服务器 + 邮箱放入 AccountPrefill(DI 单例),再跳转登录页(新建的 LoginViewModel 会取用)。
            global::App.App.Services.GetRequiredService<global::App.Services.AccountPrefill>()
                .Set(ViewModel.RegisteredServerUrl, ViewModel.RegisteredEmail);
            global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>().GoToLoginPage();
        }
    }

    private void OnBackToLoginClick(object sender, RoutedEventArgs e)
    {
        global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>().GoToLoginPage();
    }
}
