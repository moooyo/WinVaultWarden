using App.Services;
using App.ViewModels;
using Core.Abstractions;
using Core.Services;
using Core.Session;
using System.Security.Cryptography;
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
    private bool _isTwoFactorPickerOpen;
    private bool _preferHelloUnlock = true;
    private bool _isSsoFormOpen;
    private bool _closeSsoAfterCancellation;
    private bool _initializingPreferenceControls = true;
    private bool _onlineAuthenticationStarted;
    private VaultSessionLease? _helloEnrollmentLease;
    private CancellationTokenSource? _helloEnrollmentCancellation;
    private long _helloEnrollmentGeneration;
    private bool _helloEnrollmentBusy;
    private bool _canEnrollHello;
    private readonly DispatcherTimer _deviceApprovalTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _isDeactivated;
    private bool _isQuarantineConfirmationOpen;
    private bool _isAccountDeletionConfirmationOpen;
    private bool _isFullLocalResetConfirmationOpen;
    private CancellationTokenSource? _fullLocalResetConfirmationCancellation;
    private LoginStage? _helloAutoPromptedForStage;

    public LoginPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<LoginViewModel>();
        ViewModel.SetSuccessHandler(TryAcceptSuccessfulSession);

        var tokenStore = global::App.App.Services.GetRequiredService<ITokenStore>();
        if (tokenStore.TryLoad(out var session))
            ViewModel.PrepareInitialLoginState(session);
        else
            ViewModel.PrepareInitialLoginState(activeSession: null);

        InitializeComponent();
        RestoreRememberedAccount();
        RememberEmailBox.IsChecked = AppPreferences.Current.RememberEmail;
        _initializingPreferenceControls = false;
        Loaded += OnLoaded;
        LoginForm.SizeChanged += OnLoginFormSizeChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _deviceApprovalTimer.Tick += OnDeviceApprovalTimerTick;
        UpdateAuthPresentation();
    }

    private async void OnLogin(object sender, RoutedEventArgs e) => await SubmitAuthenticationAsync();

    private async Task SubmitAuthenticationAsync()
    {
        var wasAccountStage = ViewModel.IsAccountStage;
        if (wasAccountStage)
            SaveRememberedAccount();
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
        UpdateAuthPresentation();
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
        ResetPasswordReveal();
        _helloEnrollmentGeneration++;
        _helloEnrollmentLease = null;
        _helloEnrollmentCancellation?.Cancel();
        _helloEnrollmentCancellation?.Dispose();
        _helloEnrollmentCancellation = null;
        _deviceApprovalTimer.Stop();
        _deviceApprovalTimer.Tick -= OnDeviceApprovalTimerTick;
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
        if (e.PropertyName == nameof(LoginViewModel.Stage))
        {
            ResetPasswordReveal();
            if (!ViewModel.IsTwoFactorStage)
            {
                _isTwoFactorPickerOpen = false;
                TwoFactorPickerItems.ItemsSource = null;
            }
        }
        if (e.PropertyName is nameof(LoginViewModel.Stage) or nameof(LoginViewModel.IsSsoPending)
            or nameof(LoginViewModel.IsSsoBrowserOpened) or nameof(LoginViewModel.Status))
            UpdateAuthModePresentation();
        if (e.PropertyName == nameof(LoginViewModel.Stage) && ViewModel.IsAccountStage)
            _onlineAuthenticationStarted = false;
        if (ViewModel.IsPasswordStage || ViewModel.IsLoginWithDeviceStage || ViewModel.IsTwoFactorStage
            || ViewModel.IsDeviceVerificationStage || ViewModel.IsSsoPending)
            _onlineAuthenticationStarted = true;
        if (IsLayoutAffectingProperty(e.PropertyName)
            || e.PropertyName is nameof(LoginViewModel.MasterPassword) or nameof(LoginViewModel.Pin))
        {
            UpdateAuthPresentation();
            QueueFitWindowToContent();
        }

        if (e.PropertyName is nameof(LoginViewModel.Stage) or nameof(LoginViewModel.ShowHelloUnlock))
            TryAutoPromptHello();

        if (e.PropertyName == nameof(LoginViewModel.FocusRequestId))
        {
            UpdateAuthPresentation();
            DispatcherQueue.TryEnqueue(ApplyFocusRequest);
        }
    }

    // 把 VM 的抽象焦点目标翻译为当前阶段可见的具体控件;解锁阶段主密码目标对应 UnlockPasswordBox。
    private void ApplyFocusRequest()
    {
        if (_isTwoFactorPickerOpen)
        {
            FocusTwoFactorPicker();
            return;
        }
        if (_isSsoFormOpen)
        {
            _ = (ViewModel.IsSsoPending ? (Control)SsoBackButton : SsoOrganizationBox).Focus(FocusState.Programmatic);
            return;
        }
        if (_helloEnrollmentLease is not null)
        {
            _ = (_canEnrollHello ? EnableHelloEnrollmentButton : SkipHelloEnrollmentButton).Focus(FocusState.Programmatic);
            return;
        }
        if (_preferHelloUnlock && ViewModel.ShowHelloUnlock)
        {
            _ = HelloUnlockButton.Focus(FocusState.Programmatic);
            return;
        }

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
        if (_helloEnrollmentLease is not null || _isTwoFactorPickerOpen)
            return;
        if (_isSsoFormOpen)
        {
            if (!ViewModel.IsSsoPending && SsoOrganizationBox.FocusState != FocusState.Unfocused)
            {
                args.Handled = true;
                await ContinueSsoAsync();
            }
            return;
        }
        if (!ViewModel.CanUsePrimaryAction)
            return;
        var focusedElement = FocusManager.GetFocusedElement(XamlRoot);
        if (focusedElement is not TextBox and not PasswordBox)
            return;
        if (_preferHelloUnlock && ViewModel.ShowHelloUnlock)
        {
            args.Handled = true;
            await ViewModel.UnlockWithHelloCommand.ExecuteAsync(null);
            return;
        }
        if ((ViewModel.IsUnlockStage && string.IsNullOrWhiteSpace(ViewModel.MasterPassword))
            || (ViewModel.IsPinUnlockStage && string.IsNullOrWhiteSpace(ViewModel.Pin)))
        {
            return;
        }
        args.Handled = true;
        await SubmitAuthenticationAsync();
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
            _preferHelloUnlock = true;
            _helloAutoPromptedForStage = null;
            return;
        }

        var stage = ViewModel.Stage;
        if (!_preferHelloUnlock
            || _helloAutoPromptedForStage == stage || !ViewModel.ShowHelloUnlock)
            return;

        _helloAutoPromptedForStage = stage;
        _ = ViewModel.UnlockWithHelloCommand.ExecuteAsync(null);
    }

    private void RestoreRememberedAccount()
    {
        var preferences = AppPreferences.Current;
        if (ViewModel.IsAccountStage && string.IsNullOrWhiteSpace(ViewModel.Email)
            && preferences.RememberEmail && !string.IsNullOrWhiteSpace(preferences.RememberedLoginEmail))
        {
            ViewModel.PrepareAccount(
                string.IsNullOrWhiteSpace(preferences.RememberedLoginServerUrl)
                    ? ViewModel.ServerUrl : preferences.RememberedLoginServerUrl,
                preferences.RememberedLoginEmail);
        }
    }

    private void OnRememberEmailChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializingPreferenceControls)
            SaveRememberedAccount();
    }

    private void OnAccountIdentityLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_initializingPreferenceControls && ViewModel.IsAccountStage)
            SaveRememberedAccount();
    }

    private void SaveRememberedAccount()
    {
        var preferences = AppPreferences.Current;
        preferences.RememberEmail = RememberEmailBox.IsChecked == true;
        preferences.RememberedLoginEmail = preferences.RememberEmail ? ViewModel.Email.Trim() : string.Empty;
        preferences.RememberedLoginServerUrl = string.Empty;
        if (preferences.RememberEmail)
        {
            try
            {
                if (Core.Models.ServerEndpoint.TryParse(ViewModel.ServerUrl, out var endpoint))
                    preferences.RememberedLoginServerUrl = endpoint.Value;
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException)
            {
            }
        }
        AppPreferences.Save();
    }

    private bool TryAcceptSuccessfulSession(VaultSessionLease lease)
    {
        if (_isDeactivated || App.MainWindow is not { } window)
            return false;
        var services = global::App.App.Services;
        var tokenStore = services.GetRequiredService<ITokenStore>();
        var helloUnlock = services.GetRequiredService<IHelloUnlockService>();
        var hasMatchingSavedSession = tokenStore.TryLoad(out var persisted)
            && persisted.AccountId == lease.AccountId && persisted.Endpoints == lease.Endpoints
            && persisted.Generation == lease.Generation;
        if (!_onlineAuthenticationStarted || !hasMatchingSavedSession || helloUnlock.IsHelloSet)
            return window.ShowVault(lease);

        return window.ShowHelloEnrollment(lease, () => PrepareHelloEnrollment(lease));
    }

    private void PrepareHelloEnrollment(VaultSessionLease lease)
    {
        _helloEnrollmentLease = lease;
        var generation = ++_helloEnrollmentGeneration;
        _helloEnrollmentCancellation?.Cancel();
        _helloEnrollmentCancellation?.Dispose();
        _helloEnrollmentCancellation = CancellationTokenSource.CreateLinkedTokenSource(lease.CancellationToken);
        var cancellation = _helloEnrollmentCancellation;
        _canEnrollHello = false;
        _helloEnrollmentBusy = false;
        LoginForm.Visibility = Visibility.Collapsed;
        HelloEnrollmentPanel.Visibility = Visibility.Visible;
        _isSsoFormOpen = false;
        _isTwoFactorPickerOpen = false;
        UpdateAuthModePresentation();
        UpdateHelloEnrollmentButtons();
        DispatcherQueue.TryEnqueue(() => _ = ProbeHelloEnrollmentAsync(lease, generation, cancellation));
        _ = SkipHelloEnrollmentButton.Focus(FocusState.Programmatic);
    }

    private bool IsCurrentHelloEnrollment(VaultSessionLease lease, long generation, CancellationTokenSource cancellation) =>
        !_isDeactivated && generation == _helloEnrollmentGeneration
        && ReferenceEquals(_helloEnrollmentCancellation, cancellation)
        && _helloEnrollmentLease?.SameSessionAs(lease) == true && !cancellation.IsCancellationRequested;

    private async Task ProbeHelloEnrollmentAsync(VaultSessionLease lease, long generation, CancellationTokenSource cancellation)
    {
        var services = global::App.App.Services;
        var session = services.GetRequiredService<Vault.VaultSession>();
        var available = await services.GetRequiredService<IWindowsHelloAuthenticator>().IsAvailableAsync();
        if (!IsCurrentHelloEnrollment(lease, generation, cancellation))
            return;
        services.GetRequiredService<IAppLifecycleCoordinator>().TryRunSensitive(() => session.TryRunIfUnlocked(lease, () =>
        {
            if (!IsCurrentHelloEnrollment(lease, generation, cancellation))
                return;
            _canEnrollHello = available && !session.UsesV2UserKey;
            if (session.UsesV2UserKey)
                SetHelloEnrollmentStatus("此账户使用新版加密密钥，目前需要用主密码解锁。可以跳过此步骤。", InfoBarSeverity.Informational);
            else if (!available)
                SetHelloEnrollmentStatus("请先在 Windows 设置中配置 Windows Hello。也可以以后再开启。", InfoBarSeverity.Informational);
            UpdateHelloEnrollmentButtons();
        }));
    }

    private async void OnEnableHelloEnrollmentClick(object sender, RoutedEventArgs e) => await EnableHelloEnrollmentAsync();

    private async Task EnableHelloEnrollmentAsync()
    {
        if (_helloEnrollmentBusy || !_canEnrollHello || _helloEnrollmentLease is not { } lease
            || _helloEnrollmentCancellation is not { } cancellation)
            return;
        var generation = _helloEnrollmentGeneration;
        if (!IsCurrentHelloEnrollment(lease, generation, cancellation))
            return;
        _helloEnrollmentBusy = true;
        UpdateHelloEnrollmentButtons();
        byte[]? signature = null;
        var challenge = RandomNumberGenerator.GetBytes(32);
        try
        {
            var services = global::App.App.Services;
            var lifecycle = services.GetRequiredService<IAppLifecycleCoordinator>();
            var session = services.GetRequiredService<Vault.VaultSession>();
            Task<byte[]?>? ceremony = null;
            lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(lease, () =>
            {
                if (IsCurrentHelloEnrollment(lease, generation, cancellation) && !session.UsesV2UserKey)
                    ceremony = services.GetRequiredService<IWindowsHelloAuthenticator>()
                        .CreateProtectorAsync(challenge, cancellation.Token);
            }));
            if (ceremony is null)
                return;
            signature = await ceremony;
            if (!IsCurrentHelloEnrollment(lease, generation, cancellation))
                return;
            if (signature is null)
            {
                SetHelloEnrollmentStatus("Windows Hello 未完成。可以重试，或以后再说。", InfoBarSeverity.Informational);
                return;
            }

            var committed = false;
            lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(lease, () =>
            {
                if (!IsCurrentHelloEnrollment(lease, generation, cancellation))
                    return;
                services.GetRequiredService<IHelloUnlockService>().SetHello(signature, challenge);
                committed = true;
            }));
            if (committed && IsCurrentHelloEnrollment(lease, generation, cancellation))
                await CompleteHelloEnrollmentAsync(lease);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested || lease.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (IsCurrentHelloEnrollment(lease, generation, cancellation))
                SetHelloEnrollmentStatus("无法开启 Windows Hello。可以重试，或以后再说。", InfoBarSeverity.Error);
        }
        finally
        {
            if (signature is not null)
                CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(challenge);
            if (IsCurrentHelloEnrollment(lease, generation, cancellation))
            {
                _helloEnrollmentBusy = false;
                UpdateHelloEnrollmentButtons();
            }
        }
    }

    private async void OnSkipHelloEnrollmentClick(object sender, RoutedEventArgs e)
    {
        if (!_helloEnrollmentBusy && _helloEnrollmentLease is { } lease
            && _helloEnrollmentCancellation is { } cancellation
            && IsCurrentHelloEnrollment(lease, _helloEnrollmentGeneration, cancellation))
            await CompleteHelloEnrollmentAsync(lease);
    }

    private async Task CompleteHelloEnrollmentAsync(VaultSessionLease lease)
    {
        _helloEnrollmentBusy = true;
        UpdateHelloEnrollmentButtons();
        var accepted = App.MainWindow?.ShowVault(lease) == true;
        _helloEnrollmentGeneration++;
        _helloEnrollmentLease = null;
        _helloEnrollmentCancellation?.Cancel();
        _helloEnrollmentCancellation?.Dispose();
        _helloEnrollmentCancellation = null;
        if (!accepted)
        {
            var services = global::App.App.Services;
            var session = services.GetRequiredService<Vault.VaultSession>();
            await services.GetRequiredService<IAppLifecycleCoordinator>()
                .RequestIfAsync(AppLifecycleIntent.Lock, () => session.IsUnlocked(lease));
        }
    }

    private void UpdateHelloEnrollmentButtons()
    {
        EnableHelloEnrollmentButton.IsEnabled = _canEnrollHello && !_helloEnrollmentBusy;
        SkipHelloEnrollmentButton.IsEnabled = !_helloEnrollmentBusy;
        HelloEnrollmentButtonText.Text = _helloEnrollmentBusy ? "正在开启…" : "开启 Windows Hello";
    }

    private void SetHelloEnrollmentStatus(string message, InfoBarSeverity severity)
    {
        HelloEnrollmentStatus.Message = message;
        HelloEnrollmentStatus.Severity = severity;
        HelloEnrollmentStatus.IsOpen = true;
        HelloEnrollmentStatus.Visibility = Visibility.Visible;
    }

    private void OnUseMasterPasswordClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBusy)
            return;

        _preferHelloUnlock = false;
        UpdateAuthPresentation();
        DispatcherQueue.TryEnqueue(ApplyFocusRequest);
    }

    private void OnUsePinClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUsePinUnlock)
            return;

        _preferHelloUnlock = false;
        UpdateAuthPresentation();
        DispatcherQueue.TryEnqueue(ApplyFocusRequest);
    }

    private string BackActionText(bool isPasswordStage) => isPasswordStage ? "不是你？" : "返回";

    private Visibility NavigationLinksVisibility(bool isPasswordStage, bool canGoBack) =>
        isPasswordStage || canGoBack ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateAuthModePresentation()
    {
        if (_isSsoFormOpen && !ViewModel.IsSsoPending
            && (_closeSsoAfterCancellation || !ViewModel.IsAccountStage))
        {
            _isSsoFormOpen = false;
            _closeSsoAfterCancellation = false;
        }
        var enrollment = _helloEnrollmentLease is not null;
        LoginForm.Visibility = !enrollment && !_isSsoFormOpen && !_isTwoFactorPickerOpen
            ? Visibility.Visible : Visibility.Collapsed;
        HelloEnrollmentPanel.Visibility = enrollment ? Visibility.Visible : Visibility.Collapsed;
        SsoSignInPanel.Visibility = !enrollment && _isSsoFormOpen ? Visibility.Visible : Visibility.Collapsed;
        TwoFactorPickerPanel.Visibility = !enrollment && !_isSsoFormOpen && _isTwoFactorPickerOpen
            ? Visibility.Visible : Visibility.Collapsed;
        if (!_isSsoFormOpen)
            return;
        var pending = ViewModel.IsSsoPending;
        SsoOrganizationForm.Visibility = pending ? Visibility.Collapsed : Visibility.Visible;
        SsoWaitingRow.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        SsoContinueButton.Visibility = pending ? Visibility.Collapsed : Visibility.Visible;
        SsoReopenButton.Visibility = pending && ViewModel.IsSsoBrowserOpened ? Visibility.Visible : Visibility.Collapsed;
        SsoBackButton.Content = pending ? "取消" : "返回";
        SsoSubtitle.Text = pending && ViewModel.IsSsoBrowserOpened
            ? "已在浏览器中打开组织的登录页面，完成后会自动回到这里"
            : "输入组织标识符，会在浏览器中打开组织的登录页面";
        SsoWaitingText.Text = ViewModel.IsSsoBrowserOpened ? "正在等待浏览器中的登录…" : "正在打开浏览器…";
        SsoStatusInfoBar.IsOpen = ViewModel.HasStatus && ViewModel.StatusKind == LoginStatusKind.Error;
        SsoStatusInfoBar.Visibility = SsoStatusInfoBar.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        SsoStatusInfoBar.Severity = InfoBarSeverity.Error;
        SsoStatusInfoBar.Message = ViewModel.Status;
    }

    private void OnOpenSsoSignInClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanUseSso || !ViewModel.PrepareSsoSignIn())
            return;
        SaveRememberedAccount();
        _isSsoFormOpen = true;
        _closeSsoAfterCancellation = false;
        SsoOrganizationError.Visibility = Visibility.Collapsed;
        UpdateAuthModePresentation();
        DispatcherQueue.TryEnqueue(() => _ = SsoOrganizationBox.Focus(FocusState.Programmatic));
    }

    private async void OnSsoContinueClick(object sender, RoutedEventArgs e) => await ContinueSsoAsync();

    private async Task ContinueSsoAsync()
    {
        if (!_isSsoFormOpen || ViewModel.IsBusy)
            return;
        var missingIdentifier = string.IsNullOrWhiteSpace(ViewModel.SsoOrganizationIdentifier);
        SsoOrganizationError.Visibility = missingIdentifier ? Visibility.Visible : Visibility.Collapsed;
        if (missingIdentifier)
        {
            SsoOrganizationBox.BorderBrush = SsoOrganizationError.Foreground;
            SsoOrganizationBox.BorderThickness = new Thickness(1, 1, 1, 2);
        }
        else
        {
            SsoOrganizationBox.ClearValue(Control.BorderBrushProperty);
            SsoOrganizationBox.ClearValue(Control.BorderThicknessProperty);
        }
        if (missingIdentifier)
        {
            _ = SsoOrganizationBox.Focus(FocusState.Programmatic);
            return;
        }
        await ViewModel.LoginWithSsoCommand.ExecuteAsync(null);
        UpdateAuthModePresentation();
        if (!_isDeactivated && !_isSsoFormOpen)
            ViewModel.RequestStageFocus();
    }

    private async void OnSsoReopenClick(object sender, RoutedEventArgs e)
    {
        SsoReopenButton.IsEnabled = false;
        try { await ViewModel.ReopenSsoBrowserAsync(); }
        finally { if (!_isDeactivated) SsoReopenButton.IsEnabled = true; }
    }

    private void OnSsoBackClick(object sender, RoutedEventArgs e) => LeaveSsoSignIn();

    private void LeaveSsoSignIn()
    {
        if (ViewModel.IsSsoPending)
        {
            _closeSsoAfterCancellation = true;
            ViewModel.CancelSsoCommand.Execute(null);
            return;
        }
        _isSsoFormOpen = false;
        ViewModel.SsoOrganizationIdentifier = string.Empty;
        UpdateAuthModePresentation();
        ViewModel.RequestStageFocus();
    }

    private void OnAuthBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_helloEnrollmentLease is not null)
            return;
        if (_isTwoFactorPickerOpen)
        {
            args.Handled = true;
            CloseTwoFactorPicker();
            ViewModel.RequestStageFocus();
        }
        else if (_isSsoFormOpen)
        {
            args.Handled = true;
            LeaveSsoSignIn();
        }
        else if (ViewModel.CanGoBack)
        {
            args.Handled = true;
            ViewModel.BackCommand.Execute(null);
        }
    }

    private void OnPasswordRevealChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Primitives.ToggleButton button
            || button.Tag is not string targetName || FindName(targetName) is not PasswordBox password)
            return;
        var visible = button.IsChecked == true;
        password.PasswordRevealMode = visible ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        var label = visible ? "隐藏主密码" : "显示主密码";
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        if (button.Content is FontIcon icon)
            icon.Glyph = visible ? "\uED1A" : "\uE890";
    }

    private void ResetPasswordReveal()
    {
        RevealMasterPasswordBoxButton.IsChecked = false;
        RevealUnlockPasswordBoxButton.IsChecked = false;
    }

    private void OnOpenTwoFactorPickerClick(object sender, RoutedEventArgs e)
    {
        if (_isDeactivated || !ViewModel.IsTwoFactorStage || ViewModel.IsBusy
            || _helloEnrollmentLease is not null)
            return;

        TwoFactorPickerItems.ItemsSource = ViewModel.AvailableTwoFactorProviderOptions
            .DistinctBy(option => option.Type)
            .OrderBy(option => option.Type == 8 ? 1 : 0)
            .Select(CreateTwoFactorPickerOption)
            .ToArray();
        _isTwoFactorPickerOpen = true;
        UpdateAuthModePresentation();
        DispatcherQueue.TryEnqueue(FocusTwoFactorPicker);
    }

    private void OnCloseTwoFactorPickerClick(object sender, RoutedEventArgs e)
    {
        if (!_isTwoFactorPickerOpen)
            return;
        CloseTwoFactorPicker();
        if (!_isDeactivated && ViewModel.IsTwoFactorStage)
            ViewModel.RequestStageFocus();
    }

    private void CloseTwoFactorPicker()
    {
        _isTwoFactorPickerOpen = false;
        TwoFactorPickerItems.ItemsSource = null;
        UpdateAuthModePresentation();
    }

    private void FocusTwoFactorPicker()
    {
        if (!_isTwoFactorPickerOpen || _isDeactivated)
            return;
        TwoFactorPickerItems.UpdateLayout();
        var firstOption = FindTwoFactorPickerButton(TwoFactorPickerItems);
        _ = (firstOption as Control ?? TwoFactorPickerBackButton).Focus(FocusState.Programmatic);
    }

    private static Button? FindTwoFactorPickerButton(DependencyObject root)
    {
        for (var index = 0; index < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, index);
            if (child is Button button)
                return button;
            if (FindTwoFactorPickerButton(child) is { } nested)
                return nested;
        }
        return null;
    }

    private void OnTwoFactorPickerOptionClick(object sender, RoutedEventArgs e)
    {
        if (!_isTwoFactorPickerOpen || _isDeactivated || !ViewModel.IsTwoFactorStage || ViewModel.IsBusy
            || (sender as FrameworkElement)?.DataContext is not TwoFactorPickerOption option
            || !ViewModel.AvailableTwoFactorProviderOptions.Any(available => available.Type == option.Type))
            return;

        var useRecovery = option.Type == 8;
        if (ViewModel.IsRecoveryCodeMode != useRecovery)
            ViewModel.ToggleRecoveryCodeModeCommand.Execute(null);
        if (!useRecovery)
        {
            var index = ViewModel.TwoFactorProviderOptions
                .Select((provider, index) => (provider.Type, Index: index))
                .Where(provider => provider.Type == option.Type)
                .Select(provider => provider.Index)
                .DefaultIfEmpty(-1)
                .First();
            if (index < 0)
                return;
            ViewModel.SelectedTwoFactorProviderIndex = index;
        }
        ViewModel.TwoFactorCode = string.Empty;
        CloseTwoFactorPicker();
        ViewModel.RequestStageFocus();
    }

    private static TwoFactorPickerOption CreateTwoFactorPickerOption(TwoFactorProviderOption option) =>
        option.Type switch
        {
            0 => new(option.Type, "身份验证器应用", "打开身份验证器应用，输入 6 位验证码", "\uE823"),
            1 => new(option.Type, "电子邮件", "使用发送到邮箱的一次性验证码", "\uE715"),
            2 => new(option.Type, "Duo", "在浏览器中完成验证", "\uEA18"),
            3 => new(option.Type, "YubiKey OTP", "连接 YubiKey，输入一次性密码", "\uE88E"),
            6 => new(option.Type, "组织 Duo", "在浏览器中完成组织要求的验证", "\uEA18"),
            7 => new(option.Type, "通行密钥或安全密钥", "插入安全密钥，或者用 Windows Hello 验证", "\uE88E"),
            8 => new(option.Type, "恢复代码", "丢失所有验证方式时使用", "\uE8D7"),
            _ => new(option.Type, option.DisplayName, "使用账户启用的验证方式", "\uEA18"),
        };

    private Visibility UnlockPanelVisibility(bool visible, bool helloAvailable) =>
        visible && !(_preferHelloUnlock && helloAvailable)
            ? Visibility.Visible : Visibility.Collapsed;

    private bool PrimaryActionEnabled(bool canUsePrimaryAction, string password, string pin) =>
        canUsePrimaryAction
        && (!ViewModel.IsUnlockStage || !string.IsNullOrWhiteSpace(password))
        && (!ViewModel.IsPinUnlockStage || !string.IsNullOrWhiteSpace(pin));

    private void UpdateAuthPresentation()
    {
        if (_helloEnrollmentLease is not null)
            return;
        UpdateAuthModePresentation();
        var isLocked = ViewModel.ShowUnlockPanel || ViewModel.IsPinUnlockStage;
        var helloPrimary = isLocked && _preferHelloUnlock && ViewModel.ShowHelloUnlock;
        LoginForm.MaxWidth = isLocked ? 320 : 360;
        var usesAppIcon = ViewModel.IsAccountStage || ViewModel.IsPasswordStage;
        LoginAppIcon.Visibility = !isLocked && usesAppIcon ? Visibility.Visible : Visibility.Collapsed;
        LoginStepIcon.Visibility = !isLocked && !usesAppIcon ? Visibility.Visible : Visibility.Collapsed;
        LoginStepGlyph.Glyph = ViewModel.Stage switch
        {
            LoginStage.LoginWithDevice or LoginStage.TrustedDeviceApproval => "\uE8F2",
            LoginStage.TwoFactor or LoginStage.DeviceVerification => "\uEA18",
            _ => "\uE72E",
        };
        LoginAvatar.Visibility = isLocked ? Visibility.Visible : Visibility.Collapsed;
        LoginSubtitle.Margin = new Thickness(0, isLocked ? 2 : 4, 0, 0);
        LoginUnlockStage.Visibility = ViewModel.ShowUnlockPanel && !helloPrimary
            ? Visibility.Visible : Visibility.Collapsed;
        LoginPinUnlockStage.Visibility = ViewModel.IsPinUnlockStage && !helloPrimary
            ? Visibility.Visible : Visibility.Collapsed;
        HelloUnlockButton.Visibility = helloPrimary ? Visibility.Visible : Visibility.Collapsed;
        PrimaryAuthButton.Visibility = ViewModel.ShowPrimaryAction && !helloPrimary
            ? Visibility.Visible : Visibility.Collapsed;
        PrimaryAuthButton.Margin = new Thickness(0, isLocked ? 8 : 16, 0, 0);
        PrimaryAuthButton.IsEnabled = ViewModel.CanUsePrimaryAction
            && (!ViewModel.IsUnlockStage || !string.IsNullOrWhiteSpace(ViewModel.MasterPassword))
            && (!ViewModel.IsPinUnlockStage || !string.IsNullOrWhiteSpace(ViewModel.Pin));
        UnlockAlternatives.Visibility = isLocked ? Visibility.Visible : Visibility.Collapsed;
        UseMasterPasswordButton.Visibility = isLocked && (helloPrimary || ViewModel.IsPinUnlockStage)
            ? Visibility.Visible : Visibility.Collapsed;
        UsePinUnlockButton.Visibility = isLocked && ViewModel.IsPinUnlockAvailable
            && (helloPrimary || !ViewModel.IsPinUnlockStage) ? Visibility.Visible : Visibility.Collapsed;
        PinHelloUnlockButton.Visibility = isLocked && ViewModel.ShowHelloUnlock && !helloPrimary
            ? Visibility.Visible : Visibility.Collapsed;
        LockNote.Visibility = isLocked ? Visibility.Visible : Visibility.Collapsed;
        SwitchAccountLinks.Visibility = isLocked ? Visibility.Visible : Visibility.Collapsed;
        if (ViewModel.IsLoginWithDeviceStage)
        {
            UpdateDeviceApprovalCountdown();
            _deviceApprovalTimer.Start();
        }
        else
        {
            _deviceApprovalTimer.Stop();
        }
        if (isLocked)
            LockNote.Text = DescribeLockPolicy();

        var inlineError = UpdateInlineAuthError();
        var redundantUnlockPrompt = isLocked && ViewModel.StatusKind == LoginStatusKind.Info
            && ViewModel.Status is "请输入 PIN 解锁。" or "请输入主密码解锁。";
        LoginStatusInfoBar.Visibility = ViewModel.HasStatus && !redundantUnlockPrompt && !inlineError
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool UpdateInlineAuthError()
    {
        (Control Input, TextBlock Error)[] fields =
        [
            (ServerUrlTextBox, ServerErrorText),
            (EmailTextBox, EmailErrorText),
            (MasterPasswordBox, MasterPasswordErrorText),
            (UnlockPasswordBox, UnlockPasswordErrorText),
            (PinBox, PinErrorText),
            (TwoFactorCodeTextBox, TwoFactorErrorText),
            (DeviceVerificationCodeTextBox, DeviceVerificationErrorText),
        ];
        foreach (var field in fields)
        {
            field.Input.ClearValue(Control.BorderBrushProperty);
            field.Input.ClearValue(Control.BorderThicknessProperty);
            field.Error.Text = string.Empty;
            field.Error.Visibility = Visibility.Collapsed;
        }
        ServerUrlHelper.Visibility = ViewModel.ShowCustomServerUrl ? Visibility.Visible : Visibility.Collapsed;
        var message = ViewModel.Status;
        if (ViewModel.StatusKind != LoginStatusKind.Error
            || (_preferHelloUnlock && ViewModel.ShowHelloUnlock)
            || !(message.StartsWith("请输入", StringComparison.Ordinal)
                || message.StartsWith("主密码错误", StringComparison.Ordinal)
                || message.StartsWith("用户名或主密码错误", StringComparison.Ordinal)
                || message.StartsWith("验证码无效", StringComparison.Ordinal)
                || message.StartsWith("恢复代码无效", StringComparison.Ordinal)
                || message.StartsWith("PIN ", StringComparison.Ordinal)))
        {
            return false;
        }

        var target = ViewModel.FocusTarget switch
        {
            LoginFocusTarget.ServerUrl => 0,
            LoginFocusTarget.Email => 1,
            LoginFocusTarget.MasterPassword when ViewModel.IsUnlockStage => 3,
            LoginFocusTarget.MasterPassword when ViewModel.IsPasswordStage => 2,
            LoginFocusTarget.Pin => 4,
            LoginFocusTarget.TwoFactorCode => 5,
            LoginFocusTarget.DeviceVerificationCode => 6,
            _ => -1,
        };
        if (target < 0)
            return false;

        var selected = fields[target];
        selected.Input.BorderBrush = selected.Error.Foreground;
        selected.Input.BorderThickness = new Thickness(1, 1, 1, 2);
        selected.Error.Text = message;
        selected.Error.Visibility = Visibility.Visible;
        if (target == 0)
            ServerUrlHelper.Visibility = Visibility.Collapsed;
        return true;
    }

    private void OnDeviceApprovalTimerTick(object? sender, object e) => UpdateDeviceApprovalCountdown();

    private void UpdateDeviceApprovalCountdown()
    {
        if (ViewModel.LoginWithDeviceExpiresAtUtc is not { } expiresAt)
        {
            DeviceApprovalWaitingText.Text = "正在等待批准";
            return;
        }

        var seconds = Math.Max(0, (int)Math.Ceiling((expiresAt - DateTimeOffset.UtcNow).TotalSeconds));
        DeviceApprovalWaitingText.Text = seconds > 0
            ? $"正在等待批准 · {seconds / 60}:{seconds % 60:00} 后过期"
            : "请求已过期";
    }

    private static string DescribeLockPolicy()
    {
        var preferences = AppPreferences.Current;
        var vault = global::App.App.Services.GetRequiredService<IVaultService>();
        var timeout = IdleTimeoutPolicy.Resolve(
            vault.Snapshot, preferences.SessionTimeoutIndex, preferences.TimeoutActionIndex);
        var action = timeout.Action == VaultTimeoutAction.Logout ? "退出登录" : "锁定";
        return timeout.Trigger switch
        {
            VaultTimeoutTrigger.AfterIdle => $"闲置 {timeout.Minutes} 分钟后会自动{action}",
            VaultTimeoutTrigger.OnAppRestart => $"重启应用后会{action}",
            VaultTimeoutTrigger.OnSystemLock => $"锁定 Windows 时会{action}",
            _ => "不会自动锁定",
        };
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
        global::App.App.Services.GetRequiredService<AccountPrefill>()
            .Set(ViewModel.ServerUrl, ViewModel.Email);
        global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>().GoToRegister();
    }

    private void OnSavedAccountClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SavedAccountOption account)
            ViewModel.SelectSavedAccountCommand.Execute(account);
    }
}

public sealed record TwoFactorPickerOption(int Type, string DisplayName, string Description, string Glyph);

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
