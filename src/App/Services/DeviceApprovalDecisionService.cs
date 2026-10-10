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
            return UiOperationResult.Failure("请先在请求设备上核对指纹短语，完全一致才批准。");
        if (!IsPending(reviewed))
            return UiOperationResult.Failure("登录请求已过期，请在请求设备上重新发送。");
        if (!_operations.TryEnter(out var operation))
            return UiOperationResult.Busy();

        using (operation)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var original = session.CaptureLease();
                if (!session.IsUnlocked(original) && !await unlock(ct))
                    return UiOperationResult.Failure("尚未完成解锁。请使用 Windows Hello，或先在主窗口解锁密码库。");

                ct.ThrowIfCancellationRequested();
                var lease = session.CaptureLease();
                if (lease.AccountId != original.AccountId || lease.Endpoints != original.Endpoints
                    || !session.IsUnlocked(lease))
                    return UiOperationResult.Cancelled();

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
                var current = (await requests.ListPendingAsync(linked.Token))
                    .SingleOrDefault(item => item.Id == reviewed.Id);
                if (current is null || !IsPending(current))
                    return UiOperationResult.Failure("登录请求已处理或过期。");
                if (current.PublicKey != reviewed.PublicKey
                    || current.FingerprintPhrase != reviewed.FingerprintPhrase
                    || current.CreatedAt != reviewed.CreatedAt)
                    return UiOperationResult.Failure("登录请求已变化，请重新查看并核对指纹短语。");
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
                return UiOperationResult.Failure("无法批准登录请求，请检查连接并确认密码库已解锁。");
            }
        }
    }
}
