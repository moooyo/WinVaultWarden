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

public class BrowserPasskeyRequestHandlerTests
{
    [Fact]
    public async Task HandleGet_LockDuringApproval_DoesNotSign()
    {
        var session = NewUnlockedPasskeySession(out var lease, ExistingCredentialCipher());
        var approval = new BlockingApprovalService();
        var write = new FakeVaultWriteService();
        var handler = new BrowserPasskeyRequestHandler(
            new VaultService(session), write, approval);
        using var payload = GetPayload();

        var running = handler.HandleAsync(
            new BrowserPasskeyRequest("get-1", "passkey.get", payload.RootElement, Context: Ctx()),
            CancellationToken.None);
        await approval.UseEntered.Task;
        session.Lock();
        approval.CompleteUse(new PasskeyUseApprovalResult(
            new PasskeyApprovalEvidence(
                Approved: true,
                UserPresent: true,
                UserVerified: true),
            CipherId: null,
            CredentialId: null));

        var response = await running;

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.OperationAborted, response.Error?.Code);
        Assert.False(session.IsCurrent(lease));
        Assert.Null(write.Saved);
    }

    [Fact]
    public async Task HandleCreate_LockDuringApproval_DoesNotSaveCredential()
    {
        var session = NewUnlockedPasskeySession(out _, ExistingLoginWithoutPasskey());
        var approval = new BlockingApprovalService();
        var write = new FakeVaultWriteService();
        var handler = new BrowserPasskeyRequestHandler(
            new VaultService(session), write, approval);
        using var payload = CreatePayload();

        var running = handler.HandleAsync(
            new BrowserPasskeyRequest("create-1", "passkey.create", payload.RootElement, Context: Ctx()),
            CancellationToken.None);
        await approval.CreateEntered.Task;
        session.Lock();
        approval.CompleteCreate(new PasskeyCreateApprovalResult(
            new PasskeyApprovalEvidence(
                Approved: true,
                UserPresent: true,
                UserVerified: true),
            CreateNew: true,
            CipherId: null));

        var response = await running;

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.OperationAborted, response.Error?.Code);
        Assert.Null(write.Saved);
    }

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
            new BrowserPasskeyRequest("req-1", "passkey.get", payload.RootElement, Context: Ctx()),
            TestContext.Current.CancellationToken);

        Assert.True(response.Ok);
        Assert.Equal("passkey.get", response.Type);
        Assert.NotNull(response.Payload);
        var assertion = response.Payload.Value.Deserialize(PasskeyJsonContext.Default.PasskeyGetAssertionPayload)!;
        Assert.Equal(credentialId, assertion.CredentialId);
        Assert.False(string.IsNullOrWhiteSpace(assertion.AuthenticatorData));
        Assert.False(string.IsNullOrWhiteSpace(assertion.ClientDataJson));
        Assert.False(string.IsNullOrWhiteSpace(assertion.Signature));
        Assert.Equal("github.com", approval.Request?.Source.RpId);
        Assert.Equal("GitHub", Assert.Single(approval.Request!.Candidates).CipherName);
    }

    // Browser-task-7 remediation (review L3): credential_unusable must never forward ex.Message onto
    // the wire. This forces a REAL FormatException deep in WebAuthnCrypto.SignAssertion (malformed
    // stored private key, sentinel-flavoured to look like something worth leaking) and asserts the
    // response is strictly code-only: no Message property, no serialized "message" field, and none of
    // the sentinel/path/token markers anywhere in the response JSON.
    [Fact]
    public async Task HandleAsync_GetAssertion_MalformedStoredKey_ReturnsCodeOnlyCredentialUnusable()
    {
        const string sentinel = @"SENTINEL_INTERNAL C:\Users\alice\vault token=secret stack=WebAuthnCrypto";
        var credentialId = WebAuthnCrypto.EncodeBase64Url([9, 9, 9, 9]);
        var credential = new CipherFido2Credential(
            credentialId,
            "public-key",
            "ECDSA",
            "P-256",
            KeyValue: sentinel, // not valid base64url: forces FormatException during signing
            "github.com",
            WebAuthnCrypto.EncodeBase64Url([5, 6, 7, 8]),
            "octo@example.com",
            5,
            "GitHub",
            "Octo",
            true,
            DateTimeOffset.Parse("2026-06-24T00:00:00Z"));
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
          "userVerification": "preferred",
          "timeout": 60000
        }
        """);

        var response = await handler.HandleAsync(
            new BrowserPasskeyRequest("req-cu", "passkey.get", payload.RootElement, Context: Ctx()),
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal("credential_unusable", response.Error?.Code);
        Assert.Null(response.Error?.Message);
        var json = JsonSerializer.Serialize(response, PasskeyJsonContext.Default.BrowserPasskeyResponse);
        Assert.DoesNotContain("message", json, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "SENTINEL_INTERNAL", @"C:\Users", "token=secret", "stack=" })
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_GetAssertion_WhenVaultLocked_ReturnsClientNotHandled()
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
            new BrowserPasskeyRequest("req-2", "passkey.get", payload.RootElement, Context: Ctx()),
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        // Initial locked vault is "not yet handled by the App": fall back to the platform
        // authenticator instead of surfacing an internal locked state to the page.
        Assert.Equal(PasskeyErrorCodes.ClientNotHandled, response.Error?.Code);
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
            new BrowserPasskeyRequest("req-3", "passkey.get", payload.RootElement, Context: Ctx()),
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
            new BrowserPasskeyRequest("req-nc", "passkey.get", payload.RootElement, Context: Ctx()),
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.ClientNotHandled, response.Error?.Code);
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
            new BrowserPasskeyRequest("req-decl", "passkey.get", payload.RootElement, Context: Ctx()),
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

    [Fact]
    public void ApprovalResultRecords_KeepEvidenceAndSelectionShape()
    {
        // Guard: this task only extends the approval *request* inputs. The final evidence/result
        // record shapes the first three layers depend on must stay byte-for-byte identical.
        Assert.Equal(
            new[] { "Evidence", "CreateNew", "CipherId" },
            typeof(PasskeyCreateApprovalResult).GetProperties().Select(p => p.Name));
        Assert.Equal(
            new[] { "Evidence", "CipherId", "CredentialId" },
            typeof(PasskeyUseApprovalResult).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public async Task DiscoverableGet_MultipleCandidates_UsesExplicitSecondSelection()
    {
        var first = CandidateCipher("cipher-a", "credential-a", "alice@example.com");
        var second = CandidateCipher("cipher-b", "credential-b", "bob@example.com");
        var approval = new CapturingApprovalService
        {
            UseResult = new PasskeyUseApprovalResult(
                new PasskeyApprovalEvidence(true, true, false),
                "cipher-b",
                "credential-b"),
        };
        var handler = CreateHandler([first, second], approval);

        var response = await handler.HandleAsync(
            RequestFor("passkey.get", allowCredentials: []),
            TestContext.Current.CancellationToken);

        Assert.True(response.Ok);
        Assert.Equal(2, approval.Request!.Candidates.Count);
        var payload = response.Payload!.Value.Deserialize(
            PasskeyJsonContext.Default.PasskeyGetAssertionPayload)!;
        Assert.Equal("credential-b", payload.CredentialId);
        var flags = WebAuthnCrypto.DecodeBase64Url(payload.AuthenticatorData)[32];
        Assert.Equal(0x01, flags & 0x01);
        Assert.Equal(0x00, flags & 0x04);
    }

    [Fact]
    public async Task Get_ForgedApprovalSelection_IsRejectedBeforeSigning()
    {
        var approval = new CapturingApprovalService
        {
            UseResult = new PasskeyUseApprovalResult(
                new PasskeyApprovalEvidence(true, true, true),
                "not-a-candidate",
                "not-a-credential"),
        };
        var handler = CreateHandler([CandidateCipher("cipher-a", "credential-a", "alice")], approval);

        var response = await handler.HandleAsync(
            RequestFor("passkey.get", allowCredentials: []),
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.InternalError, response.Error!.Code);
    }

    [Fact]
    public async Task DiscoverableGet_SingleCandidate_SignsSelectedCredential()
    {
        var approval = new CapturingApprovalService
        {
            UseResult = new PasskeyUseApprovalResult(
                new PasskeyApprovalEvidence(true, true, false),
                "cipher-a",
                "credential-a"),
        };
        var handler = CreateHandler([CandidateCipher("cipher-a", "credential-a", "alice")], approval);

        var response = await handler.HandleAsync(
            RequestFor("passkey.get", allowCredentials: []),
            TestContext.Current.CancellationToken);

        Assert.True(response.Ok);
        Assert.Single(approval.Request!.Candidates);
        var payload = response.Payload!.Value.Deserialize(
            PasskeyJsonContext.Default.PasskeyGetAssertionPayload)!;
        Assert.Equal("credential-a", payload.CredentialId);
    }

    [Fact]
    public async Task DiscoverableGet_RepromptCipher_PassesRequiresRepromptTarget()
    {
        var approval = new CapturingApprovalService
        {
            UseResult = new PasskeyUseApprovalResult(
                new PasskeyApprovalEvidence(true, true, true),
                "cipher-a",
                "credential-a"),
        };
        var handler = CreateHandler(
            [CandidateCipher("cipher-a", "credential-a", "alice", reprompt: true)],
            approval);

        var response = await handler.HandleAsync(
            RequestFor("passkey.get", allowCredentials: []),
            TestContext.Current.CancellationToken);

        Assert.True(response.Ok);
        Assert.True(Assert.Single(approval.Request!.Candidates).RequiresReprompt);
    }

    [Fact]
    public async Task HandleCreate_ForgedAttachSelection_IsRejected()
    {
        var existing = new Cipher
        {
            Id = "c-9",
            Type = CipherType.Login,
            Name = "GitHub",
            Login = new CipherLogin("u", null, null, []),
        };
        var approval = new CapturingApprovalService
        {
            CreateResult = new PasskeyCreateApprovalResult(
                new PasskeyApprovalEvidence(true, true, true),
                CreateNew: false,
                CipherId: "not-a-candidate"),
        };
        var handler = new BrowserPasskeyRequestHandler(
            new FakeVaultService(VaultState.Unlocked, [existing]),
            new FakeVaultWriteService(),
            approval);
        using var doc = CreatePayload();

        var response = await handler.HandleAsync(
            new BrowserPasskeyRequest("create-forge", "passkey.create", doc.RootElement, Context: Ctx()),
            TestContext.Current.CancellationToken);

        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.InternalError, response.Error!.Code);
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
        var response = await handler.HandleAsync(new BrowserPasskeyRequest("req-1", "passkey.create", doc.RootElement, Context: Ctx()), TestContext.Current.CancellationToken);

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
    public async Task HandleCreate_RemoteCommitWithRefreshFailure_StillReturnsAttestation()
    {
        var write = new FakeVaultWriteService
        {
            SaveException = new VaultRefreshException(
                "Remote write committed.",
                new HttpRequestException("Refresh failed.")),
        };
        var approval = new CapturingApprovalService(approve: true)
        {
            CreateResult = new PasskeyCreateApprovalResult(
                new PasskeyApprovalEvidence(true, true, true),
                CreateNew: true,
                CipherId: null),
        };
        var handler = new BrowserPasskeyRequestHandler(
            new FakeVaultService(VaultState.Unlocked, []),
            write,
            approval);

        using var doc = CreatePayload();
        var response = await handler.HandleAsync(
            new BrowserPasskeyRequest("req-committed", "passkey.create", doc.RootElement, Context: Ctx()),
            TestContext.Current.CancellationToken);

        Assert.True(response.Ok);
        var payload = response.Payload!.Value.Deserialize(
            PasskeyJsonContext.Default.PasskeyCreateAttestationPayload)!;
        Assert.False(string.IsNullOrWhiteSpace(payload.AttestationObject));
        Assert.NotNull(write.Saved);
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
        await handler.HandleAsync(new BrowserPasskeyRequest("req-1", "passkey.create", doc.RootElement, Context: Ctx()), TestContext.Current.CancellationToken);

        Assert.Equal("c-9", write.Saved!.Id);
        Assert.Single(write.Saved.Login!.Fido2Credentials);
    }

    [Fact]
    public async Task HandleCreate_WhenLocked_ReturnsClientNotHandled()
    {
        var handler = new BrowserPasskeyRequestHandler(new FakeVaultService(VaultState.Locked, []), new FakeVaultWriteService(), new CapturingApprovalService(approve: true));
        using var doc = CreatePayload();
        var response = await handler.HandleAsync(new BrowserPasskeyRequest("r", "passkey.create", doc.RootElement, Context: Ctx()), TestContext.Current.CancellationToken);
        Assert.False(response.Ok);
        Assert.Equal(PasskeyErrorCodes.ClientNotHandled, response.Error?.Code);
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
        var response = await handler.HandleAsync(new BrowserPasskeyRequest("r", "passkey.create", doc.RootElement, Context: Ctx()), TestContext.Current.CancellationToken);
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
        var response = await handler.HandleAsync(new BrowserPasskeyRequest("r", "passkey.create", doc.RootElement, Context: Ctx()), TestContext.Current.CancellationToken);
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
            new BrowserPasskeyRequest("matrix-get", "passkey.get", payload.RootElement, Context: Ctx()),
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
            new BrowserPasskeyRequest("matrix-create", "passkey.create", payload.RootElement, Context: Ctx()),
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

    // Trusted browser context supplied by the native host. The Task 1 policy derives origin/RP
    // from this, never from the page-reported payload. Defaults describe a top-level github.com frame.
    private static BrowserRequestContext Ctx(
        string frameOrigin = "https://github.com",
        string? topOrigin = null,
        int tabId = 1,
        int frameId = 0,
        bool publicKeyCredentialsGetAllowed = true) =>
        new(
            frameOrigin,
            topOrigin ?? frameOrigin,
            tabId,
            frameId,
            "document-1",
            publicKeyCredentialsGetAllowed);

    private static BrowserPasskeyRequestHandler CreateHandler(
        Cipher[] ciphers,
        IPasskeyApprovalService approval) =>
        new(
            new FakeVaultService(VaultState.Unlocked, ciphers),
            new FakeVaultWriteService(),
            approval);

    private static BrowserPasskeyRequest RequestFor(
        string type,
        IReadOnlyList<string>? allowCredentials = null,
        string origin = "https://github.com",
        string rpId = "github.com",
        string userVerification = "preferred")
    {
        var allow = string.Join(
            ",",
            (allowCredentials ?? []).Select(id =>
                $$"""{ "id": "{{id}}", "type": "public-key", "transports": ["internal"] }"""));
        using var doc = JsonDocument.Parse($$"""
        {
          "origin": "{{origin}}",
          "rpId": "{{rpId}}",
          "challenge": "AQIDBA",
          "allowCredentials": [{{allow}}],
          "userVerification": "{{userVerification}}",
          "timeout": 60000
        }
        """);
        return new BrowserPasskeyRequest(
            Guid.NewGuid().ToString("N"),
            type,
            doc.RootElement.Clone(),
            Context: Ctx(origin));
    }

    private static Cipher CandidateCipher(
        string cipherId,
        string credentialId,
        string userName,
        bool reprompt = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credential = new CipherFido2Credential(
            credentialId,
            "public-key",
            "ECDSA",
            "P-256",
            WebAuthnCrypto.EncodeBase64Url(key.ExportPkcs8PrivateKey()),
            "github.com",
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
            Reprompt = reprompt,
            Login = new CipherLogin(userName, null, null, [])
            {
                Fido2Credentials = [credential],
            },
        };
    }

    private sealed class CapturingApprovalService : IPasskeyApprovalService
    {
        private readonly PasskeyApprovalEvidence _evidence;

        public CapturingApprovalService()
        {
            _evidence = new PasskeyApprovalEvidence(false, false, false);
            CreateResult = new PasskeyCreateApprovalResult(_evidence, CreateNew: false, CipherId: null);
        }

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

        // When set, ConfirmUseAsync returns this verbatim so a test can drive an explicit (or a
        // forged) selection; otherwise it approves the first offered candidate by default.
        public PasskeyUseApprovalResult? UseResult { get; set; }

        public PasskeyApprovalRequest? Request { get; private set; }

        public PasskeyCreateApprovalRequest? CreateRequest { get; private set; }

        public PasskeyCreateApprovalResult CreateResult { get; set; }

        public Task<PasskeyUseApprovalResult> ConfirmUseAsync(
            PasskeyApprovalRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            if (UseResult is { } explicitResult)
                return Task.FromResult(explicitResult);
            var target = request.Candidates.Count > 0 ? request.Candidates[0] : null;
            return Task.FromResult(new PasskeyUseApprovalResult(
                _evidence,
                _evidence.Approved ? target?.CipherId : null,
                _evidence.Approved ? target?.CredentialId : null));
        }

        public Task<PasskeyCreateApprovalResult> ConfirmCreateAsync(
            PasskeyCreateApprovalRequest request,
            CancellationToken ct = default)
        {
            CreateRequest = request;
            return Task.FromResult(CreateResult);
        }
    }

    private static VaultSession NewUnlockedPasskeySession(
        out VaultSessionLease lease,
        params Cipher[] ciphers)
    {
        var session = new VaultSession();
        var origin = new ServerOrigin("https://vault.example");
        lease = session.BeginAccountSession("account-a", "octo@example.com", origin);
        var key = new SymmetricCryptoKey(new byte[64]);
        session.TryCommit(lease, () =>
        {
            session.SetUnlockedKey(key);
            session.SetSnapshot(new DecryptedVault(
                new AccountInfo(
                    "account-a",
                    "octo@example.com",
                    origin,
                    "O",
                    string.Empty),
                [],
                ciphers,
                0,
                0));
        });
        return session;
    }

    private static Cipher ExistingCredentialCipher()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentialId = WebAuthnCrypto.EncodeBase64Url([1, 2, 3, 4]);
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

    private static Cipher ExistingLoginWithoutPasskey() => new()
    {
        Id = "c-9",
        Type = CipherType.Login,
        Name = "GitHub",
        Login = new CipherLogin("octo@example.com", null, null, []),
    };

    private static JsonDocument GetPayload() =>
        JsonDocument.Parse(
            """
            {
              "origin": "https://github.com",
              "rpId": "github.com",
              "challenge": "AQIDBA",
              "allowCredentials": [],
              "userVerification": "preferred",
              "timeout": 60000
            }
            """);

    private sealed class BlockingApprovalService : IPasskeyApprovalService
    {
        private readonly TaskCompletionSource<PasskeyUseApprovalResult> _use =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<PasskeyCreateApprovalResult> _create =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource UseEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CreateEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PasskeyUseApprovalResult> ConfirmUseAsync(
            PasskeyApprovalRequest request,
            CancellationToken ct = default)
        {
            UseEntered.TrySetResult();
            return await _use.Task.WaitAsync(ct);
        }

        public async Task<PasskeyCreateApprovalResult> ConfirmCreateAsync(
            PasskeyCreateApprovalRequest request,
            CancellationToken ct = default)
        {
            CreateEntered.TrySetResult();
            return await _create.Task.WaitAsync(ct);
        }

        public void CompleteUse(PasskeyUseApprovalResult result) =>
            _use.TrySetResult(result);

        public void CompleteCreate(PasskeyCreateApprovalResult result) =>
            _create.TrySetResult(result);
    }

    private sealed class FakeVaultService : IVaultService
    {
        private readonly VaultSession _session = new();

        public FakeVaultService(
            VaultState state,
            IReadOnlyList<Cipher> ciphers)
        {
            if (state != VaultState.Unlocked)
                return;
            var origin = new ServerOrigin("https://vault.example");
            var lease = _session.BeginAccountSession(
                "account-a", "a@example.com", origin);
            var key = new SymmetricCryptoKey(new byte[64]);
            _session.TryCommit(lease, () =>
            {
                _session.SetUnlockedKey(key);
                _session.SetSnapshot(new DecryptedVault(
                    new AccountInfo(
                        "account-a",
                        "a@example.com",
                        origin,
                        "A",
                        string.Empty),
                    [],
                    ciphers,
                    0,
                    0));
            });
        }

        public IVaultSnapshot Snapshot => _session;
        public IReadOnlyList<Cipher> GetCiphers() => _session.Ciphers;
        public IReadOnlyList<Folder> GetFolders() => _session.Folders;
        public IReadOnlyList<DeviceInfo> GetDevices() => _session.Devices;
        public VaultSessionLease CaptureLease() => _session.CaptureLease();
        public bool IsCurrent(VaultSessionLease lease) =>
            _session.IsCurrent(lease);
        public void ThrowIfExpired(VaultSessionLease lease) =>
            _session.ThrowIfExpired(lease);
        public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease) =>
            _session.GetCiphers(lease);
    }

    private sealed class FakeVaultWriteService : IVaultWriteService
    {
        public Cipher? Saved { get; private set; }
        public Exception? SaveException { get; init; }

        public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default)
        {
            Saved = cipher;
            return SaveException is null ? Task.CompletedTask : Task.FromException(SaveException);
        }

        public Task SaveCipherAsync(Cipher cipher, VaultSessionLease lease, CancellationToken ct = default)
        {
            Saved = cipher;
            return SaveException is null ? Task.CompletedTask : Task.FromException(SaveException);
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
