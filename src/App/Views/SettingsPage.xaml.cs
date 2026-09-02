using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    private bool _suppressPinToggle;
    private bool _suppressHelloToggle;
    private CancellationTokenSource? _accountDeletionCts;

    public SettingsPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        Unloaded += OnSettingsPageUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        ViewModel.ApplyPolicyRestrictions();
        _suppressPinToggle = true;
        PinToggle.IsOn = ViewModel.IsPinSet;
        PinToggle.IsEnabled = ViewModel.CanUsePinUnlock;
        _suppressPinToggle = false;

        _suppressHelloToggle = true;
        HelloToggle.IsOn = ViewModel.IsWindowsHelloEnabled;
        _suppressHelloToggle = false;

        _ = LoadWindowsHelloAvailabilityAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        CancelAccountDeletion();
        base.OnNavigatedFrom(e);
    }

    private void OnSettingsPageUnloaded(object sender, RoutedEventArgs e) =>
        CancelAccountDeletion();

    private void CancelAccountDeletion()
    {
        try
        {
            Volatile.Read(ref _accountDeletionCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void RefreshPolicyRestrictions() => ViewModel.ApplyPolicyRestrictions();

    private async Task LoadWindowsHelloAvailabilityAsync()
    {
        await ViewModel.RefreshWindowsHelloAvailabilityAsync();

        _suppressHelloToggle = true;
        HelloToggle.IsEnabled = ViewModel.CanToggleWindowsHello;
        HelloCard.Description = ViewModel.IsWindowsHelloAvailable
            ? "使用 Windows Hello 快速解锁密码库"
            : "此设备未启用 Windows Hello";
        _suppressHelloToggle = false;
    }

    private async void OnTrustCurrentDeviceClick(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.TrustCurrentDeviceAsync();
        if (result.Status != UiOperationStatus.Failed)
            return;

        var dialog = new ContentDialog
        {
            Title = "无法信任此设备",
            Content = result.Error ?? "操作失败，请稍后重试",
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await DialogHelper.ShowTrackedAsync(dialog);
    }

    private void OnExportDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(AboutInfo.ToDiagnosticsText());
        Clipboard.SetContent(package);
    }

    // ── 编辑资料 ─────────────────────────────────────────────────────────────

    private async void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "显示名称",
            Text = string.Empty,
            MinWidth = 320,
        };
        AutomationProperties.SetAutomationId(nameBox, "EditProfileNameBox");

        var errorText = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(errorText, "EditProfileErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(nameBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "编辑资料",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = string.Empty;

        while (true)
        {
            var result = await DialogHelper.ShowTrackedAsync(dialog);
            if (result != ContentDialogResult.Primary)
                return;

            var operation = await ViewModel.RenameAsync(nameBox.Text.Trim());

            if (operation.IsSuccess)
                return;
            if (operation.Status != UiOperationStatus.Failed)
                return;

            errorText.Text = operation.Error ?? "操作失败，请稍后重试";
            errorText.Visibility = Visibility.Visible;
        }
    }

    // ── 修改主密码 ───────────────────────────────────────────────────────────

    private async void OnChangePasswordCardClick(object sender, RoutedEventArgs e)
    {
        var currentBox = new PasswordBox { PlaceholderText = "当前主密码", MinWidth = 320 };
        AutomationProperties.SetAutomationId(currentBox, "ChangePasswordCurrentBox");

        var newBox = new PasswordBox { PlaceholderText = "新主密码" };
        AutomationProperties.SetAutomationId(newBox, "ChangePasswordNewBox");

        var confirmBox = new PasswordBox { PlaceholderText = "确认新主密码" };
        AutomationProperties.SetAutomationId(confirmBox, "ChangePasswordConfirmBox");

        var hintBox = new TextBox { PlaceholderText = "密码提示（可选）" };
        AutomationProperties.SetAutomationId(hintBox, "ChangePasswordHintBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(errorText, "ChangePasswordErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(currentBox);
        panel.Children.Add(newBox);
        panel.Children.Add(confirmBox);
        panel.Children.Add(hintBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "修改主密码",
            Content = panel,
            PrimaryButtonText = "修改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = string.Empty;

        try
        {
            while (true)
            {
                var result = await DialogHelper.ShowTrackedAsync(
                    dialog,
                    () =>
                    {
                        currentBox.Password = string.Empty;
                        newBox.Password = string.Empty;
                        confirmBox.Password = string.Empty;
                    });
                if (result != ContentDialogResult.Primary)
                    return;

                var hint = string.IsNullOrWhiteSpace(hintBox.Text) ? null : hintBox.Text.Trim();
                var operation = await ViewModel.ChangePasswordAsync(
                    currentBox.Password,
                    newBox.Password,
                    confirmBox.Password,
                    hint);

                // 成功由 App 层协调器统一登出并回登录页(SettingsViewModel 发 CredentialChanged intent)。
                if (operation.IsSuccess)
                    return;
                if (operation.Status != UiOperationStatus.Failed)
                    return;

                errorText.Text = operation.Error ?? "操作失败，请稍后重试";
                errorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            currentBox.Password = string.Empty;
            newBox.Password = string.Empty;
            confirmBox.Password = string.Empty;
        }
    }

    // ── 加密设置（KDF 迭代次数） ─────────────────────────────────────────────

    private async void OnChangeKdfCardClick(object sender, RoutedEventArgs e)
    {
        var currentBox = new PasswordBox { PlaceholderText = "当前主密码", MinWidth = 320 };
        AutomationProperties.SetAutomationId(currentBox, "ChangeKdfCurrentBox");

        var iterationsBox = new NumberBox
        {
            Header = "PBKDF2 迭代次数",
            Minimum = KdfParameterPolicy.MinimumPbkdf2WriteIterations,
            Maximum = KdfParameterPolicy.MaximumPbkdf2WriteIterations,
            Value = KdfParameterPolicy.MinimumPbkdf2WriteIterations,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 100000,
            LargeChange = 500000,
        };
        AutomationProperties.SetAutomationId(iterationsBox, "ChangeKdfIterationsBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(errorText, "ChangeKdfErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(currentBox);
        panel.Children.Add(iterationsBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "加密设置",
            Content = panel,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = string.Empty;

        try
        {
            while (true)
            {
                var result = await DialogHelper.ShowTrackedAsync(
                    dialog,
                    () => currentBox.Password = string.Empty);
                if (result != ContentDialogResult.Primary)
                    return;

                var iterations = double.IsNaN(iterationsBox.Value) ? 600000 : (int)iterationsBox.Value;
                var operation = await ViewModel.ChangeIterationsAsync(currentBox.Password, iterations);

                // 成功由 App 层协调器统一登出并回登录页(SettingsViewModel 发 CredentialChanged intent)。
                if (operation.IsSuccess)
                    return;
                if (operation.Status != UiOperationStatus.Failed)
                    return;

                errorText.Text = operation.Error ?? "操作失败，请稍后重试";
                errorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            currentBox.Password = string.Empty;
        }
    }

    // ── PIN 解锁 ────────────────────────────────────────────────────────────

    private async void OnPinToggleToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressPinToggle)
            return;

        if (PinToggle.IsOn)
        {
            var pin = await PromptSetPinAsync();
            if (pin is null)
            {
                _suppressPinToggle = true;
                PinToggle.IsOn = false;
                _suppressPinToggle = false;
                return;
            }

            ViewModel.SetPin(pin);
        }
        else
        {
            ViewModel.ClearPin();
        }
    }

    private async Task<string?> PromptSetPinAsync()
    {
        var pinBox = new PasswordBox { PlaceholderText = $"PIN（至少 {Core.Services.PinPolicy.MinLength} 位）", MinWidth = 320 };
        AutomationProperties.SetAutomationId(pinBox, "SetPinBox");

        var confirmBox = new PasswordBox { PlaceholderText = "确认 PIN" };
        AutomationProperties.SetAutomationId(confirmBox, "SetPinConfirmBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(errorText, "SetPinErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(pinBox);
        panel.Children.Add(confirmBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "设置 PIN",
            Content = panel,
            PrimaryButtonText = "设置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        try
        {
            while (true)
            {
                var result = await DialogHelper.ShowTrackedAsync(
                    dialog,
                    () =>
                    {
                        pinBox.Password = string.Empty;
                        confirmBox.Password = string.Empty;
                    });
                if (result != ContentDialogResult.Primary)
                    return null;

                var pin = pinBox.Password;
                var confirm = confirmBox.Password;

                if (!Core.Services.PinPolicy.IsValid(pin))
                {
                    errorText.Text = $"PIN 至少需要 {Core.Services.PinPolicy.MinLength} 位。";
                    errorText.Visibility = Visibility.Visible;
                    continue;
                }

                if (pin != confirm)
                {
                    errorText.Text = "两次输入的 PIN 不一致。";
                    errorText.Visibility = Visibility.Visible;
                    continue;
                }

                return pin;
            }
        }
        finally
        {
            pinBox.Password = string.Empty;
            confirmBox.Password = string.Empty;
        }
    }

    // ── Windows Hello 解锁 ──────────────────────────────────────────────────

    private async void OnHelloToggleToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressHelloToggle)
            return;

        if (HelloToggle.IsOn)
        {
            var ok = await ViewModel.EnableWindowsHelloAsync();
            if (!ok)
            {
                _suppressHelloToggle = true;
                HelloToggle.IsOn = false;
                _suppressHelloToggle = false;

                var dialog = new ContentDialog
                {
                    Title = "Windows Hello",
                    Content = new TextBlock { Text = "未能启用 Windows Hello", TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = "关闭",
                    XamlRoot = XamlRoot,
                };
                await DialogHelper.ShowTrackedAsync(dialog);
            }
        }
        else
        {
            ViewModel.DisableWindowsHello();
        }
    }

    // ── 双重验证（2FA）管理入口 ──────────────────────────────────────────────

    private async void OnTwoFactorCardClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // ── Loading 面板 ─────────────────────────────────────────────────
            var loadingPanel = new StackPanel
            {
                Name = "TwoFactorLoadingPanel",
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new ProgressRing { IsActive = true, Width = 24, Height = 24 },
                    new TextBlock { Text = "正在加载双重验证设置…", VerticalAlignment = VerticalAlignment.Center },
                },
            };
            AutomationProperties.SetAutomationId(loadingPanel, "TwoFactorLoadingPanel");

            // ── Error 面板 ───────────────────────────────────────────────────
            var errorBar = new InfoBar
            {
                Severity = InfoBarSeverity.Error,
                IsOpen = true,
                IsClosable = false,
            };
            var retryButton = new Button { Content = "重试" };
            AutomationProperties.SetAutomationId(retryButton, "TwoFactorRetryButton");
            var errorPanel = new StackPanel
            {
                Visibility = Visibility.Collapsed,
                Spacing = 8,
                Children = { errorBar, retryButton },
            };
            AutomationProperties.SetAutomationId(errorPanel, "TwoFactorErrorPanel");

            // ── Content 面板（含操作按钮）────────────────────────────────────
            var infoText = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var enableTotpButton = new Button { Content = "启用 TOTP 验证器", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(enableTotpButton, "TwoFactorEnableTotpButton");
            var enableEmailButton = new Button { Content = "启用邮箱验证码", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(enableEmailButton, "TwoFactorEnableEmailButton");
            var manageWebAuthnButton = new Button
            {
                Content = "管理安全密钥 / Windows Hello",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetAutomationId(manageWebAuthnButton, "TwoFactorManageWebAuthnButton");
            var disableButton = new Button { Content = "禁用已启用的验证", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(disableButton, "TwoFactorDisableButton");
            var contentPanel = new StackPanel
            {
                Spacing = 12,
                Visibility = Visibility.Collapsed,
                Children = { infoText, enableTotpButton, enableEmailButton, manageWebAuthnButton, disableButton },
            };
            AutomationProperties.SetAutomationId(contentPanel, "TwoFactorContentPanel");

            var rootPanel = new StackPanel { Spacing = 4, MinWidth = 360 };
            rootPanel.Children.Add(loadingPanel);
            rootPanel.Children.Add(errorPanel);
            rootPanel.Children.Add(contentPanel);

            var menuDialog = new ContentDialog
            {
                Title = "双重验证",
                Content = rootPanel,
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot,
            };

            using var loadCts = new CancellationTokenSource();
            using var trackedLoad = global::App.App.Services
                .GetRequiredService<ITransientOperationRegistry>()
                .Register(loadCts.Cancel, menuDialog.Hide, () => { });

            IReadOnlyList<Core.Models.TwoFactorProvider>? loadedProviders = null;

            async Task LoadAsync()
            {
                loadingPanel.Visibility = Visibility.Visible;
                errorPanel.Visibility = Visibility.Collapsed;
                contentPanel.Visibility = Visibility.Collapsed;

                var result = await ViewModel.LoadProvidersAsync(loadCts.Token);
                loadingPanel.Visibility = Visibility.Collapsed;
                if (!result.IsSuccess || result.Value is null)
                {
                    if (result.Status == UiOperationStatus.Failed)
                    {
                        errorBar.Message = result.Error ?? "无法加载双重验证设置";
                        errorPanel.Visibility = Visibility.Visible;
                    }
                    return;
                }

                loadedProviders = result.Value;
                var providers = result.Value;
                var totpEnabled = providers.Any(provider =>
                    provider.Type == (int)TwoFactorProviderType.Authenticator && provider.Enabled);
                var emailEnabled = providers.Any(provider =>
                    provider.Type == (int)TwoFactorProviderType.Email && provider.Enabled);
                var webAuthnEnabled = providers.Any(provider =>
                    provider.Type == (int)TwoFactorProviderType.WebAuthn && provider.Enabled);
                infoText.Text = providers.Count == 0
                    ? "尚未配置任何双重验证方式。"
                    : $"TOTP 验证器：{(totpEnabled ? "已启用" : "未启用")}　电子邮件验证码：{(emailEnabled ? "已启用" : "未启用")}　安全密钥：{(webAuthnEnabled ? "已启用" : "未启用")}";
                contentPanel.Visibility = Visibility.Visible;
            }

            retryButton.Click += async (_, _) => await LoadAsync();

            string? action = null;
            enableTotpButton.Click  += (_, _) => { action = "totp";    menuDialog.Hide(); };
            enableEmailButton.Click += (_, _) => { action = "email";   menuDialog.Hide(); };
            manageWebAuthnButton.Click += (_, _) => { action = "webauthn"; menuDialog.Hide(); };
            disableButton.Click     += (_, _) => { action = "disable"; menuDialog.Hide(); };

            menuDialog.Opened += async (_, _) => await LoadAsync();

            await DialogHelper.ShowTrackedAsync(menuDialog);

            switch (action)
            {
                case "totp":    await ShowEnableTotpDialogAsync(); break;
                case "email":   await ShowEnableEmailDialogAsync(); break;
                case "webauthn": await ShowManageWebAuthnDialogAsync(); break;
                case "disable":
                    if (loadedProviders is not null)
                        await ShowDisableTwoFactorDialogAsync(loadedProviders);
                    break;
            }
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex))
        {
            // 生命周期取消，正常退出
        }
        catch (Exception ex) when (Api.RequestTimeoutClassifier.IsTimeout(ex))
        {
            ViewModel.OperationError = UiExceptionPolicy.RequestTimeoutMessage;
        }
        catch (Exception)
        {
            // async void 边界保护，异常不逃逸
        }
    }

    // ── TOTP 启用对话框（三步：密码 → secret+code → 恢复码） ─────────────────

    private async Task ShowEnableTotpDialogAsync()
    {
        // 步骤 1：输入主密码
        var pwBox = new PasswordBox { PlaceholderText = "当前主密码", MinWidth = 360 };
        AutomationProperties.SetAutomationId(pwBox, "TotpSetupPasswordBox");

        var step1Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step1Error, "TotpSetupStep1ErrorText");
        step1Error.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var step1Panel = new StackPanel { Spacing = 8 };
        step1Panel.Children.Add(new TextBlock { Text = "请输入主密码以开始设置 TOTP 验证器", TextWrapping = TextWrapping.Wrap });
        step1Panel.Children.Add(pwBox);
        step1Panel.Children.Add(step1Error);

        var step1Dialog = new ContentDialog
        {
            Title = "启用 TOTP 验证器 — 第 1 步",
            Content = step1Panel,
            PrimaryButtonText = "下一步",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        string secret = string.Empty;
        string otpauth = string.Empty;

        ViewModel.OperationError = string.Empty;

        try
        {
            while (true)
            {
                var r1 = await DialogHelper.ShowTrackedAsync(
                    step1Dialog,
                    () => pwBox.Password = string.Empty);
                if (r1 != ContentDialogResult.Primary) return;

                var operation = await ViewModel.BeginTotpSetupAsync(pwBox.Password);

                if (operation.IsSuccess)
                {
                    (secret, otpauth) = operation.Value;
                    break;
                }
                if (operation.Status != UiOperationStatus.Failed)
                    return;

                step1Error.Text = operation.Error ?? "操作失败，请稍后重试";
                step1Error.Visibility = Visibility.Visible;
            }

        // 步骤 2：显示 secret/otpauth，让用户输入验证码
        var secretText = new TextBlock
        {
            Text = secret,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        AutomationProperties.SetAutomationId(secretText, "TotpSetupSecretText");

        var otpauthText = new TextBlock
        {
            Text = otpauth,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var sb2)
                ? (Microsoft.UI.Xaml.Media.Brush)sb2 : null,
        };
        AutomationProperties.SetAutomationId(otpauthText, "TotpSetupOtpauthText");

        var codeBox = new TextBox { PlaceholderText = "输入验证码（6 位）", MaxLength = 8 };
        AutomationProperties.SetAutomationId(codeBox, "TotpSetupCodeBox");

        var step2Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step2Error, "TotpSetupStep2ErrorText");
        step2Error.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var step2Panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        step2Panel.Children.Add(new TextBlock { Text = "请将以下密钥添加到您的验证器应用，然后输入验证码", TextWrapping = TextWrapping.Wrap });
        step2Panel.Children.Add(new TextBlock { Text = "密钥（Base32）：", Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0) });
        step2Panel.Children.Add(secretText);
        step2Panel.Children.Add(new TextBlock { Text = "OTPAuth URI：", Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0) });
        step2Panel.Children.Add(otpauthText);
        step2Panel.Children.Add(new TextBlock { Text = "验证码：", Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0) });
        step2Panel.Children.Add(codeBox);
        step2Panel.Children.Add(step2Error);

        var step2Dialog = new ContentDialog
        {
            Title = "启用 TOTP 验证器 — 第 2 步",
            Content = step2Panel,
            PrimaryButtonText = "启用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        Core.Models.TotpActivationResult? activation = null;
        ViewModel.OperationError = string.Empty;

        while (true)
        {
            var r2 = await DialogHelper.ShowTrackedAsync(
                step2Dialog,
                () =>
                {
                    pwBox.Password = string.Empty;
                    codeBox.Text = string.Empty;
                    secretText.Text = string.Empty;
                    otpauthText.Text = string.Empty;
                });
            if (r2 != ContentDialogResult.Primary) return;

            var operation = await ViewModel.EnableTotpAsync(
                pwBox.Password,
                secret,
                codeBox.Text.Trim());

            if (operation.IsSuccess)
            {
                activation = operation.Value!;
                pwBox.Password = string.Empty;
                codeBox.Text = string.Empty;
                secretText.Text = string.Empty;
                otpauthText.Text = string.Empty;
                secret = string.Empty;
                otpauth = string.Empty;
                break;
            }
            if (operation.Status != UiOperationStatus.Failed)
                return;

            step2Error.Text = operation.Error ?? "操作失败，请稍后重试";
            step2Error.Visibility = Visibility.Visible;
        }

        if (activation is null)
            return;

        // 步骤 3：显示恢复码
        var recoveryCode = activation.RecoveryCode ?? string.Empty;
        var rcText = new TextBlock
        {
            Text = recoveryCode,
            Visibility = activation.HasRecoveryCode ? Visibility.Visible : Visibility.Collapsed,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
        };
        AutomationProperties.SetAutomationId(rcText, "TotpSetupRecoveryCodeText");

        var recoveryWarning = new InfoBar
        {
            IsOpen = !activation.HasRecoveryCode,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Title = "TOTP 已启用",
            Message = "恢复码暂时无法获取。您可以稍后重试；重试不会再次启用 TOTP。",
        };
        AutomationProperties.SetAutomationId(recoveryWarning, "TotpSetupRecoveryWarning");
        AutomationProperties.SetName(recoveryWarning, "TOTP 已启用，恢复码暂时无法获取");

        var recoveryPasswordBox = new PasswordBox
        {
            PlaceholderText = "当前主密码",
            Visibility = activation.HasRecoveryCode ? Visibility.Collapsed : Visibility.Visible,
        };
        AutomationProperties.SetAutomationId(recoveryPasswordBox, "TotpRecoveryPasswordBox");
        AutomationProperties.SetName(recoveryPasswordBox, "用于获取恢复码的当前主密码");

        var step3Panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        step3Panel.Children.Add(new TextBlock { Text = "TOTP 验证器已启用。请妥善保存恢复码，账户锁定时可凭此找回。", TextWrapping = TextWrapping.Wrap });
        step3Panel.Children.Add(recoveryWarning);
        step3Panel.Children.Add(rcText);
        step3Panel.Children.Add(recoveryPasswordBox);

        var step3Dialog = new ContentDialog
        {
            Title = "TOTP 启用成功",
            Content = step3Panel,
            PrimaryButtonText = activation.HasRecoveryCode ? string.Empty : "重新获取恢复码",
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        while (true)
        {
            var r3 = await DialogHelper.ShowTrackedAsync(
                step3Dialog,
                () =>
                {
                    rcText.Text = string.Empty;
                    recoveryPasswordBox.Password = string.Empty;
                });
            if (r3 != ContentDialogResult.Primary)
            {
                recoveryPasswordBox.Password = string.Empty;
                break;
            }

            UiOperationResult<string> recoveryOperation;
            try
            {
                recoveryOperation = await ViewModel.GetRecoveryCodeAsync(recoveryPasswordBox.Password);
            }
            finally
            {
                recoveryPasswordBox.Password = string.Empty;
            }

            if (recoveryOperation.IsSuccess)
            {
                rcText.Text = recoveryOperation.Value!;
                rcText.Visibility = Visibility.Visible;
                recoveryWarning.IsOpen = false;
                recoveryPasswordBox.Visibility = Visibility.Collapsed;
                step3Dialog.PrimaryButtonText = string.Empty;
                continue;
            }
            if (recoveryOperation.Status != UiOperationStatus.Failed)
                break;

            recoveryWarning.Message = recoveryOperation.Error ?? "恢复码暂时无法获取，请稍后重试";
            recoveryWarning.IsOpen = true;
        }
        rcText.Text = string.Empty;
        recoveryPasswordBox.Password = string.Empty;
        }
        finally
        {
            pwBox.Password = string.Empty;
        }
    }

    // ── Email 两步验证启用对话框（两步：密码+邮箱 → 验证码） ─────────────────

    private async Task ShowEnableEmailDialogAsync()
    {
        // 步骤 1：密码 + 邮箱
        var emailPwBox = new PasswordBox { PlaceholderText = "当前主密码", MinWidth = 360 };
        AutomationProperties.SetAutomationId(emailPwBox, "EmailSetupPasswordBox");

        var emailAddressBox = new TextBox { PlaceholderText = "验证邮箱地址" };
        AutomationProperties.SetAutomationId(emailAddressBox, "EmailSetupAddressBox");

        var step1Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step1Error, "EmailSetupStep1ErrorText");
        step1Error.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var step1Panel = new StackPanel { Spacing = 8 };
        step1Panel.Children.Add(new TextBlock { Text = "请输入主密码和接收验证码的邮箱地址", TextWrapping = TextWrapping.Wrap });
        step1Panel.Children.Add(emailPwBox);
        step1Panel.Children.Add(emailAddressBox);
        step1Panel.Children.Add(step1Error);

        var step1Dialog = new ContentDialog
        {
            Title = "启用邮箱验证码 — 第 1 步",
            Content = step1Panel,
            PrimaryButtonText = "发送验证码",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = string.Empty;

        try
        {
            while (true)
            {
                var r1 = await DialogHelper.ShowTrackedAsync(
                    step1Dialog,
                    () => emailPwBox.Password = string.Empty);
                if (r1 != ContentDialogResult.Primary) return;

                var operation = await ViewModel.SendEmailAsync(
                    emailPwBox.Password,
                    emailAddressBox.Text.Trim());

                if (operation.IsSuccess)
                    break;
                if (operation.Status != UiOperationStatus.Failed)
                    return;

                step1Error.Text = operation.Error ?? "操作失败，请稍后重试";
                step1Error.Visibility = Visibility.Visible;
            }

        // 步骤 2：输入收到的验证码
        var tokenBox = new TextBox { PlaceholderText = "输入邮件验证码" };
        AutomationProperties.SetAutomationId(tokenBox, "EmailSetupTokenBox");

        var step2Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step2Error, "EmailSetupStep2ErrorText");
        step2Error.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var step2Panel = new StackPanel { Spacing = 8, MinWidth = 360 };
        step2Panel.Children.Add(new TextBlock { Text = $"验证码已发送至 {emailAddressBox.Text.Trim()}，请输入：", TextWrapping = TextWrapping.Wrap });
        step2Panel.Children.Add(tokenBox);
        step2Panel.Children.Add(step2Error);

        var step2Dialog = new ContentDialog
        {
            Title = "启用邮箱验证码 — 第 2 步",
            Content = step2Panel,
            PrimaryButtonText = "启用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = string.Empty;

        while (true)
        {
            var r2 = await DialogHelper.ShowTrackedAsync(
                step2Dialog,
                () =>
                {
                    emailPwBox.Password = string.Empty;
                    tokenBox.Text = string.Empty;
                });
            if (r2 != ContentDialogResult.Primary) return;

            var operation = await ViewModel.EnableEmailAsync(
                emailPwBox.Password,
                emailAddressBox.Text.Trim(),
                tokenBox.Text.Trim());

            if (operation.IsSuccess)
                break;
            if (operation.Status != UiOperationStatus.Failed)
                return;

            step2Error.Text = operation.Error ?? "操作失败，请稍后重试";
            step2Error.Visibility = Visibility.Visible;
        }

        var doneDialog = new ContentDialog
        {
            Title = "邮箱验证码已启用",
            Content = new TextBlock { Text = "邮箱两步验证已成功启用。", TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "完成",
            XamlRoot = XamlRoot,
        };
        await DialogHelper.ShowTrackedAsync(doneDialog);
        }
        finally
        {
            emailPwBox.Password = string.Empty;
        }
    }

    // WebAuthn credential management

    private async Task ShowManageWebAuthnDialogAsync()
    {
        using var managementCts = new CancellationTokenSource();
        var verificationMethod = new ComboBox
        {
            Header = "验证方式",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        verificationMethod.Items.Add(new ComboBoxItem
        {
            Content = "主密码",
            Tag = WebAuthnVerificationMethod.MasterPassword,
        });
        verificationMethod.Items.Add(new ComboBoxItem
        {
            Content = "邮件一次性验证码",
            Tag = WebAuthnVerificationMethod.EmailOtp,
        });
        verificationMethod.Items.Add(new ComboBoxItem
        {
            Content = "设备登录访问码",
            Tag = WebAuthnVerificationMethod.AuthRequestAccessCode,
        });
        verificationMethod.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(
            verificationMethod,
            "WebAuthnVerificationMethodCombo");

        var verificationInput = new PasswordBox
        {
            PlaceholderText = "当前主密码",
            MinWidth = 360,
            MaxLength = 4096,
        };
        AutomationProperties.SetAutomationId(
            verificationInput,
            "WebAuthnVerificationInputBox");

        var verificationHelp = new TextBlock
        {
            Text = "主密码只在本机派生验证哈希，不会直接发送。",
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(
            verificationHelp,
            "WebAuthnVerificationHelpText");

        var requestOtpButton = new Button
        {
            Content = "发送验证码",
            Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(
            requestOtpButton,
            "WebAuthnRequestOtpButton");

        var verificationStatus = new InfoBar
        {
            IsOpen = false,
            IsClosable = false,
        };
        AutomationProperties.SetAutomationId(
            verificationStatus,
            "WebAuthnVerificationStatusBar");

        var passwordError = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        passwordError.SetValue(
            TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);
        AutomationProperties.SetAutomationId(passwordError, "WebAuthnManagementPasswordError");

        var passwordPanel = new StackPanel { Spacing = 8 };
        passwordPanel.Children.Add(new TextBlock
        {
            Text = "选择一种服务器验证方式以查看并管理已注册的安全密钥。",
            TextWrapping = TextWrapping.Wrap,
        });
        passwordPanel.Children.Add(verificationMethod);
        passwordPanel.Children.Add(verificationInput);
        passwordPanel.Children.Add(verificationHelp);
        passwordPanel.Children.Add(requestOtpButton);
        passwordPanel.Children.Add(verificationStatus);
        passwordPanel.Children.Add(passwordError);

        static WebAuthnVerificationMethod SelectedVerificationMethod(ComboBox combo) =>
            (combo.SelectedItem as ComboBoxItem)?.Tag is WebAuthnVerificationMethod method
                ? method
                : WebAuthnVerificationMethod.MasterPassword;

        void UpdateVerificationInput()
        {
            verificationInput.Password = string.Empty;
            passwordError.Visibility = Visibility.Collapsed;
            verificationStatus.IsOpen = false;
            switch (SelectedVerificationMethod(verificationMethod))
            {
                case WebAuthnVerificationMethod.MasterPassword:
                    verificationInput.PlaceholderText = "当前主密码";
                    verificationHelp.Text = "主密码只在本机派生验证哈希，不会直接发送。";
                    requestOtpButton.Visibility = Visibility.Collapsed;
                    break;
                case WebAuthnVerificationMethod.EmailOtp:
                    verificationInput.PlaceholderText = "邮件验证码";
                    verificationHelp.Text = "先发送验证码，再输入服务器邮件中的一次性代码。";
                    requestOtpButton.Visibility = Visibility.Visible;
                    break;
                case WebAuthnVerificationMethod.AuthRequestAccessCode:
                    verificationInput.PlaceholderText = "设备登录访问码";
                    verificationHelp.Text = "仅在 Bitwarden 的“使用其他设备登录”流程已向此会话提供访问码时使用。";
                    requestOtpButton.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        verificationMethod.SelectionChanged += (_, _) => UpdateVerificationInput();
        requestOtpButton.Click += async (_, _) =>
        {
            requestOtpButton.IsEnabled = false;
            verificationStatus.IsOpen = false;
            try
            {
                var send = await ViewModel.RequestWebAuthnVerificationOtpAsync(
                    managementCts.Token);
                verificationStatus.Severity = send.IsSuccess
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Error;
                verificationStatus.Message = send.IsSuccess
                    ? "验证码已发送，请检查账户邮箱。"
                    : send.Error ?? "无法发送验证码，请稍后重试。";
                verificationStatus.IsOpen = true;
            }
            finally
            {
                requestOtpButton.IsEnabled = true;
            }
        };

        var passwordDialog = new ContentDialog
        {
            Title = "管理安全密钥",
            Content = passwordPanel,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        TwoFactorUserVerification? activeVerification = null;
        using var trackedManagement = global::App.App.Services
            .GetRequiredService<ITransientOperationRegistry>()
            .Register(
                managementCts.Cancel,
                () => { },
                () =>
                {
                    verificationInput.Password = string.Empty;
                    activeVerification = null;
                });

        try
        {
            WebAuthnManagementSnapshot snapshot;
            while (true)
            {
                var result = await DialogHelper.ShowTrackedAsync(
                    passwordDialog,
                    () =>
                    {
                        verificationInput.Password = string.Empty;
                        activeVerification = null;
                    },
                    managementCts.Token);
                if (result != ContentDialogResult.Primary)
                    return;

                TwoFactorUserVerification verification;
                try
                {
                    verification = SettingsViewModel.CreateWebAuthnVerification(
                        SelectedVerificationMethod(verificationMethod),
                        verificationInput.Password);
                }
                catch (ArgumentException)
                {
                    passwordError.Text = "请输入所选验证方式对应的值。";
                    passwordError.Visibility = Visibility.Visible;
                    continue;
                }

                var load = await ViewModel.LoadWebAuthnAsync(verification, managementCts.Token);
                if (load.IsSuccess && load.Value is not null)
                {
                    activeVerification = verification;
                    verificationInput.Password = string.Empty;
                    snapshot = load.Value;
                    break;
                }
                if (load.Status == UiOperationStatus.Busy)
                {
                    passwordError.Text = "另一项设置操作正在进行，请稍后重试。";
                    passwordError.Visibility = Visibility.Visible;
                    continue;
                }
                if (load.Status != UiOperationStatus.Failed)
                    return;

                passwordError.Text = load.Error ?? "无法加载安全密钥，请稍后重试";
                passwordError.Visibility = Visibility.Visible;
            }

            string? statusMessage = ViewModel.CanRegisterWebAuthn
                ? null
                : "此设备不支持 Windows WebAuthn。您仍可查看或删除已注册的安全密钥。";
            InfoBarSeverity statusSeverity = ViewModel.CanRegisterWebAuthn
                ? InfoBarSeverity.Informational
                : InfoBarSeverity.Warning;

            while (true)
            {
                var credentialPanel = new StackPanel { Spacing = 8 };
                AutomationProperties.SetAutomationId(
                    credentialPanel,
                    "WebAuthnCredentialList");

                if (snapshot.Credentials.Count == 0)
                {
                    credentialPanel.Children.Add(new TextBlock
                    {
                        Text = "尚未注册安全密钥。",
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                WebAuthnCredentialListItem? deleteTarget = null;
                ContentDialog? managementDialog = null;
                foreach (var credential in snapshot.Credentials)
                {
                    var row = new Grid
                    {
                        Padding = new Thickness(8),
                        ColumnSpacing = 12,
                    };
                    row.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1, GridUnitType.Star),
                    });
                    row.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = GridLength.Auto,
                    });

                    var displayName = string.IsNullOrWhiteSpace(credential.Name)
                        ? $"安全密钥 {credential.Id}"
                        : credential.Name;
                    var nameText = new TextBlock
                    {
                        Text = credential.Migrated
                            ? $"{displayName}（从 U2F 迁移）"
                            : displayName,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(nameText, 0);
                    row.Children.Add(nameText);

                    var deleteButton = new Button
                    {
                        Content = new SymbolIcon(Symbol.Delete),
                        HorizontalAlignment = HorizontalAlignment.Right,
                    };
                    AutomationProperties.SetName(deleteButton, $"删除 {displayName}");
                    AutomationProperties.SetAutomationId(
                        deleteButton,
                        $"WebAuthnDeleteCredentialButton_{credential.Id}");
                    ToolTipService.SetToolTip(deleteButton, "删除安全密钥");
                    deleteButton.Click += (_, _) =>
                    {
                        deleteTarget = credential;
                        managementDialog?.Hide();
                    };
                    Grid.SetColumn(deleteButton, 1);
                    row.Children.Add(deleteButton);
                    credentialPanel.Children.Add(row);
                }

                var nameBox = new TextBox
                {
                    Header = "新安全密钥名称",
                    PlaceholderText = "例如：办公安全密钥",
                    MaxLength = 50,
                };
                AutomationProperties.SetAutomationId(nameBox, "WebAuthnCredentialNameBox");

                var statusBar = new InfoBar
                {
                    IsOpen = !string.IsNullOrWhiteSpace(statusMessage),
                    IsClosable = false,
                    Severity = statusSeverity,
                    Message = statusMessage ?? string.Empty,
                };
                AutomationProperties.SetAutomationId(statusBar, "WebAuthnManagementStatusBar");

                var credentialScroller = new ScrollViewer
                {
                    MaxHeight = 280,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollMode = ScrollMode.Auto,
                    Content = credentialPanel,
                };

                var content = new StackPanel
                {
                    MinWidth = 420,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "使用 Windows Hello 或兼容的安全密钥作为账户双重验证方式。",
                            TextWrapping = TextWrapping.Wrap,
                        },
                        credentialScroller,
                        nameBox,
                        statusBar,
                    },
                };

                managementDialog = new ContentDialog
                {
                    Title = "安全密钥",
                    Content = content,
                    PrimaryButtonText = "添加安全密钥",
                    IsPrimaryButtonEnabled = ViewModel.CanRegisterWebAuthn,
                    CloseButtonText = "完成",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };

                var action = await DialogHelper.ShowTrackedAsync(
                    managementDialog,
                    () =>
                    {
                        activeVerification = null;
                        nameBox.Text = string.Empty;
                    },
                    managementCts.Token);

                if (deleteTarget is not null)
                {
                    var isLastCredential = snapshot.Credentials.Count == 1;
                    var confirmed = await DialogHelper.ConfirmAsync(
                        XamlRoot,
                        "删除安全密钥",
                        isLastCredential
                            ? "这是最后一把安全密钥。删除后将关闭 WebAuthn 双重验证，确定继续吗？"
                            : $"确定删除“{deleteTarget.Name}”吗？",
                        "删除",
                        ct: managementCts.Token);
                    if (!confirmed)
                    {
                        if (managementCts.IsCancellationRequested)
                            return;
                        continue;
                    }

                    var deletion = await ViewModel.DeleteWebAuthnCredentialAsync(
                        activeVerification
                            ?? throw new InvalidOperationException("WebAuthn verification is unavailable."),
                        deleteTarget.Id,
                        managementCts.Token);
                    if (deletion.IsSuccess && deletion.Value is not null)
                    {
                        snapshot = deletion.Value;
                        statusMessage = isLastCredential
                            ? "WebAuthn 双重验证已关闭。"
                            : "安全密钥已删除。";
                        statusSeverity = InfoBarSeverity.Success;
                        continue;
                    }
                    if (deletion.Status == UiOperationStatus.Busy)
                    {
                        statusMessage = "另一项设置操作正在进行，请稍后重试。";
                        statusSeverity = InfoBarSeverity.Warning;
                        continue;
                    }
                    if (deletion.Status != UiOperationStatus.Failed)
                        return;

                    statusMessage = deletion.Error ?? "无法删除安全密钥，请稍后重试";
                    statusSeverity = InfoBarSeverity.Error;
                    continue;
                }

                if (action != ContentDialogResult.Primary)
                    return;

                var credentialName = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(credentialName))
                {
                    statusMessage = "请输入安全密钥名称。";
                    statusSeverity = InfoBarSeverity.Error;
                    continue;
                }

                int nextId;
                try
                {
                    nextId = snapshot.GetNextCredentialId();
                }
                catch (InvalidOperationException)
                {
                    statusMessage = "无法分配新的安全密钥标识。";
                    statusSeverity = InfoBarSeverity.Error;
                    continue;
                }

                var registration = await ViewModel.AddWebAuthnCredentialWithDetailsAsync(
                    activeVerification
                        ?? throw new InvalidOperationException("WebAuthn verification is unavailable."),
                    nextId,
                    credentialName,
                    managementCts.Token);
                nameBox.Text = string.Empty;
                if (registration.Status == UiOperationStatus.Cancelled)
                    return;
                if (registration.Status == UiOperationStatus.Busy)
                {
                    statusMessage = "另一项设置操作正在进行，请稍后重试。";
                    statusSeverity = InfoBarSeverity.Warning;
                    continue;
                }

                if (registration.Value is not null)
                    snapshot = registration.Value;

                if (registration.IsSuccess)
                {
                    statusMessage = "安全密钥已添加。";
                    statusSeverity = InfoBarSeverity.Success;
                }
                else
                {
                    statusMessage = registration.Error
                        ?? "无法添加安全密钥，请稍后重试";
                    statusSeverity = InfoBarSeverity.Error;
                }
            }
        }
        finally
        {
            managementCts.Cancel();
            verificationInput.Password = string.Empty;
            activeVerification = null;
        }
    }

    // ── 禁用双重验证对话框 ──────────────────────────────────────────────────

    private async Task ShowDisableTwoFactorDialogAsync(IReadOnlyList<Core.Models.TwoFactorProvider> providers)
    {
        var enabledProviders = providers.Where(p => p.Enabled).ToList();

        if (enabledProviders.Count == 0)
        {
            var noneDialog = new ContentDialog
            {
                Title = "禁用双重验证",
                Content = new TextBlock { Text = "当前没有已启用的两步验证方式。", TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot,
            };
            await DialogHelper.ShowTrackedAsync(noneDialog);
            return;
        }

        var disablePwBox = new PasswordBox { PlaceholderText = "当前主密码", MinWidth = 360 };
        AutomationProperties.SetAutomationId(disablePwBox, "DisableTwoFactorPasswordBox");

        var providerCombo = new ComboBox { MinWidth = 280, PlaceholderText = "选择要禁用的验证方式" };
        AutomationProperties.SetAutomationId(providerCombo, "DisableTwoFactorProviderCombo");

        foreach (var p in enabledProviders)
            providerCombo.Items.Add(new ComboBoxItem
            {
                Content = SettingsViewModel.GetTwoFactorProviderDisplayName(p.Type),
                Tag = p.Type,
            });

        providerCombo.SelectedIndex = 0;

        var disableError = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(disableError, "DisableTwoFactorErrorText");
        disableError.SetValue(TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);

        var disablePanel = new StackPanel { Spacing = 8 };
        disablePanel.Children.Add(new TextBlock { Text = "选择要禁用的验证方式并输入主密码确认：", TextWrapping = TextWrapping.Wrap });
        disablePanel.Children.Add(providerCombo);
        disablePanel.Children.Add(disablePwBox);
        disablePanel.Children.Add(disableError);

        var disableDialog = new ContentDialog
        {
            Title = "禁用双重验证",
            Content = disablePanel,
            PrimaryButtonText = "禁用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = string.Empty;

        try
        {
            while (true)
            {
                var rd = await DialogHelper.ShowTrackedAsync(
                    disableDialog,
                    () => disablePwBox.Password = string.Empty);
                if (rd != ContentDialogResult.Primary) return;

                if (providerCombo.SelectedItem is not ComboBoxItem selectedItem)
                {
                    disableError.Text = "请选择要禁用的验证方式";
                    disableError.Visibility = Visibility.Visible;
                    continue;
                }

                int providerType = (int)selectedItem.Tag;
                var operation = await ViewModel.DisableTwoFactorAsync(
                    disablePwBox.Password,
                    providerType);

                if (operation.IsSuccess)
                    break;
                if (operation.Status != UiOperationStatus.Failed)
                    return;

                disableError.Text = operation.Error ?? "操作失败，请稍后重试";
                disableError.Visibility = Visibility.Visible;
            }

            var doneDialog = new ContentDialog
            {
                Title = "双重验证已禁用",
                Content = new TextBlock { Text = "所选两步验证方式已成功禁用。", TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "完成",
                XamlRoot = XamlRoot,
            };
            await DialogHelper.ShowTrackedAsync(doneDialog);
        }
        finally
        {
            disablePwBox.Password = string.Empty;
        }
    }

    // Account deletion flow

    private async void OnDeleteAccountClick(object sender, RoutedEventArgs e)
    {
        DeleteAccountButton.IsEnabled = false;
        try
        {
            await RunAccountDeletionAsync();
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex))
        {
        }
        catch
        {
            await ShowAccountDeletionMessageAsync(
                "无法删除账户",
                "账户删除流程发生错误，未确认服务端状态。请勿自动重试。",
                CancellationToken.None);
        }
        finally
        {
            DeleteAccountButton.IsEnabled = true;
        }
    }

    private async Task RunAccountDeletionAsync()
    {
        using var operationCts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(
                ref _accountDeletionCts,
                operationCts,
                comparand: null) is not null)
        {
            return;
        }

        try
        {
            await RunAccountDeletionCoreAsync(operationCts);
        }
        finally
        {
            _ = Interlocked.CompareExchange(
                ref _accountDeletionCts,
                value: null,
                comparand: operationCts);
        }
    }

    private async Task RunAccountDeletionCoreAsync(CancellationTokenSource operationCts)
    {
        PasswordBox? activeSecretBox = null;
        using var trackedOperation = global::App.App.Services
            .GetRequiredService<ITransientOperationRegistry>()
            .Register(
                operationCts.Cancel,
                () => { },
                () =>
                {
                    if (activeSecretBox is not null)
                        activeSecretBox.Password = string.Empty;
                });

        var preflightOperation = ViewModel.GetAccountDeletionPreflight();
        if (!preflightOperation.IsSuccess || preflightOperation.Value is null)
        {
            if (preflightOperation.Status == UiOperationStatus.Busy)
            {
                await ShowAccountDeletionMessageAsync(
                    "操作正在进行",
                    "另一项设置操作正在进行，请稍后重试。",
                    operationCts.Token);
            }
            else if (preflightOperation.Status == UiOperationStatus.Failed)
            {
                await ShowAccountDeletionMessageAsync(
                    "无法检查删除条件",
                    preflightOperation.Error ?? "请重新在线登录后重试。",
                    operationCts.Token);
            }
            return;
        }

        var preflight = preflightOperation.Value;
        if (!preflight.CanDispatch)
        {
            var (title, message) = preflight.BlockReason switch
            {
                AccountDeletionPreflightBlockReason.ClaimedByOrganization => (
                    "组织账户不能在此删除",
                    "此账户的邮箱域名已由组织认领。客户端不会发送删除请求，请联系组织管理员处理。"),
                AccountDeletionPreflightBlockReason.FreshOnlineAuthenticationRequired => (
                    "需要重新在线登录",
                    "无法确认此账户是否设置了主密码。为安全起见，请退出并重新在线登录后再尝试删除账户。"),
                _ => (
                    "暂时无法删除账户",
                    "当前账户状态不允许发送删除请求。请重新在线登录后重试。"),
            };
            await ShowAccountDeletionMessageAsync(title, message, operationCts.Token);
            return;
        }

        if (!await ConfirmAccountDeletionConsequencesAsync(preflight, operationCts.Token))
            return;
        if (!await ConfirmAccountDeletionEmailAsync(preflight.Binding.NormalizedEmail, operationCts.Token))
            return;

        if (preflight.CredentialRequirement == AccountDeletionCredentialRequirement.OneTimePassword)
        {
            var otpOperation = await ViewModel.RequestAccountDeletionOtpAsync(operationCts.Token);
            if (!otpOperation.IsSuccess || otpOperation.Value is null)
            {
                if (otpOperation.Status == UiOperationStatus.Failed)
                {
                    await ShowAccountDeletionMessageAsync(
                        "无法发送验证码",
                        otpOperation.Error ?? "请稍后重试。",
                        operationCts.Token);
                }
                return;
            }

            switch (otpOperation.Value)
            {
                case AccountDeletionOtpRequestResult.Sent sent:
                    preflight = sent.Preflight;
                    break;
                case AccountDeletionOtpRequestResult.Rejected rejected:
                    await ShowAccountDeletionMessageAsync(
                        "无法发送验证码",
                        SettingsViewModel.GetAccountDeletionRejectionMessage(rejected.Reason),
                        operationCts.Token);
                    return;
                case AccountDeletionOtpRequestResult.CancelledBeforeDispatch:
                    await ShowAccountDeletionMessageAsync(
                        "已取消",
                        "验证码请求未发送。",
                        operationCts.Token);
                    return;
                case AccountDeletionOtpRequestResult.OutcomeUncertain:
                    await ShowAccountDeletionMessageAsync(
                        "验证码请求状态不确定",
                        "无法确认验证码邮件是否已发送。应用不会自动重试，请稍后检查邮箱。",
                        operationCts.Token);
                    return;
            }
        }

        if (preflight.CredentialRequirement
            == AccountDeletionCredentialRequirement.FreshOnlineAuthentication)
        {
            await ShowAccountDeletionMessageAsync(
                "需要重新在线登录",
                "当前登录信息不足以安全删除账户，请重新在线登录后重试。",
                operationCts.Token);
            return;
        }

        var credentialBox = new PasswordBox
        {
            MinWidth = 360,
            PlaceholderText = preflight.CredentialRequirement
                == AccountDeletionCredentialRequirement.MasterPassword
                    ? "当前主密码"
                    : "邮件验证码",
        };
        activeSecretBox = credentialBox;
        AutomationProperties.SetAutomationId(
            credentialBox,
            preflight.CredentialRequirement == AccountDeletionCredentialRequirement.MasterPassword
                ? "DeleteAccountMasterPasswordBox"
                : "DeleteAccountOtpBox");

        var credentialError = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        credentialError.SetValue(
            TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);
        AutomationProperties.SetAutomationId(credentialError, "DeleteAccountCredentialError");

        var credentialPanel = new StackPanel { Spacing = 8 };
        credentialPanel.Children.Add(new TextBlock
        {
            Text = preflight.CredentialRequirement == AccountDeletionCredentialRequirement.MasterPassword
                ? "输入当前主密码以授权永久删除。"
                : $"验证码已发送至 {preflight.Binding.NormalizedEmail}。输入邮件中的验证码以授权永久删除。",
            TextWrapping = TextWrapping.Wrap,
        });
        credentialPanel.Children.Add(credentialBox);
        credentialPanel.Children.Add(credentialError);

        var credentialDialog = new ContentDialog
        {
            Title = "最终确认",
            Content = credentialPanel,
            PrimaryButtonText = "永久删除账户",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        AutomationProperties.SetAutomationId(
            credentialDialog,
            "DeleteAccountCredentialDialog");

        char[]? credentialCharacters = null;
        try
        {
            var credentialResult = await DialogHelper.ShowTrackedAsync(
                credentialDialog,
                () => credentialBox.Password = string.Empty,
                operationCts.Token);
            if (credentialResult != ContentDialogResult.Primary)
                return;
            if (string.IsNullOrWhiteSpace(credentialBox.Password))
            {
                await ShowAccountDeletionMessageAsync(
                    "缺少验证信息",
                    "请输入验证信息后再继续。",
                    operationCts.Token);
                return;
            }

            var credentialBuffer = credentialBox.Password.ToCharArray();
            credentialCharacters = credentialBuffer;
            credentialBox.Password = string.Empty;
            AccountDeletionCredential credential;
            try
            {
                credential = preflight.CredentialRequirement
                    == AccountDeletionCredentialRequirement.MasterPassword
                        ? AccountDeletionCredential.FromMasterPassword(credentialBuffer)
                        : AccountDeletionCredential.FromOneTimePassword(credentialBuffer);
            }
            finally
            {
                Array.Clear(credentialBuffer);
            }
            credentialCharacters = null;
            using var ownedCredential = credential;
            var deletion = await ViewModel.DeleteAccountAsync(credential, operationCts.Token);
            if (!deletion.IsSuccess || deletion.Value is null)
            {
                if (deletion.Status == UiOperationStatus.Failed)
                {
                    await ShowAccountDeletionMessageAsync(
                        "无法确认账户删除结果",
                        deletion.Error ?? "请勿自动重试。",
                        operationCts.Token);
                }
                return;
            }

            await HandleAccountDeletionResultAsync(deletion.Value, operationCts.Token);
        }
        finally
        {
            if (credentialCharacters is not null)
                Array.Clear(credentialCharacters);
            credentialBox.Password = string.Empty;
            activeSecretBox = null;
        }
    }

    private async Task<bool> ConfirmAccountDeletionConsequencesAsync(
        AccountDeletionPreflight preflight,
        CancellationToken ct)
    {
        var panel = new StackPanel { Spacing = 12, MinWidth = 400 };
        panel.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Error,
            Title = "此操作不可撤销",
            Message = "账户、个人密码库、附件、发送项目和服务端访问权限将永久删除。",
        });
        if (preflight.OwnerWarnings.Count > 0)
        {
            var organizations = string.Join(
                Environment.NewLine,
                preflight.OwnerWarnings.Select(warning => $"• {warning.OrganizationName}"));
            panel.Children.Add(new InfoBar
            {
                IsOpen = true,
                IsClosable = false,
                Severity = InfoBarSeverity.Warning,
                Title = "您是组织所有者",
                Message = $"删除账户前请先处理以下组织的所有权：{Environment.NewLine}{organizations}",
            });
        }

        var dialog = new ContentDialog
        {
            Title = "永久删除账户？",
            Content = panel,
            PrimaryButtonText = "我了解，继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        AutomationProperties.SetAutomationId(dialog, "DeleteAccountConsequencesDialog");
        return await DialogHelper.ShowTrackedAsync(dialog, ct: ct)
            == ContentDialogResult.Primary;
    }

    private async Task<bool> ConfirmAccountDeletionEmailAsync(
        string normalizedEmail,
        CancellationToken ct)
    {
        var emailBox = new TextBox
        {
            Header = "逐字输入当前邮箱",
            PlaceholderText = normalizedEmail,
            MinWidth = 360,
        };
        AutomationProperties.SetAutomationId(emailBox, "DeleteAccountEmailConfirmationBox");
        var error = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        error.SetValue(
            TextBlock.ForegroundProperty,
            Application.Current.Resources["SystemFillColorCriticalBrush"]);
        AutomationProperties.SetAutomationId(error, "DeleteAccountEmailConfirmationError");

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"请输入 {normalizedEmail} 以继续。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(emailBox);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = "确认账户邮箱",
            Content = panel,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        AutomationProperties.SetAutomationId(dialog, "DeleteAccountEmailConfirmationDialog");

        try
        {
            while (true)
            {
                var result = await DialogHelper.ShowTrackedAsync(dialog, ct: ct);
                if (result != ContentDialogResult.Primary)
                    return false;
                if (string.Equals(emailBox.Text, normalizedEmail, StringComparison.Ordinal))
                    return true;

                error.Text = "邮箱必须与当前账户完全一致。";
                error.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            emailBox.Text = string.Empty;
        }
    }

    private async Task HandleAccountDeletionResultAsync(
        AccountDeletionResult result,
        CancellationToken ct)
    {
        switch (result)
        {
            case AccountDeletionResult.Confirmed:
                await ViewModel.TransitionAfterAccountDeletionAsync(result);
                return;

            case AccountDeletionResult.Rejected rejected:
                if (rejected.Pending is not null)
                {
                    await ViewModel.TransitionAfterAccountDeletionAsync(result);
                    return;
                }
                await ShowAccountDeletionMessageAsync(
                    "账户未删除",
                    SettingsViewModel.GetAccountDeletionRejectionMessage(rejected.Reason),
                    ct);
                return;

            case AccountDeletionResult.CancelledBeforeDispatch cancelled:
                if (cancelled.Pending is not null)
                {
                    await ViewModel.TransitionAfterAccountDeletionAsync(result);
                    return;
                }
                await ShowAccountDeletionMessageAsync(
                    "已取消",
                    "删除请求尚未发送，账户没有变化。",
                    ct);
                return;

            case AccountDeletionResult.OutcomeUncertain:
                await ViewModel.TransitionAfterAccountDeletionAsync(result);
                return;

            case AccountDeletionResult.OutcomeUncertainLocalCleanupComplete:
                await ViewModel.TransitionAfterAccountDeletionAsync(result);
                return;

            case AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete:
                await ViewModel.TransitionAfterAccountDeletionAsync(result);
                return;

            case AccountDeletionResult.ConfirmedLocalCleanupIncomplete:
                await ViewModel.TransitionAfterAccountDeletionAsync(result);
                return;
        }
    }

    private async Task ShowAccountDeletionMessageAsync(
        string title,
        string message,
        CancellationToken ct)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
            },
            CloseButtonText = "确定",
            XamlRoot = XamlRoot,
        };
        await DialogHelper.ShowTrackedAsync(dialog, ct: ct);
    }

    // ── 紧急访问入口 ─────────────────────────────────────────────────────────

    private void OnEmergencyAccessCardClick(object sender, RoutedEventArgs e)
    {
        global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>().GoToEmergencyAccess();
    }
}
