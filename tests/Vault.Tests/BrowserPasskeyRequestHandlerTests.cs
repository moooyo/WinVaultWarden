using System.Security.Cryptography;
using System.Text.Json;
using Core.Enums;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Core.Session;
using Xunit;

namespace Vault.Tests;

public class BrowserPasskeyRequestHandlerTests
{
    [Fact]
    public async Task HandleAsync_GetAssertion_SignsMatchingCredentialAfterApproval()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentialId = WebAuthnCrypto.EncodeBase64Url([1, 2, 3, 4]);
        var credential = Credential(key, credentialId);
        var cipher = new Cipher
        {
            Id = "cipher-1",
            Type = CipherType.Login,
            Name = "GitHub",
            Login = new CipherLogin("octo@example.com", null, null, [])
            {
                Fido2Credentials = [credential],
            },
        };
        var approval = new CapturingApprovalService(approve: true);
        var handler = new BrowserPasskeyRequestHandler(
            new FakeVaultService(VaultState.Unlocked, [cipher]),
            new FakeVaultWriteService(),
            approval);

        using var payload = JsonDocument.Parse($$"""
        {
          "origin": "https://github.com",
          "rpId": "github.com",
          "challenge": "AQIDBA",
          "allowCredentials": [{ "id": "{{credentialId}}", "type": "public-key", "transports": ["internal"] }],
          "userVerification": "required",
          "mediation": null,
          "timeout": 60000
        }
        """);

        var response = await handler.HandleAsync(
            new BrowserPasskeyRequest("req-1", "passkey.get", payload.RootElement),
            TestContext.Current.CancellationToken);

        Assert.True(response.Ok);
        Assert.Equal("passkey.get", response.Type);
        Assert.NotNull(response.Payload);
        var assertion = response.Payload.Value.Deserialize(PasskeyJsonContext.Default.PasskeyGetAssertionPayload)!;
        Assert.Equal(credentialId, assertion.CredentialId);
        Assert.False(string.IsNullOrWhiteSpace(assertion.AuthenticatorData));
        Assert.False(string.IsNullOrWhiteSpace(assertion.ClientDataJson));
        Assert.False(string.IsNullOrWhiteSpace(assertion.Signature));
        Assert.Equal("github.com", approval.Request?.RpId);
        Assert.Equal("GitHub", approval.Request?.CipherName);
    }

    [Fact]
    public async Task HandleAsync_GetAssertion_WhenVaultLocked_ReturnsVaultLocked()
    {
        var approval = new CapturingApprovalService(approve: true);
        var handler = new BrowserPasskeyRequestHandler(
            new FakeVaultService(VaultState.Locked, []),
            new FakeVaultWriteService(),
            approval);

        using var payload = JsonDocument.Parse(
            """
            {
              "origin": "https://github.com",
              "rpId": "github.com",
              "challenge": "AQIDBA",
              "allowCredentials": []
            }
            """);

        var response = await handler.HandleAsync(
            new BrowserPasskeyRequest("req-2", "passkey.get", payload.RootElement),
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal("vault_locked", response.Error?.Code);
        Assert.Null(approval.Request);
    }

    [Fact]
    public async Task HandleAsync_GetAssertion_WhenUserCancels_DoesNotSign()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credential = Credential(key, WebAuthnCrypto.EncodeBase64Url([1, 2, 3, 4]));
        var cipher = new Cipher
        {
            Id = "cipher-1",
            Type = CipherType.Login,
            Name = "GitHub",
            Login = new CipherLogin("octo@example.com", null, null, [])
            {
                Fido2Credentials = [credential],
            },
        };
        var handler = new BrowserPasskeyRequestHandler(
            new FakeVaultService(VaultState.Unlocked, [cipher]),
            new FakeVaultWriteService(),
            new CapturingApprovalService(approve: false));

        using var payload = JsonDocument.Parse(
            """
            {
              "origin": "https://github.com",
              "rpId": "github.com",
              "challenge": "AQIDBA",
              "allowCredentials": []
            }
            """);

        var response = await handler.HandleAsync(
            new BrowserPasskeyRequest("req-3", "passkey.get", payload.RootElement),
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal("user_cancelled", response.Error?.Code);
    }

    [Fact]
    public async Task HandleAsync_GetAssertion_WhenNoCredential_ReturnsGenericError()
    {
        var approval = new CapturingApprovalService(approve: true);
        var handler = new BrowserPasskeyRequestHandler(
            new FakeVaultService(VaultState.Unlocked, []),
            new FakeVaultWriteService(),
            approval);

        using var payload = JsonDocument.Parse(
            """
            {
              "origin": "https://github.com",
              "rpId": "github.com",
              "challenge": "AQIDBA",
              "allowCredentials": []
            }
            """);

        var response = await handler.HandleAsync(
            new BrowserPasskeyRequest("req-nc", "passkey.get", payload.RootElement),
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal("no_credential", response.Error?.Code);
        // The approval prompt must NOT have run for a missing credential.
        Assert.Null(approval.Request);
    }

    [Fact]
    public async Task HandleAsync_GetAssertion_PresentButDeclined_ReturnsUserCancelled()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credential = Credential(key, WebAuthnCrypto.EncodeBase64Url([1, 2, 3, 4]));
        var cipher = new Cipher
        {
            Id = "cipher-1",
            Type = CipherType.Login,
            Name = "GitHub",
            Login = new CipherLogin("octo@example.com", null, null, [])
            {
                Fido2Credentials = [credential],
            },
        };
        var handler = new BrowserPasskeyRequestHandler(
            new FakeVaultService(VaultState.Unlocked, [cipher]),
            new FakeVaultWriteService(),
            new CapturingApprovalService(approve: false));

        using var payload = JsonDocument.Parse(
            """
            {
              "origin": "https://github.com",
              "rpId": "github.com",
              "challenge": "AQIDBA",
              "allowCredentials": []
            }
            """);

        var response = await handler.HandleAsync(
            new BrowserPasskeyRequest("req-decl", "passkey.get", payload.RootElement),
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        // A present-but-declined credential reports user cancellation, distinct from the
        // no-credential case: no_credential falls back to the platform authenticator,
        // user_cancelled must surface as NotAllowedError (no fallback).
        Assert.Equal("user_cancelled", response.Error?.Code);
    }

    [Fact]
    public void ApprovalContract_RecordShapesExposeEvidenceAndSelections()
    {
        var evidence = new PasskeyApprovalEvidence(
            Approved: true,
            UserPresent: true,
            UserVerified: false);
        var create = new PasskeyCreateApprovalResult(
            evidence,
            CreateNew: true,
            CipherId: null);
        var use = new PasskeyUseApprovalResult(
            evidence,
            CipherId: "cipher-1",
            CredentialId: "credential-1");

        Assert.Equal(evidence, create.Evidence);
        Assert.True(create.CreateNew);
        Assert.Null(create.CipherId);
        Assert.Equal(evidence, use.Evidence);
        Assert.Equal("cipher-1", use.CipherId);
        Assert.Equal("credential-1", use.CredentialId);
        Assert.Equal(
            [
                PasskeyUserVerificationRequirement.Preferred,
                PasskeyUserVerificationRequirement.Required,
                PasskeyUserVerificationRequirement.Discouraged,
            ],
            Enum.GetValues<PasskeyUserVerificationRequirement>());
    }

    private static JsonDocument CreatePayload(
        string origin = "https://github.com",
        string rpId = "github.com",
        string exclude = "",
        string alg = "-7",
        string userVerification = "required") =>
        JsonDocument.Parse($$"""
        {
          "origin": "{{origin}}", "rpId": "{{rpId}}", "challenge": "AQIDBA",
          "rp": { "id": "{{rpId}}", "name": "GitHub" },
          "user": { "id": "dXNlcklk", "name": "octo", "displayName": "Octo Cat" },
          "pubKeyCredParams": [{ "type": "public-key", "alg": {{alg}} }],
          "excludeCredentials": [{{exclude}}],
          "authenticatorSelection": {
            "userVerification": "{{userVerification}}",
            "residentKey": "required"
          },
          "attestation": "none",
          "timeout": 60000
        }
        """);

    [Fact]
    public async Task HandleCreate_ApprovalCreateNew_SavesNewLoginWithCredential_AndReturnsAttestation()
    {
        var write = new FakeVaultWriteService();
        var approval = new CapturingApprovalService(approve: true)
        {
            CreateResult = new PasskeyCreateApprovalResult(
                new PasskeyApprovalEvidence(true, true, true),
                CreateNew: true,
                CipherId: null),
        };
        var handler = new BrowserPasskeyRequestHandler(new FakeVaultService(VaultState.Unlocked, []), write, approval);

        using var doc = CreatePayload();
        var response = await handler.HandleAsync(new BrowserPasskeyRequest("req-1", "passkey.create", doc.RootElement), TestContext.Current.CancellationToken);

        Assert.True(response.Ok);
        Assert.Equal("passkey.create", response.Type);
        var payload = response.Payload!.Value.Deserialize(PasskeyJsonContext.Default.PasskeyCreateAttestationPayload)!;
        Assert.False(string.IsNullOrWhiteSpace(payload.AttestationObject));
        Assert.NotNull(write.Saved);
        Assert.Equal(CipherType.Login, write.Saved!.Type);
        var cred = Assert.Single(write.Saved.Login!.Fido2Credentials);
        Assert.Equal("github.com", cred.RpId);
        Assert.Equal(payload.CredentialId, cred.CredentialId);
    }

    [Fact]
    public async Task HandleCreate_ApprovalAttach_AugmentsExistingCipher()
    {
        var existing = new Cipher { Id = "c-9", Type = CipherType.Login, Name = "GitHub", Login = new CipherLogin("u", null, null, []) };
        var write = new FakeVaultWriteService();
        var approval = new CapturingApprovalService(approve: true)
        {
            CreateResult = new PasskeyCreateApprovalResult(
                new PasskeyApprovalEvidence(true, true, true),
                CreateNew: false,
                CipherId: "c-9"),
        };
        var handler = new BrowserPasskeyRequestHandler(new FakeVaultService(VaultState.Unlocked, [existing]), write, approval);

        using var doc = CreatePayload();
        await handler.HandleAsync(new BrowserPasskeyRequest("req-1", "passkey.create", doc.RootElement), TestContext.Current.CancellationToken);

        Assert.Equal("c-9", write.Saved!.Id);
        Assert.Single(write.Saved.Login!.Fido2Credentials);
    }

    [Fact]
    public async Task HandleCreate_WhenLocked_ReturnsVaultLocked()
    {
        var handler = new BrowserPasskeyRequestHandler(new FakeVaultService(VaultState.Locked, []), new FakeVaultWriteService(), new CapturingApprovalService(approve: true));
        using var doc = CreatePayload();
        var response = await handler.HandleAsync(new BrowserPasskeyRequest("r", "passkey.create", doc.RootElement), TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
    }

    [Fact]
    public async Task HandleCreate_Cancelled_DoesNotSave()
    {
        var write = new FakeVaultWriteService();
        var approval = new CapturingApprovalService(approve: false)
        {
            CreateResult = new PasskeyCreateApprovalResult(
                new PasskeyApprovalEvidence(false, false, false),
                CreateNew: false,
                CipherId: null),
        };
        var handler = new BrowserPasskeyRequestHandler(
            new FakeVaultService(VaultState.Unlocked, []),
            write,
            approval);
        using var doc = CreatePayload();
        var response = await handler.HandleAsync(new BrowserPasskeyRequest("r", "passkey.create", doc.RootElement), TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Null(write.Saved);
    }

    [Fact]
    public async Task HandleCreate_ExcludeExistingCredential_ReturnsError()
    {
        // existing passkey for github.com with credId "ZXhpc3Q"; exclude it → InvalidStateError.
        var cred = new CipherFido2Credential("ZXhpc3Q", "public-key", "ECDSA", "P-256", "k", "github.com", "u", "octo", 0, "GitHub", "Octo", true, DateTimeOffset.UtcNow);
        var existing = new Cipher { Id = "c-1", Type = CipherType.Login, Name = "GitHub", Login = new CipherLogin("u", null, null, []) { Fido2Credentials = [cred] } };
        var write = new FakeVaultWriteService();
        var handler = new BrowserPasskeyRequestHandler(new FakeVaultService(VaultState.Unlocked, [existing]), write, new CapturingApprovalService(approve: true));
        using var doc = CreatePayload(exclude: """{ "id": "ZXhpc3Q", "type": "public-key", "transports": ["internal"] }""");
        var response = await handler.HandleAsync(new BrowserPasskeyRequest("r", "passkey.create", doc.RootElement), TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Null(write.Saved);
    }

    [Theory]
    [InlineData(null, PasskeyUserVerificationRequirement.Preferred, true, true, false, true, null, 0x01)]
    [InlineData("required", PasskeyUserVerificationRequirement.Required, true, true, true, true, null, 0x05)]
    [InlineData("required", PasskeyUserVerificationRequirement.Required, true, true, false, false, "user_verification_failed", 0)]
    [InlineData("preferred", PasskeyUserVerificationRequirement.Preferred, true, true, true, true, null, 0x05)]
    [InlineData("preferred", PasskeyUserVerificationRequirement.Preferred, true, true, false, true, null, 0x01)]
    [InlineData("discouraged", PasskeyUserVerificationRequirement.Discouraged, true, true, true, true, null, 0x05)]
    [InlineData("discouraged", PasskeyUserVerificationRequirement.Discouraged, true, true, false, true, null, 0x01)]
    [InlineData("preferred", PasskeyUserVerificationRequirement.Preferred, false, false, false, false, "user_cancelled", 0)]
    [InlineData("preferred", PasskeyUserVerificationRequirement.Preferred, true, false, true, false, "user_verification_failed", 0)]
    public async Task HandleGet_UserVerificationRequirementAndEvidenceMatrix(
        string? wireRequirement,
        PasskeyUserVerificationRequirement expectedRequirement,
        bool approved,
        bool userPresent,
        bool userVerified,
        bool expectedOk,
        string? expectedError,
        int expectedFlags)
    {
        var cipher = CreateCipherWithCredential(out var credentialId);
        var approval = new CapturingApprovalService(approved, userPresent, userVerified);
        var handler = new BrowserPasskeyRequestHandler(
            new FakeVaultService(VaultState.Unlocked, [cipher]),
            new FakeVaultWriteService(),
            approval);
        using var payload = CreateGetPayload(credentialId, wireRequirement);

        var response = await handler.HandleAsync(
            new BrowserPasskeyRequest("matrix-get", "passkey.get", payload.RootElement),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedOk, response.Ok);
        Assert.Equal(expectedRequirement, approval.Request!.UserVerificationRequirement);
        if (!expectedOk)
        {
            Assert.Equal(expectedError, response.Error?.Code);
            Assert.Null(response.Payload);
            return;
        }

        var assertion = response.Payload!.Value.Deserialize(
            PasskeyJsonContext.Default.PasskeyGetAssertionPayload)!;
        var authData = WebAuthnCrypto.DecodeBase64Url(assertion.AuthenticatorData);
        Assert.Equal(expectedFlags, (int)authData[32]);
    }

    [Theory]
    [InlineData("required", PasskeyUserVerificationRequirement.Required, true, true, true, true, null, 0x45)]
    [InlineData("required", PasskeyUserVerificationRequirement.Required, true, true, false, false, "user_verification_failed", 0)]
    [InlineData("preferred", PasskeyUserVerificationRequirement.Preferred, true, true, true, true, null, 0x45)]
    [InlineData("preferred", PasskeyUserVerificationRequirement.Preferred, true, true, false, true, null, 0x41)]
    [InlineData("discouraged", PasskeyUserVerificationRequirement.Discouraged, true, true, true, true, null, 0x45)]
    [InlineData("discouraged", PasskeyUserVerificationRequirement.Discouraged, true, true, false, true, null, 0x41)]
    [InlineData("discouraged", PasskeyUserVerificationRequirement.Discouraged, false, false, false, false, "user_cancelled", 0)]
    [InlineData("preferred", PasskeyUserVerificationRequirement.Preferred, true, false, true, false, "user_verification_failed", 0)]
    public async Task HandleCreate_UserVerificationRequirementAndEvidenceMatrix(
        string wireRequirement,
        PasskeyUserVerificationRequirement expectedRequirement,
        bool approved,
        bool userPresent,
        bool userVerified,
        bool expectedOk,
        string? expectedError,
        int expectedFlags)
    {
        var write = new FakeVaultWriteService();
        var approval = new CapturingApprovalService(approved, userPresent, userVerified);
        var handler = new BrowserPasskeyRequestHandler(
            new FakeVaultService(VaultState.Unlocked, []),
            write,
            approval);
        using var payload = CreatePayload(userVerification: wireRequirement);

        var response = await handler.HandleAsync(
            new BrowserPasskeyRequest("matrix-create", "passkey.create", payload.RootElement),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedOk, response.Ok);
        Assert.Equal(expectedRequirement, approval.CreateRequest!.UserVerificationRequirement);
        if (!expectedOk)
        {
            Assert.Equal(expectedError, response.Error?.Code);
            Assert.Null(write.Saved);
            return;
        }

        Assert.NotNull(write.Saved);
        var attestation = response.Payload!.Value.Deserialize(
            PasskeyJsonContext.Default.PasskeyCreateAttestationPayload)!;
        var authData = WebAuthnCrypto.DecodeBase64Url(attestation.AuthData);
        Assert.Equal(expectedFlags, (int)authData[32]);
    }

    private static JsonDocument CreateGetPayload(
        string credentialId,
        string? userVerification) =>
        JsonDocument.Parse($$"""
        {
          "origin": "https://github.com",
          "rpId": "github.com",
          "challenge": "AQIDBA",
          "allowCredentials": [
            { "id": "{{credentialId}}", "type": "public-key", "transports": ["internal"] }
          ],
          "userVerification": {{System.Text.Json.JsonSerializer.Serialize(userVerification)}},
          "mediation": null,
          "timeout": 60000
        }
        """);

    private static Cipher CreateCipherWithCredential(out string credentialId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        credentialId = WebAuthnCrypto.EncodeBase64Url([1, 2, 3, 4]);
        return new Cipher
        {
            Id = "cipher-1",
            Type = CipherType.Login,
            Name = "GitHub",
            Login = new CipherLogin("octo@example.com", null, null, [])
            {
                Fido2Credentials = [Credential(key, credentialId)],
            },
        };
    }

    private static CipherFido2Credential Credential(ECDsa key, string credentialId) => new(
        credentialId,
        "public-key",
        "ECDSA",
        "P-256",
        WebAuthnCrypto.EncodeBase64Url(key.ExportPkcs8PrivateKey()),
        "github.com",
        WebAuthnCrypto.EncodeBase64Url([5, 6, 7, 8]),
        "octo@example.com",
        5,
        "GitHub",
        "Octo",
        true,
        DateTimeOffset.Parse("2026-06-24T00:00:00Z"));

    private sealed class CapturingApprovalService : IPasskeyApprovalService
    {
        private readonly PasskeyApprovalEvidence _evidence;

        public CapturingApprovalService(
            bool approve,
            bool userPresent = true,
            bool userVerified = true)
        {
            _evidence = approve
                ? new PasskeyApprovalEvidence(true, userPresent, userVerified)
                : new PasskeyApprovalEvidence(false, false, false);
            CreateResult = new PasskeyCreateApprovalResult(_evidence, true, null);
        }

        public PasskeyApprovalRequest? Request { get; private set; }

        public PasskeyCreateApprovalRequest? CreateRequest { get; private set; }

        public PasskeyCreateApprovalResult CreateResult { get; set; }

        public Task<PasskeyUseApprovalResult> ConfirmUseAsync(
            PasskeyApprovalRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new PasskeyUseApprovalResult(
                _evidence,
                _evidence.Approved ? request.CipherId : null,
                _evidence.Approved ? request.CredentialId : null));
        }

        public Task<PasskeyCreateApprovalResult> ConfirmCreateAsync(
            PasskeyCreateApprovalRequest request,
            CancellationToken ct = default)
        {
            CreateRequest = request;
            return Task.FromResult(CreateResult);
        }
    }

    private sealed class FakeVaultService : IVaultService
    {
        private readonly IReadOnlyList<Cipher> _ciphers;

        public FakeVaultService(VaultState state, IReadOnlyList<Cipher> ciphers)
        {
            _ciphers = ciphers;
            Snapshot = new FakeSnapshot(state);
        }

        public IVaultSnapshot Snapshot { get; }

        public IReadOnlyList<Cipher> GetCiphers() => _ciphers;

        public IReadOnlyList<Folder> GetFolders() => [];

        public IReadOnlyList<DeviceInfo> GetDevices() => [];
    }

    private sealed class FakeVaultWriteService : IVaultWriteService
    {
        public Cipher? Saved { get; private set; }

        public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default)
        {
            Saved = cipher;
            return Task.CompletedTask;
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

    private sealed class FakeSnapshot : IVaultSnapshot
    {
        public FakeSnapshot(VaultState state) => State = state;

        public VaultState State { get; }

        public IReadOnlyList<Cipher> Ciphers => [];

        public IReadOnlyList<Folder> Folders => [];

        public IReadOnlyList<DeviceInfo> Devices => [];

        public AccountInfo Account => AccountInfo.Empty;

        public int SkippedCipherCount => 0;

        public bool DataStale => false;
    }
}
