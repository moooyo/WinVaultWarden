using App.Services;
using App.ViewModels;
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
    public GeneratorViewModel ViewModel => Workspace.ViewModel;

    public GeneratorPage()
    {
        _session = global::App.App.Services.GetRequiredService<VaultSession>();
        _lifecycle = global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>();
        InitializeComponent();
    }

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PageLayout.Padding = e.NewSize.Width < 540 ? new Thickness(24,32,24,40) : new Thickness(40,40,40,48);
    }

    public void SelectMode(string mode) => ViewModel.Mode = mode switch
    {
        "passphrase" => GeneratorMode.Passphrase,
        "username" => GeneratorMode.Username,
        _ => GeneratorMode.Password,
    };

    private void OnNewLoginRequested(object? sender, EventArgs e)
    {
        if (ViewModel.CanCopy && !ViewModel.IsUsernameMode)
            global::App.App.Services.GetRequiredService<INavigationService>().GoToVault("vault:new-generated");
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
            if (await DialogHelper.ConfirmAsync(XamlRoot, expectedSession, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0855A60751CB08D3), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text92FD0A1DFF6FC9B6), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBCE2377283C2455A)))
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
