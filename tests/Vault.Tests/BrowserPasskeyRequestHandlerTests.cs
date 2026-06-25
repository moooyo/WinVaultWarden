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
        var assertion = Assert.IsType<PasskeyGetAssertionPayload>(response.Payload);
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
        private readonly bool _approve;

        public CapturingApprovalService(bool approve) => _approve = approve;

        public PasskeyApprovalRequest? Request { get; private set; }

        public Task<bool> ConfirmUseAsync(PasskeyApprovalRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(_approve);
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

    private sealed class FakeSnapshot : IVaultSnapshot
    {
        public FakeSnapshot(VaultState state) => State = state;

        public VaultState State { get; }

        public IReadOnlyList<Cipher> Ciphers => [];

        public IReadOnlyList<Folder> Folders => [];

        public IReadOnlyList<DeviceInfo> Devices => [];

        public AccountInfo Account => AccountInfo.Empty;
    }
}
