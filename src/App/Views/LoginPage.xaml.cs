using App.Services;
using App.ViewModels;
using Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using System.ComponentModel;

namespace App.Views;

public sealed partial class LoginPage : Page
{
    public LoginViewModel ViewModel { get; }
    private bool _isWindowFitQueued;
    private bool _isDeactivated;
    private bool _isQuarantineConfirmationOpen;
    private bool _isAccountDeletionConfirmationOpen;
    private bool _isFullLocalResetConfirmationOpen;
    private CancellationTokenSource? _fullLocalResetConfirmationCancellation;
    private LoginStage? _helloAutoPromptedForStage;

    public LoginPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<LoginViewModel>();
        ViewModel.SetSuccessHandler(lease =>
            App.MainWindow is { } window && window.ShowVault(lease));

        var tokenStore = global::App.App.Services.GetRequiredService<ITokenStore>();
        if (tokenStore.TryLoad(out var session))
            ViewModel.PrepareInitialLoginState(session);
        else
            ViewModel.PrepareInitialLoginState(activeSession: null);

        InitializeComponent();
        Loaded += OnLoaded;
        LoginForm.SizeChanged += OnLoginFormSizeChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoginCommand.ExecuteAsync(null);
    }

    private async void OnClearLoginWithDeviceQuarantineClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_isQuarantineConfirmationOpen
            || !ViewModel.CanClearLoginWithDeviceQuarantine)
        {
            return;
        }

        _isQuarantineConfirmationOpen = true;
        try
        {
            if (await DialogHelper.ConfirmAsync(
                    XamlRoot,
                    "清除设备登录隔离？",
                    "请先检查并撤销其他设备中的可疑会话。清除后，将重新检查此设备是否可使用设备登录。",
                    "清除隔离")
                && !_isDeactivated
                && ViewModel.CanClearLoginWithDeviceQuarantine)
            {
                await ViewModel.ClearLoginWithDeviceQuarantineCommand.ExecuteAsync(null);
            }
        }
        finally
        {
            _isQuarantineConfirmationOpen = false;
        }
    }

    private async void OnClearPendingAccountDeletionClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_isAccountDeletionConfirmationOpen
            || !ViewModel.CanRunAccountDeletionRecoveryAction
            || !ViewModel.ShowClearPendingAccountDeletion)
        {
            return;
        }

        _isAccountDeletionConfirmationOpen = true;
        try
        {
            if (await DialogHelper.ConfirmAsync(
                    XamlRoot,
                    "仅清除此设备上的数据？",
                    "账户删除请求的服务器结果仍不确定。此前发送的删除请求仍可能稍后在服务器完成。成功后会写入永久安全隔离记录，并在本安装中永久禁用此账户标识与当前服务器端点组合。若日后确认账户仍存在，要在此安装中恢复，只能通过 Windows 应用重置清除此应用的全部本地数据。此操作不会再次向服务器发送删除请求。",
                    "仅清除此设备")
                && !_isDeactivated
                && ViewModel.CanRunAccountDeletionRecoveryAction
                && ViewModel.ShowClearPendingAccountDeletion)
            {
                await ViewModel.ClearPendingAccountDeletionLocalDataAsync();
            }
        }
        finally
        {
            _isAccountDeletionConfirmationOpen = false;
        }
    }

    private async void OnRequestFullLocalResetClick(
        object sender,
        RoutedEventArgs e)
    {
        if (_isFullLocalResetConfirmationOpen
            || !ViewModel.CanRequestFullLocalReset)
        {
            return;
        }

        _isFullLocalResetConfirmationOpen = true;
        using var confirmationCancellation = new CancellationTokenSource();
        _fullLocalResetConfirmationCancellation = confirmationCancellation;
        var confirmation = new TextBox
        {
            Header = "输入 RESET 以确认",
            PlaceholderText = "RESET",
            MaxLength = 5,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(
            confirmation,
            "FullLocalResetConfirmationTextBox");
        var warning = new TextBlock
        {
            Text = "此操作现在会记录重置请求并退出应用；所有保存账户、离线密码库、设备密钥、应用偏好和永久删除隔离记录会在下次启动、登录界面出现前完成删除。服务器上的账户和密码库数据不会受到影响。浏览器集成可能需要重新配置。",
            TextWrapping = TextWrapping.Wrap,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(warning);
        content.Children.Add(confirmation);
        var dialog = new ContentDialog
        {
            Title = "清除全部本机数据并退出？",
            Content = content,
            PrimaryButtonText = "清除全部本机数据并退出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot,
        };

        void UpdateConfirmation(object _, TextChangedEventArgs __) =>
            dialog.IsPrimaryButtonEnabled = string.Equals(
                confirmation.Text,
                "RESET",
                StringComparison.Ordinal);

        confirmation.TextChanged += UpdateConfirmation;
        try
        {
            var result = await DialogHelper.ShowTrackedAsync(
                dialog,
                ct: confirmationCancellation.Token);
            if (ReferenceEquals(
                    _fullLocalResetConfirmationCancellation,
                    confirmationCancellation))
            {
                _fullLocalResetConfirmationCancellation = null;
            }
            if (result == ContentDialogResult.Primary
                && !_isDeactivated
                && ViewModel.CanRequestFullLocalReset)
            {
                await ViewModel.RequestFullLocalResetCommand.ExecuteAsync(null);
            }
        }
        finally
        {
            confirmation.TextChanged -= UpdateConfirmation;
            confirmation.Text = string.Empty;
            if (ReferenceEquals(
                    _fullLocalResetConfirmationCancellation,
                    confirmationCancellation))
            {
                _fullLocalResetConfirmationCancellation = null;
            }
            _isFullLocalResetConfirmationOpen = false;
        }
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

    private void OnUnloaded(object sender, RoutedEventArgs e) => Deactivate();

    private void Deactivate()
    {
        if (_isDeactivated)
            return;

        _isDeactivated = true;
        _fullLocalResetConfirmationCancellation?.Cancel();
        _fullLocalResetConfirmationCancellation = null;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        LoginForm.SizeChanged -= OnLoginFormSizeChanged;
        Loaded -= OnLoaded;
        ViewModel.Deactivate();
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
            LoginFocusTarget.DeviceVerificationCode => DeviceVerificationCodeTextBox,
            LoginFocusTarget.TwoFactorCode => TwoFactorCodeTextBox,
            LoginFocusTarget.NewMasterPassword => NewMasterPasswordBox,
            LoginFocusTarget.Pin => PinBox,
            LoginFocusTarget.LoginWithDeviceCancel => CancelLoginWithDeviceButton,
            LoginFocusTarget.AccountDeletionRecoveryAction =>
                ContinueFromAccountDeletionRecoveryButton,
            LoginFocusTarget.PrimaryAction => PrimaryAuthButton,
            _ when ViewModel.IsUnlockStage => UnlockPasswordBox,
            _ => MasterPasswordBox,
        };
        _ = target.Focus(FocusState.Programmatic);
    }

    // 回车提交;忙碌时不重入(CanUsePrimaryAction 在 IsBusy 时为 false)。
    private async void OnSubmitAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!ViewModel.CanUsePrimaryAction)
            return;
        args.Handled = true;
        await ViewModel.LoginCommand.ExecuteAsync(null);
    }

    // 解锁阶段(主密码/PIN)首次出现且 Hello 已启用并可用时,自动发起一次 Hello 手势。
    // 按"已为哪个 Stage 值自动尝试过"去重(而非"是否收到过 Stage 事件")——
    // 可用性探测在真机上可能同步完成,单次 Stage 赋值仍会连续触发多个 PropertyChanged
    // (NotifyStageProperties 里的 ShowHelloUnlock、探测完成后的 ShowHelloUnlock、最后的 Stage 本身),
    // 若按"收到 Stage 事件就重置"会在同一次进入解锁阶段时重复弹出手势。离开解锁阶段则清空,
    // 下次重新进入(含切换 PIN/主密码)按新 Stage 值再次自动尝试。
    private void TryAutoPromptHello()
    {
        if (!(ViewModel.ShowUnlockPanel || ViewModel.IsPinUnlockStage))
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
        or nameof(LoginViewModel.ShowUnlockPanel)
        or nameof(LoginViewModel.ShowCustomServerUrl)
        or nameof(LoginViewModel.ShowHelloUnlock)
        or nameof(LoginViewModel.HasStatus)
        or nameof(LoginViewModel.Status)
        or nameof(LoginViewModel.IsBusy)
        or nameof(LoginViewModel.FormTitle)
        or nameof(LoginViewModel.FormSubtitle)
        or nameof(LoginViewModel.StepText)
        or nameof(LoginViewModel.PrimaryButtonText)
        or nameof(LoginViewModel.IsSsoPending)
        or nameof(LoginViewModel.ShowCancelSso)
        or nameof(LoginViewModel.ShowSavedAccounts)
        or nameof(LoginViewModel.SavedAccounts)
        or nameof(LoginViewModel.IsLoginWithDeviceAvailable)
        or nameof(LoginViewModel.ShowLoginWithDeviceQuarantineNotice)
        or nameof(LoginViewModel.ShowLoginWithDeviceQuarantineClearAction)
        or nameof(LoginViewModel.LoginWithDeviceQuarantineTitle)
        or nameof(LoginViewModel.LoginWithDeviceQuarantineMessage)
        or nameof(LoginViewModel.ShowLoginWithDeviceOption)
        or nameof(LoginViewModel.IsLoginWithDeviceStage)
        or nameof(LoginViewModel.HasLoginWithDeviceFingerprint)
        or nameof(LoginViewModel.LoginWithDeviceFingerprint)
        or nameof(LoginViewModel.HasLoginWithDeviceExpiry)
        or nameof(LoginViewModel.LoginWithDeviceExpiryText)
        or nameof(LoginViewModel.ShowLoginWithDeviceTwoFactorExpiry)
        or nameof(LoginViewModel.ShowCancelLoginWithDevice)
        or nameof(LoginViewModel.ShowPrimaryAction)
        or nameof(LoginViewModel.IsAccountDeletionRecoveryStage)
        or nameof(LoginViewModel.AccountDeletionRecoveryNotice)
        or nameof(LoginViewModel.AccountDeletionRecoveryTitle)
        or nameof(LoginViewModel.AccountDeletionRecoveryMessage)
        or nameof(LoginViewModel.AccountDeletionRecoveryContinueText)
        or nameof(LoginViewModel.ShowClearPendingAccountDeletion)
        or nameof(LoginViewModel.ShowRetryConfirmedAccountDeletionCleanup)
        or nameof(LoginViewModel.ShowFullLocalResetAction)
        or nameof(LoginViewModel.CanRequestFullLocalReset)
        or nameof(LoginViewModel.HasApprovalFingerprint)
        or nameof(LoginViewModel.ShowCancelDeviceApproval);

    private void OnGoToRegisterClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEditServer)
            return;
        global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>().GoToRegister();
    }

    private void OnSavedAccountClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SavedAccountOption account)
            ViewModel.SelectSavedAccountCommand.Execute(account);
    }
}

public sealed partial class LoginStatusKindToInfoBarSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value switch
        {
            LoginStatusKind.Success => InfoBarSeverity.Success,
            LoginStatusKind.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
