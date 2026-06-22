using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class LoginPage : Page
{
    public LoginViewModel ViewModel { get; }

    public LoginPage()
    {
        // 用完全限定名避免与命名空间 App.Services 冲突(App 类的静态属性 Services)。
        ViewModel = global::App.App.Services.GetRequiredService<LoginViewModel>();
        InitializeComponent();
    }

    // mock 登录:不验真,直接切到主界面。真实认证后续接入 AuthService。
    private void OnLogin(object sender, RoutedEventArgs e)
    {
        if (App.MainWindow is { } w) w.ShowVault();
    }
}
