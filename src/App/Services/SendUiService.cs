using System.Net;
using System.Security.Cryptography;
using Api;
using App.ViewModels.Models;
using Core.Enums;
using Core.Models;
using Core.Services;
using Vault;
using AppSendType = App.ViewModels.Models.SendType;
using CoreSendType = Core.Enums.SendType;

namespace App.Services;

internal static class SendUiErrorMessages
{
    public const string DemoUnavailable = "演示保险库为本地离线数据，无法打开外部 Send 链接。";
    public const string InvalidLink = "Send 链接格式不正确，请检查链接后重试。";
    public const string UnsupportedAuthentication = "此 Send 使用当前版本尚不支持的验证方式，暂时无法打开。";
    public const string UnsupportedContent = "此 Send 的内容类型当前版本暂不支持。";
    public const string InvalidContent = "无法验证该 Send 的内容，请确认链接完整且未被修改。";
    public const string AccessDenied = "无法访问该 Send，请确认链接有效且未过期。";
    public const string SendUnavailable = "未找到该 Send，它可能已被删除或已过期。";
    public const string RateLimited = "请求过于频繁，请稍后再试。";
    public const string ServiceUnavailable = "Send 服务暂时不可用，请稍后再试。";
    public const string NetworkFailure = "无法连接 Send 服务，请检查网络和链接地址后重试。";
    public const string ReadFailure = "无法读取该 Send 的内容，请确认链接完整后重试。";
    public const string GenericFailure = "无法打开该 Send 链接，请检查链接是否完整，并稍后重试。";

    public static bool IsRecoverableAccessFailure(Exception exception) =>
        RequestTimeoutClassifier.IsTimeout(exception)
        || exception is FormatException
            or SendUnsupportedAuthenticationException
            or NotSupportedException
            or CryptographicException
            or InvalidDataException
            or UnauthorizedAccessException
            or HttpRequestException
            or IOException;

    public static string ForAccessFailure(Exception exception)
    {
        if (RequestTimeoutClassifier.IsTimeout(exception))
            return UiExceptionPolicy.RequestTimeoutMessage;

        if (exception is VaultApiException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Gone })
            return SendUnavailable;
        if (exception is VaultApiException { StatusCode: HttpStatusCode.Forbidden })
            return AccessDenied;
        if (exception is VaultApiException { StatusCode: HttpStatusCode.TooManyRequests })
            return RateLimited;
        if (exception is VaultApiException { StatusCode: { } statusCode }
            && (int)statusCode >= 500)
        {
            return ServiceUnavailable;
        }

        return exception switch
        {
            FormatException => InvalidLink,
            SendUnsupportedAuthenticationException => UnsupportedAuthentication,
            NotSupportedException => UnsupportedContent,
            CryptographicException or InvalidDataException => InvalidContent,
            UnauthorizedAccessException => AccessDenied,
            VaultApiException => GenericFailure,
            HttpRequestException => NetworkFailure,
            IOException => ReadFailure,
            _ => GenericFailure,
        };
    }

    public static string SanitizeAccessResultError(string? message) => message switch
    {
        DemoUnavailable => DemoUnavailable,
        UiExceptionPolicy.RequestTimeoutMessage => UiExceptionPolicy.RequestTimeoutMessage,
        InvalidLink => InvalidLink,
        UnsupportedAuthentication => UnsupportedAuthentication,
        UnsupportedContent => UnsupportedContent,
        InvalidContent => InvalidContent,
        AccessDenied => AccessDenied,
        SendUnavailable => SendUnavailable,
        RateLimited => RateLimited,
        ServiceUnavailable => ServiceUnavailable,
        NetworkFailure => NetworkFailure,
        ReadFailure => ReadFailure,
        GenericFailure => GenericFailure,
        _ => GenericFailure,
    };
}

// 真实 Send UI 服务:把 App 层 SendEditorDraft/SendListItem(SendType Text=0,File=1)
// 映射到 Core 领域(SendDraftModel + Core.Enums.SendType Text=0,File=1),并委派给
// ISendService / ISendWriteService / ISendAccessService。删除日期相对标签在此转绝对时间(<=31 天)。
//
// 演示/离线会话(VaultSession.IsDemo)特判:该会话未配置服务端地址,任何走网络的 Send 读写都会抛
// 「An invalid request URI was provided…」。故 IsDemo 时全部改走会话内存中的离线 Send 列表(读/建/改/删),
// 完全不触网,并展示演示数据。session 为 null(设计时/旧构造)时按在线路径处理。
public sealed class SendUiService : ISendUiService
{
    private readonly ISendService _read;
    private readonly ISendWriteService _write;
    private readonly ISendAccessService _access;
    private readonly VaultSession? _session;
    private readonly object _reconciliationGate = new();
    private readonly Dictionary<PendingKey, PendingCommit> _pendingCommits = [];
    private readonly Dictionary<PendingKey, PendingReplacement> _pendingReplacements = [];
    private readonly Dictionary<ReconciliationScope, long> _mutationEpochs = [];
    private readonly Dictionary<Guid, ActiveMutation> _activeMutations = [];
    private readonly Dictionary<PendingKey, Guid> _inFlightOwners = [];

    public SendUiService(ISendService read, ISendWriteService write, ISendAccessService access, VaultSession? session = null)
    {
        _read = read;
        _write = write;
        _access = access;
        _session = session;
    }

    private bool IsDemo => _session?.IsDemo == true;

    public async Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default)
    {
        if (IsDemo)
            return _session!.Sends.Select(send => ToListItem(send)).ToList();

        var context = CaptureMutationContext();
        long readEpoch;
        lock (_reconciliationGate)
            readEpoch = CurrentEpochNoLock(context.Scope);

        var snapshot = await _read.ObserveSendsAsync(ct);
        ValidateSnapshotContext(context, snapshot);
        return ApplySnapshotForContext(context, () =>
        {
            if (readEpoch != CurrentEpochNoLock(context.Scope)
                || _activeMutations.Values.Any(active => active.Scope == context.Scope))
                throw new SendSnapshotSupersededException();

            var items = snapshot.Observations
                .Where(observation => observation.DecryptStatus == SendDecryptStatus.Decrypted)
                .GroupBy(observation => observation.Id, StringComparer.Ordinal)
                .Select(group => ToListItem(group.First().Value!, snapshot.IsStale))
                .ToList();
            return ApplyReconciliationNoLock(context.Scope, snapshot, items);
        });
    }

    public async Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            var demoSend = _session!.Sends.FirstOrDefault(send => send.Id == id);
            return demoSend is null ? null : SendEditorDraft.FromExisting(demoSend);
        }

        var context = CaptureMutationContext();
        long readEpoch;
        lock (_reconciliationGate)
        {
            if (IsReconciliationPendingNoLock(context.Scope, id))
                return null;
            readEpoch = CurrentEpochNoLock(context.Scope);
        }

        var snapshot = await _read.ObserveSendsAsync(ct);
        ValidateSnapshotContext(context, snapshot);
        if (snapshot.IsStale)
            return null;
        return ApplySnapshotForContext(context, () =>
        {
            if (readEpoch != CurrentEpochNoLock(context.Scope)
                || _activeMutations.Values.Any(active => active.Scope == context.Scope))
                throw new SendSnapshotSupersededException();
            if (IsReconciliationPendingNoLock(context.Scope, id))
                return null;

            var observation = UniqueObservation(snapshot, id);
            return observation is { DecryptStatus: SendDecryptStatus.Decrypted, Value: { } send }
                ? SendEditorDraft.FromExisting(send)
                : null;
        });
    }

    public async Task<SendListItem> CreateSendAsync(SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            var created = BuildDemoSend(draft, file, id: Guid.NewGuid().ToString("N"), existing: null);
            _session!.UpsertSend(created);
            return ToListItem(created);
        }

        var context = CaptureMutationContextForWrite(draft.OriginatingSession);
        var expectedSession = RequireWriteSession(context);
        var model = ToDraftModel(draft, id: null);
        using var reservation = ReserveMutation(
            context,
            sendId: null,
            expectedKind: SendMutationKind.Create);
        SendMutationReceipt receipt;
        try
        {
            receipt = await WriteSendAsync(model, id: null, file, expectedSession, ct);
            CompleteReceipt(reservation, receipt, draft.Type);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
        return await ResolveCommittedItemAsync(receipt, draft.Type, ct);
    }

    public async Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default)
    {
        if (IsDemo)
        {
            var existing = _session!.Sends.FirstOrDefault(s => s.Id == id);
            var updated = BuildDemoSend(draft, file, id, existing);
            _session!.UpsertSend(updated);
            return ToListItem(updated);
        }

        var context = CaptureMutationContextForWrite(draft.OriginatingSession);
        var expectedSession = RequireWriteSession(context);
        var expectedKind = draft.Type == AppSendType.File && file is not null
            ? SendMutationKind.Replace
            : SendMutationKind.Update;
        using var reservation = ReserveMutation(context, id, expectedKind);

        var model = ToDraftModel(draft, id);
        try
        {
            var receipt = await WriteSendAsync(model, id, file, expectedSession, ct);
            CompleteReceipt(reservation, receipt, draft.Type);
            return await ResolveCommittedItemAsync(receipt, draft.Type, ct);
        }
        catch (SendReplacementUncertainException uncertain)
        {
            CompleteUncertainReplacement(reservation, uncertain.Replacement, draft.Type);
            return await TrackUncertainReplacementAsync(
                uncertain.ExistingSendId,
                uncertain.Replacement,
                draft.Type,
                ct);
        }
    }

    // 文件型 Send 的单一流契约边界:直接消费前层可重开 SendFileSource,不再在 App 做任何 byte[] 适配。
    //   创建(id==null)必须提供文件源 → CreateFileSendAsync(streaming);
    //   替换(id!=null 且重选了文件)→ ReplaceFileSendAsync(建+传新件再删旧件,streaming);
    //   仅改元数据(id!=null 且未重选文件,file==null)→ UpdateFileSendMetadataAsync 只发 PUT,绝不重传字节。
    // 绝不把 null 当零字节,也不保留任何 byte[] 上传重载。上限校验在选择文件时(StorageFileSendSource)已完成。
    private async Task<SendMutationReceipt> WriteSendAsync(
        SendDraftModel model,
        string? id,
        SendFileSource? file,
        Core.Session.VaultSessionLease expectedSession,
        CancellationToken ct)
    {
        if (model.Type != CoreSendType.File)
            return await _write.SaveTextSendAsync(model, expectedSession, ct);

        if (id is null)
        {
            if (file is null)
                throw new InvalidOperationException("创建文件 Send 必须选择文件。");
            return await _write.CreateFileSendAsync(model, file, expectedSession, ct);
        }
        if (file is null)
        {
            return await _write.UpdateFileSendMetadataAsync(model, expectedSession, ct);
        }

        return await _write.ReplaceFileSendAsync(id, model, file, expectedSession, ct);
    }

    public async Task DeleteSendAsync(
        string id,
        Core.Session.VaultSessionLease originatingSession,
        CancellationToken ct = default)
    {
        if (IsDemo)
        {
            _session!.RemoveSend(id);
            return;
        }

        var context = CaptureMutationContextForWrite(originatingSession);
        var expectedSession = RequireWriteSession(context);
        using var reservation = ReserveMutation(context, id, SendMutationKind.Delete);
        try
        {
            var receipt = await _write.DeleteSendAsync(id, expectedSession, ct);
            CompleteReceipt(reservation, receipt, AppSendType.Text);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }

        try
        {
            await GetSendsAsync(ct);
        }
        catch (Exception)
        {
            // The delete is committed; reconciliation remains as a tombstone.
        }
    }

    public string? CopyShareLink(SendListItem item) => item.Link;

    public async Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default)
    {
        // 演示/离线会话无法访问外部 Send 服务端:明确提示而非抛出相对 URI 异常。
        if (IsDemo)
            return SendReceivedResult.Failure(SendUiErrorMessages.DemoUnavailable);

        SendAccessResult accessed;
        try
        {
            accessed = await _access.AccessAsync(url, password, ct);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return SendReceivedResult.Wrong();
        }
        catch (VaultApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            return SendReceivedResult.Wrong();
        }
        catch (Exception ex) when (SendUiErrorMessages.IsRecoverableAccessFailure(ex))
        {
            return SendReceivedResult.Failure(SendUiErrorMessages.ForAccessFailure(ex));
        }

        var result = new SendReceivedResult(
            Ok: true,
            WrongPassword: false,
            Type: MapType(accessed.Type),
            Name: accessed.Name,
            TextContent: accessed.TextContent,
            FileName: accessed.FileName,
            Error: null,
            Accessed: accessed.Type == CoreSendType.File ? accessed : null);
        if (accessed.Type != CoreSendType.File)
            accessed.Dispose();
        return result;
    }

    private async Task<SendListItem> ResolveCommittedItemAsync(
        SendMutationReceipt receipt,
        AppSendType type,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receipt.SendId);
        var pending = PendingItem(
            receipt.SendId,
            type,
            SendReconciliationState.CommittedPendingRefresh);
        var scope = ScopeFromReceipt(receipt);
        try
        {
            if (!IsCurrentReconciliationScope(scope))
                return pending;
            var items = await GetSendsAsync(ct);
            if (!IsCurrentReconciliationScope(scope))
                return pending;
            return items.FirstOrDefault(item => item.Id == receipt.SendId) ?? pending;
        }
        catch (Exception)
        {
            return pending;
        }
    }

    private async Task<SendListItem> TrackUncertainReplacementAsync(
        string existingSendId,
        SendMutationReceipt replacement,
        AppSendType type,
        CancellationToken ct)
    {
        var pending = PendingItem(
            replacement.SendId,
            type,
            SendReconciliationState.ReplacementNewPending);
        var scope = ScopeFromReceipt(replacement);
        try
        {
            if (!IsCurrentReconciliationScope(scope))
                return pending;
            var items = await GetSendsAsync(ct);
            if (!IsCurrentReconciliationScope(scope))
                return pending;
            return items.FirstOrDefault(item => item.Id == replacement.SendId) ?? pending;
        }
        catch (Exception)
        {
            return pending;
        }
    }

    private IReadOnlyList<SendListItem> ApplyReconciliationNoLock(
        ReconciliationScope scope,
        SendReadSnapshot snapshot,
        List<SendListItem> items)
    {
        var stateChanged = false;
        foreach (var pending in _pendingCommits.Values
                     .Where(pending => pending.Scope == scope)
                     .ToArray())
        {
            if (ReceiptObserved(pending.Receipt, snapshot))
            {
                _pendingCommits.Remove(new PendingKey(scope, pending.Receipt.SendId));
                stateChanged = true;
                continue;
            }

            if (pending.Receipt.Kind == SendMutationKind.Delete)
            {
                items.RemoveAll(item => item.Id == pending.Receipt.SendId);
                continue;
            }

            if (pending.Receipt.Kind == SendMutationKind.Replace
                && !string.IsNullOrEmpty(pending.Receipt.ReplacedSendId))
            {
                items.RemoveAll(item => item.Id == pending.Receipt.ReplacedSendId);
            }
            MarkOrAddPending(
                items,
                pending.Receipt.SendId,
                pending.Type,
                SendReconciliationState.CommittedPendingRefresh);
        }

        foreach (var replacement in _pendingReplacements.Values
                     .Where(replacement => replacement.Scope == scope)
                     .ToArray())
        {
            var oldObserved = snapshot.Observations.Any(observation =>
                observation.Id == replacement.ExistingSendId);
            var newObservation = UniqueObservation(snapshot, replacement.Receipt.SendId);
            if (snapshot.IsComplete
                && !oldObserved
                && newObservation is { DecryptStatus: SendDecryptStatus.Decrypted })
            {
                _pendingReplacements.Remove(new PendingKey(scope, replacement.ExistingSendId));
                stateChanged = true;
                continue;
            }

            if (oldObserved)
            {
                MarkOrAddPending(
                    items,
                    replacement.ExistingSendId,
                    replacement.Type,
                    SendReconciliationState.ReplacementOriginalPending);
            }
            MarkOrAddPending(
                items,
                replacement.Receipt.SendId,
                replacement.Type,
                SendReconciliationState.ReplacementNewPending);
        }

        if (stateChanged)
            AdvanceEpochNoLock(scope);
        return items;
    }

    private static bool ReceiptObserved(SendMutationReceipt receipt, SendReadSnapshot snapshot)
    {
        var observation = UniqueObservation(snapshot, receipt.SendId);
        return receipt.Kind switch
        {
            SendMutationKind.Create =>
                observation is { DecryptStatus: SendDecryptStatus.Decrypted },
            SendMutationKind.Update =>
                observation is { DecryptStatus: SendDecryptStatus.Decrypted }
                && UpdateWitnessObserved(receipt, observation),
            SendMutationKind.Replace =>
                snapshot.IsComplete
                && observation is { DecryptStatus: SendDecryptStatus.Decrypted }
                && !snapshot.Observations.Any(item => item.Id == receipt.ReplacedSendId),
            SendMutationKind.Delete =>
                snapshot.IsComplete
                && !snapshot.Observations.Any(item => item.Id == receipt.SendId),
            _ => false,
        };
    }

    private static bool UpdateWitnessObserved(
        SendMutationReceipt receipt,
        SendObservation observation)
    {
        if (!string.IsNullOrEmpty(receipt.RawMutationWitness)
            && receipt.RawMutationWitness == observation.RawMutationWitness)
            return true;
        return receipt.RevisionDate is DateTimeOffset receiptRevision
            && observation.RevisionDate is DateTimeOffset observedRevision
            && observedRevision > receiptRevision;
    }

    private static SendObservation? UniqueObservation(SendReadSnapshot snapshot, string? sendId)
    {
        if (string.IsNullOrEmpty(sendId))
            return null;
        var matches = snapshot.Observations.Where(observation => observation.Id == sendId).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static void MarkOrAddPending(
        List<SendListItem> items,
        string sendId,
        AppSendType type,
        SendReconciliationState state)
    {
        var index = items.FindIndex(item => item.Id == sendId);
        if (index >= 0)
            items[index] = items[index] with { ReconciliationState = state };
        else
            items.Add(PendingItem(sendId, type, state));
    }

    private bool IsReconciliationPending(ReconciliationScope scope, string sendId)
    {
        lock (_reconciliationGate)
            return IsReconciliationPendingNoLock(scope, sendId);
    }

    private bool IsReconciliationPendingNoLock(ReconciliationScope scope, string sendId) =>
        _pendingCommits.Values.Any(pending =>
            pending.Scope == scope
            && (pending.Receipt.SendId == sendId || pending.Receipt.ReplacedSendId == sendId))
        || _pendingReplacements.Values.Any(replacement =>
            replacement.Scope == scope
            && (replacement.ExistingSendId == sendId
                || replacement.Receipt.SendId == sendId));

    private MutationContext CaptureMutationContext()
    {
        if (_session is null)
            return new MutationContext(ReconciliationScope.Unscoped, null);

        try
        {
            var lease = _session.CaptureLease();
            return new MutationContext(
                new ReconciliationScope(lease.AccountId, lease.Endpoints),
                lease);
        }
        catch (InvalidOperationException)
        {
            return new MutationContext(ReconciliationScope.Unscoped, null);
        }
    }

    private MutationContext CaptureMutationContextForWrite(
        Core.Session.VaultSessionLease? originatingSession)
    {
        if (_session is null)
            return new MutationContext(ReconciliationScope.Unscoped, originatingSession);
        if (originatingSession is null)
            throw new InvalidOperationException("A Send mutation requires its originating session lease.");
        return new MutationContext(
            new ReconciliationScope(originatingSession.AccountId, originatingSession.Endpoints),
            originatingSession);
    }

    private static Core.Session.VaultSessionLease RequireWriteSession(MutationContext context) =>
        context.Lease
        ?? throw new InvalidOperationException("A Send mutation requires an active session lease.");

    private static ReconciliationScope ScopeFromReceipt(SendMutationReceipt receipt) =>
        receipt.SessionLease is { } lease
            ? new ReconciliationScope(lease.AccountId, lease.Endpoints)
            : ReconciliationScope.Unscoped;

    private bool IsCurrentReconciliationScope(ReconciliationScope expected) =>
        CaptureMutationContext().Scope == expected;

    private static void ValidateSnapshotContext(
        MutationContext expected,
        SendReadSnapshot snapshot)
    {
        if (expected.Lease is null)
        {
            if (snapshot.SessionLease is not null)
                throw new SendSnapshotSupersededException();
            return;
        }
        if (!expected.Lease.SameSessionAs(snapshot.SessionLease))
            throw new SendSnapshotSupersededException();
    }

    private T ApplySnapshotForContext<T>(MutationContext context, Func<T> apply)
    {
        if (context.Lease is null)
        {
            lock (_reconciliationGate)
                return apply();
        }

        if (_session is null)
            throw new SendSnapshotSupersededException();

        T result = default!;
        var applied = _session.TryRunIfUnlocked(context.Lease, () =>
        {
            lock (_reconciliationGate)
                result = apply();
        });
        if (!applied)
            throw new SendSnapshotSupersededException();
        return result;
    }

    private MutationReservation ReserveMutation(
        MutationContext context,
        string? sendId,
        SendMutationKind expectedKind)
    {
        lock (_reconciliationGate)
        {
            PendingKey? target = null;
            if (!string.IsNullOrEmpty(sendId))
            {
                target = new PendingKey(context.Scope, sendId);
                if (IsReconciliationPendingNoLock(context.Scope, sendId))
                {
                    throw new InvalidOperationException(
                        "This Send is awaiting server reconciliation and cannot be modified yet.");
                }
                if (_inFlightOwners.ContainsKey(target.Value))
                    throw new SendMutationInProgressException(sendId);
            }

            var operationId = Guid.NewGuid();
            var reservation = new MutationReservation(this, operationId);
            _activeMutations.Add(
                operationId,
                new ActiveMutation(
                    operationId,
                    context.Scope,
                    context.Lease,
                    target,
                    expectedKind));
            if (target is PendingKey targetKey)
                _inFlightOwners.Add(targetKey, operationId);
            AdvanceEpochNoLock(context.Scope);
            return reservation;
        }
    }

    private void CompleteReceipt(
        MutationReservation reservation,
        SendMutationReceipt receipt,
        AppSendType type)
    {
        lock (_reconciliationGate)
        {
            var active = ValidateCompletionNoLock(reservation, receipt);
            if (!reservation.TryFinish())
                throw new InvalidOperationException("The Send mutation reservation is already complete.");
            _pendingCommits[new PendingKey(active.Scope, receipt.SendId)] =
                new PendingCommit(active.Scope, receipt, type);
            FinishActiveNoLock(active);
        }
    }

    private void CompleteUncertainReplacement(
        MutationReservation reservation,
        SendMutationReceipt receipt,
        AppSendType type)
    {
        lock (_reconciliationGate)
        {
            var active = ValidateCompletionNoLock(reservation, receipt);
            var existingSendId = receipt.ReplacedSendId
                ?? active.Target?.SendId
                ?? throw new InvalidOperationException("Replacement receipt is missing the previous Send id.");
            if (!reservation.TryFinish())
                throw new InvalidOperationException("The Send mutation reservation is already complete.");
            _pendingReplacements[new PendingKey(active.Scope, existingSendId)] =
                new PendingReplacement(active.Scope, existingSendId, receipt, type);
            FinishActiveNoLock(active);
        }
    }

    private ActiveMutation ValidateCompletionNoLock(
        MutationReservation reservation,
        SendMutationReceipt receipt)
    {
        if (!_activeMutations.TryGetValue(reservation.OperationId, out var active))
            throw new InvalidOperationException("The Send mutation reservation is no longer active.");
        if (receipt.Kind != active.ExpectedKind)
            throw new InvalidOperationException("The Send mutation receipt kind does not match its reservation.");
        if (active.Target is PendingKey target)
        {
            var targetMatches = receipt.Kind == SendMutationKind.Replace
                ? receipt.ReplacedSendId == target.SendId
                : receipt.SendId == target.SendId;
            if (!targetMatches)
                throw new InvalidOperationException("The Send mutation receipt target does not match its reservation.");
        }
        if (active.Lease is not null)
        {
            if (receipt.SessionLease is null || !active.Lease.SameSessionAs(receipt.SessionLease))
                throw new InvalidOperationException("The Send mutation receipt belongs to another session.");
        }
        else if (receipt.SessionLease is not null)
        {
            throw new InvalidOperationException("An unscoped mutation returned a scoped receipt.");
        }
        return active;
    }

    private void AbortMutation(MutationReservation reservation)
    {
        lock (_reconciliationGate)
        {
            if (!reservation.TryFinish()
                || !_activeMutations.TryGetValue(reservation.OperationId, out var active))
                return;
            FinishActiveNoLock(active);
        }
    }

    private void FinishActiveNoLock(ActiveMutation active)
    {
        _activeMutations.Remove(active.OperationId);
        if (active.Target is PendingKey target
            && _inFlightOwners.TryGetValue(target, out var owner)
            && owner == active.OperationId)
            _inFlightOwners.Remove(target);
        AdvanceEpochNoLock(active.Scope);
    }

    private long CurrentEpochNoLock(ReconciliationScope scope) =>
        _mutationEpochs.TryGetValue(scope, out var epoch) ? epoch : 0;

    private void AdvanceEpochNoLock(ReconciliationScope scope) =>
        _mutationEpochs[scope] = checked(CurrentEpochNoLock(scope) + 1);

    private static SendListItem PendingItem(
        string sendId,
        AppSendType type,
        SendReconciliationState state) =>
        new(sendId, "已提交的 Send", type, "待服务器确认", null, state);

    private SendListItem ToListItem(Send send, bool isReadOnly = false)
    {
        var deletion = send.DeletionDate.LocalDateTime.ToString("yyyy/M/d HH:mm");
        return new SendListItem(
            send.Id,
            send.Name,
            MapType(send.Type),
            $"{deletion} 截止",
            send.ShareUrl,
            IsReadOnly: isReadOnly);
    }

    private SendDraftModel ToDraftModel(SendEditorDraft draft, string? id) => new()
    {
        Id = id,
        Key = draft.WrappedKey,
        Type = MapType(draft.Type),
        Name = draft.Name.Trim(),
        Notes = NullIfBlank(draft.Notes),
        TextContent = draft.Type == AppSendType.Text ? draft.Text : null,
        TextHidden = draft.HideTextByDefault,
        FileName = draft.Type == AppSendType.File ? NullIfBlank(draft.FileName) : null,
        MaxAccessCount = draft.MaxAccessCount,
        ExpirationDate = draft.ExpirationDate,
        DeletionDate = draft.ToDeletionDate(),
        Disabled = draft.Disabled,
        HideEmail = draft.HideEmail,
        ExistingHasPassword = id is not null && draft.ExistingHasPassword,
        ExistingAuthType = id is null ? SendAuthType.None : draft.ExistingAuthType,
        ExistingRawAuthType = id is null ? (int)SendAuthType.None : draft.ExistingRawAuthType,
        PasswordChange = string.IsNullOrWhiteSpace(draft.Password)
            ? new SendPasswordChange.Keep()
            : new SendPasswordChange.Set(draft.Password),
    };

    private static AppSendType MapType(CoreSendType type) => type == CoreSendType.File ? AppSendType.File : AppSendType.Text;
    private static CoreSendType MapType(AppSendType type) => type == AppSendType.File ? CoreSendType.File : CoreSendType.Text;
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed record PendingCommit(
        ReconciliationScope Scope,
        SendMutationReceipt Receipt,
        AppSendType Type);

    private sealed record PendingReplacement(
        ReconciliationScope Scope,
        string ExistingSendId,
        SendMutationReceipt Receipt,
        AppSendType Type);

    private sealed record MutationContext(
        ReconciliationScope Scope,
        Core.Session.VaultSessionLease? Lease);

    private sealed record ActiveMutation(
        Guid OperationId,
        ReconciliationScope Scope,
        Core.Session.VaultSessionLease? Lease,
        PendingKey? Target,
        SendMutationKind ExpectedKind);

    private sealed class MutationReservation : IDisposable
    {
        private readonly SendUiService _owner;
        private int _finished;

        public MutationReservation(SendUiService owner, Guid operationId)
        {
            _owner = owner;
            OperationId = operationId;
        }

        public Guid OperationId { get; }

        public bool TryFinish() => Interlocked.Exchange(ref _finished, 1) == 0;

        public void Dispose() => _owner.AbortMutation(this);
    }

    private readonly record struct PendingKey(ReconciliationScope Scope, string SendId);

    private sealed record ReconciliationScope(string AccountId, ServerEndpoints? Endpoints)
    {
        public static ReconciliationScope Unscoped { get; } = new("unscoped", null);
    }

    // 演示/离线会话:直接把可编辑草稿映射成内存 Send 领域模型(不加密、不上传)。ShareUrl 为占位链接,
    // 仅供「复制链接」演示。文件 Send 若未重新选取文件(file==null)则复用既有 File 元数据
    // (Size/SizeName/FileId/FileName),避免 FromExisting 不带文件源导致的大小归零;
    // 重选文件时大小取 file.Length(绝不打开流读取字节);HasPassword 同理在草稿未填新密码时沿用既有值。
    private static Send BuildDemoSend(SendEditorDraft draft, SendFileSource? file, string id, Send? existing)
    {
        var type = MapType(draft.Type);
        SendFile? sendFile = null;
        if (type == CoreSendType.File)
        {
            if (file is null && existing?.File is not null)
            {
                sendFile = existing.File;
            }
            else
            {
                long fileSize = file?.Length ?? existing?.File?.Size ?? 0;
                sendFile = new SendFile(NullIfBlank(draft.FileName) ?? draft.Name.Trim(), fileSize, HumanSize(fileSize), id);
            }
        }

        return new Send
        {
            Id = id,
            AccessId = id,
            Type = type,
            Name = draft.Name.Trim(),
            Notes = NullIfBlank(draft.Notes),
            Text = type == CoreSendType.Text ? new SendText(draft.Text, draft.HideTextByDefault) : null,
            File = sendFile,
            MaxAccessCount = draft.MaxAccessCount,
            ExpirationDate = draft.ExpirationDate,
            DeletionDate = draft.ToDeletionDate(),
            Disabled = draft.Disabled,
            HideEmail = draft.HideEmail,
            AuthType = !string.IsNullOrWhiteSpace(draft.Password)
                ? SendAuthType.Password
                : existing?.AuthType ?? SendAuthType.None,
            RawAuthType = !string.IsNullOrWhiteSpace(draft.Password)
                ? (int)SendAuthType.Password
                : existing?.RawAuthType ?? (int)SendAuthType.None,
            HasPassword = !string.IsNullOrWhiteSpace(draft.Password) || (existing?.HasPassword ?? false),
            ShareUrl = $"https://demo.local/#/send/{id}/demoAccessKey",
        };
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
