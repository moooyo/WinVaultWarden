using Core.Services;
using Core.WindowsPasskeys;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace App.Services;

/// <summary>
/// Approval for platform-authenticated requests. Windows supplies an RP and a hash;
/// this presentation deliberately does not invent a browser origin or caller identity.
/// </summary>
public sealed class WindowsPasskeyApprovalService(
    IVaultUiService vault,
    IVaultService session,
    IItemRepromptGate reprompt,
    IUserVerificationService verification) : IWindowsPasskeyApprovalService
{
    private readonly SemaphoreSlim _dialogGate = PasskeyApprovalDialogGate.Shared;
    private static WindowsPasskeyApproval Rejected => new(new(false, false, false));

    public async Task<bool> EnsureUnlockedAsync(CancellationToken ct)
    {
        var window = global::App.App.MainWindow;
        if (window is null) return false;
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            if (ct.IsCancellationRequested) return;
            window.RestoreFromTray();
            window.Activate();
        })) return false;
        // The normal sign-in/unlock UI owns authentication. No password or decrypted
        // key is passed to the COM host. The platform request's deadline bounds this wait.
        while (session.Snapshot.State != Core.Session.VaultState.Unlocked)
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
        return true;
    }

    public Task<WindowsPasskeyApproval> ConfirmCreateAsync(WindowsPasskeyApprovalRequest request, CancellationToken ct) =>
        ConfirmAsync(request, create: true, ct);

    public Task<WindowsPasskeyApproval> ConfirmUseAsync(WindowsPasskeyApprovalRequest request, CancellationToken ct) =>
        ConfirmAsync(request, create: false, ct);

    private async Task<WindowsPasskeyApproval> ConfirmAsync(
        WindowsPasskeyApprovalRequest request, bool create, CancellationToken ct)
    {
        await _dialogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var window = global::App.App.MainWindow;
            if (window is null) return Rejected;
            var completion = new TaskCompletionSource<WindowsPasskeyApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => completion.TrySetCanceled(ct));
            if (!window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    completion.TrySetResult(await ConfirmOnUiAsync(window, request, create, ct));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); }
                catch (Exception exception) { completion.TrySetException(exception); }
            })) return Rejected;
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _dialogGate.Release(); }
    }

    private async Task<WindowsPasskeyApproval> ConfirmOnUiAsync(
        MainWindow window, WindowsPasskeyApprovalRequest request, bool create, CancellationToken ct)
    {
        if (window.Content is not FrameworkElement root || root.XamlRoot is null) return Rejected;
        window.Activate();
        var panel = new StackPanel { Spacing = 12, MaxWidth = 480 };
        panel.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA0448EAC4AC1D27B),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text0354330A72695CAF,request.RpId), TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        if (create)
        {
            panel.Children.Add(new TextBlock
            {
                Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextD8CD9318E145A089,request.UserDisplayName ?? request.UserName ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEFEBA845669841BE)),
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(request.RpName))
                panel.Children.Add(new TextBlock { Text = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextA7EF418BF03E67A8,request.RpName), TextWrapping = TextWrapping.Wrap });
        }
        ComboBox? selection = null;
        if (!create)
        {
            selection = new ComboBox
            {
                Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0E4DBF48C2E075E2), ItemsSource = request.Candidates,
                DisplayMemberPath = nameof(PasskeyCredentialTarget.DisplayName), HorizontalAlignment = HorizontalAlignment.Stretch,
                SelectedIndex = request.Candidates.Count == 1 ? 0 : -1,
            };
            AutomationProperties.SetName(selection, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text96B35384FED857D2));
            panel.Children.Add(selection);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot, Title = create ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFED68D9E019CD3D7) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD9E4422AF2BDD1C1),
            Content = panel, PrimaryButtonText = create ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFB963E5B8E5FC451) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7A970CB4533F7DD1),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text19766ED6CCB2F4A3), DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = create || selection?.SelectedItem is not null,
        };
        if (selection is not null)
            selection.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = selection.SelectedItem is not null;
        if (await global::App.Views.DialogHelper.ShowTrackedAsync(dialog, ct: ct) != ContentDialogResult.Primary)
            return Rejected;
        ct.ThrowIfCancellationRequested();
        var selected = selection?.SelectedItem as PasskeyCredentialTarget;
        var verified = false;
        if (!create)
        {
            if (selected is null) return Rejected;
            var policy = vault.GetAccessPolicy(selected.CipherId);
            var wasAuthorized = reprompt.IsAuthorized(policy);
            var authorized = await reprompt.AuthorizeAsync(policy, SensitiveItemAction.UsePasskey,
                token => PromptPasswordAsync(root, token), ct);
            if (!authorized.IsSuccess) return Rejected;
            verified = policy.RequiresReprompt && !wasAuthorized;
        }
        // A real verification gesture is required on every platform operation, including
        // requests which did not themselves require UV. Unlock state alone is not evidence.
        if (!verified)
            verified = await verification.VerifyAsync(token => PromptPasswordAsync(root, token), ct);
        ct.ThrowIfCancellationRequested();
        return verified
            ? new WindowsPasskeyApproval(new(true, true, true), selected?.CipherId, selected?.CredentialId)
            : Rejected;
    }

    private static async Task<string?> PromptPasswordAsync(FrameworkElement root, CancellationToken ct)
    {
        var password = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF4C2EE7D031E215C) };
        AutomationProperties.SetName(password, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE0E470C52DB10634));
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot, Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text54A89EDB1299F5F9), Content = password,
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEEA2745E2867A677), CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text19766ED6CCB2F4A3), DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            return await global::App.Views.DialogHelper.ShowTrackedAsync(dialog,
                clearSensitive: () => password.Password = string.Empty, ct: ct) == ContentDialogResult.Primary
                ? password.Password : null;
        }
        finally { password.Password = string.Empty; }
    }
}
