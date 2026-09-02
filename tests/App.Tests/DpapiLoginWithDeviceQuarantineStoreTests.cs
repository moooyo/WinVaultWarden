using System.Security.Cryptography;
using System.Text;
using App.Services;
using Core.Abstractions;
using Core.Models;
using Xunit;

namespace App.Tests;

public sealed class DpapiLoginWithDeviceQuarantineStoreTests
{
    private const string DeviceId = "11111111-2222-3333-4444-555555555555";
    private const string RequestId = "66666666-7777-8888-9999-aaaaaaaaaaaa";

    private static readonly DateTimeOffset ArmedAt =
        DateTimeOffset.Parse("2026-08-31T00:00:00Z");

    [Fact]
    public void Models_NormalizeBindingAndRejectInvalidRecords()
    {
        var binding = Binding(email: "  USER@Example.COM  ");
        Assert.Equal("user@example.com", binding.NormalizedEmail);
        Assert.Equal(DeviceId, binding.DeviceIdentifier);

        var upperDeviceId = DeviceId.ToUpperInvariant();
        Assert.Equal(
            DeviceId,
            Binding(deviceIdentifier: upperDeviceId).DeviceIdentifier);
        Assert.Throws<ArgumentException>(() => Binding(deviceIdentifier: Guid.Empty.ToString("D")));
        Assert.Throws<ArgumentException>(() => Binding(deviceIdentifier: "not-a-guid"));
        Assert.Throws<ArgumentException>(() => Binding(email: "   "));

        var expiresAt = ArmedAt.AddMinutes(5);
        Assert.Throws<ArgumentException>(() => new LoginWithDeviceQuarantineRecord(
            Guid.NewGuid(),
            1,
            binding,
            RequestId,
            ArmedAt,
            ArmedAt,
            expiresAt));
        Assert.Throws<ArgumentException>(() => new LoginWithDeviceQuarantineRecord(
            Guid.NewGuid(),
            1,
            binding,
            RequestId,
            ArmedAt,
            expiresAt,
            expiresAt.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => new LoginWithDeviceQuarantineRecord(
            Guid.NewGuid(),
            1,
            binding,
            Guid.Empty.ToString("D"),
            ArmedAt,
            expiresAt,
            expiresAt));
    }

    [Fact]
    public void FakePolicies_RestoreFullBindingsAndNeverDeleteByTtl()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var replacement = new TestAtomicReplacePolicy();
        var binding = Binding();
        var alternate = Binding(
            endpoints: WithAlternateApi(Endpoints()),
            deviceIdentifier: "bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        try
        {
            var first = Store(path, protection, replacement);
            var created = Assert.IsType<LoginWithDeviceQuarantineRecord>(Arm(first, binding));
            Assert.Null(Arm(first, Binding(email: "USER@EXAMPLE.COM")));
            Assert.NotNull(Arm(
                first,
                alternate,
                requestId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

            var yearsLater = new FixedTimeProvider(ArmedAt.AddYears(20));
            var reopened = Store(path, protection, replacement, yearsLater);
            var recovered = AssertFound(reopened.Read(binding));
            Assert.Equal(created, recovered);
            Assert.True(recovered.IsClearEligible(yearsLater.GetUtcNow()));
            Assert.Equal(alternate, AssertFound(reopened.Read(alternate)).Binding);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void Remove_UsesExactCasAndEnforcesUserEligibility()
    {
        var path = NewPath();
        var now = new FixedTimeProvider(ArmedAt.AddMinutes(6));
        var store = Store(
            path,
            new TestProtectionPolicy(),
            new TestAtomicReplacePolicy(),
            now);
        var binding = Binding();
        try
        {
            var current = Assert.IsType<LoginWithDeviceQuarantineRecord>(
                Arm(store, binding, clearEligibleAt: ArmedAt.AddHours(1)));
            var stale = new LoginWithDeviceQuarantineRecord(
                current.EntryId,
                current.Revision + 1,
                current.Binding,
                current.RequestId,
                current.ArmedAt,
                current.RequestExpiresAt,
                current.ClearEligibleAt);

            Assert.False(store.Remove(
                stale,
                LoginWithDeviceQuarantineRemovalReason.DefiniteOutcome));
            Assert.False(store.Remove(
                current,
                LoginWithDeviceQuarantineRemovalReason.UserConfirmedAfterEligibility));
            Assert.Equal(
                LoginWithDeviceQuarantineStoreReadStatus.Found,
                store.Read(binding).Status);

            now.UtcNow = current.ClearEligibleAt;
            Assert.True(store.Remove(
                current,
                LoginWithDeviceQuarantineRemovalReason.UserConfirmedAfterEligibility));
            Assert.Equal(
                LoginWithDeviceQuarantineStoreReadStatus.Absent,
                store.Read(binding).Status);

            var replacement = Assert.IsType<LoginWithDeviceQuarantineRecord>(Arm(store, binding));
            Assert.False(store.Remove(
                current,
                LoginWithDeviceQuarantineRemovalReason.SessionPersisted));
            Assert.True(store.Remove(
                replacement,
                LoginWithDeviceQuarantineRemovalReason.SessionPersisted));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task CrossInstanceArm_AllowsOnlyOneRecordPerBinding()
    {
        var path = NewPath();
        var binding = Binding();
        var first = Store(path);
        var second = Store(path);
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        try
        {
            Task<LoginWithDeviceQuarantineRecord?> ArmAsync(
                DpapiLoginWithDeviceQuarantineStore store,
                string requestId) => Task.Run(() =>
            {
                ready.Signal();
                start.Wait(CancellationToken.None);
                return Arm(store, binding, requestId);
            });

            var firstArm = ArmAsync(first, RequestId);
            var secondArm = ArmAsync(
                second,
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            start.Set();

            var results = await Task.WhenAll(firstArm, secondArm);
            Assert.Single(results, result => result is not null);
            Assert.Equal(
                LoginWithDeviceQuarantineStoreReadStatus.Found,
                first.Read(binding).Status);
        }
        finally
        {
            start.Set();
            DeleteScope(path);
        }
    }

    [Fact]
    public void Ledger_EnforcesThirtyTwoEntryLimitWithoutLosingExistingEntries()
    {
        var path = NewPath();
        var store = Store(path);
        var firstBinding = Binding(deviceIdentifier: Guid.NewGuid().ToString("D"));
        try
        {
            Assert.NotNull(Arm(store, firstBinding, Guid.NewGuid().ToString("D")));
            for (var index = 1; index < 32; index++)
            {
                Assert.NotNull(Arm(
                    store,
                    Binding(deviceIdentifier: Guid.NewGuid().ToString("D")),
                    Guid.NewGuid().ToString("D")));
            }

            Assert.Throws<InvalidDataException>(() => Arm(
                store,
                Binding(deviceIdentifier: Guid.NewGuid().ToString("D")),
                Guid.NewGuid().ToString("D")));
            Assert.Equal(firstBinding, AssertFound(store.Read(firstBinding)).Binding);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void Read_DistinguishesAbsentCorruptAndUnavailableAndMutationsFailClosed()
    {
        var path = NewPath();
        var binding = Binding();
        var protection = new TestProtectionPolicy();
        var store = Store(path, protection);
        byte[]? corrupt = null;
        try
        {
            Assert.Equal(
                LoginWithDeviceQuarantineStoreReadStatus.Absent,
                store.Read(binding).Status);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            corrupt = "WVWLDQ01corrupt"u8.ToArray();
            File.WriteAllBytes(path, corrupt);
            Assert.Equal(
                LoginWithDeviceQuarantineStoreReadStatus.Corrupt,
                store.Read(binding).Status);
            Assert.ThrowsAny<CryptographicException>(() => Arm(store, binding));
            Assert.Equal(corrupt, File.ReadAllBytes(path));

            WriteProtectedJson(
                path,
                protection,
                "{\"format\":\"WinVaultWarden.LoginWithDeviceQuarantineLedger\",\"schemaVersion\":1,\"entries\":[],\"accessCode\":\"must-not-be-accepted\"}");
            Assert.Equal(
                LoginWithDeviceQuarantineStoreReadStatus.Corrupt,
                store.Read(binding).Status);

            using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write))
                oversized.SetLength(1024L * 1024 + 1);
            var unprotectCalls = protection.UnprotectCalls;
            Assert.Equal(
                LoginWithDeviceQuarantineStoreReadStatus.Corrupt,
                store.Read(binding).Status);
            Assert.Equal(unprotectCalls, protection.UnprotectCalls);

            var unsupportedPath = NewPath();
            try
            {
                var unsupported = new DpapiLoginWithDeviceQuarantineStore(
                    unsupportedPath,
                    new UnsupportedProtectionPolicy(),
                    new TestAtomicReplacePolicy());
                Assert.Equal(
                    LoginWithDeviceQuarantineStoreReadStatus.Unavailable,
                    unsupported.Read(binding).Status);
                Assert.Throws<PlatformNotSupportedException>(() => Arm(unsupported, binding));
                Assert.False(Directory.Exists(Path.GetDirectoryName(unsupportedPath)));
            }
            finally
            {
                DeleteScope(unsupportedPath);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(corrupt);
            DeleteScope(path);
        }
    }

    [Fact]
    public void ReplacementFailure_PreservesOldLedgerAndCleansTemporaryFile()
    {
        var path = NewPath();
        var replacement = new TestAtomicReplacePolicy();
        var store = Store(path, atomicReplace: replacement);
        var firstBinding = Binding();
        var secondBinding = Binding(
            email: "other@example.com",
            deviceIdentifier: "bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        try
        {
            var first = Assert.IsType<LoginWithDeviceQuarantineRecord>(
                Arm(store, firstBinding));
            var oldFile = File.ReadAllBytes(path);
            replacement.FailNext = true;

            Assert.Throws<IOException>(() => Arm(
                store,
                secondBinding,
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));
            Assert.Equal(oldFile, File.ReadAllBytes(path));
            Assert.Equal(first, AssertFound(store.Read(firstBinding)));
            Assert.Equal(
                LoginWithDeviceQuarantineStoreReadStatus.Absent,
                store.Read(secondBinding).Status);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void FakeProtection_UsesDedicatedEntropyAndClearsTransientBuffers()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var store = Store(path, protection);
        try
        {
            Assert.NotNull(Arm(store, Binding()));
            Assert.True(protection.SawExpectedEntropy);
            AssertCleared(protection.LastProtectPlaintext);
            AssertCleared(protection.LastProtectEntropy);
            AssertCleared(protection.LastProtectOutput);

            AssertFound(store.Read(Binding()));
            AssertCleared(protection.LastUnprotectProtectedData);
            AssertCleared(protection.LastUnprotectEntropy);
            AssertCleared(protection.LastUnprotectPlaintext);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [WindowsFact]
    public void CurrentUserDpapi_RoundTripsWithoutPersistingBindingPlaintext()
    {
        var path = NewPath();
        var binding = Binding();
        try
        {
            var store = new DpapiLoginWithDeviceQuarantineStore(path);
            var created = Assert.IsType<LoginWithDeviceQuarantineRecord>(Arm(store, binding));
            var file = File.ReadAllBytes(path);
            var email = Encoding.UTF8.GetBytes(binding.NormalizedEmail);
            var requestId = Encoding.UTF8.GetBytes(created.RequestId);
            try
            {
                Assert.False(ContainsSequence(file, email));
                Assert.False(ContainsSequence(file, requestId));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(requestId);
                CryptographicOperations.ZeroMemory(email);
                CryptographicOperations.ZeroMemory(file);
            }

            Assert.Equal(
                created,
                AssertFound(new DpapiLoginWithDeviceQuarantineStore(path).Read(binding)));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    private static LoginWithDeviceQuarantineRecord? Arm(
        DpapiLoginWithDeviceQuarantineStore store,
        LoginWithDeviceQuarantineBinding binding,
        string requestId = RequestId,
        DateTimeOffset? clearEligibleAt = null) => store.TryArm(
        binding,
        requestId,
        ArmedAt,
        ArmedAt.AddMinutes(5),
        clearEligibleAt ?? ArmedAt.AddHours(24));

    private static DpapiLoginWithDeviceQuarantineStore Store(
        string path,
        TestProtectionPolicy? protection = null,
        TestAtomicReplacePolicy? atomicReplace = null,
        TimeProvider? timeProvider = null) => new(
        path,
        protection ?? new TestProtectionPolicy(),
        atomicReplace ?? new TestAtomicReplacePolicy(),
        timeProvider);

    private static LoginWithDeviceQuarantineBinding Binding(
        string email = "user@example.com",
        ServerEndpoints? endpoints = null,
        string deviceIdentifier = DeviceId) => new(
        endpoints ?? Endpoints(),
        email,
        deviceIdentifier);

    private static ServerEndpoints Endpoints() =>
        ServerEndpoints.ForSelfHosted("https://vault.example/base");

    private static ServerEndpoints WithAlternateApi(ServerEndpoints endpoints) => new(
        endpoints.WebVault,
        new ServerEndpoint("https://api.example/custom"),
        endpoints.Identity,
        endpoints.Notifications,
        endpoints.Icons,
        endpoints.SendWeb);

    private static LoginWithDeviceQuarantineRecord AssertFound(
        LoginWithDeviceQuarantineStoreReadResult result)
    {
        Assert.Equal(LoginWithDeviceQuarantineStoreReadStatus.Found, result.Status);
        return Assert.IsType<LoginWithDeviceQuarantineRecord>(result.Record);
    }

    private static string NewPath() => Path.Combine(
        Path.GetTempPath(),
        "WinVaultWarden.Tests",
        Guid.NewGuid().ToString("N"),
        "login-with-device-quarantine.bin");

    private static void DeleteScope(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static void WriteProtectedJson(
        string path,
        TestProtectionPolicy protection,
        string json)
    {
        var plaintext = Encoding.UTF8.GetBytes(json);
        var entropy = CreateExpectedEntropy();
        var protectedBytes = protection.Protect(plaintext, entropy);
        var framed = new byte[8 + protectedBytes.Length];
        try
        {
            "WVWLDQ01"u8.CopyTo(framed);
            protectedBytes.CopyTo(framed, 8);
            File.WriteAllBytes(path, framed);
        }
        finally
        {
            Clear(framed);
            Clear(protectedBytes);
            Clear(entropy);
            Clear(plaintext);
        }
    }

    private static byte[] CreateExpectedEntropy()
    {
        var domain = Encoding.UTF8.GetBytes(
            "WinVaultWarden.LoginWithDeviceQuarantineLedgerProtection.v1");
        try
        {
            return SHA256.HashData(domain);
        }
        finally
        {
            Clear(domain);
        }
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
        {
            if (haystack.AsSpan(offset, needle.Length).SequenceEqual(needle))
                return true;
        }
        return false;
    }

    private static void AssertCleared(byte[]? bytes)
    {
        Assert.NotNull(bytes);
        Assert.All(bytes!, value => Assert.Equal(0, value));
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null)
            CryptographicOperations.ZeroMemory(bytes);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class UnsupportedProtectionPolicy
        : ILoginWithDeviceQuarantineProtectionPolicy
    {
        public bool IsSupported => false;

        public byte[] Protect(byte[] plaintext, byte[] entropy) =>
            throw new InvalidOperationException();

        public byte[] Unprotect(byte[] protectedData, byte[] entropy) =>
            throw new InvalidOperationException();
    }

    private sealed class TestAtomicReplacePolicy
        : ILoginWithDeviceQuarantineAtomicReplacePolicy
    {
        public bool IsSupported => true;

        public bool FailNext { get; set; }

        public void Replace(string sourcePath, string destinationPath)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new IOException("Injected atomic replacement failure.");
            }
            File.Move(sourcePath, destinationPath, overwrite: true);
        }
    }

    private sealed class TestProtectionPolicy
        : ILoginWithDeviceQuarantineProtectionPolicy
    {
        public bool IsSupported => true;

        public bool SawExpectedEntropy { get; private set; }

        public int UnprotectCalls { get; private set; }

        public byte[]? LastProtectPlaintext { get; private set; }

        public byte[]? LastProtectEntropy { get; private set; }

        public byte[]? LastProtectOutput { get; private set; }

        public byte[]? LastUnprotectProtectedData { get; private set; }

        public byte[]? LastUnprotectEntropy { get; private set; }

        public byte[]? LastUnprotectPlaintext { get; private set; }

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            VerifyEntropy(entropy);
            LastProtectPlaintext = plaintext;
            LastProtectEntropy = entropy;
            var output = new byte[entropy.Length + plaintext.Length];
            entropy.CopyTo(output, 0);
            for (var index = 0; index < plaintext.Length; index++)
            {
                output[entropy.Length + index] =
                    (byte)(plaintext[index] ^ entropy[index % entropy.Length]);
            }
            LastProtectOutput = output;
            return output;
        }

        public byte[] Unprotect(byte[] protectedData, byte[] entropy)
        {
            UnprotectCalls++;
            VerifyEntropy(entropy);
            LastUnprotectProtectedData = protectedData;
            LastUnprotectEntropy = entropy;
            if (protectedData.Length <= entropy.Length
                || !CryptographicOperations.FixedTimeEquals(
                    protectedData.AsSpan(0, entropy.Length),
                    entropy))
            {
                throw new CryptographicException("Protection entropy does not match.");
            }

            var plaintext = new byte[protectedData.Length - entropy.Length];
            for (var index = 0; index < plaintext.Length; index++)
            {
                plaintext[index] =
                    (byte)(protectedData[entropy.Length + index]
                        ^ entropy[index % entropy.Length]);
            }
            LastUnprotectPlaintext = plaintext;
            return plaintext;
        }

        private void VerifyEntropy(byte[] entropy)
        {
            var expected = CreateExpectedEntropy();
            try
            {
                SawExpectedEntropy = CryptographicOperations.FixedTimeEquals(
                    expected,
                    entropy);
                if (!SawExpectedEntropy)
                    throw new CryptographicException("Unexpected protection entropy.");
            }
            finally
            {
                Clear(expected);
            }
        }
    }
}
