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
    private readonly IPasskeyApprovalService _approval;

    public BrowserPasskeyRequestHandler(IVaultService vault, IPasskeyApprovalService approval)
    {
        _vault = vault;
        _approval = approval;
    }

    public async Task<BrowserPasskeyResponse> HandleAsync(
        BrowserPasskeyRequest request,
        CancellationToken ct = default)
    {
        if (!PasskeyRequestParser.TryParse(request, out var parsed, out var parseError))
            return Error(request.Id, parseError!);

        return parsed!.Type switch
        {
            "passkey.get" => await HandleGetAsync(request.Id, (PasskeyGetPayload)parsed.Payload, ct),
            "passkey.create" => Error(request.Id, "not_implemented", "Passkey creation is not implemented yet."),
            _ => Error(request.Id, "unsupported_passkey_type", "Unsupported passkey request type."),
        };
    }

    private async Task<BrowserPasskeyResponse> HandleGetAsync(
        string? requestId,
        PasskeyGetPayload payload,
        CancellationToken ct)
    {
        if (_vault.Snapshot.State != VaultState.Unlocked)
            return Error(requestId, "vault_locked", "Unlock WinVaultWarden before using passkeys.");

        if (!TryGetEffectiveRpId(payload.Origin, payload.RpId, out var rpId, out var rpError))
            return Error(requestId, rpError!);

        var allowedCredentialIds = (payload.AllowCredentials ?? [])
            .Select(credential => credential.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToArray();
        var credential = FindCredential(rpId!, allowedCredentialIds);
        if (credential is null)
            return Error(requestId, "no_credential_or_cancelled", "The passkey request could not be completed.");

        var approved = await _approval.ConfirmUseAsync(new PasskeyApprovalRequest(
            requestId ?? string.Empty,
            payload.Origin,
            rpId!,
            credential.Cipher.Id,
            credential.Cipher.Name,
            credential.Credential.UserName,
            credential.Credential.UserDisplayName), ct);

        if (!approved)
            return Error(requestId, "no_credential_or_cancelled", "The passkey request could not be completed.");

        try
        {
            var assertion = WebAuthnAssertionService.CreateAssertion(
                credential.Credential,
                new WebAuthnGetAssertionRequest(
                    payload.Origin,
                    payload.Challenge,
                    rpId,
                    allowedCredentialIds,
                    UserVerified: RequiresUserVerification(payload.UserVerification)));

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

    private PasskeyCredentialCandidate? FindCredential(string rpId, IReadOnlyList<string> allowedCredentialIds)
    {
        var allowed = allowedCredentialIds.Count == 0
            ? null
            : allowedCredentialIds.ToHashSet(StringComparer.Ordinal);

        return _vault.GetCiphers()
            .Where(cipher => !cipher.IsDeleted && cipher.Type == CipherType.Login && cipher.Login?.HasFido2Credentials == true)
            .SelectMany(cipher => cipher.Login!.Fido2Credentials.Select(credential => new PasskeyCredentialCandidate(cipher, credential)))
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Credential.RpId, rpId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(candidate.Credential.CredentialId)
                && (allowed is null || allowed.Contains(candidate.Credential.CredentialId!)));
    }

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

    private static bool RequiresUserVerification(string? userVerification) =>
        string.Equals(userVerification, "required", StringComparison.OrdinalIgnoreCase)
        || string.Equals(userVerification, "preferred", StringComparison.OrdinalIgnoreCase);

    private static BrowserPasskeyResponse Error(string? id, BrowserPasskeyError error) =>
        Error(id, error.Code, error.Message);

    private static BrowserPasskeyResponse Error(string? id, string code, string message) =>
        new(id, "error", false, Error: new BrowserPasskeyError(code, message));

    private sealed record PasskeyCredentialCandidate(Cipher Cipher, CipherFido2Credential Credential);
}
