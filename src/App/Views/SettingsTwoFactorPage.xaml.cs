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
        StatusText.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text21BD738E0D7191A7);
        StatusIcon.Glyph = "\uE8FD";
        StatusIcon.Foreground = ThemeBrushResolver.Resolve(this, "TextFillColorSecondaryBrush");
        var result = await _viewModel.LoadProvidersAsync(_loadCancellation.Token);
        if (!_isActive)
            return;
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        if (!result.IsSuccess || result.Value is null)
        {
            if (result.Status is UiOperationStatus.Failed or UiOperationStatus.Busy)
            {
                LoadErrorBar.Message = result.Status == UiOperationStatus.Busy ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAF93175FD8B57EE7) : result.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDF8C039805EDFA38);
                LoadErrorBar.IsOpen = true;
                StatusText.Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF3F42080D855A931);
                StatusIcon.Glyph = "\uEA39";
                StatusIcon.Foreground = ThemeBrushResolver.Resolve(this, "SystemFillColorCriticalBrush");
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
        StatusText.Text = count == 0 ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3CFFA9757B693B52) : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextCA9B43DAC1149DD1,count);
        StatusIcon.Glyph = count == 0 ? "\uE711" : "\uE73E";
        StatusIcon.Foreground = ThemeBrushResolver.Resolve(this, count == 0
            ? "TextFillColorSecondaryBrush" : "SystemFillColorSuccessBrush");
        AddProvider(TwoFactorProviderType.Authenticator, "\uE916", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0BFE93D95E677A9A),
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8BE28597FC65D03B), _enableTotp!);
        AddProvider(TwoFactorProviderType.Email, "\uE715", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1B2714B812D42F77),
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA039010DA9C6C342), _enableEmail!);
        ProviderRows.Children.Add(CreateRow("\uE192", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4671FB290C9D89B0),
            _webAuthnSnapshot?.Credentials.Count > 0 ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextB8FC936C368B01B5,_webAuthnSnapshot.Credentials.Count) : string.Empty,
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3FA9688B69499706), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text16242928CC42A717), _manageWebAuthn!));
        var yubiKeyEnabled = IsProviderEnabled(TwoFactorProviderType.YubiKey);
        ProviderRows.Children.Add(CreateRow("\uE765", "YubiKey OTP",
            yubiKeyEnabled ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text38050252C51911CD) : string.Empty,
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0CA10504B5B7069D),
            yubiKeyEnabled ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3FD47EDCE45B3603) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text47691943215E3493), yubiKeyEnabled
                ? () => _disableProvider!(_providers.Where(provider => provider.Type == (int)TwoFactorProviderType.YubiKey).ToArray())
                : OpenWebVaultAsync));
        ProviderRows.Children.Add(CreateRow("\uEA18", "Duo",
            IsProviderEnabled(TwoFactorProviderType.Duo) ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text38050252C51911CD) : string.Empty,
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA04B3DC011BC2406), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text47691943215E3493), OpenWebVaultAsync));
        foreach (var provider in _providers.Where(provider => provider.Enabled
            && provider.Type != (int)TwoFactorProviderType.Authenticator
            && provider.Type != (int)TwoFactorProviderType.Email
            && provider.Type != (int)TwoFactorProviderType.WebAuthn
            && provider.Type != (int)TwoFactorProviderType.YubiKey
            && provider.Type != (int)TwoFactorProviderType.Duo
            && provider.Type != (int)TwoFactorProviderType.Remember))
        {
            ProviderRows.Children.Add(CreateRow("\uEA18",
                SettingsViewModel.GetTwoFactorProviderDisplayName(provider.Type), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text38050252C51911CD),
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9EC5065A44A1E58A), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3FD47EDCE45B3603),
                () => _disableProvider!(new[] { provider })));
        }
        RecoveryRows.Children.Add(CreateRow("\uE8D7", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC5674F920EF6D556), string.Empty,
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextED8DC25279B054AF), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCBE3C1369FA23D66), _showRecovery!));
        WebAuthnGroup.Visibility = _webAuthnSnapshot?.Credentials.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_webAuthnSnapshot is null)
            WebAuthnRows.Children.Add(CreateRow("\uE8D7", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text16F5F3573908D49C), string.Empty,
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA94A75647943BCB5), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCBE3C1369FA23D66), _reloadWebAuthn!));
        else
        {
            foreach (var credential in _webAuthnSnapshot.Credentials)
            {
                var name = string.IsNullOrWhiteSpace(credential.Name) ? global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text8A05D6B461E47BE2,credential.Id) : credential.Name;
                WebAuthnRows.Children.Add(CreateRow("\uE8D7", name, string.Empty,
                    credential.Migrated ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text955BD28E1EE67C07) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7BD3DD315113306C), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6135D4159E892541),
                    () => _removeWebAuthn!(credential.Id)));
            }
        }
    }

    private bool IsProviderEnabled(TwoFactorProviderType type) =>
        _providers.Any(provider => provider.Type == (int)type && provider.Enabled);

    private void AddProvider(TwoFactorProviderType type, string glyph, string title, string description, Func<Task> configure)
    {
        var enabled = IsProviderEnabled(type);
        ProviderRows.Children.Add(CreateRow(glyph, title, enabled ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text38050252C51911CD) : string.Empty,
            description, enabled ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3FD47EDCE45B3603) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDA9FC8BA94970B9D), enabled
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
            Background = ThemeBrushResolver.Resolve(this, "WardenTileBrush"),
            Child = new FontIcon { Glyph = glyph, FontSize = 16, Foreground = ThemeBrushResolver.Resolve(this, "TextFillColorSecondaryBrush") },
        };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var heading = new Grid
        {
            ColumnSpacing = string.IsNullOrEmpty(badge) ? 0 : 8,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = title, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemeBrushResolver.Resolve(this, "TextFillColorPrimaryBrush"),
        });
        if (!string.IsNullOrEmpty(badge))
        {
            var status = new TextBlock
            {
                Text = badge, FontSize = 12, LineHeight = 16,
                VerticalAlignment = VerticalAlignment.Bottom,
                Foreground = ThemeBrushResolver.Resolve(this, "TextFillColorSecondaryBrush"),
            };
            Grid.SetColumn(status, 1);
            heading.Children.Add(status);
        }
        text.Children.Add(heading);
        text.Children.Add(new TextBlock
        {
            Text = description, FontSize = 12, LineHeight = 16,
            Foreground = ThemeBrushResolver.Resolve(this, "TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
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
                LoadErrorBar.Message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDCD31A1D6390879C);
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
