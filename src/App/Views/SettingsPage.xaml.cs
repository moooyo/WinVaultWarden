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
    public string ToggleState(bool value) => value ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text39EAE64CFC41EF37) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5D0AE622F61F8EBF);

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
        _ = InitializePlatformSettingsAsync();
        InitializeRuntimeIntegrationSettings();
        InitializeLanguageSettings();
    }

    public void SelectSection(string section)
    {
        CancelSecuritySummary();
        if (section == "emergency")
        {
            SelectSection("security");
            ShowSubpage(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3F1ADE0818888D21));
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
                SectionTitle.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text061246BD31E084A7);
                AccountSettingsSection.Visibility = Visibility.Visible;
                break;
            case "security":
                ViewModel.SelectedSectionIndex = 0;
                SectionTitle.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAFB63A620BDCFF15);
                SecuritySettingsSection.Visibility = Visibility.Visible;
                _ = RefreshSecuritySummaryAsync();
                break;
            case "ssh":
                SectionTitle.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3F5E6929157C5E75);
                SshSettingsSection.Visibility = Visibility.Visible;
                break;
            case "appearance":
                ViewModel.SelectedSectionIndex = 2;
                SectionTitle.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0371F8EADCAA92A3);
                PreferencesSettingsSection.Visibility = Visibility.Visible;
                ViewModel.SelectedThemeIndex = AppPreferences.Current.ThemeIndex;
                break;
            case "about":
                ViewModel.SelectedSectionIndex = 3;
                SectionTitle.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text52D25A9E30BA94F1);
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
        SettingsBreadcrumb.ItemsSource = new[] { _sectionKey == "account" ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text061246BD31E084A7) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAFB63A620BDCFF15), title };
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
        TwoFactorSummaryCard.Description = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text823FDA7C625753B4);
        EmergencySummaryCard.Description = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text60B82A5A1837BD24);
        try
        {
            var providers = await global::App.App.Services.GetRequiredService<ITwoFactorUiService>()
                .ListProvidersAsync(cancellation.Token);
            if (!ReferenceEquals(_securitySummaryCancellation, cancellation) || !session.IsUnlocked(expectedSession))
                return;
            var names = providers.Where(provider => provider.Enabled && provider.Type != (int)TwoFactorProviderType.Remember)
                .Select(provider => SettingsViewModel.GetTwoFactorProviderDisplayName(provider.Type)).ToArray();
            TwoFactorSummaryCard.Description = names.Length == 0
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBCB6F99FE1930A07)
                : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC72200309BD0199F) + string.Join("、", names);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        catch (Exception)
        {
            if (ReferenceEquals(_securitySummaryCancellation, cancellation))
                TwoFactorSummaryCard.Description = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9DE4868F4C663761);
        }
        try
        {
            var contacts = await global::App.App.Services.GetRequiredService<IEmergencyAccessUiService>()
                .GetTrustedAsync(cancellation.Token);
            if (!ReferenceEquals(_securitySummaryCancellation, cancellation) || !session.IsUnlocked(expectedSession))
                return;
            var requested = contacts.FirstOrDefault(contact => contact.Status == EmergencyAccessStatus.RecoveryInitiated);
            EmergencySummaryCard.Description = requested is null
                ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text05359BDEB5A27D76,contacts.Count)
                : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text70D67DBB4C49E20D,requested.Name ?? requested.Email ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3303B56982A00FD1));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (ReferenceEquals(_securitySummaryCancellation, cancellation))
                EmergencySummaryCard.Description = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9DE4868F4C663761);
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
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD05226AEDC48241B),
            Content = result.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB10D00FCBB7711DC),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFAC2A67AD87807C4),
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
            SettingsFeedbackInfoBar.Message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text70F64FD35FD9AE2A);
        }
        catch
        {
            SettingsFeedbackInfoBar.Severity = InfoBarSeverity.Error;
            SettingsFeedbackInfoBar.Message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextED4245AB2EA447AA);
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
                ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text21FAE05EFF8ABFB5), InfoBarSeverity.Error);
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
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD44E9B3D3B31D37B),
            PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD44E9B3D3B31D37B),
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
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD4D65E113BC6FA98),
            Content = CreateDialogBody(panel),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA3030BF8F16DC63C),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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
                        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1D219BF1C803D6C4)
                        : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9A13B5A596291322));
                }));
                return;
            }
            if (operation.Status != UiOperationStatus.Failed)
                return;

            errorText.Text = operation.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB10D00FCBB7711DC);
            errorText.Visibility = Visibility.Visible;
        }
    }

    // ── 修改主密码 ───────────────────────────────────────────────────────────

    private void OnChangePasswordCardClick(object sender, RoutedEventArgs e)
    {
        ShowSubpage(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6B444E0F3275A327));
        var page = new SettingsPasswordPage();
        page.Initialize(ViewModel, () => SelectSection("account"));
        SettingsContentFrame.Content = page;
    }
    private async void OnChangeKdfCardClick(object sender, RoutedEventArgs e)
    {
        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var expectedSession = session.CaptureLease();
        if (!ViewModel.TryReadCurrentKdf(out var kdfType, out var iterations, out var memory, out var parallelism))
        {
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCC9373B965DAF537), InfoBarSeverity.Error);
            return;
        }
        var verifiedPassword = await PromptVerifiedMasterPasswordAsync(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAF4F27631E272943));
        if (verifiedPassword is null)
            return;
        var currentBox = new PasswordBox { Password = verifiedPassword };
        verifiedPassword = string.Empty;
        AutomationProperties.SetAutomationId(currentBox, "ChangeKdfCurrentBox");

        var algorithmBox = new ComboBox
        {
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8D7021328B651D20),
            ItemsSource = new[] { "PBKDF2-SHA256", "Argon2id" },
            SelectedIndex = kdfType == KdfType.Argon2id ? 1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(algorithmBox, "ChangeKdfAlgorithmBox");
        var iterationsBox = new NumberBox
        {
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5D3E914B37EE0F55),
            Minimum = KdfParameterPolicy.MinimumPbkdf2WriteIterations,
            Maximum = KdfParameterPolicy.MaximumPbkdf2WriteIterations,
            Value = Math.Clamp(iterations, KdfParameterPolicy.MinimumPbkdf2WriteIterations, KdfParameterPolicy.MaximumPbkdf2WriteIterations),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 100000,
            LargeChange = 500000,
        };
        AutomationProperties.SetAutomationId(iterationsBox, "ChangeKdfIterationsBox");

        var memoryBox = new NumberBox
        {
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA83563731FF5A4E5),
            Minimum = KdfParameterPolicy.MinimumArgon2MemoryMiB,
            Maximum = KdfParameterPolicy.MaximumArgon2MemoryMiB,
            Value = memory ?? 64,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 16,
            LargeChange = 64,
        };
        var parallelismBox = new NumberBox
        {
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text69585FB8AA14E9DD),
            Minimum = KdfParameterPolicy.MinimumArgon2Parallelism,
            Maximum = KdfParameterPolicy.MaximumArgon2Parallelism,
            Value = parallelism ?? 4,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            SmallChange = 1,
        };
        AutomationProperties.SetAutomationId(memoryBox, "ChangeKdfMemoryBox");
        AutomationProperties.SetAutomationId(parallelismBox, "ChangeKdfParallelismBox");

        void SetAlgorithm(bool useDefaults)
        {
            var argon2 = algorithmBox.SelectedIndex == 1;
            iterationsBox.Header = argon2 ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8DEB03AB08E3FBA9) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text21A701E45A06E592);
            iterationsBox.Minimum = argon2
                ? KdfParameterPolicy.MinimumArgon2ReadIterations
                : KdfParameterPolicy.MinimumPbkdf2WriteIterations;
            iterationsBox.Maximum = argon2
                ? KdfParameterPolicy.MaximumArgon2ReadIterations
                : KdfParameterPolicy.MaximumPbkdf2WriteIterations;
            iterationsBox.SmallChange = argon2 ? 1 : 100_000;
            iterationsBox.LargeChange = argon2 ? 1 : 500_000;
            iterationsBox.Value = useDefaults
                ? argon2 ? 3 : 600_000
                : Math.Clamp(iterations, iterationsBox.Minimum, iterationsBox.Maximum);
            memoryBox.Visibility = parallelismBox.Visibility = argon2
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        SetAlgorithm(useDefaults: false);
        algorithmBox.SelectionChanged += (_, _) => SetAlgorithm(useDefaults: true);

        var errorText = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(errorText, "ChangeKdfErrorText");
        errorText.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA7D52B1CE26BF185)
                + global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7CD49F23FD3089B1),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(algorithmBox);
        panel.Children.Add(iterationsBox);
        panel.Children.Add(memoryBox);
        panel.Children.Add(parallelismBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB207881F4013AAA8),
            Content = CreateDialogBody(panel),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text63C73C4730F4473E),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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

                var argon2 = algorithmBox.SelectedIndex == 1;
                if (!TryWholeNumber(iterationsBox.Value, out var requestedIterations)
                    || (argon2 && (!TryWholeNumber(memoryBox.Value, out _)
                        || !TryWholeNumber(parallelismBox.Value, out _))))
                {
                    errorText.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text406955749D3D8A0B);
                    errorText.Visibility = Visibility.Visible;
                    continue;
                }
                var parameters = argon2
                    ? AccountKdfParameters.Argon2id(requestedIterations, (int)memoryBox.Value, (int)parallelismBox.Value)
                    : AccountKdfParameters.Pbkdf2(requestedIterations);
                var operation = await ViewModel.ChangeKdfAsync(currentBox.Password, parameters);

                // 成功由 App 层协调器统一登出并回登录页(SettingsViewModel 发 CredentialChanged intent)。
                if (operation.IsSuccess)
                    return;
                if (operation.Status != UiOperationStatus.Failed)
                    return;

                errorText.Text = operation.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB10D00FCBB7711DC);
                errorText.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            currentBox.Password = string.Empty;
        }
    }

    private static bool TryWholeNumber(double value, out int result)
    {
        result = 0;
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue || value != Math.Truncate(value))
            return false;
        result = (int)value;
        return true;
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
                ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB3E0A409B2C0C6D2));
            }
        }
        catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex)) { }
        catch (Exception)
        {
            if (IsLoaded)
                ShowFeedback(ViewModel.SupportsLocalUnlock ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEB8AACAA54658D52) : ViewModel.PinUnlockDescription, InfoBarSeverity.Error);
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
                ShowFeedback(ViewModel.SupportsLocalUnlock ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEB8AACAA54658D52) : ViewModel.PinUnlockDescription, InfoBarSeverity.Error);
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
            ShowFeedback(replacing ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE229F1E76C74332C) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB674F605BE91AF32));
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
        var pinBox = new PasswordBox { Header = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text5D03B818B7CEB89A,Core.Services.PinPolicy.MinLength), PlaceholderText = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text5D03B818B7CEB89A,Core.Services.PinPolicy.MinLength), MinWidth = 0 };
        AutomationProperties.SetAutomationId(pinBox, "SetPinBox");

        var confirmBox = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text16602FE411DCCF16), PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text16602FE411DCCF16) };
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
            Title = ViewModel.IsPinSet ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6B5E3096F5DCE722) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF54042A50188D732),
            Content = CreateDialogBody(panel),
            PrimaryButtonText = ViewModel.IsPinSet ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6B01FCE4DBEAEEB9) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDF3D58C7D84B85F2),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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
                    errorText.Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text3F1A9CAFBA0E24C4,Core.Services.PinPolicy.MinLength);
                    errorText.Visibility = Visibility.Visible;
                    continue;
                }

                if (pin != confirm)
                {
                    errorText.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE5CEAC30D151AE5D);
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
                    Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8DC55E1E92CF84B3),
                    Content = new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA91B930098185F7A), TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3FD47EDCE45B3603),
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
        var verifiedPassword = await PromptVerifiedMasterPasswordAsync(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6DDC6D799DA36729));
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
        ShowSubpage(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text96672359B79F7777));
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
        var pwBox = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F), PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F), MinWidth = 0 };
        AutomationProperties.SetAutomationId(pwBox, "TotpSetupPasswordBox");

        var step1Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step1Error, "TotpSetupStep1ErrorText");
        step1Error.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var step1Panel = new StackPanel { Spacing = 8 };
        step1Panel.Children.Add(new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE7D78E0D04CF7C0A), TextWrapping = TextWrapping.Wrap });
        step1Panel.Children.Add(pwBox);
        step1Panel.Children.Add(step1Error);

        var step1Dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9BFBC275409B8007),
            Content = CreateDialogBody(step1Panel),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextACFC4E74A650E7DF),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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

                step1Error.Text = operation.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB10D00FCBB7711DC);
                step1Error.Visibility = Visibility.Visible;
            }

        // Step 2: render the actual setup URI locally and keep manual entry available.
        credentialsPanel = await TotpSetupCredentialsPanel.CreateAsync(secret, otpauth, expectedSession);
        if (credentialsPanel is null)
            return;
        var codeBox = new TextBox { PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF0B7E00F07DBC816), MaxLength = 6, Width = 160, HorizontalAlignment = HorizontalAlignment.Left, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Consolas") };
        AutomationProperties.SetAutomationId(codeBox, "TotpSetupCodeBox");

        var step2Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step2Error, "TotpSetupStep2ErrorText");
        step2Error.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var step2Panel = new StackPanel { Spacing = 8, MinWidth = 0 };
        step2Panel.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text75A252CAA144089F),
            TextWrapping = TextWrapping.Wrap,
        });
        step2Panel.Children.Add(credentialsPanel);
        step2Panel.Children.Add(new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBB015C60BAFC8A96), Margin = new Thickness(0, 8, 0, 0) });
        step2Panel.Children.Add(codeBox);
        step2Panel.Children.Add(step2Error);

        var step2Dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text81ED1460E84A6854),
            Content = CreateDialogBody(step2Panel, 472),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF4F0EAD1116B5B62),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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

            step2Error.Text = operation.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB10D00FCBB7711DC);
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
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text31B74FC31E902202),
            Message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3607DCFA07CC7355),
        };
        AutomationProperties.SetAutomationId(recoveryWarning, "TotpSetupRecoveryWarning");
        AutomationProperties.SetName(recoveryWarning, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB3D47ECBED43B5FF));

        var recoveryPasswordBox = new PasswordBox
        {
            PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F),
            Visibility = activation.HasRecoveryCode ? Visibility.Collapsed : Visibility.Visible,
        };
        AutomationProperties.SetAutomationId(recoveryPasswordBox, "TotpRecoveryPasswordBox");
        AutomationProperties.SetName(recoveryPasswordBox, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE1E54712DCCDDE2E));

        var step3Panel = new StackPanel { Spacing = 8, MinWidth = 0 };
        step3Panel.Children.Add(new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text78F1FB8757CBE30B), TextWrapping = TextWrapping.Wrap });
        step3Panel.Children.Add(recoveryWarning);
        step3Panel.Children.Add(rcText);
        step3Panel.Children.Add(recoveryPasswordBox);

        var step3Dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text18FAF51C2F7DF4D8),
            Content = CreateDialogBody(step3Panel),
            PrimaryButtonText = activation.HasRecoveryCode ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text63D90D977348AB1F) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB7E7EBA227E7E00F),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC0B3FBFF51CCC40B),
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
                step3Dialog.PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text63D90D977348AB1F);
                continue;
            }
            if (recoveryOperation.Status != UiOperationStatus.Failed)
                break;

            recoveryWarning.Message = recoveryOperation.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text61914DB5C5685D94);
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
        var emailPwBox = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F), PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F), MinWidth = 0 };
        AutomationProperties.SetAutomationId(emailPwBox, "EmailSetupPasswordBox");

        var emailAddressBox = new TextBox { PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5AAB5A80C90D7E32) };
        AutomationProperties.SetAutomationId(emailAddressBox, "EmailSetupAddressBox");

        var step1Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step1Error, "EmailSetupStep1ErrorText");
        step1Error.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var step1Panel = new StackPanel { Spacing = 8 };
        step1Panel.Children.Add(new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text90C88ED08F793840), TextWrapping = TextWrapping.Wrap });
        step1Panel.Children.Add(emailPwBox);
        step1Panel.Children.Add(emailAddressBox);
        step1Panel.Children.Add(step1Error);

        var step1Dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAD1878540413E5BB),
            Content = CreateDialogBody(step1Panel),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4656FED515AE8F99),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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

                step1Error.Text = operation.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB10D00FCBB7711DC);
                step1Error.Visibility = Visibility.Visible;
            }

        // 步骤 2：输入收到的验证码
        var tokenBox = new TextBox { PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFC4A1B7CE502BD8A) };
        AutomationProperties.SetAutomationId(tokenBox, "EmailSetupTokenBox");

        var step2Error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(step2Error, "EmailSetupStep2ErrorText");
        step2Error.SetValue(TextBlock.ForegroundProperty,
            global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));

        var step2Panel = new StackPanel { Spacing = 8, MinWidth = 0 };
        step2Panel.Children.Add(new TextBlock { Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text48C4F16CD6F2D10D,emailAddressBox.Text.Trim()), TextWrapping = TextWrapping.Wrap });
        step2Panel.Children.Add(tokenBox);
        step2Panel.Children.Add(step2Error);

        var step2Dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text970D2208673797A1),
            Content = CreateDialogBody(step2Panel),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF4F0EAD1116B5B62),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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

            step2Error.Text = operation.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB10D00FCBB7711DC);
            step2Error.Visibility = Visibility.Visible;
        }

        var doneDialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9764559FBFB73C16),
            Content = new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text90F0AB83E56058E7), TextWrapping = TextWrapping.Wrap },
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC0B3FBFF51CCC40B),
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
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text52373E4342173705),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        verificationMethod.Items.Add(new ComboBoxItem
        {
            Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF36F5F11EDEFB912),
            Tag = WebAuthnVerificationMethod.MasterPassword,
        });
        verificationMethod.Items.Add(new ComboBoxItem
        {
            Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text839832B1CFD24E7A),
            Tag = WebAuthnVerificationMethod.EmailOtp,
        });
        verificationMethod.Items.Add(new ComboBoxItem
        {
            Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB24BFDDE8B7FE380),
            Tag = WebAuthnVerificationMethod.AuthRequestAccessCode,
        });
        verificationMethod.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(
            verificationMethod,
            "WebAuthnVerificationMethodCombo");

        var verificationInput = new PasswordBox
        {
            PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F),
            MinWidth = 0,
            MaxLength = 4096,
        };
        AutomationProperties.SetAutomationId(
            verificationInput,
            "WebAuthnVerificationInputBox");

        var verificationHelp = new TextBlock
        {
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text62E3F1245886399E),
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(
            verificationHelp,
            "WebAuthnVerificationHelpText");

        var requestOtpButton = new Button
        {
            Content = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4656FED515AE8F99),
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
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text327D9E341DF65BE4),
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
                    verificationInput.PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F);
                    verificationHelp.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text62E3F1245886399E);
                    requestOtpButton.Visibility = Visibility.Collapsed;
                    break;
                case WebAuthnVerificationMethod.EmailOtp:
                    verificationInput.PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2D60DEDA8E1EFFDF);
                    verificationHelp.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD3FA146F5FB1C766);
                    requestOtpButton.Visibility = Visibility.Visible;
                    break;
                case WebAuthnVerificationMethod.AuthRequestAccessCode:
                    verificationInput.PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB24BFDDE8B7FE380);
                    verificationHelp.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2C7C57A397966D59);
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
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4A0BFDD00D61E16D)
                    : send.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8A47DCDB3211CE86);
                verificationStatus.IsOpen = true;
            }
            finally
            {
                requestOtpButton.IsEnabled = true;
            }
        };

        var passwordDialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text49D88EE42C65C26A),
            Content = CreateDialogBody(passwordPanel),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C9691192F1B7340),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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
                    passwordError.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8241919877DE4DC3);
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
                    passwordError.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4AED4A171F7ADC43);
                    passwordError.Visibility = Visibility.Visible;
                    continue;
                }
                if (load.Status != UiOperationStatus.Failed)
                    return;

                passwordError.Text = load.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2EED99EDE676CABA);
                passwordError.Visibility = Visibility.Visible;
            }

            if (listOnly || removeId.HasValue && snapshot.Credentials.All(credential => credential.Id != removeId.Value))
                return;

            string? statusMessage = ViewModel.CanRegisterWebAuthn
                ? null
                : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9AA35C22B60B2F55);
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
                        Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text63F01A336A4C69B3),
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
                        ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text8A05D6B461E47BE2,credential.Id)
                        : credential.Name;
                    var nameText = new TextBlock
                    {
                        Text = credential.Migrated
                            ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextE7E0D09D1F7C1D5E,displayName)
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
                    AutomationProperties.SetName(deleteButton, global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text94C3A41B2A048C54,displayName));
                    AutomationProperties.SetAutomationId(
                        deleteButton,
                        $"WebAuthnDeleteCredentialButton_{credential.Id}");
                    ToolTipService.SetToolTip(deleteButton, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE88420CEFDD0A8B3));
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
                    Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA265E4F4FFBC8888),
                    PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2A41AAEDA83BDFD3),
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
                            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA9DD0F040BD04AD9),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        credentialScroller,
                        nameBox,
                        statusBar,
                    },
                };

                managementDialog = new ContentDialog
                {
                    Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9C2C2AC57C49519F),
                    Content = CreateDialogBody(content),
                    PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9C2C2AC57C49519F),
                    IsPrimaryButtonEnabled = ViewModel.CanRegisterWebAuthn,
                    CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC0B3FBFF51CCC40B),
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
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE88420CEFDD0A8B3),
                        isLastCredential
                            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCF7E1EB5979E2028)
                            : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text05FAA160FC7BD24D,deleteTarget.Name),
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2F9DAA828907B93F),
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
                            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text61BF8143A2A68340)
                            : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text16880E6BDFCFDE6E);
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
                        statusMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4AED4A171F7ADC43);
                        statusSeverity = InfoBarSeverity.Warning;
                        continue;
                    }
                    if (deletion.Status != UiOperationStatus.Failed)
                        return;

                    statusMessage = deletion.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1664878C3AB92130);
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
                    statusMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF7C2DEA603C4539B);
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
                    statusMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6D3765C8FA133DDD);
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
                    statusMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4AED4A171F7ADC43);
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
                    statusMessage = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCDB0D78AC6B07AEF);
                    statusSeverity = InfoBarSeverity.Success;
                }
                else
                {
                    statusMessage = registration.Error
                        ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF69FA772D5F71318);
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
                Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text802C4E946E23D1A7),
                Content = new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text990A9C2C94711955), TextWrapping = TextWrapping.Wrap },
                CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3FD47EDCE45B3603),
                XamlRoot = XamlRoot,
            };
            await DialogHelper.ShowTrackedAsync(noneDialog);
            return;
        }

        var disablePwBox = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F), PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F), MinWidth = 0 };
        AutomationProperties.SetAutomationId(disablePwBox, "DisableTwoFactorPasswordBox");

        var providerCombo = new ComboBox { MinWidth = 0, PlaceholderText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1BE01DFFC337C72C) };
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
        disablePanel.Children.Add(new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text050F1BD5F6A6153E), TextWrapping = TextWrapping.Wrap });
        disablePanel.Children.Add(providerCombo);
        disablePanel.Children.Add(disablePwBox);
        disablePanel.Children.Add(disableError);

        var disableDialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text802C4E946E23D1A7),
            Content = CreateDialogBody(disablePanel),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7DF5C456C765E4C3),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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
                    disableError.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text55C4FB7D23F65FFA);
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

                disableError.Text = operation.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB10D00FCBB7711DC);
                disableError.Visibility = Visibility.Visible;
            }

            var doneDialog = new ContentDialog
            {
                Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1B18E407CCD47923),
                Content = new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA8E16438CE43DE6E), TextWrapping = TextWrapping.Wrap },
                CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC0B3FBFF51CCC40B),
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
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA922332B277E8679),
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBEA22C0288F571DB),
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
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text44BC19D112AF21E9),
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4AED4A171F7ADC43),
                    operationCts.Token);
            }
            else if (preflightOperation.Status == UiOperationStatus.Failed)
            {
                await ShowAccountDeletionMessageAsync(
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCB844DE4F581EB10),
                    preflightOperation.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5282B26D62B5E7E2),
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
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD932E530895258E8),
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text688B69B3704E373C)),
                AccountDeletionPreflightBlockReason.FreshOnlineAuthenticationRequired => (
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0952EA66B20F51C9),
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2C253941C8670356)),
                _ => (
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text54C8D3F5D34B0A55),
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text67DF77E0B12965CD)),
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
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAE987FD5AE53CC17),
                        otpOperation.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEFC81CED18EB3BB0),
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
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAE987FD5AE53CC17),
                        SettingsViewModel.GetAccountDeletionRejectionMessage(rejected.Reason),
                        operationCts.Token);
                    return;
                case AccountDeletionOtpRequestResult.CancelledBeforeDispatch:
                    await ShowAccountDeletionMessageAsync(
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA37778F17C5F3EE5),
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3554ECE1F4048FFC),
                        operationCts.Token);
                    return;
                case AccountDeletionOtpRequestResult.OutcomeUncertain:
                    await ShowAccountDeletionMessageAsync(
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text593FAD366346BF9C),
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text305F975546501D6D),
                        operationCts.Token);
                    return;
            }
        }

        if (preflight.CredentialRequirement
            == AccountDeletionCredentialRequirement.FreshOnlineAuthentication)
        {
            await ShowAccountDeletionMessageAsync(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0952EA66B20F51C9),
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAD6536C8FA414BED),
                operationCts.Token);
            return;
        }

        var credentialBox = new PasswordBox
        {
            MinWidth = 0,
            PlaceholderText = preflight.CredentialRequirement
                == AccountDeletionCredentialRequirement.MasterPassword
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7B0789FB7EA3F)
                    : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2D60DEDA8E1EFFDF),
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
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0E6575FDC2AAF305)
                : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text845D3AFCD0916E9E,preflight.Binding.NormalizedEmail),
            TextWrapping = TextWrapping.Wrap,
        });
        credentialPanel.Children.Add(credentialBox);
        credentialPanel.Children.Add(credentialError);

        var credentialDialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextED253257E0DDBE0B),
            Content = CreateDialogBody(credentialPanel),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0679C9FD9CF9AB5E),
            IsPrimaryButtonEnabled = false,
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text02DADCEAA932431F),
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2BE596F1AD147944),
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
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text59CD2DE5879024E6),
                        deletion.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC2A41081247B5B15),
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
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2EA1B3409EF26414),
            Message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD7C781C4212E573A),
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
                Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5A656B455E506F8C),
                Message = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text6D332DC44E3D68CB,Environment.NewLine,organizations),
            });
        }

        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF581C3C2F870F22A),
            Content = CreateDialogBody(panel),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0363130B80FDA375),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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
            Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBA09DCD694A0F444),
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
            Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextDA39D2FF876ECAB1,normalizedEmail),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(emailBox);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBB6047043E6F3977),
            Content = CreateDialogBody(panel),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C9691192F1B7340),
            IsPrimaryButtonEnabled = false,
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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

                error.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text14CB989329A74BD9);
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
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text864450DB1B149CE6),
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
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA37778F17C5F3EE5),
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDB9E0BBFD9FB2024),
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
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFAC2A67AD87807C4),
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
        var password = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF36F5F11EDEFB912), MinWidth = 280 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        error.SetValue(TextBlock.ForegroundProperty, global::App.Controls.ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush"));
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(password);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9BFBC275409B8007),
            Content = CreateDialogBody(content),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C9691192F1B7340),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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
                    error.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7EF4C5EF71FC45BE);
                    error.Visibility = Visibility.Visible;
                    continue;
                }
                if (await global::App.App.Services.GetRequiredService<IAccountService>()
                    .VerifyMasterPasswordAsync(password.Password, cancellation.Token))
                    return session.IsUnlocked(expectedSession) ? password.Password : null;
                error.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0A703B71C61859CC);
                error.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text708633C6BB398C92), InfoBarSeverity.Error);
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
        var password = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF36F5F11EDEFB912), MinWidth = 280 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text41390FA572F04EE8), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(password);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC5674F920EF6D556),
            Content = CreateDialogBody(content),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C9691192F1B7340),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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
                error.Text = result.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA1A6158F3EB552A3);
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
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCD3648007C40F7AE),
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
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC5674F920EF6D556),
            Content = CreateDialogBody(body),
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text63D90D977348AB1F),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3FD47EDCE45B3603),
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
                    ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6F47D1A740362936));
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
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB6AF1A5049FCA19D),
            Content = new TextBlock { Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB7042B66F2BED0D6), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3AB8CC15939F3B5C),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
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
                ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text38A447846D24CFCF), InfoBarSeverity.Error);
        }
        catch (Exception)
        {
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text38A447846D24CFCF), InfoBarSeverity.Error);
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
                ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text41AA81BA735B4D78));
            else
                ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD346535A6A66502D), InfoBarSeverity.Error);
        }
        catch (Exception)
        {
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD346535A6A66502D), InfoBarSeverity.Error);
        }
    }
}
