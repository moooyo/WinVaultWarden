using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Core.Enums;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Core.Session;

namespace Core.WindowsPasskeys;

/// <summary>
/// A lease-bound CTAP authenticator. The platform adapter authenticates signed requests
/// before dispatch; this service supplies explicit user approval and vault operations.
/// </summary>
public sealed class WindowsPasskeyAuthenticator(
    IVaultService vault,
    IVaultWriteService write,
    IWindowsPasskeyApprovalService approval,
    ISyncService? sync = null)
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public bool IsLocked => vault.Snapshot.State != VaultState.Unlocked;
    public VaultSessionLease CaptureLease() => vault.CaptureLease();
    public bool IsCurrent(VaultSessionLease lease) => vault.IsCurrent(lease);

    public async Task<byte[]> HandleAsync(WindowsPasskeyRequest request, CancellationToken ct) =>
        (await ExecuteAsync(request, ct).ConfigureAwait(false)).EncodedResponse;

    public async Task<WindowsPasskeyResult> ExecuteAsync(WindowsPasskeyRequest request, CancellationToken ct)
    {
        ValidateRequest(request);
        if (!await _operationGate.WaitAsync(0, ct).ConfigureAwait(false))
            throw new WindowsPasskeyException(WindowsPasskeyException.Busy, "Another operation is in progress.");
        VaultSessionLease? lease = null;
        try
        {
            if (IsLocked && (!await approval.EnsureUnlockedAsync(ct).ConfigureAwait(false) || IsLocked))
                throw new WindowsPasskeyException(WindowsPasskeyException.UserCancelled, "The vault is locked.");
            lease = vault.CaptureLease();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            var response = request.Operation switch
            {
                WindowsPasskeyOperation.MakeCredential =>
                    await MakeCredentialAsync(request, lease, linked.Token).ConfigureAwait(false),
                WindowsPasskeyOperation.GetAssertion =>
                    await GetAssertionAsync(request, lease, linked.Token).ConfigureAwait(false),
                _ => throw InvalidRequest(),
            };
            vault.ThrowIfExpired(lease);
            return new WindowsPasskeyResult(response, lease);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || lease?.CancellationToken.IsCancellationRequested == true)
        {
            throw new WindowsPasskeyException(WindowsPasskeyException.UserCancelled, "The passkey operation was cancelled.");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public IReadOnlyList<WindowsPasskeyCredentialMetadata> GetCredentialMetadata()
    {
        if (IsLocked)
            return [];
        var lease = vault.CaptureLease();
        var metadata = new List<WindowsPasskeyCredentialMetadata>();
        foreach (var cipher in vault.GetCiphers(lease))
        {
            if (cipher.IsDeleted || cipher.IsArchived || !cipher.CanViewPassword || cipher.Login is null)
                continue;
            foreach (var credential in cipher.Login.Fido2Credentials)
            {
                if (!TryUsableCredential(credential, out var credentialId, out var userId))
                    continue;
                metadata.Add(new WindowsPasskeyCredentialMetadata(credentialId, credential.RpId!,
                    credential.RpName ?? credential.RpId!, userId, credential.UserName, credential.UserDisplayName));
            }
        }
        vault.ThrowIfExpired(lease);
        return metadata;
    }

    private async Task<byte[]> MakeCredentialAsync(
        WindowsPasskeyRequest request, VaultSessionLease lease, CancellationToken ct)
    {
        if (request.UserId is not { Length: > 0 and <= 64 }
            || request.Algorithms?.Contains(-7) != true)
            throw new WindowsPasskeyException(WindowsPasskeyException.NotSupported, "ES256 and a user ID are required.");
        var ciphers = vault.GetCiphers(lease);
        if (FindCredentials(ciphers, request.RpId, request.CredentialIds).Count > 0
            && request.CredentialIds.Count > 0)
            throw new WindowsPasskeyException(WindowsPasskeyException.AlreadyExists, "An excluded credential exists.");
        var approved = await approval.ConfirmCreateAsync(ApprovalRequest(request, []), ct).ConfigureAwait(false);
        CheckApproval(approved, request.RequireUserVerification);
        ct.ThrowIfCancellationRequested();
        vault.ThrowIfExpired(lease);
        if (request.CredentialIds.Count > 0
            && FindCredentials(vault.GetCiphers(lease), request.RpId, request.CredentialIds).Count > 0)
            throw new WindowsPasskeyException(WindowsPasskeyException.AlreadyExists, "An excluded credential appeared during approval.");

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = ecdsa.ExportPkcs8PrivateKey();
        try
        {
            var credentialId = Guid.NewGuid().ToString("D");
            var wireId = CredentialId.DecodeStorage(credentialId);
            var credential = new CipherFido2Credential(
                CredentialId: credentialId, KeyType: "public-key", KeyAlgorithm: "ECDSA",
                KeyCurve: "P-256", KeyValue: WebAuthnCrypto.EncodeBase64Url(privateKey),
                RpId: request.RpId, UserHandle: WebAuthnCrypto.EncodeBase64Url(request.UserId),
                UserName: request.UserName, Counter: 0, RpName: request.RpName,
                UserDisplayName: request.UserDisplayName, Discoverable: true, CreationDate: DateTimeOffset.UtcNow);
            var parameters = ecdsa.ExportParameters(false);
            var coseKey = EncodePublicKey(parameters);
            var authData = WebAuthnCrypto.BuildAttestedAuthenticatorData(request.RpId, 0,
                approved.Evidence, Convert.FromHexString(WindowsPasskeyIdentity.Aaguid.ToString("N")),
                wireId, coseKey, PasskeyBackupState.Synced);
            var cipher = new Cipher
            {
                Name = string.IsNullOrWhiteSpace(request.RpName) ? request.RpId : request.RpName,
                Type = CipherType.Login,
                Login = new CipherLogin(request.UserName, null, null, []) { Fido2Credentials = [credential] },
            };
            try
            {
                await write.SaveCipherAsync(cipher, lease, ct).ConfigureAwait(false);
            }
            catch (VaultRefreshException)
            {
                // A committed credential remains valid when only the local refresh failed.
            }
            ct.ThrowIfCancellationRequested();
            vault.ThrowIfExpired(lease);
            return WindowsPasskeyCtapResponse.MakeCredential(authData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private async Task<byte[]> GetAssertionAsync(
        WindowsPasskeyRequest request, VaultSessionLease lease, CancellationToken ct)
    {
        var candidates = FindCredentials(vault.GetCiphers(lease), request.RpId, request.CredentialIds);
        if (candidates.Any(candidate => candidate.Credential.Counter.GetValueOrDefault() > 0))
        {
            if (sync is null)
                throw new WindowsPasskeyException(WindowsPasskeyException.NotSupported, "A credential counter requires online synchronization.");
            await sync.SyncAsync(lease, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            vault.ThrowIfExpired(lease);
            candidates = FindCredentials(vault.GetCiphers(lease), request.RpId, request.CredentialIds);
        }
        if (candidates.Count == 0)
            throw new WindowsPasskeyException(WindowsPasskeyException.NotFound, "No matching credential exists.");
        var approved = await approval.ConfirmUseAsync(ApprovalRequest(request,
            candidates.Select(candidate => new PasskeyCredentialTarget(candidate.Cipher.Id,
                candidate.Credential.CredentialId!, candidate.Cipher.Name, candidate.Credential.UserName,
                candidate.Credential.UserDisplayName, candidate.Cipher.Reprompt)).ToArray()), ct).ConfigureAwait(false);
        CheckApproval(approved, request.RequireUserVerification);
        ct.ThrowIfCancellationRequested();
        vault.ThrowIfExpired(lease);
        var selected = candidates.SingleOrDefault(candidate =>
            candidate.Cipher.Id == approved.CipherId
            && string.Equals(candidate.Credential.CredentialId, approved.CredentialId, StringComparison.Ordinal));
        if (selected is null)
            throw new WindowsPasskeyException(WindowsPasskeyException.UserCancelled, "The selected credential is not valid.");
        var current = FindCredentials(vault.GetCiphers(lease), request.RpId, request.CredentialIds)
            .SingleOrDefault(candidate => candidate.Cipher.Id == selected.Cipher.Id
                && candidate.Credential.CredentialId == selected.Credential.CredentialId);
        if (current is null
            || current.Credential.KeyValue != selected.Credential.KeyValue
            || current.Credential.UserHandle != selected.Credential.UserHandle
            || current.Credential.UserName != selected.Credential.UserName
            || current.Credential.UserDisplayName != selected.Credential.UserDisplayName
            || current.Cipher.Name != selected.Cipher.Name
            || current.Cipher.Reprompt != selected.Cipher.Reprompt)
            throw new WindowsPasskeyException(WindowsPasskeyException.UserCancelled, "The selected credential changed during approval.");
        selected = current;
        var credential = selected.Credential;
        if (selected.Cipher.Reprompt && !approved.Evidence.UserVerified)
            throw new WindowsPasskeyException(WindowsPasskeyException.UserCancelled, "Item verification is required.");
        var counter = credential.Counter.GetValueOrDefault();
        if (counter < 0 || counter >= uint.MaxValue || (counter > 0 && !selected.Cipher.CanEdit))
            throw new WindowsPasskeyException(WindowsPasskeyException.NotSupported, "This credential counter cannot be safely advanced.");
        if (counter > 0)
        {
            counter++;
            var updated = WithCredentialCounter(selected.Cipher, credential, counter);
            try { await write.SaveCipherAsync(updated, lease, ct).ConfigureAwait(false); }
            catch (VaultRefreshException) { }
            ct.ThrowIfCancellationRequested();
            vault.ThrowIfExpired(lease);
            var afterWrite = FindCredentials(vault.GetCiphers(lease), request.RpId, request.CredentialIds)
                .SingleOrDefault(candidate => candidate.Cipher.Id == selected.Cipher.Id
                    && candidate.Credential.CredentialId == credential.CredentialId);
            if (afterWrite is null || afterWrite.Credential.KeyValue != credential.KeyValue
                || afterWrite.Credential.UserHandle != credential.UserHandle
                || afterWrite.Cipher.Reprompt != selected.Cipher.Reprompt)
                throw new WindowsPasskeyException(WindowsPasskeyException.UserCancelled, "The credential changed while its counter was saved.");
        }
        var authData = WebAuthnCrypto.BuildAuthenticatorData(request.RpId, (uint)counter, approved.Evidence,
            PasskeyBackupState.ForStoredCredential(credential.CredentialId!));
        var privateKey = WebAuthnCrypto.DecodeBase64Url(credential.KeyValue!);
        var signedData = new byte[authData.Length + request.ClientDataHash.Length];
        try
        {
            authData.CopyTo(signedData, 0);
            request.ClientDataHash.CopyTo(signedData, authData.Length);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(privateKey, out var consumed);
            if (consumed != privateKey.Length || ecdsa.KeySize != 256
                || ecdsa.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw InvalidRequest();
            var signature = ecdsa.SignData(signedData, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            ct.ThrowIfCancellationRequested();
            vault.ThrowIfExpired(lease);
            return WindowsPasskeyCtapResponse.GetAssertion(selected.CredentialId, authData, signature, selected.UserId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
            CryptographicOperations.ZeroMemory(signedData);
        }
    }

    private static List<Candidate> FindCredentials(
        IReadOnlyList<Cipher> ciphers, string rpId, IReadOnlyList<byte[]> ids)
    {
        var result = new List<Candidate>();
        foreach (var cipher in ciphers)
        {
            if (cipher.IsDeleted || cipher.IsArchived || !cipher.CanViewPassword || cipher.Login is null)
                continue;
            foreach (var credential in cipher.Login.Fido2Credentials)
            {
                if (!string.Equals(credential.RpId, rpId, StringComparison.Ordinal)
                    || !TryUsableCredential(credential, out var credentialId, out var userId)
                    || (ids.Count == 0 && credential.Discoverable != true)
                    || (ids.Count > 0 && !ids.Any(id => CryptographicOperations.FixedTimeEquals(id, credentialId))))
                    continue;
                result.Add(new Candidate(cipher, credential, credentialId, userId));
            }
        }
        return result;
    }

    private static bool TryUsableCredential(CipherFido2Credential credential, out byte[] id, out byte[] userId)
    {
        id = [];
        userId = [];
        try
        {
            if (string.IsNullOrEmpty(credential.RpId) || string.IsNullOrEmpty(credential.KeyValue)
                || string.IsNullOrEmpty(credential.UserHandle)
                || !CredentialId.TryDecodeStorage(credential.CredentialId, out id, out _))
                return false;
            userId = WebAuthnCrypto.DecodeBase64Url(credential.UserHandle);
            return id.Length is > 0 and <= WindowsPasskeyIdentity.MaximumCredentialIdBytes
                && userId.Length is > 0 and <= 64
                && credential.KeyAlgorithm == "ECDSA" && credential.KeyCurve == "P-256";
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateRequest(WindowsPasskeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TransactionId == Guid.Empty || request.ClientDataHash is not { Length: 32 }
            || request.CredentialIds is null || request.CredentialIds.Count > WindowsPasskeyIdentity.MaximumCredentialCount
            || request.CredentialIds.Any(id => id is not { Length: > 0 and <= WindowsPasskeyIdentity.MaximumCredentialIdBytes })
            || request.RpId is null || request.RpId.Length is 0 or > 253 || request.RpId == WindowsPasskeyIdentity.PluginRpId)
            throw InvalidRequest();
        string ascii;
        try { ascii = new IdnMapping().GetAscii(request.RpId); }
        catch (ArgumentException) { throw InvalidRequest(); }
        if (ascii != request.RpId || ascii != ascii.ToLowerInvariant()
            || Uri.CheckHostName(ascii) == UriHostNameType.Unknown || ascii.Contains(':')
            || Encoding.UTF8.GetByteCount(ascii) > 253)
            throw InvalidRequest();
    }

    private static WindowsPasskeyApprovalRequest ApprovalRequest(
        WindowsPasskeyRequest request, IReadOnlyList<PasskeyCredentialTarget> candidates) =>
        new(request.TransactionId, request.RpId, request.RpName, request.UserName,
            request.UserDisplayName, request.RequireUserVerification, candidates);

    private static void CheckApproval(WindowsPasskeyApproval approval, bool requireUv)
    {
        if (!approval.Evidence.Approved || !approval.Evidence.UserPresent
            || (requireUv && !approval.Evidence.UserVerified))
            throw new WindowsPasskeyException(WindowsPasskeyException.UserCancelled, "User approval was not completed.");
    }

    private static byte[] EncodePublicKey(ECParameters parameters)
    {
        var writer = new System.Formats.Cbor.CborWriter(System.Formats.Cbor.CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(5);
        writer.WriteInt32(1); writer.WriteInt32(2);
        writer.WriteInt32(3); writer.WriteInt32(-7);
        writer.WriteInt32(-1); writer.WriteInt32(1);
        writer.WriteInt32(-2); writer.WriteByteString(parameters.Q.X!);
        writer.WriteInt32(-3); writer.WriteByteString(parameters.Q.Y!);
        writer.WriteEndMap();
        return writer.Encode();
    }

    private static Cipher WithCredentialCounter(Cipher source, CipherFido2Credential credential, long counter) => new()
    {
        Id = source.Id, Type = source.Type, OrganizationId = source.OrganizationId,
        CollectionIds = source.CollectionIds, FolderId = source.FolderId, Key = source.Key,
        UsesBlobEncryption = source.UsesBlobEncryption, Favorite = source.Favorite, Reprompt = source.Reprompt,
        Name = source.Name, Notes = source.Notes, CreationDate = source.CreationDate, RevisionDate = source.RevisionDate,
        DeletedDate = source.DeletedDate, ArchivedDate = source.ArchivedDate, CanEdit = source.CanEdit,
        CanViewPassword = source.CanViewPassword, CanDelete = source.CanDelete, CanRestore = source.CanRestore,
        Login = source.Login! with
        {
            Fido2Credentials = source.Login.Fido2Credentials.Select(item =>
                item.CredentialId == credential.CredentialId ? item with { Counter = counter } : item).ToArray(),
        },
        Card = source.Card, Identity = source.Identity, SecureNote = source.SecureNote, Ssh = source.Ssh,
        BankAccount = source.BankAccount, DriversLicense = source.DriversLicense, Passport = source.Passport,
        Fields = source.Fields, Attachments = source.Attachments, PasswordHistory = source.PasswordHistory,
    };

    private static WindowsPasskeyException InvalidRequest() =>
        new(WindowsPasskeyException.InvalidRequest, "The passkey request is invalid.");

    private sealed record Candidate(Cipher Cipher, CipherFido2Credential Credential, byte[] CredentialId, byte[] UserId);
}
