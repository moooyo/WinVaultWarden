using App.Services;
using App.ViewModels.Models;
using Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class VaultPage
{
    private long _editorFolderInteractionVersion;
    private CancellationTokenSource? _editorFolderCancellation;
    private ContentDialog? _editorFolderDialog;

    private void CancelEditorFolderInteraction()
    {
        _editorFolderInteractionVersion++;
        _editorFolderCancellation?.Cancel();
        _editorFolderDialog?.Hide();
    }

    private bool CanContinueEditorFolderInteraction(
        CipherEditorDraft draft,
        VaultSessionLease lease,
        long version) =>
        version == _editorFolderInteractionVersion && IsLoaded && XamlRoot is not null
        && ViewModel.IsEditing && ViewModel.CanEditEditorDraft
        && ReferenceEquals(ViewModel.EditorDraft, draft)
        && global::App.App.Services.GetRequiredService<Vault.VaultSession>().IsUnlocked(lease);

    private async Task CreateFolderFromEditorAsync(
        CipherEditorDraft draft,
        VaultSessionLease lease,
        long menuVersion)
    {
        if (_folderInteractionPending || !CanContinueEditorFolderInteraction(draft, lease, menuVersion))
            return;

        _folderInteractionPending = true;
        var version = ++_editorFolderInteractionVersion;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lease.CancellationToken);
        _editorFolderCancellation = cancellation;
        var input = new TextBox { Header = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBACF701908D8CF45) };
        AutomationProperties.SetAutomationId(input, "VaultEditorFolderNameTextBox");
        var dialog = new ContentDialog
        {
            Title = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text84244ABC71DE03AC),
            Content = input,
            PrimaryButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text50EF2F4CF6A46924),
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2CD0F3BE8738A86C),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot,
        };
        AutomationProperties.SetAutomationId(dialog, "VaultEditorCreateFolderDialog");
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        _editorFolderDialog = dialog;
        using var hideOnCancellation = cancellation.Token.Register(
            () => dialog.DispatcherQueue.TryEnqueue(dialog.Hide));

        try
        {
            var answer = await DialogHelper.ShowTrackedAsync(
                dialog,
                lease,
                () => input.Text = string.Empty,
                cancellation.Token);
            _editorFolderDialog = null;
            if (answer != ContentDialogResult.Primary || cancellation.IsCancellationRequested
                || !CanContinueEditorFolderInteraction(draft, lease, version))
                return;
            var name = input.Text;
            if (string.IsNullOrWhiteSpace(name))
                return;

            var services = global::App.App.Services;
            var session = services.GetRequiredService<Vault.VaultSession>();
            Task<UiOperationResult>? creation = null;
            services.GetRequiredService<IAppLifecycleCoordinator>().TryRunSensitive(
                () => session.TryRunIfUnlocked(lease, () =>
                {
                    if (!cancellation.IsCancellationRequested && CanContinueEditorFolderInteraction(draft, lease, version))
                        creation = ViewModel.CreateFolderForEditorAsync(draft, name, cancellation.Token);
                }));
            if (creation is null || !(await creation).IsSuccess)
                return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (CanContinueEditorFolderInteraction(draft, lease, version) && !HasOpenVaultPopup())
                    CipherEditorFolderBox.Focus(FocusState.Programmatic);
            });
        }
        finally
        {
            input.Text = string.Empty;
            if (ReferenceEquals(_editorFolderDialog, dialog))
                _editorFolderDialog = null;
            if (ReferenceEquals(_editorFolderCancellation, cancellation))
                _editorFolderCancellation = null;
            _folderInteractionPending = false;
        }
    }
}
