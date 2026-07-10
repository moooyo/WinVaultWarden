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

    public BrowserPasskeyRequestHandler(IVaultService vault, IVaultWriteService write, IPasskeyApprovalService approval)
    {
        _vault = vault;
        _write = write;
        _approval = approval;
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
                    (PasskeyGetPayload)parsed.Payload,
                    linked.Token),
                "passkey.create" => await HandleCreateAsync(
                    lease,
                    request.Id,
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
        PasskeyCreatePayload payload,
        CancellationToken ct)
    {
        if (!TryGetEffectiveRpId(payload.Origin, payload.Rp?.Id ?? payload.RpId, out var rpId, out var rpError))
            return Error(requestId, rpError!);

        var algorithms = (payload.PubKeyCredParams ?? []).Select(p => p.Alg).ToArray();

        // lease-bound 快照:只读取本次请求 lease 对应会话的条目。
        var ciphers = _vault.GetCiphers(lease);

        // excludeCredentials:若本库已有该 rpId 的凭据且其 CredentialId 在排除列表 → InvalidStateError。
        var excluded = (payload.ExcludeCredentials ?? []).Select(c => c.Id).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.Ordinal);
        if (excluded.Count > 0 && ExistingCredentialsForRp(ciphers, rpId!).Any(c => excluded.Contains(c.CredentialId!)))
            return Error(requestId, "credential_excluded", "A passkey already exists for this site.");

        var existingLogins = ciphers
            .Where(c => !c.IsDeleted && c.Type == CipherType.Login)
            .Select(c => new PasskeyLoginTarget(c.Id, c.Name))
            .ToArray();

        var userVerificationRequirement =
            GetUserVerificationRequirementForCreate(payload.AuthenticatorSelection);
        var approval = await _approval.ConfirmCreateAsync(
            new PasskeyCreateApprovalRequest(
                requestId ?? string.Empty,
                payload.Origin,
                rpId!,
                payload.Rp?.Name,
                payload.User?.Name,
                payload.User?.DisplayName,
                existingLogins,
                userVerificationRequirement),
            ct);
        _vault.ThrowIfExpired(lease);

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
                payload.Origin,
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
                ? BuildNewLoginCipher(payload.Rp?.Name ?? rpId!, result.Credential)
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
        PasskeyGetPayload payload,
        CancellationToken ct)
    {
        if (!TryGetEffectiveRpId(payload.Origin, payload.RpId, out var rpId, out var rpError))
            return Error(requestId, rpError!);

        var ciphers = _vault.GetCiphers(lease);
        var allowedCredentialIds = (payload.AllowCredentials ?? [])
            .Select(credential => credential.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        var credential = FindCredential(ciphers, rpId!, allowedCredentialIds);
        if (credential is null)
            return Error(requestId, "no_credential", "No matching passkey was found for this site.");

        var userVerificationRequirement =
            ParseUserVerificationRequirement(payload.UserVerification);
        var approval = await _approval.ConfirmUseAsync(
            new PasskeyApprovalRequest(
                requestId ?? string.Empty,
                payload.Origin,
                rpId!,
                credential.Cipher.Id,
                credential.Credential.CredentialId!,
                credential.Cipher.Name,
                credential.Credential.UserName,
                credential.Credential.UserDisplayName,
                userVerificationRequirement),
            ct);
        _vault.ThrowIfExpired(lease);

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
                credential.Credential,
                new WebAuthnGetAssertionRequest(
                    payload.Origin,
                    payload.Challenge,
                    rpId,
                    allowedCredentialIds,
                    ApprovalEvidence: approval.Evidence,
                    CrossOrigin: payload.CrossOrigin));
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

    private static PasskeyCredentialCandidate? FindCredential(
        IReadOnlyList<Cipher> ciphers,
        string rpId,
        IReadOnlyList<string> allowedCredentialIds)
    {
        var allowed = allowedCredentialIds.Count == 0
            ? null
            : allowedCredentialIds.ToHashSet(StringComparer.Ordinal);

        return ciphers
            .Where(cipher => !cipher.IsDeleted && cipher.Type == CipherType.Login && cipher.Login?.HasFido2Credentials == true)
            .SelectMany(cipher => cipher.Login!.Fido2Credentials.Select(credential => new PasskeyCredentialCandidate(cipher, credential)))
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Credential.RpId, rpId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(candidate.Credential.CredentialId)
                && (allowed is null || allowed.Contains(candidate.Credential.CredentialId!)));
    }

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

    private static bool TryGetEffectiveRpId(
        string origin,
        string? requestedRpId,
        out string? rpId,
        out BrowserPasskeyError? error)
    {
        rpId = null;
        error = null;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || string.IsNullOrWhiteSpace(originUri.Host))
        {
            error = new BrowserPasskeyError("invalid_request", "Passkey request origin must be an absolute origin.");
            return false;
        }

        rpId = string.IsNullOrWhiteSpace(requestedRpId) ? originUri.Host : requestedRpId;
        if (!string.Equals(originUri.Host, rpId, StringComparison.OrdinalIgnoreCase)
            && !originUri.Host.EndsWith($".{rpId}", StringComparison.OrdinalIgnoreCase))
        {
            error = new BrowserPasskeyError("invalid_request", "The requested rpId is not valid for the origin.");
            rpId = null;
            return false;
        }

        return true;
    }

    private static BrowserPasskeyResponse Error(string? id, BrowserPasskeyError error) =>
        Error(id, error.Code, error.Message);

    private static BrowserPasskeyResponse Error(string? id, string code, string message) =>
        new(id, "error", false, Error: new BrowserPasskeyError(code, message));

    private sealed record PasskeyCredentialCandidate(Cipher Cipher, CipherFido2Credential Credential);
}
