using App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;

namespace App.Views;

public sealed partial class RegisterPage : Page
{
    public RegisterViewModel ViewModel { get; }

    public RegisterPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<RegisterViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // x:Bind 函数绑定辅助方法（必须是实例方法，不能是 static）
    private bool Not(bool value) => !value;

    private string RegisterButtonText(bool isBusy) => isBusy ? "正在注册" : "创建账户";

    private void OnLoaded(object sender, RoutedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() => _ = EmailTextBox.Focus(FocusState.Programmatic));

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegisterViewModel.FocusRequestId))
            DispatcherQueue.TryEnqueue(ApplyFocusRequest);
    }

    // 校验失败时把焦点定位到第一个无效字段(五个具名输入框)。
    private void ApplyFocusRequest()
    {
        Control target = ViewModel.FocusTarget switch
        {
            RegisterFocusTarget.ServerUrl => ServerUrlTextBox,
            RegisterFocusTarget.Email => EmailTextBox,
            RegisterFocusTarget.Name => NameTextBox,
            RegisterFocusTarget.MasterPassword => MasterPasswordBox,
            RegisterFocusTarget.ConfirmPassword => ConfirmPasswordBox,
            _ => EmailTextBox,
        };
        _ = target.Focus(FocusState.Programmatic);
    }

    // 回车提交;忙碌时不重入(表单整体也已随 CanEditForm 禁用)。
    private async void OnSubmitAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsBusy)
            return;
        args.Handled = true;
        await SubmitAsync();
    }

    private async void OnRegisterClick(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async Task SubmitAsync()
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
