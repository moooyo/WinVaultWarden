using App.Services;
using Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vault;
using Windows.System;

namespace App.Views;

public sealed partial class SettingsPage
{
    private bool _initializingRuntimeIntegrations;
    private bool _changingSshAgent;
    private bool _changingWindowsPasskeys;
    private bool _changingLockedDeviceRequests;

    private void OnPinRestartPolicyToggled(object sender, RoutedEventArgs args)
    {
        if (!IsLoaded || sender is not ToggleSwitch toggle
            || toggle.IsOn == ViewModel.RequireMasterPasswordOnRestart)
            return;
        var session = global::App.App.Services.GetRequiredService<VaultSession>();
        var lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
        try
        {
            var lease = session.CaptureLease();
            if (!lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(
                    lease, () => ViewModel.SetPinRestartPolicy(toggle.IsOn))))
                throw new OperationCanceledException(lease.CancellationToken);
        }
        catch (Exception exception) when (IsRecoverableIntegrationFailure(exception))
        {
            CrashLogger.WriteRedacted("PinRestartPolicy", exception);
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text227250AF478470F7), InfoBarSeverity.Error);
        }
        finally
        {
            toggle.IsOn = ViewModel.RequireMasterPasswordOnRestart;
        }
    }

    private void InitializeRuntimeIntegrationSettings()
    {
        _initializingRuntimeIntegrations = true;
        try
        {
            if (FindName("SshAgentToggle") is ToggleSwitch toggle)
                toggle.IsOn = global::App.App.Services.GetRequiredService<ISshAgentHost>().IsRunning;
            if (FindName("LockedDeviceRequestsToggle") is ToggleSwitch requestsToggle)
                requestsToggle.IsOn = AppPreferences.Current.MonitorDeviceRequestsWhileLocked;
            RefreshSshAgentStatus();
            RefreshWindowsPasskeyStatus();
        }
        finally
        {
            _initializingRuntimeIntegrations = false;
        }
    }

    private void RefreshSshAgentStatus()
    {
        if (FindName("SshAgentStatusText") is not TextBlock text)
            return;
        var server = global::App.App.Services.GetRequiredService<ISshAgentHost>();
        text.Text = server.IsRunning
            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text532510A115523715)
            : AppPreferences.Current.SshAgentEnabled
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB13D01D1A1FA58B2)
                : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB7C92A551DAF563D);
    }

    private async void OnLockedDeviceRequestsToggled(object sender, RoutedEventArgs args)
    {
        if (_initializingRuntimeIntegrations || _changingLockedDeviceRequests || !IsLoaded
            || sender is not ToggleSwitch toggle)
            return;
        _changingLockedDeviceRequests = true;
        toggle.IsEnabled = false;
        var windows = global::App.App.Services.GetRequiredService<LockedDeviceRequestWindowService>();
        try
        {
            var session = global::App.App.Services.GetRequiredService<VaultSession>();
            var lease = session.CaptureLease();
            if (toggle.IsOn && !session.IsUnlocked(lease))
                return;
            AppPreferences.Current.MonitorDeviceRequestsWhileLocked = toggle.IsOn;
            AppPreferences.Save();
            if (toggle.IsOn)
                await windows.OnUnlockedAsync(lease, explicitlyEnabled: true);
            else
                windows.Stop();
        }
        catch (Exception exception) when (IsRecoverableIntegrationFailure(exception))
        {
            windows.Stop();
            CrashLogger.WriteRedacted("LockedDeviceRequestSetting", exception);
            if (IsLoaded)
                ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3F0289E8BFC2BFF8), InfoBarSeverity.Error);
        }
        finally
        {
            _changingLockedDeviceRequests = false;
            _initializingRuntimeIntegrations = true;
            toggle.IsOn = AppPreferences.Current.MonitorDeviceRequestsWhileLocked;
            toggle.IsEnabled = true;
            _initializingRuntimeIntegrations = false;
        }
    }

    private async void OnSshAgentToggleToggled(object sender, RoutedEventArgs args)
    {
        if (_initializingRuntimeIntegrations || _changingSshAgent || sender is not ToggleSwitch toggle)
            return;
        _changingSshAgent = true;
        toggle.IsEnabled = false;
        var server = global::App.App.Services.GetRequiredService<ISshAgentHost>();
        try
        {
            var enabled = toggle.IsOn;
            if (enabled)
            {
                var result = server.Start();
                if (result is not (SshAgentStartResult.Started or SshAgentStartResult.AlreadyRunning))
                {
                    enabled = false;
                    ShowFeedback(result == SshAgentStartResult.PipeInUse
                        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1C82443383E9E3B5)
                        : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3C0897295B2175C4), InfoBarSeverity.Warning);
                }
            }
            else
            {
                await server.StopAsync();
            }
            AppPreferences.Current.SshAgentEnabled = enabled;
            AppPreferences.Save();
            _initializingRuntimeIntegrations = true;
            toggle.IsOn = enabled && server.IsRunning;
        }
        catch (Exception exception) when (IsRecoverableIntegrationFailure(exception))
        {
            CrashLogger.WriteRedacted("SshAgentSetting", exception);
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4617FAE7891279F7), InfoBarSeverity.Error);
        }
        finally
        {
            _initializingRuntimeIntegrations = false;
            _changingSshAgent = false;
            toggle.IsEnabled = true;
            RefreshSshAgentStatus();
        }
    }

    private void RefreshWindowsPasskeyStatus()
    {
        var status = global::App.App.Services.GetRequiredService<IWindowsPasskeyProvider>().GetStatus();
        if (FindName("WindowsPasskeyStatusText") is TextBlock text)
            text.Text = status.Description;
        if (FindName("WindowsPasskeyRegistrationButton") is Button button)
        {
            button.Content = status.Registered ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBE01AA28A44C6B3F) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBB7234EC12451361);
            button.IsEnabled = status.Supported && !_changingWindowsPasskeys;
        }
    }

    private async void OnWindowsPasskeyRegistrationClick(object sender, RoutedEventArgs args)
    {
        if (_changingWindowsPasskeys)
            return;
        _changingWindowsPasskeys = true;
        var provider = global::App.App.Services.GetRequiredService<IWindowsPasskeyProvider>();
        try
        {
            var status = provider.GetStatus();
            if (!status.Supported)
            {
                ShowFeedback(status.Description, InfoBarSeverity.Warning);
                return;
            }
            RefreshWindowsPasskeyStatus();
            await provider.StartAsync();
            await Task.Run(() =>
            {
                if (status.Registered) provider.Unregister();
                else provider.Register();
            });
            ShowFeedback(status.Registered
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0A8D9B08FA8CA789)
                : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAF04FDB0DAD1CE96),
                InfoBarSeverity.Informational);
        }
        catch (Exception exception) when (IsRecoverableIntegrationFailure(exception))
        {
            CrashLogger.WriteRedacted("WindowsPasskeyRegistration", exception);
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3FA3E19224067398), InfoBarSeverity.Error);
        }
        finally
        {
            _changingWindowsPasskeys = false;
            RefreshWindowsPasskeyStatus();
        }
    }

    private async void OnWindowsPasskeySettingsClick(object sender, RoutedEventArgs args)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:passkeys")))
                ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text470690820B67286F), InfoBarSeverity.Warning);
        }
        catch (Exception exception) when (IsRecoverableIntegrationFailure(exception))
        {
            CrashLogger.WriteRedacted("WindowsPasskeySettings", exception);
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text470690820B67286F), InfoBarSeverity.Error);
        }
    }

    private void OnAccountManagementClick(object sender, RoutedEventArgs args)
    {
        ShowSubpage(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1D85C11E14444F61));
        SettingsContentFrame.Navigate(typeof(AccountManagementPage));
    }

    private static bool IsRecoverableIntegrationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or NotSupportedException or OperationCanceledException
            or System.Runtime.InteropServices.ExternalException
            or System.Security.Cryptography.CryptographicException;
}
