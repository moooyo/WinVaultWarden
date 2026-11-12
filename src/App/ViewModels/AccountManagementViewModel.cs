using Api;
using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Services;
using Core.Session;
using Vault;

namespace App.ViewModels;

public partial class AccountManagementViewModel(
    IAccountManagementService management,
    VaultSession session,
    IAppLifecycleCoordinator lifecycle) : ObservableObject, ISensitiveState
{
    private readonly AsyncOperationGate _operations = new();
    private CancellationTokenSource? _activeOperation;
    private string? _pendingEmail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    [NotifyPropertyChangedFor(nameof(CanRequestEmailChange))]
    [NotifyPropertyChangedFor(nameof(CanConfirmEmailChange))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRequestEmailChange))]
    [NotifyPropertyChangedFor(nameof(CanConfirmEmailChange))]
    public partial bool CanChangeEmail { get; set; }

    [ObservableProperty]
    public partial string? EmailChangeUnavailableReason { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmEmailChange))]
    public partial bool EmailChangeCodeSent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    public partial string? Feedback { get; set; }

    [ObservableProperty]
    public partial bool FeedbackIsError { get; set; }

    public bool CanOperate => !IsBusy;
    public bool CanRequestEmailChange => CanChangeEmail && !IsBusy;
    public bool CanConfirmEmailChange => CanChangeEmail && EmailChangeCodeSent && !IsBusy;
    public bool HasFeedback => !string.IsNullOrEmpty(Feedback);
    public string CurrentEmail => session.Account.Email;

    public void RefreshAvailability()
    {
        try
        {
            var available = management.GetAvailability();
            CanChangeEmail = available.CanChangeEmail;
            EmailChangeUnavailableReason = available.EmailChangeUnavailableReason;
        }
        catch (InvalidOperationException)
        {
            CanChangeEmail = false;
            EmailChangeUnavailableReason = "Unlock the vault before managing the account.";
        }
        OnPropertyChanged(nameof(CurrentEmail));
    }

    public void ResetEmailChangeChallenge()
    {
        _pendingEmail = null;
        EmailChangeCodeSent = false;
    }

    public async Task<UiOperationResult> RequestEmailChangeAsync(
        string password, string email, CancellationToken ct = default)
    {
        var result = await ExecuteAsync(async token =>
        {
            await management.RequestEmailChangeAsync(password, email, token);
            return true;
        }, "The request could not be completed. Check the address and server email configuration.", ct);
        if (result.IsSuccess)
        {
            _pendingEmail = email.Trim().ToLowerInvariant();
            EmailChangeCodeSent = true;
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE00488BD049EFA56)
                + global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text71270C8D604BBA32));
            return UiOperationResult.Success();
        }
        return ToUntyped(result);
    }

    public Task<UiOperationResult<AccountSecurityChangeReceipt>> ChangeEmailAsync(
        string password, string email, string token, CancellationToken ct = default)
    {
        if (!EmailChangeCodeSent
            || !string.Equals(_pendingEmail, email.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            const string error = "Request a code for this email address before confirming the change.";
            ShowFeedback(error, true);
            return Task.FromResult(UiOperationResult<AccountSecurityChangeReceipt>.Failure(error));
        }
        return ExecuteAsync(
            operationToken => management.ChangeEmailAsync(password, email, token, operationToken),
            "The email change was rejected. Check the password and code, then try again.", ct);
    }

    public async Task<UiOperationResult> RequestEmailVerificationAsync(CancellationToken ct = default)
    {
        var result = await ExecuteAsync(async token =>
        {
            await management.RequestEmailVerificationAsync(token);
            return true;
        }, "The verification email could not be requested. Check the server email configuration.", ct);
        if (result.IsSuccess)
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text66E3D0FD13E9F509)
                + global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1373D35855901FEC));
        return ToUntyped(result);
    }

    public async Task<UiOperationResult> VerifyEmailAsync(string token, CancellationToken ct = default)
    {
        var result = await ExecuteAsync(async operationToken =>
        {
            await management.VerifyEmailAsync(token, operationToken);
            return true;
        }, "The verification token was rejected or expired.", ct);
        if (result.IsSuccess)
            ShowFeedback(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text67DE19720479F36B));
        return ToUntyped(result);
    }

    public Task<UiOperationResult<AccountSecurityChangeReceipt>> RevokeAllSessionsAsync(
        string password, CancellationToken ct = default) =>
        ExecuteAsync(
            token => management.RevokeAllSessionsAsync(password, token),
            "The session-revocation request was rejected. Verify the current master password.", ct);

    public Task CompleteSecurityChangeAsync(AccountSecurityChangeReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return lifecycle.RequestAsync(AppLifecycleIntent.CredentialChanged,
            receipt.InvalidatedSession ?? receipt.OriginalSession);
    }

    public void ClearSensitive()
    {
        try { _activeOperation?.Cancel(); }
        catch (ObjectDisposedException) { }
        ResetEmailChangeChallenge();
        Feedback = null;
    }

    private async Task<UiOperationResult<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action, string failureMessage, CancellationToken ct)
    {
        if (!_operations.TryEnter(out var entered))
            return UiOperationResult<T>.Busy();
        using (entered)
        {
            VaultSessionLease lease;
            try
            {
                ct.ThrowIfCancellationRequested();
                lease = session.CaptureLease();
                if (!session.IsUnlocked(lease))
                    return UiOperationResult<T>.Cancelled();
            }
            catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
            {
                return UiOperationResult<T>.Cancelled();
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
            _activeOperation = linked;
            IsBusy = true;
            Feedback = null;
            try
            {
                var result = await action(linked.Token);
                if (result is not AccountSecurityChangeReceipt
                    && (!session.IsCurrent(lease) || linked.IsCancellationRequested))
                    return UiOperationResult<T>.Cancelled();
                return UiOperationResult<T>.Success(result);
            }
            catch (OperationCanceledException ex)
                when (UiExceptionPolicy.IsGenuineCancellation(ex, linked.Token))
            {
                return UiOperationResult<T>.Cancelled();
            }
            catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
            {
                const string message = "The request timed out. Check the server before retrying.";
                PublishFailure(lease, message);
                return UiOperationResult<T>.Failure(message);
            }
            catch (AccountOperationException ex)
            {
                PublishFailure(lease, ex.Message);
                return UiOperationResult<T>.Failure(ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                or FormatException or ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                PublishFailure(lease, failureMessage);
                return UiOperationResult<T>.Failure(failureMessage);
            }
            finally
            {
                if (ReferenceEquals(_activeOperation, linked))
                    _activeOperation = null;
                IsBusy = false;
            }
        }
    }

    private void PublishFailure(VaultSessionLease lease, string message) =>
        lifecycle.TryRunSensitive(() => session.TryRunIfUnlocked(lease, () => ShowFeedback(message, true)));

    private void ShowFeedback(string message, bool error = false)
    {
        FeedbackIsError = error;
        Feedback = message;
    }

    private static UiOperationResult ToUntyped<T>(UiOperationResult<T> result) => result.Status switch
    {
        UiOperationStatus.Succeeded => UiOperationResult.Success(),
        UiOperationStatus.Cancelled => UiOperationResult.Cancelled(),
        UiOperationStatus.Busy => UiOperationResult.Busy(),
        _ => UiOperationResult.Failure(result.Error ?? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text142AAE58EE55B8A5)),
    };
}
