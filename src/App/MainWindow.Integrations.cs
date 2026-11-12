using App.Services;
using Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Vault;

namespace App;

public sealed partial class MainWindow
{
    public void InitializeLockedDeviceRequestIntegration()
    {
        var windows = global::App.App.Services.GetRequiredService<LockedDeviceRequestWindowService>();
        windows.NoticeRaised += ShowNotice;
        windows.UnlockRequested = requested =>
        {
            var session = global::App.App.Services.GetRequiredService<VaultSession>();
            if (session.State != VaultState.Locked)
                return;
            var current = session.CaptureLease();
            if (current.AccountId != requested.AccountId || current.Endpoints != requested.Endpoints)
            {
                windows.Stop();
                return;
            }
            RestoreFromTray();
            Activate();
        };
        Closed += (_, _) => windows.Stop();
    }

    private void StartOptionalVaultIntegrations()
    {
        if (AppPreferences.Current.SshAgentEnabled)
        {
            try
            {
                var result = global::App.App.Services.GetRequiredService<ISshAgentHost>().Start();
                if (result is not (SshAgentStartResult.Started or SshAgentStartResult.AlreadyRunning))
                    ShowNotice(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3F04EFE8589029D6));
            }
            catch (Exception exception)
            {
                CrashLogger.WriteRedacted("SshAgentStartup", exception);
                ShowNotice(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3F04EFE8589029D6));
            }
        }
        _ = RefreshWindowsPasskeyMetadataAsync();
        _ = ResumeDeviceRequestMonitoringAsync();
    }

    private static async Task ResumeDeviceRequestMonitoringAsync()
    {
        try
        {
            if (!AppPreferences.Current.MonitorDeviceRequestsWhileLocked)
            {
                global::App.App.Services.GetService<LockedDeviceRequestMonitor>()?.Stop();
                return;
            }
            var session = global::App.App.Services.GetRequiredService<VaultSession>();
            var lease = session.CaptureLease();
            await global::App.App.Services.GetRequiredService<LockedDeviceRequestWindowService>()
                .OnUnlockedAsync(lease, AppPreferences.Current.MonitorDeviceRequestsWhileLocked);
        }
        catch (Exception exception)
        {
            CrashLogger.WriteRedacted("LockedDeviceRequests", exception);
        }
    }

    private static async Task RefreshWindowsPasskeyMetadataAsync()
    {
        try
        {
            await Task.Run(() => global::App.App.Services
                .GetRequiredService<IWindowsPasskeyProvider>().SynchronizeMetadata());
        }
        catch (Exception exception)
        {
            CrashLogger.WriteRedacted("WindowsPasskeyMetadata", exception);
        }
    }
}
