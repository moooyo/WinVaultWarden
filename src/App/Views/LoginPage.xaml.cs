using App.ViewModels;
using Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;

namespace App.Views;

public sealed partial class LoginPage : Page
{
    public LoginViewModel ViewModel { get; }
    private bool _isWindowFitQueued;
    private LoginStage? _helloAutoPromptedForStage;

    public LoginPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<LoginViewModel>();
        ViewModel.SetSuccessCallback(() =>
        {
            if (App.MainWindow is { } window)
                window.ShowVault();
        });

        var tokenStore = global::App.App.Services.GetRequiredService<ITokenStore>();
        if (tokenStore.TryLoad(out var session))
            ViewModel.PrepareUnlock(session.ServerUrl, session.Email);

        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LoginForm.SizeChanged += OnLoginFormSizeChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoginCommand.ExecuteAsync(null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        QueueFitWindowToContent();
        // 保存的会话在构造函数里已 PrepareUnlock(早于下面的 PropertyChanged 订阅),
        // 若 Hello 可用性探测在此之前就已就绪(理论上极快的场景),Loaded 时补一次检查。
        TryAutoPromptHello();
        // 构造期 PrepareUnlock/PrepareAccount 的焦点请求早于页面订阅,会被漏掉;加载完成后按当前阶段补发一次。
        ViewModel.RequestStageFocus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        LoginForm.SizeChanged -= OnLoginFormSizeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
    }

    public void RequestWindowFit() => QueueFitWindowToContent();

    private void OnLoginFormSizeChanged(object sender, SizeChangedEventArgs e) => QueueFitWindowToContent();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsLayoutAffectingProperty(e.PropertyName))
            QueueFitWindowToContent();

        if (e.PropertyName is nameof(LoginViewModel.Stage) or nameof(LoginViewModel.ShowHelloUnlock))
            TryAutoPromptHello();

        if (e.PropertyName == nameof(LoginViewModel.FocusRequestId))
            DispatcherQueue.TryEnqueue(ApplyFocusRequest);
    }

    // 把 VM 的抽象焦点目标翻译为当前阶段可见的具体控件;解锁阶段主密码目标对应 UnlockPasswordBox。
    private void ApplyFocusRequest()
    {
        Control target = ViewModel.FocusTarget switch
        {
            LoginFocusTarget.ServerUrl => ServerUrlTextBox,
            LoginFocusTarget.Email => EmailTextBox,
            LoginFocusTarget.TwoFactorCode => TwoFactorCodeTextBox,
            LoginFocusTarget.Pin => PinBox,
            _ when ViewModel.IsUnlockStage => UnlockPasswordBox,
            _ => MasterPasswordBox,
        };
        _ = target.Focus(FocusState.Programmatic);
    }

    // 回车提交;忙碌时不重入(CanUsePrimaryAction 在 IsBusy 时为 false)。
    private void OnSubmitAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.CanUsePrimaryAction)
            return;
        args.Handled = true;
        _ = ViewModel.LoginCommand.ExecuteAsync(null);
    }

    // 解锁阶段(主密码/PIN)首次出现且 Hello 已启用并可用时,自动发起一次 Hello 手势。
    // 按"已为哪个 Stage 值自动尝试过"去重(而非"是否收到过 Stage 事件")——
    // 可用性探测在真机上可能同步完成,单次 Stage 赋值仍会连续触发多个 PropertyChanged
    // (NotifyStageProperties 里的 ShowHelloUnlock、探测完成后的 ShowHelloUnlock、最后的 Stage 本身),
    // 若按"收到 Stage 事件就重置"会在同一次进入解锁阶段时重复弹出手势。离开解锁阶段则清空,
    // 下次重新进入(含切换 PIN/主密码)按新 Stage 值再次自动尝试。
    private void TryAutoPromptHello()
    {
        if (!(ViewModel.IsUnlockStage || ViewModel.IsPinUnlockStage))
        {
            _helloAutoPromptedForStage = null;
            return;
        }

        var stage = ViewModel.Stage;
        if (_helloAutoPromptedForStage == stage || !ViewModel.ShowHelloUnlock)
            return;

        _helloAutoPromptedForStage = stage;
        _ = ViewModel.UnlockWithHelloCommand.ExecuteAsync(null);
    }

    private void QueueFitWindowToContent()
    {
        if (_isWindowFitQueued)
            return;

        _isWindowFitQueued = true;
        if (!DispatcherQueue.TryEnqueue(FitWindowToContent))
            FitWindowToContent();
    }

    private void FitWindowToContent()
    {
        _isWindowFitQueued = false;
        LoginForm.UpdateLayout();
        App.MainWindow?.FitLoginWindowToContent(LoginForm, LoginSurface.Padding);
    }

    private static bool IsLayoutAffectingProperty(string? propertyName) =>
        propertyName is null
        or nameof(LoginViewModel.Stage)
        or nameof(LoginViewModel.ShowCustomServerUrl)
        or nameof(LoginViewModel.ShowHelloUnlock)
        or nameof(LoginViewModel.HasStatus)
        or nameof(LoginViewModel.Status)
        or nameof(LoginViewModel.IsBusy)
        or nameof(LoginViewModel.FormTitle)
        or nameof(LoginViewModel.FormSubtitle)
        or nameof(LoginViewModel.StepText)
        or nameof(LoginViewModel.PrimaryButtonText);

    private void OnGoToRegisterClick(object sender, RoutedEventArgs e)
    {
        global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>().GoToRegister();
    }
}
