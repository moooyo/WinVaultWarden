using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Api;
using Api.Dtos;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Passkeys;
using Core.Services;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class WebAuthnTwoFactorManagementTests
{
    private const string Email = "test@example.com";
    private const string MasterPassword = "correct-horse-battery-staple";
    private const int Iterations = 600_000;

    [Fact]
    public void RegistrationChallenge_ParsesAndBindsCreationOptions()
    {
        var options = CreateChallengeOptions(
            rpId: "vault.example",
            timeout: 500_000,
            transports: ["smart-card"]);

        var challenge = WebAuthnTwoFactorRegistrationProtocol.ParseChallenge(
            options,
            ServerEndpoints.ForSelfHosted("https://vault.example"),
            "uv-token");

        Assert.Equal("https://vault.example", challenge.Origin);
        Assert.Equal("vault.example", challenge.RpId);
        Assert.Equal("Vault", challenge.RpName);
        Assert.Equal(120_000, challenge.TimeoutMilliseconds);
        Assert.Equal("cross-platform", challenge.AuthenticatorAttachment);
        Assert.Equal("discouraged", challenge.ResidentKey);
        Assert.Equal("discouraged", challenge.UserVerification);
        Assert.Equal("none", challenge.Attestation);
        Assert.Equal("uv-token", challenge.UserVerificationToken);
        Assert.Equal([-7, -257], challenge.PublicKeyCredentialParameters.Select(value => value.Alg));
        Assert.Equal("smart-card", Assert.Single(challenge.ExcludeCredentials).Transports.Single());
    }

    [Fact]
    public void RegistrationChallenge_RejectsRpOutsideCurrentSessionOrigin()
    {
        var options = CreateChallengeOptions(rpId: "other.example");

        Assert.Throws<WebAuthnTwoFactorException>(() =>
            WebAuthnTwoFactorRegistrationProtocol.ParseChallenge(
                options,
                ServerEndpoints.ForSelfHosted("https://vault.example")));
    }

    [Fact]
    public void RegistrationChallenge_RejectsUnsupportedExtensions()
    {
        var options = CreateChallengeOptions(extensions: new JsonObject { ["credProps"] = true });

        Assert.Throws<WebAuthnTwoFactorException>(() =>
            WebAuthnTwoFactorRegistrationProtocol.ParseChallenge(
                options,
                ServerEndpoints.ForSelfHosted("https://vault.example")));
    }

    [Fact]
    public void DeviceResponse_RejectsClientDataFromAnotherChallenge()
    {
        var challenge = WebAuthnTwoFactorRegistrationProtocol.ParseChallenge(
            CreateChallengeOptions(),
            ServerEndpoints.ForSelfHosted("https://vault.example"));
        var response = CreateDeviceResponse(challenge, challengeOverride: Encode(new byte[32]));

        Assert.Throws<WebAuthnTwoFactorException>(() =>
            WebAuthnTwoFactorRegistrationProtocol.ValidateDeviceResponse(challenge, response));
    }

    [Fact]
    public async Task GetWebAuthn_MapsCredentialsAndSendsMasterPasswordHash()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(1, "Windows Hello", false),
            new WebAuthnCredentialItem(2, "Migrated key", true));

        var details = await fixture.Service.GetWebAuthnAsync(
            MasterPassword,
            TestContext.Current.CancellationToken);

        Assert.True(details.Enabled);
        Assert.Equal(2, details.Credentials.Count);
        Assert.Equal("Migrated key", details.Credentials[1].Name);
        Assert.True(details.Credentials[1].Migrated);
        Assert.Equal(fixture.MasterPasswordHash, fixture.Api.LastGetWebAuthn?.MasterPasswordHash);
    }

    [Theory]
    [InlineData(TwoFactorUserVerificationKind.Otp, "123456")]
    [InlineData(TwoFactorUserVerificationKind.AuthRequestAccessCode, "access-code")]
    public async Task GetWebAuthn_SendsOnlySelectedNonPasswordVerification(
        TwoFactorUserVerificationKind kind,
        string secret)
    {
        var fixture = CreateFixture();
        var verification = kind == TwoFactorUserVerificationKind.Otp
            ? TwoFactorUserVerification.FromOtp(secret)
            : TwoFactorUserVerification.FromAuthRequestAccessCode(secret);

        await fixture.Service.GetWebAuthnAsync(
            verification,
            TestContext.Current.CancellationToken);

        var request = Assert.IsType<WebAuthnVerificationRequest>(fixture.Api.LastGetWebAuthn);
        Assert.Null(request.MasterPasswordHash);
        Assert.Null(request.UserVerificationToken);
        if (kind == TwoFactorUserVerificationKind.Otp)
        {
            Assert.Equal(secret, request.Otp);
            Assert.Null(request.AuthRequestAccessCode);
        }
        else
        {
            Assert.Equal(secret, request.AuthRequestAccessCode);
            Assert.Null(request.Otp);
        }
    }

    [Fact]
    public async Task OtpVerification_DoesNotRequirePersistedKdfMaterial()
    {
        var fixture = CreateFixture(persistSessionForService: false);

        await fixture.Service.GetWebAuthnAsync(
            TwoFactorUserVerification.FromOtp("123456"),
            TestContext.Current.CancellationToken);

        Assert.Equal("123456", fixture.Api.LastGetWebAuthn?.Otp);
    }

    [Fact]
    public void VerificationUnion_DoesNotExposeSecretThroughToString()
    {
        const string Secret = "do-not-log-this";
        var verification = TwoFactorUserVerification.FromAuthRequestAccessCode(Secret);

        Assert.Equal(TwoFactorUserVerificationKind.AuthRequestAccessCode, verification.Kind);
        Assert.DoesNotContain(Secret, verification.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestProtectedActionOtp_UsesAuthenticatedApiEndpoint()
    {
        var fixture = CreateFixture();

        await fixture.Service.RequestProtectedActionOtpAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Api.RequestProtectedActionOtpCalls);
    }

    [Fact]
    public async Task GetWebAuthn_PreservesExistingUnnamedCredentialAsEmptyName()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(1, string.Empty, true));

        var details = await fixture.Service.GetWebAuthnAsync(
            MasterPassword,
            TestContext.Current.CancellationToken);

        var credential = Assert.Single(details.Credentials);
        Assert.Equal(string.Empty, credential.Name);
        Assert.True(credential.Migrated);
    }

    [Fact]
    public async Task GetChallenge_UsesVerificationTokenAndBindsCurrentCredentialIds()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(1, "Existing", false)) with
        {
            UserVerificationToken = "uv-token",
        };
        fixture.Api.WebAuthnChallengeResponse = new WebAuthnChallengeResponse(
            CreateChallengeOptions());

        var challenge = await fixture.Service.GetWebAuthnChallengeAsync(
            MasterPassword,
            TestContext.Current.CancellationToken);

        Assert.Equal("uv-token", challenge.UserVerificationToken);
        Assert.Null(fixture.Api.LastGetWebAuthnChallenge?.MasterPasswordHash);
        Assert.Null(fixture.Api.LastGetWebAuthnChallenge?.Otp);
        Assert.Null(fixture.Api.LastGetWebAuthnChallenge?.AuthRequestAccessCode);
        Assert.Equal("uv-token", fixture.Api.LastGetWebAuthnChallenge?.UserVerificationToken);

        var error = await Assert.ThrowsAsync<TwoFactorOperationException>(() =>
            fixture.Service.CompleteWebAuthnRegistrationAsync(
                MasterPassword,
                1,
                "Duplicate slot",
                challenge,
                CreateDeviceResponse(challenge),
                TestContext.Current.CancellationToken));
        Assert.Contains("already in use", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Api.EnableWebAuthnCalls);
    }

    [Fact]
    public async Task VaultwardenChallengeAndCreate_ReuseOnlyProtectedActionOtp()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(false);
        fixture.Api.WebAuthnChallengeResponse = new WebAuthnChallengeResponse(
            CreateChallengeOptions());
        var verification = TwoFactorUserVerification.FromOtp("123456");
        var challenge = await fixture.Service.GetWebAuthnChallengeAsync(
            verification,
            TestContext.Current.CancellationToken);
        fixture.Api.EnableWebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(1, "OTP key", false));

        var outcome = await fixture.Service.CompleteWebAuthnRegistrationAsync(
            verification,
            1,
            "OTP key",
            challenge,
            CreateDeviceResponse(challenge),
            TestContext.Current.CancellationToken);

        Assert.IsType<WebAuthnRegistrationOutcome.Confirmed>(outcome);
        Assert.Equal("123456", fixture.Api.LastGetWebAuthn?.Otp);
        Assert.Equal("123456", fixture.Api.LastGetWebAuthnChallenge?.Otp);
        Assert.Null(fixture.Api.LastGetWebAuthnChallenge?.MasterPasswordHash);
        var request = Assert.IsType<EnableWebAuthnRequest>(fixture.Api.LastEnableWebAuthn);
        Assert.Equal("123456", request.Otp);
        Assert.Null(request.MasterPasswordHash);
        Assert.Null(request.AuthRequestAccessCode);
        Assert.Null(request.UserVerificationToken);
    }

    [Fact]
    public async Task OfficialChallenge_ExchangesAuthRequestAccessCodeForTokenOnlyContinuation()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(false) with
        {
            UserVerificationToken = "uv-token",
        };
        fixture.Api.WebAuthnChallengeResponse = new WebAuthnChallengeResponse(
            CreateChallengeOptions());

        await fixture.Service.GetWebAuthnChallengeAsync(
            TwoFactorUserVerification.FromAuthRequestAccessCode("access-code"),
            TestContext.Current.CancellationToken);

        Assert.Equal("access-code", fixture.Api.LastGetWebAuthn?.AuthRequestAccessCode);
        Assert.Null(fixture.Api.LastGetWebAuthn?.MasterPasswordHash);
        Assert.Null(fixture.Api.LastGetWebAuthn?.Otp);
        Assert.Equal("uv-token", fixture.Api.LastGetWebAuthnChallenge?.UserVerificationToken);
        Assert.Null(fixture.Api.LastGetWebAuthnChallenge?.AuthRequestAccessCode);
        Assert.Null(fixture.Api.LastGetWebAuthnChallenge?.MasterPasswordHash);
        Assert.Null(fixture.Api.LastGetWebAuthnChallenge?.Otp);
    }

    [Fact]
    public async Task CompleteRegistration_SubmitsCanonicalAttestationOnce()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(false) with
        {
            UserVerificationToken = "uv-token",
        };
        fixture.Api.WebAuthnChallengeResponse = new WebAuthnChallengeResponse(
            CreateChallengeOptions());
        var challenge = await fixture.Service.GetWebAuthnChallengeAsync(
            MasterPassword,
            TestContext.Current.CancellationToken);
        var deviceResponse = CreateDeviceResponse(challenge);
        fixture.Api.EnableWebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(1, "Windows Hello", false));

        var outcome = await fixture.Service.CompleteWebAuthnRegistrationAsync(
            MasterPassword,
            1,
            "  Windows Hello  ",
            challenge,
            deviceResponse,
            TestContext.Current.CancellationToken);

        var confirmed = Assert.IsType<WebAuthnRegistrationOutcome.Confirmed>(outcome);
        Assert.False(confirmed.Reconciled);
        Assert.True(challenge.IsConsumed);
        Assert.Equal(1, fixture.Api.EnableWebAuthnCalls);
        var request = Assert.IsType<EnableWebAuthnRequest>(fixture.Api.LastEnableWebAuthn);
        Assert.Equal("Windows Hello", request.Name);
        Assert.Null(request.MasterPasswordHash);
        Assert.Null(request.Otp);
        Assert.Null(request.AuthRequestAccessCode);
        Assert.Equal("uv-token", request.UserVerificationToken);
        Assert.Equal(Encode(deviceResponse.CredentialId), request.DeviceResponse.Id);
        Assert.Equal(request.DeviceResponse.Id, request.DeviceResponse.RawId);
        Assert.Equal(Encode(deviceResponse.AttestationObject), request.DeviceResponse.Response.AttestationObject);
        Assert.Equal(Encode(deviceResponse.ClientDataJson), request.DeviceResponse.Response.ClientDataJson);

        await Assert.ThrowsAsync<TwoFactorOperationException>(() =>
            fixture.Service.CompleteWebAuthnRegistrationAsync(
                MasterPassword,
                1,
                "Windows Hello",
                challenge,
                deviceResponse,
                TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Api.EnableWebAuthnCalls);
    }

    [Fact]
    public async Task CompleteRegistration_TransportFailureReconcilesWithoutReplayingAttestation()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(false);
        fixture.Api.WebAuthnChallengeResponse = new WebAuthnChallengeResponse(
            CreateChallengeOptions());
        var challenge = await fixture.Service.GetWebAuthnChallengeAsync(
            MasterPassword,
            TestContext.Current.CancellationToken);
        fixture.Api.EnableWebAuthnException = new HttpRequestException("response lost");
        fixture.Api.WebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(1, "Security key", false));

        var outcome = await fixture.Service.CompleteWebAuthnRegistrationAsync(
            MasterPassword,
            1,
            "Security key",
            challenge,
            CreateDeviceResponse(challenge),
            TestContext.Current.CancellationToken);

        var confirmed = Assert.IsType<WebAuthnRegistrationOutcome.Confirmed>(outcome);
        Assert.True(confirmed.Reconciled);
        Assert.Equal(1, fixture.Api.EnableWebAuthnCalls);
        Assert.Equal(2, fixture.Api.GetWebAuthnCalls);
    }

    [Fact]
    public async Task CompleteRegistration_DirectRequestTimeoutReconcilesWithoutReplay()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(false);
        fixture.Api.WebAuthnChallengeResponse = new WebAuthnChallengeResponse(
            CreateChallengeOptions());
        var challenge = await fixture.Service.GetWebAuthnChallengeAsync(
            MasterPassword,
            TestContext.Current.CancellationToken);
        fixture.Api.EnableWebAuthnException = new RequestTimeoutException(
            RequestTimeoutPhase.ControlPlane,
            TimeSpan.FromSeconds(1));
        fixture.Api.WebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(1, "Security key", false));

        var outcome = await fixture.Service.CompleteWebAuthnRegistrationAsync(
            MasterPassword,
            1,
            "Security key",
            challenge,
            CreateDeviceResponse(challenge),
            TestContext.Current.CancellationToken);

        Assert.True(Assert.IsType<WebAuthnRegistrationOutcome.Confirmed>(outcome).Reconciled);
        Assert.Equal(1, fixture.Api.EnableWebAuthnCalls);
    }

    [Fact]
    public async Task CompleteRegistration_UnresolvedFailureIsUncertainAndChallengeCannotBeReused()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(false);
        fixture.Api.WebAuthnChallengeResponse = new WebAuthnChallengeResponse(
            CreateChallengeOptions());
        var challenge = await fixture.Service.GetWebAuthnChallengeAsync(
            MasterPassword,
            TestContext.Current.CancellationToken);
        fixture.Api.EnableWebAuthnException = new HttpRequestException("response lost");

        var outcome = await fixture.Service.CompleteWebAuthnRegistrationAsync(
            MasterPassword,
            1,
            "Security key",
            challenge,
            CreateDeviceResponse(challenge),
            TestContext.Current.CancellationToken);

        Assert.IsType<WebAuthnRegistrationOutcome.OutcomeUncertain>(outcome);
        await Assert.ThrowsAsync<TwoFactorOperationException>(() =>
            fixture.Service.CompleteWebAuthnRegistrationAsync(
                MasterPassword,
                1,
                "Security key",
                challenge,
                CreateDeviceResponse(challenge),
                TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Api.EnableWebAuthnCalls);
    }

    [Fact]
    public async Task CompleteRegistration_DefinitiveRejectionStillConsumesChallenge()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(false);
        fixture.Api.WebAuthnChallengeResponse = new WebAuthnChallengeResponse(
            CreateChallengeOptions());
        var challenge = await fixture.Service.GetWebAuthnChallengeAsync(
            MasterPassword,
            TestContext.Current.CancellationToken);
        fixture.Api.EnableWebAuthnException = new VaultWriteException(
            HttpStatusCode.BadRequest,
            "invalid attestation",
            null);

        await Assert.ThrowsAsync<VaultWriteException>(() =>
            fixture.Service.CompleteWebAuthnRegistrationAsync(
                MasterPassword,
                1,
                "Security key",
                challenge,
                CreateDeviceResponse(challenge),
                TestContext.Current.CancellationToken));

        Assert.True(challenge.IsConsumed);
        Assert.Equal(1, fixture.Api.EnableWebAuthnCalls);
        Assert.Equal(1, fixture.Api.GetWebAuthnCalls);
    }

    [Fact]
    public async Task DeleteLastVaultwardenCredential_DeletesCredentialThenDisablesProvider()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(7, "Only key", false));
        fixture.Api.DeleteWebAuthnResponse = Details(true);

        var details = await fixture.Service.DeleteWebAuthnCredentialAsync(
            MasterPassword,
            7,
            TestContext.Current.CancellationToken);

        Assert.False(details.Enabled);
        Assert.Empty(details.Credentials);
        Assert.Equal(1, fixture.Api.DeleteWebAuthnCalls);
        Assert.Equal(1, fixture.Api.DisableCalls);
        Assert.Equal(7, fixture.Api.LastDeleteWebAuthn?.Id);
        Assert.Equal(fixture.MasterPasswordHash, fixture.Api.LastDeleteWebAuthn?.MasterPasswordHash);
        Assert.Equal((int)TwoFactorProviderType.WebAuthn, fixture.Api.LastDisable?.Type);
        Assert.Equal(fixture.MasterPasswordHash, fixture.Api.LastDisable?.MasterPasswordHash);
    }

    [Fact]
    public async Task DeleteLastOfficialCredential_UsesAtomicDeleteAllWithVerificationToken()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(7, "Only key", false)) with
        {
            UserVerificationToken = "uv-token",
        };

        var details = await fixture.Service.DeleteWebAuthnCredentialAsync(
            MasterPassword,
            7,
            TestContext.Current.CancellationToken);

        Assert.False(details.Enabled);
        Assert.Empty(details.Credentials);
        Assert.Equal(1, fixture.Api.DeleteAllWebAuthnCalls);
        Assert.Equal("uv-token", fixture.Api.LastDeleteAllWebAuthn?.UserVerificationToken);
        Assert.Equal(0, fixture.Api.DeleteWebAuthnCalls);
        Assert.Equal(0, fixture.Api.DisableCalls);
    }

    [Fact]
    public async Task DeleteLastOfficialCredential_TimeoutReconcilesWithoutFallbackWrites()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponses.Enqueue(Details(
            true,
            new WebAuthnCredentialItem(7, "Only key", false)) with
        {
            UserVerificationToken = "uv-token",
        });
        fixture.Api.WebAuthnResponses.Enqueue(Details(false));
        fixture.Api.DeleteAllWebAuthnException = new HttpRequestException("response lost");

        var details = await fixture.Service.DeleteWebAuthnCredentialAsync(
            MasterPassword,
            7,
            TestContext.Current.CancellationToken);

        Assert.False(details.Enabled);
        Assert.Empty(details.Credentials);
        Assert.Equal(1, fixture.Api.DeleteAllWebAuthnCalls);
        Assert.Equal(0, fixture.Api.DeleteWebAuthnCalls);
        Assert.Equal(0, fixture.Api.DisableCalls);
        Assert.Equal(2, fixture.Api.GetWebAuthnCalls);
    }

    [Fact]
    public async Task DeleteLastVaultwardenCredential_TimeoutReconcilesThenDisables()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponses.Enqueue(Details(
            true,
            new WebAuthnCredentialItem(7, "Only key", true)));
        fixture.Api.WebAuthnResponses.Enqueue(Details(true));
        fixture.Api.DeleteWebAuthnException = new HttpRequestException("response lost");

        var details = await fixture.Service.DeleteWebAuthnCredentialAsync(
            MasterPassword,
            7,
            TestContext.Current.CancellationToken);

        Assert.False(details.Enabled);
        Assert.Empty(details.Credentials);
        Assert.Equal(1, fixture.Api.DeleteWebAuthnCalls);
        Assert.Equal(1, fixture.Api.DisableCalls);
        Assert.Equal(2, fixture.Api.GetWebAuthnCalls);
    }

    [Fact]
    public async Task DeleteLastVaultwardenCredential_DisableTimeoutReconcilesFinalState()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponses.Enqueue(Details(
            true,
            new WebAuthnCredentialItem(7, "Only key", true)));
        fixture.Api.WebAuthnResponses.Enqueue(Details(false));
        fixture.Api.DeleteWebAuthnResponse = Details(true);
        fixture.Api.DisableException = new HttpRequestException("response lost");

        var details = await fixture.Service.DeleteWebAuthnCredentialAsync(
            MasterPassword,
            7,
            TestContext.Current.CancellationToken);

        Assert.False(details.Enabled);
        Assert.Empty(details.Credentials);
        Assert.Equal(1, fixture.Api.DeleteWebAuthnCalls);
        Assert.Equal(1, fixture.Api.DisableCalls);
        Assert.Equal(2, fixture.Api.GetWebAuthnCalls);
    }

    [Fact]
    public async Task DeleteCredential_WithRemainingKeyUsesPerCredentialEndpoint()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(1, "First", false),
            new WebAuthnCredentialItem(2, "Second", false));
        fixture.Api.DeleteWebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(2, "Second", false));

        var details = await fixture.Service.DeleteWebAuthnCredentialAsync(
            MasterPassword,
            1,
            TestContext.Current.CancellationToken);

        Assert.True(details.Enabled);
        Assert.Equal(2, Assert.Single(details.Credentials).Id);
        Assert.Equal(1, fixture.Api.DeleteWebAuthnCalls);
        Assert.Equal(0, fixture.Api.DisableCalls);
        Assert.Equal(1, fixture.Api.LastDeleteWebAuthn?.Id);
        Assert.Equal(fixture.MasterPasswordHash, fixture.Api.LastDeleteWebAuthn?.MasterPasswordHash);
    }

    [Fact]
    public async Task DeleteCredential_WithOtpSendsNoPasswordOrAccessCode()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(1, "First", false),
            new WebAuthnCredentialItem(2, "Second", false));
        fixture.Api.DeleteWebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(2, "Second", false));

        await fixture.Service.DeleteWebAuthnCredentialAsync(
            TwoFactorUserVerification.FromOtp("654321"),
            1,
            TestContext.Current.CancellationToken);

        var request = Assert.IsType<DeleteWebAuthnCredentialRequest>(fixture.Api.LastDeleteWebAuthn);
        Assert.Equal("654321", request.Otp);
        Assert.Null(request.MasterPasswordHash);
        Assert.Null(request.AuthRequestAccessCode);
        Assert.Null(request.UserVerificationToken);
    }

    [Fact]
    public async Task DeleteCredential_ServerLeavesEnabledEmptyProvider_DisablesIt()
    {
        var fixture = CreateFixture();
        fixture.Api.WebAuthnResponse = Details(
            true,
            new WebAuthnCredentialItem(1, "First", false),
            new WebAuthnCredentialItem(2, "Second", false));
        fixture.Api.DeleteWebAuthnResponse = Details(true);

        var details = await fixture.Service.DeleteWebAuthnCredentialAsync(
            MasterPassword,
            1,
            TestContext.Current.CancellationToken);

        Assert.False(details.Enabled);
        Assert.Empty(details.Credentials);
        Assert.Equal(1, fixture.Api.DeleteWebAuthnCalls);
        Assert.Equal(1, fixture.Api.DisableCalls);
    }

    private static TestFixture CreateFixture(bool persistSessionForService = true)
    {
        var crypto = new CryptoService();
        var masterKey = crypto.DeriveMasterKey(
            MasterPassword,
            Email,
            KdfType.Pbkdf2,
            Iterations,
            null,
            null);
        string masterPasswordHash;
        try
        {
            masterPasswordHash = crypto.ComputeMasterPasswordHash(masterKey, MasterPassword);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }

        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            Email,
            new ServerOrigin("https://vault.example"),
            1,
            Email,
            "device-id",
            "refresh-token",
            "placeholder-protected-user-key",
            KdfType.Pbkdf2,
            Iterations,
            null,
            null,
            Email));
        var session = new VaultSession();
        Assert.True(store.TryLoad(out var persisted));
        session.RestorePersistedSession(persisted);
        var api = new FakeTwoFactorApiClient();
        var service = new TwoFactorService(
            crypto,
            FakeVaultApiClientProvider.ForAuthenticated<ITwoFactorApiClient>(api),
            session,
            persistSessionForService ? store : new MemoryTokenStore());
        return new TestFixture(service, api, masterPasswordHash);
    }

    private static WebAuthnTwoFactorResponse Details(
        bool enabled,
        params WebAuthnCredentialItem[] credentials) =>
        new(enabled, credentials);

    private static WebAuthnRegistrationDeviceResponse CreateDeviceResponse(
        WebAuthnRegistrationChallenge challenge,
        string? challengeOverride = null)
    {
        var clientDataJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "webauthn.create",
            challenge = challengeOverride ?? challenge.Challenge,
            origin = challenge.Origin,
            crossOrigin = false,
        });
        return new WebAuthnRegistrationDeviceResponse(
            [9, 8, 7, 6],
            [0xA3, 0x01, 0x02],
            clientDataJson,
            ["internal"]);
    }

    private static JsonElement CreateChallengeOptions(
        string rpId = "vault.example",
        int timeout = 60_000,
        IReadOnlyList<string>? transports = null,
        JsonObject? extensions = null)
    {
        var challenge = Encode(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var userId = Encode(Enumerable.Range(33, 16).Select(value => (byte)value).ToArray());
        var excludedId = Encode([1, 3, 5, 7]);
        var transportNodes = (transports ?? ["usb"])
            .Select(static value => (JsonNode?)JsonValue.Create(value))
            .ToArray();
        var root = new JsonObject
        {
            ["challenge"] = challenge,
            ["rp"] = new JsonObject
            {
                ["id"] = rpId,
                ["name"] = "Vault",
            },
            ["user"] = new JsonObject
            {
                ["id"] = userId,
                ["name"] = Email,
                ["displayName"] = "Test User",
            },
            ["pubKeyCredParams"] = new JsonArray
            {
                new JsonObject { ["type"] = "public-key", ["alg"] = -7 },
                new JsonObject { ["type"] = "public-key", ["alg"] = -257 },
            },
            ["excludeCredentials"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "public-key",
                    ["id"] = excludedId,
                    ["transports"] = new JsonArray(transportNodes),
                },
            },
            ["timeout"] = timeout,
            ["authenticatorSelection"] = new JsonObject
            {
                ["authenticatorAttachment"] = "cross-platform",
                ["residentKey"] = "discouraged",
                ["requireResidentKey"] = false,
                ["userVerification"] = "discouraged",
            },
            ["attestation"] = "none",
            ["extensions"] = extensions ?? new JsonObject(),
            ["status"] = "ok",
            ["errorMessage"] = "",
        };

        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    private static string Encode(byte[] value) =>
        WebAuthnCrypto.EncodeBase64Url(value);

    private sealed record TestFixture(
        TwoFactorService Service,
        FakeTwoFactorApiClient Api,
        string MasterPasswordHash);
}
