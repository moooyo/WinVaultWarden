using System.Security.Cryptography;
using System.Text.Json;
using Core.Enums;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Core.Session;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

// Task 7: the browser passkey handler captures the request's lease exactly once at entry and
// re-validates it after every await. When Lock / account-switch / abort invalidates that lease
// across an approval or save boundary, a late approval or a token-ignoring write must never sign,
// save, commit to another account, or return success — the request fails as operation_aborted
// (code-only), and the ORIGINAL lease is the only one ever handed to the writer (A can never write B).
public class BrowserPasskeyCancellationTests
{
    private const string RpId = "github.com";
    private const string FrameOrigin = "https://github.com";

    [Fact]
    public async Task LockDuringApproval_DiscardsLateApprovalAndDoesNotSave()
    {
        var session = UnlockedSession("account-a", "https://a.example");
        var approval = new BlockingApprovalService(ignoreCancellation: true);
        var writer = new CapturingWriter();
        var handler = CreateHandler(session, approval, writer);

        var operation = handler.HandleAsync(
            RequestForCredential("00000000000000000000000000000021"),
            CancellationToken.None);

        await approval.Entered.Task;
        session.Lock();
        approval.Complete(new PasskeyUseApprovalResult(
            new PasskeyApprovalEvidence(true, true, true),
            "cipher-a",
            "credential-a"));

        var response = await operation;

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.OperationAborted, response.Error!.Code);
        Assert.Equal(0, writer.SaveCalls);
        // Code-only: no internal text ever crosses to the page.
        Assert.Null(response.Error.Message);
    }

    [Fact]
    public async Task AccountSwitchDuringApproval_CannotWriteAToB()
    {
        var session = UnlockedSession("account-a", "https://a.example");
        var approval = new BlockingApprovalService(ignoreCancellation: true);
        var writer = new CapturingWriter();
        var handler = CreateHandler(session, approval, writer);

        var operation = handler.HandleAsync(
            RequestForCredential("00000000000000000000000000000022"),
            CancellationToken.None);

        await approval.Entered.Task;
        session.BeginAccountSession(
            "account-b",
            "b@example.com",
            new ServerOrigin("https://b.example"));
        approval.Complete(ApprovedUse("cipher-a", "credential-a"));

        var response = await operation;

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.OperationAborted, response.Error!.Code);
        Assert.Empty(writer.Leases);
    }

    [Fact]
    public async Task LockDuringSave_TokenIgnoringWriteDoesNotReturnSuccess_UsesOriginalLease()
    {
        var session = UnlockedSession("account-a", "https://a.example");
        var lease = session.CaptureLease();
        var approval = new ImmediateApprovalService(ApprovedCreateNew());
        // Writer blocks inside SaveCipherAsync and, once released, ignores the token and "succeeds".
        var writer = new CapturingWriter(blockUntilReleased: true);
        var handler = CreateHandler(session, approval, writer);

        var operation = handler.HandleAsync(
            RequestForCreate("00000000000000000000000000000024"),
            CancellationToken.None);

        // Approval already completed; the write is now in flight. Lock mid-save, then let the write
        // return success while ignoring cancellation.
        await writer.Entered.Task;
        session.Lock();
        writer.Release();

        var response = await operation;

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.OperationAborted, response.Error!.Code);
        // The writer only ever saw the request's ORIGINAL lease (same generation/account/origin as
        // the lease captured at request start), never a new account's lease.
        var seen = Assert.Single(writer.Leases);
        Assert.Equal(lease.Generation, seen.Generation);
        Assert.Equal(lease.AccountId, seen.AccountId);
        Assert.Equal(lease.Origin, seen.Origin);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static BrowserPasskeyRequestHandler CreateHandler(
        VaultSession session,
        IPasskeyApprovalService approval,
        IVaultWriteService writer) =>
        new(new VaultService(session), writer, approval);

    private static PasskeyUseApprovalResult ApprovedUse(string cipherId, string credentialId) =>
        new(new PasskeyApprovalEvidence(true, true, true), cipherId, credentialId);

    private static PasskeyCreateApprovalResult ApprovedCreateNew() =>
        new(new PasskeyApprovalEvidence(true, true, true), CreateNew: true, CipherId: null);

    private static BrowserPasskeyRequest RequestForCredential(string requestId)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "origin": "{{FrameOrigin}}",
              "rpId": "{{RpId}}",
              "challenge": "AQIDBA",
              "allowCredentials": [],
              "userVerification": "preferred",
              "timeout": 60000
            }
            """);
        return new BrowserPasskeyRequest(
            requestId, "passkey.get", doc.RootElement.Clone(), Context: Ctx());
    }

    private static BrowserPasskeyRequest RequestForCreate(string requestId)
    {
        using var doc = JsonDocument.Parse(
            $$"""
            {
              "origin": "{{FrameOrigin}}", "rpId": "{{RpId}}", "challenge": "AQIDBA",
              "rp": { "id": "{{RpId}}", "name": "GitHub" },
              "user": { "id": "dXNlcklk", "name": "octo", "displayName": "Octo Cat" },
              "pubKeyCredParams": [{ "type": "public-key", "alg": -7 }],
              "excludeCredentials": [],
              "authenticatorSelection": { "userVerification": "preferred", "residentKey": "required" },
              "attestation": "none",
              "timeout": 60000
            }
            """);
        return new BrowserPasskeyRequest(
            requestId, "passkey.create", doc.RootElement.Clone(), Context: Ctx());
    }

    private static BrowserRequestContext Ctx() =>
        new(FrameOrigin, FrameOrigin, 1, 0, "document-1", true);

    private static VaultSession UnlockedSession(string account, string serverOrigin)
    {
        var session = new VaultSession();
        var origin = new ServerOrigin(serverOrigin);
        var lease = session.BeginAccountSession(account, $"{account}@example.com", origin);
        var key = new SymmetricCryptoKey(new byte[64]);
        session.TryCommit(lease, () =>
        {
            session.SetUnlockedKey(key);
            session.SetSnapshot(new DecryptedVault(
                new AccountInfo(account, $"{account}@example.com", origin, "A", string.Empty),
                [],
                [CredentialCipher("cipher-a", "credential-a", "octo@example.com")],
                0,
                0));
        });
        return session;
    }

    private static Cipher CredentialCipher(string cipherId, string credentialId, string userName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credential = new CipherFido2Credential(
            credentialId,
            "public-key",
            "ECDSA",
            "P-256",
            WebAuthnCrypto.EncodeBase64Url(key.ExportPkcs8PrivateKey()),
            RpId,
            WebAuthnCrypto.EncodeBase64Url([5, 6, 7, 8]),
            userName,
            0,
            "GitHub",
            userName,
            true,
            DateTimeOffset.Parse("2026-06-24T00:00:00Z"));
        return new Cipher
        {
            Id = cipherId,
            Type = CipherType.Login,
            Name = "GitHub",
            Login = new CipherLogin(userName, null, null, [])
            {
                Fido2Credentials = [credential],
            },
        };
    }

    // Blocks the approval until Complete(...) is called. With ignoreCancellation the await does NOT
    // observe the token, so the approval can resolve *after* Lock/switch already invalidated the
    // lease — proving the handler re-checks the lease on its own rather than relying on the approval
    // to notice cancellation.
    private sealed class BlockingApprovalService : IPasskeyApprovalService
    {
        private readonly bool _ignoreCancellation;
        private readonly TaskCompletionSource<PasskeyUseApprovalResult> _use =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<PasskeyCreateApprovalResult> _create =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingApprovalService(bool ignoreCancellation) =>
            _ignoreCancellation = ignoreCancellation;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PasskeyUseApprovalResult> ConfirmUseAsync(
            PasskeyApprovalRequest request, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            return _ignoreCancellation ? await _use.Task : await _use.Task.WaitAsync(ct);
        }

        public async Task<PasskeyCreateApprovalResult> ConfirmCreateAsync(
            PasskeyCreateApprovalRequest request, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            return _ignoreCancellation ? await _create.Task : await _create.Task.WaitAsync(ct);
        }

        public void Complete(PasskeyUseApprovalResult result) => _use.TrySetResult(result);
        public void Complete(PasskeyCreateApprovalResult result) => _create.TrySetResult(result);
    }

    private sealed class ImmediateApprovalService : IPasskeyApprovalService
    {
        private readonly PasskeyCreateApprovalResult _create;

        public ImmediateApprovalService(PasskeyCreateApprovalResult create) => _create = create;

        public Task<PasskeyUseApprovalResult> ConfirmUseAsync(
            PasskeyApprovalRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PasskeyUseApprovalResult(
                new PasskeyApprovalEvidence(true, true, true),
                request.Candidates.Count > 0 ? request.Candidates[0].CipherId : null,
                request.Candidates.Count > 0 ? request.Candidates[0].CredentialId : null));

        public Task<PasskeyCreateApprovalResult> ConfirmCreateAsync(
            PasskeyCreateApprovalRequest request, CancellationToken ct = default) =>
            Task.FromResult(_create);
    }

    // Records every lease handed to it and, optionally, blocks inside SaveCipherAsync while ignoring
    // the token — modelling a network write already in flight that "succeeds" after Lock.
    private sealed class CapturingWriter : IVaultWriteService
    {
        private readonly bool _block;
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CapturingWriter(bool blockUntilReleased = false) => _block = blockUntilReleased;

        public int SaveCalls { get; private set; }
        public List<VaultSessionLease> Leases { get; } = new();
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) =>
            throw new NotSupportedException("Browser handler must use the lease-bound overload.");

        public async Task SaveCipherAsync(Cipher cipher, VaultSessionLease lease, CancellationToken ct = default)
        {
            SaveCalls++;
            Leases.Add(lease);
            Entered.TrySetResult();
            if (_block)
                await _release.Task; // deliberately ignore ct: an in-flight write cannot be cancelled.
        }

        public Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
