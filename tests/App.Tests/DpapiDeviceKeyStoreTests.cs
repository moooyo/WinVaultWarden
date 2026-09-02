using System.Security.Cryptography;
using App.Services;
using Core.Abstractions;
using Core.Models;
using Core.Services;
using Xunit;

namespace App.Tests;

public sealed class DpapiDeviceKeyStoreTests
{
    [Fact]
    public void Binding_RejectsAmbiguousOrMalformedIdentifiers()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example/base");
        var tooLong = new string('a', DeviceKeyBinding.MaximumIdentifierLength + 1);

        Assert.Throws<ArgumentNullException>(() =>
            new DeviceKeyBinding(null!, endpoints, "device-1"));
        Assert.Throws<ArgumentNullException>(() =>
            new DeviceKeyBinding("account-1", null!, "device-1"));
        Assert.Throws<ArgumentNullException>(() =>
            new DeviceKeyBinding("account-1", endpoints, null!));

        foreach (var invalid in new[] { "", " ", " account", "account ", "account\n1", "\uD800", tooLong })
        {
            Assert.Throws<ArgumentException>(() =>
                new DeviceKeyBinding(invalid, endpoints, "device-1"));
            Assert.Throws<ArgumentException>(() =>
                new DeviceKeyBinding("account-1", endpoints, invalid));
        }
    }

    [Fact]
    public void Binding_DerivationsAreDeterministicAndUseEveryScopeComponent()
    {
        var endpoints = ServerEndpoints.ForSelfHosted("https://vault.example/base");
        var customApiEndpoints = new ServerEndpoints(
            endpoints.WebVault,
            new ServerEndpoint("https://api.example/custom"),
            endpoints.Identity,
            endpoints.Notifications,
            endpoints.Icons,
            endpoints.SendWeb);
        var bindings = new[]
        {
            new DeviceKeyBinding("account-1", endpoints, "device-1"),
            new DeviceKeyBinding("account-2", endpoints, "device-1"),
            new DeviceKeyBinding(
                "account-1",
                ServerEndpoints.ForSelfHosted("https://other.example/base"),
                "device-1"),
            new DeviceKeyBinding("account-1", customApiEndpoints, "device-1"),
            new DeviceKeyBinding("account-1", endpoints, "device-2"),
        };

        Assert.Equal(
            bindings[0].CreateStorageFileName(),
            new DeviceKeyBinding("account-1", endpoints, "device-1").CreateStorageFileName());
        Assert.Equal(bindings.Length, bindings.Select(binding => binding.CreateStorageFileName()).Distinct().Count());
        Assert.All(bindings, binding =>
        {
            var fileName = binding.CreateStorageFileName();
            Assert.Matches("^[0-9a-f]{64}\\.bin$", fileName);
            Assert.DoesNotContain(binding.AccountId, fileName, StringComparison.Ordinal);
            Assert.DoesNotContain(binding.DeviceIdentifier, fileName, StringComparison.Ordinal);
        });

        var entropyValues = bindings.Select(binding => binding.CreateProtectionEntropy()).ToArray();
        try
        {
            Assert.All(entropyValues, entropy => Assert.Equal(32, entropy.Length));
            Assert.Equal(
                entropyValues.Length,
                entropyValues.Select(Convert.ToHexString).Distinct().Count());
        }
        finally
        {
            foreach (var entropy in entropyValues)
                CryptographicOperations.ZeroMemory(entropy);
        }
    }

    [Fact]
    public void UnsupportedProtection_FailsClosedWithoutTouchingDisk()
    {
        var directory = NewDirectory();
        var protection = new UnsupportedProtectionPolicy();
        var store = new DpapiDeviceKeyStore(directory, protection);
        var binding = Binding("account-1", "https://vault.example", "device-1");
        var key = DeviceKey(7);
        try
        {
            Assert.False(store.TryLoad(binding, out var loaded));
            Assert.Empty(loaded);
            Assert.False(store.Remove(binding));
            Assert.Equal(
                DeviceKeyRemovalStatus.Unavailable,
                store.RemoveExact(binding));
            Assert.Equal(
                AccountScopedRemovalStatus.Unavailable,
                store.RemoveAccountScope(binding.AccountId, binding.Endpoints));
            Assert.Throws<PlatformNotSupportedException>(() => store.Save(binding, key));
            Assert.False(protection.WasInvoked);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void DefaultProtection_OnNonWindowsFailsClosed()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = NewDirectory();
        var store = new DpapiDeviceKeyStore(directory);
        var binding = Binding("account-1", "https://vault.example", "device-1");
        var key = DeviceKey(11);
        try
        {
            Assert.False(store.TryLoad(binding, out var loaded));
            Assert.Empty(loaded);
            Assert.Throws<PlatformNotSupportedException>(() => store.Save(binding, key));
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void AbstractProtection_RoundTripsAtomicallyAndIsolatesBindings()
    {
        var directory = NewDirectory();
        var protection = new BindingProtectionPolicy();
        var store = new DpapiDeviceKeyStore(directory, protection);
        var firstBinding = Binding("account-a", "https://vault.example/a", "device-a");
        var secondBinding = Binding("account-b", "https://vault.example/b", "device-b");
        var firstKey = DeviceKey(19);
        var replacementKey = DeviceKey(37);
        var secondKey = DeviceKey(71);
        byte[]? loaded = null;
        try
        {
            store.Save(firstBinding, firstKey);
            AssertBuffersClearedAfterProtect(protection);
            store.Save(firstBinding, replacementKey);
            AssertBuffersClearedAfterProtect(protection);
            store.Save(secondBinding, secondKey);
            AssertBuffersClearedAfterProtect(protection);

            Assert.Equal(
                DeviceKeyRemovalStatus.Absent,
                store.RemoveExact(Binding(
                    "missing-account",
                    "https://vault.example/missing",
                    "missing-device")));

            var files = Directory.GetFiles(directory, "*.bin");
            Assert.Single(files);
            Assert.Equal(
                Path.Combine(directory, "device-key-ledger.bin"),
                Assert.Single(files));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

            Assert.True(store.TryLoad(firstBinding, out loaded));
            Assert.Equal(replacementKey, loaded);
            AssertBuffersClearedAfterUnprotect(protection);
            CryptographicOperations.ZeroMemory(loaded);
            loaded = null;

            Assert.True(store.TryLoad(secondBinding, out loaded));
            Assert.Equal(secondKey, loaded);
            Assert.Equal(
                DeviceKeyRemovalStatus.Removed,
                store.RemoveExact(firstBinding));
            Assert.False(store.TryLoad(firstBinding, out _));
            Assert.True(store.TryLoad(secondBinding, out var stillLoaded));
            try
            {
                Assert.Equal(secondKey, stillLoaded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stillLoaded);
            }
        }
        finally
        {
            if (loaded is not null)
                CryptographicOperations.ZeroMemory(loaded);
            CryptographicOperations.ZeroMemory(firstKey);
            CryptographicOperations.ZeroMemory(replacementKey);
            CryptographicOperations.ZeroMemory(secondKey);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void AbstractProtection_CopiedCiphertextCannotCrossBinding()
    {
        var directory = NewDirectory();
        var protection = new BindingProtectionPolicy();
        var store = new DpapiDeviceKeyStore(directory, protection);
        var sourceBinding = Binding("account-a", "https://vault.example", "device-a");
        var targetBinding = Binding("account-a", "https://vault.example", "device-b");
        var key = DeviceKey(101);
        byte[]? entropy = null;
        byte[]? protectedKey = null;
        try
        {
            Directory.CreateDirectory(directory);
            entropy = sourceBinding.CreateProtectionEntropy();
            protectedKey = protection.Protect(key, entropy);
            File.WriteAllBytes(
                Path.Combine(directory, sourceBinding.CreateStorageFileName()),
                protectedKey);
            File.Copy(
                Path.Combine(directory, sourceBinding.CreateStorageFileName()),
                Path.Combine(directory, targetBinding.CreateStorageFileName()));

            Assert.False(store.TryLoad(targetBinding, out var loaded));
            Assert.Empty(loaded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey!);
            CryptographicOperations.ZeroMemory(entropy!);
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void AbstractProtection_InvalidPlaintextLengthFailsClosedAndClearsIt()
    {
        var directory = NewDirectory();
        var protection = new InvalidLengthProtectionPolicy();
        var store = new DpapiDeviceKeyStore(directory, protection);
        var binding = Binding("account-1", "https://vault.example", "device-1");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, binding.CreateStorageFileName()), [1]);
        try
        {
            Assert.False(store.TryLoad(binding, out var loaded));
            Assert.Empty(loaded);
            Assert.NotNull(protection.ReturnedPlaintext);
            Assert.All(protection.ReturnedPlaintext!, value => Assert.Equal(0, value));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void AbstractProtection_AliasedUnprotectOutputIsCopiedBeforeCleanup()
    {
        var directory = NewDirectory();
        var binding = Binding("account-1", "https://vault.example", "device-1");
        var expected = DeviceKey(91);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(
                Path.Combine(directory, binding.CreateStorageFileName()),
                expected);
            var store = new DpapiDeviceKeyStore(
                directory,
                new AliasingProtectionPolicy());

            Assert.True(store.TryLoad(binding, out var loaded));
            try
            {
                Assert.Equal(expected, loaded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(loaded);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Save_RejectsEveryNon64ByteKeyWithoutCreatingStorage()
    {
        var directory = NewDirectory();
        var store = new DpapiDeviceKeyStore(directory, new BindingProtectionPolicy());
        var binding = Binding("account-1", "https://vault.example", "device-1");

        Assert.Throws<ArgumentException>(() => store.Save(binding, new byte[63]));
        Assert.Throws<ArgumentException>(() => store.Save(binding, new byte[65]));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Save_WhenAtomicWriteFailsClearsEveryOwnedBuffer()
    {
        var directory = NewDirectory();
        var parent = Directory.GetParent(directory)!.FullName;
        Directory.CreateDirectory(parent);
        File.WriteAllText(directory, "blocks-directory-creation");
        var protection = new BindingProtectionPolicy();
        var store = new DpapiDeviceKeyStore(directory, protection);
        var binding = Binding("account-1", "https://vault.example", "device-1");
        var key = DeviceKey(113);
        try
        {
            Assert.ThrowsAny<IOException>(() => store.Save(binding, key));
            AssertBuffersClearedAfterProtect(protection);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Save_WhenLocalWriteGuardDeniesThrowsWithoutTouchingDisk()
    {
        var directory = NewDirectory();
        var guard = new TrackingLocalWriteGuard(allow: false);
        var store = new DpapiDeviceKeyStore(
            directory,
            new BindingProtectionPolicy(),
            guard);
        var binding = Binding("account-1", "https://vault.example", "device-1");
        var key = DeviceKey(117);
        try
        {
            Assert.Throws<AccountDeletionWriteBlockedException>(() =>
                store.Save(binding, key));

            Assert.Equal(1, guard.AcquireCount);
            Assert.Equal(binding.AccountId, guard.LastAccountId);
            Assert.Equal(binding.Endpoints, guard.LastEndpoints);
            Assert.False(guard.IsHeld);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Save_HoldsLocalWriteGuardThroughCommitAndReleasesIt()
    {
        var directory = NewDirectory();
        var guard = new TrackingLocalWriteGuard(allow: true);
        var replacement = new GuardObservingAtomicReplacePolicy(guard);
        var store = new DpapiDeviceKeyStore(
            directory,
            new BindingProtectionPolicy(),
            replacement,
            guard);
        var binding = Binding("account-1", "https://vault.example", "device-1");
        var key = DeviceKey(118);
        try
        {
            store.Save(binding, key);

            Assert.True(replacement.ObservedHeldLease);
            Assert.Equal(1, guard.AcquireCount);
            Assert.Equal(1, guard.DisposeCount);
            Assert.False(guard.IsHeld);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Save_WhenCommitFailsReleasesLocalWriteGuard()
    {
        var directory = NewDirectory();
        var guard = new TrackingLocalWriteGuard(allow: true);
        var replacement = new GuardObservingAtomicReplacePolicy(guard)
        {
            FailAfterObservation = true,
        };
        var store = new DpapiDeviceKeyStore(
            directory,
            new BindingProtectionPolicy(),
            replacement,
            guard);
        var binding = Binding("account-1", "https://vault.example", "device-1");
        var key = DeviceKey(118);
        try
        {
            Assert.Throws<IOException>(() => store.Save(binding, key));

            Assert.True(replacement.ObservedHeldLease);
            Assert.Equal(1, guard.AcquireCount);
            Assert.Equal(1, guard.DisposeCount);
            Assert.False(guard.IsHeld);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RemovalOperations_DoNotAcquireDenyingLocalWriteGuard()
    {
        var directory = NewDirectory();
        var protection = new BindingProtectionPolicy();
        var unguarded = new DpapiDeviceKeyStore(directory, protection);
        var removeBinding = Binding("account-a", "https://vault.example", "device-1");
        var removeExactBinding = Binding("account-a", "https://vault.example", "device-2");
        var removeScopeBinding = Binding("account-b", "https://vault.example", "device-3");
        var key = DeviceKey(119);
        try
        {
            unguarded.Save(removeBinding, key);
            unguarded.Save(removeExactBinding, key);
            unguarded.Save(removeScopeBinding, key);

            var guard = new TrackingLocalWriteGuard(allow: false);
            var guarded = new DpapiDeviceKeyStore(
                directory,
                protection,
                FileMoveDeviceKeyAtomicReplacePolicy.Instance,
                guard);

            Assert.True(guarded.Remove(removeBinding));
            Assert.Equal(
                DeviceKeyRemovalStatus.Removed,
                guarded.RemoveExact(removeExactBinding));
            Assert.Equal(
                AccountScopedRemovalStatus.Removed,
                guarded.RemoveAccountScope(
                    removeScopeBinding.AccountId,
                    removeScopeBinding.Endpoints));
            Assert.Equal(0, guard.AcquireCount);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RemoveAccountScope_RemovesEveryTargetDeviceAndPreservesOtherScopes()
    {
        var directory = NewDirectory();
        var protection = new BindingProtectionPolicy();
        var first = new DpapiDeviceKeyStore(directory, protection);
        var second = new DpapiDeviceKeyStore(directory, protection);
        var targetOne = Binding("account-a", "https://vault.example", "device-1");
        var targetTwo = Binding("account-a", "https://vault.example", "device-2");
        var otherAccount = Binding("account-b", "https://vault.example", "device-3");
        var otherServer = Binding("account-a", "https://other.example", "device-4");
        var key = DeviceKey(121);
        try
        {
            first.Save(targetOne, key);
            second.Save(targetTwo, key);
            first.Save(otherAccount, key);
            second.Save(otherServer, key);

            Assert.Equal(
                AccountScopedRemovalStatus.Removed,
                second.RemoveAccountScope(targetOne.AccountId, targetOne.Endpoints));
            Assert.False(first.TryLoad(targetOne, out _));
            Assert.False(first.TryLoad(targetTwo, out _));
            Assert.True(first.TryLoad(otherAccount, out var otherAccountKey));
            Assert.True(first.TryLoad(otherServer, out var otherServerKey));
            try
            {
                Assert.Equal(key, otherAccountKey);
                Assert.Equal(key, otherServerKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(otherServerKey);
                CryptographicOperations.ZeroMemory(otherAccountKey);
            }

            Assert.Equal(
                AccountScopedRemovalStatus.Absent,
                first.RemoveAccountScope(targetOne.AccountId, targetOne.Endpoints));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RemoveAccountScope_DistinguishesCorruptAndUnavailable()
    {
        var directory = NewDirectory();
        var binding = Binding("account-a", "https://vault.example", "device-1");
        try
        {
            var supported = new DpapiDeviceKeyStore(
                directory,
                new BindingProtectionPolicy());
            Assert.Equal(
                AccountScopedRemovalStatus.Absent,
                supported.RemoveAccountScope(binding.AccountId, binding.Endpoints));

            File.WriteAllBytes(
                Path.Combine(directory, "device-key-ledger.bin"),
                "WVWDKL01corrupt"u8.ToArray());
            Assert.Equal(
                AccountScopedRemovalStatus.Corrupt,
                supported.RemoveAccountScope(binding.AccountId, binding.Endpoints));

            var unavailableDirectory = NewDirectory();
            try
            {
                var unavailable = new DpapiDeviceKeyStore(
                    unavailableDirectory,
                    new UnsupportedProtectionPolicy());
                Assert.Equal(
                    AccountScopedRemovalStatus.Unavailable,
                    unavailable.RemoveAccountScope(binding.AccountId, binding.Endpoints));
                Assert.False(Directory.Exists(unavailableDirectory));
            }
            finally
            {
                DeleteDirectory(unavailableDirectory);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RemoveAccountScope_ReplaceFailurePreservesTargetLedger()
    {
        var directory = NewDirectory();
        var replacement = new TestAtomicReplacePolicy();
        var store = new DpapiDeviceKeyStore(
            directory,
            new BindingProtectionPolicy(),
            replacement);
        var binding = Binding("account-a", "https://vault.example", "device-1");
        var key = DeviceKey(127);
        try
        {
            store.Save(binding, key);
            replacement.FailNext = true;
            Assert.Equal(
                AccountScopedRemovalStatus.Unavailable,
                store.RemoveAccountScope(binding.AccountId, binding.Endpoints));
            Assert.True(store.TryLoad(binding, out var recovered));
            try
            {
                Assert.Equal(key, recovered);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recovered);
            }
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RemoveAccountScope_ProtectionWriteFailureIsUnavailable()
    {
        var directory = NewDirectory();
        var protection = new BindingProtectionPolicy();
        var store = new DpapiDeviceKeyStore(directory, protection);
        var binding = Binding("account-a", "https://vault.example", "device-1");
        var key = DeviceKey(127);
        try
        {
            store.Save(binding, key);
            protection.FailNextProtect = true;
            Assert.Equal(
                AccountScopedRemovalStatus.Unavailable,
                store.RemoveAccountScope(binding.AccountId, binding.Endpoints));
            Assert.True(store.TryLoad(binding, out var recovered));
            CryptographicOperations.ZeroMemory(recovered);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RemoveAccountScope_InvalidInnerKeyIsCorruptAndDoesNotRewrite()
    {
        var directory = NewDirectory();
        var protection = new BindingProtectionPolicy();
        var store = new DpapiDeviceKeyStore(directory, protection);
        var binding = Binding("account-a", "https://vault.example", "device-1");
        var key = DeviceKey(128);
        try
        {
            store.Save(binding, key);
            var path = Path.Combine(directory, "device-key-ledger.bin");
            var before = File.ReadAllBytes(path);
            protection.FailDeviceKeyUnprotect = true;

            Assert.Equal(
                AccountScopedRemovalStatus.Corrupt,
                store.RemoveAccountScope(binding.AccountId, binding.Endpoints));
            Assert.Equal(before, File.ReadAllBytes(path));

            protection.FailDeviceKeyUnprotect = false;
            Assert.True(store.TryLoad(binding, out var recovered));
            CryptographicOperations.ZeroMemory(recovered);
            CryptographicOperations.ZeroMemory(before);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RemoveAccountScope_UntrackedLegacyFailsClosedAndCanonicalTempIsCleaned()
    {
        var directory = NewDirectory();
        var store = new DpapiDeviceKeyStore(directory, new BindingProtectionPolicy());
        var binding = Binding("account-a", "https://vault.example", "device-1");
        var key = DeviceKey(129);
        try
        {
            store.Save(binding, key);
            var orphan = Path.Combine(directory, new string('a', 64) + ".bin");
            File.WriteAllBytes(orphan, [1, 2, 3]);

            Assert.Equal(
                AccountScopedRemovalStatus.Unavailable,
                store.RemoveAccountScope(binding.AccountId, binding.Endpoints));
            Assert.True(File.Exists(orphan));
            Assert.True(store.TryLoad(binding, out var recovered));
            CryptographicOperations.ZeroMemory(recovered);

            File.Delete(orphan);
            var temporary = Path.Combine(directory, ".device-key-ledger.bin.stale.tmp");
            File.WriteAllBytes(temporary, [4, 5, 6]);
            Assert.Equal(
                AccountScopedRemovalStatus.Removed,
                store.RemoveAccountScope(binding.AccountId, binding.Endpoints));
            Assert.False(File.Exists(temporary));
            Assert.False(store.TryLoad(binding, out _));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [WindowsFact]
    public void RemoveAccountScope_PostCommitTempFailureIsUnavailableThenRetryIsAbsent()
    {
        var directory = NewDirectory();
        using var replacement = new LeavingLockedSourceAtomicReplacePolicy();
        var store = new DpapiDeviceKeyStore(
            directory,
            new BindingProtectionPolicy(),
            replacement);
        var target = Binding("account-a", "https://vault.example", "device-1");
        var other = Binding("account-b", "https://vault.example", "device-2");
        var key = DeviceKey(130);
        try
        {
            store.Save(target, key);
            store.Save(other, key);
            replacement.LockSourceAfterReplace = true;

            Assert.Equal(
                AccountScopedRemovalStatus.Unavailable,
                store.RemoveAccountScope(target.AccountId, target.Endpoints));
            replacement.ReleaseSource();
            Assert.Equal(
                AccountScopedRemovalStatus.Absent,
                store.RemoveAccountScope(target.AccountId, target.Endpoints));
            Assert.True(store.TryLoad(other, out var recovered));
            CryptographicOperations.ZeroMemory(recovered);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Save_MigratesExactLegacyFileIntoCanonicalLedger()
    {
        var directory = NewDirectory();
        var protection = new BindingProtectionPolicy();
        var store = new DpapiDeviceKeyStore(directory, protection);
        var binding = Binding("account-a", "https://vault.example", "device-1");
        var oldKey = DeviceKey(131);
        var newKey = DeviceKey(137);
        byte[]? entropy = null;
        byte[]? protectedKey = null;
        try
        {
            Directory.CreateDirectory(directory);
            entropy = binding.CreateProtectionEntropy();
            protectedKey = protection.Protect(oldKey, entropy);
            var legacyPath = Path.Combine(directory, binding.CreateStorageFileName());
            File.WriteAllBytes(legacyPath, protectedKey);
            Assert.True(store.TryLoad(binding, out var loadedLegacy));
            Assert.Equal(oldKey, loadedLegacy);
            CryptographicOperations.ZeroMemory(loadedLegacy);

            store.Save(binding, newKey);
            Assert.False(File.Exists(legacyPath));
            Assert.True(store.TryLoad(binding, out var loadedCanonical));
            try
            {
                Assert.Equal(newKey, loadedCanonical);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(loadedCanonical);
            }
        }
        finally
        {
            if (protectedKey is not null)
                CryptographicOperations.ZeroMemory(protectedKey);
            if (entropy is not null)
                CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(newKey);
            CryptographicOperations.ZeroMemory(oldKey);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void TryLoadExactLegacy_AutomaticallyMigratesThenScopePurgeSucceeds()
    {
        var directory = NewDirectory();
        var protection = new BindingProtectionPolicy();
        var store = new DpapiDeviceKeyStore(directory, protection);
        var binding = Binding("account-a", "https://vault.example", "device-legacy");
        var key = DeviceKey(139);
        byte[]? entropy = null;
        byte[]? protectedKey = null;
        try
        {
            Directory.CreateDirectory(directory);
            entropy = binding.CreateProtectionEntropy();
            protectedKey = protection.Protect(key, entropy);
            var legacyPath = Path.Combine(directory, binding.CreateStorageFileName());
            File.WriteAllBytes(legacyPath, protectedKey);

            Assert.True(store.TryLoad(binding, out var loaded));
            try
            {
                Assert.Equal(key, loaded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(loaded);
            }
            Assert.False(File.Exists(legacyPath));
            Assert.True(File.Exists(Path.Combine(directory, "device-key-ledger.bin")));

            var reopened = new DpapiDeviceKeyStore(directory, protection);
            Assert.True(reopened.TryLoad(binding, out var canonical));
            CryptographicOperations.ZeroMemory(canonical);
            Assert.Equal(
                AccountScopedRemovalStatus.Removed,
                reopened.RemoveAccountScope(binding.AccountId, binding.Endpoints));
            Assert.False(reopened.TryLoad(binding, out _));
        }
        finally
        {
            if (protectedKey is not null)
                CryptographicOperations.ZeroMemory(protectedKey);
            if (entropy is not null)
                CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void TryLoadExactLegacy_MigrationFailureStillReturnsAuthenticatedKey()
    {
        var directory = NewDirectory();
        var protection = new BindingProtectionPolicy();
        var replacement = new TestAtomicReplacePolicy { FailNext = true };
        var store = new DpapiDeviceKeyStore(directory, protection, replacement);
        var binding = Binding("account-a", "https://vault.example", "device-legacy");
        var key = DeviceKey(141);
        byte[]? entropy = null;
        byte[]? protectedKey = null;
        try
        {
            Directory.CreateDirectory(directory);
            entropy = binding.CreateProtectionEntropy();
            protectedKey = protection.Protect(key, entropy);
            var legacyPath = Path.Combine(directory, binding.CreateStorageFileName());
            File.WriteAllBytes(legacyPath, protectedKey);

            Assert.True(store.TryLoad(binding, out var loaded));
            try
            {
                Assert.Equal(key, loaded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(loaded);
            }
            Assert.True(File.Exists(legacyPath));
            Assert.False(File.Exists(Path.Combine(directory, "device-key-ledger.bin")));
            Assert.Equal(
                AccountScopedRemovalStatus.Unavailable,
                store.RemoveAccountScope(binding.AccountId, binding.Endpoints));
        }
        finally
        {
            if (protectedKey is not null)
                CryptographicOperations.ZeroMemory(protectedKey);
            if (entropy is not null)
                CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void TryLoadExactLegacy_WhenWriteGuardRejectsReturnsKeyWithoutMigration()
    {
        var directory = NewDirectory();
        var protection = new BindingProtectionPolicy();
        var guard = new TrackingLocalWriteGuard(allow: false);
        var store = new DpapiDeviceKeyStore(directory, protection, guard);
        var binding = Binding("account-a", "https://vault.example", "device-legacy");
        var key = DeviceKey(143);
        byte[]? entropy = null;
        byte[]? protectedKey = null;
        try
        {
            Directory.CreateDirectory(directory);
            entropy = binding.CreateProtectionEntropy();
            protectedKey = protection.Protect(key, entropy);
            var legacyPath = Path.Combine(directory, binding.CreateStorageFileName());
            File.WriteAllBytes(legacyPath, protectedKey);

            Assert.True(store.TryLoad(binding, out var loaded));
            try
            {
                Assert.Equal(key, loaded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(loaded);
            }
            Assert.Equal(1, guard.AcquireCount);
            Assert.True(File.Exists(legacyPath));
            Assert.False(File.Exists(Path.Combine(directory, "device-key-ledger.bin")));
        }
        finally
        {
            if (protectedKey is not null)
                CryptographicOperations.ZeroMemory(protectedKey);
            if (entropy is not null)
                CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void CanonicalLedger_EnforcesThirtyTwoEntryLimit()
    {
        var directory = NewDirectory();
        var store = new DpapiDeviceKeyStore(directory, new BindingProtectionPolicy());
        var key = DeviceKey(143);
        try
        {
            for (var index = 0; index < PersistedDeviceKeyLedger.MaximumEntries; index++)
            {
                store.Save(
                    Binding("account-a", "https://vault.example", $"device-{index}"),
                    key);
            }

            Assert.Throws<InvalidDataException>(() => store.Save(
                Binding("account-a", "https://vault.example", "device-overflow"),
                key));
            Assert.True(store.TryLoad(
                Binding("account-a", "https://vault.example", "device-0"),
                out var first));
            CryptographicOperations.ZeroMemory(first);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ProductionSource_UsesLocksWriteThroughAndNoFileExistsAbsenceCheck()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "App",
            "Services",
            "DpapiDeviceKeyStore.cs"));

        Assert.DoesNotContain("File.Exists", source, StringComparison.Ordinal);
        Assert.Contains("FileShare.None", source, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("FileOptions.WriteThrough", source, StringComparison.Ordinal);
        Assert.Contains("stream.Flush(flushToDisk: true)", source, StringComparison.Ordinal);
        Assert.Contains(
            "MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough",
            source,
            StringComparison.Ordinal);
    }

    [WindowsFact]
    public void CurrentUserDpapi_RoundTripsWithoutPersistingPlaintext()
    {
        var directory = NewDirectory();
        var store = new DpapiDeviceKeyStore(directory);
        var binding = Binding("account-dpapi", "https://vault.example", "device-dpapi");
        var key = DeviceKey(131);
        byte[]? loaded = null;
        try
        {
            store.Save(binding, key);
            var persisted = File.ReadAllBytes(Path.Combine(directory, "device-key-ledger.bin"));
            try
            {
                Assert.False(ContainsSequence(persisted, key));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(persisted);
            }

            Assert.True(store.TryLoad(binding, out loaded));
            Assert.Equal(key, loaded);
        }
        finally
        {
            if (loaded is not null)
                CryptographicOperations.ZeroMemory(loaded);
            CryptographicOperations.ZeroMemory(key);
            DeleteDirectory(directory);
        }
    }

    private static DeviceKeyBinding Binding(
        string accountId,
        string serverUrl,
        string deviceIdentifier) => new(
        accountId,
        ServerEndpoints.FromWebVault(serverUrl),
        deviceIdentifier);

    private static byte[] DeviceKey(int seed) =>
        Enumerable.Range(seed, IDeviceKeyStore.KeyLength).Select(value => (byte)value).ToArray();

    private static string NewDirectory() => Path.Combine(
        Path.GetTempPath(),
        "WinVaultWarden.Tests",
        Guid.NewGuid().ToString("N"),
        "device-keys");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinVaultWarden.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static void DeleteDirectory(string directory)
    {
        var root = Directory.GetParent(directory)?.FullName;
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static void AssertBuffersClearedAfterProtect(BindingProtectionPolicy protection)
    {
        AssertCleared(protection.LastProtectPlaintext);
        AssertCleared(protection.LastProtectEntropy);
        AssertCleared(protection.LastProtectedOutput);
    }

    private static void AssertBuffersClearedAfterUnprotect(BindingProtectionPolicy protection)
    {
        AssertCleared(protection.LastUnprotectProtectedData);
        AssertCleared(protection.LastUnprotectEntropy);
    }

    private static void AssertCleared(byte[]? buffer)
    {
        Assert.NotNull(buffer);
        Assert.All(buffer!, value => Assert.Equal(0, value));
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0)
            return true;
        for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle))
                return true;
        }
        return false;
    }

    private sealed class UnsupportedProtectionPolicy : IDeviceKeyProtectionPolicy
    {
        public bool WasInvoked { get; private set; }

        public bool IsSupported => false;

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            WasInvoked = true;
            throw new InvalidOperationException();
        }

        public byte[] Unprotect(byte[] protectedData, byte[] entropy)
        {
            WasInvoked = true;
            throw new InvalidOperationException();
        }
    }

    private sealed class TestAtomicReplacePolicy : IDeviceKeyAtomicReplacePolicy
    {
        public bool IsSupported => true;

        public bool FailNext { get; set; }

        public void Replace(string sourcePath, string destinationPath)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Injected device-key ledger replacement failure.");
            }
            File.Move(sourcePath, destinationPath, overwrite: true);
        }
    }

    private sealed class GuardObservingAtomicReplacePolicy(
        TrackingLocalWriteGuard guard) : IDeviceKeyAtomicReplacePolicy
    {
        public bool IsSupported => true;

        public bool FailAfterObservation { get; init; }

        public bool ObservedHeldLease { get; private set; }

        public void Replace(string sourcePath, string destinationPath)
        {
            ObservedHeldLease = guard.IsHeld;
            if (FailAfterObservation)
                throw new IOException("Injected device-key ledger replacement failure.");
            File.Move(sourcePath, destinationPath, overwrite: true);
        }
    }

    private sealed class TrackingLocalWriteGuard(bool allow)
        : IAccountDeletionLocalWriteGuard
    {
        public int AcquireCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool IsHeld { get; private set; }

        public string? LastAccountId { get; private set; }

        public ServerEndpoints? LastEndpoints { get; private set; }

        public IDisposable? TryAcquire(string accountId, ServerEndpoints endpoints)
        {
            AcquireCount++;
            LastAccountId = accountId;
            LastEndpoints = endpoints;
            if (!allow)
                return null;

            Assert.False(IsHeld);
            IsHeld = true;
            return new CallbackDisposable(() =>
            {
                Assert.True(IsHeld);
                IsHeld = false;
                DisposeCount++;
            });
        }
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        private Action? _callback = callback;

        public void Dispose() => Interlocked.Exchange(ref _callback, null)?.Invoke();
    }

    private sealed class LeavingLockedSourceAtomicReplacePolicy
        : IDeviceKeyAtomicReplacePolicy,
          IDisposable
    {
        private FileStream? _source;

        public bool IsSupported => true;

        public bool LockSourceAfterReplace { get; set; }

        public void Replace(string sourcePath, string destinationPath)
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            if (LockSourceAfterReplace)
            {
                _source = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
            }
            else
            {
                File.Delete(sourcePath);
            }
        }

        public void ReleaseSource()
        {
            _source?.Dispose();
            _source = null;
            LockSourceAfterReplace = false;
        }

        public void Dispose() => ReleaseSource();
    }

    private sealed class BindingProtectionPolicy : IDeviceKeyProtectionPolicy
    {
        public bool IsSupported => true;

        public bool FailDeviceKeyUnprotect { get; set; }

        public bool FailNextProtect { get; set; }

        public byte[]? LastProtectPlaintext { get; private set; }

        public byte[]? LastProtectEntropy { get; private set; }

        public byte[]? LastProtectedOutput { get; private set; }

        public byte[]? LastUnprotectProtectedData { get; private set; }

        public byte[]? LastUnprotectEntropy { get; private set; }

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            if (FailNextProtect)
            {
                FailNextProtect = false;
                throw new CryptographicException("Injected device-key protection failure.");
            }
            LastProtectPlaintext = plaintext;
            LastProtectEntropy = entropy;
            var protectedData = new byte[entropy.Length + plaintext.Length];
            entropy.CopyTo(protectedData, 0);
            for (var index = 0; index < plaintext.Length; index++)
                protectedData[entropy.Length + index] = (byte)(plaintext[index] ^ entropy[index % entropy.Length]);
            LastProtectedOutput = protectedData;
            return protectedData;
        }

        public byte[] Unprotect(byte[] protectedData, byte[] entropy)
        {
            LastUnprotectProtectedData = protectedData;
            LastUnprotectEntropy = entropy;
            if (FailDeviceKeyUnprotect
                && protectedData.Length == entropy.Length + IDeviceKeyStore.KeyLength)
            {
                throw new CryptographicException("Injected inner device-key corruption.");
            }
            if (protectedData.Length <= entropy.Length
                || !CryptographicOperations.FixedTimeEquals(
                    protectedData.AsSpan(0, entropy.Length),
                    entropy))
            {
                throw new CryptographicException("Binding entropy does not match.");
            }

            var plaintext = new byte[protectedData.Length - entropy.Length];
            for (var index = 0; index < plaintext.Length; index++)
                plaintext[index] = (byte)(protectedData[entropy.Length + index] ^ entropy[index % entropy.Length]);
            return plaintext;
        }
    }

    private sealed class InvalidLengthProtectionPolicy : IDeviceKeyProtectionPolicy
    {
        public bool IsSupported => true;

        public byte[]? ReturnedPlaintext { get; private set; }

        public byte[] Protect(byte[] plaintext, byte[] entropy) => throw new NotSupportedException();

        public byte[] Unprotect(byte[] protectedData, byte[] entropy)
        {
            ReturnedPlaintext = new byte[IDeviceKeyStore.KeyLength - 1];
            Array.Fill(ReturnedPlaintext, (byte)0xA5);
            return ReturnedPlaintext;
        }
    }

    private sealed class AliasingProtectionPolicy : IDeviceKeyProtectionPolicy
    {
        public bool IsSupported => true;

        public byte[] Protect(byte[] plaintext, byte[] entropy) =>
            throw new NotSupportedException();

        public byte[] Unprotect(byte[] protectedData, byte[] entropy) =>
            protectedData;
    }
}
