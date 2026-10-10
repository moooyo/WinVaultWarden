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
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    private bool _suppressPinToggle;
    private bool _suppressHelloToggle;
    private CancellationTokenSource? _accountDeletionCts;
    private string _sectionKey = "account";
    private bool _isSubpage;
    private CancellationTokenSource? _securitySummaryCancellation;
    public string CurrentSection => _sectionKey;
    public string ToggleState(bool value) => value ? "开" : "关";

    private void OnToggleSettingClick(object sender, RoutedEventArgs e)
    {
        if (sender is CommunityToolkit.WinUI.Controls.SettingsCard { Content: StackPanel panel }
            && panel.Children.OfType<ToggleSwitch>().FirstOrDefault() is { IsEnabled: true } toggle)
            toggle.IsOn = !toggle.IsOn;
    }

    public SettingsPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<SettingsViewModel>();
        InitializeComponent();
        Unloaded += OnSettingsPageUnloaded;
        KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && TryNavigateBack())
                e.Handled = true;
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.PropertyChanged -= OnSettingsPropertyChanged;
        ViewModel.PropertyChanged += OnSettingsPropertyChanged;

        ViewModel.ApplyPolicyRestrictions();
        _suppressPinToggle = true;
        PinToggle.IsOn = ViewModel.IsPinSet;
        PinToggle.IsEnabled = ViewModel.CanUsePinUnlock;
        _suppressPinToggle = false;

        _suppressHelloToggle = true;
        HelloToggle.IsOn = ViewModel.IsWindowsHelloEnabled;
        _suppressHelloToggle = false;

        _ = LoadWindowsHelloAvailabilityAsync();
        SelectSection(e.Parameter as string ?? _sectionKey);
    }

    public void SelectSection(string section)
    {
        CancelSecuritySummary();
        if (section == "emergency")
        {
            SelectSection("security");
            ShowSubpage("紧急访问");
            SettingsContentFrame.Navigate(typeof(EmergencyAccessPage));
            return;
        }
        _sectionKey = section switch
        {
            "data" => "import",
            "account" or "security" or "ssh" or "devices" or "import" or "appearance" or "about" => section,
            _ => "account",
        };
        _isSubpage = false;
        SettingsContentFrame.Content = null;
        SubpageHost.Visibility = Visibility.Collapsed;
        SettingsScrollViewer.Visibility = Visibility.Visible;
        SettingsFeedbackInfoBar.IsOpen = false;
        ViewModel.OperationError = string.Empty;
        AccountSettingsSection.Visibility = Visibility.Collapsed;
        SecuritySettingsSection.Visibility = Visibility.Collapsed;
        SshSettingsSection.Visibility = Visibility.Collapsed;
        PreferencesSettingsSection.Visibility = Visibility.Collapsed;
        AboutSettingsSection.Visibility = Visibility.Collapsed;
        switch (_sectionKey)
        {
            case "account":
                ViewModel.SelectedSectionIndex = 1;
                SectionTitle.Text = "账户";
                AccountSettingsSection.Visibility = Visibility.Visible;
                break;
            case "security":
                ViewModel.SelectedSectionIndex = 0;
                SectionTitle.Text = "安全";
                SecuritySettingsSection.Visibility = Visibility.Visible;
                _ = RefreshSecuritySummaryAsync();
                break;
            case "ssh":
                SectionTitle.Text = "SSH 代理";
                SshSettingsSection.Visibility = Visibility.Visible;
                break;
            case "appearance":
                ViewModel.SelectedSectionIndex = 2;
                SectionTitle.Text = "外观与行为";
                PreferencesSettingsSection.Visibility = Visibility.Visible;
                ViewModel.SelectedThemeIndex = AppPreferences.Current.ThemeIndex;
                break;
            case "about":
                ViewModel.SelectedSectionIndex = 3;
                SectionTitle.Text = "关于";
                AboutSettingsSection.Visibility = Visibility.Visible;
                break;
            case "devices":
                ShowSectionPage(typeof(DevicesPage));
                break;
            case "import":
                ShowSectionPage(typeof(ImportExportPage));
                break;
        }
        SettingsScrollViewer.ChangeView(0, 0, null, true);
    }

    public bool TryNavigateBack()
    {
        if (!_isSubpage)
            return false;
        SelectSection(_sectionKey);
        return true;
    }

    private void ShowSectionPage(Type pageType)
    {
        SettingsScrollViewer.Visibility = Visibility.Collapsed;
        SubpageHost.Visibility = Visibility.Visible;
        BreadcrumbContainer.Visibility = Visibility.Collapsed;
        SettingsContentFrame.Navigate(pageType);
    }

    private void ShowSubpage(string title)
    {
        CancelSecuritySummary();
        _isSubpage = true;
        SettingsScrollViewer.Visibility = Visibility.Collapsed;
        SubpageHost.Visibility = Visibility.Visible;
        BreadcrumbContainer.Visibility = Visibility.Visible;
        SettingsBreadcrumb.ItemsSource = new[] { _sectionKey == "account" ? "账户" : "安全", title };
        SettingsFeedbackInfoBar.IsOpen = false;
    }

    private void OnBreadcrumbItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0)
            TryNavigateBack();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= OnSettingsPropertyChanged;
        CancelAccountDeletion();
        base.OnNavigatedFrom(e);
    }

    private void OnSettingsPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnSettingsPropertyChanged;
        CancelAccountDeletion();
        SettingsContentFrame.Content = null;
        CancelSecuritySummary();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.SelectedSectionIndex))
            return;

        SettingsFeedbackInfoBar.IsOpen = false;
        DispatcherQueue.TryEnqueue(() => SettingsScrollViewer.ChangeView(0, 0, null, true));
    }

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

    private void CancelSecuritySummary()
    {
        var cancellation = Interlocked.Exchange(ref _securitySummaryCancellation, null);
        if (cancellation is null)
            return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        cancellation.Dispose();
    }

    private async Task RefreshSecuritySummaryAsync()
    {
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var expectedSession = session.CaptureLease();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(expectedSession.CancellationToken);
        _securitySummaryCancellation = cancellation;
        TwoFactorSummaryCard.Description = "正在读取两步登录设置…";
        EmergencySummaryCard.Description = "正在读取紧急联系人…";
        try
        {
            var providers = await global::App.App.Services.GetRequiredService<ITwoFactorUiService>()
                .ListProvidersAsync(cancellation.Token);
            if (!ReferenceEquals(_securitySummaryCancellation, cancellation) || !session.IsUnlocked(expectedSession))
                return;
            var names = providers.Where(provider => provider.Enabled && provider.Type != (int)TwoFactorProviderType.Remember)
                .Select(provider => SettingsViewModel.GetTwoFactorProviderDisplayName(provider.Type)).ToArray();
            TwoFactorSummaryCard.Description = names.Length == 0
                ? "未开启 · 主密码泄露时，它能挡住别人登录"
                : "已开启 · " + string.Join("、", names);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception)
        {
            if (ReferenceEquals(_securitySummaryCancellation, cancellation))
                TwoFactorSummaryCard.Description = "暂时无法读取状态，可以打开管理页重试";
        }
        try
        {
            var contacts = await global::App.App.Services.GetRequiredService<IEmergencyAccessUiService>()
                .GetTrustedAsync(cancellation.Token);
            if (!ReferenceEquals(_securitySummaryCancellation, cancellation) || !session.IsUnlocked(expectedSession))
                return;
            var requested = contacts.FirstOrDefault(contact => contact.Status == EmergencyAccessStatus.RecoveryInitiated);
            EmergencySummaryCard.Description = requested is null
                ? $"{contacts.Count} 位紧急联系人"
                : $"{requested.Name ?? requested.Email ?? "联系人"} 发起了访问请求，等你处理";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (ReferenceEquals(_securitySummaryCancellation, cancellation))
                EmergencySummaryCard.Description = "暂时无法读取状态，可以打开管理页重试";
        }
        finally
        {
            Interlocked.CompareExchange(ref _securitySummaryCancellation, null, cancellation);
        }
    }

    public void RefreshPolicyRestrictions()
    {
        ViewModel.ApplyPolicyRestrictions();
        _suppressPinToggle = true;
        PinToggle.IsOn = ViewModel.IsPinSet;
        _suppressPinToggle = false;
    }

    private async Task LoadWindowsHelloAvailabilityAsync()
    {
        await ViewModel.RefreshWindowsHelloAvailabilityAsync();

        _suppressHelloToggle = true;
        HelloToggle.IsEnabled = ViewModel.CanToggleWindowsHello;
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
        try
        {
            var package = new DataPackage();
            package.SetText(AboutInfo.ToDiagnosticsText());
            Clipboard.SetContent(package);
            SettingsFeedbackInfoBar.Severity = InfoBarSeverity.Success;
            SettingsFeedbackInfoBar.Message = "诊断信息已复制。";
        }
        catch
        {
            SettingsFeedbackInfoBar.Severity = InfoBarSeverity.Error;
            SettingsFeedbackInfoBar.Message = "无法写入剪贴板，请重试。";
        }
        SettingsFeedbackInfoBar.IsOpen = true;
    }

    private ScrollViewer CreateDialogBody(UIElement content, double preferredWidth = 432)
    {
        var scroller = new ScrollViewer
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var root = XamlRoot;
        void ResizeBody()
        {
            scroller.Width = ResponsiveDialogSizing.Clamp(root.Size.Width, preferredWidth, 96);
            scroller.MaxHeight = Math.Max(0, root.Size.Height - 200);
        }
        void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeBody();
        scroller.Loaded += (_, _) =>
        {
            ResizeBody();
            root.Changed += OnRootChanged;
        };
        scroller.Unloaded += (_, _) => root.Changed -= OnRootChanged;
        ResizeBody();
        return scroller;
    }

    // ── 编辑资料 ─────────────────────────────────────────────────────────────

    private async void OnEditProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !button.IsEnabled)
            return;
        button.IsEnabled = false;
        try
        {
            await EditProfileAsync();
        }
        catch (Exception)
        {
            if (IsLoaded)
                ShowFeedback("无法打开名称编辑，请稍后重试。", InfoBarSeverity.Error);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task EditProfileAsync()
    {
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        Core.Session.VaultSessionLease expectedSession;
        try { expectedSession = session.CaptureLease(); }
        catch (InvalidOperationException) { return; }
        if (!session.IsUnlocked(expectedSession))
            return;
        var lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
        var sync = global::App.App.Services.GetRequiredService<ISyncService>();
        var nameBox = new TextBox
        {
            Header = "名称",
            PlaceholderText = "名称",
            Text = ViewModel.AccountName,
            MaxLength = 50,
            MinWidth = 0,
        };
        AutomationProperties.SetAutomationId(nameBox, "EditProfileNameBox");

        var errorText = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(errorText, "EditProfileErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(nameBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "编辑名称",
            Content = CreateDialogBody(panel),
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        ViewModel.OperationError = string.Empty;

        while (true)
        {
            var result = await DialogHelper.ShowTrackedAsync(dialog, expectedSession);
            if (result != ContentDialogResult.Primary)
                return;

            var operation = await ViewModel.RenameAsync(
                nameBox.Text.Trim(),
                expectedSession.CancellationToken,
                token => sync.SyncAsync(expectedSession, token));

            if (operation.IsSuccess)
            {
                lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(expectedSession, () =>
                {
                    if (!ViewModel.ProfileRefreshPending)
                    {
                        try { global::App.App.MainWindow?.RefreshVaultSnapshot(); }
                        catch (Exception) { ViewModel.ProfileRefreshPending = true; }
                    }
                    ShowFeedback(ViewModel.ProfileRefreshPending
                        ? "已更新名称，账户信息将在下次同步时刷新"
                        : "已更新名称");
                }));
                return;
            }
            if (operation.Status != UiOperationStatus.Failed)
                return;

            errorText.Text = operation.Error ?? "操作失败，请稍后重试";
            errorText.Visibility = Visibility.Visible;
        }
    }

    // ── 修改主密码 ───────────────────────────────────────────────────────────

    private void OnChangePasswordCardClick(object sender, RoutedEventArgs e)
    {
        ShowSubpage("更改主密码");
        var page = new SettingsPasswordPage();
        page.Initialize(ViewModel, () => SelectSection("account"));
        SettingsContentFrame.Content = page;
    }
    private async void OnChangeKdfCardClick(object sender, RoutedEventArgs e)
    {
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var expectedSession = session.CaptureLease();
        if (!ViewModel.TryReadCurrentKdf(out var kdfType, out var iterations, out _, out _))
        {
            ShowFeedback("无法读取当前账户的加密参数，请重新登录后重试。", InfoBarSeverity.Error);
            return;
        }
        if (kdfType != KdfType.Pbkdf2)
        {
            ShowFeedback("当前账户使用 Argon2id；此版本暂不支持编辑其参数或切换加密算法。", InfoBarSeverity.Informational);
            return;
        }
        var verifiedPassword = await PromptVerifiedMasterPasswordAsync("更改加密方式前，需要验证主密码。");
        if (verifiedPassword is null)
            return;
        var currentBox = new PasswordBox { Password = verifiedPassword };
        verifiedPassword = string.Empty;
        AutomationProperties.SetAutomationId(currentBox, "ChangeKdfCurrentBox");

        var iterationsBox = new NumberBox
        {
            Header = "PBKDF2 迭代次数",
            Minimum = KdfParameterPolicy.MinimumPbkdf2WriteIterations,
            Maximum = KdfParameterPolicy.MaximumPbkdf2WriteIterations,
            Value = Math.Clamp(iterations, KdfParameterPolicy.MinimumPbkdf2WriteIterations, KdfParameterPolicy.MaximumPbkdf2WriteIterations),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 100000,
            LargeChange = 500000,
        };
        AutomationProperties.SetAutomationId(iterationsBox, "ChangeKdfIterationsBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(errorText, "ChangeKdfErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "此版本支持调整 PBKDF2-SHA256 迭代次数；暂不支持切换到 Argon2id。",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(iterationsBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = "更改加密方式",
            Content = CreateDialogBody(panel),
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
                    expectedSession,
                    () => currentBox.Password = string.Empty);
                if (result != ContentDialogResult.Primary)
                    return;

                var requestedIterations = double.IsNaN(iterationsBox.Value) ? iterations : (int)iterationsBox.Value;
                var operation = await ViewModel.ChangeIterationsAsync(currentBox.Password, requestedIterations);

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
        try
        {
            if (PinToggle.IsOn)
                await SetPinFromDialogAsync(false);
            else
            {
                ViewModel.ClearPin();
                ShowFeedback("已关闭 PIN 解锁");
            }
        }
        catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex)) { }
        catch (Exception)
        {
            if (IsLoaded)
                ShowFeedback(ViewModel.SupportsLocalUnlock ? "无法更新 PIN 解锁，请稍后重试。" : ViewModel.PinUnlockDescription, InfoBarSeverity.Error);
        }
        finally
        {
            RestorePinToggle();
        }
    }

    private async void OnChangePinClick(object sender, RoutedEventArgs e)
    {
        try { await SetPinFromDialogAsync(true); }
        catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex)) { }
        catch (Exception)
        {
            if (IsLoaded)
                ShowFeedback(ViewModel.SupportsLocalUnlock ? "无法更新 PIN 解锁，请稍后重试。" : ViewModel.PinUnlockDescription, InfoBarSeverity.Error);
        }
        finally { RestorePinToggle(); }
    }

    private async Task SetPinFromDialogAsync(bool replacing)
    {
        if (!ViewModel.CanUsePinUnlock)
        {
            ShowFeedback(ViewModel.PinUnlockDescription, InfoBarSeverity.Informational);
            return;
        }
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var expectedSession = session.CaptureLease();
        var pin = await PromptSetPinAsync();
        if (pin is null)
            return;
        var lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
        if (lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(expectedSession, () => ViewModel.SetPin(pin))))
            ShowFeedback(replacing ? "已更改 PIN" : "已开启 PIN 解锁");
    }

    private void RestorePinToggle()
    {
        _suppressPinToggle = true;
        try
        {
            PinToggle.IsOn = ViewModel.IsPinSet;
            PinToggle.IsEnabled = ViewModel.CanUsePinUnlock;
        }
        finally { _suppressPinToggle = false; }
    }
    private async Task<string?> PromptSetPinAsync()
    {
        var pinBox = new PasswordBox { Header = $"PIN（至少 {Core.Services.PinPolicy.MinLength} 位）", PlaceholderText = $"PIN（至少 {Core.Services.PinPolicy.MinLength} 位）", MinWidth = 0 };
        AutomationProperties.SetAutomationId(pinBox, "SetPinBox");

        var confirmBox = new PasswordBox { Header = "确认 PIN", PlaceholderText = "确认 PIN" };
        AutomationProperties.SetAutomationId(confirmBox, "SetPinConfirmBox");

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(errorText, "SetPinErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(pinBox);
        panel.Children.Add(confirmBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = ViewModel.IsPinSet ? "更改 PIN" : "设置 PIN",
            Content = CreateDialogBody(panel),
            PrimaryButtonText = ViewModel.IsPinSet ? "更改" : "设置",
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
            var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
            var expectedSession = session.CaptureLease();
            var lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
            var ok = await ViewModel.EnableWindowsHelloAsync(
                action => lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(expectedSession, action)),
                expectedSession.CancellationToken);
            if (!session.IsUnlocked(expectedSession))
                return;
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
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var expectedSession = session.CaptureLease();
        var verifiedPassword = await PromptVerifiedMasterPasswordAsync("管理两步登录前，需要验证主密码。");
        if (verifiedPassword is null)
            return;
        WebAuthnManagementSnapshot? webAuthnSnapshot;
        try
        {
            var load = await ViewModel.LoadWebAuthnAsync(verifiedPassword, expectedSession.CancellationToken);
            if (!session.IsUnlocked(expectedSession))
                return;
            webAuthnSnapshot = load.Value;
        }
        finally
        {
            verifiedPassword = string.Empty;
        }
        ShowSubpage("两步登录");
        var page = new SettingsTwoFactorPage();
        page.Initialize(
            ViewModel,
            ShowEnableTotpDialogAsync,
            ShowEnableEmailDialogAsync,
            () => ShowManageWebAuthnDialogAsync(page.ApplyWebAuthnSnapshot, addOnly: true),
            ShowDisableTwoFactorDialogAsync,
            ShowRecoveryCodeDialogAsync,
            id => ShowManageWebAuthnDialogAsync(page.ApplyWebAuthnSnapshot, removeId: id),
            webAuthnSnapshot,
            () => ShowManageWebAuthnDialogAsync(page.ApplyWebAuthnSnapshot, listOnly: true));
        SettingsContentFrame.Content = page;
    }
    private async Task ShowEnableTotpDialogAsync()
    {
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var expectedSession = session.CaptureLease();
        if (!session.IsUnlocked(expectedSession))
            return;
        TotpSetupCredentialsPanel? credentialsPanel = null;
        // Step 1: authenticate before requesting a setup secret.
        var pwBox = new PasswordBox { Header = "当前主密码", PlaceholderText = "当前主密码", MinWidth = 0 };
        AutomationProperties.SetAutomationId(pwBox, "TotpSetupPasswordBox");

        var step1Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step1Error, "TotpSetupStep1ErrorText");
        step1Error.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var step1Panel = new StackPanel { Spacing = 8 };
        step1Panel.Children.Add(new TextBlock { Text = "请输入主密码以开始设置 TOTP 验证器", TextWrapping = TextWrapping.Wrap });
        step1Panel.Children.Add(pwBox);
        step1Panel.Children.Add(step1Error);

        var step1Dialog = new ContentDialog
        {
            Title = "验证主密码",
            Content = CreateDialogBody(step1Panel),
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
                    expectedSession,
                    () => pwBox.Password = string.Empty);
                if (r1 != ContentDialogResult.Primary) return;

                var operation = await ViewModel.BeginTotpSetupAsync(pwBox.Password, expectedSession.CancellationToken);
                if (!session.IsUnlocked(expectedSession))
                    return;

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

        // Step 2: render the actual setup URI locally and keep manual entry available.
        credentialsPanel = await TotpSetupCredentialsPanel.CreateAsync(secret, otpauth, expectedSession);
        if (credentialsPanel is null)
            return;
        var codeBox = new TextBox { PlaceholderText = "6 位验证码", MaxLength = 6, Width = 160, HorizontalAlignment = HorizontalAlignment.Left, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        AutomationProperties.SetAutomationId(codeBox, "TotpSetupCodeBox");

        var step2Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step2Error, "TotpSetupStep2ErrorText");
        step2Error.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var step2Panel = new StackPanel { Spacing = 8, MinWidth = 0 };
        step2Panel.Children.Add(new TextBlock
        {
            Text = "用 Microsoft Authenticator、Google Authenticator 等应用扫描二维码，再输入应用里显示的 6 位验证码。",
            TextWrapping = TextWrapping.Wrap,
        });
        step2Panel.Children.Add(credentialsPanel);
        step2Panel.Children.Add(new TextBlock { Text = "验证码", Margin = new Thickness(0, 8, 0, 0) });
        step2Panel.Children.Add(codeBox);
        step2Panel.Children.Add(step2Error);

        var step2Dialog = new ContentDialog
        {
            Title = "设置身份验证器应用",
            Content = CreateDialogBody(step2Panel, 472),
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
                expectedSession,
                () =>
                {
                    pwBox.Password = string.Empty;
                    codeBox.Text = string.Empty;
                    credentialsPanel.ClearSensitive();
                    secret = string.Empty;
                    otpauth = string.Empty;
                });
            if (r2 != ContentDialogResult.Primary) return;

            var operation = await ViewModel.EnableTotpAsync(
                pwBox.Password,
                secret,
                codeBox.Text.Trim(), expectedSession.CancellationToken);
            if (!session.IsUnlocked(expectedSession))
                return;

            if (operation.IsSuccess)
            {
                activation = operation.Value!;
                pwBox.Password = string.Empty;
                codeBox.Text = string.Empty;
                credentialsPanel.ClearSensitive();
                otpauth = string.Empty;
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

        // Step 3: show the server-issued recovery code only after activation succeeds.
        var recoveryCode = activation.RecoveryCode ?? string.Empty;
        var rcText = new TextBlock
        {
            Text = recoveryCode,
            Visibility = activation.HasRecoveryCode ? Visibility.Visible : Visibility.Collapsed,
            IsTextSelectionEnabled = false,
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

        var step3Panel = new StackPanel { Spacing = 8, MinWidth = 0 };
        step3Panel.Children.Add(new TextBlock { Text = "TOTP 验证器已启用。请妥善保存恢复码，账户锁定时可凭此找回。", TextWrapping = TextWrapping.Wrap });
        step3Panel.Children.Add(recoveryWarning);
        step3Panel.Children.Add(rcText);
        step3Panel.Children.Add(recoveryPasswordBox);

        var step3Dialog = new ContentDialog
        {
            Title = "TOTP 启用成功",
            Content = CreateDialogBody(step3Panel),
            PrimaryButtonText = activation.HasRecoveryCode ? "复制" : "重新获取恢复码",
            CloseButtonText = "完成",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };

        while (true)
        {
            var r3 = await DialogHelper.ShowTrackedAsync(
                step3Dialog,
                expectedSession,
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

            if (!string.IsNullOrEmpty(rcText.Text))
            {
                var lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
                lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(expectedSession, () =>
                    global::App.App.Services.GetRequiredService<IClipboardService>().SetSecretText(rcText.Text)));
                break;
            }

            UiOperationResult<string> recoveryOperation;
            try
            {
                recoveryOperation = await ViewModel.GetRecoveryCodeAsync(recoveryPasswordBox.Password, expectedSession.CancellationToken);
            }
            finally
            {
                recoveryPasswordBox.Password = string.Empty;
            }

            if (!session.IsUnlocked(expectedSession))
                return;

            if (recoveryOperation.IsSuccess)
            {
                rcText.Text = recoveryOperation.Value!;
                rcText.Visibility = Visibility.Visible;
                recoveryWarning.IsOpen = false;
                recoveryPasswordBox.Visibility = Visibility.Collapsed;
                step3Dialog.PrimaryButtonText = "复制";
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
            credentialsPanel?.Dispose();
            secret = string.Empty;
            otpauth = string.Empty;
            pwBox.Password = string.Empty;
        }
    }

    // ── Email 两步验证启用对话框（两步：密码+邮箱 → 验证码） ─────────────────

    private async Task ShowEnableEmailDialogAsync()
    {
        // 步骤 1：密码 + 邮箱
        var emailPwBox = new PasswordBox { Header = "当前主密码", PlaceholderText = "当前主密码", MinWidth = 0 };
        AutomationProperties.SetAutomationId(emailPwBox, "EmailSetupPasswordBox");

        var emailAddressBox = new TextBox { PlaceholderText = "验证邮箱地址" };
        AutomationProperties.SetAutomationId(emailAddressBox, "EmailSetupAddressBox");

        var step1Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step1Error, "EmailSetupStep1ErrorText");
        step1Error.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var step1Panel = new StackPanel { Spacing = 8 };
        step1Panel.Children.Add(new TextBlock { Text = "请输入主密码和接收验证码的邮箱地址", TextWrapping = TextWrapping.Wrap });
        step1Panel.Children.Add(emailPwBox);
        step1Panel.Children.Add(emailAddressBox);
        step1Panel.Children.Add(step1Error);

        var step1Dialog = new ContentDialog
        {
            Title = "启用邮箱验证码 — 第 1 步",
            Content = CreateDialogBody(step1Panel),
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
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var step2Panel = new StackPanel { Spacing = 8, MinWidth = 0 };
        step2Panel.Children.Add(new TextBlock { Text = $"验证码已发送至 {emailAddressBox.Text.Trim()}，请输入：", TextWrapping = TextWrapping.Wrap });
        step2Panel.Children.Add(tokenBox);
        step2Panel.Children.Add(step2Error);

        var step2Dialog = new ContentDialog
        {
            Title = "启用邮箱验证码 — 第 2 步",
            Content = CreateDialogBody(step2Panel),
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

    private async Task ShowManageWebAuthnDialogAsync(
        Action<WebAuthnManagementSnapshot> publishSnapshot,
        bool listOnly = false,
        bool addOnly = false,
        int? removeId = null)
    {
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var expectedSession = session.CaptureLease();
        using var managementCts = CancellationTokenSource.CreateLinkedTokenSource(expectedSession.CancellationToken);
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
            MinWidth = 0,
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
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));
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
            Content = CreateDialogBody(passwordPanel),
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
                    expectedSession,
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
                    if (!session.IsUnlocked(expectedSession))
                        return;
                    publishSnapshot(snapshot);
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

            if (listOnly || removeId.HasValue && snapshot.Credentials.All(credential => credential.Id != removeId.Value))
                return;

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

                WebAuthnCredentialListItem? deleteTarget = removeId.HasValue
                    ? snapshot.Credentials.FirstOrDefault(credential => credential.Id == removeId.Value)
                    : null;
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
                    Visibility = addOnly ? Visibility.Collapsed : Visibility.Visible,
                    MaxHeight = 280,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollMode = ScrollMode.Auto,
                    Content = credentialPanel,
                };

                var content = new StackPanel
                {
                    MinWidth = 0,
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
                    Title = "添加安全密钥",
                    Content = CreateDialogBody(content),
                    PrimaryButtonText = "添加安全密钥",
                    IsPrimaryButtonEnabled = ViewModel.CanRegisterWebAuthn,
                    CloseButtonText = "完成",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };

                var action = removeId.HasValue ? ContentDialogResult.None : await DialogHelper.ShowTrackedAsync(
                    managementDialog,
                    expectedSession,
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
                        expectedSession,
                        "删除安全密钥",
                        isLastCredential
                            ? "这是最后一把安全密钥。删除后将关闭 WebAuthn 双重验证，确定继续吗？"
                            : $"确定删除“{deleteTarget.Name}”吗？",
                        "删除",
                        ct: managementCts.Token);
                    if (!confirmed)
                    {
                        if (managementCts.IsCancellationRequested || removeId.HasValue)
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
                        if (!session.IsUnlocked(expectedSession))
                            return;
                        publishSnapshot(snapshot);
                        statusMessage = isLastCredential
                            ? "WebAuthn 双重验证已关闭。"
                            : "安全密钥已删除。";
                        statusSeverity = InfoBarSeverity.Success;
                        if (removeId.HasValue)
                        {
                            ShowFeedback(statusMessage);
                            return;
                        }
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
                    if (removeId.HasValue)
                    {
                        ShowFeedback(statusMessage, statusSeverity);
                        return;
                    }
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
                {
                    snapshot = registration.Value;
                    if (!session.IsUnlocked(expectedSession))
                        return;
                    publishSnapshot(snapshot);
                }

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
                if (addOnly)
                {
                    ShowFeedback(statusMessage, statusSeverity);
                    return;
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

        var disablePwBox = new PasswordBox { Header = "当前主密码", PlaceholderText = "当前主密码", MinWidth = 0 };
        AutomationProperties.SetAutomationId(disablePwBox, "DisableTwoFactorPasswordBox");

        var providerCombo = new ComboBox { MinWidth = 0, PlaceholderText = "选择要禁用的验证方式" };
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
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var disablePanel = new StackPanel { Spacing = 8 };
        disablePanel.Children.Add(new TextBlock { Text = "选择要禁用的验证方式并输入主密码确认：", TextWrapping = TextWrapping.Wrap });
        disablePanel.Children.Add(providerCombo);
        disablePanel.Children.Add(disablePwBox);
        disablePanel.Children.Add(disableError);

        var disableDialog = new ContentDialog
        {
            Title = "禁用双重验证",
            Content = CreateDialogBody(disablePanel),
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
            MinWidth = 0,
            PlaceholderText = preflight.CredentialRequirement
                == AccountDeletionCredentialRequirement.MasterPassword
                    ? "当前主密码"
                    : "邮件验证码",
        };
        activeSecretBox = credentialBox;
        credentialBox.Header = credentialBox.PlaceholderText;
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
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));
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
            Content = CreateDialogBody(credentialPanel),
            PrimaryButtonText = "永久删除账户",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        AutomationProperties.SetAutomationId(
            credentialDialog,
            "DeleteAccountCredentialDialog");
        void OnCredentialChanged(object sender, RoutedEventArgs args) =>
            credentialDialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(credentialBox.Password);
        credentialBox.PasswordChanged += OnCredentialChanged;

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
            credentialBox.PasswordChanged -= OnCredentialChanged;
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
        var panel = new StackPanel { Spacing = 12, MinWidth = 0 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{preflight.Binding.NormalizedEmail}\n{preflight.Binding.Endpoints.WebVault.Value}",
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = false,
        });
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
            Content = CreateDialogBody(panel),
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
            MinWidth = 0,
        };
        AutomationProperties.SetAutomationId(emailBox, "DeleteAccountEmailConfirmationBox");
        var error = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
        };
        error.SetValue(
            TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));
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
            Content = CreateDialogBody(panel),
            PrimaryButtonText = "继续",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        AutomationProperties.SetAutomationId(dialog, "DeleteAccountEmailConfirmationDialog");
        void OnEmailConfirmationChanged(object sender, TextChangedEventArgs args) =>
            dialog.IsPrimaryButtonEnabled = string.Equals(emailBox.Text, normalizedEmail, StringComparison.Ordinal);
        emailBox.TextChanged += OnEmailConfirmationChanged;

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
            emailBox.TextChanged -= OnEmailConfirmationChanged;
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
        SelectSection("emergency");
    }

    private void ShowFeedback(string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        SettingsFeedbackInfoBar.Message = message;
        SettingsFeedbackInfoBar.Severity = severity;
        SettingsFeedbackInfoBar.IsOpen = true;
    }

    private async Task<bool> VerifyMasterPasswordAsync(string reason) =>
        await PromptVerifiedMasterPasswordAsync(reason) is not null;

    private async Task<string?> PromptVerifiedMasterPasswordAsync(string reason)
    {
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var expectedSession = session.CaptureLease();
        var password = new PasswordBox { Header = "主密码", MinWidth = 280 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        error.SetValue(TextBlock.ForegroundProperty, global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(password);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "验证主密码",
            Content = CreateDialogBody(content),
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(expectedSession.CancellationToken);
        using var registration = global::App.App.Services.GetRequiredService<ITransientOperationRegistry>()
            .Register(cancellation.Cancel, () => dialog.DispatcherQueue.TryEnqueue(dialog.Hide), () => password.Password = string.Empty);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (await DialogHelper.ShowTrackedAsync(dialog, expectedSession, () => password.Password = string.Empty, ct: cancellation.Token)
                    != ContentDialogResult.Primary)
                    return null;
                if (string.IsNullOrEmpty(password.Password))
                {
                    error.Text = "请输入主密码";
                    error.Visibility = Visibility.Visible;
                    continue;
                }
                if (await global::App.App.Services.GetRequiredService<IAccountService>()
                    .VerifyMasterPasswordAsync(password.Password, cancellation.Token))
                    return session.IsUnlocked(expectedSession) ? password.Password : null;
                error.Text = "主密码不正确，请重试";
                error.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            ShowFeedback("暂时无法验证主密码，请重试。", InfoBarSeverity.Error);
        }
        finally
        {
            password.Password = string.Empty;
        }
        return null;
    }

    private async Task ShowRecoveryCodeDialogAsync()
    {
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var expectedSession = session.CaptureLease();
        var lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
        var password = new PasswordBox { Header = "主密码", MinWidth = 280 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "查看恢复代码前，需要验证主密码。", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(password);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = "恢复代码",
            Content = CreateDialogBody(content),
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        string recovery = string.Empty;
        try
        {
            while (true)
            {
                if (await DialogHelper.ShowTrackedAsync(dialog, expectedSession, () => password.Password = string.Empty)
                    != ContentDialogResult.Primary)
                    return;
                session.ThrowIfExpired(expectedSession);
                var result = await ViewModel.GetRecoveryCodeAsync(password.Password);
                if (result.IsSuccess)
                {
                    session.ThrowIfExpired(expectedSession);
                    recovery = result.Value ?? string.Empty;
                    break;
                }
                if (result.Status != UiOperationStatus.Failed)
                    return;
                error.Text = result.Error ?? "无法获取恢复代码";
                error.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            password.Password = string.Empty;
        }

        var code = new TextBlock
        {
            Text = recovery,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            IsTextSelectionEnabled = false,
        };
        var body = new StackPanel { Spacing = 16 };
        body.Children.Add(new TextBlock
        {
            Text = "把它保存在密码库以外的安全地方，比如打印出来。丢失所有验证方式时用它登录，登录后两步登录会全部关闭。",
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new Border
        {
            Child = code,
            Padding = new Thickness(16, 14, 16, 14),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = global::App.Controls.ThemeBrushResolver.Resolve(this, "DividerStrokeColorDefaultBrush"),
        });
        var recoveryDialog = new ContentDialog
        {
            Title = "恢复代码",
            Content = CreateDialogBody(body),
            PrimaryButtonText = "复制",
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        try
        {
            if (await DialogHelper.ShowTrackedAsync(recoveryDialog, expectedSession, () =>
                {
                    code.Text = string.Empty;
                    recovery = string.Empty;
                })
                == ContentDialogResult.Primary)
            {
                if (lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(expectedSession, () =>
                    global::App.App.Services.GetRequiredService<IClipboardService>().SetSecretText(recovery))))
                    ShowFeedback("恢复代码已复制");
            }
        }
        finally
        {
            code.Text = string.Empty;
            recovery = string.Empty;
        }
    }

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "退出登录？",
            Content = new TextBlock { Text = "清除本机缓存的密码库，下次需要联网登录。", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "退出登录",
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
        };
        if (await DialogHelper.ShowTrackedAsync(dialog) == ContentDialogResult.Primary)
            await global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>().RequestAsync(AppLifecycleIntent.Logout);
    }

    private async void OnOpenSourceClick(object sender, RoutedEventArgs e) =>
        await OpenWebLinkAsync("https://github.com/moooyo/WinVaultWarden");

    private async void OnOpenReleasesClick(object sender, RoutedEventArgs e) =>
        await OpenWebLinkAsync("https://github.com/moooyo/WinVaultWarden/releases");

    private async Task OpenWebLinkAsync(string address)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri(address)))
                ShowFeedback("无法打开浏览器。", InfoBarSeverity.Error);
        }
        catch (Exception)
        {
            ShowFeedback("无法打开浏览器。", InfoBarSeverity.Error);
        }
    }

    private async void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinVaultWarden", "logs");
            Directory.CreateDirectory(path);
            var folder = await StorageFolder.GetFolderFromPathAsync(path);
            if (await Launcher.LaunchFolderAsync(folder))
                ShowFeedback("已在资源管理器中打开日志文件夹");
            else
                ShowFeedback("无法打开日志文件夹。", InfoBarSeverity.Error);
        }
        catch (Exception)
        {
            ShowFeedback("无法打开日志文件夹。", InfoBarSeverity.Error);
        }
    }
}
