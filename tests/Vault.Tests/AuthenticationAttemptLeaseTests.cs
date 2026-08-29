using Core.Enums;
using Core.Models;
using Core.Session;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class AuthenticationAttemptLeaseTests
{
    [Fact]
    public void Lease_HasNoPublicConstructor()
    {
        Assert.Empty(typeof(AuthenticationAttemptLease).GetConstructors());
    }

    [Fact]
    public void NewAttempt_InvalidatesPreviousAttempt()
    {
        var session = new VaultSession();
        var first = session.BeginAuthenticationAttempt();

        var second = session.BeginAuthenticationAttempt();

        Assert.False(session.IsCurrent(first));
        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.True(session.IsCurrent(second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockOrLogout_InvalidatesAttempt(bool logout)
    {
        var session = new VaultSession();
        var attempt = session.BeginAuthenticationAttempt();

        if (logout)
            session.Logout();
        else
            session.Lock();

        Assert.False(session.IsCurrent(attempt));
        Assert.True(attempt.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void TryBeginAccountSession_PromotesOnlyCurrentAttempt()
    {
        var session = new VaultSession();
        var stale = session.BeginAuthenticationAttempt();
        var current = session.BeginAuthenticationAttempt();

        Assert.False(session.TryBeginAccountSession(
            stale,
            "account-a",
            "a@example.com",
            new ServerOrigin("https://a.example"),
            out _));
        Assert.True(session.TryBeginAccountSession(
            current,
            "account-b",
            "b@example.com",
            new ServerOrigin("https://b.example"),
            out var lease));

        Assert.False(session.IsCurrent(current));
        Assert.True(current.CancellationToken.IsCancellationRequested);
        Assert.True(session.IsCurrent(lease!));
        Assert.Equal("account-b", lease!.AccountId);
    }

    [Fact]
    public void TryRestorePersistedSession_AfterConcurrentLock_CannotRestore()
    {
        var session = new VaultSession();
        var attempt = session.BeginAuthenticationAttempt();
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            new ServerOrigin("https://a.example"),
            1,
            "a@example.com",
            "device-a",
            "refresh-a",
            "2.protected",
            KdfType.Pbkdf2,
            600_000,
            null,
            null);

        session.Lock();

        Assert.False(session.TryRestorePersistedSession(
            attempt,
            persisted,
            out _));
        Assert.Equal(VaultState.Locked, session.State);
        Assert.Equal(AccountInfo.Empty, session.Account);
    }
}
