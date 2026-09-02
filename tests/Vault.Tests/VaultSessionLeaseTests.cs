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
    public void SecurityStamp_FirstCommitMatchesAndSurvivesLock()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);

        Assert.Equal(
            SecurityStampComparison.NoBaseline,
            session.CompareSecurityStamp(lease, "stamp-a"));
        Assert.True(session.TryCommitSecurityStamp(lease, "stamp-a"));
        Assert.Equal(
            SecurityStampComparison.Match,
            session.CompareSecurityStamp(lease, "stamp-a"));
        Assert.Equal(
            SecurityStampComparison.Mismatch,
            session.CompareSecurityStamp(lease, "STAMP-A"));

        var lockedLease = session.Lock()!;

        Assert.Equal(
            SecurityStampComparison.Match,
            session.CompareSecurityStamp(lockedLease, "stamp-a"));
        Assert.Equal(
            SecurityStampComparison.StaleLease,
            session.CompareSecurityStamp(lease, "stamp-a"));
    }

    [Fact]
    public void SecurityStamp_FreshLoginAndLogoutClearBinding()
    {
        var session = new VaultSession();
        var first = session.BeginAccountSession("account-a", "a@example.com", A);
        Assert.True(session.TryCommitSecurityStamp(first, "stamp-a"));

        var freshLogin = session.BeginAccountSession("account-a", "a@example.com", A);
        Assert.Equal(
            SecurityStampComparison.NoBaseline,
            session.CompareSecurityStamp(freshLogin, "stamp-b"));
        Assert.True(session.TryCommitSecurityStamp(freshLogin, "stamp-b"));

        session.Logout();
        var afterLogout = session.BeginAccountSession("account-a", "a@example.com", A);
        Assert.Equal(
            SecurityStampComparison.NoBaseline,
            session.CompareSecurityStamp(afterLogout, "stamp-c"));
    }

    [Fact]
    public void SecurityStamp_RestorePreservesOtherInProcessAccountBinding()
    {
        var session = new VaultSession();
        var endpointsA = ServerEndpoints.FromWebVault(A.Value);
        var leaseA = session.BeginAccountSession("account-a", "a@example.com", endpointsA);
        Assert.True(session.TryCommitSecurityStamp(leaseA, "stamp-a"));
        var leaseB = session.BeginAccountSession("account-b", "b@example.com", B);
        Assert.True(session.TryCommitSecurityStamp(leaseB, "stamp-b"));
        var persistedA = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            endpointsA,
            leaseB.Generation + 1,
            "a@example.com",
            "device-a",
            "refresh-a",
            "2.protected-a",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        var restoredA = session.RestorePersistedSession(persistedA);

        Assert.Equal(
            SecurityStampComparison.Match,
            session.CompareSecurityStamp(restoredA, "stamp-a"));
        Assert.Equal(
            SecurityStampComparison.StaleLease,
            session.CompareSecurityStamp(leaseB, "stamp-b"));
    }

    [Fact]
    public void SecurityStamp_LruKeepsReactivatedAccountAndEvictsLeastRecentBinding()
    {
        var session = new VaultSession();
        var endpoints = Enumerable.Range(0, 6)
            .Select(index => ServerEndpoints.ForSelfHosted($"https://vault-{index}.example"))
            .ToArray();
        VaultSessionLease? latest = null;
        for (var index = 0; index < 5; index++)
        {
            latest = session.BeginAccountSession(
                $"account-{index}",
                $"user-{index}@example.com",
                endpoints[index]);
            Assert.True(session.TryCommitSecurityStamp(latest, $"stamp-{index}"));
        }

        var reactivated = session.RestorePersistedSession(Persisted(
            "account-0",
            endpoints[0],
            latest!.Generation + 1));
        var sixth = session.BeginAccountSession(
            "account-5",
            "user-5@example.com",
            endpoints[5]);
        Assert.True(session.TryCommitSecurityStamp(sixth, "stamp-5"));

        var restoredMostRecent = session.RestorePersistedSession(Persisted(
            "account-0",
            endpoints[0],
            sixth.Generation + 1));
        Assert.Equal(
            SecurityStampComparison.Match,
            session.CompareSecurityStamp(restoredMostRecent, "stamp-0"));

        var restoredEvicted = session.RestorePersistedSession(Persisted(
            "account-1",
            endpoints[1],
            restoredMostRecent.Generation + 1));
        Assert.Equal(
            SecurityStampComparison.NoBaseline,
            session.CompareSecurityStamp(restoredEvicted, "stamp-1"));

        static PersistedSession Persisted(
            string accountId,
            ServerEndpoints serverEndpoints,
            long generation) => new(
                PersistedSession.CurrentSchemaVersion,
                accountId,
                serverEndpoints,
                generation,
                accountId + "@example.com",
                "device-id",
                "refresh-token",
                "2.protected-key",
                Core.Enums.KdfType.Pbkdf2,
                600_000,
                null,
                null);
    }

    [Fact]
    public void IsCurrent_WithPersistedSession_RequiresCompleteEndpointBinding()
    {
        var session = new VaultSession();
        var endpoints = ServerEndpoints.ForSelfHosted("https://same.example/account-a");
        var lease = session.BeginAccountSession(
            "same-account",
            "same@example.com",
            endpoints);
        var matching = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            lease.AccountId,
            lease.Endpoints,
            lease.Generation,
            "same@example.com",
            "device-id",
            "refresh-token",
            "2.protected",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);
        var differentPath = matching with
        {
            Origin = new ServerOrigin("https://same.example"),
            Endpoints = ServerEndpoints.ForSelfHosted("https://same.example/account-b"),
        };

        Assert.True(session.IsCurrent(lease, matching));
        Assert.False(session.IsCurrent(lease, differentPath));
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
    public void TryGetEncryptedPrivateKey_StaleLease_ReturnsFalseAndNoSnapshot()
    {
        var session = new VaultSession();
        var lease = session.BeginAccountSession("account-a", "a@example.com", A);
        session.TryCommit(lease, () =>
        {
            session.SetUnlockedKey(new Crypto.SymmetricCryptoKey(new byte[64]));
            session.SetEncryptedPrivateKey("2.private");
        });
        session.Lock();

        var result = session.TryGetEncryptedPrivateKey(
            lease,
            out var encryptedPrivateKey);

        Assert.False(result);
        Assert.Null(encryptedPrivateKey);
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

    // ── I-1 fix tests: generation 饱和、持久化上界、try/finally 清理 ─────────────────────────

    /// <summary>
    /// long.MaxValue 超过 MaxPersistedGeneration 上界 → HasValidBinding 应为 false。
    /// RED: 当前 HasValidBinding 只检查 Generation > 0，long.MaxValue 通过。
    /// </summary>
    [Fact]
    public void HasValidBinding_MaxValueGeneration_ReturnsFalse()
    {
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            A,
            long.MaxValue,
            "a@example.com",
            "device-1",
            "refresh-token",
            "2.protected-key",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        Assert.False(persisted.HasValidBinding);
        // 常量必须存在且 MaxValue 超出它
        Assert.True(long.MaxValue > PersistedSession.MaxPersistedGeneration);
    }

    /// <summary>
    /// 恢复 Generation=long.MaxValue 的持久化记录 → fail-closed 拒绝（InvalidDataException）。
    /// RED: 当前 HasValidBinding 允许 long.MaxValue，RestorePersistedSession 不抛。
    /// </summary>
    [Fact]
    public void RestorePersistedSession_MaxValueGeneration_ThrowsInvalidDataException()
    {
        var vaultSession = new VaultSession();
        _ = vaultSession.BeginAccountSession("account-a", "a@example.com", A);
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            A,
            long.MaxValue,
            "a@example.com",
            "device-1",
            "refresh-token",
            "2.protected-key",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        Assert.Throws<InvalidDataException>(() => vaultSession.RestorePersistedSession(persisted));
    }

    /// <summary>
    /// 恢复 long.MaxValue-1 后连续两次 Lock/Logout 不得抛出。
    /// RED: 第一次 Lock 后 _generation=MaxValue；第二次 checked(MaxValue+1) 抛 OverflowException。
    /// </summary>
    [Fact]
    public void RestoreMaxMinus1_TwoConsecutiveLocks_DoesNotThrow()
    {
        var vaultSession = new VaultSession();
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            A,
            long.MaxValue - 1,
            "a@example.com",
            "device-1",
            "refresh-token",
            "2.protected-key",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        vaultSession.RestorePersistedSession(persisted); // _generation = MaxValue-1
        vaultSession.Lock();  // _generation = MaxValue  (MaxValue-1+1, 无溢出)
        vaultSession.Lock();  // saturate: MaxValue 保持，不得溢出
    }

    /// <summary>
    /// generation 饱和（long.MaxValue）时 Lock 仍轮换 stamp 与 CTS → 旧 lease 必须失效。
    /// RED: Lock 在 _generation=MaxValue 时抛 OverflowException，旧 lease 根本无法失效。
    /// </summary>
    [Fact]
    public void SaturatedGeneration_Lock_StillRotatesStampAndCts_OldLeaseStale()
    {
        var vaultSession = new VaultSession();
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            A,
            long.MaxValue - 1,
            "a@example.com",
            "device-1",
            "refresh-token",
            "2.protected-key",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        // 将 _generation 推到 long.MaxValue
        vaultSession.RestorePersistedSession(persisted); // gen = MaxValue-1
        vaultSession.Lock();                              // gen = MaxValue (一次正常递增)

        // 在 long.MaxValue 上开启新会话拿到 lease
        var leaseAtMax = vaultSession.BeginAccountSession("account-a", "a@example.com", A);
        Assert.Equal(long.MaxValue, leaseAtMax.Generation);

        // Lock at long.MaxValue: generation 饱和保持 MaxValue，但 stamp/CTS 必须轮换
        vaultSession.Lock();

        Assert.True(leaseAtMax.CancellationToken.IsCancellationRequested, "旧 CTS 未取消");
        Assert.False(vaultSession.IsCurrent(leaseAtMax), "旧 lease 未失效");

        // 再来一轮确认：在 MaxValue 上仍可连续失效
        var lease2 = vaultSession.BeginAccountSession("account-a", "a@example.com", A);
        Assert.Equal(long.MaxValue, lease2.Generation); // generation 仍饱和在 MaxValue
        vaultSession.Lock();
        Assert.True(lease2.CancellationToken.IsCancellationRequested, "第二轮旧 CTS 未取消");
        Assert.False(vaultSession.IsCurrent(lease2), "第二轮旧 lease 未失效");
    }

    /// <summary>
    /// generation 饱和后 Lock 仍必须清除 UserKey/AccessToken/明文集合（try/finally 保证）。
    /// RED: checked 抛 OverflowException 时，ClearSensitiveState 未执行，UserKey 残留。
    /// </summary>
    [Fact]
    public void Lock_AtMaxGeneration_ClearsSensitiveStateViaFinally()
    {
        var vaultSession = new VaultSession();
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            A,
            long.MaxValue - 1,
            "a@example.com",
            "device-1",
            "refresh-token",
            "2.protected-key",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        // 将 _generation 推到 long.MaxValue，并写入敏感状态
        vaultSession.RestorePersistedSession(persisted); // gen = MaxValue-1
        var lease = vaultSession.BeginAccountSession("account-a", "a@example.com", A); // gen = MaxValue
        Assert.Equal(long.MaxValue, lease.Generation);

        var key = new Crypto.SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        Assert.True(vaultSession.TryCommit(lease, () =>
        {
            vaultSession.SetUnlockedKey(key);
            vaultSession.SetTokens("access-tok", "refresh-tok");
        }));
        Assert.NotNull(vaultSession.UserKey);

        // Lock at MaxValue (饱和)：try/finally 必须清除敏感状态
        vaultSession.Lock();

        Assert.Null(vaultSession.UserKey);
        Assert.Null(vaultSession.AccessToken);
        Assert.Empty(vaultSession.Ciphers);
        Assert.Equal(VaultState.Locked, vaultSession.State);
    }

    /// <summary>
    /// generation 饱和后 Logout 必须置 LoggedOut、清空 AccountInfo、清除敏感状态。
    /// RED: checked 抛 OverflowException，Logout 路径同样 fail-open。
    /// </summary>
    [Fact]
    public void Logout_AtMaxGeneration_ClearsSensitiveStateAndSetsLoggedOut()
    {
        var vaultSession = new VaultSession();
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            A,
            long.MaxValue - 1,
            "a@example.com",
            "device-1",
            "refresh-token",
            "2.protected-key",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        vaultSession.RestorePersistedSession(persisted); // gen = MaxValue-1
        var lease = vaultSession.BeginAccountSession("account-a", "a@example.com", A); // gen = MaxValue
        var key = new Crypto.SymmetricCryptoKey(
            Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        Assert.True(vaultSession.TryCommit(lease, () =>
        {
            vaultSession.SetUnlockedKey(key);
            vaultSession.SetTokens("access-tok", "refresh-tok");
        }));

        vaultSession.Logout(); // gen = MaxValue → saturate，try/finally 必须清除状态

        Assert.Null(vaultSession.UserKey);
        Assert.Null(vaultSession.AccessToken);
        Assert.Equal(VaultState.LoggedOut, vaultSession.State);
        Assert.Equal(AccountInfo.Empty, vaultSession.Account);
    }

    /// <summary>
    /// generation 饱和后的旧 lease TryCommit 应返回 false（stamp 已轮换）。
    /// RED: Lock 在 MaxValue 直接抛 OverflowException，测试无法执行。
    /// </summary>
    [Fact]
    public void TryCommit_OldLeaseAfterSaturation_ReturnsFalse()
    {
        var vaultSession = new VaultSession();
        var persisted = new PersistedSession(
            PersistedSession.CurrentSchemaVersion,
            "account-a",
            A,
            long.MaxValue - 1,
            "a@example.com",
            "device-1",
            "refresh-token",
            "2.protected-key",
            Core.Enums.KdfType.Pbkdf2,
            600_000,
            null,
            null);

        vaultSession.RestorePersistedSession(persisted);
        var oldLease = vaultSession.BeginAccountSession("account-a", "a@example.com", A); // gen = MaxValue
        Assert.Equal(long.MaxValue, oldLease.Generation);

        vaultSession.Lock(); // stamp/CTS 轮换，oldLease 失效

        var mutationRan = false;
        Assert.False(vaultSession.TryCommit(oldLease, () => mutationRan = true));
        Assert.False(mutationRan);
    }
}

