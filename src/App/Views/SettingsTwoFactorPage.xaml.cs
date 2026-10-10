using App.Services;
using App.Controls;
using App.ViewModels;
using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace App.Views;

public sealed partial class SettingsTwoFactorPage : Page
{
    private SettingsViewModel? _viewModel;
    private Func<Task>? _enableTotp;
    private Func<Task>? _enableEmail;
    private Func<Task>? _manageWebAuthn;
    private Func<IReadOnlyList<TwoFactorProvider>, Task>? _disableProvider;
    private Func<Task>? _showRecovery;
    private Func<int, Task>? _removeWebAuthn;
    private Func<Task>? _reloadWebAuthn;
    private WebAuthnManagementSnapshot? _webAuthnSnapshot;
    private CancellationTokenSource? _loadCancellation;
    private IDisposable? _transientRegistration;
    private IReadOnlyList<TwoFactorProvider> _providers = Array.Empty<TwoFactorProvider>();
    private bool _isActive;
    private bool _isOperating;

    public SettingsTwoFactorPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_isActive && MethodsContent.Visibility == Visibility.Visible)
            BuildRows();
    }

    public void Initialize(
        SettingsViewModel viewModel,
        Func<Task> enableTotp,
        Func<Task> enableEmail,
        Func<Task> manageWebAuthn,
        Func<IReadOnlyList<TwoFactorProvider>, Task> disableProvider,
        Func<Task> showRecovery,
        Func<int, Task> removeWebAuthn,
        WebAuthnManagementSnapshot? webAuthnSnapshot,
        Func<Task> reloadWebAuthn)
    {
        _viewModel = viewModel;
        _enableTotp = enableTotp;
        _enableEmail = enableEmail;
        _manageWebAuthn = manageWebAuthn;
        _disableProvider = disableProvider;
        _showRecovery = showRecovery;
        _removeWebAuthn = removeWebAuthn;
        _webAuthnSnapshot = webAuthnSnapshot;
        _reloadWebAuthn = reloadWebAuthn;
    }

    public void ApplyWebAuthnSnapshot(WebAuthnManagementSnapshot snapshot)
    {
        if (!_isActive)
            return;
        _webAuthnSnapshot = snapshot;
        BuildRows();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isActive = true;
        _loadCancellation = new CancellationTokenSource();
        _transientRegistration = global::App.App.Services.GetRequiredService<ITransientOperationRegistry>()
            .Register(CancelLoading, () => MethodsContent.IsEnabled = false, ClearState);
        await LoadProvidersAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isActive = false;
        CancelLoading();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _transientRegistration?.Dispose();
        _transientRegistration = null;
        ClearState();
    }

    private void CancelLoading()
    {
        _isActive = false;
        try { _loadCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ClearState()
    {
        _providers = Array.Empty<TwoFactorProvider>();
        ProviderRows.Children.Clear();
        RecoveryRows.Children.Clear();
        WebAuthnRows.Children.Clear();
        _webAuthnSnapshot = null;
    }

    private async Task LoadProvidersAsync()
    {
        if (_viewModel is null || !_isActive || _loadCancellation is null)
            return;
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        MethodsContent.Visibility = Visibility.Collapsed;
        LoadErrorBar.IsOpen = false;
        var result = await _viewModel.LoadProvidersAsync(_loadCancellation.Token);
        if (!_isActive)
            return;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        if (!result.IsSuccess || result.Value is null)
        {
            if (result.Status is UiOperationStatus.Failed or UiOperationStatus.Busy)
            {
                LoadErrorBar.Message = result.Status == UiOperationStatus.Busy ? "另一项安全操作仍在结束，请稍后重试。" : result.Error ?? "无法加载两步登录设置。";
                LoadErrorBar.IsOpen = true;
                StatusText.Text = "无法加载";
            }
            return;
        }
        _providers = result.Value;
        BuildRows();
        MethodsContent.Visibility = Visibility.Visible;
    }

    private void BuildRows()
    {
        ProviderRows.Children.Clear();
        RecoveryRows.Children.Clear();
        WebAuthnRows.Children.Clear();
        var count = _providers.Count(provider => provider.Enabled
            && provider.Type != (int)TwoFactorProviderType.Remember);
        StatusText.Text = count == 0 ? "未开启" : $"已开启 · {count} 种方式";
        StatusIcon.Glyph = count == 0 ? "\uE711" : "\uE73E";
        StatusIcon.Foreground = ThemeBrushResolver.Resolve(this, count == 0
            ? "TextFillColorSecondaryBrush" : "SystemFillColorSuccessBrush");
        AddProvider(TwoFactorProviderType.Authenticator, "\uE916", "身份验证器应用",
            "用 Microsoft Authenticator 等应用生成 6 位验证码", _enableTotp!);
        AddProvider(TwoFactorProviderType.Email, "\uE715", "电子邮件",
            "通过已配置的验证邮箱接收验证码", _enableEmail!);
        ProviderRows.Children.Add(CreateRow("\uE192", "通行密钥或安全密钥",
            _webAuthnSnapshot?.Credentials.Count > 0 ? $"· 已添加 {_webAuthnSnapshot.Credentials.Count} 个" : string.Empty,
            "YubiKey、Windows Hello 等 FIDO2 设备", "添加…", _manageWebAuthn!));
        ProviderRows.Children.Add(CreateRow("\uEA18", "Duo",
            IsProviderEnabled(TwoFactorProviderType.Duo) ? "· 已开启" : string.Empty,
            "需要 Duo 管理后台里的应用信息，在网页版中设置", "打开网页版", OpenWebVaultAsync));
        foreach (var provider in _providers.Where(provider => provider.Enabled
            && provider.Type != (int)TwoFactorProviderType.Authenticator
            && provider.Type != (int)TwoFactorProviderType.Email
            && provider.Type != (int)TwoFactorProviderType.WebAuthn
            && provider.Type != (int)TwoFactorProviderType.Duo
            && provider.Type != (int)TwoFactorProviderType.Remember))
        {
            ProviderRows.Children.Add(CreateRow("\uEA18",
                SettingsViewModel.GetTwoFactorProviderDisplayName(provider.Type), "· 已开启",
                "服务器已配置的其他验证方式", "关闭",
                () => _disableProvider!(new[] { provider })));
        }
        RecoveryRows.Children.Add(CreateRow("\uE8D7", "恢复代码", string.Empty,
            "丢失所有验证方式时用它登录，登录后两步登录会全部关闭", "查看…", _showRecovery!));
        WebAuthnGroup.Visibility = _webAuthnSnapshot?.Credentials.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_webAuthnSnapshot is null)
            WebAuthnRows.Children.Add(CreateRow("\uE8D7", "安全密钥列表暂时无法获取", string.Empty,
                "重新验证后查看服务器上已添加的密钥", "查看…", _reloadWebAuthn!));
        else
        {
            foreach (var credential in _webAuthnSnapshot.Credentials)
            {
                var name = string.IsNullOrWhiteSpace(credential.Name) ? $"安全密钥 {credential.Id}" : credential.Name;
                WebAuthnRows.Children.Add(CreateRow("\uE8D7", name, string.Empty,
                    credential.Migrated ? "从 U2F 迁移的安全密钥" : "FIDO2 安全密钥", "移除",
                    () => _removeWebAuthn!(credential.Id)));
            }
        }
    }

    private bool IsProviderEnabled(TwoFactorProviderType type) =>
        _providers.Any(provider => provider.Type == (int)type && provider.Enabled);

    private void AddProvider(TwoFactorProviderType type, string glyph, string title, string description, Func<Task> configure)
    {
        var enabled = IsProviderEnabled(type);
        ProviderRows.Children.Add(CreateRow(glyph, title, enabled ? "· 已开启" : string.Empty,
            description, enabled ? "关闭" : "设置…", enabled
                ? () => _disableProvider!(_providers.Where(provider => provider.Type == (int)type).ToArray())
                : configure));
    }

    private Border CreateRow(string glyph, string title, string badge, string description, string actionText, Func<Task> action)
    {
        var grid = new Grid { MinHeight = 60, ColumnSpacing = 14, Padding = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(6),
            Background = ThemeBrushResolver.Resolve(this, "CardBackgroundFillColorDefaultBrush"),
            Child = new FontIcon { Glyph = glyph, FontSize = 16, Foreground = ThemeBrushResolver.Resolve(this, "TextFillColorSecondaryBrush") },
        };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var heading = new TextBlock { Text = string.IsNullOrEmpty(badge) ? title : $"{title} {badge}", TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrushResolver.Resolve(this, "TextFillColorPrimaryBrush") };
        text.Children.Add(heading);
        text.Children.Add(new TextBlock
        {
            Text = description, FontSize = 12, LineHeight = 16,
            Foreground = ThemeBrushResolver.Resolve(this, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var button = new Button
        {
            Content = actionText, Height = 32, Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, $"{title}，{actionText}");
        button.Click += async (_, _) => await RunActionAsync(action);
        Grid.SetColumn(button, 2);
        grid.Children.Add(button);
        return new Border
        {
            BorderBrush = ThemeBrushResolver.Resolve(this, "DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1), Child = grid,
        };
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_isOperating || !_isActive)
            return;
        _isOperating = true;
        MethodsContent.IsEnabled = false;
        try
        {
            await action();
            if (_isActive)
                await LoadProvidersAsync();
        }
        catch (OperationCanceledException) when (!_isActive) { }
        catch (Exception)
        {
            if (_isActive)
            {
                LoadErrorBar.Message = "操作未完成，请重试。";
                LoadErrorBar.IsOpen = true;
            }
        }
        finally
        {
            _isOperating = false;
            MethodsContent.IsEnabled = _isActive;
        }
    }

    private async Task OpenWebVaultAsync()
    {
        if (_viewModel is null || !Uri.TryCreate(_viewModel.AccountServer, UriKind.Absolute, out var server))
            return;
        var address = new Uri(server.ToString().TrimEnd('/') + "/#/settings/security/two-step-login");
        if (!await Launcher.LaunchUriAsync(address))
            throw new InvalidOperationException("The web vault could not be opened.");
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await LoadProvidersAsync();
}
