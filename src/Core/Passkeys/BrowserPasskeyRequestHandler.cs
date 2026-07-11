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

        // 入口只捕获一次 lease,并把它的取消令牌与外部 ct 链接:此后所有候选读取/审批/签名/保存
        // 都以这唯一 lease 校验,任何跨 await 期间的 Lock/切换账户都会使 lease 失效并稳定失败。
        VaultSessionLease lease;
        try
        {
            lease = _vault.CaptureLease();
        }
        catch (InvalidOperationException)
        {
            return Error(
                request.Id,
                "vault_locked",
                "Unlock WinVaultWarden before using passkeys.");
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
                _ => Error(
                    request.Id,
                    "unsupported_passkey_type",
                    "Unsupported passkey request type."),
            };
        }
        catch (OperationCanceledException) when (
            lease.CancellationToken.IsCancellationRequested
            && !ct.IsCancellationRequested)
        {
            return Error(
                request.Id,
                "vault_locked",
                "The vault session expired before the passkey operation completed.");
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
            return Error(requestId, PasskeyErrorCodes.SecuritySender, "A trusted request context is required.");

        // Task 1: derive the canonical RP and normalised origins from the trusted context.
        var decision = _policy.Evaluate(BrowserPasskeyOperation.Create, context, payload.Rp?.Id ?? payload.RpId);
        if (decision.Context is not { } validated)
            return Error(requestId, decision.ErrorCode!, "The passkey request failed origin validation.");
        var rpId = validated.CanonicalRpId;

        var algorithms = (payload.PubKeyCredParams ?? []).Select(p => p.Alg).ToArray();

        // lease-bound 快照:只读取本次请求 lease 对应会话的条目。
        var ciphers = _vault.GetCiphers(lease);

        // excludeCredentials:若本库已有该 rpId 的凭据且其 CredentialId 在排除列表 → InvalidStateError。
        var excluded = (payload.ExcludeCredentials ?? []).Select(c => c.Id).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.Ordinal);
        if (excluded.Count > 0 && ExistingCredentialsForRp(ciphers, rpId).Any(c => excluded.Contains(c.CredentialId!)))
            return Error(requestId, "credential_excluded", "A passkey already exists for this site.");

        var existingLogins = ciphers
            .Where(c => !c.IsDeleted && c.Type == CipherType.Login)
            .Select(c => new PasskeyLoginTarget(c.Id, c.Name, c.Reprompt))
            .ToArray();

        // Task 4: reserve the single ceremony slot for this (tab, frame, canonical RP) before prompting.
        using var gate = _requestGate.TryEnter(
            new PasskeyRequestKey(context.TabId, context.FrameId, rpId),
            out var gateError);
        if (gate is null)
            return Error(requestId, gateError!, "A passkey request is already in progress.");

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
            return Error(requestId, PasskeyErrorCodes.InternalError, "The approved item was not offered.");
        }

        if (ValidateApproval(
                requestId,
                userVerificationRequirement,
                approval.Evidence,
                "The passkey creation was cancelled.") is { } approvalError)
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
                return Error(requestId, "cipher_not_found", "The selected item was not found.");

            await _write.SaveCipherAsync(cipher, lease, ct);
            _vault.ThrowIfExpired(lease);

            return new BrowserPasskeyResponse(requestId, "passkey.create", true,
                JsonSerializer.SerializeToElement(result.Payload, PasskeyJsonContext.Default.PasskeyCreateAttestationPayload));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException or CryptographicException)
        {
            return Error(requestId, "credential_unusable", ex.Message);
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
            return Error(requestId, PasskeyErrorCodes.SecuritySender, "A trusted request context is required.");

        // Task 1: canonical RP + normalised origins + verified cross-origin from the trusted context.
        var decision = _policy.Evaluate(BrowserPasskeyOperation.Get, context, payload.RpId);
        if (decision.Context is not { } validated)
            return Error(requestId, decision.ErrorCode!, "The passkey request failed origin validation.");

        var allowedCredentialIds = (payload.AllowCredentials ?? [])
            .Select(credential => credential.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        var allowed = allowedCredentialIds.Length == 0
            ? null
            : allowedCredentialIds.ToHashSet(StringComparer.Ordinal);

        // Every matching discoverable credential is offered to the user; no FirstOrDefault default.
        var candidates = FindCredentials(lease, validated.CanonicalRpId, allowed);
        // No candidate before any prompt → let the platform authenticator try (fallback), and do
        // not consume a ceremony slot / rate-limit budget for a request we can't service.
        if (candidates.Count == 0)
            return Error(requestId, PasskeyErrorCodes.ClientNotHandled, "No matching passkey was found for this site.");

        // Task 4: reserve the single ceremony slot for this (tab, frame, canonical RP).
        using var gate = _requestGate.TryEnter(
            new PasskeyRequestKey(context.TabId, context.FrameId, validated.CanonicalRpId),
            out var gateError);
        if (gate is null)
            return Error(requestId, gateError!, "A passkey request is already in progress.");

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
            && candidate.Credential.CredentialId == approval.CredentialId);
        if (approval.Evidence.Approved && selected is null)
            return Error(requestId, PasskeyErrorCodes.InternalError, "The approved credential was not offered.");

        if (ValidateApproval(
                requestId,
                userVerificationRequirement,
                approval.Evidence,
                "The passkey request was cancelled.") is { } approvalError)
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
            return Error(requestId, "credential_unusable", ex.Message);
        }
    }

    // 返回本次 lease-bound 快照中匹配该 canonical rpId(及 allowCredentials,若有)的全部 discoverable
    // 凭据,按名称/账户稳定排序,供 App 明确列出多账号。rpId 用 Ordinal 精确匹配 canonical A-label。
    private IReadOnlyList<PasskeyCredentialCandidate> FindCredentials(
        VaultSessionLease lease,
        string rpId,
        IReadOnlySet<string>? allowed)
    {
        return _vault.GetCiphers(lease)
            .Where(cipher => !cipher.IsDeleted && cipher.Type == CipherType.Login && cipher.Login?.HasFido2Credentials == true)
            .SelectMany(cipher => cipher.Login!.Fido2Credentials.Select(
                credential => new PasskeyCredentialCandidate(cipher, credential)))
            .Where(candidate =>
                string.Equals(candidate.Credential.RpId, rpId, StringComparison.Ordinal)
                && candidate.Credential.CredentialId is { Length: > 0 } id
                && (allowed is null || allowed.Contains(id)))
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
            .Where(cipher => !cipher.IsDeleted && cipher.Type == CipherType.Login && cipher.Login?.HasFido2Credentials == true)
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
            FolderId = existing.FolderId,
            Key = existing.Key,
            Favorite = existing.Favorite,
            Reprompt = existing.Reprompt,
            Name = existing.Name,
            Notes = existing.Notes,
            CreationDate = existing.CreationDate,
            RevisionDate = existing.RevisionDate,
            DeletedDate = existing.DeletedDate,
            Login = existing.Login with
            {
                Fido2Credentials = [.. existing.Login.Fido2Credentials, credential],
            },
            Card = existing.Card,
            Identity = existing.Identity,
            SecureNote = existing.SecureNote,
            Ssh = existing.Ssh,
            Fields = existing.Fields,
            Attachments = existing.Attachments,
            PasswordHistory = existing.PasswordHistory,
        };
    }

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
        PasskeyApprovalEvidence evidence,
        string cancellationMessage)
    {
        if (!evidence.Approved)
            return Error(requestId, "user_cancelled", cancellationMessage);

        if (evidence.UserVerified && !evidence.UserPresent)
        {
            return Error(
                requestId,
                "user_verification_failed",
                "User verification evidence requires user presence.");
        }

        if (requirement == PasskeyUserVerificationRequirement.Required
            && !evidence.UserVerified)
        {
            return Error(
                requestId,
                "user_verification_failed",
                "User verification is required for this passkey request.");
        }

        return null;
    }

    // extensions.credProps 存在即代表浏览器请求了 credProps 扩展结果。
    private static bool HasCredProps(JsonElement? extensions) =>
        extensions is { } element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty("credProps", out _);

    private static BrowserPasskeyResponse Error(string? id, BrowserPasskeyError error) =>
        Error(id, error.Code, error.Message);

    private static BrowserPasskeyResponse Error(string? id, string code, string message) =>
        new(id, "error", false, Error: new BrowserPasskeyError(code, message));

    private sealed record PasskeyCredentialCandidate(Cipher Cipher, CipherFido2Credential Credential);
}
