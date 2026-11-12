#if NATIVE_UI_REVIEW
using System.Security.Cryptography;
using System.Text;
using App.Services;
using App.ViewModels;
using App.ViewModels.Models;
using App.Views;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using Crypto;
using Crypto.PasswordStrength;
using Microsoft.Extensions.DependencyInjection;
using Vault;
using AppSendType = App.ViewModels.Models.SendType;

namespace App.NativeUiReview;

/// <summary>Supplies synthetic, memory-only dependencies to the native XAML review process.</summary>
internal sealed class ReviewFixture : IDisposable
{
    private const string HealthyCipherId = "review-healthy";
    private static readonly VaultCapabilities Capabilities = new(true, true, true);
    private readonly IDisposable _preferences;
    private readonly ReviewLifecycle _lifecycle;
    private readonly ReviewSendUiService _sends;
    private readonly string _dataMode;
    private bool _disposed;

    public IServiceProvider Services { get; }
    public VaultSession Session { get; }
    public ReviewVerifier Verifier { get; }
    public ReviewClipboard Clipboard { get; }
    public SensitiveActionVerificationService SensitiveActions { get; }
    public SettingsViewModel Settings { get; }
    public VaultViewModel Vault { get; }
    public string HealthyLoginId => HealthyCipherId;
    public bool NoExternalCapabilityActions =>
        Services.GetRequiredService<ISshAgentHost>() is ReviewSshAgentHost { StartCalls: 0 }
        && Services.GetRequiredService<IWindowsPasskeyProvider>() is ReviewWindowsPasskeyProvider { OperationCalls: 0 }
        && Services.GetRequiredService<StartupRegistrationService>() is not null
        && Services.GetRequiredService<IStartupRegistrationBackend>() is ReviewStartupRegistration { ChangeCalls: 0 }
        && Services.GetRequiredService<IAccountManagementService>() is ReviewAccountManagementService { MutationCalls: 0 };
    public SendListItem ReviseSendForRefresh() => _sends.ReviseTextForReview();
    public void RestoreSendAfterRefresh() => _sends.RestoreReviewRevision();
    public IReadOnlyDictionary<VaultItemKind, string> DetailIds { get; } =
        Enum.GetValues<VaultItemKind>().Select((kind, index) => (kind, id: (index + 1).ToString()))
            .ToDictionary(pair => pair.kind, pair => pair.id);

    public bool SendEmpty
    {
        get => _sends.Empty;
        set => _sends.Empty = value;
    }

    public ReviewFixture(string languagePreference = "zh-CN", string dataMode = "full")
    {
        NativeReviewStartupTrace.Record("fixture-constructor-entered");
        _dataMode = dataMode;
        _preferences = AppPreferences.UseInMemoryForTesting(new AppPreferencesData
        {
            RememberEmail = false,
            AllowScreenshots = true,
            ShowWebsiteIcons = true,
            ShowTrayIcon = false,
            CloseToTray = false,
            MinimizeToTray = false,
            AllowBrowserIntegration = false,
            LanguagePreference = languagePreference,
            SshAgentEnabled = false,
            MonitorDeviceRequestsWhileLocked = false,
            SensitiveActionVerificationIndex = 0,
        });
        NativeReviewStartupTrace.Record("fixture-memory-preferences-installed");
        Session = new VaultSession();
        Clipboard = new ReviewClipboard();
        Verifier = new ReviewVerifier();
        _lifecycle = new ReviewLifecycle(Session);
        SensitiveActions = new SensitiveActionVerificationService(Verifier, Session);
        ReopenSession();

        var collection = new ServiceCollection();
        var reader = new ReviewVaultReader(Session);
        var sync = new ReviewSync(Session);
        var writer = new ReviewRejectingVaultWriter();
        var vaultUi = new VaultUiService(reader, writer, sync, Session);
        var account = new ReviewAccountService();
        var accountUi = new AccountUiService(reader, account);
        var twoFactor = new ReviewTwoFactorUiService();
        var requests = new ReviewAuthRequests();
        var transients = new TransientOperationRegistry(_ => { });
        var states = new SensitiveStateRegistry(_ => { });
        NativeReviewStartupTrace.Record("fixture-before-frequency-dictionaries");
        var strength = new PasswordStrengthEvaluator(new Omnimatch(new DictionaryMatcher(FrequencyDictionaries.Load())));
        NativeReviewStartupTrace.Record("fixture-frequency-dictionaries-ready");
        var prefill = new AccountPrefill();
        var tokens = new ReviewTokenStore();
        var auth = new ReviewAuthService(Session);
        var emergency = new MockEmergencyAccessUiService();
        var health = new ReviewHealthUiService();
        var savedViews = new AppPreferencesSavedSearchStore();
        var reprompt = new ItemRepromptGate(account, Session);
        _sends = new ReviewSendUiService();

        // The optional session argument remains null to disable persistent recent-use metadata.
        Vault = new VaultViewModel(vaultUi, Clipboard, savedStore: savedViews, reprompt: reprompt, session: null);
        Settings = new SettingsViewModel(accountUi, twoFactor, lifecycle: _lifecycle,
            browserIntegrationSupported: false, vault: reader, supportsLocalUnlock: () => false);

        collection.AddSingleton(Session);
        collection.AddSingleton<IVaultService>(reader);
        collection.AddSingleton<ISyncService>(sync);
        collection.AddSingleton<IVaultWriteService>(writer);
        collection.AddSingleton<IVaultUiService>(vaultUi);
        collection.AddSingleton<IAccountService>(account);
        collection.AddSingleton<IAccountUiService>(accountUi);
        collection.AddSingleton<ITwoFactorUiService>(twoFactor);
        collection.AddSingleton<IAuthRequestUiService>(requests);
        collection.AddSingleton<ITransientOperationRegistry>(transients);
        collection.AddSingleton<ISensitiveStateRegistry>(states);
        collection.AddSingleton<ISshAgentHost, ReviewSshAgentHost>();
        collection.AddSingleton<IWindowsPasskeyProvider, ReviewWindowsPasskeyProvider>();
        collection.AddSingleton<IStartupRegistrationBackend, ReviewStartupRegistration>();
        collection.AddSingleton<StartupRegistrationService>();
        collection.AddSingleton<IAccountManagementService, ReviewAccountManagementService>();
        collection.AddTransient(provider => new AccountManagementViewModel(
            provider.GetRequiredService<IAccountManagementService>(), Session, _lifecycle));
        collection.AddSingleton<IAppLifecycleCoordinator>(_lifecycle);
        collection.AddSingleton<IClipboardService>(Clipboard);
        collection.AddSingleton<IClipboardStatus>(Clipboard);
        collection.AddSingleton<ISensitiveActionVerifier>(Verifier);
        collection.AddSingleton<ISensitiveActionVerificationService>(SensitiveActions);
        collection.AddSingleton<IItemRepromptGate>(reprompt);
        collection.AddSingleton<IFaviconCache, ReviewFaviconCache>();
        collection.AddSingleton<ITokenStore>(tokens);
        collection.AddSingleton<IAuthService>(auth);
        collection.AddSingleton<IPwnedPasswordsClient, ReviewPwnedPasswordsClient>();
        collection.AddSingleton<IRegisterUiService>(new RegisterUiService());
        collection.AddSingleton<IEmergencyAccessUiService>(emergency);
        collection.AddSingleton<IVaultHealthUiService>(health);
        collection.AddSingleton<ISendUiService>(_sends);
        collection.AddSingleton<ISavedSearchStore>(savedViews);
        collection.AddSingleton(strength);
        collection.AddSingleton(prefill);
        collection.AddSingleton<VaultTimeoutService>();
        collection.AddSingleton<FrameNavigationService>();
        collection.AddSingleton<INavigationService>(provider => provider.GetRequiredService<FrameNavigationService>());
        collection.AddSingleton<UiDispatcher>();
        collection.AddSingleton<IUiDispatcher>(provider => provider.GetRequiredService<UiDispatcher>());
        collection.AddSingleton(Vault);
        collection.AddSingleton(Settings);
        collection.AddSingleton(new GeneratorViewModel(Clipboard, strength, Session, _lifecycle));
        collection.AddSingleton(new SendViewModel(_sends, Clipboard, Session, _lifecycle));
        collection.AddSingleton(new SecurityReportViewModel(health));
        collection.AddTransient(_ => new LoginViewModel(auth, null, prefill: prefill, tokenStore: tokens, lifecycle: _lifecycle));
        collection.AddTransient(_ => new RegisterViewModel(new RegisterUiService()));
        collection.AddSingleton<IDeviceUiService, MockDeviceUiService>();
        collection.AddTransient(provider => new DevicesViewModel(provider.GetRequiredService<IDeviceUiService>(), requests, Session, _lifecycle));
        collection.AddTransient(_ => new EmergencyAccessViewModel(emergency, Session));
        collection.AddSingleton<IVaultExportService, ReviewExport>();
        collection.AddSingleton<IVaultImportService>(new ReviewImport(Session));
        collection.AddTransient(provider => new ImportExportViewModel(provider.GetRequiredService<IVaultExportService>(),
            provider.GetRequiredService<IVaultImportService>(), account));
        collection.AddSingleton(new DeviceApprovalWindowService(requests, Session, transients, _lifecycle));
        Services = collection.BuildServiceProvider();
        NativeReviewStartupTrace.Record("fixture-services-built");
        states.Register(Vault);
        states.Register(SensitiveActions);
    }

    public void ReopenSession()
    {
        SensitiveActions.EndGracePeriod();
        var origin = new ServerOrigin("https://vault.review.example");
        var lease = Session.BeginAccountSession("native-review-account", "reviewer@review.example", origin);
        var key = new SymmetricCryptoKey(Enumerable.Range(1, 64).Select(value => (byte)value).ToArray());
        if (!Session.TryCommit(lease, () =>
        {
            Session.SetUnlockedKey(key);
            var snapshot = CreateSnapshot(origin);
            if (_dataMode == "empty")
                snapshot = new(snapshot.Account, Array.Empty<Folder>(), Array.Empty<Cipher>(), 0, 0);
            else if (_dataMode == "single-login")
                snapshot = new(snapshot.Account, Array.Empty<Folder>(), new Cipher[]
                {
                    new()
                    {
                        Id = "1", Type = CipherType.Login, Name = "Synthetic Minimal Login",
                        Login = new("reviewer@minimal.example", "Synthetic-only-42!", null, Array.Empty<CipherLoginUri>()),
                    },
                }, 0, 0);
            Session.SetSnapshot(snapshot, capabilities: Capabilities);
            Session.MarkDemo();
        }))
        {
            key.Dispose();
            throw new InvalidOperationException("The synthetic review session could not be opened.");
        }
        _lifecycle.MarkSessionReady();
    }

    private static DecryptedVault CreateSnapshot(ServerOrigin origin)
    {
        var created = new DateTimeOffset(2026, 9, 12, 8, 30, 0, TimeSpan.Zero);
        var edited = DateTimeOffset.UtcNow.AddDays(-2);
        var account = new AccountInfo("native-review-account", "reviewer@review.example", origin, "R", "PBKDF2 · 600,000 iterations")
        {
            Name = "Native Review",
        };
        Folder[] folders =
        [
            new() { Id = "review-work", Name = "Work", RevisionDate = edited },
            new() { Id = "review-personal", Name = "Personal", RevisionDate = edited },
            new() { Id = "review-finance", Name = "Finance", RevisionDate = edited },
        ];
        Cipher[] ciphers =
        [
            new()
            {
                Id = "1", Type = CipherType.Login, Name = "Aurora Workspace", FolderId = "review-work", Favorite = true,
                CreationDate = created, RevisionDate = edited,
                Notes = "Synthetic workspace account used only by the native visual review fixture.",
                Login = new CipherLogin("reviewer@aurora.example", "Synthetic-only-42!", "JBSWY3DPEHPK3PXP",
                    (CipherLoginUri[])[new("https://workspace.aurora.example/account?mode=review#details", null), new("https://login.aurora.example", 1)])
                {
                    PasswordRevisionDate = edited,
                    Fido2Credentials = (CipherFido2Credential[])[new(null, "public-key", "ECDSA", "P-256", null, "aurora.example", null,
                        "reviewer@aurora.example", 0, "Aurora", "Review User", true, created)],
                },
                Fields = (CipherField[])[new("Workspace", "Aurora Design", CipherFieldType.Text),
                    new("Recovery label", "Synthetic recovery value", CipherFieldType.Hidden),
                    new("Review enabled", "true", CipherFieldType.Boolean)],
                PasswordHistory = (PasswordHistoryEntry[])[new("Synthetic-old-2026-C!", edited.AddDays(-12)),
                    new("Synthetic-old-2026-B!", edited.AddDays(-38)), new("Synthetic-old-2026-A!", edited.AddDays(-72))],
            },
            new()
            {
                Id = "2", Type = CipherType.Card, Name = "Example Travel Card", FolderId = "review-finance", Favorite = true,
                CreationDate = created, RevisionDate = edited.AddHours(-2), Notes = "Synthetic payment information.",
                Card = new("Review User", "4111111111111111", "08", "2030", "123", "Visa"),
                Fields = (CipherField[])[new("Purpose", "Native review only", CipherFieldType.Text)],
            },
            new()
            {
                Id = "3", Type = CipherType.Identity, Name = "Review Identity", FolderId = "review-personal",
                CreationDate = created, RevisionDate = edited.AddHours(-3),
                Identity = new("Mx", "Review", null, "User", "review-user", "Example Studio", "000-00-0000", "REVIEW-P0001", "REVIEW-L0001",
                    "reviewer@identity.example", "+1 202 555 0100", "100 Example Avenue", "Suite 12", null, "Example City", "Example State", "00000", "Example Country"),
                Notes = "All identity values in this fixture are synthetic.",
            },
            new()
            {
                Id = "4", Type = CipherType.SecureNote, Name = "Project Recovery Notes", FolderId = "review-work",
                CreationDate = created, RevisionDate = edited.AddHours(-4), SecureNote = new(0),
                Notes = "Native review checklist\n\n1. Review the current production XAML.\n2. Compare light and dark surfaces.\n3. Keep synthetic data isolated from user accounts.\n\nThis note is a fixture and contains no recovery material.",
            },
            new()
            {
                Id = "5", Type = CipherType.SshKey, Name = "Review Build Host", FolderId = "review-work",
                CreationDate = created, RevisionDate = edited.AddHours(-5),
                Ssh = new("SYNTHETIC PRIVATE KEY TEXT\nThis is not a usable private key.",
                    "ssh-ed25519 SYNTHETIC_REVIEW_PUBLIC_KEY review@host.example", "SHA256:SyntheticReviewFingerprintOnly"),
                Notes = "Synthetic key labels for layout verification only.",
            },
            new()
            {
                Id = "6", Type = CipherType.BankAccount, Name = "Example Savings Account", FolderId = "review-finance",
                CreationDate = created, RevisionDate = edited.AddHours(-6),
                BankAccount = new("Example Bank", "Review User", "Savings", "000012340000", "000000000", "001", "0000", "REVIEWXX", "EX00 REVIEW 0000 0000", "+1 202 555 0101"),
                Notes = "Synthetic account identifiers; no banking service is contacted.",
            },
            new()
            {
                Id = "7", Type = CipherType.DriversLicense, Name = "Review Driver License", FolderId = "review-personal",
                CreationDate = created, RevisionDate = edited.AddHours(-7),
                DriversLicense = new("Review", null, "User", "1990-01-01", "REVIEW-DL-0001", "Example Country", "Example State", "2025-01-01", "2030-01-01", "Example Authority", "B"),
            },
            new()
            {
                Id = "8", Type = CipherType.Passport, Name = "Review Passport", FolderId = "review-personal",
                CreationDate = created, RevisionDate = edited.AddHours(-8),
                Passport = new("User", "Review", "1990-01-01", "X", "Example City", "Example Nationality", "Example Country", "REVIEW-P0001", "Ordinary", "REVIEW-ID-0001", "Example Authority", "2025-01-01", "2035-01-01"),
            },
            new()
            {
                Id = "review-reused", Type = CipherType.Login, Name = "Example Collaboration", FolderId = "review-work",
                CreationDate = created, RevisionDate = edited.AddDays(-3),
                Login = new("reviewer@collaboration.example", "Synthetic-only-42!", null, (CipherLoginUri[])[new("https://collaboration.example", null)]),
            },
            new()
            {
                Id = "review-http", Type = CipherType.Login, Name = "Example Legacy Portal", FolderId = "review-personal",
                CreationDate = created, RevisionDate = edited.AddDays(-4),
                Login = new("reviewer@legacy.example", "review123", null, (CipherLoginUri[])[new("http://legacy.example/login", null)]),
            },
            new()
            {
                Id = HealthyCipherId, Type = CipherType.Login, Name = "Example Secure Service", FolderId = "review-work",
                CreationDate = created, RevisionDate = edited.AddDays(-1),
                Login = new("reviewer@secure.example", "pZ8$T4u#qL6!F9s&vR2@bN7%hK5", null,
                    (CipherLoginUri[])[new("https://secure.review.example/sign-in", null)]),
            },
            new()
            {
                Id = "review-archived", Type = CipherType.SecureNote, Name = "Archived Review Note", Notes = "Synthetic archived content.",
                SecureNote = new(0), CreationDate = created, RevisionDate = edited, ArchivedDate = edited,
            },
            new()
            {
                Id = "review-deleted", Type = CipherType.Login, Name = "Deleted Review Account",
                Login = new("removed@review.example", "Synthetic-deleted-42!", null, []),
                CreationDate = created, RevisionDate = edited, DeletedDate = edited,
            },
        ];
        return new DecryptedVault(account, folders, ciphers, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SensitiveActions.ClearSensitive();
        Clipboard.ClearSecretsNow();
        Session.Logout();
        (Services as IDisposable)?.Dispose();
        _preferences.Dispose();
    }
}

internal enum ReviewVerifierMode { Allow, Deny, InlinePrompt }

/// <summary>Verifies only the fixture phrase, without any Windows authentication API.</summary>
internal sealed class ReviewVerifier : ISensitiveActionVerifier
{
    public const string SyntheticPhrase = "Synthetic review verification only!";
    public int Calls { get; private set; }
    public ReviewVerifierMode Mode { get; set; } = ReviewVerifierMode.Allow;
    public object? LastPromptTarget { get; private set; }
    public DialogPasswordPrompt? PendingPrompt { get; private set; }

    public async Task<bool> VerifyAsync(nint hwnd, string message,
        Func<CancellationToken, Task<string?>> prompt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        if (Mode == ReviewVerifierMode.Allow) return true;
        if (Mode == ReviewVerifierMode.Deny) return false;
        LastPromptTarget = prompt.Target;
        PendingPrompt = prompt.Target as DialogPasswordPrompt;
        try
        {
            var phrase = await prompt(ct);
            return !ct.IsCancellationRequested && string.Equals(phrase, SyntheticPhrase, StringComparison.Ordinal);
        }
        finally
        {
            PendingPrompt = null;
        }
    }

    public void Reset()
    {
        Calls = 0;
        Mode = ReviewVerifierMode.Allow;
        LastPromptTarget = null;
        PendingPrompt = null;
    }
}

/// <summary>Records clipboard ownership metadata without retaining or exposing copied content.</summary>
internal sealed class ReviewClipboard : IClipboardService, IClipboardStatus
{
    private byte[]? _lastWriteDigest;
    public long WriteVersion { get; private set; }
    public DateTimeOffset? ClearAt { get; private set; }
    public int TotalSeconds { get; private set; }
    public bool HasContent { get; private set; }
    public DateTimeOffset? LastClearedAt { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public event EventHandler? Changed;

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_lastWriteDigest is not null) CryptographicOperations.ZeroMemory(_lastWriteDigest);
        _lastWriteDigest = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        WriteVersion++;
        HasContent = true;
        TotalSeconds = 30;
        ClearAt = DateTimeOffset.UtcNow.AddSeconds(TotalSeconds);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void SetSecretText(string text, int? autoClearSeconds = null) => SetText(text);
    public void SetLabel(string label) { Label = label; Changed?.Invoke(this, EventArgs.Empty); }
    public void ClearSecretsNow()
    {
        if (_lastWriteDigest is not null) CryptographicOperations.ZeroMemory(_lastWriteDigest);
        _lastWriteDigest = null;
        HasContent = false;
        ClearAt = null;
        TotalSeconds = 0;
        LastClearedAt = DateTimeOffset.UtcNow;
        Label = string.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public Task ClearSecretsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ClearSecretsNow();
        return Task.CompletedTask;
    }

    public bool LastWriteMatches(string expected)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        try { return _lastWriteDigest is not null && CryptographicOperations.FixedTimeEquals(digest, _lastWriteDigest); }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }
}

internal sealed class ReviewLifecycle(VaultSession session) : IAppLifecycleCoordinator
{
    private readonly object _gate = new();
    private bool _ready;
    public bool IsTransitioning { get; private set; }
    public bool MarkSessionReady() { lock (_gate) { _ready = session.State == VaultState.Unlocked; return _ready; } }
    public bool TryActivateSession(Func<bool> activate) { lock (_gate) { _ready = true; return activate(); } }
    public bool TryRunSensitive(Func<bool> action)
    {
        lock (_gate) return _ready && !IsTransitioning && session.State == VaultState.Unlocked && action();
    }
    public Task RequestAsync(AppLifecycleIntent intent, VaultSessionLease? expectedSession = null)
    {
        lock (_gate)
        {
            if (expectedSession is not null && !session.IsCurrent(expectedSession)) return Task.CompletedTask;
            _ready = false;
            IsTransitioning = true;
            if (intent == AppLifecycleIntent.Lock) session.Lock(); else session.Logout();
            IsTransitioning = false;
        }
        return Task.CompletedTask;
    }
    public async Task<bool> RequestIfAsync(AppLifecycleIntent intent, Func<bool> predicate)
    {
        lock (_gate) { if (!predicate()) return false; _ready = false; }
        await RequestAsync(intent);
        return true;
    }
    public void FailClosedSynchronously() { lock (_gate) { _ready = false; session.Lock(); } }
}

internal sealed class ReviewVaultReader(VaultSession session) : IVaultService
{
    public IVaultSnapshot Snapshot => session;
    public IReadOnlyList<Cipher> GetCiphers() => session.Ciphers;
    public IReadOnlyList<Folder> GetFolders() => session.Folders;
    public IReadOnlyList<DeviceInfo> GetDevices() => session.Devices;
    public VaultSessionLease CaptureLease() => session.CaptureLease();
    public bool IsCurrent(VaultSessionLease lease) => session.IsCurrent(lease);
    public void ThrowIfExpired(VaultSessionLease lease) => session.ThrowIfExpired(lease);
    public IReadOnlyList<Cipher> GetCiphers(VaultSessionLease lease) { session.ThrowIfExpired(lease); return session.Ciphers; }
}

internal sealed class ReviewSync(VaultSession session) : ISyncService
{
    public Task<IReadOnlyList<Cipher>> SyncAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(session.Ciphers); }
    public Task<IReadOnlyList<Cipher>> SyncAsync(VaultSessionLease lease, CancellationToken ct = default)
    { session.ThrowIfExpired(lease); return SyncAsync(ct); }
}

/// <summary>Rejects any unexpected escape from the production demo writer path.</summary>
internal sealed class ReviewRejectingVaultWriter : IVaultWriteService
{
    private static Task Rejected() => Task.FromException(new InvalidOperationException("Native review does not permit authenticated I/O."));
    public Task SaveCipherAsync(Cipher cipher, CancellationToken ct = default) => Rejected();
    public Task DeleteCipherAsync(string cipherId, bool permanent, CancellationToken ct = default) => Rejected();
    public Task RestoreCipherAsync(string cipherId, CancellationToken ct = default) => Rejected();
    public Task SaveFolderAsync(string? folderId, string name, CancellationToken ct = default) => Rejected();
    public Task DeleteFolderAsync(string folderId, CancellationToken ct = default) => Rejected();
    public Task MoveCiphersAsync(IReadOnlyCollection<string> ids, string? folderId, CancellationToken ct = default) => Rejected();
    public Task DeleteCiphersAsync(IReadOnlyCollection<string> ids, bool permanent, CancellationToken ct = default) => Rejected();
    public Task RestoreCiphersAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => Rejected();
}

internal sealed class ReviewFaviconCache : IFaviconCache
{
    public Task<byte[]?> GetAsync(string domain, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
    public void Clear() { }
    public FaviconCacheClearStatus ClearForAccountDeletion() => FaviconCacheClearStatus.Absent;
}

internal sealed class ReviewTokenStore : ITokenStore
{
    public bool TryLoad(out PersistedSession session) { session = null!; return false; }
    public void Save(PersistedSession session) => throw new InvalidOperationException("Native review does not persist sessions.");
    public void Clear() { }
    public IReadOnlyList<PersistedAccountMetadata> GetSavedAccounts() => [];
}

internal sealed class ReviewAuthService(VaultSession session) : IAuthService
{
    private static Task<AuthResult> Rejected() => Task.FromResult<AuthResult>(new AuthResult.Failure("Native review authentication is synthetic.", AuthFailureKind.SafeMessage));
    public AuthenticationAttemptLease BeginAuthenticationAttempt() => session.BeginAuthenticationAttempt();
    public void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt) => session.AbandonAuthenticationAttempt(attempt);
    public Task<bool> IsLoginWithDeviceAvailableAsync(string serverUrl, string email, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult(true); }
    public Task<LoginWithDeviceQuarantineInfo> GetLoginWithDeviceQuarantineInfoAsync(string serverUrl, string email, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<LoginWithDeviceQuarantineInfo>(new LoginWithDeviceQuarantineInfo.Absent()); }
    public Task<AuthResult> LoginAsync(AuthenticationAttemptLease attempt, string serverUrl, string email, string masterPassword, CancellationToken ct = default) => Rejected();
    public Task<AuthResult> SubmitTwoFactorAsync(AuthenticationAttemptLease attempt, string code, int provider, CancellationToken ct = default, bool rememberDevice = true) => Rejected();
    public Task<bool> ResendTwoFactorEmailAsync(AuthenticationAttemptLease attempt, CancellationToken ct = default) => Task.FromResult(false);
    public Task<AuthResult> UnlockAsync(AuthenticationAttemptLease attempt, string masterPassword, CancellationToken ct = default) => Rejected();
    public Task<AuthResult> UnlockWithPinAsync(AuthenticationAttemptLease attempt, string pin, CancellationToken ct = default) => Rejected();
    public Task<AuthResult> UnlockWithHelloAsync(AuthenticationAttemptLease attempt, byte[] signature, CancellationToken ct = default) => Rejected();
    public Task LockAsync(CancellationToken ct = default) { session.Lock(); return Task.CompletedTask; }
    public Task LockAsync(VaultSessionLease expectedSession, CancellationToken ct = default) { if (session.IsCurrent(expectedSession)) session.Lock(); return Task.CompletedTask; }
    public Task LogoutAsync(CancellationToken ct = default) { session.Logout(); return Task.CompletedTask; }
    public Task<bool> LogoutAsync(VaultSessionLease expectedSession, CancellationToken ct = default)
    { var current = session.IsCurrent(expectedSession); if (current) session.Logout(); return Task.FromResult(current); }
}

internal sealed class ReviewAccountService : IAccountService
{
    public Task UpdateNameAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChangePasswordAsync(string currentPassword, string newPassword, string? hint, CancellationToken ct = default) => Task.CompletedTask;
    public Task ChangeKdfAsync(string currentPassword, int newIterations, CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> VerifyMasterPasswordAsync(string password, CancellationToken ct = default) => Task.FromResult(password == ReviewVerifier.SyntheticPhrase);
}

internal sealed class ReviewSshAgentHost : ISshAgentHost
{
    public bool IsRunning { get; private set; }
    public int StartCalls { get; private set; }
    public SshAgentStartResult Start()
    {
        StartCalls++;
        var result = IsRunning ? SshAgentStartResult.AlreadyRunning : SshAgentStartResult.Started;
        IsRunning = true;
        return result;
    }
    public Task StopAsync() { IsRunning = false; return Task.CompletedTask; }
}

internal sealed class ReviewWindowsPasskeyProvider : IWindowsPasskeyProvider
{
    private bool _registered;
    private bool _running;
    public int OperationCalls { get; private set; }
    public WindowsPasskeyProviderStatus GetStatus() =>
        new(true, _registered, _running, "Synthetic provider capability; no Windows registration or COM activation is used.");
    public Task StartAsync() { OperationCalls++; _running = true; return Task.CompletedTask; }
    public Task StopAsync() { OperationCalls++; _running = false; return Task.CompletedTask; }
    public void Register() { OperationCalls++; _registered = true; }
    public void Unregister() { OperationCalls++; _registered = false; _running = false; }
    public void SynchronizeMetadata() { OperationCalls++; }
    public Task OnSessionLockedAsync() { OperationCalls++; return Task.CompletedTask; }
}

internal sealed class ReviewStartupRegistration : IStartupRegistrationBackend
{
    private bool _enabled;
    public int ChangeCalls { get; private set; }
    public Task<StartupRegistrationResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new StartupRegistrationResult(
            _enabled ? StartupRegistrationState.Enabled : StartupRegistrationState.Disabled,
            "Synthetic startup setting; no registry or startup-task access is used."));
    }
    public Task<StartupRegistrationResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChangeCalls++;
        _enabled = enabled;
        return ReadAsync(cancellationToken);
    }
}

internal sealed class ReviewAccountManagementService : IAccountManagementService
{
    public int MutationCalls { get; private set; }
    public AccountManagementAvailability GetAvailability() => new(true, null);
    private Task Rejected()
    {
        MutationCalls++;
        return Task.FromException(new InvalidOperationException("Native review does not send account-management requests."));
    }
    private Task<AccountSecurityChangeReceipt> RejectedChange()
    {
        MutationCalls++;
        return Task.FromException<AccountSecurityChangeReceipt>(new InvalidOperationException("Native review does not change account security."));
    }
    public Task RequestEmailChangeAsync(string currentPassword, string newEmail, CancellationToken ct = default) => Rejected();
    public Task<AccountSecurityChangeReceipt> ChangeEmailAsync(string currentPassword, string newEmail, string token, CancellationToken ct = default) => RejectedChange();
    public Task RequestEmailVerificationAsync(CancellationToken ct = default) => Rejected();
    public Task VerifyEmailAsync(string token, CancellationToken ct = default) => Rejected();
    public Task<AccountSecurityChangeReceipt> RevokeAllSessionsAsync(string currentPassword, CancellationToken ct = default) => RejectedChange();
}

internal sealed class ReviewAuthRequests : IAuthRequestUiService
{
    public Task<IReadOnlyList<AuthRequestItem>> ListPendingAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuthRequestItem>>([]);
    public Task ApproveAsync(string id, string publicKey, CancellationToken ct = default) => Task.CompletedTask;
    public Task DenyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class ReviewPwnedPasswordsClient : IPwnedPasswordsClient
{
    public Task<int> GetBreachCountAsync(string password, CancellationToken ct = default) => Task.FromResult(0);
}

internal sealed class ReviewHealthUiService : IVaultHealthUiService
{
    private static readonly HealthItemRef Main = new("1", "Aurora Workspace", "reviewer@aurora.example");
    private static readonly HealthItemRef Reused = new("review-reused", "Example Collaboration", "reviewer@collaboration.example");
    private static readonly HealthItemRef Legacy = new("review-http", "Example Legacy Portal", "reviewer@legacy.example");
    public Task<HealthReport> AnalyzeOfflineAsync(IProgress<HealthAnalysisProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(new(100, 100, "Synthetic analysis complete"));
        return Task.FromResult(new HealthReport((ReusedGroup[])[new(2, (HealthItemRef[])[Main, Reused])],
            (WeakFinding[])[new(Legacy, 12)], (UnsecuredFinding[])[new(Legacy, "http://legacy.example/login")]));
    }
    public Task<IReadOnlyList<ExposedFinding>> CheckExposedAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExposedFinding>>((ExposedFinding[])[new(Main, 5)]);
}

internal sealed class ReviewExport : IVaultExportService
{
    public bool CanExport => false;
    public string Export(ExportFormat format) => throw new InvalidOperationException("Native review does not export vault content.");
}

internal sealed class ReviewImport(VaultSession session) : IVaultImportService
{
    public IReadOnlyList<Folder> GetDestinationFolders() =>
        session.State == VaultState.Unlocked ? session.Folders.ToArray() : Array.Empty<Folder>();
    public ImportPreview Parse(ImportFormat format, string content) => new(0, 0, "Native review does not parse imported files.");
    public Task<int> ImportAsync(ImportFormat format, string content, CancellationToken ct = default) =>
        Task.FromException<int>(new InvalidOperationException("Native review does not import files."));
}

internal sealed class ReviewTwoFactorUiService : ITwoFactorUiService
{
    private static Task<T> Rejected<T>() => Task.FromException<T>(new InvalidOperationException("Native review does not run two-factor ceremonies."));
    private static Task Rejected() => Task.FromException(new InvalidOperationException("Native review does not run two-factor ceremonies."));
    public Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TwoFactorProvider>>((TwoFactorProvider[])[new(0, true), new(1, false), new(7, false)]);
    public Task<(string secret, string otpauth)> BeginTotpSetupAsync(string pw, CancellationToken ct = default) => Rejected<(string, string)>();
    public Task<TotpActivationResult> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default) => Rejected<TotpActivationResult>();
    public Task<string> GetRecoveryCodeAsync(string pw, CancellationToken ct = default) => Rejected<string>();
    public Task<string?> BeginEmailSetupAsync(string pw, CancellationToken ct = default) => Rejected<string?>();
    public Task SendEmailAsync(string pw, string email, CancellationToken ct = default) => Rejected();
    public Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default) => Rejected();
    public Task<WebAuthnTwoFactorDetails> GetWebAuthnAsync(string pw, CancellationToken ct = default) => Rejected<WebAuthnTwoFactorDetails>();
    public Task<WebAuthnTwoFactorDetails> GetWebAuthnAsync(TwoFactorUserVerification verification, CancellationToken ct = default) => Rejected<WebAuthnTwoFactorDetails>();
    public Task RequestProtectedActionOtpAsync(CancellationToken ct = default) => Rejected();
    public Task<WebAuthnRegistrationChallenge> GetWebAuthnChallengeAsync(string pw, CancellationToken ct = default) => Rejected<WebAuthnRegistrationChallenge>();
    public Task<WebAuthnRegistrationChallenge> GetWebAuthnChallengeAsync(TwoFactorUserVerification verification, CancellationToken ct = default) => Rejected<WebAuthnRegistrationChallenge>();
    public Task<WebAuthnRegistrationOutcome> CompleteWebAuthnRegistrationAsync(string pw, int id, string name, WebAuthnRegistrationChallenge challenge, WebAuthnRegistrationDeviceResponse deviceResponse, CancellationToken ct = default) => Rejected<WebAuthnRegistrationOutcome>();
    public Task<WebAuthnRegistrationOutcome> CompleteWebAuthnRegistrationAsync(TwoFactorUserVerification verification, int id, string name, WebAuthnRegistrationChallenge challenge, WebAuthnRegistrationDeviceResponse deviceResponse, CancellationToken ct = default) => Rejected<WebAuthnRegistrationOutcome>();
    public Task<WebAuthnTwoFactorDetails> DeleteWebAuthnCredentialAsync(string pw, int id, CancellationToken ct = default) => Rejected<WebAuthnTwoFactorDetails>();
    public Task<WebAuthnTwoFactorDetails> DeleteWebAuthnCredentialAsync(TwoFactorUserVerification verification, int id, CancellationToken ct = default) => Rejected<WebAuthnTwoFactorDetails>();
    public Task DisableAsync(string pw, int type, CancellationToken ct = default) => Rejected();
}

internal sealed class ReviewSendUiService : ISendUiService
{
    private readonly List<SendListItem> _items;
    private SendListItem? _revisionBaseline;
    public bool Empty { get; set; }
    public ReviewSendUiService()
    {
        var now = DateTimeOffset.UtcNow;
        _items =
        [
            new("review-text", "Project Handoff", AppSendType.Text, "Future deletion", "https://send.review.example/#/send/review-text/synthetic")
            {
                TextContent = "Synthetic project handoff\n\nThe preview, edit, and sharing states use only fixture content.",
                DeletionDateTime = now.AddDays(7), ExpirationDate = now.AddDays(3), AccessCount = 2,
                AuthType = SendAuthType.None, RevisionDate = now.AddDays(-1), DisplayTime = now,
            },
            new("review-file", "Native Review Brief.pdf", AppSendType.File, "Future deletion", "https://send.review.example/#/send/review-file/synthetic")
            {
                FileName = "Native Review Brief.pdf", FileSize = 2457600, DeletionDateTime = now.AddDays(14),
                ExpirationDate = now.AddDays(7), AccessCount = 1, MaxAccessCount = 10, HasPassword = true,
                AuthType = SendAuthType.Password, RevisionDate = now.AddHours(-12), DisplayTime = now,
            },
        ];
    }
    public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<SendListItem>>(Empty ? [] : _items.ToArray()); }
    public SendListItem ReviseTextForReview()
    {
        var index = _items.FindIndex(item => item.Type == AppSendType.Text);
        _revisionBaseline = _items[index];
        var updated = _items[index] with
        {
            Name = "Project Handoff revised",
            AccessCount = _items[index].AccessCount + 7,
            RevisionDate = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        _items[index] = updated;
        return updated;
    }
    public void RestoreReviewRevision()
    {
        if (_revisionBaseline is not { } baseline) return;
        var index = _items.FindIndex(item => item.Id == baseline.Id);
        if (index >= 0) _items[index] = baseline;
        _revisionBaseline = null;
    }
    public Task<SendListItem> CreateSendAsync(SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default)
    {
        var item = FromDraft($"review-created-{_items.Count}", draft);
        _items.Add(item);
        return Task.FromResult(item);
    }
    public Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, SendFileSource? file, CancellationToken ct = default)
    {
        var item = FromDraft(id, draft);
        var index = _items.FindIndex(existing => existing.Id == id);
        if (index >= 0) _items[index] = item;
        return Task.FromResult(item);
    }
    public Task DeleteSendAsync(string id, VaultSessionLease originatingSession, CancellationToken ct = default)
    { _items.RemoveAll(item => item.Id == id); return Task.CompletedTask; }
    public string? CopyShareLink(SendListItem item) => item.Link;
    public Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default) =>
        Task.FromResult(new SendReceivedResult(true, false, AppSendType.Text, "Synthetic Received Send", "Synthetic received fixture content.", null, null, null));
    public Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default)
    {
        var item = _items.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null) return Task.FromResult<SendEditorDraft?>(null);
        return Task.FromResult<SendEditorDraft?>(new()
        {
            Type = item.Type, Name = item.Name, Text = item.TextContent ?? string.Empty, FileName = item.FileName ?? string.Empty,
            DeletionDate = item.DeletionDateTime, ExpirationDate = item.ExpirationDate, Disabled = item.Disabled,
            MaxAccessCount = item.MaxAccessCount, ExistingHasPassword = item.HasPassword, ExistingAuthType = item.AuthType,
            Notes = "Synthetic Send metadata for native review only.",
        });
    }
    private static SendListItem FromDraft(string id, SendEditorDraft draft) => new(id, draft.Name, draft.Type,
        "Future deletion", $"https://send.review.example/#/send/{id}/synthetic")
    {
        FileName = draft.IsFile ? draft.FileName : null, TextContent = draft.IsText ? draft.Text : null,
        DeletionDateTime = draft.ToDeletionDate(), ExpirationDate = draft.ExpirationDate,
        Disabled = draft.Disabled, MaxAccessCount = draft.MaxAccessCount,
        HasPassword = !string.IsNullOrEmpty(draft.Password) || (draft.ExistingHasPassword && !draft.RemovePassword),
        AuthType = SendAuthType.None, RevisionDate = DateTimeOffset.UtcNow,
    };
}
#endif
