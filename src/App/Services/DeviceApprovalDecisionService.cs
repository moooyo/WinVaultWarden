using App.ViewModels.Models;
using Api;
using Core.Services;
using Vault;

namespace App.Services;

/// <summary>Revalidates the reviewed request and account before releasing an encrypted user key.</summary>
public sealed class DeviceApprovalDecisionService(
    IAuthRequestUiService requests,
    VaultSession session,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly AsyncOperationGate _operations = new();

    public bool IsPending(AuthRequestItem item) =>
        item.ExpiresAt is { } expiry && expiry > _time.GetUtcNow();

    public async Task<UiOperationResult> ApproveAsync(
        AuthRequestItem reviewed,
        bool fingerprintReviewed,
        Func<CancellationToken, Task<bool>> unlock,
        CancellationToken ct = default)
    {
        if (!fingerprintReviewed || string.IsNullOrWhiteSpace(reviewed.FingerprintPhrase))
            return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text75C3D9954955DB54));
        if (!IsPending(reviewed))
            return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextAA0418AE7E1250BE));
        if (!_operations.TryEnter(out var operation))
            return UiOperationResult.Busy();

        using (operation)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var original = session.CaptureLease();
                if (!session.IsUnlocked(original) && !await unlock(ct))
                    return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text60D04C8338D9A348));

                ct.ThrowIfCancellationRequested();
                var lease = session.CaptureLease();
                if (lease.AccountId != original.AccountId || lease.Endpoints != original.Endpoints
                    || !session.IsUnlocked(lease))
                    return UiOperationResult.Cancelled();

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
                var current = await requests.GetPendingRequestAsync(reviewed.Id, linked.Token);
                if (current is null || !IsPending(current))
                    return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text04308BA8C199CC2D));
                if (current.PublicKey != reviewed.PublicKey
                    || current.FingerprintPhrase != reviewed.FingerprintPhrase
                    || current.CreatedAt != reviewed.CreatedAt
                    || current.ExpiresAt != reviewed.ExpiresAt
                    || current.DeviceTypeName != reviewed.DeviceTypeName
                    || current.IpAddress != reviewed.IpAddress)
                    return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6F7226B3DBB65EF7));
                linked.Token.ThrowIfCancellationRequested();
                if (!session.IsUnlocked(lease))
                    return UiOperationResult.Cancelled();

                await requests.ApproveAsync(current.Id, current.PublicKey, linked.Token);
                return session.IsUnlocked(lease) && !linked.IsCancellationRequested
                    ? UiOperationResult.Success() : UiOperationResult.Cancelled();
            }
            catch (OperationCanceledException ex) when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
            {
                return UiOperationResult.Cancelled();
            }
            catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
            {
                return UiOperationResult.Failure(UiExceptionPolicy.RequestTimeoutMessage);
            }
            catch (Exception ex) when (ex is HttpRequestException or AuthRequestOperationException or InvalidOperationException)
            {
                return UiOperationResult.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD1E2AB0373EF8481));
            }
        }
    }
}
