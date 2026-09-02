using App.Views;
using System.Runtime.InteropServices;
using Core.Models;
using Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace App.Services;

public sealed class DuoWebViewTwoFactorService : IDuoTwoFactorService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool CanAuthenticate(string providerDataJson, ServerEndpoints endpoints)
    {
        if (!IsSupported)
            return false;
        try
        {
            _ = DuoTwoFactorProtocol.ParseChallenge(providerDataJson, endpoints);
            return true;
        }
        catch (DuoTwoFactorException)
        {
            return false;
        }
    }

    public async Task<string> AuthenticateAsync(
        string providerDataJson,
        ServerEndpoints endpoints,
        CancellationToken ct = default)
    {
        var challenge = DuoTwoFactorProtocol.ParseChallenge(providerDataJson, endpoints);
        if (!IsSupported)
            throw new DuoTwoFactorException("Duo WebView authentication is not available.");

        var window = global::App.App.MainWindow
            ?? throw new DuoTwoFactorException("The Duo parent window is unavailable.");
        if (!window.DispatcherQueue.HasThreadAccess)
            throw new DuoTwoFactorException("Duo authentication must start on the UI thread.");
        if (window.Content is not FrameworkElement root || root.XamlRoot is null)
            throw new DuoTwoFactorException("The Duo XAML root is unavailable.");

        await _gate.WaitAsync(ct);
        try
        {
            return await ShowPromptAsync(challenge, root.XamlRoot, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> ShowPromptAsync(
        DuoTwoFactorChallenge challenge,
        XamlRoot xamlRoot,
        CancellationToken ct)
    {
        var completion = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalState = 0;

        bool TryComplete(string token)
        {
            if (Interlocked.CompareExchange(ref terminalState, 1, 0) != 0)
                return false;
            return completion.TrySetResult(token);
        }

        bool TryFail(Exception exception)
        {
            if (Interlocked.CompareExchange(ref terminalState, 1, 0) != 0)
                return false;
            return completion.TrySetException(exception);
        }

        bool TryCancel()
        {
            if (Interlocked.CompareExchange(ref terminalState, 1, 0) != 0)
                return false;
            return completion.TrySetCanceled(ct);
        }

        var webView = new WebView2
        {
            Width = 480,
            MinWidth = 360,
            MinHeight = 540,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var dialog = new ContentDialog
        {
            Title = "Duo 两步验证",
            Content = webView,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };

        Windows.Foundation.TypedEventHandler<
            ContentDialog,
            ContentDialogClosedEventArgs>? dialogClosedHandler =
            (_, _) => TryFail(new DuoTwoFactorCancelledException());
        dialog.Closed += dialogClosedHandler;
        var dialogTask = DialogHelper.ShowTrackedAsync(
            dialog,
            clearSensitive: () => TryFail(new DuoTwoFactorCancelledException()),
            ct: ct);
        using var cancellationRegistration = ct.Register(() => TryCancel());
        CoreWebView2? core = null;
        Windows.Foundation.TypedEventHandler<
            CoreWebView2,
            CoreWebView2NavigationStartingEventArgs>? navigationHandler = null;
        Windows.Foundation.TypedEventHandler<
            CoreWebView2,
            CoreWebView2NewWindowRequestedEventArgs>? newWindowHandler = null;
        Windows.Foundation.TypedEventHandler<
            CoreWebView2,
            CoreWebView2DownloadStartingEventArgs>? downloadHandler = null;
        Windows.Foundation.TypedEventHandler<
            CoreWebView2,
            CoreWebView2PermissionRequestedEventArgs>? permissionHandler = null;
        Windows.Foundation.TypedEventHandler<
            CoreWebView2,
            CoreWebView2ProcessFailedEventArgs>? processFailedHandler = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                null,
                GetUserDataFolder(),
                null);
            if (Volatile.Read(ref terminalState) != 0)
                throw new DuoTwoFactorCancelledException();

            var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
            controllerOptions.ProfileName = "WinVaultWarden-Duo";
            controllerOptions.IsInPrivateModeEnabled = true;
            await webView.EnsureCoreWebView2Async(environment, controllerOptions);
            core = webView.CoreWebView2
                ?? throw new DuoTwoFactorException("Duo WebView initialization failed.");
            if (Volatile.Read(ref terminalState) != 0)
                throw new DuoTwoFactorCancelledException();
            if (!core.Profile.IsInPrivateModeEnabled)
                throw new DuoTwoFactorException("Duo WebView did not enter InPrivate mode.");

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            navigationHandler = (_, args) =>
            {
                if (Volatile.Read(ref terminalState) != 0)
                {
                    args.Cancel = true;
                    return;
                }

                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
                {
                    args.Cancel = true;
                    if (TryFail(new DuoTwoFactorException("Duo navigation URI was invalid.")))
                        TryHide(dialog);
                    return;
                }

                var decision = DuoNavigationPolicy.Evaluate(challenge, uri);
                switch (decision.Action)
                {
                    case DuoNavigationAction.Allow:
                        return;
                    case DuoNavigationAction.Complete:
                        args.Cancel = true;
                        if (TryComplete(decision.Token!))
                            TryHide(dialog);
                        return;
                    default:
                        args.Cancel = true;
                        if (TryFail(new DuoTwoFactorException("Duo navigation was rejected.")))
                            TryHide(dialog);
                        return;
                }
            };
            newWindowHandler = (_, args) =>
            {
                args.Handled = true;
            };
            downloadHandler = (_, args) =>
            {
                args.Cancel = true;
                args.Handled = true;
            };
            permissionHandler = (_, args) =>
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.Handled = true;
            };
            processFailedHandler = (_, _) =>
            {
                if (TryFail(new DuoTwoFactorException("The Duo WebView process failed.")))
                    TryHide(dialog);
            };
            core.NavigationStarting += navigationHandler;
            core.NewWindowRequested += newWindowHandler;
            core.DownloadStarting += downloadHandler;
            core.PermissionRequested += permissionHandler;
            core.ProcessFailed += processFailedHandler;
            if (Volatile.Read(ref terminalState) == 0)
                core.Navigate(challenge.AuthorizationUri.AbsoluteUri);

            var completed = await Task.WhenAny(completion.Task, dialogTask);
            if (completed != completion.Task)
            {
                _ = await dialogTask;
                if (ct.IsCancellationRequested)
                    TryCancel();
                else
                    TryFail(new DuoTwoFactorCancelledException());
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryCancel();
        }
        catch (DuoTwoFactorException ex)
        {
            TryFail(ex);
        }
        catch (Exception ex)
        {
            TryFail(new DuoTwoFactorException("Duo WebView authentication failed.", ex));
        }

        try
        {
            TryHide(dialog);
            _ = await dialogTask;
        }
        catch (Exception ex)
        {
            TryFail(new DuoTwoFactorException("The Duo dialog failed to close.", ex));
        }
        finally
        {
            dialog.Closed -= dialogClosedHandler;
            if (core is not null)
            {
                try
                {
                    core.Stop();
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                }

                try
                {
                    if (navigationHandler is not null)
                        core.NavigationStarting -= navigationHandler;
                    if (newWindowHandler is not null)
                        core.NewWindowRequested -= newWindowHandler;
                    if (downloadHandler is not null)
                        core.DownloadStarting -= downloadHandler;
                    if (permissionHandler is not null)
                        core.PermissionRequested -= permissionHandler;
                    if (processFailedHandler is not null)
                        core.ProcessFailed -= processFailedHandler;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                }

                try
                {
                    await core.Profile.ClearBrowsingDataAsync(
                        CoreWebView2BrowsingDataKinds.AllProfile);
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException)
                {
                }
            }

            try
            {
                webView.Close();
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException)
            {
            }
        }

        return await completion.Task;
    }

    private static string GetUserDataFolder()
    {
        return Path.Combine(AppStorageLayout.CreateDefault().RootPath, "WebView2");
    }

    private static void TryHide(ContentDialog dialog)
    {
        try
        {
            dialog.Hide();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
        }
    }
}
