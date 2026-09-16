using App.Services;
using Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vault;

namespace App.Views;

public sealed partial class GeneratorPage : Page
{
    private bool _historyOpen;
    private readonly VaultSession _session;
    private readonly IAppLifecycleCoordinator _lifecycle;

    public GeneratorPage()
    {
        _session = global::App.App.Services.GetRequiredService<VaultSession>();
        _lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
        InitializeComponent();
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        GuidanceRail.Visibility = e.NewSize.Width >= 960 ? Visibility.Visible : Visibility.Collapsed;
        PageLayout.Padding = new Thickness(e.NewSize.Width < 640 ? 20 : 32);
    }

    private async void OnHistoryRequested(object? sender, EventArgs e)
    {
        if (_historyOpen || XamlRoot is null || _session.State != VaultState.Unlocked)
            return;
        _historyOpen = true;
        try
        {
            var expectedSession = _session.CaptureLease();
            var history = new GeneratorHistoryDialog(Workspace.ViewModel) { XamlRoot = XamlRoot };
            await DialogHelper.ShowTrackedAsync(history, expectedSession);
            if (!_session.IsCurrent(expectedSession) || !history.ClearRequested)
                return;
            if (await DialogHelper.ConfirmAsync(XamlRoot, expectedSession, "清除历史记录", "清除本次解锁期间复制的全部记录？已保存到保险库的条目不受影响。", "清除"))
                _lifecycle.TryRunSensitive(() => _session.TryRunIfUnlocked(expectedSession,
                    () => Workspace.ViewModel.ClearHistoryCommand.Execute(null)));
        }
        catch (Exception ex)
        {
            CrashLogger.WriteRedacted("GeneratorHistory", ex);
        }
        finally
        {
            _historyOpen = false;
        }
    }

}
