using System.Security.Cryptography;
using Core.Enums;
using Core.Models;
using Core.SshAgent;
using Crypto;
using Xunit;

namespace Vault.Tests;

public sealed class SshAgentServiceTests
{
    [Fact]
    public async Task SignRequiresFreshApprovalForEveryUseAndProducesVerifiableSignature()
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportPkcs8PrivateKeyPem());
        var session = NewSession(NewCipher(source, key));
        var approval = new Approval(true);
        var service = new SshAgentService(session, approval);
        using var client = new Client();
        var data = "ssh-user-authentication"u8.ToArray();
        try
        {
            var identities = await service.GetIdentitiesAsync(client);
            Assert.Equal(key.PublicKeyBlob, Assert.Single(identities).PublicKeyBlob);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var signature = await service.SignAsync(client, key.PublicKeyBlob, data, 4);
                Assert.NotNull(signature);
                Assert.True(VerifyRsa(source, data, signature));
            }
            Assert.Equal(2, approval.Calls);
            Assert.Equal(key.Fingerprint, approval.Last!.Fingerprint);
            Assert.Equal(client.Info, approval.Last.Client);
            Assert.Equal(data.Length, approval.Last.DataLength);
        }
        finally { session.Lock(); }
    }

    [Fact]
    public async Task DeniedApprovalAndUnknownKeyCannotProduceSignature()
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportPkcs8PrivateKeyPem());
        var session = NewSession(NewCipher(source, key));
        var approval = new Approval(false);
        var service = new SshAgentService(session, approval);
        using var client = new Client();
        try
        {
            Assert.Null(await service.SignAsync(client, [9], [1], 4));
            Assert.Equal(0, approval.Calls);
            Assert.Null(await service.SignAsync(client, key.PublicKeyBlob, [1], 4));
            Assert.Equal(1, approval.Calls);
        }
        finally { session.Lock(); }
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(6u)]
    [InlineData(uint.MaxValue)]
    public async Task WeakOrUnknownRsaFlagsAreRejectedBeforeApproval(uint flags)
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportPkcs8PrivateKeyPem());
        var session = NewSession(NewCipher(source, key));
        var approval = new Approval(true);
        using var client = new Client();
        try
        {
            Assert.Null(await new SshAgentService(session, approval).SignAsync(client, key.PublicKeyBlob, [1], flags));
            Assert.Equal(0, approval.Calls);
        }
        finally { session.Lock(); }
    }

    [Fact]
    public async Task LockDuringApprovalCancelsSigningAndNoKeysRemainAdvertised()
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportPkcs8PrivateKeyPem());
        var session = NewSession(NewCipher(source, key));
        var approval = new BlockingApproval();
        var service = new SshAgentService(session, approval);
        using var client = new Client();
        var pending = service.SignAsync(client, key.PublicKeyBlob, [1], 4);
        await approval.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Lock();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(await service.GetIdentitiesAsync(client));
    }

    [Fact]
    public async Task ProcessExitOrKeyReplacementDuringApprovalInvalidatesGrant()
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportPkcs8PrivateKeyPem());
        var cipher = NewCipher(source, key);
        var session = NewSession(cipher);
        using var client = new Client();
        try
        {
            var exited = new Approval(true) { OnApprove = () => client.Current = false };
            Assert.Null(await new SshAgentService(session, exited).SignAsync(client, key.PublicKeyBlob, [1], 4));
            client.Current = true;
            var changed = new Approval(true)
            {
                OnApprove = () => SetSnapshot(session, [NewCipher(source, key, revision: DateTimeOffset.UtcNow)]),
            };
            Assert.Null(await new SshAgentService(session, changed).SignAsync(client, key.PublicKeyBlob, [1], 4));
        }
        finally { session.Lock(); }
    }

    [Fact]
    public async Task HiddenDeletedArchivedAndMalformedKeysAreNotAdvertised()
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportPkcs8PrivateKeyPem());
        var session = NewSession(
            NewCipher(source, key, "valid"),
            NewCipher(source, key, "hidden", canView: false),
            NewCipher(source, key, "deleted", deleted: true),
            NewCipher(source, key, "archived", archived: true),
            new Cipher { Id = "bad", Type = CipherType.SshKey, Ssh = new("not a key", "ssh-rsa invalid", null) });
        using var client = new Client();
        try
        {
            var identities = await new SshAgentService(session, new Approval(false)).GetIdentitiesAsync(client);
            Assert.Equal("valid", Assert.Single(identities).Comment);
        }
        finally { session.Lock(); }
    }

    [Fact]
    public async Task DuplicatePublicKeysKeepStrongestRepromptPolicy()
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportPkcs8PrivateKeyPem());
        var session = NewSession(NewCipher(source, key, "a"), NewCipher(source, key, "b", reprompt: true));
        var approval = new Approval(true);
        var service = new SshAgentService(session, approval);
        using var client = new Client();
        try
        {
            Assert.Single(await service.GetIdentitiesAsync(client));
            Assert.NotNull(await service.SignAsync(client, key.PublicKeyBlob, [1], 4));
            Assert.True(approval.Last!.RequiresMasterPassword);
        }
        finally { session.Lock(); }
    }

    [Fact]
    public async Task ApprovalDeadlineCancelsUnansweredRequest()
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportPkcs8PrivateKeyPem());
        var session = NewSession(NewCipher(source, key));
        using var client = new Client();
        try
        {
            var service = new SshAgentService(session, new BlockingApproval(), TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SignAsync(client, key.PublicKeyBlob, [1], 4));
        }
        finally { session.Lock(); }
    }

    [Fact]
    public async Task AUiCallbackThatIgnoresCancellationCannotHoldLockCleanupOrSignLate()
    {
        using var source = RSA.Create(2048);
        using var key = SshAgentKey.Parse(source.ExportPkcs8PrivateKeyPem());
        var session = NewSession(NewCipher(source, key));
        using var client = new Client();
        var approval = new UncooperativeApproval();
        var service = new SshAgentService(session, approval);
        var pending = service.SignAsync(client, key.PublicKeyBlob, [1], 4);
        await approval.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Lock();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        approval.Decision.TrySetResult(true);
        Assert.Empty(await service.GetIdentitiesAsync(client));
    }

    private static bool VerifyRsa(RSA source, byte[] data, byte[] signature)
    {
        var reader = new SshAgentReader(signature);
        Assert.Equal("rsa-sha2-512"u8.ToArray(), reader.ReadString(64).ToArray());
        var bytes = reader.ReadString(1_024);
        var verified = source.VerifyData(data, bytes, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
        reader.RequireEnd();
        return verified;
    }

    private static Cipher NewCipher(RSA source, SshAgentKey key, string id = "key",
        bool canView = true, bool deleted = false, bool archived = false, bool reprompt = false,
        DateTimeOffset? revision = null) => new()
    {
        Id = id,
        Name = id,
        Type = CipherType.SshKey,
        Ssh = new(source.ExportPkcs8PrivateKeyPem(), $"ssh-rsa {Convert.ToBase64String(key.PublicKeyBlob)}", key.Fingerprint),
        CanViewPassword = canView,
        DeletedDate = deleted ? DateTimeOffset.UnixEpoch : null,
        ArchivedDate = archived ? DateTimeOffset.UnixEpoch : null,
        Reprompt = reprompt,
        RevisionDate = revision ?? DateTimeOffset.UnixEpoch,
    };

    private static VaultSession NewSession(params Cipher[] ciphers)
    {
        var session = new VaultSession();
        session.BeginAccountSession("ssh-account", "ssh@example.com", new ServerOrigin("https://vault.example"));
        session.SetUnlockedKey(new SymmetricCryptoKey(new byte[64]));
        SetSnapshot(session, ciphers);
        return session;
    }

    private static void SetSnapshot(VaultSession session, Cipher[] ciphers) => session.SetSnapshot(new DecryptedVault(
        new AccountInfo("ssh-account", "ssh@example.com", new ServerOrigin("https://vault.example"), "SSH", string.Empty),
        [], ciphers, 0, 0));

    private sealed class Client : ISshAgentClientIdentity
    {
        public bool Current { get; set; } = true;
        public SshAgentClientInfo Info => new(42, "C:\\Windows\\System32\\OpenSSH\\ssh.exe", DateTimeOffset.UnixEpoch);
        public bool IsCurrent => Current;
        public void Dispose() => Current = false;
    }

    private sealed class Approval(bool result) : ISshAgentApprovalService
    {
        public int Calls { get; private set; }
        public SshAgentApprovalRequest? Last { get; private set; }
        public Action? OnApprove { get; init; }
        public Task<bool> ConfirmAsync(SshAgentApprovalRequest request, CancellationToken ct = default)
        {
            Calls++;
            Last = request;
            OnApprove?.Invoke();
            return Task.FromResult(result);
        }
    }

    private sealed class BlockingApproval : ISshAgentApprovalService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> ConfirmAsync(SshAgentApprovalRequest request, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }
    }

    private sealed class UncooperativeApproval : ISshAgentApprovalService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> ConfirmAsync(SshAgentApprovalRequest request, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            return Decision.Task;
        }
    }
}
