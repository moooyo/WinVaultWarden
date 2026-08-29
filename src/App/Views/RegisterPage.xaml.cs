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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsEditing)
        {
            DispatcherQueue.TryEnqueue(
                () => _ = EmailTextBox.Focus(FocusState.Programmatic));
        }
        else
        {
            DispatcherQueue.TryEnqueue(FocusRegistrationHandoffAction);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ViewModel.ClearSensitive();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegisterViewModel.FocusRequestId))
            DispatcherQueue.TryEnqueue(ApplyFocusRequest);
        else if (e.PropertyName == nameof(RegisterViewModel.State)
            && !ViewModel.IsEditing)
            DispatcherQueue.TryEnqueue(FocusRegistrationHandoffAction);
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
        if (!ViewModel.CanEditForm)
            return;
        args.Handled = true;
        await SubmitAsync();
    }

    private void FocusRegistrationHandoffAction()
    {
        if (ViewModel.IsAwaitingEmailVerification)
            _ = RegisterContinueToLoginButton.Focus(FocusState.Programmatic);
        else if (ViewModel.IsOutcomeUncertain)
            _ = RegisterUncertainLoginButton.Focus(FocusState.Programmatic);
    }

    private async void OnRegisterClick(object sender, RoutedEventArgs e) => await SubmitAsync();

    private async Task SubmitAsync()
    {
        await ViewModel.RegisterAsync();

        if (ViewModel.Registered)
            NavigateToLoginWithPrefill();
    }

    private void OnContinueToLoginClick(object sender, RoutedEventArgs e) =>
        NavigateToLoginWithPrefill();

    private void OnResumeEditingClick(object sender, RoutedEventArgs e) =>
        ViewModel.ResumeEditing();

    private void NavigateToLoginWithPrefill()
    {
        if (string.IsNullOrWhiteSpace(ViewModel.RegisteredServerUrl)
            || string.IsNullOrWhiteSpace(ViewModel.RegisteredEmail))
        {
            return;
        }

        global::App.App.Services.GetRequiredService<global::App.Services.AccountPrefill>()
            .Set(ViewModel.RegisteredServerUrl, ViewModel.RegisteredEmail);
        global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>()
            .GoToLoginPage();
    }

    private void OnBackToLoginClick(object sender, RoutedEventArgs e)
    {
        global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>().GoToLoginPage();
    }
}
