using App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace App.Views;

public sealed partial class SettingsPage
{
    private readonly StartupRegistrationService _startupRegistration = global::App.App.Services
        .GetRequiredService<StartupRegistrationService>();
    private CancellationTokenSource? _platformSettingsCancellation;
    private bool _platformSettingsReady;
    private bool _suppressStartupToggle;
    private bool _suppressAutomaticUpdateToggle;
    private bool _checkingForUpdates;

    private async Task InitializePlatformSettingsAsync()
    {
        CancelPlatformSettings();
        var cancellation = new CancellationTokenSource();
        _platformSettingsCancellation = cancellation;
        Unloaded -= OnPlatformSettingsUnloaded;
        Unloaded += OnPlatformSettingsUnloaded;
        _platformSettingsReady = false;
        try
        {
            if (FindName("AutomaticUpdateCheckToggle") is ToggleSwitch automatic)
            {
                _suppressAutomaticUpdateToggle = true;
                automatic.IsOn = AppPreferences.Current.AutomaticallyCheckForUpdates;
                _suppressAutomaticUpdateToggle = false;
            }

            var result = await _startupRegistration.ReadAsync(cancellation.Token);
            if (!ReferenceEquals(_platformSettingsCancellation, cancellation))
                return;
            ApplyStartupState(result);
            RefreshMsixUpdateStatus();
            _platformSettingsReady = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private void OnPlatformSettingsUnloaded(object sender, RoutedEventArgs args) =>
        CancelPlatformSettings();

    private void CancelPlatformSettings()
    {
        _platformSettingsReady = false;
        var cancellation = Interlocked.Exchange(ref _platformSettingsCancellation, null);
        if (cancellation is null)
            return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        cancellation.Dispose();
    }

    private async void OnStartupToggleToggled(object sender, RoutedEventArgs args)
    {
        if (!_platformSettingsReady || _suppressStartupToggle || sender is not ToggleSwitch toggle)
            return;
        var cancellation = _platformSettingsCancellation;
        if (cancellation is null)
            return;
        toggle.IsEnabled = false;
        try
        {
            var result = await _startupRegistration.SetEnabledAsync(toggle.IsOn, cancellation.Token);
            if (!ReferenceEquals(_platformSettingsCancellation, cancellation))
                return;
            ApplyStartupState(result);
            ShowFeedback(result.Message, result.State is StartupRegistrationState.Failed
                or StartupRegistrationState.ConflictingRegistration ? InfoBarSeverity.Error : InfoBarSeverity.Informational);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private void ApplyStartupState(StartupRegistrationResult result)
    {
        if (FindName("StartupToggle") is ToggleSwitch toggle)
        {
            _suppressStartupToggle = true;
            toggle.IsOn = result.IsRequestedEnabled;
            toggle.IsEnabled = result.CanChange;
            _suppressStartupToggle = false;
        }
        if (FindName("StartupStatusText") is TextBlock status)
            status.Text = result.Message;
        if (result.State is StartupRegistrationState.Disabled or StartupRegistrationState.Enabled
            or StartupRegistrationState.EnabledByPolicy or StartupRegistrationState.Registered
            or StartupRegistrationState.DisabledByUser or StartupRegistrationState.DisabledByPolicy)
        {
            AppPreferences.Current.StartOnLogin = result.IsRequestedEnabled;
            AppPreferences.Save();
        }
    }

    private void OnAutomaticUpdateCheckToggled(object sender, RoutedEventArgs args)
    {
        if (!_platformSettingsReady || _suppressAutomaticUpdateToggle || sender is not ToggleSwitch toggle)
            return;
        AppPreferences.Current.AutomaticallyCheckForUpdates = toggle.IsOn;
        AppPreferences.Save();
        ShowFeedback(toggle.IsOn
            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC46990CB8CA70B0B)
            : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCBC8B65091B81782), InfoBarSeverity.Informational);
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs args)
    {
        if (_checkingForUpdates)
            return;
        var cancellation = _platformSettingsCancellation;
        if (cancellation is null)
            return;
        _checkingForUpdates = true;
        var button = sender as Button;
        if (button is not null)
            button.IsEnabled = false;
        try
        {
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFB1F9BB5255DF0DF), InfoBarSeverity.Informational);
            AppPreferences.Current.LastUpdateCheckUtc = DateTimeOffset.UtcNow;
            AppPreferences.Save();
            using var service = new ReleaseUpdateService();
            var result = await service.CheckAsync(
                AboutInfo.AppVersion, AboutInfo.Architecture, AboutInfo.IsPackaged, cancellation.Token);
            if (!ReferenceEquals(_platformSettingsCancellation, cancellation))
                return;
            ShowFeedback(result.Message, result.Status switch
            {
                ReleaseUpdateStatus.UpToDate => InfoBarSeverity.Success,
                ReleaseUpdateStatus.Failed => InfoBarSeverity.Error,
                _ => InfoBarSeverity.Informational,
            });
            if (result.Status != ReleaseUpdateStatus.UpdateAvailable || result.ReleasePage is null)
                return;

            var dialog = new ContentDialog
            {
                Title = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text03B00E0792BDF5A5,result.LatestVersion),
                Content = new TextBlock
                {
                    Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBF85376BB4D1C44D)
                        + global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB0A13E9214024DCC),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460,
                },
                PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCD6C9851E91AD7B6),
                CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text73B6E48A1B552D11),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await DialogHelper.ShowTrackedAsync(dialog) == ContentDialogResult.Primary
                && ReferenceEquals(_platformSettingsCancellation, cancellation))
            {
                if (!await Launcher.LaunchUriAsync(result.ReleasePage))
                    ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDEC0302DAB616D48), InfoBarSeverity.Error);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            if (ReferenceEquals(_platformSettingsCancellation, cancellation))
                ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0B915ACDC77E3A45), InfoBarSeverity.Error);
        }
        finally
        {
            _checkingForUpdates = false;
            if (button is not null)
                button.IsEnabled = true;
        }
    }

    private void RefreshMsixUpdateStatus()
    {
        var result = new AppInstallerEnrollmentService().ReadStatus();
        if (FindName("MsixUpdateStatusText") is TextBlock status)
            status.Text = result.Message;
        if (FindName("EnableMsixAutomaticUpdatesButton") is Button button)
        {
            button.IsEnabled = result.State is AppInstallerEnrollmentState.Available
                or AppInstallerEnrollmentState.Enrolled or AppInstallerEnrollmentState.OtherSource;
            button.Content = result.State == AppInstallerEnrollmentState.Enrolled
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE96CC1B6DE633A67) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF7075B738AE02665);
        }
    }

    private async void OnEnableMsixAutomaticUpdatesClick(object sender, RoutedEventArgs args)
    {
        var cancellation = _platformSettingsCancellation;
        if (cancellation is null)
            return;
        var service = new AppInstallerEnrollmentService();
        var state = service.ReadStatus();
        if (state.State is AppInstallerEnrollmentState.Portable or AppInstallerEnrollmentState.Unavailable)
        {
            ShowFeedback(state.Message, InfoBarSeverity.Informational);
            return;
        }

        var session = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        var lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
        var lease = session.CaptureLease();
        var consent = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF26C320789C800C1),
            Content = new TextBlock
            {
                Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3134F1081EDC9612)
                    + global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2DFDC27B52CE1100)
                    + global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD7D8BBB2F9DFA086),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            },
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5805BFDCC9AE7496),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text19766ED6CCB2F4A3),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await DialogHelper.ShowTrackedAsync(consent, lease, ct: cancellation.Token) != ContentDialogResult.Primary
            || !ReferenceEquals(_platformSettingsCancellation, cancellation))
        {
            return;
        }

        var button = sender as Button;
        if (button is not null)
            button.IsEnabled = false;
        var lockedForInstaller = false;
        try
        {
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text499A50282C0D6B5A), InfoBarSeverity.Informational);
            using var prepared = await service.PrepareAsync(cancellation.Token);
            if (!ReferenceEquals(_platformSettingsCancellation, cancellation) || !session.IsUnlocked(lease))
                return;

            // This intentionally unloads Settings and cancels its request scope. The prepared
            // file contains only validated package metadata; no secrets cross this boundary.
            lockedForInstaller = true;
            await lifecycle.RequestAsync(AppLifecycleIntent.Lock);
            if (lifecycle.IsTransitioning)
                return;
            var launched = await service.LaunchPreparedAsync(prepared);
            global::App.App.MainWindow?.ShowNotice(launched
                ? "Windows App Installer opened. Complete its installation prompt to enroll in automatic updates."
                : "Windows could not open App Installer. Download and open the official .appinstaller file from the release page.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !lockedForInstaller)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
            or UnauthorizedAccessException or InvalidOperationException
            or System.Runtime.InteropServices.COMException or FormatException or OperationCanceledException)
        {
            const string message = "The Windows update installer could not be prepared or opened. No successful enrollment is claimed; try again from Settings.";
            if (lockedForInstaller)
                global::App.App.MainWindow?.ShowNotice(message);
            else if (ReferenceEquals(_platformSettingsCancellation, cancellation))
                ShowFeedback(message, InfoBarSeverity.Error);
        }
        finally
        {
            if (!lockedForInstaller && ReferenceEquals(_platformSettingsCancellation, cancellation))
                RefreshMsixUpdateStatus();
        }
    }
}
