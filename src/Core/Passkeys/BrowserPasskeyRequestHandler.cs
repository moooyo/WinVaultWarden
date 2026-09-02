using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using System.Security.Cryptography;
using System.Text.Json;

namespace Core.Passkeys;

public sealed class BrowserPasskeyRequestHandler
{
    private readonly IVaultService _vault;
    private readonly IVaultWriteService _write;
    private readonly IPasskeyApprovalService _approval;
    private readonly WebAuthnRequestPolicy _policy;
    private readonly PasskeyRequestGate _requestGate;

    public BrowserPasskeyRequestHandler(
        IVaultService vault,
        IVaultWriteService write,
        IPasskeyApprovalService approval,
        WebAuthnRequestPolicy? policy = null,
        PasskeyRequestGate? requestGate = null)
    {
        _vault = vault;
        _write = write;
        _approval = approval;
        // Task 1 origin/RP policy and Task 4 per-(tab,frame,RP) ceremony gate. Defaults preserve the
        // long-standing 3-arg constructor (and callers/tests that use it); production injects shared
        // singletons via DI so the gate's in-flight/rate-limit state is process-wide.
        _policy = policy ?? WebAuthnRequestPolicy.CreateDefault();
        _requestGate = requestGate ?? new PasskeyRequestGate(TimeProvider.System);
    }

    public async Task<BrowserPasskeyResponse> HandleAsync(
        BrowserPasskeyRequest request,
        CancellationToken ct = default)
    {
        if (!PasskeyRequestParser.TryParse(request, out var parsed, out var parseError))
            return Error(request.Id, parseError!);

        // 尚未由 App 接管:vault 未解锁 → client_not_handled,交平台 authenticator 兜底,绝不把内部
        // 锁定状态暴露给页面。此检查在捕获 lease 之前完成。
        if (_vault.Snapshot.State != VaultState.Unlocked)
            return Error(request.Id, PasskeyErrorCodes.ClientNotHandled);

        // 入口只捕获一次 lease,并把它的取消令牌与外部 ct 链接:此后所有候选读取/审批/签名/保存
        // 都以这唯一 lease 校验,任何跨 await 期间的 Lock/切换账户都会使 lease 失效并稳定失败。
        VaultSessionLease lease;
        try
        {
            lease = _vault.CaptureLease();
        }
        catch (InvalidOperationException)
        {
            // 捕获 lease 的瞬间刚好无活跃会话(与上面的解锁检查间发生竞态):仍属"未接管"。
            return Error(request.Id, PasskeyErrorCodes.ClientNotHandled);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, lease.CancellationToken);
        try
        {
            return parsed!.Type switch
            {
                "passkey.get" => await HandleGetAsync(
                    lease,
                    request.Id,
                    request.Context,
                    (PasskeyGetPayload)parsed.Payload,
                    linked.Token),
                "passkey.create" => await HandleCreateAsync(
                    lease,
                    request.Id,
                    request.Context,
                    (PasskeyCreatePayload)parsed.Payload,
                    linked.Token),
                _ => Error(request.Id, "unsupported_passkey_type"),
            };
        }
        catch (OperationCanceledException)
        {
            // 已开始处理后的任何取消 —— Lock/Logout/切换账户使 lease 失效,或外部 Abort/pagehide/
            // navigation/native·pipe 断线/deadline —— 一律映射为 operation_aborted(code-only)。
            // deadline 自身的 deadline_exceeded 由原生宿主在其触发点保留;若此响应对应的请求已被取消,
            // 上层(PasskeyBridgeServer/native session)会丢弃它,不会有迟到帧到达页面。
            return Error(request.Id, PasskeyErrorCodes.OperationAborted);
        }
        catch (Exception)
        {
            // 其余未知异常:code-only internal_error。绝不外泄任何异常文本(Message/栈/路径/令牌)。
            // Core 层无日志设施,故此处不落诊断;App 边界(PasskeyBridgeServer)按需记脱敏诊断。
            return Error(request.Id, PasskeyErrorCodes.InternalError);
        }
    }

    private async Task<BrowserPasskeyResponse> HandleCreateAsync(
        VaultSessionLease lease,
        string? requestId,
        BrowserRequestContext? context,
        PasskeyCreatePayload payload,
        CancellationToken ct)
    {
        // Task 5: origin/RP/cross-origin come only from the trusted sender context, never the page
        // payload. A missing context means an unauthenticated sender bypassed the native host.
        if (context is null)
            return Error(requestId, PasskeyErrorCodes.SecuritySender);

        // Task 1: derive the canonical RP and normalised origins from the trusted context.
        var decision = _policy.Evaluate(BrowserPasskeyOperation.Create, context, payload.Rp?.Id ?? payload.RpId);
        if (decision.Context is not { } validated)
            return Error(requestId, decision.ErrorCode!);
        var rpId = validated.CanonicalRpId;

        var algorithms = (payload.PubKeyCredParams ?? []).Select(p => p.Alg).ToArray();

        // 完全不支持所请求的算法(客户端仅支持 ES256 / -7)→ 未接管,交平台 authenticator 兜底,
        // 且不消耗仪式槽/限流预算。这是"初始映射"的一部分,和 locked / 无候选一样折叠为 client_not_handled。
        if (!algorithms.Contains(-7))
            return Error(requestId, PasskeyErrorCodes.ClientNotHandled);

        // lease-bound 快照:只读取本次请求 lease 对应会话的条目。
        var ciphers = _vault.GetCiphers(lease);

        // excludeCredentials:若本库已有该 rpId 的凭据且其 CredentialId 在排除列表 → InvalidStateError。
        var excluded = (payload.ExcludeCredentials ?? [])
            .Select(credential => CredentialId.DecodeWire(credential.Id))
            .ToArray();
        if (excluded.Length > 0
            && ExistingCredentialsForRp(ciphers, rpId).Any(credential =>
                CredentialId.TryDecodeStorage(credential.CredentialId, out var storedId, out _)
                && excluded.Any(excludedId => CredentialId.FixedTimeEquals(storedId, excludedId))))
        {
            return Error(requestId, "credential_excluded");
        }

        var existingLogins = ciphers
            .Where(c => !c.IsDeleted && !c.IsArchived && c.Type == CipherType.Login)
            .Select(c => new PasskeyLoginTarget(c.Id, c.Name, c.Reprompt))
            .ToArray();

        // Task 4: reserve the single ceremony slot for this (tab, frame, canonical RP) before prompting.
        using var gate = _requestGate.TryEnter(
            new PasskeyRequestKey(context.TabId, context.FrameId, rpId),
            out var gateError);
        if (gate is null)
            return Error(requestId, gateError!);

        var userVerificationRequirement =
            GetUserVerificationRequirementForCreate(payload.AuthenticatorSelection);
        var approval = await _approval.ConfirmCreateAsync(
            new PasskeyCreateApprovalRequest(
                requestId ?? string.Empty,
                ToApprovalSource(validated),
                payload.Rp?.Name,
                payload.User?.Name,
                payload.User?.DisplayName,
                existingLogins,
                userVerificationRequirement),
            ct);
        _vault.ThrowIfExpired(lease);

        // An attach target must be one of the offered existing logins; a forged/absent CipherId
        // is rejected before any credential is minted or saved.
        if (approval.Evidence.Approved
            && !approval.CreateNew
            && !existingLogins.Any(login => login.CipherId == approval.CipherId))
        {
            return Error(requestId, PasskeyErrorCodes.InternalError);
        }

        if (ValidateApproval(
                requestId,
                userVerificationRequirement,
                approval.Evidence) is { } approvalError)
        {
            return approvalError;
        }

        try
        {
            var result = WebAuthnRegistrationService.CreateAttestation(new WebAuthnMakeCredentialRequest(
                validated.FrameOrigin.Value,
                payload.Challenge,
                rpId,
                payload.Rp?.Name,
                payload.User!.Id,
                payload.User.Name,
                payload.User.DisplayName,
                algorithms,
                ApprovalEvidence: approval.Evidence,
                CredProps: HasCredProps(payload.Extensions)));
            _vault.ThrowIfExpired(lease);

            var cipher = approval.CreateNew
                ? BuildNewLoginCipher(payload.Rp?.Name ?? rpId, result.Credential)
                : AttachToExisting(ciphers, approval.CipherId!, result.Credential);
            if (cipher is null)
                return Error(requestId, "cipher_not_found");

            try
            {
                await _write.SaveCipherAsync(cipher, lease, ct);
            }
            catch (VaultRefreshException)
            {
                // The credential was committed remotely. Continue only while the request's
                // original lease is still current; a lock or account switch must still abort.
            }
            ct.ThrowIfCancellationRequested();
            _vault.ThrowIfExpired(lease);

            return new BrowserPasskeyResponse(requestId, "passkey.create", true,
                JsonSerializer.SerializeToElement(result.Payload, PasskeyJsonContext.Default.PasskeyCreateAttestationPayload));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or CryptographicException)
        {
            return Error(requestId, "credential_unusable");
        }
    }

    private async Task<BrowserPasskeyResponse> HandleGetAsync(
        VaultSessionLease lease,
        string? requestId,
        BrowserRequestContext? context,
        PasskeyGetPayload payload,
        CancellationToken ct)
    {
        // Task 5: origin/RP/cross-origin come only from the trusted sender context, never the page.
        if (context is null)
            return Error(requestId, PasskeyErrorCodes.SecuritySender);

        // Task 1: canonical RP + normalised origins + verified cross-origin from the trusted context.
        var decision = _policy.Evaluate(BrowserPasskeyOperation.Get, context, payload.RpId);
        if (decision.Context is not { } validated)
            return Error(requestId, decision.ErrorCode!);

        var allowedCredentialIds = (payload.AllowCredentials ?? [])
            .Select(credential => credential.Id)
            .ToArray();
        var allowed = allowedCredentialIds.Length == 0
            ? null
            : allowedCredentialIds.Select(CredentialId.DecodeWire).ToArray();

        // Every matching discoverable credential is offered to the user; no FirstOrDefault default.
        var candidates = FindCredentials(lease, validated.CanonicalRpId, allowed);
        // No candidate before any prompt → let the platform authenticator try (fallback), and do
        // not consume a ceremony slot / rate-limit budget for a request we can't service.
        if (candidates.Count == 0)
            return Error(requestId, PasskeyErrorCodes.ClientNotHandled);

        // Task 4: reserve the single ceremony slot for this (tab, frame, canonical RP).
        using var gate = _requestGate.TryEnter(
            new PasskeyRequestKey(context.TabId, context.FrameId, validated.CanonicalRpId),
            out var gateError);
        if (gate is null)
            return Error(requestId, gateError!);

        var userVerificationRequirement =
            ParseUserVerificationRequirement(payload.UserVerification);
        var approval = await _approval.ConfirmUseAsync(
            new PasskeyApprovalRequest(
                requestId ?? string.Empty,
                ToApprovalSource(validated),
                [.. candidates.Select(ToApprovalTarget)],
                userVerificationRequirement),
            ct);
        _vault.ThrowIfExpired(lease);

        // The approved (cipher, credential) pair must be exactly one of the offered candidates:
        // an approval that names an id we never presented is treated as a forged/mismatched
        // selection and rejected before signing.
        var selected = candidates.SingleOrDefault(candidate =>
            candidate.Cipher.Id == approval.CipherId
            && CredentialId.TryDecodeStorage(approval.CredentialId, out var approvedId, out _)
            && CredentialId.FixedTimeEquals(candidate.CredentialIdBytes, approvedId));
        if (approval.Evidence.Approved && selected is null)
            return Error(requestId, PasskeyErrorCodes.InternalError);

        if (ValidateApproval(
                requestId,
                userVerificationRequirement,
                approval.Evidence) is { } approvalError)
        {
            return approvalError;
        }

        try
        {
            var assertion = WebAuthnAssertionService.CreateAssertion(
                selected!.Credential,
                new WebAuthnGetAssertionRequest(
                    validated.FrameOrigin.Value,
                    payload.Challenge,
                    validated.CanonicalRpId,
                    allowedCredentialIds,
                    ApprovalEvidence: approval.Evidence,
                    CrossOrigin: validated.IsCrossOrigin));
            _vault.ThrowIfExpired(lease);

            return new BrowserPasskeyResponse(
                requestId,
                "passkey.get",
                true,
                JsonSerializer.SerializeToElement(
                    new PasskeyGetAssertionPayload(
                        assertion.CredentialId,
                        assertion.AuthenticatorData,
                        assertion.ClientDataJson,
                        assertion.Signature,
                        assertion.UserHandle),
                    PasskeyJsonContext.Default.PasskeyGetAssertionPayload));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or CryptographicException)
        {
            return Error(requestId, "credential_unusable");
        }
    }

    // 返回本次 lease-bound 快照中匹配该 canonical rpId(及 allowCredentials,若有)的全部 discoverable
    // 凭据,按名称/账户稳定排序,供 App 明确列出多账号。rpId 用 Ordinal 精确匹配 canonical A-label。
    private IReadOnlyList<PasskeyCredentialCandidate> FindCredentials(
        VaultSessionLease lease,
        string rpId,
        IReadOnlyList<byte[]>? allowed)
    {
        var candidates = new List<PasskeyCredentialCandidate>();
        foreach (var cipher in _vault.GetCiphers(lease))
        {
            if (cipher.IsDeleted
                || cipher.IsArchived
                || cipher.Type != CipherType.Login
                || cipher.Login?.HasFido2Credentials != true)
            {
                continue;
            }

            foreach (var credential in cipher.Login.Fido2Credentials)
            {
                if (!string.Equals(credential.RpId, rpId, StringComparison.Ordinal)
                    || !CredentialId.TryDecodeStorage(
                        credential.CredentialId,
                        out var credentialIdBytes,
                        out _)
                    || (allowed is not null
                        && !allowed.Any(allowedId =>
                            CredentialId.FixedTimeEquals(credentialIdBytes, allowedId))))
                {
                    continue;
                }

                candidates.Add(new PasskeyCredentialCandidate(
                    cipher,
                    credential,
                    credentialIdBytes));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Cipher.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Credential.UserName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.Credential.CredentialId, StringComparer.Ordinal)
            .ToArray();
    }

    private static PasskeyApprovalSource ToApprovalSource(ValidatedBrowserRequestContext context) =>
        new(
            context.FrameOrigin.Value,
            context.TopOrigin.Value,
            context.CanonicalRpId,
            context.UnicodeRpId,
            context.IsCrossOrigin);

    private static PasskeyCredentialTarget ToApprovalTarget(PasskeyCredentialCandidate candidate) =>
        new(
            candidate.Cipher.Id,
            candidate.Credential.CredentialId!,
            candidate.Cipher.Name,
            candidate.Credential.UserName,
            candidate.Credential.UserDisplayName,
            candidate.Cipher.Reprompt);

    // 返回给定 lease-bound 快照中属于该 rpId 的全部 Fido2 凭据(用于 excludeCredentials 排重判断)。
    private static IEnumerable<CipherFido2Credential> ExistingCredentialsForRp(
        IReadOnlyList<Cipher> ciphers,
        string rpId) =>
        ciphers
            .Where(cipher => !cipher.IsDeleted
                && !cipher.IsArchived
                && cipher.Type == CipherType.Login
                && cipher.Login?.HasFido2Credentials == true)
            .SelectMany(cipher => cipher.Login!.Fido2Credentials)
            .Where(credential => string.Equals(credential.RpId, rpId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(credential.CredentialId));

    // 新建一个仅含该 passkey 凭据的 Login 条目;其余字段留空,交由用户后续在库中补全。
    private static Cipher BuildNewLoginCipher(string name, CipherFido2Credential credential) => new()
    {
        Type = CipherType.Login,
        Name = name,
        Login = new CipherLogin(credential.UserName, null, null, [])
        {
            Fido2Credentials = [credential],
        },
    };

    // 将新凭据附加到既有 Login 条目(基于同一 lease-bound 快照);找不到条目或条目不是 Login 时返回 null。
    private static Cipher? AttachToExisting(
        IReadOnlyList<Cipher> ciphers,
        string cipherId,
        CipherFido2Credential credential)
    {
        var existing = ciphers.FirstOrDefault(c => !c.IsDeleted && c.Id == cipherId);
        if (existing?.Login is null)
            return null;

        return new Cipher
        {
            Id = existing.Id,
            Type = existing.Type,
            OrganizationId = existing.OrganizationId,
            CollectionIds = existing.CollectionIds,
            FolderId = existing.FolderId,
            Key = existing.Key,
            UsesBlobEncryption = existing.UsesBlobEncryption,
            Favorite = existing.Favorite,
            Reprompt = existing.Reprompt,
            Name = existing.Name,
            Notes = existing.Notes,
            CreationDate = existing.CreationDate,
            RevisionDate = existing.RevisionDate,
            DeletedDate = existing.DeletedDate,
            ArchivedDate = existing.ArchivedDate,
            CanEdit = existing.CanEdit,
            CanViewPassword = existing.CanViewPassword,
            CanDelete = existing.CanDelete,
            CanRestore = existing.CanRestore,
            Login = existing.Login with
            {
                Fido2Credentials =
                [
                    .. existing.Login.Fido2Credentials.Select(NormalizeCredentialIdForWrite),
                    credential,
                ],
            },
            Card = existing.Card,
            Identity = existing.Identity,
            SecureNote = existing.SecureNote,
            Ssh = existing.Ssh,
            BankAccount = existing.BankAccount,
            DriversLicense = existing.DriversLicense,
            Passport = existing.Passport,
            Fields = existing.Fields,
            Attachments = existing.Attachments,
            PasswordHistory = existing.PasswordHistory,
        };
    }

    private static CipherFido2Credential NormalizeCredentialIdForWrite(
        CipherFido2Credential credential) =>
        CredentialId.TryNormalizeStorage(credential.CredentialId, out var normalized)
            ? credential with { CredentialId = normalized }
            : credential;

    private static PasskeyUserVerificationRequirement
        GetUserVerificationRequirementForCreate(JsonElement? authenticatorSelection)
    {
        if (authenticatorSelection is not { } element
            || element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("userVerification", out var uv)
            || uv.ValueKind != JsonValueKind.String)
        {
            return PasskeyUserVerificationRequirement.Preferred;
        }

        return ParseUserVerificationRequirement(uv.GetString());
    }

    private static PasskeyUserVerificationRequirement
        ParseUserVerificationRequirement(string? userVerification) =>
        userVerification?.Trim().ToLowerInvariant() switch
        {
            "required" => PasskeyUserVerificationRequirement.Required,
            "discouraged" => PasskeyUserVerificationRequirement.Discouraged,
            _ => PasskeyUserVerificationRequirement.Preferred,
        };

    private static BrowserPasskeyResponse? ValidateApproval(
        string? requestId,
        PasskeyUserVerificationRequirement requirement,
        PasskeyApprovalEvidence evidence)
    {
        if (!evidence.Approved)
            return Error(requestId, "user_cancelled");

        if (evidence.UserVerified && !evidence.UserPresent)
        {
            return Error(requestId, "user_verification_failed");
        }

        if (requirement == PasskeyUserVerificationRequirement.Required
            && !evidence.UserVerified)
        {
            return Error(requestId, "user_verification_failed");
        }

        return null;
    }

    // extensions.credProps 存在即代表浏览器请求了 credProps 扩展结果。
    private static bool HasCredProps(JsonElement? extensions) =>
        extensions is { } element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty("credProps", out _);

    private static BrowserPasskeyResponse Error(string? id, BrowserPasskeyError error) =>
        Error(id, error.Code);

    // Task 7: 每个 browser-facing 失败都是 code-only。此 helper **没有** message 参数 ——
    // 不是"调用点内联记录意图后丢弃",而是这个参数根本不存在,future 无法误把
    // ex.Message/栈/路径/令牌 回填进来(防未来回填泄漏,browser-task-7 remediation)。
    private static BrowserPasskeyResponse Error(string? id, string code) =>
        new(id, "error", false, Error: new BrowserPasskeyError(code, Message: null!));

    private sealed record PasskeyCredentialCandidate(
        Cipher Cipher,
        CipherFido2Credential Credential,
        byte[] CredentialIdBytes);
}
