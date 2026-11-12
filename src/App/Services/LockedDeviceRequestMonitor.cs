using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Api;
using Api.Dtos;
using App.ViewModels.Models;
using Core.Models;
using Core.Session;
using Crypto;
using Vault;

namespace App.Services;

public sealed record LockedDeviceRequestAccount(string AccountId, ServerEndpoints Endpoints, string Email);

/// <summary>Contains confirmed public request metadata, never an authorization token or vault key.</summary>
public sealed record LockedDeviceRequestNotice(Guid Epoch, LockedDeviceRequestAccount Account, AuthRequestItem Request);

/// <summary>
/// An explicit opt-in account credential scope for read-only request monitoring while locked.
/// Its memory-only access token cannot be refreshed. Sensitive-state cleanup removes all
/// presentation and unlock intent; logout, account switching and opt-out must also call Stop.
/// </summary>
public sealed class LockedDeviceRequestMonitor : ISensitiveState, IDisposable
{
    private readonly object _gate = new();
    private readonly VaultSession _session;
    private readonly IApiClientFactory _factory;
    private readonly IVaultApiClientProvider _unlockedClients;
    private readonly FingerprintService _fingerprint;
    private readonly TimeProvider _clock;
    private readonly Func<bool> _canPresent;
    private readonly TimeSpan _interval;
    private Scope? _scope;
    private LockedDeviceRequestNotice[] _notices = [];
    private LockedDeviceRequestNotice? _unlockIntent;
    private long _presentationGeneration;
    private bool _disposed;

    public LockedDeviceRequestMonitor(
        VaultSession session,
        IApiClientFactory factory,
        IVaultApiClientProvider unlockedClients,
        FingerprintService fingerprint,
        Func<bool> canPresent,
        TimeProvider? clock = null,
        TimeSpan? pollInterval = null)
    {
        _session = session;
        _factory = factory;
        _unlockedClients = unlockedClients;
        _fingerprint = fingerprint;
        _canPresent = canPresent;
        _clock = clock ?? TimeProvider.System;
        _interval = pollInterval ?? TimeSpan.FromSeconds(15);
        if (_interval < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public event Action? NoticesChanged;
    public event Action<LockedDeviceRequestAccount>? UnlockRequested;

    public IReadOnlyList<LockedDeviceRequestNotice> Notices
    {
        get { lock (_gate) return Array.AsReadOnly(_notices); }
    }

    public bool IsActive
    {
        get
        {
            Scope? scope;
            lock (_gate) scope = _scope;
            return scope is not null && CanUse(scope);
        }
    }

    /// <summary>Called after a real online unlock, before ordinary lock can erase the session token.</summary>
    public bool ArmFromUnlocked(VaultSessionLease lease, bool explicitlyEnabled)
    {
        if (!explicitlyEnabled)
        {
            Stop();
            return false;
        }
        if (_disposed)
            return false;
        Scope? created = null;
        Scope? replaced = null;
        var entered = false;
        _session.TryRunIfUnlocked(lease, () =>
        {
            entered = true;
            var authorization = _session.GetAccessTokenBinding(lease);
            var email = _session.Account.Email;
            if (!_session.IsDemo && authorization is not null && !string.IsNullOrWhiteSpace(email)
                && TryReadExpiry(authorization, _clock.GetUtcNow(), out var expiry))
            {
                var account = new LockedDeviceRequestAccount(lease.AccountId, lease.Endpoints, email);
                created = new Scope(account, authorization, expiry);
                var captured = created;
                captured.Client = _factory.CreateAuthenticated(
                    lease.Endpoints,
                    () => ReadAuthorization(captured),
                    (_, _) => Task.FromResult(false));
            }
            lock (_gate)
            {
                if (!_disposed)
                {
                    replaced = _scope;
                    _scope = created;
                    _notices = [];
                    _unlockIntent = null;
                    _presentationGeneration++;
                }
            }
        });
        replaced?.Dispose();
        if (entered)
            RaiseNoticesChanged();
        if (created is null)
            return false;
        lock (_gate)
            if (ReferenceEquals(_scope, created))
                return true;
        created.Dispose();
        return false;
    }

    public void StartPolling()
    {
        lock (_gate)
        {
            if (_scope is not { Loop: null } scope)
                return;
            scope.Loop = Task.Run(() => PollAsync(scope));
        }
    }

    private async Task PollAsync(Scope scope)
    {
        try
        {
            while (!scope.Token.IsCancellationRequested)
            {
                await RefreshAsync(scope, scope.Token).ConfigureAwait(false);
                if (!CanUse(scope))
                {
                    Stop(scope);
                    return;
                }
                await Task.Delay(_interval, _clock, scope.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (scope.Token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Stop(scope); }
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        Scope? scope;
        lock (_gate) scope = _scope;
        return scope is null ? Task.CompletedTask : RefreshAsync(scope, ct);
    }

    private async Task RefreshAsync(Scope scope, CancellationToken ct)
    {
        if (!CanUse(scope))
        {
            Stop(scope);
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, scope.Token);
        var entered = false;
        long presentation;
        lock (_gate) presentation = _presentationGeneration;
        try
        {
            await scope.RequestGate.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            if (!CanUse(scope))
            {
                Stop(scope);
                return;
            }
            var response = await scope.Client!.GetPendingAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (!CanUse(scope))
            {
                Stop(scope);
                return;
            }
            if (response.Data.Count > 256)
                throw new InvalidDataException("The device request metadata list exceeds its limit.");
            var notices = new List<LockedDeviceRequestNotice>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var request in response.Data)
            {
                if (!TryProject(request, scope.Account.Email, out var item))
                    continue;
                if (!ids.Add(item.Id))
                    throw new InvalidDataException("The device request metadata list contains duplicate identifiers.");
                notices.Add(new(scope.Epoch, scope.Account, item));
            }
            var present = _session.State == VaultState.Locked && _canPresent();
            lock (_gate)
            {
                if (!ReferenceEquals(_scope, scope) || linked.IsCancellationRequested
                    || presentation != _presentationGeneration)
                    return;
                if (_unlockIntent?.Request.ExpiresAt <= _clock.GetUtcNow())
                    _unlockIntent = null;
                _notices = present && _unlockIntent is null
                    ? notices.Select(notice => _notices.FirstOrDefault(previous =>
                        previous.Epoch == notice.Epoch && Matches(previous.Request, notice.Request)) ?? notice).ToArray()
                    : [];
            }
            RaiseNoticesChanged();
        }
        catch (OperationCanceledException) when (scope.Token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A cancellation or failed metadata request stops this credential scope; no retry
            // refreshes authentication material or keeps stale requests on screen.
            Stop(scope);
        }
        finally
        {
            if (entered)
                scope.RequestGate.Release();
        }
    }

    public bool RequestUnlock(LockedDeviceRequestNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Scope? scope;
        lock (_gate) scope = _scope;
        if (scope is null || !CanUse(scope) || _session.State != VaultState.Locked || !_canPresent()
            || notice.Request.ExpiresAt <= _clock.GetUtcNow())
            return false;
        lock (_gate)
        {
            if (!ReferenceEquals(_scope, scope) || notice.Epoch != scope.Epoch
                || !_notices.Any(candidate => ReferenceEquals(candidate, notice)))
                return false;
            _unlockIntent = notice;
            _notices = [];
        }
        RaiseNoticesChanged();
        try { UnlockRequested?.Invoke(scope.Account); }
        catch
        {
            lock (_gate) _unlockIntent = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Consumes a single metadata-only intent after real account unlock. Both endpoints are
    /// reloaded using the new unlocked session; this method never approves a request.
    /// </summary>
    public async Task<UiOperationResult<AuthRequestItem>> ResumeAfterUnlockAsync(
        VaultSessionLease lease,
        CancellationToken ct = default)
    {
        LockedDeviceRequestNotice? intent;
        Scope? scope;
        long presentation;
        lock (_gate)
        {
            intent = _unlockIntent;
            _unlockIntent = null;
            scope = _scope;
            presentation = _presentationGeneration;
        }
        if (intent is null || scope is null || intent.Epoch != scope.Epoch || _clock.GetUtcNow() >= scope.Expiry
            || lease.AccountId != intent.Account.AccountId || lease.Endpoints != intent.Account.Endpoints
            || !string.Equals(_session.Account.Email, intent.Account.Email, StringComparison.OrdinalIgnoreCase)
            || !_session.IsUnlocked(lease) || intent.Request.ExpiresAt <= _clock.GetUtcNow())
            return UiOperationResult<AuthRequestItem>.Cancelled();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken, scope.Token);
        try
        {
            var client = _unlockedClients.GetAuthenticated<IAuthRequestApiClient>(lease);
            var pending = await client.GetPendingAsync(linked.Token).ConfigureAwait(false);
            if (pending.Data.Count > 256)
                return UiOperationResult<AuthRequestItem>.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1684CAFBF878DD8F));
            var matching = pending.Data.Where(request => request.Id == intent.Request.Id).ToArray();
            if (matching.Length != 1 || !TryProject(matching[0], intent.Account.Email, out var listed)
                || !Matches(intent.Request, listed))
                return UiOperationResult<AuthRequestItem>.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD2F21F97CE97B700));
            var response = await client.GetAsync(intent.Request.Id, linked.Token).ConfigureAwait(false);
            if (!TryProject(response, intent.Account.Email, out var fresh) || !Matches(intent.Request, fresh))
                return UiOperationResult<AuthRequestItem>.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD2F21F97CE97B700));
            linked.Token.ThrowIfCancellationRequested();
            var committed = false;
            _session.TryRunIfUnlocked(lease, () =>
            {
                lock (_gate)
                {
                    var now = _clock.GetUtcNow();
                    committed = ReferenceEquals(_scope, scope) && presentation == _presentationGeneration
                        && !linked.IsCancellationRequested && now < scope.Expiry
                        && intent.Request.ExpiresAt > now && fresh.ExpiresAt > now;
                }
            });
            if (!committed)
                return UiOperationResult<AuthRequestItem>.Cancelled();
            return UiOperationResult<AuthRequestItem>.Success(fresh);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return UiOperationResult<AuthRequestItem>.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Stop(scope);
            return UiOperationResult<AuthRequestItem>.Failure(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0C31FD422A917A7A));
        }
    }

    private static bool Matches(AuthRequestItem reviewed, AuthRequestItem fresh) =>
        reviewed.Id == fresh.Id && reviewed.PublicKey == fresh.PublicKey
        && reviewed.FingerprintPhrase == fresh.FingerprintPhrase && reviewed.CreatedAt == fresh.CreatedAt
        && reviewed.ExpiresAt == fresh.ExpiresAt && reviewed.DeviceTypeName == fresh.DeviceTypeName
        && reviewed.IpAddress == fresh.IpAddress;

    private bool TryProject(AuthRequestResponse response, string email, out AuthRequestItem item)
    {
        item = null!;
        var now = _clock.GetUtcNow();
        if (string.IsNullOrWhiteSpace(response.Id) || response.Id.Length > 128
            || response.RequestApproved is not null || response.ResponseDate is not null
            || !string.IsNullOrEmpty(response.Key) || !string.IsNullOrEmpty(response.MasterPasswordHash)
            || string.IsNullOrWhiteSpace(response.RequestDeviceType) || response.RequestDeviceType.Length > 128
            || response.RequestIpAddress is not { Length: <= 256 }
            || response.PublicKey is not { Length: > 0 and <= 4096 }
            || !DateTimeOffset.TryParse(response.CreationDate, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var created)
            || created > now.AddMinutes(1) || created <= now.AddMinutes(-15))
            return false;
        byte[]? encoded = null;
        byte[]? canonical = null;
        try
        {
            encoded = Convert.FromBase64String(response.PublicKey);
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(encoded, out var read);
            if (read != encoded.Length || rsa.KeySize != 2048)
                return false;
            canonical = rsa.ExportSubjectPublicKeyInfo();
            item = new(response.Id, response.RequestDeviceType, response.RequestIpAddress,
                created.ToLocalTime().ToString("yyyy/M/d HH:mm", CultureInfo.InvariantCulture), Convert.ToBase64String(canonical))
            {
                CreatedAt = created,
                FingerprintPhrase = _fingerprint.GetFingerprintPhrase(canonical, email),
            };
            return true;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException) { return false; }
        finally
        {
            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
            if (canonical is not null) CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private AccessTokenBinding? ReadAuthorization(Scope scope)
    {
        if (!CanUse(scope))
            return null;
        lock (_gate)
            return ReferenceEquals(_scope, scope) ? scope.Authorization : null;
    }

    private bool CanUse(Scope scope)
    {
        if (scope.Token.IsCancellationRequested || _clock.GetUtcNow() >= scope.Expiry)
            return false;
        lock (_gate)
            if (!ReferenceEquals(_scope, scope))
                return false;
        try
        {
            var current = _session.CaptureLease();
            return current.AccountId == scope.Account.AccountId && current.Endpoints == scope.Account.Endpoints
                && _session.State is VaultState.Locked or VaultState.Unlocked;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static bool TryReadExpiry(AccessTokenBinding binding, DateTimeOffset now, out DateTimeOffset expiry)
    {
        expiry = default;
        if (binding.Token.Length > 32768)
            return false;
        byte[]? bytes = null;
        try
        {
            if (!AccessTokenSubjectReader.TryReadAccountId(binding.Token, out var subject) || subject != binding.AccountId)
                return false;
            var payload = binding.Token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            bytes = Convert.FromBase64String(payload);
            using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (!json.RootElement.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number
                || !exp.TryGetInt64(out var seconds))
                return false;
            expiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (expiry > now.AddHours(2)) expiry = now.AddHours(2);
            return expiry > now;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentOutOfRangeException or InvalidOperationException) { return false; }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    public void ClearSensitive()
    {
        lock (_gate)
        {
            _notices = [];
            _unlockIntent = null;
            _presentationGeneration++;
        }
        RaiseNoticesChanged();
    }

    public void Stop()
    {
        Scope? scope;
        lock (_gate)
        {
            scope = _scope;
            _scope = null;
            _notices = [];
            _unlockIntent = null;
            _presentationGeneration++;
        }
        scope?.Dispose();
        RaiseNoticesChanged();
    }

    private void Stop(Scope expected)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_scope, expected))
                return;
            _scope = null;
            _notices = [];
            _unlockIntent = null;
            _presentationGeneration++;
        }
        expected.Dispose();
        RaiseNoticesChanged();
    }

    private void RaiseNoticesChanged()
    {
        if (NoticesChanged is { } changed)
            foreach (Action handler in changed.GetInvocationList())
                try { handler(); }
                catch { /* Presentation failures cannot retain an invalid credential scope. */ }
    }

    public void Dispose() { _disposed = true; Stop(); }

    private sealed class Scope : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        public Scope(LockedDeviceRequestAccount account, AccessTokenBinding authorization, DateTimeOffset expiry)
        {
            Account = account;
            Authorization = authorization;
            Expiry = expiry;
            Token = _cancellation.Token;
        }
        public Guid Epoch { get; } = Guid.NewGuid();
        public LockedDeviceRequestAccount Account { get; }
        public AccessTokenBinding? Authorization { get; private set; }
        public DateTimeOffset Expiry { get; }
        public CancellationToken Token { get; }
        public IAuthenticatedApiClient? Client { get; set; }
        public SemaphoreSlim RequestGate { get; } = new(1, 1);
        public Task? Loop { get; set; }
        public void Dispose()
        {
            Authorization = null;
            try { _cancellation.Cancel(); }
            catch { }
            try { Client?.Dispose(); }
            catch { }
            Client = null;
        }
    }
}
