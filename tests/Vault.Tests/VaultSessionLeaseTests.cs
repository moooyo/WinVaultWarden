using Core.Models;
using Core.Session;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class VaultSessionLeaseTests
{
    private static readonly ServerOrigin A = new("https://a.example");
    private static readonly ServerOrigin B = new("https://b.example:8443/path");

    [Fact]
    public void Lease_HasNoPublicConstructor()
    {
        Assert.Empty(typeof(VaultSessionLease).GetConstructors());
    }

    [Fact]
    public void Lock_CancelsOldLeaseAndAdvancesGenerationBeforeMutation()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);
        var mutationRan = false;

        var lockedLease = session.Lock();

        Assert.NotNull(lockedLease);
        Assert.True(lease.CancellationToken.IsCancellationRequested);
        Assert.True(lockedLease!.Generation > lease.Generation);
        Assert.False(session.TryCommit(lease, () => mutationRan = true));
        Assert.False(mutationRan);
    }

    [Fact]
    public void AccountSwitch_InvalidatesOldStampEvenWhenIdentityTextRepeats()
    {
        var session = new VaultSession();
        var first = session.BeginAccountSession("same-id", "a@example.com", A);
        var second = session.BeginAccountSession("same-id", "a@example.com", A);

        Assert.True(second.Generation > first.Generation);
        Assert.False(session.IsCurrent(first));
        Assert.True(session.IsCurrent(second));
    }

    [Fact]
    public void SwitchToB_RejectsOldACommit()
    {
        var session = new VaultSession();
        var a = session.BeginAccountSession("account-a", "a@example.com", A);
        var b = session.BeginAccountSession("account-b", "b@example.com", B);
        var committed = false;

        Assert.False(session.TryCommit(a, () => committed = true));
        Assert.False(committed);
        Assert.Equal("account-b", b.AccountId);
        Assert.Equal(B, b.Origin);
    }

    [Fact]
    public void Logout_InvalidatesBeforeExternalClearCanObserveSession()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);

        session.Logout();

        Assert.True(lease.CancellationToken.IsCancellationRequested);
        Assert.False(session.IsCurrent(lease));
        Assert.Equal(VaultState.LoggedOut, session.State);
    }

    [Fact]
    public void CaptureLease_NoActiveAccount_Throws()
    {
        var session = new VaultSession();

        Assert.Throws<InvalidOperationException>(() => session.CaptureLease());
    }

    [Fact]
    public void CaptureLease_AfterBeginAccountSession_MatchesIssuedLease()
    {
        var session = new VaultSession();
        var issued = session.BeginAccountSession("account-a", "a@example.com", A);

        var captured = session.CaptureLease();

        Assert.True(session.IsCurrent(captured));
        Assert.Equal(issued.Generation, captured.Generation);
    }

    [Fact]
    public void TryCopyUserKey_StaleLease_ReturnsFalseAndNullKey()
    {
        var session = new VaultSession();
        var stale = session.BeginAccountSession("account-a", "a@example.com", A);
        session.Lock(); // 轮换 generation,stale 失效

        var ok = session.TryCopyUserKey(stale, out var key);

        Assert.False(ok);
        Assert.Null(key);
    }

    [Fact]
    public void TryCopyUserKey_CurrentLease_ReturnsIndependentCopyNotSameInstance()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);
        var original = new Crypto.SymmetricCryptoKey(Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        Assert.True(session.TryCommit(lease, () => session.SetUnlockedKey(original)));

        var ok = session.TryCopyUserKey(lease, out var copy);

        Assert.True(ok);
        Assert.NotNull(copy);
        Assert.NotSame(original, copy);
        Assert.Equal(original.FullKey.ToArray(), copy!.FullKey.ToArray());
    }

    [Fact]
    public void GetAccessTokenBinding_StaleLease_ReturnsNull()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);
        Assert.True(session.TryCommit(lease, () => session.SetTokens("access-1", "refresh-1")));
        session.Lock(); // 轮换 generation,lease 失效

        var binding = session.GetAccessTokenBinding(lease);

        Assert.Null(binding);
    }

    [Fact]
    public void GetAccessTokenBinding_CurrentLease_BindsAccountOriginGeneration()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);
        Assert.True(session.TryCommit(lease, () => session.SetTokens("access-1", "refresh-1")));

        var binding = session.GetAccessTokenBinding(lease);

        Assert.NotNull(binding);
        Assert.Equal("account-a", binding!.AccountId);
        Assert.Equal(A, binding.Origin);
        Assert.Equal(lease.Generation, binding.Generation);
        Assert.Equal("access-1", binding.Token);
    }

    [Fact]
    public void TryGetTokenSnapshot_StaleLease_ReturnsFalseAndNullTokens()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);
        Assert.True(session.TryCommit(lease, () => session.SetTokens("access-1", "refresh-1")));
        session.Logout(); // 轮换 generation(账户置空),lease 失效

        var ok = session.TryGetTokenSnapshot(lease, out var access, out var refresh);

        Assert.False(ok);
        Assert.Null(access);
        Assert.Null(refresh);
    }

    [Fact]
    public void TryGetTokenSnapshot_CurrentLease_ReturnsTokens()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);
        Assert.True(session.TryCommit(lease, () => session.SetTokens("access-1", "refresh-1")));

        var ok = session.TryGetTokenSnapshot(lease, out var access, out var refresh);

        Assert.True(ok);
        Assert.Equal("access-1", access);
        Assert.Equal("refresh-1", refresh);
    }

    [Fact]
    public void RestorePersistedSession_MismatchedGeneration_RotatesAndInvalidatesInMemoryLease()
    {
        var session = new VaultSession();
        var live = session.BeginAccountSession("account-a", "a@example.com", A);
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            A,
            live.Generation + 5, // 磁盘记录的 generation 比内存新,模拟另一进程/更早持久化写入的记录
            "a@example.com",
            "device-1",
            "refresh-token",
            "2.protected-user-key",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        var restored = session.RestorePersistedSession(persisted);

        Assert.False(session.IsCurrent(live));
        Assert.True(session.IsCurrent(restored));
        Assert.True(restored.Generation > live.Generation);
        Assert.Equal(VaultState.Locked, session.State);
    }

    [Fact]
    public void RestorePersistedSession_SameIdentityAndGeneration_ReusesCurrentLease()
    {
        var session = new VaultSession();
        var issued = session.BeginAccountSession("account-a", "a@example.com", A);
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            A,
            issued.Generation,
            "a@example.com",
            "device-1",
            "refresh-token",
            "2.protected-user-key",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        var restored = session.RestorePersistedSession(persisted);

        Assert.True(session.IsCurrent(issued));
        Assert.True(session.IsCurrent(restored));
        Assert.Equal(issued.Generation, restored.Generation);
    }

    [Fact]
    public void RestorePersistedSession_InvalidBinding_Throws()
    {
        var session = new VaultSession();
        // Obsolete 过渡构造产生的记录 generation 固定为 1,但 schema/accountId 均合法,
        // 因此用显式构造一个 generation<=0 的非法记录来验证 fail-closed 门禁。
        var invalid = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            A,
            0, // Generation<=0 → HasValidBinding=false
            "a@example.com",
            "device-1",
            "refresh-token",
            "2.protected-user-key",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        Assert.Throws<InvalidDataException>(() => session.RestorePersistedSession(invalid));
    }

    [Fact]
    public void ConcurrentTryCommit_SerializesMutationsWithoutLostUpdates()
    {
        // 互斥性压测:同一仍当前的 lease 被多线程并发 TryCommit,mutation 内部做
        // 非原子的读-加一-写。若 TryCommit 未把校验与 mutation 关在同一把锁内,
        // 就会出现"丢失更新"(counter 最终小于迭代次数)。
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);
        var counter = 0;
        const int iterations = 2000;

        Parallel.For(0, iterations, _ =>
        {
            session.TryCommit(lease, () =>
            {
                var local = counter;
                local++;
                counter = local;
            });
        });

        Assert.Equal(iterations, counter);
    }

    [Fact]
    public void ConcurrentLockDuringTryCommit_MutationNeverObservesTornLockedState()
    {
        // 撕裂检测:Lock() 的"轮换 generation + 清空敏感状态 + 置 Locked"必须与 TryCommit 的
        // "校验 lease 当前性 + 执行 mutation"共享同一把 _gate。若二者可交错,mutation 内部
        // 可能读到 State 已经被并发 Lock 置为 Locked 的撕裂态(mutation 本不该被判定为当前
        // 却仍执行)。
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);
        var sawTornState = false;

        Parallel.For(0, 1000, i =>
        {
            if (i % 2 == 0)
            {
                session.TryCommit(lease, () =>
                {
                    if (session.State == VaultState.Locked)
                        sawTornState = true;
                });
            }
            else
            {
                session.Lock();
            }
        });

        Assert.False(sawTornState);
    }
}

