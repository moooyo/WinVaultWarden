using App.Services;
using App.ViewModels;
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

    public SettingsPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        _suppressPinToggle = true;
        PinToggle.IsOn = ViewModel.IsPinSet;
        _suppressPinToggle = false;

        _suppressHelloToggle = true;
        HelloToggle.IsOn = ViewModel.IsWindowsHelloEnabled;
        _suppressHelloToggle = false;

        _ = LoadWindowsHelloAvailabilityAsync();
    }

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
            var disableButton = new Button { Content = "禁用已启用的验证", HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(disableButton, "TwoFactorDisableButton");
            var contentPanel = new StackPanel
            {
                Spacing = 12,
                Visibility = Visibility.Collapsed,
                Children = { infoText, enableTotpButton, enableEmailButton, disableButton },
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
                var totpEnabled = providers.Any(provider => provider.Type == 0 && provider.Enabled);
                var emailEnabled = providers.Any(provider => provider.Type == 1 && provider.Enabled);
                infoText.Text = providers.Count == 0
                    ? "尚未配置任何双重验证方式。"
                    : $"TOTP 验证器：{(totpEnabled ? "已启用" : "未启用")}　电子邮件验证码：{(emailEnabled ? "已启用" : "未启用")}";
                contentPanel.Visibility = Visibility.Visible;
            }

            retryButton.Click += async (_, _) => await LoadAsync();

            string? action = null;
            enableTotpButton.Click  += (_, _) => { action = "totp";    menuDialog.Hide(); };
            enableEmailButton.Click += (_, _) => { action = "email";   menuDialog.Hide(); };
            disableButton.Click     += (_, _) => { action = "disable"; menuDialog.Hide(); };

            menuDialog.Opened += async (_, _) => await LoadAsync();

            await DialogHelper.ShowTrackedAsync(menuDialog);

            switch (action)
            {
                case "totp":    await ShowEnableTotpDialogAsync(); break;
                case "email":   await ShowEnableEmailDialogAsync(); break;
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

        // 构建下拉列表
        static string ProviderName(int type) => type switch
        {
            0 => "TOTP 验证器（类型 0）",
            1 => "邮箱验证码（类型 1）",
            3 => "YubiKey OTP（类型 3）",
            4 => "FIDO2/WebAuthn（类型 4）",
            5 => "Duo（类型 5）",
            _ => $"未知（类型 {type}）",
        };

        var disablePwBox = new PasswordBox { PlaceholderText = "当前主密码", MinWidth = 360 };
        AutomationProperties.SetAutomationId(disablePwBox, "DisableTwoFactorPasswordBox");

        var providerCombo = new ComboBox { MinWidth = 280, PlaceholderText = "选择要禁用的验证方式" };
        AutomationProperties.SetAutomationId(providerCombo, "DisableTwoFactorProviderCombo");

        foreach (var p in enabledProviders)
            providerCombo.Items.Add(new ComboBoxItem { Content = ProviderName(p.Type), Tag = p.Type });

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

    // ── 紧急访问入口 ─────────────────────────────────────────────────────────

    private void OnEmergencyAccessCardClick(object sender, RoutedEventArgs e)
    {
        global::App.App.Services.GetRequiredService<global::App.Services.INavigationService>().GoToEmergencyAccess();
    }
}
