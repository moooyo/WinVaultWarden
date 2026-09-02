using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Api;
using Api.Dtos;
using Core.Models;
using Core.Services;
using Crypto;
using Crypto.V2;
using Vault;
using Xunit;

namespace Vault.Tests;

public sealed class LoginWithDeviceServiceTests
{
    private const string RequestId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
    private const string DeviceId = "11111111-2222-4333-8444-555555555555";
    private static readonly ServerEndpoints Endpoints =
        ServerEndpoints.ForSelfHosted("https://vault.example");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task BeginChecksKnownDeviceBeforeCreatingSecrets()
    {
        var api = new FakePublicAuthRequestApi { KnownDevice = false };
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();

        var exception = await Assert.ThrowsAsync<LoginWithDeviceException>(() =>
            service.BeginAsync(
                attempt,
                Endpoints,
                "  USER@Example.COM ",
                DeviceId.ToUpperInvariant()));

        Assert.Equal(LoginWithDeviceFailureReason.UnknownDevice, exception.Reason);
        Assert.Equal(new[] { "known-device" }, api.Calls);
        Assert.Equal("user@example.com", api.KnownDeviceEmail);
        Assert.Equal(DeviceId, api.KnownDeviceIdentifier);
        Assert.Null(GetPending(service));
    }

    [Fact]
    public async Task BeginCreatesBoundRsaRequestAndFiveMinuteLifetime()
    {
        var api = new FakePublicAuthRequestApi();
        var (service, session, time) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();

        var result = await service.BeginAsync(
            attempt,
            Endpoints,
            "USER@Example.COM",
            DeviceId);

        Assert.Equal(RequestId, result.RequestId);
        Assert.Equal(Now.AddMinutes(5), result.ExpiresAtUtc);
        Assert.Equal(5, result.FingerprintPhrase.Split('-').Length);
        Assert.Equal(new[] { "known-device", "create" }, api.Calls);
        Assert.NotNull(api.CreateRequest);
        Assert.Equal(0, api.CreateRequest!.Type);
        Assert.Equal("user@example.com", api.CreateRequest.Email);
        Assert.Equal(DeviceId, api.CreateRequest.DeviceIdentifier);
        Assert.Equal(25, api.CreateRequest.AccessCode.Length);
        Assert.Contains(api.CreateRequest.AccessCode, char.IsUpper);
        Assert.Contains(api.CreateRequest.AccessCode, char.IsLower);
        Assert.Contains(api.CreateRequest.AccessCode, char.IsDigit);
        using var rsa = RSA.Create();
        var publicKey = Convert.FromBase64String(api.CreateRequest.PublicKey);
        try
        {
            rsa.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
            Assert.Equal(publicKey.Length, bytesRead);
            Assert.Equal(2048, rsa.KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(publicKey);
        }
        Assert.Equal(Now, time.GetUtcNow());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task PollTreatsNullOrFalseWithoutResponseDateAsPending(bool? approved)
    {
        var api = new FakePublicAuthRequestApi();
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        api.PollHandler = (_, _, _) => Task.FromResult(
            api.Response(approved: approved, responseDate: null));

        var result = await service.PollAsync(attempt);

        Assert.Equal(LoginWithDevicePollStatus.Pending, result.Status);
        Assert.Null(result.Grant);
        Assert.NotNull(GetPending(service));
    }

    [Fact]
    public async Task NetworkFailurePreservesPendingRequestForRetry()
    {
        var api = new FakePublicAuthRequestApi();
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var calls = 0;
        api.PollHandler = (_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new HttpRequestException("offline");
            return Task.FromResult(api.Response(approved: null));
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => service.PollAsync(attempt));
        var retry = await service.PollAsync(attempt);

        Assert.Equal(LoginWithDevicePollStatus.Pending, retry.Status);
        Assert.NotNull(GetPending(service));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TransportTimeoutPreservesPendingRequestForRetry()
    {
        var api = new FakePublicAuthRequestApi();
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var calls = 0;
        api.PollHandler = (_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                throw new TaskCanceledException("transport timeout");
            return Task.FromResult(api.Response(approved: null));
        };

        await Assert.ThrowsAsync<TaskCanceledException>(() => service.PollAsync(attempt));
        Assert.NotNull(GetPending(service));
        var retry = await service.PollAsync(attempt);

        Assert.Equal(LoginWithDevicePollStatus.Pending, retry.Status);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, LoginWithDevicePollStatus.Denied)]
    [InlineData(HttpStatusCode.NotFound, LoginWithDevicePollStatus.Expired)]
    public async Task MissingOrRejectedRequestTerminatesAndZerosSecrets(
        HttpStatusCode statusCode,
        LoginWithDevicePollStatus expected)
    {
        var api = new FakePublicAuthRequestApi();
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var (privateKey, accessCode) = GetPendingSecrets(service);
        api.PollHandler = (_, _, _) => throw new HttpRequestException(
            "terminal",
            null,
            statusCode);

        var result = await service.PollAsync(attempt);

        Assert.Equal(expected, result.Status);
        Assert.All(privateKey, value => Assert.Equal(0, value));
        Assert.All(accessCode, value => Assert.Equal('\0', value));
        Assert.Null(GetPending(service));
    }

    [Fact]
    public async Task TimerExpiryTerminatesAndZerosSecrets()
    {
        var api = new FakePublicAuthRequestApi();
        var (service, session, time) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var (privateKey, accessCode) = GetPendingSecrets(service);

        time.Advance(TimeSpan.FromMinutes(5));

        Assert.All(privateKey, value => Assert.Equal(0, value));
        Assert.All(accessCode, value => Assert.Equal('\0', value));
        var result = await service.PollAsync(attempt);
        Assert.Equal(LoginWithDevicePollStatus.Expired, result.Status);
        Assert.Equal(0, api.PollCallCount);
    }

    [Fact]
    public async Task DeniedResponseTerminatesAndZerosSecrets()
    {
        var api = new FakePublicAuthRequestApi();
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var (privateKey, accessCode) = GetPendingSecrets(service);
        api.PollHandler = (_, _, _) => Task.FromResult(api.Response(
            approved: false,
            responseDate: "2026-08-31T08:01:00Z"));

        var result = await service.PollAsync(attempt);

        Assert.Equal(LoginWithDevicePollStatus.Denied, result.Status);
        Assert.All(privateKey, value => Assert.Equal(0, value));
        Assert.All(accessCode, value => Assert.Equal('\0', value));
    }

    [Fact]
    public async Task ApprovedV1ResponseReturnsOneTimeGrantAndZerosEveryOwner()
    {
        var crypto = new CryptoService();
        var api = new FakePublicAuthRequestApi();
        var (service, session, _) = CreateService(api, crypto);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var (privateKey, pendingAccessCode) = GetPendingSecrets(service);
        var expectedKey = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        api.PollHandler = (_, _, _) => Task.FromResult(
            api.ApprovedResponse(crypto, expectedKey));

        var result = await service.PollAsync(attempt);

        Assert.Equal(LoginWithDevicePollStatus.Approved, result.Status);
        Assert.NotNull(result.Grant);
        var (grantKey, grantAccessCode, version) = GetGrantSecrets(result.Grant!);
        Assert.Equal(expectedKey, grantKey);
        Assert.Equal(1, version);
        Assert.Equal(api.CreateRequest!.AccessCode, new string(grantAccessCode));
        Assert.All(privateKey, value => Assert.Equal(0, value));
        Assert.Same(pendingAccessCode, grantAccessCode);
        Assert.All(pendingAccessCode, value => Assert.NotEqual('\0', value));

        var repeated = await Assert.ThrowsAsync<LoginWithDeviceException>(() =>
            service.PollAsync(attempt));
        Assert.Equal(LoginWithDeviceFailureReason.NoPendingRequest, repeated.Reason);
        Assert.Equal(1, api.PollCallCount);

        result.Grant!.Dispose();
        Assert.All(grantKey, value => Assert.Equal(0, value));
        Assert.All(grantAccessCode, value => Assert.Equal('\0', value));
        CryptographicOperations.ZeroMemory(expectedKey);
    }

    [Fact]
    public async Task ApprovedV2TransportIsValidatedBeforeGrant()
    {
        var crypto = new CryptoService();
        var api = new FakePublicAuthRequestApi();
        var (service, session, _) = CreateService(api, crypto);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        using var v2 = V2UserKey.Generate();
        var transport = v2.ExportTransport();
        api.PollHandler = (_, _, _) => Task.FromResult(
            api.ApprovedResponse(crypto, transport));

        var result = await service.PollAsync(attempt);

        var (grantKey, _, version) = GetGrantSecrets(result.Grant!);
        Assert.Equal(2, version);
        Assert.Equal(transport, grantKey);
        using (V2UserKey.ImportTransport(grantKey))
        {
        }
        result.Grant!.Dispose();
        Assert.All(grantKey, value => Assert.Equal(0, value));
        CryptographicOperations.ZeroMemory(transport);
    }

    [Fact]
    public async Task InvalidV2TransportTerminatesAndZerosDecryptedPayload()
    {
        var api = new FakePublicAuthRequestApi();
        byte[]? decrypted = null;
        var (service, session, _) = CreateService(
            api,
            decryptRsa: (_, _) =>
            {
                decrypted = RandomNumberGenerator.GetBytes(65);
                return decrypted;
            });
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        api.PollHandler = (_, _, _) => Task.FromResult(api.Response(
            approved: true,
            responseDate: "2026-08-31T08:01:00Z",
            key: "4." + Convert.ToBase64String(new byte[256])));

        var exception = await Assert.ThrowsAsync<LoginWithDeviceException>(() =>
            service.PollAsync(attempt));

        Assert.Equal(LoginWithDeviceFailureReason.InvalidServerResponse, exception.Reason);
        Assert.NotNull(decrypted);
        Assert.All(decrypted!, value => Assert.Equal(0, value));
        Assert.Null(GetPending(service));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("public-key")]
    [InlineData("device")]
    public async Task PollRejectsMismatchedResponseBindingAndZerosSecrets(string mismatch)
    {
        var api = new FakePublicAuthRequestApi();
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var (privateKey, accessCode) = GetPendingSecrets(service);
        api.PollHandler = (_, _, _) =>
        {
            var response = api.Response(approved: null);
            response = mismatch switch
            {
                "id" => response with { Id = "bbbbbbbb-cccc-4ddd-8eee-ffffffffffff" },
                "public-key" => response with { PublicKey = Convert.ToBase64String([1, 2, 3]) },
                "device" => response with
                {
                    RequestDeviceIdentifier = "99999999-8888-4777-8666-555555555555",
                },
                _ => response,
            };
            return Task.FromResult(response);
        };

        var exception = await Assert.ThrowsAsync<LoginWithDeviceException>(() =>
            service.PollAsync(attempt));

        Assert.Equal(LoginWithDeviceFailureReason.InvalidServerResponse, exception.Reason);
        Assert.All(privateKey, value => Assert.Equal(0, value));
        Assert.All(accessCode, value => Assert.Equal('\0', value));
        Assert.Null(GetPending(service));
    }

    [Fact]
    public async Task CancelImmediatelyZerosPendingSecrets()
    {
        var api = new FakePublicAuthRequestApi();
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var (privateKey, accessCode) = GetPendingSecrets(service);

        service.Cancel(attempt);

        Assert.All(privateKey, value => Assert.Equal(0, value));
        Assert.All(accessCode, value => Assert.Equal('\0', value));
        Assert.Null(GetPending(service));
    }

    [Fact]
    public async Task AttemptInvalidationZerosSecretsAndRejectsLateApproval()
    {
        var crypto = new CryptoService();
        var api = new FakePublicAuthRequestApi();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (service, session, _) = CreateService(api, crypto);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var (privateKey, accessCode) = GetPendingSecrets(service);
        var key = RandomNumberGenerator.GetBytes(64);
        api.PollHandler = async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return api.ApprovedResponse(crypto, key);
        };

        var poll = service.PollAsync(attempt);
        await entered.Task;
        _ = session.BeginAuthenticationAttempt();
        Assert.All(privateKey, value => Assert.Equal(0, value));
        Assert.All(accessCode, value => Assert.Equal('\0', value));
        release.TrySetResult();

        var exception = await Assert.ThrowsAsync<LoginWithDeviceException>(() => poll);
        Assert.Equal(LoginWithDeviceFailureReason.NoPendingRequest, exception.Reason);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task RedirectResponseTerminatesAsInvalidAndZerosSecrets()
    {
        var api = new FakePublicAuthRequestApi();
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var (privateKey, accessCode) = GetPendingSecrets(service);
        api.PollHandler = (_, _, _) => throw new HttpRequestException(
            "redirect",
            null,
            HttpStatusCode.Redirect);

        var exception = await Assert.ThrowsAsync<LoginWithDeviceException>(() =>
            service.PollAsync(attempt));

        Assert.Equal(LoginWithDeviceFailureReason.InvalidServerResponse, exception.Reason);
        Assert.All(privateKey, value => Assert.Equal(0, value));
        Assert.All(accessCode, value => Assert.Equal('\0', value));
        Assert.Null(GetPending(service));
    }

    [Fact]
    public async Task ConcurrentPollsUseOneNetworkOperationAtATime()
    {
        var api = new FakePublicAuthRequestApi();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        api.PollHandler = async (_, _, _) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, current);
            firstEntered.TrySetResult();
            try
            {
                await release.Task;
                return api.Response(approved: null);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        };

        var first = service.PollAsync(attempt);
        await firstEntered.Task;
        var second = service.PollAsync(attempt);
        await Task.Yield();
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result =>
            Assert.Equal(LoginWithDevicePollStatus.Pending, result.Status));
        Assert.Equal(1, maximumActive);
        Assert.Equal(2, api.PollCallCount);
    }

    [Fact]
    public async Task CallerCancellationZerosSecretsEvenWhenTransportReturnsLate()
    {
        var api = new FakePublicAuthRequestApi();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (service, session, _) = CreateService(api);
        var attempt = session.BeginAuthenticationAttempt();
        await service.BeginAsync(attempt, Endpoints, "user@example.com", DeviceId);
        var (privateKey, accessCode) = GetPendingSecrets(service);
        api.PollHandler = async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return api.Response(approved: null);
        };
        using var cancellation = new CancellationTokenSource();

        var poll = service.PollAsync(attempt, cancellation.Token);
        await entered.Task;
        cancellation.Cancel();

        Assert.All(privateKey, value => Assert.Equal(0, value));
        Assert.All(accessCode, value => Assert.Equal('\0', value));
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
    }

    [Fact]
    public void GrantDisposeZerosOwnedKeyAndAccessCode()
    {
        var userKey = RandomNumberGenerator.GetBytes(64);
        var grant = new LoginWithDeviceGrant(
            RequestId,
            "Aa0bcdefghijklmnopqrstuvw",
            "user@example.com",
            Endpoints,
            DeviceId,
            userKey,
            userKeySecurityVersion: 1);
        var (_, accessCode, _) = GetGrantSecrets(grant);

        grant.Dispose();
        grant.Dispose();

        Assert.All(userKey, value => Assert.Equal(0, value));
        Assert.All(accessCode, value => Assert.Equal('\0', value));
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private static (
        LoginWithDeviceService Service,
        VaultSession Session,
        ManualTimeProvider Time) CreateService(
        FakePublicAuthRequestApi api,
        CryptoService? crypto = null,
        Func<EncString, byte[], byte[]>? decryptRsa = null)
    {
        crypto ??= new CryptoService();
        var session = new VaultSession();
        var time = new ManualTimeProvider(Now);
        var provider = FakeVaultApiClientProvider
            .ForAnonymous<IPublicAuthRequestApiClient>(api);
        var service = decryptRsa is null
            ? new LoginWithDeviceService(
                provider,
                crypto,
                new FingerprintService(),
                session,
                time)
            : new LoginWithDeviceService(
                provider,
                crypto,
                new FingerprintService(),
                session,
                time,
                decryptRsa);
        return (service, session, time);
    }

    private static object? GetPending(LoginWithDeviceService service) =>
        typeof(LoginWithDeviceService)
            .GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service);

    private static (byte[] PrivateKey, char[] AccessCode) GetPendingSecrets(
        LoginWithDeviceService service)
    {
        var pending = GetPending(service) ?? throw new InvalidOperationException("No pending request.");
        var type = pending.GetType();
        return (
            (byte[])type.GetField("_privateKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(pending)!,
            (char[])type.GetField("_accessCode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(pending)!);
    }

    private static (byte[] UserKey, char[] AccessCode, int SecurityVersion) GetGrantSecrets(
        LoginWithDeviceGrant grant)
    {
        var type = typeof(LoginWithDeviceGrant);
        return (
            (byte[])type.GetField("_userKey", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(grant)!,
            (char[])type.GetField("_accessCode", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(grant)!,
            (int)type.GetProperty(
                "UserKeySecurityVersion",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(grant)!);
    }

    private sealed class FakePublicAuthRequestApi : IPublicAuthRequestApiClient
    {
        public bool KnownDevice { get; set; } = true;
        public string? KnownDeviceEmail { get; private set; }
        public string? KnownDeviceIdentifier { get; private set; }
        public AuthRequestRequest? CreateRequest { get; private set; }
        public List<string> Calls { get; } = [];
        public int PollCallCount { get; private set; }
        public Func<AuthRequestRequest, CancellationToken, Task<AuthRequestResponse>>? CreateHandler { get; set; }
        public Func<string, string, CancellationToken, Task<AuthRequestResponse>>? PollHandler { get; set; }

        public Task<bool> GetKnownDeviceAsync(
            string email,
            string deviceIdentifier,
            CancellationToken ct = default)
        {
            Calls.Add("known-device");
            KnownDeviceEmail = email;
            KnownDeviceIdentifier = deviceIdentifier;
            return Task.FromResult(KnownDevice);
        }

        public Task<AuthRequestResponse> CreateAsync(
            AuthRequestRequest request,
            CancellationToken ct = default)
        {
            Calls.Add("create");
            CreateRequest = request;
            return CreateHandler?.Invoke(request, ct)
                ?? Task.FromResult(Response(approved: false));
        }

        public Task<AuthRequestResponse> GetResponseAsync(
            string id,
            string code,
            CancellationToken ct = default)
        {
            Calls.Add("poll");
            PollCallCount++;
            return PollHandler?.Invoke(id, code, ct)
                ?? Task.FromResult(Response(approved: null));
        }

        public AuthRequestResponse ApprovedResponse(CryptoService crypto, byte[] userKey)
        {
            var publicKey = Convert.FromBase64String(CreateRequest!.PublicKey);
            try
            {
                return Response(
                    approved: true,
                    responseDate: "2026-08-31T08:01:00Z",
                    key: crypto.EncryptRsa(userKey, publicKey).ToString());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(publicKey);
            }
        }

        public AuthRequestResponse Response(
            bool? approved,
            string? responseDate = null,
            string? key = null) =>
            new(
                RequestId,
                CreateRequest?.PublicKey ?? string.Empty,
                "Windows",
                "192.0.2.1",
                key,
                null,
                "2026-08-31T08:00:00Z",
                responseDate,
                approved,
                Endpoints.WebVault.Value,
                CreateRequest?.DeviceIdentifier);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_gate)
                _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _utcNow += amount;
                due = _timers.Where(timer => timer.IsDue(_utcNow)).ToList();
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
                _timers.Remove(timer);
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private DateTimeOffset _dueAt;
            private TimeSpan _period;
            private bool _disposed;

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                _period = period;
                _dueAt = owner.GetUtcNow() + dueTime;
            }

            public bool IsDue(DateTimeOffset now) => !_disposed && now >= _dueAt;

            public void Fire()
            {
                if (_disposed)
                    return;
                if (_period == Timeout.InfiniteTimeSpan)
                    Dispose();
                else
                    _dueAt = _owner.GetUtcNow() + _period;
                _callback(_state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                    return false;
                _period = period;
                _dueAt = _owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
