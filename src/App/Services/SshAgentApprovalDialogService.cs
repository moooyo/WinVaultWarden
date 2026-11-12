using System.Globalization;
using Core.Services;
using Core.SshAgent;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vault;

namespace App.Services;

/// <summary>Obtains an explicit one-signature grant and fresh Hello or master-password verification.</summary>
public sealed class SshAgentApprovalDialogService(
    VaultSession session,
    IAccountService account,
    IUserVerificationService userVerification) : ISshAgentApprovalService
{
    private readonly SemaphoreSlim _dialogGate = PasskeyApprovalDialogGate.Shared;

    public async Task<bool> ConfirmAsync(SshAgentApprovalRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _dialogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DispatchApprovalAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task<bool> DispatchApprovalAsync(SshAgentApprovalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var window = global::App.App.MainWindow;
        if (window is null || ct.IsCancellationRequested)
            return false;
        if (window.DispatcherQueue.HasThreadAccess)
            return await ConfirmCoreAsync(window, request, ct);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            try { completion.TrySetResult(await ConfirmCoreAsync(window, request, ct)); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Approval fails closed. Do not copy exception messages from security operations.
                completion.TrySetResult(false);
            }
        }))
            return false;
        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        try { return await completion.Task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
    }

    private async Task<bool> ConfirmCoreAsync(MainWindow window, SshAgentApprovalRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var lease = session.CaptureLease();
        if (!session.IsUnlocked(lease) || window.Content is not FrameworkElement root || root.XamlRoot is null)
            return false;
        window.Activate();
        var content = new StackPanel { Spacing = 10, MaxWidth = 480 };
        AddLine(content, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text99A52DF3FF3D4994), request.KeyName);
        AddLine(content, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5FB314B9AE1A407C), request.Fingerprint);
        AddLine(content, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD704D8AF25BED5A5), request.Algorithm);
        AddLine(content, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE147801B701CB331), request.Client.ExecutablePath);
        AddLine(content, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text125DD7D7F1E3D786), request.Client.ProcessId.ToString(CultureInfo.InvariantCulture));
        AddLine(content, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB4D508D432AD5DE8), global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text9C9AF111AB2F9FD2,request.DataLength,request.DataFingerprint));
        content.Children.Add(new InfoBar
        {
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Warning,
            Message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4ACE1BAF6C0EFA77),
        });
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2A698B0818BA0377),
            Content = content,
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E18A3FE53B32A6A),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text05A2D7332EB9D8BF),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await global::App.Views.DialogHelper.ShowTrackedAsync(dialog, lease, ct: ct)
            != ContentDialogResult.Primary)
            return false;
        ct.ThrowIfCancellationRequested();
        if (!session.IsUnlocked(lease))
            return false;
        bool verified;
        if (request.RequiresMasterPassword)
        {
            // A reprompt key requires a master password for this request, without a grace-period cache.
            var password = await PromptPasswordAsync(root, lease, ct);
            verified = password is not null && await account.VerifyMasterPasswordAsync(password, ct);
        }
        else
        {
            verified = await userVerification.VerifyAsync(
                token => PromptPasswordAsync(root, lease, token), ct);
        }
        return verified && !ct.IsCancellationRequested && session.IsUnlocked(lease);
    }

    private static async Task<string?> PromptPasswordAsync(
        FrameworkElement root, Core.Session.VaultSessionLease lease, CancellationToken ct)
    {
        var password = new PasswordBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF4C2EE7D031E215C), MinWidth = 280 };
        var dialog = new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text567543B9F66FD8F3),
            Content = password,
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEEA2745E2867A677),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text05A2D7332EB9D8BF),
            DefaultButton = ContentDialogButton.Close,
        };
        try
        {
            return await global::App.Views.DialogHelper.ShowTrackedAsync(
                dialog, lease, clearSensitive: () => password.Password = string.Empty, ct: ct)
                == ContentDialogResult.Primary ? password.Password : null;
        }
        finally
        {
            password.Password = string.Empty;
        }
    }

    private static void AddLine(StackPanel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock
        {
            Text = label,
            Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
        });
        var displayValue = new string(value.Take(2_048)
            .Select(character => char.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format
                ? '\uFFFD' : character).ToArray());
        var text = new TextBlock { Text = displayValue, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(text, $"{label}: {displayValue}");
        panel.Children.Add(text);
    }
}
