using App.Services;
using App.ViewModels;
using Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class AccountManagementPage : Page
{
    private IDisposable? _registration;
    private CancellationTokenSource? _pageCancellation;

    public AccountManagementViewModel ViewModel { get; }

    public AccountManagementPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<AccountManagementViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _pageCancellation?.Dispose();
        _pageCancellation = new();
        _registration?.Dispose();
        _registration = global::App.App.Services.GetRequiredService<ITransientOperationRegistry>()
            .Register(CancelOperations, ClearSensitive, ClearSensitive);
        ViewModel.RefreshAvailability();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelOperations();
        ClearSensitive();
        _registration?.Dispose();
        _registration = null;
        _pageCancellation?.Dispose();
        _pageCancellation = null;
    }

    private void CancelOperations()
    {
        try { _pageCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void ClearSensitive()
    {
        EmailPasswordBox.Password = string.Empty;
        RevokePasswordBox.Password = string.Empty;
        EmailCodeBox.Text = string.Empty;
        VerificationTokenBox.Text = string.Empty;
        ViewModel.ClearSensitive();
    }

    private CancellationToken CurrentToken => _pageCancellation?.Token ?? new CancellationToken(true);

    private InfoBarSeverity FeedbackSeverity(bool isError) =>
        isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    private void OnNewEmailChanged(object sender, TextChangedEventArgs e)
    {
        if (ViewModel is not null)
        {
            ViewModel.ResetEmailChangeChallenge();
            if (EmailCodeBox is not null)
                EmailCodeBox.Text = string.Empty;
        }
    }

    private async void OnRequestVerificationClick(object sender, RoutedEventArgs e) =>
        await ViewModel.RequestEmailVerificationAsync(CurrentToken);

    private async void OnVerifyEmailClick(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.VerifyEmailAsync(VerificationTokenBox.Text, CurrentToken);
        if (result.IsSuccess)
            VerificationTokenBox.Text = string.Empty;
    }

    private async void OnRequestChangeClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.RequestEmailChangeAsync(EmailPasswordBox.Password, NewEmailBox.Text, CurrentToken);
        EmailPasswordBox.Password = string.Empty;
    }

    private async void OnChangeEmailClick(object sender, RoutedEventArgs e)
    {
        if (!await DialogHelper.ConfirmAsync(XamlRoot, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDD16E499335D3A53),
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text416BAB9DEDC6581F)
                + global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9B76C00ADEF1456F),
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFF2EC2618B91B367), closeText: global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text19766ED6CCB2F4A3), ct: CurrentToken))
            return;
        var result = await ViewModel.ChangeEmailAsync(
            EmailPasswordBox.Password, NewEmailBox.Text, EmailCodeBox.Text, CurrentToken);
        EmailPasswordBox.Password = string.Empty;
        if (result.IsSuccess && result.Value is { } receipt)
            await CompleteAndShowOutcomeAsync(receipt,
                "The server confirmed the email change. Sign in again with your new email address and unchanged master password.");
    }

    private async void OnRevokeSessionsClick(object sender, RoutedEventArgs e)
    {
        if (!await DialogHelper.ConfirmAsync(XamlRoot, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text848B8E2997376686),
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCE5D5ACDD0810DD3)
                + global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text757D746FF157EC07)
                + global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF85952B02C2A6AAD), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE1A8ADE585EDAB9A),
                closeText: global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text19766ED6CCB2F4A3), ct: CurrentToken))
            return;
        var result = await ViewModel.RevokeAllSessionsAsync(RevokePasswordBox.Password, CurrentToken);
        RevokePasswordBox.Password = string.Empty;
        if (result.IsSuccess && result.Value is { } receipt)
            await CompleteAndShowOutcomeAsync(receipt,
                "The server confirmed that all sessions were revoked. Sign in again to continue.");
    }

    private async Task CompleteAndShowOutcomeAsync(AccountSecurityChangeReceipt receipt, string confirmedMessage)
    {
        await ViewModel.CompleteSecurityChangeAsync(receipt);
        var current = global::App.App.Services.GetRequiredService<Vault.VaultSession>();
        if (current.State == Core.Session.VaultState.Unlocked
            || global::App.App.MainWindow?.Content is not FrameworkElement host
            || host.XamlRoot is not { } root)
            return;
        var message = receipt.Outcome == AccountSecurityChangeOutcome.Confirmed
            ? confirmedMessage
            : "The server response could not be confirmed. The operation may already have completed. "
                + "This device has been locked or signed out. Check the server and sign-in state before trying again.";
        if (!receipt.LocalCleanupSucceeded)
        {
            message += " Some local account data could not be confirmed as removed. "
                + "Do not reuse saved credentials until local recovery has completed.";
        }
        await DialogHelper.ShowTrackedAsync(new ContentDialog
        {
            Title = receipt.Outcome == AccountSecurityChangeOutcome.Confirmed
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0CAFAAC6A54D0BAE) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCBCA517D399A5E23),
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
            CloseButtonText = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7D9EB7ACB13E2462),
            XamlRoot = root,
        });
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        CancelOperations();
        ClearSensitive();
        global::App.App.Services.GetRequiredService<INavigationService>().GoToSettings();
    }
}
