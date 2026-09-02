using System.Security.Cryptography;
using System.Text;
using App.Services;
using Core.Abstractions;
using Core.Models;
using Xunit;

namespace App.Tests;

public sealed class DpapiAccountDeletionTombstoneStoreTests
{
    private const string DeviceId = "11111111-2222-3333-4444-555555555555";
    private static readonly DateTimeOffset CreatedAt =
        DateTimeOffset.Parse("2026-08-31T00:00:00Z");

    [Fact]
    public void Models_NormalizeBindingAndEnforceTimestampInvariants()
    {
        var binding = Binding(email: "  USER@Example.COM  ");
        Assert.Equal("user@example.com", binding.NormalizedEmail);
        Assert.Equal(DeviceId, binding.DeviceIdentifier);
        Assert.Equal(
            DeviceId,
            Binding(deviceIdentifier: DeviceId.ToUpperInvariant()).DeviceIdentifier);

        Assert.Throws<ArgumentException>(() => Binding(accountId: " account-1"));
        Assert.Throws<ArgumentException>(() => Binding(email: "   "));
        Assert.Throws<ArgumentException>(() => Binding(
            deviceIdentifier: Guid.Empty.ToString("D")));
        Assert.Throws<ArgumentException>(() => Binding(deviceIdentifier: "not-a-guid"));

        Assert.Throws<ArgumentException>(() => new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            1,
            CreatedAt,
            CreatedAt.AddTicks(-1),
            binding,
            AccountDeletionStage.Prepared));
        Assert.Throws<ArgumentException>(() => new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            1,
            CreatedAt,
            CreatedAt.AddTicks(1),
            binding,
            AccountDeletionStage.Prepared));

        var first = new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            1,
            CreatedAt,
            CreatedAt,
            binding,
            AccountDeletionStage.Prepared);
        var changedIdentity = new AccountDeletionTombstoneRecord(
            Guid.NewGuid(),
            1,
            CreatedAt,
            CreatedAt,
            Binding(
                email: "renamed@example.com",
                deviceIdentifier: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            AccountDeletionStage.Prepared);
        Assert.Throws<InvalidDataException>(() =>
            PersistedAccountDeletionTombstoneLedger.Create([first, changedIdentity]));
    }

    [Fact]
    public void RoundTrip_RestoresFullBindingAllowsDistinctEndpointScopesAndNeverExpires()
    {
        var path = NewPath();
        var firstBinding = Binding();
        var alternateBinding = Binding(
            accountId: firstBinding.AccountId,
            email: "other@example.com",
            endpoints: WithAlternateApi(Endpoints()),
            deviceIdentifier: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        try
        {
            var first = Store(path);
            var created = Assert.IsType<AccountDeletionTombstoneRecord>(
                first.TryCreate(firstBinding, CreatedAt.AddYears(-20)));
            var alternate = Assert.IsType<AccountDeletionTombstoneRecord>(
                first.TryCreate(alternateBinding, CreatedAt));
            Assert.Null(first.TryCreate(Binding(email: "USER@EXAMPLE.COM"), CreatedAt));

            var reopened = Store(path);
            Assert.Equal(created, AssertFound(reopened.Read(firstBinding)));
            Assert.Equal(alternate, AssertFound(reopened.Read(alternateBinding)));
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Absent,
                reopened.Read(Binding(endpoints: WithAlternateApi(Endpoints()))).Status);

            var all = reopened.ReadAll();
            Assert.Equal(AccountDeletionTombstoneReadStatus.Found, all.Status);
            Assert.Equal(new[] { created, alternate }, all.Records);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void AdvanceAndRemove_UseExactCasAndEnforceStageReasons()
    {
        var path = NewPath();
        var store = Store(path);
        var binding = Binding();
        try
        {
            var prepared = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryCreate(binding, CreatedAt));
            var wrongWitness = new AccountDeletionTombstoneRecord(
                prepared.EntryId,
                prepared.Revision,
                prepared.CreatedAt.AddTicks(1),
                prepared.UpdatedAt.AddTicks(1),
                prepared.Binding,
                prepared.Stage);

            Assert.Null(store.TryAdvance(
                wrongWitness,
                AccountDeletionStage.ServerConfirmed,
                CreatedAt.AddMinutes(1)));
            Assert.Throws<ArgumentException>(() => store.TryAdvance(
                prepared,
                AccountDeletionStage.ServerConfirmed,
                CreatedAt.AddTicks(-1)));

            var confirmed = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryAdvance(
                    prepared,
                    AccountDeletionStage.ServerConfirmed,
                    CreatedAt.AddMinutes(1)));
            Assert.Equal(2, confirmed.Revision);
            Assert.Equal(AccountDeletionStage.ServerConfirmed, confirmed.Stage);
            Assert.Null(store.TryAdvance(
                prepared,
                AccountDeletionStage.ServerConfirmed,
                CreatedAt.AddMinutes(2)));
            Assert.Throws<ArgumentException>(() => store.TryAdvance(
                confirmed,
                AccountDeletionStage.ServerConfirmed,
                CreatedAt.AddMinutes(2)));

            Assert.False(store.Remove(
                prepared,
                AccountDeletionTombstoneRemovalReason.ServerRejectedPreparedRequest));
            foreach (var reason in Enum.GetValues<AccountDeletionTombstoneRemovalReason>())
                Assert.False(store.Remove(confirmed, reason));

            var rejected = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryCreate(
                    Binding(accountId: "account-rejected"),
                    CreatedAt.AddHours(1)));
            Assert.True(store.Remove(
                rejected,
                AccountDeletionTombstoneRemovalReason.ServerRejectedPreparedRequest));

            var userCleared = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryCreate(
                    Binding(accountId: "account-user-cleared"),
                    CreatedAt.AddHours(2)));
            userCleared = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryAdvance(
                    userCleared,
                    AccountDeletionStage.UncertainLocalCleanupComplete,
                    CreatedAt.AddHours(2).AddMinutes(1)));
            Assert.Equal(
                AccountDeletionStage.UncertainLocalCleanupComplete,
                userCleared.Stage);
            foreach (var reason in Enum.GetValues<AccountDeletionTombstoneRemovalReason>())
                Assert.False(store.Remove(userCleared, reason));

            var cancelled = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryCreate(
                    Binding(accountId: "account-cancelled"),
                    CreatedAt.AddHours(3)));
            Assert.True(store.Remove(
                cancelled,
                AccountDeletionTombstoneRemovalReason.CancelledBeforeDispatch));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void LocalCleanupComplete_RoundTripsAndCannotBeRemovedOrAdvanced()
    {
        var path = NewPath();
        var binding = Binding();
        try
        {
            var store = Store(path);
            var prepared = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryCreate(binding, CreatedAt));
            Assert.Throws<ArgumentException>(() => store.TryAdvance(
                prepared,
                AccountDeletionStage.LocalCleanupComplete,
                CreatedAt.AddMinutes(1)));

            var confirmed = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryAdvance(
                    prepared,
                    AccountDeletionStage.ServerConfirmed,
                    CreatedAt.AddMinutes(1)));
            var complete = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryAdvance(
                    confirmed,
                    AccountDeletionStage.LocalCleanupComplete,
                    CreatedAt.AddMinutes(2)));
            Assert.Equal(3, complete.Revision);
            Assert.Equal(AccountDeletionStage.LocalCleanupComplete, complete.Stage);
            Assert.Null(store.TryAdvance(
                confirmed,
                AccountDeletionStage.LocalCleanupComplete,
                CreatedAt.AddMinutes(3)));
            Assert.Throws<ArgumentException>(() => store.TryAdvance(
                complete,
                AccountDeletionStage.LocalCleanupComplete,
                CreatedAt.AddMinutes(3)));

            var reopened = Store(path);
            Assert.Equal(complete, AssertFound(reopened.Read(binding)));
            foreach (var reason in Enum.GetValues<AccountDeletionTombstoneRemovalReason>())
                Assert.False(reopened.Remove(complete, reason));
            Assert.Equal(complete, AssertFound(reopened.Read(binding)));

            var uncertainBinding = Binding(accountId: "account-uncertain");
            var uncertain = Assert.IsType<AccountDeletionTombstoneRecord>(
                reopened.TryCreate(uncertainBinding, CreatedAt.AddHours(1)));
            uncertain = Assert.IsType<AccountDeletionTombstoneRecord>(
                reopened.TryAdvance(
                    uncertain,
                    AccountDeletionStage.UncertainLocalCleanupComplete,
                    CreatedAt.AddHours(1).AddMinutes(1)));
            Assert.Equal(2, uncertain.Revision);
            Assert.Throws<ArgumentException>(() => reopened.TryAdvance(
                uncertain,
                AccountDeletionStage.ServerConfirmed,
                CreatedAt.AddHours(1).AddMinutes(2)));
            foreach (var reason in Enum.GetValues<AccountDeletionTombstoneRemovalReason>())
                Assert.False(reopened.Remove(uncertain, reason));

            var reopenedAgain = Store(path);
            Assert.Equal(complete, AssertFound(reopenedAgain.Read(binding)));
            Assert.Equal(uncertain, AssertFound(reopenedAgain.Read(uncertainBinding)));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task CrossInstanceCreate_AllowsOnlyOneRecordPerAccountEndpointScope()
    {
        var path = NewPath();
        var firstBinding = Binding();
        var changedIdentityBinding = Binding(
            email: "renamed@example.com",
            deviceIdentifier: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var first = Store(path);
        var second = Store(path);
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        try
        {
            Task<AccountDeletionTombstoneRecord?> CreateAsync(
                DpapiAccountDeletionTombstoneStore store,
                AccountDeletionBinding binding) => Task.Run(() =>
            {
                ready.Signal();
                start.Wait(CancellationToken.None);
                return store.TryCreate(binding, CreatedAt);
            });

            var firstAttempt = CreateAsync(first, firstBinding);
            var secondAttempt = CreateAsync(second, changedIdentityBinding);
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            start.Set();
            var results = await Task.WhenAll(firstAttempt, secondAttempt);

            var winner = Assert.Single(results, result => result is not null)!;
            Assert.Single(first.ReadAll().Records);
            Assert.True(
                winner.Binding == firstBinding
                || winner.Binding == changedIdentityBinding);
            var loserBinding = winner.Binding == firstBinding
                ? changedIdentityBinding
                : firstBinding;
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Absent,
                first.Read(loserBinding).Status);
        }
        finally
        {
            start.Set();
            DeleteScope(path);
        }
    }

    [Fact]
    public async Task CrossInstanceAdvance_AllowsOnlyOneExactCasWinner()
    {
        var path = NewPath();
        var binding = Binding();
        var creator = Store(path);
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        try
        {
            var prepared = Assert.IsType<AccountDeletionTombstoneRecord>(
                creator.TryCreate(binding, CreatedAt));
            var first = Store(path);
            var second = Store(path);

            Task<AccountDeletionTombstoneRecord?> AdvanceAsync(
                DpapiAccountDeletionTombstoneStore store) => Task.Run(() =>
            {
                ready.Signal();
                start.Wait(CancellationToken.None);
                return store.TryAdvance(
                    prepared,
                    AccountDeletionStage.ServerConfirmed,
                    CreatedAt.AddMinutes(1));
            });

            var firstAttempt = AdvanceAsync(first);
            var secondAttempt = AdvanceAsync(second);
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
            start.Set();
            var results = await Task.WhenAll(firstAttempt, secondAttempt);

            var winner = Assert.Single(results, result => result is not null)!;
            Assert.Equal(winner, AssertFound(creator.Read(binding)));
            Assert.Equal(2, winner.Revision);
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
        var firstBinding = Binding(
            accountId: "account-0",
            deviceIdentifier: Guid.NewGuid().ToString("D"));
        try
        {
            var permanent = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryCreate(firstBinding, CreatedAt));
            permanent = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryAdvance(
                    permanent,
                    AccountDeletionStage.UncertainLocalCleanupComplete,
                    CreatedAt.AddMinutes(1)));
            for (var index = 1; index < PersistedAccountDeletionTombstoneLedger.MaximumEntries; index++)
            {
                Assert.NotNull(store.TryCreate(
                    Binding(
                        accountId: $"account-{index}",
                        deviceIdentifier: Guid.NewGuid().ToString("D")),
                    CreatedAt));
            }

            Assert.Throws<InvalidDataException>(() => store.TryCreate(
                Binding(
                    accountId: "account-overflow",
                    deviceIdentifier: Guid.NewGuid().ToString("D")),
                CreatedAt));
            Assert.Equal(permanent, AssertFound(store.Read(firstBinding)));
            Assert.Equal(
                PersistedAccountDeletionTombstoneLedger.MaximumEntries,
                store.ReadAll().Records.Count);
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void ReadAndReadAll_DistinguishAbsentCorruptAndUnavailable()
    {
        var path = NewPath();
        var binding = Binding();
        var protection = new TestProtectionPolicy();
        var store = Store(path, protection);
        byte[]? corrupt = null;
        try
        {
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Absent,
                store.Read(binding).Status);
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Absent,
                store.ReadAll().Status);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            corrupt = "WVWADT01corrupt"u8.ToArray();
            File.WriteAllBytes(path, corrupt);
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Corrupt,
                store.Read(binding).Status);
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Corrupt,
                store.ReadAll().Status);
            Assert.ThrowsAny<CryptographicException>(() =>
                store.TryCreate(binding, CreatedAt));
            Assert.Equal(corrupt, File.ReadAllBytes(path));

            WriteProtectedJson(
                path,
                protection,
                "{\"format\":\"WinVaultWarden.AccountDeletionTombstoneLedger\",\"schemaVersion\":1,\"entries\":[],\"refreshToken\":\"must-not-be-accepted\"}");
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Corrupt,
                store.ReadAll().Status);

            WriteProtectedJson(
                path,
                protection,
                "{\"Format\":\"WinVaultWarden.AccountDeletionTombstoneLedger\",\"schemaVersion\":1,\"entries\":[]}");
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Corrupt,
                store.ReadAll().Status);

            WriteProtectedJson(
                path,
                protection,
                "{\"format\":\"WinVaultWarden.AccountDeletionTombstoneLedger\",\"schemaVersion\":1,\"entries\":[],\"entries\":[]}");
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Corrupt,
                store.ReadAll().Status);

            using (var oversized = new FileStream(path, FileMode.Create, FileAccess.Write))
                oversized.SetLength(1024L * 1024 + 1);
            var unprotectCalls = protection.UnprotectCalls;
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Corrupt,
                store.ReadAll().Status);
            Assert.Equal(unprotectCalls, protection.UnprotectCalls);

            var unsupportedPath = NewPath();
            try
            {
                var unsupported = new DpapiAccountDeletionTombstoneStore(
                    unsupportedPath,
                    new UnsupportedProtectionPolicy(),
                    new TestAtomicReplacePolicy());
                Assert.Equal(
                    AccountDeletionTombstoneReadStatus.Unavailable,
                    unsupported.Read(binding).Status);
                Assert.Equal(
                    AccountDeletionTombstoneReadStatus.Unavailable,
                    unsupported.ReadAll().Status);
                Assert.Throws<PlatformNotSupportedException>(() =>
                    unsupported.TryCreate(binding, CreatedAt));
                Assert.False(Directory.Exists(Path.GetDirectoryName(unsupportedPath)));
            }
            finally
            {
                DeleteScope(unsupportedPath);
            }
        }
        finally
        {
            Clear(corrupt);
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
            accountId: "account-2",
            email: "other@example.com",
            deviceIdentifier: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        try
        {
            var first = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryCreate(firstBinding, CreatedAt));
            var oldFile = File.ReadAllBytes(path);
            replacement.FailNext = true;

            Assert.Throws<IOException>(() =>
                store.TryCreate(secondBinding, CreatedAt));
            Assert.Equal(oldFile, File.ReadAllBytes(path));
            Assert.Equal(first, AssertFound(store.Read(firstBinding)));
            Assert.Equal(
                AccountDeletionTombstoneReadStatus.Absent,
                store.Read(secondBinding).Status);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    [Fact]
    public void FakeProtection_UsesDedicatedEntropyAndPersistsNoCredentialFields()
    {
        var path = NewPath();
        var protection = new TestProtectionPolicy();
        var store = Store(path, protection);
        try
        {
            Assert.NotNull(store.TryCreate(Binding(), CreatedAt));
            Assert.True(protection.SawExpectedEntropy);
            Assert.NotNull(protection.LastProtectedJson);
            Assert.DoesNotContain("passwordHash", protection.LastProtectedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("otp", protection.LastProtectedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("refreshToken", protection.LastProtectedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("accessToken", protection.LastProtectedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("userKey", protection.LastProtectedJson, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void ProductionSource_UsesDurableWriteThroughReplacement()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "App",
            "Services",
            "DpapiAccountDeletionTombstoneStore.cs"));

        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("FileOptions.WriteThrough", source, StringComparison.Ordinal);
        Assert.Contains("stream.Flush(flushToDisk: true)", source, StringComparison.Ordinal);
        Assert.Contains(
            "MoveFileFlags.ReplaceExisting | MoveFileFlags.WriteThrough",
            source,
            StringComparison.Ordinal);
        Assert.Contains("FileShare.None", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionSource_RegistersStoreAndGeneratedJsonMetadata()
    {
        var root = FindRepositoryRoot();
        var services = File.ReadAllText(Path.Combine(
            root,
            "src",
            "App",
            "Services",
            "ServiceConfiguration.cs"));
        var context = File.ReadAllText(Path.Combine(
            root,
            "src",
            "App",
            "Services",
            "AppJsonContext.cs"));

        Assert.Contains(
            "AddSingleton<IAccountDeletionTombstoneStore",
            services,
            StringComparison.Ordinal);
        Assert.Contains(
            "DpapiAccountDeletionTombstoneStore",
            services,
            StringComparison.Ordinal);
        Assert.Contains(
            "JsonSerializable(typeof(PersistedAccountDeletionTombstoneLedger))",
            context,
            StringComparison.Ordinal);
    }

    [WindowsFact]
    public void CurrentUserDpapi_RoundTripsWithoutPersistingBindingPlaintext()
    {
        var path = NewPath();
        var binding = Binding();
        try
        {
            var store = new DpapiAccountDeletionTombstoneStore(path);
            var created = Assert.IsType<AccountDeletionTombstoneRecord>(
                store.TryCreate(binding, CreatedAt));
            var file = File.ReadAllBytes(path);
            var accountId = Encoding.UTF8.GetBytes(binding.AccountId);
            var email = Encoding.UTF8.GetBytes(binding.NormalizedEmail);
            try
            {
                Assert.False(ContainsSequence(file, accountId));
                Assert.False(ContainsSequence(file, email));
            }
            finally
            {
                Clear(email);
                Clear(accountId);
                Clear(file);
            }

            var reopened = new DpapiAccountDeletionTombstoneStore(path);
            Assert.Equal(created, AssertFound(reopened.Read(binding)));
            Assert.Equal(
                created,
                Assert.Single(reopened.ReadAll().Records));
        }
        finally
        {
            DeleteScope(path);
        }
    }

    private static DpapiAccountDeletionTombstoneStore Store(
        string path,
        TestProtectionPolicy? protection = null,
        TestAtomicReplacePolicy? atomicReplace = null) => new(
        path,
        protection ?? new TestProtectionPolicy(),
        atomicReplace ?? new TestAtomicReplacePolicy());

    private static AccountDeletionBinding Binding(
        string accountId = "account-1",
        string email = "user@example.com",
        ServerEndpoints? endpoints = null,
        string deviceIdentifier = DeviceId) => new(
        accountId,
        email,
        endpoints ?? Endpoints(),
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

    private static AccountDeletionTombstoneRecord AssertFound(
        AccountDeletionTombstoneReadResult result)
    {
        Assert.Equal(AccountDeletionTombstoneReadStatus.Found, result.Status);
        return Assert.IsType<AccountDeletionTombstoneRecord>(result.Record);
    }

    private static string NewPath() => Path.Combine(
        Path.GetTempPath(),
        "WinVaultWarden.Tests",
        Guid.NewGuid().ToString("N"),
        "account-deletion-tombstones.bin");

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
            "WVWADT01"u8.CopyTo(framed);
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
            "WinVaultWarden.AccountDeletionTombstoneLedgerProtection.v1");
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

    private sealed class UnsupportedProtectionPolicy
        : IAccountDeletionTombstoneProtectionPolicy
    {
        public bool IsSupported => false;

        public byte[] Protect(byte[] plaintext, byte[] entropy) =>
            throw new InvalidOperationException();

        public byte[] Unprotect(byte[] protectedData, byte[] entropy) =>
            throw new InvalidOperationException();
    }

    private sealed class TestAtomicReplacePolicy
        : IAccountDeletionTombstoneAtomicReplacePolicy
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
        : IAccountDeletionTombstoneProtectionPolicy
    {
        public bool IsSupported => true;

        public bool SawExpectedEntropy { get; private set; }
        public int UnprotectCalls { get; private set; }
        public string? LastProtectedJson { get; private set; }
        public byte[]? LastProtectPlaintext { get; private set; }
        public byte[]? LastProtectEntropy { get; private set; }
        public byte[]? LastProtectOutput { get; private set; }
        public byte[]? LastUnprotectProtectedData { get; private set; }
        public byte[]? LastUnprotectEntropy { get; private set; }
        public byte[]? LastUnprotectPlaintext { get; private set; }

        public byte[] Protect(byte[] plaintext, byte[] entropy)
        {
            VerifyEntropy(entropy);
            LastProtectedJson = Encoding.UTF8.GetString(plaintext);
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
