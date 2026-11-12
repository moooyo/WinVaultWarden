using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Enums;
using Core.Models;
using Crypto;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class AuthRequestPendingLookupTests
{
    [Fact]
    public async Task SingleLookup_ValidatesPendingStateAndDerivesTheReviewedFingerprint()
    {
        using var rsa = RSA.Create(2048);
        using var key = new SymmetricCryptoKey(new byte[64]);
        var session = new VaultSession();
        var lease = session.BeginAccountSession("recipient", "recipient@example.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(key);
        var response = Response(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
        var api = new FakeAuthRequestApiClient { GetResult = response };
        var fingerprint = new FingerprintService();
        var service = Service(api, session, lease, fingerprint);

        var result = await service.GetPendingRequestAsync(response.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(response.Id, result.Id);
        Assert.Equal(fingerprint.GetFingerprintPhrase(rsa.ExportSubjectPublicKeyInfo(), "recipient@example.com"), result.FingerprintPhrase);
        Assert.Equal(new[] { "get" }, api.Calls);
        session.Lock();
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("denied")]
    [InlineData("responded")]
    [InlineData("key")]
    [InlineData("hash")]
    [InlineData("id")]
    [InlineData("invalid-key")]
    public async Task SingleLookup_RejectsProcessedOrReplacedRequests(string mutation)
    {
        using var rsa = RSA.Create(2048);
        using var key = new SymmetricCryptoKey(new byte[64]);
        var session = new VaultSession();
        var lease = session.BeginAccountSession("recipient", "recipient@example.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(key);
        var original = Response(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
        var response = mutation switch
        {
            "approved" => original with { RequestApproved = true },
            "denied" => original with { RequestApproved = false },
            "responded" => original with { ResponseDate = DateTimeOffset.UtcNow.ToString("O") },
            "key" => original with { Key = "already-encrypted-key" },
            "hash" => original with { MasterPasswordHash = "already-populated" },
            "id" => original with { Id = "another-request" },
            "invalid-key" => original with { PublicKey = "not-a-public-key" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var api = new FakeAuthRequestApiClient { GetResult = response };
        var service = Service(api, session, lease, new FingerprintService());

        Assert.Null(await service.GetPendingRequestAsync(original.Id, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("approve", api.Calls);
        session.Lock();
    }

    private static AuthRequestService Service(FakeAuthRequestApiClient api, VaultSession session,
        Core.Session.VaultSessionLease lease, FingerprintService fingerprint)
    {
        var store = new MemoryTokenStore();
        store.Save(new PersistedSession(PersistedSession.CurrentSchemaVersion, lease.AccountId,
            lease.Endpoints, lease.Generation, "recipient@example.com", "device", "refresh",
            "2.iv|ciphertext|mac", KdfType.Pbkdf2, 600000, null, null));
        return new(FakeVaultApiClientProvider.ForAuthenticated<IAuthRequestApiClient>(api),
            new CryptoService(), session, store, fingerprint);
    }

    private static AuthRequestResponse Response(string key) => new(
        "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", key, "Windows", "192.0.2.1", null, null,
        DateTimeOffset.UtcNow.ToString("O"), null, null, "https://vault.example");
}
