using App.Services;
using Api;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Abstractions;
using Core.Enums;
using Core.Models;
using Core.Services;
using Core.Session;
using System.Collections.ObjectModel;

namespace App.ViewModels;

public enum LoginStage
{
    Account,
    Password,
    LoginWithDevice,
    DeviceVerification,
    TwoFactor,
    PasswordChangeRequired,
    Unlock,
    PinUnlock,
    KeyConnectorUnlock,
    TrustedDeviceSso,
    TrustedDeviceApproval,
    TdeV2JitProvisioning,
    AccountDeletionRecovery,
}

public enum LoginFocusTarget
{
    ServerUrl,
    Email,
    MasterPassword,
    DeviceVerificationCode,
    TwoFactorCode,
    NewMasterPassword,
    Pin,
    LoginWithDeviceCancel,
    AccountDeletionRecoveryAction,
    PrimaryAction,
    ApiKeyClientId,
    ApiKeySecret,
}

public enum LoginStatusKind
{
    Info,
    Success,
    Error,
}

public enum LoginWithDeviceTerminalKind
{
    None,
    Canceled,
    Denied,
    Expired,
    OutcomeUncertain,
    Failed,
}

public enum AccountDeletionRecoveryNoticeKind
{
    None,
    Prepared,
    ServerConfirmed,
    LocalCleanupComplete,
    UncertainLocalCleanupComplete,
    LedgerUnavailable,
}

public partial class LoginViewModel : ObservableObject, ISensitiveState
{
    [ObservableProperty]
    public partial bool IsApiKeyMode { get; set; }

    [ObservableProperty]
    public partial string ApiKeyClientId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiKeySecret { get; set; } = string.Empty;

    public bool CanUseApiKeySignIn => IsAccountStage && !IsBusy;

    partial void OnIsApiKeyModeChanged(bool value)
    {
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(FormSubtitle));
        OnPropertyChanged(nameof(CanRequestPasswordHint));
        OnPropertyChanged(nameof(ShowLoginWithDeviceOption));
        RequestPasswordHintCommand.NotifyCanExecuteChanged();
        if (value)
        {
            CancelLoginWithDeviceAvailabilityProbe();
            ResetLoginWithDevicePresentation(clearAvailability: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseApiKeySignIn))]
    private void OpenApiKeySignIn()
    {
        if (!CanUseApiKeySignIn)
            return;
        ContinueToPassword();
        if (!IsPasswordStage)
            return;
        IsApiKeyMode = true;
        RequestFocus(LoginFocusTarget.ApiKeyClientId);
    }

    private const string BitwardenUsUrl = "https://vault.bitwarden.com";
    private const string BitwardenEuUrl = "https://vault.bitwarden.eu";

    private const int EmailTwoFactorProvider = (int)TwoFactorProviderType.Email;
    private const int RecoveryCodeProvider = (int)TwoFactorProviderType.RecoveryCode;
    private static readonly TimeSpan LoginWithDevicePollInterval = TimeSpan.FromSeconds(3);

    private readonly IAuthService _auth;
    private readonly IDemoVaultSessionService? _demoVault;
    private readonly IPinService? _pin;
    private readonly IWindowsHelloAuthenticator? _hello;
    private readonly IHelloUnlockService? _helloUnlock;
    private readonly ISavedAccountService? _savedAccountService;
    private readonly ISsoAuthorizationService? _ssoAuthorization;
    private readonly IWebAuthnTwoFactorService? _webAuthnTwoFactor;
    private readonly IDuoTwoFactorService? _duoTwoFactor;
    private readonly ITokenStore? _tokenStore;
    private readonly ITrustedDeviceApprovalService? _trustedDeviceApproval;
    private readonly IAccountDeletionService? _accountDeletion;
    private readonly AccountDeletionRecoveryHost? _accountDeletionRecoveryHost;
    private readonly ILocalDataResetService? _localDataReset;
    private readonly IAppLifecycleCoordinator? _lifecycle;
    private readonly Action? _beforeAccountChange;
    private long _passwordHintGeneration;
    private CancellationTokenSource? _passwordHintCancellation;
    private bool _isPasswordHintRequestActive;
    private bool _statusContainsPasswordHint;
    private bool _usesKeyConnector;
    private bool _usesTrustedDeviceSso;
    private Action<VaultSessionLease>? _onSuccess;
    private Func<VaultSessionLease, bool>? _tryAcceptSuccess;
    private bool _requiresSuccessAcceptance;
    private AuthenticationAttemptLease? _authenticationAttempt;
    private CancellationTokenSource? _ssoCancellation;
    private CancellationTokenSource? _approvalCancellation;
    private CancellationTokenSource? _loginWithDeviceAvailabilityCancellation;
    private CancellationTokenSource? _loginWithDeviceQuarantineEligibilityCancellation;
    private CancellationTokenSource? _loginWithDeviceCancellation;
    private CancellationTokenSource? _loginWithDeviceTwoFactorExpirationCancellation;
    private AuthenticationAttemptLease? _loginWithDeviceAttempt;
    private string? _loginWithDeviceRequestId;
    private long _loginWithDeviceGeneration;
    private bool _isLoginWithDeviceTwoFactor;
    private LoginWithDeviceQuarantineInfo? _loginWithDeviceQuarantineInfo;
    private AccountDeletionTombstoneRecord? _accountDeletionPending;
    private bool _blockDemoVaultForAccountDeletionRecovery;
    private long _accountDeletionRecoveryGeneration;
    private bool _isAccountDeletionRecoveryOperationActive;
    private string _accountDeletionRecoveryServerUrl = string.Empty;
    private string _accountDeletionRecoveryEmail = string.Empty;
    private VaultSessionLease? _approvalLease;
    private TrustedDeviceApprovalMethod _approvalMethod = TrustedDeviceApprovalMethod.Device;
    private VaultSessionLease? _tdeV2JitLease;
    private string? _tdeV2JitOrganizationIdentifier;
    private VaultSessionLease? _requiredPasswordChangeLease;
    private RequiredPasswordChangeReason _requiredPasswordChangeReason;
    private MasterPasswordPolicyRequirements? _requiredPasswordPolicy;
    private bool _isSsoTwoFactor;
    private IReadOnlyList<int> _twoFactorProviders = Array.Empty<int>();
    private IReadOnlyDictionary<int, string> _twoFactorProviderData =
        new Dictionary<int, string>();

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = BitwardenUsUrl;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MasterPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Pin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TwoFactorCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceVerificationCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewMasterPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmNewMasterPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MasterPasswordHint { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTwoFactorProviderPicker))]
    public partial ObservableCollection<TwoFactorProviderOption> TwoFactorProviderOptions { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResendEmailCode))]
    [NotifyPropertyChangedFor(nameof(CanResendEmailCode))]
    [NotifyPropertyChangedFor(nameof(TwoFactorInputHeader))]
    [NotifyPropertyChangedFor(nameof(TwoFactorInputPlaceholder))]
    [NotifyPropertyChangedFor(nameof(IsWebAuthnMode))]
    [NotifyPropertyChangedFor(nameof(IsDuoMode))]
    [NotifyPropertyChangedFor(nameof(ShowTwoFactorCodeInput))]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText))]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    public partial int SelectedTwoFactorProviderIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTwoFactorProviderPicker))]
    [NotifyPropertyChangedFor(nameof(ShowResendEmailCode))]
    [NotifyPropertyChangedFor(nameof(CanResendEmailCode))]
    [NotifyPropertyChangedFor(nameof(ShowRememberDevice))]
    [NotifyPropertyChangedFor(nameof(ShowRecoveryCodeWarning))]
    [NotifyPropertyChangedFor(nameof(RecoveryCodeActionText))]
    [NotifyPropertyChangedFor(nameof(TwoFactorInputHeader))]
    [NotifyPropertyChangedFor(nameof(TwoFactorInputPlaceholder))]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText))]
    [NotifyPropertyChangedFor(nameof(FormTitle))]
    [NotifyPropertyChangedFor(nameof(IsWebAuthnMode))]
    [NotifyPropertyChangedFor(nameof(IsDuoMode))]
    [NotifyPropertyChangedFor(nameof(ShowTwoFactorCodeInput))]
    public partial bool IsRecoveryCodeMode { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LoginStatusKind StatusKind { get; set; } = LoginStatusKind.Info;

    [ObservableProperty]
    public partial LoginFocusTarget? InlineErrorTarget { get; set; }

    [ObservableProperty]
    public partial bool IsUnlockPrompt { get; set; }

    [ObservableProperty]
    public partial int SelectedServerOptionIndex { get; set; }

    [ObservableProperty]
    public partial bool RememberDevice { get; set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoginWithDeviceOption))]
    [NotifyPropertyChangedFor(nameof(CanStartLoginWithDevice))]
    public partial bool IsLoginWithDeviceAvailable { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoginWithDeviceFingerprint))]
    public partial string LoginWithDeviceFingerprint { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginWithDeviceExpiryText))]
    [NotifyPropertyChangedFor(nameof(HasLoginWithDeviceExpiry))]
    [NotifyPropertyChangedFor(nameof(ShowLoginWithDeviceTwoFactorExpiry))]
    public partial DateTimeOffset? LoginWithDeviceExpiresAtUtc { get; set; }

    [ObservableProperty]
    public partial LoginWithDeviceTerminalKind LoginWithDeviceTerminal { get; set; }

    [ObservableProperty]
    public partial AccountDeletionRecoveryNoticeKind AccountDeletionRecoveryNotice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasApprovalFingerprint))]
    public partial string ApprovalFingerprint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool TrustCurrentDeviceAfterApproval { get; set; } = true;

    [ObservableProperty]
    public partial bool CanChooseTrustCurrentDeviceAfterJit { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApprovalMethodPicker))]
    public partial ObservableCollection<TrustedDeviceApprovalMethodOption> ApprovalMethodOptions { get; set; } = new();

    [ObservableProperty]
    public partial int SelectedApprovalMethodIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCancelSso))]
    [NotifyCanExecuteChangedFor(nameof(CancelSsoCommand))]
    public partial bool IsSsoPending { get; set; }

    [ObservableProperty]
    public partial string SsoOrganizationIdentifier { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSsoBrowserOpened { get; set; }

    [ObservableProperty]
    public partial LoginStage Stage { get; set; } = LoginStage.Account;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedAccounts))]
    [NotifyPropertyChangedFor(nameof(ShowSavedAccounts))]
    public partial ObservableCollection<SavedAccountOption> SavedAccounts { get; set; } = new();

    // 页面据 FocusRequestId 单调递增触发一次编程式焦点(而非直接绑定 FocusTarget,避免同值不触发)。
    [ObservableProperty]
    public partial LoginFocusTarget FocusTarget { get; set; }

    [ObservableProperty]
    public partial long FocusRequestId { get; set; }

    public LoginViewModel(IAuthService auth)
        : this(auth, null)
    {
    }

    public LoginViewModel(
        IAuthService auth,
        IDemoVaultSessionService? demoVault,
        IPinService? pin = null,
        AccountPrefill? prefill = null,
        IWindowsHelloAuthenticator? hello = null,
        IHelloUnlockService? helloUnlock = null,
        ISavedAccountService? savedAccountService = null,
        ISsoAuthorizationService? ssoAuthorization = null,
        IWebAuthnTwoFactorService? webAuthnTwoFactor = null,
        IDuoTwoFactorService? duoTwoFactor = null,
        ITokenStore? tokenStore = null,
        ITrustedDeviceApprovalService? trustedDeviceApproval = null,
        IAccountDeletionService? accountDeletion = null,
        AccountDeletionRecoveryHost? accountDeletionRecoveryHost = null,
        ILocalDataResetService? localDataReset = null,
        IAppLifecycleCoordinator? lifecycle = null,
        Action? beforeAccountChange = null)
    {
        _auth = auth;
        _demoVault = demoVault;
        _pin = pin;
        _hello = hello;
        _helloUnlock = helloUnlock;
        _savedAccountService = savedAccountService;
        _ssoAuthorization = ssoAuthorization;
        _webAuthnTwoFactor = webAuthnTwoFactor;
        _duoTwoFactor = duoTwoFactor;
        _tokenStore = tokenStore;
        _trustedDeviceApproval = trustedDeviceApproval;
        _accountDeletion = accountDeletion;
        _accountDeletionRecoveryHost = accountDeletionRecoveryHost;
        _localDataReset = localDataReset;
        _lifecycle = lifecycle;
        _beforeAccountChange = beforeAccountChange;
        RefreshSavedAccounts();

        // 注册成功后经 AccountPrefill(DI 单例)把服务器/邮箱预填带到新建的 LoginViewModel(Transient,
        // 注册页设置的实例会被登录页新建的实例取代,直接在旧实例上 PrepareAccount 会丢失,R10)。取用即清。
        if (prefill?.Take() is { } p)
            PrepareAccount(p.ServerUrl, p.Email);
    }

    public bool IsAccountStage => Stage == LoginStage.Account;

    public bool IsPasswordStage => Stage == LoginStage.Password;

    public bool IsLoginWithDeviceStage => Stage == LoginStage.LoginWithDevice;

    public bool IsDeviceVerificationStage => Stage == LoginStage.DeviceVerification;

    public bool IsTwoFactorStage => Stage == LoginStage.TwoFactor;

    public bool IsPasswordChangeRequiredStage => Stage == LoginStage.PasswordChangeRequired;

    public bool IsUnlockStage => Stage == LoginStage.Unlock;

    public bool IsPinUnlockStage => Stage == LoginStage.PinUnlock;

    public bool IsKeyConnectorUnlockStage => Stage == LoginStage.KeyConnectorUnlock;

    public bool IsTrustedDeviceSsoStage => Stage == LoginStage.TrustedDeviceSso;

    public bool IsTrustedDeviceApprovalStage =>
        Stage == LoginStage.TrustedDeviceApproval;

    public bool IsTdeV2JitProvisioningStage =>
        Stage == LoginStage.TdeV2JitProvisioning;

    public bool IsAccountDeletionRecoveryStage =>
        Stage == LoginStage.AccountDeletionRecovery;

    public bool CanContinueFromAccountDeletionRecovery =>
        IsAccountDeletionRecoveryStage && !IsBusy;

    public bool ShowClearPendingAccountDeletion =>
        IsAccountDeletionRecoveryStage
        && AccountDeletionRecoveryNotice == AccountDeletionRecoveryNoticeKind.Prepared;

    public bool ShowRetryConfirmedAccountDeletionCleanup =>
        IsAccountDeletionRecoveryStage
        && AccountDeletionRecoveryNotice is
            AccountDeletionRecoveryNoticeKind.ServerConfirmed
            or AccountDeletionRecoveryNoticeKind.LocalCleanupComplete
            or AccountDeletionRecoveryNoticeKind.UncertainLocalCleanupComplete;

    public bool CanRunAccountDeletionRecoveryAction =>
        !IsBusy
        && (ShowClearPendingAccountDeletion
            || ShowRetryConfirmedAccountDeletionCleanup);

    public bool ShowFullLocalResetAction =>
        IsAccountDeletionRecoveryStage
        && AccountDeletionRecoveryNotice == AccountDeletionRecoveryNoticeKind.LedgerUnavailable;

    public bool CanRequestFullLocalReset =>
        ShowFullLocalResetAction
        && !IsBusy
        && _localDataReset is not null
        && _lifecycle is not null;

    public string AccountDeletionRecoveryTitle => AccountDeletionRecoveryNotice switch
    {
        AccountDeletionRecoveryNoticeKind.Prepared => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA2D3FB875236BF9A),
        AccountDeletionRecoveryNoticeKind.ServerConfirmed => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text78C6DCA880CC9E59),
        AccountDeletionRecoveryNoticeKind.LocalCleanupComplete => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5EE7615E5C801886),
        AccountDeletionRecoveryNoticeKind.UncertainLocalCleanupComplete =>
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1B1DB5F8B44E439B),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text255B1F670F319151),
    };

    public string AccountDeletionRecoveryMessage => AccountDeletionRecoveryNotice switch
    {
        AccountDeletionRecoveryNoticeKind.Prepared =>
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0979AE1B16406AF7),
        AccountDeletionRecoveryNoticeKind.ServerConfirmed =>
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text13F48B295BB8FEE9),
        AccountDeletionRecoveryNoticeKind.LocalCleanupComplete =>
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text62E50E9A31DC2036),
        AccountDeletionRecoveryNoticeKind.UncertainLocalCleanupComplete =>
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text27C5A978B8E6CA91),
        _ =>
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD03AF11A076D615F),
    };

    public string AccountDeletionRecoveryContinueText =>
        AccountDeletionRecoveryNotice == AccountDeletionRecoveryNoticeKind.LedgerUnavailable
            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0AEF68CF9FFDFEAC)
            : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4068C9F588A3E8FD);

    public bool ShowLoginWithDeviceOption =>
        IsPasswordStage
        && !IsApiKeyMode
        && IsLoginWithDeviceAvailable
        && !IsLoginWithDeviceQuarantineBlocked;

    public bool CanStartLoginWithDevice =>
        ShowLoginWithDeviceOption && !IsBusy;

    public bool IsLoginWithDeviceQuarantineBlocked =>
        _loginWithDeviceQuarantineInfo is not LoginWithDeviceQuarantineInfo.Absent;

    public bool ShowLoginWithDeviceQuarantineNotice =>
        IsPasswordStage
        && _loginWithDeviceQuarantineInfo is not null
        && IsLoginWithDeviceQuarantineBlocked;

    public bool ShowLoginWithDeviceQuarantineClearAction =>
        IsPasswordStage
        && _loginWithDeviceQuarantineInfo is
            LoginWithDeviceQuarantineInfo.Found { IsClearEligible: true };

    public bool CanClearLoginWithDeviceQuarantine =>
        ShowLoginWithDeviceQuarantineClearAction && !IsBusy;

    public string LoginWithDeviceQuarantineTitle =>
        _loginWithDeviceQuarantineInfo switch
        {
            LoginWithDeviceQuarantineInfo.Found => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC871DC318451D352),
            LoginWithDeviceQuarantineInfo.Corrupt => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD4A57749F54DF67E),
            _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6903D2ED4FDDE1A8),
        };

    public string LoginWithDeviceQuarantineMessage =>
        _loginWithDeviceQuarantineInfo switch
        {
            LoginWithDeviceQuarantineInfo.Found { IsClearEligible: true } =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC5B4CC4664FB858E),
            LoginWithDeviceQuarantineInfo.Found =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC6E7D89C25ED6358),
            LoginWithDeviceQuarantineInfo.Corrupt =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text17371ACD082600A4),
            _ =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF026D709DA885C4A),
        };

    public bool ShowCancelLoginWithDevice =>
        IsLoginWithDeviceStage && IsBusy;

    public bool ShowPrimaryAction =>
        !IsLoginWithDeviceStage && !IsAccountDeletionRecoveryStage;

    public bool HasLoginWithDeviceFingerprint =>
        !string.IsNullOrWhiteSpace(LoginWithDeviceFingerprint);

    public bool HasLoginWithDeviceExpiry => LoginWithDeviceExpiresAtUtc is not null;

    public bool ShowLoginWithDeviceTwoFactorExpiry =>
        IsTwoFactorStage && _isLoginWithDeviceTwoFactor && HasLoginWithDeviceExpiry;

    public string LoginWithDeviceExpiryText => LoginWithDeviceExpiresAtUtc is { } expiresAt
        ? expiresAt.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture)
        : string.Empty;

    public bool HasApprovalFingerprint =>
        !string.IsNullOrWhiteSpace(ApprovalFingerprint);

    public bool ShowCancelDeviceApproval =>
        IsTrustedDeviceApprovalStage && IsBusy;

    public bool ShowApprovalMethodPicker =>
        IsTrustedDeviceApprovalStage && ApprovalMethodOptions.Count > 1;

    public bool ShowUnlockPanel =>
        IsUnlockStage || IsKeyConnectorUnlockStage || IsTrustedDeviceSsoStage;

    public bool HasSavedAccounts => SavedAccounts.Count > 0;

    public bool ShowSavedAccounts => IsAccountStage && HasSavedAccounts;

    public bool IsWindowsHelloAvailable { get; private set; }

    // 解锁阶段(主密码或 PIN)+ 已启用 Hello + 设备可用 → 显示 Hello 解锁入口。
    public bool ShowHelloUnlock => (ShowUnlockPanel || IsPinUnlockStage)
        && (_helloUnlock?.IsHelloSet ?? false) && IsWindowsHelloAvailable;

    public bool IsPinUnlockAvailable => (ShowUnlockPanel || IsPinUnlockStage)
        && (_pin?.IsPinSet ?? false)
        && _pin?.RequiresMasterPasswordBeforePin != true;

    public bool CanUsePinUnlock => IsPinUnlockAvailable && !IsBusy;

    public string AlternativeUnlockText => _usesTrustedDeviceSso
        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text289D355190DA124D)
        : _usesKeyConnector
            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4D40732CDE51D8E9)
            : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5F65D5BC87316299);

    public bool ShowResendEmailCode => IsTwoFactorStage
        && !IsRecoveryCodeMode
        && ResolveSelectedTwoFactorProvider() == EmailTwoFactorProvider;

    public bool CanResendEmailCode => ShowResendEmailCode && !IsBusy;

    public IReadOnlyList<TwoFactorProviderOption> AvailableTwoFactorProviderOptions =>
        _twoFactorProviders.Where(IsSupportedTwoFactorProvider)
            .Select(provider => new TwoFactorProviderOption(provider, ProviderDisplayName(provider)))
            .Append(new TwoFactorProviderOption(RecoveryCodeProvider, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC5674F920EF6D556)))
            .DistinctBy(option => option.Type).ToArray();

    public bool ShowTwoFactorProviderPicker => !IsRecoveryCodeMode && TwoFactorProviderOptions.Count > 1;

    public bool ShowRememberDevice => IsTwoFactorStage && !IsRecoveryCodeMode;

    public bool ShowRecoveryCodeWarning => IsTwoFactorStage && IsRecoveryCodeMode;

    public bool ShowRecoveryCodeSwitch => IsTwoFactorStage
        && (IsRecoveryCodeMode
            ? _twoFactorProviders.Any(IsSupportedTwoFactorProvider)
            : _twoFactorProviders.Count > 0);

    public bool IsWebAuthnMode => IsTwoFactorStage
        && !IsRecoveryCodeMode
        && ResolveSelectedTwoFactorProvider() == (int)TwoFactorProviderType.WebAuthn;

    public bool IsDuoMode => IsTwoFactorStage
        && !IsRecoveryCodeMode
        && ResolveSelectedTwoFactorProvider() is
            (int)TwoFactorProviderType.Duo or
            (int)TwoFactorProviderType.OrganizationDuo;

    public bool ShowTwoFactorCodeInput => IsTwoFactorStage && !IsWebAuthnMode && !IsDuoMode;

    public string RecoveryCodeActionText => IsRecoveryCodeMode ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E9F2E2D76E259CD) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA24F4E7BB6DC1B64);

    public string TwoFactorInputHeader => ResolveSelectedTwoFactorProvider() switch
    {
        (int)TwoFactorProviderType.YubiKey => "YubiKey OTP",
        RecoveryCodeProvider => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC5674F920EF6D556),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBB015C60BAFC8A96),
    };

    public string TwoFactorInputPlaceholder => ResolveSelectedTwoFactorProvider() switch
    {
        (int)TwoFactorProviderType.YubiKey => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text61EEED6D42C5E140),
        RecoveryCodeProvider => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1547F87F5594259A),
        _ => "000000",
    };

    private static bool IsTypedCodeProvider(int provider) => provider is
        (int)TwoFactorProviderType.Authenticator or
        (int)TwoFactorProviderType.Email or
        (int)TwoFactorProviderType.YubiKey;

    private bool IsSupportedTwoFactorProvider(int provider) =>
        IsTypedCodeProvider(provider)
        || (provider == (int)TwoFactorProviderType.WebAuthn
            && _webAuthnTwoFactor?.IsSupported == true
            && _twoFactorProviderData.ContainsKey(provider))
        || ((provider is (int)TwoFactorProviderType.Duo
                or (int)TwoFactorProviderType.OrganizationDuo)
            && CanUseDuo(provider));

    private bool CanUseDuo(int provider)
    {
        if (_duoTwoFactor?.IsSupported != true
            || !_twoFactorProviderData.TryGetValue(provider, out var providerData))
            return false;
        try
        {
            return _duoTwoFactor.CanAuthenticate(
                providerData,
                ServerEndpoints.FromWebVault(ResolveServerUrl()));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool HasLegacyDuoChallenge()
    {
        var endpoints = ServerEndpoints.FromWebVault(ResolveServerUrl());
        foreach (var provider in _twoFactorProviders)
        {
            if ((provider != (int)TwoFactorProviderType.Duo
                    && provider != (int)TwoFactorProviderType.OrganizationDuo)
                || !_twoFactorProviderData.TryGetValue(provider, out var providerData))
            {
                continue;
            }

            try
            {
                _ = DuoTwoFactorProtocol.ParseChallenge(providerData, endpoints);
            }
            catch (DuoLegacyUnsupportedException)
            {
                return true;
            }
            catch (Exception ex) when (ex is DuoTwoFactorException or ArgumentException)
            {
            }
        }

        return false;
    }

    private static string ProviderDisplayName(int provider) => provider switch
    {
        0 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD6A4E838B9B78E09),
        1 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1B2714B812D42F77),
        2 => "Duo",
        3 => "YubiKey OTP",
        6 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4F42C08738E301D2),
        7 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD43412EE36BE899E),
        8 => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC5674F920EF6D556),
        _ => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text0C3D44AF5EB4A62D,provider),
    };

    private int ResolveSelectedTwoFactorProvider()
    {
        var options = TwoFactorProviderOptions;
        if (options.Count == 0)
            return _twoFactorProviders.FirstOrDefault();
        var index = Math.Clamp(SelectedTwoFactorProviderIndex, 0, options.Count - 1);
        return options[index].Type;
    }

    public bool CanEditServer => IsAccountStage && !IsBusy;

    public bool CanUseDemoVault => _demoVault is not null
        && IsAccountStage
        && !IsBusy
        && !_blockDemoVaultForAccountDeletionRecovery;

    public bool CanGoBack => !IsBusy
        && (IsPasswordStage || IsLoginWithDeviceStage || IsDeviceVerificationStage
            || IsTwoFactorStage || IsTrustedDeviceApprovalStage);

    public bool CanResendNewDeviceCode => IsDeviceVerificationStage && !IsBusy;

    public string RequiredPasswordPolicySummary
    {
        get
        {
            var policy = _requiredPasswordPolicy;
            var requirements = new List<string>
            {
                global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextFFE004E224D09E9B,Math.Max(MasterPasswordPolicyRequirements.MinimumLength, policy?.MinLength ?? 0)),
            };
            if (policy?.MinComplexity is > 0)
                requirements.Add(global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text958BDBCD3E1FCE23,policy.MinComplexity));
            if (policy?.RequireUpper == true)
                requirements.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCD806D2DC853D2DD));
            if (policy?.RequireLower == true)
                requirements.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB609131A895029C8));
            if (policy?.RequireNumbers == true)
                requirements.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD886BF750293503B));
            if (policy?.RequireSpecial == true)
                requirements.Add(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD5FC42B507823E35));
            return string.Join("；", requirements);
        }
    }

    public bool CanUsePrimaryAction => !IsBusy && ShowPrimaryAction;

    public bool CanUseSso => _ssoAuthorization is not null && IsAccountStage && !IsBusy;

    public bool CanRequestPasswordHint => IsPasswordStage && !IsApiKeyMode && !IsBusy;

    public string PasswordHintButtonText => _isPasswordHintRequestActive
        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3D969F2D71FD815C) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text90FA43B0A18F9E2A);

    public bool ShowCancelSso => IsSsoPending;

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public bool ShowCustomServerUrl => IsAccountStage && SelectedServerOptionIndex == 2;

    public string PrimaryButtonText => _isPasswordHintRequestActive ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E2DF9C3075AE9E4) : IsBusy
        ? Stage switch
        {
            LoginStage.Password => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9E27D3FB2BFB4146),
            LoginStage.LoginWithDevice => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6D1F6406824C2BA6),
            LoginStage.DeviceVerification => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEC59AEF352FDEDC4),
            LoginStage.PasswordChangeRequired => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text977A1413167F2AA0),
            LoginStage.TwoFactor => IsRecoveryCodeMode
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7266E439CC2E1277)
                : IsWebAuthnMode
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0D59C25CE7A97C4D)
                    : IsDuoMode ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCE632BA9DCA3BAD9) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text80652BE50407F6A0),
            LoginStage.Unlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC15D8A980EEC8CBC),
            LoginStage.PinUnlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC15D8A980EEC8CBC),
            LoginStage.KeyConnectorUnlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7006498F498CEB30),
            LoginStage.TrustedDeviceSso => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA0B7742C128B3952),
            LoginStage.TrustedDeviceApproval => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6D1F6406824C2BA6),
            LoginStage.TdeV2JitProvisioning => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC63F8C7405116F6F),
            LoginStage.AccountDeletionRecovery => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE2D6654015A34F7A),
            _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text20CFD1C3B4C849A9),
        }
        : Stage switch
        {
            LoginStage.Account => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7C9691192F1B7340),
            LoginStage.LoginWithDevice => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6AE9704445B25B5A),
            LoginStage.DeviceVerification => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E58055546842167),
            LoginStage.PasswordChangeRequired => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text96DFC135A1EAC3B2),
            LoginStage.TwoFactor => IsRecoveryCodeMode
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2C7E6131265CD356)
                : IsWebAuthnMode
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextACBF7FBFB2509C24)
                    : IsDuoMode ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text66B6A3EFFBC0306F) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text154723A1AA0A09A4),
            LoginStage.Unlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text70FBA17FFFB72F1A),
            LoginStage.PinUnlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text70FBA17FFFB72F1A),
            LoginStage.KeyConnectorUnlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4D1D50DE94008BD0),
            LoginStage.TrustedDeviceSso => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text68F234FE70D8CADC),
            LoginStage.TrustedDeviceApproval => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1D199F9370640DD0),
            LoginStage.TdeV2JitProvisioning => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9FF367BEDB88298E),
            LoginStage.AccountDeletionRecovery => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB847DB2F1A2C997C),
            _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E2DF9C3075AE9E4),
        };

    public string FormTitle => Stage switch
    {
        LoginStage.Password => IsApiKeyMode ? "Personal API key sign-in" : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text91BFD6063710FADF),
        LoginStage.LoginWithDevice => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text24A7591B498DFBCB),
        LoginStage.DeviceVerification => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF7CFC51BDC273DB0),
        LoginStage.PasswordChangeRequired => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD23804775C52FD31),
        LoginStage.TwoFactor => IsRecoveryCodeMode
            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEDD6C223B64B7B5E)
            : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text62AA5B7A409DFF6F,ProviderDisplayName(ResolveSelectedTwoFactorProvider())),
        LoginStage.Unlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text799316DCCBA64119),
        LoginStage.PinUnlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text799316DCCBA64119),
        LoginStage.KeyConnectorUnlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4D1D50DE94008BD0),
        LoginStage.TrustedDeviceSso => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA958E42950912643),
        LoginStage.TrustedDeviceApproval => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF9D8B15D6898FF6D),
        LoginStage.TdeV2JitProvisioning => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB834D6431FA92B5E),
        LoginStage.AccountDeletionRecovery => AccountDeletionRecoveryTitle,
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E2DF9C3075AE9E4),
    };

    public string FormSubtitle => Stage switch
    {
        LoginStage.Password => IsApiKeyMode
            ? "The API key authenticates this account. The master password decrypts it locally."
            : $"{AccountSummary} · {ServerHost}",
        LoginStage.LoginWithDevice => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text614D25837F55AE60),
        LoginStage.DeviceVerification => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text009047809A2743BC),
        LoginStage.PasswordChangeRequired => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text57AEF4A59BF8D1AA),
        LoginStage.TwoFactor => IsRecoveryCodeMode
            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0CEAECFDDD8A72E0)
            : IsWebAuthnMode
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2C7D4F4AB6B753F1)
                : IsDuoMode
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2B3D73FCD62AE167)
                    : ResolveSelectedTwoFactorProvider() == EmailTwoFactorProvider
                        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBEC24832DDD5E149)
                        : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0FFEABE91CFB28AF),
        LoginStage.Unlock or LoginStage.PinUnlock => $"{AccountDisplayName} · {ServerHost}",
        LoginStage.KeyConnectorUnlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2F077FF5DE0D2D2A),
        LoginStage.TrustedDeviceSso => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text800CDDD9EA7DEE07),
        LoginStage.TrustedDeviceApproval => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text298A4409C0A2AA7E),
        LoginStage.TdeV2JitProvisioning => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF89727E8EA3FD415),
        LoginStage.AccountDeletionRecovery => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB2E276756F8AF043),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1F9E4918CF6DD169),
    };

    public string StepText => Stage switch
    {
        LoginStage.Password => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF36F5F11EDEFB912),
        LoginStage.LoginWithDevice => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB7ED76DF2220A3DA),
        LoginStage.DeviceVerification => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB16CF917405DD423),
        LoginStage.PasswordChangeRequired => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE2B840D8022EB61E),
        LoginStage.TwoFactor => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5E406936FA039548),
        LoginStage.Unlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text56CEE9095850D2FA),
        LoginStage.PinUnlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text56CEE9095850D2FA),
        LoginStage.KeyConnectorUnlock => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text56CEE9095850D2FA),
        LoginStage.TrustedDeviceSso => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7BFE6A805EDEBC5C),
        LoginStage.TrustedDeviceApproval => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA613C234017E7C68),
        LoginStage.TdeV2JitProvisioning => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB65CD461B47FF2EB),
        LoginStage.AccountDeletionRecovery => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1A41926D6ADD97B2),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text34E5636C63CC8F78),
    };

    public bool ShowAccountContext => !IsAccountStage && !string.IsNullOrWhiteSpace(Email);

    public string AccountSummary => string.IsNullOrWhiteSpace(Email) ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2E412E03837F7DF0) : Email.Trim();

    public string ServerSummary => FormatServerSummary(ResolveServerUrl());

    private string ServerHost => Uri.TryCreate(ResolveServerUrl(), UriKind.Absolute, out var uri)
        ? uri.Host
        : ServerSummary;

    private string AccountDisplayName => AccountSummary.Split('@')[0];

    public string AccountInitial
    {
        get
        {
            var trimmed = Email.Trim();
            return string.IsNullOrWhiteSpace(trimmed) ? "V" : trimmed[..1].ToUpperInvariant();
        }
    }

    public void SetSuccessCallback(Action<VaultSessionLease> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _requiresSuccessAcceptance = false;
        _tryAcceptSuccess = null;
        _onSuccess = callback;
    }

    public void SetSuccessHandler(Func<VaultSessionLease, bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _requiresSuccessAcceptance = true;
        _onSuccess = null;
        _tryAcceptSuccess = handler;
    }

    public void Deactivate()
    {
        ApiKeySecret = string.Empty;
        ApiKeyClientId = string.Empty;
        InvalidatePasswordHintRequest();
        _onSuccess = null;
        _tryAcceptSuccess = null;
        LoginCommand.Cancel();
        LoginWithDeviceCommand.Cancel();
        ClearLoginWithDeviceQuarantineCommand.Cancel();
        RetryConfirmedAccountDeletionCleanupCommand.Cancel();
        RequestFullLocalResetCommand.Cancel();
        InvalidateAccountDeletionRecoveryOperation();
        LoginWithSsoCommand.Cancel();
        ResendEmailCodeCommand.Cancel();
        CancelLoginWithDeviceTwoFactorExpiration();

        var wasTwoFactor = IsTwoFactorStage;
        AbandonCurrentAuthenticationAttempt();
        if (wasTwoFactor)
            ReturnFromTwoFactorStage(refreshLoginWithDeviceAvailability: false);
        else
            ClearTwoFactorChallengeState();

        if (IsLoginWithDeviceStage)
            Stage = LoginStage.Password;
        ResetLoginWithDevicePresentation(clearAvailability: true);
    }

    public void PreparePersistedSession(PersistedSession persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        PrepareInitialLoginState(persisted);
    }

    public void PrepareInitialLoginState(PersistedSession? activeSession)
    {
        if (_accountDeletion is null)
        {
            if (activeSession is not null)
                PrepareUnlock(activeSession.ServerUrl, activeSession.Email);
            return;
        }

        AccountDeletionTombstoneReadAllResult read;
        try
        {
            read = _accountDeletion.ReadPending();
        }
        catch
        {
            PrepareUnavailableAccountDeletionRecovery(activeSession);
            return;
        }

        if (read.Status == AccountDeletionTombstoneReadStatus.Absent)
        {
            ResolveStaleStartupCleanupFailures([]);
            if (activeSession is not null)
                PrepareUnlock(activeSession.ServerUrl, activeSession.Email);
            return;
        }
        if (read.Status is AccountDeletionTombstoneReadStatus.Corrupt
            or AccountDeletionTombstoneReadStatus.Unavailable)
        {
            PrepareUnavailableAccountDeletionRecovery(activeSession);
            return;
        }

        var activeMatches = (activeSession?.Endpoints is { } activeEndpoints
            ? read.Records.Where(record =>
                string.Equals(
                    record.Binding.AccountId,
                    activeSession!.AccountId,
                    StringComparison.Ordinal)
                && record.Binding.Endpoints == activeEndpoints)
            : Enumerable.Empty<AccountDeletionTombstoneRecord>())
            .ToArray();
        var startupFailures = CurrentStartupCleanupFailures(read.Records);
        var candidates = activeMatches.Length > 0
            ? activeMatches
            : startupFailures.Count > 0
                ? startupFailures
            : read.Records.Where(record =>
                record.Stage is not (
                    AccountDeletionStage.LocalCleanupComplete
                    or AccountDeletionStage.UncertainLocalCleanupComplete));
        var pending = candidates
            .OrderByDescending(record => AccountDeletionRecoveryPriority(record.Stage))
            .ThenByDescending(record => record.UpdatedAt)
            .FirstOrDefault();
        if (pending is null)
        {
            if (activeSession is not null)
            {
                PrepareUnlock(activeSession.ServerUrl, activeSession.Email);
            }
            else if (IsAccountDeletionRecoveryStage)
            {
                PrepareAccount(
                    string.IsNullOrWhiteSpace(_accountDeletionRecoveryServerUrl)
                        ? BitwardenUsUrl
                        : _accountDeletionRecoveryServerUrl,
                    string.Empty);
            }
            return;
        }
        var useActiveDisplay = activeSession?.Endpoints is { } endpoints
            && string.Equals(
                pending.Binding.AccountId,
                activeSession!.AccountId,
                StringComparison.Ordinal)
            && pending.Binding.Endpoints == endpoints;
        PrepareAccountDeletionRecovery(
            useActiveDisplay
                ? activeSession!.ServerUrl
                : pending.Binding.Endpoints.WebVault.Value,
            useActiveDisplay
                ? activeSession!.Email
                : pending.Binding.NormalizedEmail,
            pending,
            AccountDeletionRecoveryNoticeForStage(pending.Stage));
    }

    private IReadOnlyList<AccountDeletionTombstoneRecord> CurrentStartupCleanupFailures(
        IReadOnlyList<AccountDeletionTombstoneRecord> currentRecords)
    {
        if (_accountDeletionRecoveryHost is null)
            return [];

        ResolveStaleStartupCleanupFailures(currentRecords);
        return _accountDeletionRecoveryHost.FailedCleanupRecords
            .Where(currentRecords.Contains)
            .ToArray();
    }

    private void ResolveStaleStartupCleanupFailures(
        IReadOnlyList<AccountDeletionTombstoneRecord> currentRecords)
    {
        if (_accountDeletionRecoveryHost is null)
            return;

        foreach (var failure in _accountDeletionRecoveryHost.FailedCleanupRecords)
        {
            if (!currentRecords.Contains(failure))
                _accountDeletionRecoveryHost.MarkResolved(failure);
        }
    }

    private static int AccountDeletionRecoveryPriority(AccountDeletionStage stage) =>
        stage switch
        {
            AccountDeletionStage.ServerConfirmed => 3,
            AccountDeletionStage.Prepared => 2,
            AccountDeletionStage.LocalCleanupComplete => 1,
            AccountDeletionStage.UncertainLocalCleanupComplete => 1,
            _ => 0,
        };

    private static AccountDeletionRecoveryNoticeKind AccountDeletionRecoveryNoticeForStage(
        AccountDeletionStage stage) => stage switch
        {
            AccountDeletionStage.Prepared => AccountDeletionRecoveryNoticeKind.Prepared,
            AccountDeletionStage.ServerConfirmed =>
                AccountDeletionRecoveryNoticeKind.ServerConfirmed,
            AccountDeletionStage.LocalCleanupComplete =>
                AccountDeletionRecoveryNoticeKind.LocalCleanupComplete,
            AccountDeletionStage.UncertainLocalCleanupComplete =>
                AccountDeletionRecoveryNoticeKind.UncertainLocalCleanupComplete,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

    private void PrepareUnavailableAccountDeletionRecovery(
        PersistedSession? activeSession) => PrepareAccountDeletionRecovery(
        activeSession?.ServerUrl ?? BitwardenUsUrl,
        activeSession?.Email ?? string.Empty,
        pending: null,
        AccountDeletionRecoveryNoticeKind.LedgerUnavailable);

    private void PrepareAccountDeletionRecovery(
        string serverUrl,
        string email,
        AccountDeletionTombstoneRecord? pending,
        AccountDeletionRecoveryNoticeKind notice)
    {
        PrepareAccount(serverUrl, email);
        _accountDeletionPending = pending;
        _accountDeletionRecoveryServerUrl = serverUrl;
        _accountDeletionRecoveryEmail = email;
        AccountDeletionRecoveryNotice = notice;
        Stage = LoginStage.AccountDeletionRecovery;
        SetStatus(string.Empty, LoginStatusKind.Info);
        RequestFocus(LoginFocusTarget.AccountDeletionRecoveryAction);
    }

    [RelayCommand(CanExecute = nameof(CanContinueFromAccountDeletionRecovery))]
    private void ContinueFromAccountDeletionRecovery()
    {
        var serverUrl = _accountDeletionRecoveryServerUrl;
        if (AccountDeletionRecoveryNotice
            == AccountDeletionRecoveryNoticeKind.LedgerUnavailable)
        {
            PersistedSession? activeSession = null;
            try
            {
                if (_tokenStore?.TryLoad(out var persisted) == true)
                    activeSession = persisted;
            }
            catch
            {
                activeSession = null;
            }

            PrepareAccount(serverUrl, string.Empty);
            PrepareInitialLoginState(activeSession);
            var stillUnavailable = IsAccountDeletionRecoveryStage
                && AccountDeletionRecoveryNotice
                    == AccountDeletionRecoveryNoticeKind.LedgerUnavailable;
            SetStatus(
                stillUnavailable
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7F007CA140D2309F)
                    : IsAccountDeletionRecoveryStage
                        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE3274866761A7968)
                        : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB959C60A7BB5C068),
                stillUnavailable
                    ? LoginStatusKind.Error
                    : LoginStatusKind.Success);
            return;
        }

        PrepareAccount(serverUrl, string.Empty);
        _blockDemoVaultForAccountDeletionRecovery = true;
        OnPropertyChanged(nameof(CanUseDemoVault));
        UseDemoVaultCommand.NotifyCanExecuteChanged();
        SetStatus(
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5A4B328A30191D38),
            LoginStatusKind.Info);
    }

    [RelayCommand(CanExecute = nameof(CanRequestFullLocalReset))]
    private async Task RequestFullLocalResetAsync(CancellationToken ct)
    {
        if (!CanRequestFullLocalReset)
            return;

        var resetAttempted = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            IsBusy = true;
            resetAttempted = true;
            LocalDataResetRequestResult result;
            try
            {
                result = _localDataReset!.RequestReset();
            }
            catch
            {
                SetStatus(
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9E15FE0C8A335558),
                    LoginStatusKind.Error);
                await ExitAfterLocalDataResetRequestAsync(alwaysFailClosed: true);
                return;
            }

            if (result.Status is LocalDataResetRequestStatus.Requested
                or LocalDataResetRequestStatus.AlreadyPending)
            {
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text77BFEA8B9B125A80), LoginStatusKind.Success);
            }
            else
            {
                SetStatus(
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9E15FE0C8A335558),
                    LoginStatusKind.Info);
            }
            await ExitAfterLocalDataResetRequestAsync(alwaysFailClosed: false);
        }
        catch (OperationCanceledException) when (!resetAttempted && ct.IsCancellationRequested)
        {
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text13CDD4CC6F88C58F), LoginStatusKind.Info);
        }
        finally
        {
            if (!resetAttempted)
                IsBusy = false;
        }
    }

    private async Task ExitAfterLocalDataResetRequestAsync(bool alwaysFailClosed)
    {
        var failClosed = alwaysFailClosed;
        try
        {
            await _lifecycle!.RequestAsync(AppLifecycleIntent.Exit);
        }
        catch
        {
            failClosed = true;
            SetStatus(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7A81B377F4455ECA),
                LoginStatusKind.Error);
        }

        if (!failClosed)
            return;
        try
        {
            _lifecycle?.FailClosedSynchronously();
        }
        catch
        {
        }
    }

    public async Task ClearPendingAccountDeletionLocalDataAsync(
        CancellationToken ct = default)
    {
        if (!CanRunAccountDeletionRecoveryAction
            || AccountDeletionRecoveryNotice != AccountDeletionRecoveryNoticeKind.Prepared
            || _accountDeletionPending is not { } expected
            || _accountDeletion is null)
        {
            return;
        }

        var generation = Volatile.Read(ref _accountDeletionRecoveryGeneration);
        try
        {
            IsBusy = true;
            _isAccountDeletionRecoveryOperationActive = true;
            var result = await _accountDeletion.ClearLocalDataForPendingAsync(
                expected,
                ct);
            if (!IsCurrentAccountDeletionRecoveryOperation(generation, expected))
                return;
            if (result.Status == AccountDeletionLocalResolutionStatus.Completed)
            {
                FinishAccountDeletionRecoveryAction(
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1EBEA131AEC7D43F),
                    LoginStatusKind.Success);
                return;
            }
            if (result.Status == AccountDeletionLocalResolutionStatus.NotFound)
            {
                FinishAccountDeletionRecoveryAction(
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDB08724D0D51A6DE),
                    LoginStatusKind.Info,
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text527FBC187D0D9776));
                return;
            }

            RetainAccountDeletionRecovery(result.Pending);
            SetStatus(
                result.Status == AccountDeletionLocalResolutionStatus.CancelledBeforeCleanup
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB261C35B068A9CF1)
                    : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text18615DA78C720317),
                result.Status == AccountDeletionLocalResolutionStatus.CancelledBeforeCleanup
                    ? LoginStatusKind.Info
                    : LoginStatusKind.Error);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            if (IsCurrentAccountDeletionRecoveryOperation(generation, expected))
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB261C35B068A9CF1), LoginStatusKind.Info);
        }
        catch
        {
            if (IsCurrentAccountDeletionRecoveryOperation(generation, expected))
            {
                SetStatus(
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text18615DA78C720317),
                    LoginStatusKind.Error);
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _accountDeletionRecoveryGeneration))
            {
                _isAccountDeletionRecoveryOperationActive = false;
                IsBusy = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunAccountDeletionRecoveryAction))]
    private async Task RetryConfirmedAccountDeletionCleanupAsync(
        CancellationToken ct)
    {
        if (AccountDeletionRecoveryNotice is not (
                AccountDeletionRecoveryNoticeKind.ServerConfirmed
                or AccountDeletionRecoveryNoticeKind.LocalCleanupComplete
                or AccountDeletionRecoveryNoticeKind.UncertainLocalCleanupComplete)
            || _accountDeletionPending is not { } expected
            || _accountDeletion is null)
        {
            return;
        }

        var generation = Volatile.Read(ref _accountDeletionRecoveryGeneration);
        try
        {
            IsBusy = true;
            _isAccountDeletionRecoveryOperationActive = true;
            var result = await _accountDeletion.ResumeConfirmedCleanupAsync(
                expected,
                ct);
            if (!IsCurrentAccountDeletionRecoveryOperation(generation, expected))
                return;
            if (IsSuccessfulAccountDeletionCleanup(expected, result))
            {
                _accountDeletionRecoveryHost?.MarkResolved(expected);
                FinishAccountDeletionRecoveryAction(
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1EBEA131AEC7D43F),
                    LoginStatusKind.Success);
                return;
            }

            var pending = result switch
            {
                AccountDeletionResult.OutcomeUncertainLocalCleanupComplete complete =>
                    complete.Pending,
                AccountDeletionResult.ConfirmedLocalCleanupIncomplete incomplete =>
                    incomplete.Pending,
                AccountDeletionResult.OutcomeUncertainLocalCleanupIncomplete incomplete =>
                    incomplete.Pending,
                AccountDeletionResult.OutcomeUncertain uncertain => uncertain.Pending,
                AccountDeletionResult.Rejected rejected => rejected.Pending,
                _ => null,
            };
            RetainAccountDeletionRecovery(pending);
            SetStatus(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text847DCF2337A38D85),
                LoginStatusKind.Error);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            if (IsCurrentAccountDeletionRecoveryOperation(generation, expected))
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB261C35B068A9CF1), LoginStatusKind.Info);
        }
        catch
        {
            if (IsCurrentAccountDeletionRecoveryOperation(generation, expected))
            {
                SetStatus(
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text847DCF2337A38D85),
                    LoginStatusKind.Error);
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _accountDeletionRecoveryGeneration))
            {
                _isAccountDeletionRecoveryOperationActive = false;
                IsBusy = false;
            }
        }
    }

    private static bool IsSuccessfulAccountDeletionCleanup(
        AccountDeletionTombstoneRecord expected,
        AccountDeletionResult result) => expected.Stage switch
        {
            AccountDeletionStage.ServerConfirmed
                or AccountDeletionStage.LocalCleanupComplete =>
                result is AccountDeletionResult.Confirmed,
            AccountDeletionStage.UncertainLocalCleanupComplete =>
                result is AccountDeletionResult.OutcomeUncertainLocalCleanupComplete complete
                && complete.Pending == expected,
            _ => false,
        };

    private bool IsCurrentAccountDeletionRecoveryOperation(
        long generation,
        AccountDeletionTombstoneRecord expected) =>
        generation == Volatile.Read(ref _accountDeletionRecoveryGeneration)
        && ReferenceEquals(_accountDeletionPending, expected)
        && IsAccountDeletionRecoveryStage;

    private void RetainAccountDeletionRecovery(
        AccountDeletionTombstoneRecord? pending)
    {
        var baseline = _accountDeletionPending;
        if (pending is null
            || pending != baseline
            || !string.Equals(
                pending.Binding.AccountId,
                baseline?.Binding.AccountId,
                StringComparison.Ordinal)
            || pending.Binding.Endpoints != baseline?.Binding.Endpoints)
        {
            _accountDeletionPending = null;
            AccountDeletionRecoveryNotice =
                AccountDeletionRecoveryNoticeKind.LedgerUnavailable;
            return;
        }

        _accountDeletionPending = pending;
        AccountDeletionRecoveryNotice = AccountDeletionRecoveryNoticeForStage(
            pending.Stage);
    }

    private void FinishAccountDeletionRecoveryAction(
        string message,
        LoginStatusKind statusKind,
        string? remainingRecoveryMessage = null)
    {
        remainingRecoveryMessage ??= global::App.Localization.UiText.Get(
            global::App.Localization.UiStringKey.TextD974D584F8F8B2D1);
        var serverUrl = _accountDeletionRecoveryServerUrl;
        var email = AccountDeletionRecoveryNotice is
                AccountDeletionRecoveryNoticeKind.ServerConfirmed
                or AccountDeletionRecoveryNoticeKind.LocalCleanupComplete
                or AccountDeletionRecoveryNoticeKind.UncertainLocalCleanupComplete
            ? string.Empty
            : _accountDeletionRecoveryEmail;
        _isAccountDeletionRecoveryOperationActive = false;
        IsBusy = false;
        PrepareAccount(serverUrl, email);
        RefreshSavedAccounts();
        PrepareInitialLoginState(activeSession: null);
        SetStatus(
            IsAccountDeletionRecoveryStage
                ? remainingRecoveryMessage
                : message,
            statusKind);
    }

    private void ClearAccountDeletionRecovery()
    {
        InvalidateAccountDeletionRecoveryOperation();
        _accountDeletionPending = null;
        var wasDemoBlocked = _blockDemoVaultForAccountDeletionRecovery;
        _blockDemoVaultForAccountDeletionRecovery = false;
        _accountDeletionRecoveryServerUrl = string.Empty;
        _accountDeletionRecoveryEmail = string.Empty;
        AccountDeletionRecoveryNotice = AccountDeletionRecoveryNoticeKind.None;
        if (wasDemoBlocked)
        {
            OnPropertyChanged(nameof(CanUseDemoVault));
            UseDemoVaultCommand.NotifyCanExecuteChanged();
        }
    }

    private void InvalidateAccountDeletionRecoveryOperation()
    {
        Interlocked.Increment(ref _accountDeletionRecoveryGeneration);
        if (_isAccountDeletionRecoveryOperationActive)
        {
            _isAccountDeletionRecoveryOperationActive = false;
            IsBusy = false;
        }
    }

    public void PrepareUnlock(string serverUrl, string email)
    {
        InvalidatePasswordHintRequest();
        ClearAccountDeletionRecovery();
        ClearRequiredPasswordChange();
        ClearTrustedDeviceApproval();
        ClearTdeV2JitProvisioning();
        AbandonCurrentAuthenticationAttempt();
        ResetLoginWithDevicePresentation(clearAvailability: true);
        ServerUrl = serverUrl;
        Email = email;
        MasterPassword = string.Empty;
        DeviceVerificationCode = string.Empty;
        TwoFactorCode = string.Empty;
        TwoFactorProviderOptions = new();
        _twoFactorProviders = [];
        _twoFactorProviderData = new Dictionary<int, string>();
        _isSsoTwoFactor = false;
        Pin = string.Empty;
        var unlockMethod = ResolveCurrentSavedUnlockMethod(serverUrl, email);
        _usesKeyConnector = unlockMethod == PersistedSessionUnlockMethod.KeyConnector;
        _usesTrustedDeviceSso = unlockMethod == PersistedSessionUnlockMethod.TrustedDeviceSso;
        OnPropertyChanged(nameof(AlternativeUnlockText));
        var requiresMasterPassword = _pin?.RequiresMasterPasswordBeforePin == true;
        Stage = (_pin?.IsPinSet ?? false) && !requiresMasterPassword
            ? LoginStage.PinUnlock
            : ResolveNonPinUnlockStage();
        SetStatus(
            requiresMasterPassword
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA742F292FBBB039A)
                : IsPinUnlockStage
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE4C5CE7FC065A98B)
                : IsKeyConnectorUnlockStage
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6C26BE6DAC29951E)
                    : IsTrustedDeviceSsoStage
                        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text52729D84A5FC5563)
                    : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text92735DEC8C488201),
            LoginStatusKind.Info,
            isUnlockPrompt: !requiresMasterPassword && (Stage is LoginStage.Unlock or LoginStage.PinUnlock));
    }

    public void PrepareAccount(string serverUrl, string email)
    {
        InvalidatePasswordHintRequest();
        ClearAccountDeletionRecovery();
        ClearRequiredPasswordChange();
        ClearTrustedDeviceApproval();
        ClearTdeV2JitProvisioning();
        AbandonCurrentAuthenticationAttempt();
        ResetLoginWithDevicePresentation(clearAvailability: true);
        SelectedServerOptionIndex = serverUrl == BitwardenUsUrl ? 0
            : serverUrl == BitwardenEuUrl ? 1
            : 2;
        Email = email;
        MasterPassword = string.Empty;
        DeviceVerificationCode = string.Empty;
        Pin = string.Empty;
        TwoFactorCode = string.Empty;
        TwoFactorProviderOptions = new();
        _twoFactorProviders = [];
        _twoFactorProviderData = new Dictionary<int, string>();
        IsRecoveryCodeMode = false;
        _isSsoTwoFactor = false;
        _usesKeyConnector = false;
        _usesTrustedDeviceSso = false;
        OnPropertyChanged(nameof(AlternativeUnlockText));
        ClearStatus();
        Stage = LoginStage.Account;
        // Assign the supplied endpoint after the option change clears any prior official endpoint.
        ServerUrl = serverUrl;
    }

    [RelayCommand]
    private async Task LoginAsync(CancellationToken ct)
    {
        if (IsBusy)
            return;

        if (IsAccountStage)
        {
            ContinueToPassword();
            return;
        }

        if (IsLoginWithDeviceStage || IsAccountDeletionRecoveryStage)
            return;

        if (IsTrustedDeviceSsoStage)
        {
            await LoginWithSsoAsync(ct);
            return;
        }

        if (IsTrustedDeviceApprovalStage)
        {
            await CompleteTrustedDeviceApprovalAsync(ct);
            return;
        }

        if (IsTdeV2JitProvisioningStage)
        {
            await CompleteTdeV2JitProvisioningAsync(ct);
            return;
        }

        if (IsPasswordChangeRequiredStage)
        {
            await CompleteRequiredPasswordChangeAsync(ct);
            return;
        }

        if (!ValidateSecretInput())
            return;

        if (IsPasswordStage)
        {
            _isSsoTwoFactor = false;
            _isLoginWithDeviceTwoFactor = false;
        }

        var attempt = IsTwoFactorStage || IsDeviceVerificationStage
            ? _authenticationAttempt
            : BeginAuthenticationAttempt();
        if (attempt is null)
        {
            if (IsTwoFactorStage)
                ReturnFromTwoFactorStage();
            else if (IsDeviceVerificationStage)
                ReturnFromDeviceVerificationStage();
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE02E26E76AA0E916), LoginStatusKind.Error);
            RequestFocus(CurrentSecretFocusTarget());
            return;
        }

        try
        {
            IsBusy = true;
            string? twoFactorToken = null;
            var twoFactorProvider = ResolveSelectedTwoFactorProvider();
            if (IsTwoFactorStage)
            {
                if (IsWebAuthnMode)
                {
                    if (_webAuthnTwoFactor is null
                        || !_twoFactorProviderData.TryGetValue(twoFactorProvider, out var providerData))
                    {
                        throw new WebAuthnTwoFactorException(
                            "The WebAuthn challenge is unavailable.");
                    }

                    SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC5883D11EA9AE0EE), LoginStatusKind.Info);
                    twoFactorToken = await _webAuthnTwoFactor.AuthenticateAsync(
                        providerData,
                        ServerEndpoints.FromWebVault(ResolveServerUrl()),
                        ct);
                }
                else if (IsDuoMode)
                {
                    if (_duoTwoFactor is null
                        || !_twoFactorProviderData.TryGetValue(twoFactorProvider, out var providerData))
                    {
                        throw new DuoTwoFactorException("The Duo challenge is unavailable.");
                    }

                    SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E2AFE1D2D279D33), LoginStatusKind.Info);
                    twoFactorToken = await _duoTwoFactor.AuthenticateAsync(
                        providerData,
                        ServerEndpoints.FromWebVault(ResolveServerUrl()),
                        ct);
                }
                else
                {
                    twoFactorToken = TwoFactorCode.Trim();
                }
            }

            var result = IsPinUnlockStage
                ? await _auth.UnlockWithPinAsync(attempt, Pin, ct)
                : IsKeyConnectorUnlockStage
                    ? await _auth.UnlockWithKeyConnectorAsync(attempt, ct)
                : IsUnlockStage
                    ? await _auth.UnlockAsync(attempt, MasterPassword, ct)
                    : IsDeviceVerificationStage
                        ? await _auth.SubmitNewDeviceVerificationAsync(
                            attempt,
                            DeviceVerificationCode,
                            ct)
                    : IsTwoFactorStage
                        ? await _auth.SubmitTwoFactorAsync(
                            attempt,
                            twoFactorToken!,
                            twoFactorProvider,
                            ct,
                            rememberDevice: !IsRecoveryCodeMode && RememberDevice)
                        : IsApiKeyMode
                            ? await _auth.LoginWithApiKeyAsync(
                                attempt,
                                ResolveServerUrl(),
                                Email.Trim(),
                                ApiKeyClientId.Trim(),
                                ApiKeySecret,
                                MasterPassword,
                                ct)
                            : await _auth.LoginAsync(attempt, ResolveServerUrl(), Email.Trim(), MasterPassword, ct);

            await HandleResultAsync(attempt, result);
        }
        catch (WebAuthnTwoFactorCancelledException)
        {
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2DC4E9694B9E9F62), LoginStatusKind.Info);
            RequestFocus(LoginFocusTarget.PrimaryAction);
        }
        catch (WebAuthnTwoFactorException)
        {
            HandleFailure(attempt, new AuthResult.Failure(
                "WebAuthn authentication failed.",
                AuthFailureKind.Unspecified));
        }
        catch (DuoTwoFactorCancelledException)
        {
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3A4C44BE28ED9F46), LoginStatusKind.Info);
            RequestFocus(LoginFocusTarget.PrimaryAction);
        }
        catch (DuoTwoFactorException)
        {
            HandleFailure(attempt, new AuthResult.Failure(
                "Duo authentication failed.",
                AuthFailureKind.Unspecified));
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            AbandonAuthenticationAttempt(attempt);
            if (IsTwoFactorStage)
                ReturnFromTwoFactorStage();
            else if (IsDeviceVerificationStage)
                ReturnFromDeviceVerificationStage();
            ClearStatus();
        }
        catch (HttpRequestException)
        {
            HandleFailure(attempt, new AuthResult.Failure(
                "The authentication server could not be reached.",
                AuthFailureKind.Network));
        }
        catch (TaskCanceledException)
        {
            HandleFailure(attempt, new AuthResult.Failure(
                "The authentication request timed out.",
                AuthFailureKind.Timeout));
        }
        finally
        {
            ApiKeySecret = string.Empty;
            IsBusy = false;
        }
    }

    public bool PrepareSsoSignIn()
    {
        if (IsBusy || _ssoAuthorization is null)
            return false;
        ClearStatus();
        var serverUrl = ResolveServerUrl();
        if (!IsValidServerUrl(serverUrl))
        {
            SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA9C8539849E5660C), LoginFocusTarget.ServerUrl);
            return false;
        }
        if (string.IsNullOrWhiteSpace(Email))
        {
            SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCC1DD3420167F716), LoginFocusTarget.Email);
            return false;
        }

        ServerUrl = serverUrl;
        Email = Email.Trim();
        return true;
    }

    [RelayCommand]
    private async Task LoginWithSsoAsync(CancellationToken ct)
    {
        if (IsBusy || _ssoAuthorization is null)
            return;

        if (!PrepareSsoSignIn())
            return;
        var serverUrl = ServerUrl;
        var attempt = BeginAuthenticationAttempt();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ssoCancellation = cancellation;
        try
        {
            IsBusy = true;
            IsSsoPending = true;
            IsSsoBrowserOpened = false;
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4D1DC9D952D5FC73), LoginStatusKind.Info);

            var endpoints = ServerEndpoints.FromWebVault(serverUrl);
            var browserOpened = new Progress<bool>(opened =>
            {
                if (ReferenceEquals(_ssoCancellation, cancellation) && !cancellation.IsCancellationRequested)
                    IsSsoBrowserOpened = opened;
            });
            var authorization = await _ssoAuthorization.AuthorizeAsync(
                endpoints,
                Email,
                SsoOrganizationIdentifier,
                browserOpened,
                cancellation.Token);
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE4BE808A116C4241), LoginStatusKind.Info);
            var result = await _auth.LoginWithSsoAsync(
                attempt,
                serverUrl,
                Email,
                authorization,
                cancellation.Token);
            _isSsoTwoFactor = result is AuthResult.TwoFactorRequired;
            _isLoginWithDeviceTwoFactor = false;
            await HandleResultAsync(attempt, result);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, cancellation.Token))
        {
            AbandonAuthenticationAttempt(attempt);
            ClearStatus();
        }
        catch (TimeoutException)
        {
            HandleFailure(attempt, new AuthResult.Failure(
                "The SSO browser flow timed out.",
                AuthFailureKind.Timeout));
        }
        catch (SsoAuthorizationException)
        {
            HandleFailure(attempt, new AuthResult.Failure(
                "The SSO browser flow could not be started.",
                AuthFailureKind.SsoAuthorization));
        }
        finally
        {
            if (ReferenceEquals(_ssoCancellation, cancellation))
                _ssoCancellation = null;
            cancellation.Dispose();
            IsSsoPending = false;
            IsSsoBrowserOpened = false;
            IsBusy = false;
        }
    }

    public async Task ReopenSsoBrowserAsync()
    {
        if (!IsSsoPending || !IsSsoBrowserOpened || _ssoAuthorization is null || _ssoCancellation is not { } cancellation)
            return;
        try
        {
            if (!await _ssoAuthorization.ReopenPendingBrowserAsync(cancellation.Token)
                && ReferenceEquals(_ssoCancellation, cancellation) && !cancellation.IsCancellationRequested)
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD4E2CF6A2C4B6445), LoginStatusKind.Error);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is SsoAuthorizationException or InvalidOperationException)
        {
            if (ReferenceEquals(_ssoCancellation, cancellation))
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD4E2CF6A2C4B6445), LoginStatusKind.Error);
        }
    }

    private bool CanCancelSso() => IsSsoPending;

    [RelayCommand(CanExecute = nameof(CanCancelSso))]
    private void CancelSso() => _ssoCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanStartLoginWithDevice))]
    private async Task LoginWithDeviceAsync(CancellationToken ct)
    {
        if (!CanStartLoginWithDevice)
            return;

        var serverUrl = ServerUrl.Trim();
        var email = Email.Trim();
        CancelLoginWithDeviceAvailabilityProbe();
        SetLoginWithDeviceQuarantineInfo(null);
        IsLoginWithDeviceAvailable = false;
        var attempt = BeginAuthenticationAttempt();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var generation = Interlocked.Increment(ref _loginWithDeviceGeneration);
        _loginWithDeviceCancellation = cancellation;
        _loginWithDeviceAttempt = attempt;
        _loginWithDeviceRequestId = null;
        LoginWithDeviceFingerprint = string.Empty;
        LoginWithDeviceExpiresAtUtc = null;
        LoginWithDeviceTerminal = LoginWithDeviceTerminalKind.None;
        IsBusy = true;
        Stage = LoginStage.LoginWithDevice;
        SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFE7BDBAC615E654E), LoginStatusKind.Info);

        try
        {
            var begin = await AwaitLoginWithDeviceOperationAsync(
                _auth.BeginLoginWithDeviceAsync(
                    attempt,
                    serverUrl,
                    email,
                    cancellation.Token),
                cancellation.Token);
            if (!IsCurrentLoginWithDeviceOperation(generation, cancellation, attempt))
            {
                await ReleaseDiscardedAuthResultAsync(begin);
                return;
            }

            if (begin is AuthResult.Failure beginFailure)
            {
                CompleteLoginWithDeviceBeginFailure(beginFailure);
                return;
            }

            if (begin is not AuthResult.LoginWithDeviceWaiting waiting
                || !TryApplyLoginWithDeviceWaiting(waiting))
            {
                await ReleaseDiscardedAuthResultAsync(begin);
                CompleteLoginWithDeviceFailure(
                    new AuthResult.Failure(
                        "The login-with-device response was invalid.",
                        AuthFailureKind.InvalidResponse));
                return;
            }

            SetStatus(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6E4C5EF6DDE4522C),
                LoginStatusKind.Info);

            while (IsCurrentLoginWithDeviceOperation(generation, cancellation, attempt))
            {
                if (LoginWithDeviceExpiresAtUtc is not { } expiresAt
                    || expiresAt <= DateTimeOffset.UtcNow)
                {
                    CompleteLoginWithDeviceTerminal(
                        LoginWithDeviceTerminalKind.Expired,
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFCE4930A78A1E385),
                        LoginStatusKind.Error,
                        cancelService: true);
                    return;
                }

                AuthResult result;
                try
                {
                    result = await AwaitLoginWithDeviceOperationAsync(
                        _auth.PollLoginWithDeviceAsync(attempt, cancellation.Token),
                        cancellation.Token);
                }
                catch (OperationCanceledException ex) when (
                    UiExceptionPolicy.IsGenuineCancellation(
                        ex,
                        cancellation.Token))
                {
                    throw;
                }
                catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
                {
                    result = new AuthResult.Failure(
                        "The login-with-device status request timed out.",
                        AuthFailureKind.Timeout);
                }
                catch (HttpRequestException)
                {
                    result = new AuthResult.Failure(
                        "The login-with-device status server could not be reached.",
                        AuthFailureKind.Network);
                }
                if (!IsCurrentLoginWithDeviceOperation(generation, cancellation, attempt))
                {
                    await ReleaseDiscardedAuthResultAsync(result);
                    return;
                }

                if (result is AuthResult.LoginWithDevicePending pending)
                {
                    if (!MatchesCurrentLoginWithDeviceRequest(pending))
                    {
                        CompleteLoginWithDeviceFailure(
                            new AuthResult.Failure(
                                "The login-with-device poll response was invalid.",
                                AuthFailureKind.InvalidResponse));
                        return;
                    }

                    if (pending.Status == LoginWithDeviceUiStatus.Denied)
                    {
                        CompleteLoginWithDeviceTerminal(
                            LoginWithDeviceTerminalKind.Denied,
                            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text694F7EC4E5358984),
                            LoginStatusKind.Error,
                            cancelService: false);
                        return;
                    }

                    if (pending.Status == LoginWithDeviceUiStatus.Expired)
                    {
                        CompleteLoginWithDeviceTerminal(
                            LoginWithDeviceTerminalKind.Expired,
                            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFCE4930A78A1E385),
                            LoginStatusKind.Error,
                            cancelService: false);
                        return;
                    }

                    if (pending.Status != LoginWithDeviceUiStatus.Pending)
                    {
                        CompleteLoginWithDeviceFailure(
                            new AuthResult.Failure(
                                "The login-with-device poll status was invalid.",
                                AuthFailureKind.InvalidResponse));
                        return;
                    }

                    SetStatus(
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6E4C5EF6DDE4522C),
                        LoginStatusKind.Info);
                    await Task.Delay(LoginWithDevicePollInterval, cancellation.Token);
                    continue;
                }

                if (result is AuthResult.Failure
                    {
                        Kind: AuthFailureKind.Network or AuthFailureKind.Timeout,
                    })
                {
                    SetStatus(
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2F79A92B3835297D),
                        LoginStatusKind.Info);
                    await Task.Delay(LoginWithDevicePollInterval, cancellation.Token);
                    continue;
                }

                if (result is AuthResult.LoginWithDeviceOutcomeUncertain
                    or AuthResult.Failure
                    {
                        Kind: AuthFailureKind.OutcomeUncertain,
                    })
                {
                    CompleteLoginWithDeviceOutcomeUncertain();
                    return;
                }

                if (result is AuthResult.Success or AuthResult.TwoFactorRequired)
                {
                    _isLoginWithDeviceTwoFactor = result is AuthResult.TwoFactorRequired;
                    _loginWithDeviceAttempt = null;
                    _loginWithDeviceRequestId = null;
                    LoginWithDeviceFingerprint = string.Empty;
                    if (!_isLoginWithDeviceTwoFactor)
                        LoginWithDeviceExpiresAtUtc = null;
                    LoginWithDeviceTerminal = LoginWithDeviceTerminalKind.None;
                    await HandleResultAsync(attempt, result);
                    return;
                }

                CompleteLoginWithDeviceFailure(
                    result as AuthResult.Failure
                    ?? new AuthResult.Failure(
                        "The login-with-device result was invalid.",
                        AuthFailureKind.InvalidResponse));
                return;
            }
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, cancellation.Token))
        {
            if (IsCurrentLoginWithDeviceOperation(
                    generation,
                    cancellation,
                    attempt,
                    allowCancellation: true))
            {
                CompleteLoginWithDeviceTerminal(
                    LoginWithDeviceTerminalKind.Canceled,
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8992739C06ABCE52),
                    LoginStatusKind.Info,
                    cancelService: true);
            }
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            if (IsCurrentLoginWithDeviceOperation(generation, cancellation, attempt))
            {
                CompleteLoginWithDeviceFailure(new AuthResult.Failure(
                    "The login-with-device request timed out.",
                    AuthFailureKind.Timeout));
            }
        }
        catch (HttpRequestException)
        {
            if (IsCurrentLoginWithDeviceOperation(generation, cancellation, attempt))
            {
                CompleteLoginWithDeviceFailure(new AuthResult.Failure(
                    "The login-with-device server could not be reached.",
                    AuthFailureKind.Network));
            }
        }
        catch (Exception)
        {
            if (IsCurrentLoginWithDeviceOperation(generation, cancellation, attempt))
            {
                CompleteLoginWithDeviceFailure(new AuthResult.Failure(
                    "The login-with-device request failed.",
                    AuthFailureKind.InvalidResponse));
            }
        }
        finally
        {
            if (ReferenceEquals(_loginWithDeviceCancellation, cancellation))
            {
                _loginWithDeviceCancellation = null;
                IsBusy = false;
            }
            cancellation.Dispose();
        }
    }

    private bool CanCancelLoginWithDevice() =>
        IsLoginWithDeviceStage && _loginWithDeviceCancellation is not null;

    [RelayCommand(CanExecute = nameof(CanCancelLoginWithDevice))]
    private void CancelLoginWithDevice() => CompleteLoginWithDeviceTerminal(
        LoginWithDeviceTerminalKind.Canceled,
        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text8992739C06ABCE52),
        LoginStatusKind.Info,
        cancelService: true);

    private Task<AuthResult> AwaitLoginWithDeviceOperationAsync(
        Task<AuthResult> operation,
        CancellationToken ct) => CancelWinsTask.AwaitAsync(
            operation,
            ReleaseDiscardedAuthResultAsync,
            ct);

    private async ValueTask ReleaseDiscardedAuthResultAsync(AuthResult result)
    {
        var lease = result switch
        {
            AuthResult.Success success => success.Lease,
            AuthResult.SsoAuthenticated sso => sso.Lease,
            AuthResult.TrustedDeviceApprovalRequired approval => approval.Lease,
            AuthResult.TdeV2JitProvisioningRequired jit => jit.Lease,
            AuthResult.PasswordChangeRequired passwordChange => passwordChange.Lease,
            _ => null,
        };
        if (lease is not null)
            await LockOrLogoutSessionAsync(lease);
    }

    private async Task LockOrLogoutSessionAsync(VaultSessionLease lease)
    {
        try
        {
            await _auth.LockAsync(lease, CancellationToken.None);
        }
        catch (Exception)
        {
            try
            {
                await _auth.LogoutAsync(lease, CancellationToken.None);
            }
            catch (Exception)
            {
                // Cleanup is best-effort after both conditional session operations failed.
            }
        }
    }

    private void StartLoginWithDeviceTwoFactorExpiration()
    {
        CancelLoginWithDeviceTwoFactorExpiration();
        if (!_isLoginWithDeviceTwoFactor
            || LoginWithDeviceExpiresAtUtc is not { } expiresAt)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _loginWithDeviceTwoFactorExpirationCancellation = cancellation;
        _ = ExpireLoginWithDeviceTwoFactorAsync(expiresAt, cancellation);
    }

    private async Task ExpireLoginWithDeviceTwoFactorAsync(
        DateTimeOffset expiresAt,
        CancellationTokenSource cancellation)
    {
        try
        {
            var delay = expiresAt - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellation.Token);

            if (!ReferenceEquals(
                    _loginWithDeviceTwoFactorExpirationCancellation,
                    cancellation))
            {
                return;
            }

            _loginWithDeviceTwoFactorExpirationCancellation = null;
            if (!_isLoginWithDeviceTwoFactor
                || !IsTwoFactorStage
                || LoginWithDeviceExpiresAtUtc != expiresAt)
            {
                return;
            }

            LoginCommand.Cancel();
            AbandonCurrentAuthenticationAttempt();
            ReturnFromTwoFactorStage();
            LoginWithDeviceTerminal = LoginWithDeviceTerminalKind.Expired;
            IsBusy = false;
            SetStatus(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFCE4930A78A1E385),
                LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.MasterPassword);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    _loginWithDeviceTwoFactorExpirationCancellation,
                    cancellation))
            {
                _loginWithDeviceTwoFactorExpirationCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelLoginWithDeviceTwoFactorExpiration()
    {
        var cancellation = _loginWithDeviceTwoFactorExpirationCancellation;
        _loginWithDeviceTwoFactorExpirationCancellation = null;
        cancellation?.Cancel();
    }

    private bool TryApplyLoginWithDeviceWaiting(
        AuthResult.LoginWithDeviceWaiting waiting)
    {
        if (string.IsNullOrWhiteSpace(waiting.RequestId)
            || string.IsNullOrWhiteSpace(waiting.FingerprintPhrase)
            || waiting.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        _loginWithDeviceRequestId = waiting.RequestId;
        LoginWithDeviceFingerprint = waiting.FingerprintPhrase;
        LoginWithDeviceExpiresAtUtc = waiting.ExpiresAtUtc;
        return true;
    }

    private bool MatchesCurrentLoginWithDeviceRequest(
        AuthResult.LoginWithDevicePending pending) =>
        string.Equals(
            pending.RequestId,
            _loginWithDeviceRequestId,
            StringComparison.Ordinal)
        && string.Equals(
            pending.FingerprintPhrase,
            LoginWithDeviceFingerprint,
            StringComparison.Ordinal)
        && pending.ExpiresAtUtc == LoginWithDeviceExpiresAtUtc;

    private bool IsCurrentLoginWithDeviceOperation(
        long generation,
        CancellationTokenSource cancellation,
        AuthenticationAttemptLease attempt,
        bool allowCancellation = false) =>
        generation == Volatile.Read(ref _loginWithDeviceGeneration)
        && ReferenceEquals(_loginWithDeviceCancellation, cancellation)
        && ReferenceEquals(_loginWithDeviceAttempt, attempt)
        && ReferenceEquals(_authenticationAttempt, attempt)
        && (allowCancellation || !cancellation.IsCancellationRequested);

    private void CompleteLoginWithDeviceFailure(AuthResult.Failure failure) =>
        CompleteLoginWithDeviceTerminal(
            LoginWithDeviceTerminalKind.Failed,
            LoginWithDeviceFailureMessage(failure),
            LoginStatusKind.Error,
            cancelService: true);

    private void CompleteLoginWithDeviceBeginFailure(AuthResult.Failure failure) =>
        CompleteLoginWithDeviceTerminal(
            LoginWithDeviceTerminalKind.Failed,
            failure.Kind is AuthFailureKind.Network or AuthFailureKind.Timeout
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF626EC92CB2CC4A7)
                : LoginWithDeviceFailureMessage(failure),
            LoginStatusKind.Error,
            cancelService: true);

    private void CompleteLoginWithDeviceOutcomeUncertain(
        AuthenticationAttemptLease? handedOffAttempt = null)
    {
        if (handedOffAttempt is not null
            && ReferenceEquals(_authenticationAttempt, handedOffAttempt))
        {
            _auth.AbandonAuthenticationAttempt(handedOffAttempt);
            _authenticationAttempt = null;
        }
        _isSsoTwoFactor = false;
        _isLoginWithDeviceTwoFactor = false;
        ClearTwoFactorChallengeState();
        CompleteLoginWithDeviceTerminal(
            LoginWithDeviceTerminalKind.OutcomeUncertain,
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4166C7678F99D802),
            LoginStatusKind.Error,
            cancelService: false);
    }

    private string LoginWithDeviceFailureMessage(AuthResult.Failure failure) =>
        failure.Kind switch
        {
            AuthFailureKind.InvalidCredentials =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF1FE8882639B63B5),
            AuthFailureKind.SafeMessage =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text65E8F45D102F86BB),
            AuthFailureKind.Unspecified =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text77428D9DB100EDB6),
            _ => FailureMessage(failure, wasTwoFactor: false),
        };

    private void CompleteLoginWithDeviceTerminal(
        LoginWithDeviceTerminalKind terminal,
        string message,
        LoginStatusKind statusKind,
        bool cancelService)
    {
        CancelLoginWithDeviceTwoFactorExpiration();
        var attempt = _loginWithDeviceAttempt;
        var cancellation = _loginWithDeviceCancellation;
        Interlocked.Increment(ref _loginWithDeviceGeneration);
        _loginWithDeviceAttempt = null;
        _loginWithDeviceCancellation = null;
        _loginWithDeviceRequestId = null;

        if (attempt is not null)
        {
            if (cancelService)
            {
                var cancellationResult = _auth.CancelLoginWithDevice(attempt);
                if (terminal == LoginWithDeviceTerminalKind.Canceled
                    && cancellationResult.Status
                        == LoginWithDeviceCancellationStatus.OutcomeUncertain)
                {
                    terminal = LoginWithDeviceTerminalKind.OutcomeUncertain;
                    message = global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3A4B11E851EEF02F);
                    statusKind = LoginStatusKind.Error;
                }
            }
            if (ReferenceEquals(_authenticationAttempt, attempt))
                _authenticationAttempt = null;
        }
        cancellation?.Cancel();

        LoginWithDeviceFingerprint = string.Empty;
        LoginWithDeviceExpiresAtUtc = null;
        LoginWithDeviceTerminal = terminal;
        IsLoginWithDeviceAvailable = false;
        IsBusy = false;
        Stage = LoginStage.Password;
        SetStatus(message, statusKind);
        RequestFocus(LoginFocusTarget.MasterPassword);
        StartLoginWithDeviceAvailabilityProbe();
    }

    private void StartLoginWithDeviceAvailabilityProbe()
    {
        CancelLoginWithDeviceAvailabilityProbe();
        SetLoginWithDeviceQuarantineInfo(null);
        IsLoginWithDeviceAvailable = false;
        if (!IsPasswordStage)
            return;

        var serverUrl = ServerUrl.Trim();
        var email = Email.Trim();
        var cancellation = new CancellationTokenSource();
        var generation = Interlocked.Increment(ref _loginWithDeviceGeneration);
        _loginWithDeviceAvailabilityCancellation = cancellation;
        _ = ProbeLoginWithDeviceAvailabilityAsync(
            generation,
            cancellation,
            serverUrl,
            email);
    }

    private async Task ProbeLoginWithDeviceAvailabilityAsync(
        long generation,
        CancellationTokenSource cancellation,
        string serverUrl,
        string email)
    {
        var available = false;
        LoginWithDeviceQuarantineInfo? quarantineInfo = null;
        try
        {
            quarantineInfo = await CancelWinsTask.AwaitAsync(
                _auth.GetLoginWithDeviceQuarantineInfoAsync(
                    serverUrl,
                    email,
                    cancellation.Token),
                static _ => ValueTask.CompletedTask,
                cancellation.Token);

            if (!quarantineInfo.BlocksLoginWithDevice)
            {
                available = await CancelWinsTask.AwaitAsync(
                    _auth.IsLoginWithDeviceAvailableAsync(
                        serverUrl,
                        email,
                        cancellation.Token),
                    static _ => ValueTask.CompletedTask,
                    cancellation.Token);
            }
        }
        catch (Exception)
        {
            // Any lookup failure leaves the device-login boundary closed.
        }
        finally
        {
            if (generation == Volatile.Read(ref _loginWithDeviceGeneration)
                && ReferenceEquals(
                    _loginWithDeviceAvailabilityCancellation,
                    cancellation))
            {
                _loginWithDeviceAvailabilityCancellation = null;
                SetLoginWithDeviceQuarantineInfo(
                    quarantineInfo
                    ?? new LoginWithDeviceQuarantineInfo.Unavailable());
                IsLoginWithDeviceAvailable = available
                    && IsPasswordStage
                    && !IsLoginWithDeviceQuarantineBlocked
                    && string.Equals(ServerUrl.Trim(), serverUrl, StringComparison.Ordinal)
                    && string.Equals(Email.Trim(), email, StringComparison.OrdinalIgnoreCase);
            }
            cancellation.Dispose();
        }
    }

    private void CancelLoginWithDeviceAvailabilityProbe()
    {
        Interlocked.Increment(ref _loginWithDeviceGeneration);
        CancelLoginWithDeviceQuarantineEligibilityTimer();
        var cancellation = _loginWithDeviceAvailabilityCancellation;
        _loginWithDeviceAvailabilityCancellation = null;
        cancellation?.Cancel();
    }

    private void ResetLoginWithDevicePresentation(bool clearAvailability)
    {
        CancelLoginWithDeviceTwoFactorExpiration();
        if (clearAvailability)
            IsLoginWithDeviceAvailable = false;
        _isLoginWithDeviceTwoFactor = false;
        LoginWithDeviceFingerprint = string.Empty;
        LoginWithDeviceExpiresAtUtc = null;
        LoginWithDeviceTerminal = LoginWithDeviceTerminalKind.None;
    }

    private void SetLoginWithDeviceQuarantineInfo(
        LoginWithDeviceQuarantineInfo? info)
    {
        CancelLoginWithDeviceQuarantineEligibilityTimer();
        _loginWithDeviceQuarantineInfo = info;
        NotifyLoginWithDeviceQuarantineProperties();

        if (info is LoginWithDeviceQuarantineInfo.Found
            {
                IsClearEligible: false,
            } found)
        {
            StartLoginWithDeviceQuarantineEligibilityTimer(found);
        }
    }

    private void StartLoginWithDeviceQuarantineEligibilityTimer(
        LoginWithDeviceQuarantineInfo.Found found)
    {
        var cancellation = new CancellationTokenSource();
        _loginWithDeviceQuarantineEligibilityCancellation = cancellation;
        _ = ObserveLoginWithDeviceQuarantineEligibilityAsync(found, cancellation);
    }

    private async Task ObserveLoginWithDeviceQuarantineEligibilityAsync(
        LoginWithDeviceQuarantineInfo.Found found,
        CancellationTokenSource cancellation)
    {
        try
        {
            while (true)
            {
                var remaining = found.ClearEligibleAtUtc - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                await Task.Delay(
                    remaining > TimeSpan.FromHours(12)
                        ? TimeSpan.FromHours(12)
                        : remaining,
                    cancellation.Token);
            }

            if (ReferenceEquals(_loginWithDeviceQuarantineInfo, found)
                && ReferenceEquals(
                    _loginWithDeviceQuarantineEligibilityCancellation,
                    cancellation))
            {
                _loginWithDeviceQuarantineInfo = found with
                {
                    IsClearEligible = true,
                };
                _loginWithDeviceQuarantineEligibilityCancellation = null;
                NotifyLoginWithDeviceQuarantineProperties();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(
                    _loginWithDeviceQuarantineEligibilityCancellation,
                    cancellation))
            {
                _loginWithDeviceQuarantineEligibilityCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private void CancelLoginWithDeviceQuarantineEligibilityTimer()
    {
        var cancellation = _loginWithDeviceQuarantineEligibilityCancellation;
        _loginWithDeviceQuarantineEligibilityCancellation = null;
        cancellation?.Cancel();
    }

    private void NotifyLoginWithDeviceQuarantineProperties()
    {
        OnPropertyChanged(nameof(IsLoginWithDeviceQuarantineBlocked));
        OnPropertyChanged(nameof(ShowLoginWithDeviceQuarantineNotice));
        OnPropertyChanged(nameof(ShowLoginWithDeviceQuarantineClearAction));
        OnPropertyChanged(nameof(CanClearLoginWithDeviceQuarantine));
        OnPropertyChanged(nameof(LoginWithDeviceQuarantineTitle));
        OnPropertyChanged(nameof(LoginWithDeviceQuarantineMessage));
        OnPropertyChanged(nameof(ShowLoginWithDeviceOption));
        OnPropertyChanged(nameof(CanStartLoginWithDevice));
        LoginWithDeviceCommand.NotifyCanExecuteChanged();
        ClearLoginWithDeviceQuarantineCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearLoginWithDeviceQuarantine))]
    private async Task ClearLoginWithDeviceQuarantineAsync(CancellationToken ct)
    {
        if (!CanClearLoginWithDeviceQuarantine
            || _loginWithDeviceQuarantineInfo is not
                LoginWithDeviceQuarantineInfo.Found expected)
        {
            return;
        }

        var serverUrl = ServerUrl.Trim();
        var email = Email.Trim();
        LoginWithDeviceQuarantineClearResult result;
        try
        {
            IsBusy = true;
            result = await _auth.ClearLoginWithDeviceQuarantineAsync(
                serverUrl,
                email,
                expected,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            result = new LoginWithDeviceQuarantineClearResult(
                LoginWithDeviceQuarantineClearStatus.Unavailable);
        }
        finally
        {
            IsBusy = false;
        }

        if (!IsPasswordStage
            || !string.Equals(ServerUrl.Trim(), serverUrl, StringComparison.Ordinal)
            || !string.Equals(Email.Trim(), email, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetStatus(
            result.Status switch
            {
                LoginWithDeviceQuarantineClearStatus.Cleared =>
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text844A03306C229E1C),
                LoginWithDeviceQuarantineClearStatus.AlreadyAbsent =>
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB9C2AA7F7C85A8AC),
                LoginWithDeviceQuarantineClearStatus.NotEligible =>
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text04B087985A0E1ECF),
                LoginWithDeviceQuarantineClearStatus.Changed =>
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE708D7557FA34C17),
                LoginWithDeviceQuarantineClearStatus.Corrupt =>
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text624835823E41786E),
                _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3A364F81DE8B6588),
            },
            result.IsCleared ? LoginStatusKind.Success : LoginStatusKind.Error);
        StartLoginWithDeviceAvailabilityProbe();
    }

    [RelayCommand]
    private void CancelTrustedDeviceApproval() => _approvalCancellation?.Cancel();

    private async Task CompleteTrustedDeviceApprovalAsync(CancellationToken ct)
    {
        var service = _trustedDeviceApproval;
        var lease = _approvalLease;
        if (service is null || lease is null)
        {
            ReturnFromTrustedDeviceApproval(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD9A83B91AE38EF79),
                LoginStatusKind.Error);
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _approvalCancellation = cancellation;
        try
        {
            IsBusy = true;
            ApprovalFingerprint = string.Empty;
            var request = await service.BeginAsync(
                lease,
                Email,
                ResolveCurrentDeviceIdentifier(),
                _approvalMethod,
                cancellation.Token);
            ApprovalFingerprint = request.FingerprintPhrase;
            SetStatus(
                _approvalMethod == TrustedDeviceApprovalMethod.Administrator
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE9682E07E64CFA99)
                    : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6CB460D88AAD4FF6),
                LoginStatusKind.Info);

            while (true)
            {
                var poll = await service.PollAsync(
                    lease,
                    request.Id,
                    cancellation.Token);
                if (poll.Status == TrustedDeviceApprovalStatus.Pending)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(2),
                        cancellation.Token);
                    continue;
                }
                if (poll.Status == TrustedDeviceApprovalStatus.Denied)
                {
                    ReturnFromTrustedDeviceApproval(
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBD90CAA5C8BC0ED7),
                        LoginStatusKind.Error);
                    return;
                }
                if (poll.Status == TrustedDeviceApprovalStatus.Expired)
                {
                    ReturnFromTrustedDeviceApproval(
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4321D5A1245D6CB1),
                        LoginStatusKind.Error);
                    return;
                }
                if (poll.UserKey is null)
                    throw new TrustedDeviceApprovalException(
                        "The approved response did not contain a user key.");

                var userKey = poll.UserKey;
                AuthResult result;
                try
                {
                    result = await _auth.CompleteTrustedDeviceApprovalAsync(
                        lease,
                        userKey,
                        TrustCurrentDeviceAfterApproval,
                        cancellation.Token);
                    userKey = null!;
                }
                finally
                {
                    if (userKey is not null)
                    {
                        System.Security.Cryptography.CryptographicOperations
                            .ZeroMemory(userKey);
                    }
                }

                await HandleTrustedDeviceApprovalResultAsync(lease, result);
                return;
            }
        }
        catch (OperationCanceledException ex) when (
            UiExceptionPolicy.IsGenuineCancellation(ex, cancellation.Token)
            || lease.CancellationToken.IsCancellationRequested)
        {
            service.Cancel(lease);
            ReturnFromTrustedDeviceApproval(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5B51CE28698579EE),
                LoginStatusKind.Info);
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            service.Cancel(lease);
            ReturnFromTrustedDeviceApproval(
                UiExceptionPolicy.RequestTimeoutMessage,
                LoginStatusKind.Error);
        }
        catch (HttpRequestException)
        {
            service.Cancel(lease);
            ReturnFromTrustedDeviceApproval(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9B0501625B589BC9),
                LoginStatusKind.Error);
        }
        catch (TrustedDeviceApprovalException)
        {
            service.Cancel(lease);
            ReturnFromTrustedDeviceApproval(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6898E24FA43A79EA),
                LoginStatusKind.Error);
        }
        finally
        {
            if (ReferenceEquals(_approvalCancellation, cancellation))
                _approvalCancellation = null;
            IsBusy = false;
        }
    }

    private async Task CompleteTdeV2JitProvisioningAsync(CancellationToken ct)
    {
        var lease = _tdeV2JitLease;
        var organizationIdentifier = _tdeV2JitOrganizationIdentifier;
        if (lease is null || string.IsNullOrWhiteSpace(organizationIdentifier))
        {
            ClearTdeV2JitProvisioning();
            Stage = LoginStage.TrustedDeviceSso;
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA406A2098500DDDE), LoginStatusKind.Error);
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _auth.CompleteTdeV2JitProvisioningAsync(
                lease,
                organizationIdentifier,
                TrustCurrentDeviceAfterApproval,
                ct);
            if (result is AuthResult.Success success
                && success.Lease.SameSessionAs(lease))
            {
                ClearTdeV2JitProvisioning();
                ClearStatus();
                await NotifySuccessfulSessionAsync(success.Lease);
                return;
            }

            var message = result is AuthResult.Failure failure
                ? failure.Kind == AuthFailureKind.SafeMessage
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9082168205CF9F96)
                    : FailureMessage(failure, wasTwoFactor: false)
                : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA406A2098500DDDE);
            ClearTdeV2JitProvisioning();
            Stage = LoginStage.TrustedDeviceSso;
            SetStatus(message, LoginStatusKind.Error);
        }
        catch (OperationCanceledException ex) when (
            UiExceptionPolicy.IsGenuineCancellation(ex, ct)
            || lease.CancellationToken.IsCancellationRequested)
        {
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4AA66B2C5CB5667B), LoginStatusKind.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UnlockWithHelloAsync(CancellationToken ct)
    {
        if (IsBusy)
            return;

        if (_hello is null || _helloUnlock is null)
            return;

        var challenge = _helloUnlock.LoadChallenge();
        if (challenge is null)
            return;

        var attempt = BeginAuthenticationAttempt();

        try
        {
            IsBusy = true;

            var signature = await _hello.GetProtectorAsync(challenge, ct);
            if (signature is null)
            {
                AbandonAuthenticationAttempt(attempt);
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF97B74127CF56E08), LoginStatusKind.Info);
                return;
            }

            var result = await _auth.UnlockWithHelloAsync(attempt, signature, ct);
            await HandleResultAsync(attempt, result); // Reuse the same navigation and status handling as login.
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            AbandonAuthenticationAttempt(attempt);
            ClearStatus();
        }
        catch (HttpRequestException)
        {
            HandleFailure(attempt, new AuthResult.Failure(
                "The authentication server could not be reached.",
                AuthFailureKind.Network));
        }
        catch (TaskCanceledException)
        {
            HandleFailure(attempt, new AuthResult.Failure(
                "The authentication request timed out.",
                AuthFailureKind.Timeout));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CompleteRequiredPasswordChangeAsync(CancellationToken ct)
    {
        var lease = _requiredPasswordChangeLease;
        if (lease is null)
        {
            ClearRequiredPasswordChange();
            Stage = LoginStage.Password;
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB07501BE717A6027), LoginStatusKind.Error);
            return;
        }

        if (NewMasterPassword.Length is < MasterPasswordPolicyRequirements.MinimumLength
                or > MasterPasswordPolicyRequirements.MaximumLength)
        {
            SetFieldError(
                global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextBDCAFC74BC969881,MasterPasswordPolicyRequirements.MinimumLength)
                + global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.TextCD9F30C1A9C1B07E,MasterPasswordPolicyRequirements.MaximumLength), LoginFocusTarget.NewMasterPassword);
            return;
        }
        if (!string.Equals(NewMasterPassword, ConfirmNewMasterPassword, StringComparison.Ordinal))
        {
            SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD64FFABDBF3917ED), LoginFocusTarget.NewMasterPassword);
            return;
        }
        if (MasterPasswordHint.Length > 50
            || string.Equals(MasterPasswordHint.Trim(), NewMasterPassword, StringComparison.Ordinal))
        {
            SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2275448E3BC841A4), LoginFocusTarget.NewMasterPassword);
            return;
        }

        try
        {
            IsBusy = true;
            ClearStatus();
            var result = await _auth.CompleteRequiredPasswordChangeAsync(
                lease,
                NewMasterPassword,
                string.IsNullOrWhiteSpace(MasterPasswordHint)
                    ? null
                    : MasterPasswordHint.Trim(),
                ct);

            switch (result)
            {
                case AuthResult.PasswordChanged:
                    ClearRequiredPasswordChange();
                    RefreshSavedAccounts();
                    Stage = LoginStage.Password;
                    SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text789964519717BE78), LoginStatusKind.Success);
                    RequestFocus(LoginFocusTarget.MasterPassword);
                    break;
                case AuthResult.PasswordChangeUncertain:
                    ClearRequiredPasswordChange();
                    RefreshSavedAccounts();
                    Stage = LoginStage.Password;
                    SetStatus(
                        global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text77811F0564F33C99),
                        LoginStatusKind.Info);
                    RequestFocus(LoginFocusTarget.MasterPassword);
                    break;
                case AuthResult.Failure failure:
                    SetStatus(FailureMessage(failure, wasTwoFactor: false), LoginStatusKind.Error);
                    RequestFocus(LoginFocusTarget.NewMasterPassword);
                    break;
                default:
                    ClearRequiredPasswordChange();
                    Stage = LoginStage.Password;
                    SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB07501BE717A6027), LoginStatusKind.Error);
                    RequestFocus(LoginFocusTarget.MasterPassword);
                    break;
            }
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB2C17B463283370A), LoginStatusKind.Info);
        }
        catch (HttpRequestException)
        {
            ClearRequiredPasswordChange();
            Stage = LoginStage.Password;
            SetStatus(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text69BB59F297B20D98),
                LoginStatusKind.Info);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseDemoVault))]
    private async Task UseDemoVaultAsync()
    {
        if (!CanUseDemoVault || _demoVault is null)
            return;

        AbandonCurrentAuthenticationAttempt();
        var lease = await _demoVault.OpenDemoVaultAsync();
        ClearStatus();
        MasterPassword = string.Empty;
        TwoFactorCode = string.Empty;
        Stage = LoginStage.Account;
        await NotifySuccessfulSessionAsync(lease);
    }

    [RelayCommand]
    private void Back()
    {
        if (IsBusy)
            return;

        ClearStatus();
        if (IsTwoFactorStage)
        {
            AbandonCurrentAuthenticationAttempt();
            ReturnFromTwoFactorStage();
        }
        else if (IsPasswordStage)
        {
            CancelLoginWithDeviceAvailabilityProbe();
            ResetLoginWithDevicePresentation(clearAvailability: true);
            MasterPassword = string.Empty;
            ApiKeySecret = string.Empty;
            ApiKeyClientId = string.Empty;
            IsApiKeyMode = false;
            Stage = LoginStage.Account;
        }
        else if (IsDeviceVerificationStage)
        {
            AbandonCurrentAuthenticationAttempt();
            ReturnFromDeviceVerificationStage();
        }
        else if (IsTrustedDeviceApprovalStage)
        {
            ClearTrustedDeviceApproval();
            Stage = LoginStage.TrustedDeviceSso;
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5B51CE28698579EE), LoginStatusKind.Info);
        }
    }

    [RelayCommand(CanExecute = nameof(CanResendEmailCode))]
    private async Task ResendEmailCodeAsync()
    {
        var attempt = _authenticationAttempt;
        var sent = attempt is not null
            && await _auth.ResendTwoFactorEmailAsync(attempt);
        SetStatus(
            sent ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text88847641B77E6CC2) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6108070FC5C825DB),
            sent ? LoginStatusKind.Success : LoginStatusKind.Error);
    }

    [RelayCommand(CanExecute = nameof(CanResendNewDeviceCode))]
    private async Task ResendNewDeviceCodeAsync()
    {
        var attempt = _authenticationAttempt;
        var sent = attempt is not null
            && await _auth.ResendNewDeviceVerificationAsync(attempt);
        SetStatus(
            sent ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text158623E9757FFFC6) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC438E3E9A9A21B2D),
            sent ? LoginStatusKind.Success : LoginStatusKind.Error);
    }

    [RelayCommand]
    private void ToggleRecoveryCodeMode()
    {
        if (!IsTwoFactorStage || _twoFactorProviders.Count == 0)
            return;

        TwoFactorCode = string.Empty;
        IsRecoveryCodeMode = !IsRecoveryCodeMode;
        TwoFactorProviderOptions = IsRecoveryCodeMode
            ? new ObservableCollection<TwoFactorProviderOption>
            {
                new(RecoveryCodeProvider, ProviderDisplayName(RecoveryCodeProvider)),
            }
            : new ObservableCollection<TwoFactorProviderOption>(_twoFactorProviders
                .Where(IsSupportedTwoFactorProvider)
                .Select(provider => new TwoFactorProviderOption(provider, ProviderDisplayName(provider))));
        SelectedTwoFactorProviderIndex = 0;
        SetStatus(
            IsRecoveryCodeMode
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2C5A840D68009AA0)
                : IsWebAuthnMode
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6513C3EA82E29EAE)
                    : IsDuoMode ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text15E78C7911618EC3) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7D4FFC10D08AE2EF),
            LoginStatusKind.Info);
        RequestFocus(CurrentStageFocusTarget());
        NotifyTwoFactorModeProperties();
    }

    [RelayCommand(CanExecute = nameof(CanRequestPasswordHint))]
    private async Task RequestPasswordHintAsync(CancellationToken ct)
    {
        if (!CanRequestPasswordHint)
            return;
        var generation = ++_passwordHintGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _passwordHintCancellation = cancellation;
        var operationToken = cancellation.Token;
        var serverUrl = ResolveServerUrl();
        var email = Email.Trim();
        _isPasswordHintRequestActive = true;
        IsBusy = true;
        ClearStatus();

        bool IsCurrent() => generation == _passwordHintGeneration && !operationToken.IsCancellationRequested
            && IsPasswordStage && string.Equals(Email.Trim(), email, StringComparison.Ordinal)
            && string.Equals(ResolveServerUrl(), serverUrl, StringComparison.Ordinal);

        try
        {
            await _auth.RequestPasswordHintAsync(serverUrl, email, operationToken);
            if (IsCurrent())
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDE4EE248911F2DBE), LoginStatusKind.Info);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex, operationToken))
        {
            if (IsCurrent())
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB788983A282B5A6B), LoginStatusKind.Error);
        }
        catch (VaultApiException ex)
        {
            if (!IsCurrent())
                return;
            const string hintPrefix = "Your password hint is: ";
            var message = ex.ServerMessage ?? string.Empty;
            if (message.StartsWith(hintPrefix, StringComparison.Ordinal))
                SetStatus(global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text3EF1A849A7CDBD84,message[hintPrefix.Length..]), LoginStatusKind.Info,
                    containsPasswordHint: true);
            else if (message == "Sorry, you have no password hint...")
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6C0A724FA2399151), LoginStatusKind.Info);
            else if (message == "This server is not configured to provide password hints.")
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text01976BC9092ACA1D), LoginStatusKind.Error);
            else if (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC7DCEDFB83090600), LoginStatusKind.Error);
            else
                SetStatus(string.IsNullOrWhiteSpace(message) ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text38C5BC271169217A) : global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text49D8B10BE48D6E03,message), LoginStatusKind.Error);
        }
        catch (NotSupportedException)
        {
            if (IsCurrent())
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2D904ACEBC390E5D), LoginStatusKind.Error);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
        {
            if (IsCurrent())
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFFE34B00B3A54C78), LoginStatusKind.Error);
        }
        finally
        {
            if (ReferenceEquals(_passwordHintCancellation, cancellation))
                _passwordHintCancellation = null;
            if (generation == _passwordHintGeneration)
            {
                _isPasswordHintRequestActive = false;
                IsBusy = false;
            }
        }
    }

    private void InvalidatePasswordHintRequest()
    {
        _passwordHintGeneration++;
        var cancellation = _passwordHintCancellation;
        _passwordHintCancellation = null;
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        RequestPasswordHintCommand.Cancel();
        if (_statusContainsPasswordHint)
            ClearStatus();
        if (_isPasswordHintRequestActive)
        {
            _isPasswordHintRequestActive = false;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUsePinUnlock))]
    private void UsePinUnlock()
    {
        if (!CanUsePinUnlock)
            return;

        MasterPassword = string.Empty;
        Pin = string.Empty;
        ClearStatus();
        Stage = LoginStage.PinUnlock;
        RequestFocus(LoginFocusTarget.Pin);
    }

    [RelayCommand]
    private void UseMasterPassword()
    {
        if (IsBusy)
            return;

        Stage = ResolveNonPinUnlockStage();
        Pin = string.Empty;
        SetStatus(
            IsKeyConnectorUnlockStage
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6C26BE6DAC29951E)
                : IsTrustedDeviceSsoStage
                    ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text52729D84A5FC5563)
                : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text92735DEC8C488201),
            LoginStatusKind.Info);
    }

    [RelayCommand]
    private async Task SwitchAccountAsync()
    {
        if (IsBusy)
            return;

        _beforeAccountChange?.Invoke();

        var authenticatedLease = _requiredPasswordChangeLease
            ?? _tdeV2JitLease
            ?? _approvalLease;
        if (authenticatedLease is not null)
        {
            try
            {
                await _auth.LogoutAsync(
                    authenticatedLease,
                    CancellationToken.None);
            }
            catch (AuthSessionPersistenceException)
            {
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text31501890D6B0E2AC), LoginStatusKind.Error);
                return;
            }
        }

        var currentServer = ServerUrl;
        PrepareAccount(currentServer, string.Empty);
        RefreshSavedAccounts();
    }

    [RelayCommand]
    private void SelectSavedAccount(SavedAccountOption? account)
    {
        if (IsBusy)
            return;
        if (account is not null)
            _beforeAccountChange?.Invoke();
        if (account is null || _savedAccountService?.TryActivate(account) != true)
        {
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text47A71D6B04A3381A), LoginStatusKind.Error);
            return;
        }

        if (_tokenStore?.TryLoad(out var activated) != true
            || activated.Endpoints is null
            || !string.Equals(
                activated.AccountId,
                account.AccountId,
                StringComparison.Ordinal)
            || activated.Endpoints != account.Endpoints)
        {
            SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text5CE126BA805BF30B), LoginStatusKind.Error);
            return;
        }

        RefreshSavedAccounts();
        PreparePersistedSession(activated);
    }

    // 锁屏/登出时清空登录页所有机密输入:主密码、PIN、2FA 验证码与其可选项。幂等。
    public void ClearSensitive()
    {
        ApiKeySecret = string.Empty;
        ApiKeyClientId = string.Empty;
        IsApiKeyMode = false;
        InvalidatePasswordHintRequest();
        ClearTrustedDeviceApproval();
        ClearTdeV2JitProvisioning();
        ClearRequiredPasswordChange();
        AbandonCurrentAuthenticationAttempt();
        ResetLoginWithDevicePresentation(clearAvailability: true);
        MasterPassword = string.Empty;
        DeviceVerificationCode = string.Empty;
        Pin = string.Empty;
        TwoFactorCode = string.Empty;
        TwoFactorProviderOptions = new();
        _twoFactorProviders = [];
        _twoFactorProviderData = new Dictionary<int, string>();
        IsRecoveryCodeMode = false;
    }

    private void ContinueToPassword()
    {
        ClearStatus();
        IsApiKeyMode = false;
        ApiKeySecret = string.Empty;
        ApiKeyClientId = string.Empty;

        var serverUrl = ResolveServerUrl();
        if (!IsValidServerUrl(serverUrl))
        {
            SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA9C8539849E5660C), LoginFocusTarget.ServerUrl);
            return;
        }

        if (string.IsNullOrWhiteSpace(Email))
        {
            SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCC1DD3420167F716), LoginFocusTarget.Email);
            return;
        }

        ServerUrl = serverUrl;
        Email = Email.Trim();
        MasterPassword = string.Empty;
        DeviceVerificationCode = string.Empty;
        TwoFactorCode = string.Empty;
        Stage = LoginStage.Password;
        StartLoginWithDeviceAvailabilityProbe();
    }

    private bool ValidateSecretInput()
    {
        ClearStatus();

        if (IsPasswordStage && IsApiKeyMode)
        {
            if (string.IsNullOrWhiteSpace(ApiKeyClientId))
            {
                SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text54DEEC654340E762), LoginFocusTarget.ApiKeyClientId);
                return false;
            }
            if (string.IsNullOrWhiteSpace(ApiKeySecret))
            {
                SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text23183976D85A9FEE), LoginFocusTarget.ApiKeySecret);
                return false;
            }
        }

        if ((IsPasswordStage || IsUnlockStage) && string.IsNullOrWhiteSpace(MasterPassword))
        {
            SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC8EA1DB856C8E54D), LoginFocusTarget.MasterPassword);
            return false;
        }

        if (IsPinUnlockStage && string.IsNullOrWhiteSpace(Pin))
        {
            SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4769860954D39FA4), LoginFocusTarget.Pin);
            return false;
        }

        if (IsDeviceVerificationStage && string.IsNullOrWhiteSpace(DeviceVerificationCode))
        {
            SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextEDCC38BAA8BD584A), LoginFocusTarget.DeviceVerificationCode);
            return false;
        }

        if (ShowTwoFactorCodeInput && string.IsNullOrWhiteSpace(TwoFactorCode))
        {
            SetFieldError(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7D4FFC10D08AE2EF), LoginFocusTarget.TwoFactorCode);
            return false;
        }

        return true;
    }

    private LoginStage ResolveNonPinUnlockStage() => _usesTrustedDeviceSso
        ? LoginStage.TrustedDeviceSso
        : _usesKeyConnector
            ? LoginStage.KeyConnectorUnlock
            : LoginStage.Unlock;

    private PersistedSessionUnlockMethod ResolveCurrentSavedUnlockMethod(
        string serverUrl,
        string email)
    {
        if (_tokenStore?.TryLoad(out var session) != true
            || session.Endpoints is not { } endpoints
            || !TryParseServerEndpoint(serverUrl, out var webVault))
        {
            return PersistedSessionUnlockMethod.MasterPassword;
        }

        return endpoints.WebVault == webVault
            && string.Equals(
                session.Email.Trim(),
                email.Trim(),
                StringComparison.OrdinalIgnoreCase)
            ? session.UnlockMethod
            : PersistedSessionUnlockMethod.MasterPassword;
    }

    // 页面在 Loaded 时调用:构造期 PrepareUnlock/PrepareAccount 的焦点请求早于页面订阅 PropertyChanged,
    // 会被漏掉,故加载完成后按当前阶段补发一次。
    public void RequestStageFocus() => RequestFocus(CurrentStageFocusTarget());

    private void RequestFocus(LoginFocusTarget target)
    {
        FocusTarget = target;
        checked { FocusRequestId++; }
    }

    private LoginFocusTarget CurrentStageFocusTarget() => Stage switch
    {
        // 自托管且 URL 尚不合法时先聚焦地址框,否则默认从邮箱开始。
        LoginStage.Account => SelectedServerOptionIndex == 2 && !IsValidServerUrl(ServerUrl.Trim())
            ? LoginFocusTarget.ServerUrl
            : LoginFocusTarget.Email,
        LoginStage.Password => IsApiKeyMode
            ? LoginFocusTarget.ApiKeyClientId
            : LoginFocusTarget.MasterPassword,
        LoginStage.LoginWithDevice => LoginFocusTarget.LoginWithDeviceCancel,
        LoginStage.AccountDeletionRecovery =>
            LoginFocusTarget.AccountDeletionRecoveryAction,
        LoginStage.DeviceVerification => LoginFocusTarget.DeviceVerificationCode,
        LoginStage.PasswordChangeRequired => LoginFocusTarget.NewMasterPassword,
        LoginStage.Unlock => LoginFocusTarget.MasterPassword,
        LoginStage.KeyConnectorUnlock => LoginFocusTarget.PrimaryAction,
        LoginStage.TrustedDeviceSso => LoginFocusTarget.PrimaryAction,
        LoginStage.TrustedDeviceApproval => LoginFocusTarget.PrimaryAction,
        LoginStage.TwoFactor => IsWebAuthnMode || IsDuoMode
            ? LoginFocusTarget.PrimaryAction
            : LoginFocusTarget.TwoFactorCode,
        LoginStage.PinUnlock => LoginFocusTarget.Pin,
        _ => LoginFocusTarget.Email,
    };

    // 网络/超时失败后把焦点还给当前阶段的机密输入,方便用户直接重试。
    private LoginFocusTarget CurrentSecretFocusTarget() => Stage switch
    {
        LoginStage.Password when IsApiKeyMode => LoginFocusTarget.ApiKeySecret,
        LoginStage.LoginWithDevice => LoginFocusTarget.LoginWithDeviceCancel,
        LoginStage.AccountDeletionRecovery =>
            LoginFocusTarget.AccountDeletionRecoveryAction,
        LoginStage.DeviceVerification => LoginFocusTarget.DeviceVerificationCode,
        LoginStage.PasswordChangeRequired => LoginFocusTarget.NewMasterPassword,
        LoginStage.TwoFactor => IsWebAuthnMode || IsDuoMode
            ? LoginFocusTarget.PrimaryAction
            : LoginFocusTarget.TwoFactorCode,
        LoginStage.PinUnlock => LoginFocusTarget.Pin,
        LoginStage.KeyConnectorUnlock => LoginFocusTarget.PrimaryAction,
        LoginStage.TrustedDeviceSso => LoginFocusTarget.PrimaryAction,
        LoginStage.TrustedDeviceApproval => LoginFocusTarget.PrimaryAction,
        LoginStage.Account => LoginFocusTarget.Email,
        _ => LoginFocusTarget.MasterPassword,
    };

    private async Task<bool> NotifySuccessfulSessionAsync(VaultSessionLease lease)
    {
        var handler = _tryAcceptSuccess;
        if (handler is null)
        {
            if (!_requiresSuccessAcceptance)
            {
                _onSuccess?.Invoke(lease);
                return true;
            }
        }
        else
        {
            try
            {
                if (handler(lease))
                    return true;
            }
            catch (Exception)
            {
                // A UI handoff failure is treated the same as an explicit rejection.
            }
        }

        await LockOrLogoutSessionAsync(lease);
        if (_accountDeletion is null)
            PrepareUnlock(lease.Endpoints.WebVault.Value, Email);
        else if (_tokenStore?.TryLoad(out var persisted) == true)
            PrepareInitialLoginState(persisted);
        else
            PrepareInitialLoginState(activeSession: null);
        SetStatus(
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text78E4345CDD8E87DC),
            LoginStatusKind.Error);
        return false;
    }

    private async Task HandleResultAsync(
        AuthenticationAttemptLease attempt,
        AuthResult result)
    {
        if (!ReferenceEquals(_authenticationAttempt, attempt))
        {
            await ReleaseDiscardedAuthResultAsync(result);
            return;
        }

        switch (result)
        {
            case AuthResult.Success success:
                var wasTwoFactor = IsTwoFactorStage;
                _isSsoTwoFactor = false;
                _isLoginWithDeviceTwoFactor = false;
                _authenticationAttempt = null;
                ClearStatus();
                MasterPassword = string.Empty;
                DeviceVerificationCode = string.Empty;
                TwoFactorCode = string.Empty;
                Pin = string.Empty;
                IsRecoveryCodeMode = false;
                _twoFactorProviderData = new Dictionary<int, string>();
                LoginWithDeviceExpiresAtUtc = null;
                CancelLoginWithDeviceTwoFactorExpiration();
                if (wasTwoFactor)
                    Stage = LoginStage.Password;
                await NotifySuccessfulSessionAsync(success.Lease);
                break;
            case AuthResult.SsoAuthenticated:
                _isSsoTwoFactor = false;
                _isLoginWithDeviceTwoFactor = false;
                _authenticationAttempt = null;
                MasterPassword = string.Empty;
                DeviceVerificationCode = string.Empty;
                TwoFactorCode = string.Empty;
                Pin = string.Empty;
                IsRecoveryCodeMode = false;
                _twoFactorProviderData = new Dictionary<int, string>();
                RefreshSavedAccounts();
                Stage = LoginStage.Unlock;
                SetStatus(global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3C96EFB37F913E30), LoginStatusKind.Success);
                break;
            case AuthResult.TrustedDeviceApprovalRequired approval:
                _isSsoTwoFactor = false;
                _isLoginWithDeviceTwoFactor = false;
                _authenticationAttempt = null;
                _approvalLease = approval.Lease;
                ApprovalMethodOptions = CreateApprovalMethodOptions(approval);
                SelectedApprovalMethodIndex = 0;
                _approvalMethod = ApprovalMethodOptions[0].Method;
                _usesTrustedDeviceSso = true;
                _usesKeyConnector = false;
                OnPropertyChanged(nameof(AlternativeUnlockText));
                MasterPassword = string.Empty;
                TwoFactorCode = string.Empty;
                Pin = string.Empty;
                ApprovalFingerprint = string.Empty;
                TrustCurrentDeviceAfterApproval = true;
                RefreshSavedAccounts();
                Stage = LoginStage.TrustedDeviceApproval;
                SetStatus(
                    ApprovalMethodOptions.Count > 1
                        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB6B67384AF83A2CF)
                        : _approvalMethod == TrustedDeviceApprovalMethod.Administrator
                            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBFC8927EA86FFE19)
                            : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3362C47F93FE349C),
                    LoginStatusKind.Info);
                break;
            case AuthResult.TdeV2JitProvisioningRequired jit:
                _isSsoTwoFactor = false;
                _isLoginWithDeviceTwoFactor = false;
                _authenticationAttempt = null;
                _tdeV2JitLease = jit.Lease;
                _tdeV2JitOrganizationIdentifier = jit.OrganizationIdentifier;
                _usesTrustedDeviceSso = true;
                _usesKeyConnector = false;
                MasterPassword = string.Empty;
                TwoFactorCode = string.Empty;
                Pin = string.Empty;
                TrustCurrentDeviceAfterApproval =
                    jit.RequiredTrustCurrentDevice ?? true;
                CanChooseTrustCurrentDeviceAfterJit =
                    jit.RequiredTrustCurrentDevice is null;
                RefreshSavedAccounts();
                Stage = LoginStage.TdeV2JitProvisioning;
                SetStatus(
                    jit.RequiredTrustCurrentDevice is null
                        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE4D61CCE24398378)
                        : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB7BC6869A6090766),
                    LoginStatusKind.Info);
                break;
            case AuthResult.PasswordChangeRequired passwordChange:
                _isLoginWithDeviceTwoFactor = false;
                _authenticationAttempt = null;
                _requiredPasswordChangeLease = passwordChange.Lease;
                _requiredPasswordChangeReason = passwordChange.Reason;
                _requiredPasswordPolicy = passwordChange.Policy;
                MasterPassword = string.Empty;
                DeviceVerificationCode = string.Empty;
                TwoFactorCode = string.Empty;
                NewMasterPassword = string.Empty;
                ConfirmNewMasterPassword = string.Empty;
                MasterPasswordHint = string.Empty;
                Stage = LoginStage.PasswordChangeRequired;
                OnPropertyChanged(nameof(RequiredPasswordPolicySummary));
                SetStatus(
                    passwordChange.Reason == RequiredPasswordChangeReason.AdministratorTemporaryPassword
                        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6AECF91477513EB8)
                        : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD76C4B60C9AB517A),
                    LoginStatusKind.Info);
                RequestFocus(LoginFocusTarget.NewMasterPassword);
                break;
            case AuthResult.TwoFactorRequired twoFactor:
                _twoFactorProviders = twoFactor.Providers;
                _twoFactorProviderData = twoFactor.ProviderData
                    ?? new Dictionary<int, string>();
                var supported = twoFactor.Providers
                    .Where(IsSupportedTwoFactorProvider)
                    .ToList();
                Stage = LoginStage.TwoFactor;
                MasterPassword = string.Empty;
                TwoFactorCode = string.Empty;
                if (supported.Count == 0)
                {
                    IsRecoveryCodeMode = true;
                    TwoFactorProviderOptions = new ObservableCollection<TwoFactorProviderOption>
                    {
                        new TwoFactorProviderOption(
                            RecoveryCodeProvider,
                            ProviderDisplayName(RecoveryCodeProvider)),
                    };
                    SelectedTwoFactorProviderIndex = 0;
                    SetStatus(
                        HasLegacyDuoChallenge()
                            ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC07F42630DA1FC91)
                            : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text30DDD6A6A92A669D),
                        LoginStatusKind.Info);
                    NotifyTwoFactorModeProperties();
                    if (_isLoginWithDeviceTwoFactor)
                        StartLoginWithDeviceTwoFactorExpiration();
                    break;
                }
                IsRecoveryCodeMode = false;
                TwoFactorProviderOptions = new ObservableCollection<TwoFactorProviderOption>(supported
                    .Select(p => new TwoFactorProviderOption(p, ProviderDisplayName(p))));
                SelectedTwoFactorProviderIndex = 0;
                SetStatus(
                    IsWebAuthnMode
                        ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6513C3EA82E29EAE)
                        : IsDuoMode ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text15E78C7911618EC3) : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7D4FFC10D08AE2EF),
                    LoginStatusKind.Info);
                NotifyTwoFactorModeProperties();
                if (_isLoginWithDeviceTwoFactor)
                    StartLoginWithDeviceTwoFactorExpiration();
                break;
            case AuthResult.DeviceVerificationRequired:
                _isLoginWithDeviceTwoFactor = false;
                Stage = LoginStage.DeviceVerification;
                MasterPassword = string.Empty;
                DeviceVerificationCode = string.Empty;
                TwoFactorCode = string.Empty;
                SetStatus(
                    global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text58AE3E6A7B01397F),
                    LoginStatusKind.Info);
                RequestFocus(LoginFocusTarget.DeviceVerificationCode);
                break;
            case AuthResult.PinCleared pinCleared:
                AbandonAuthenticationAttempt(attempt);
                SetStatus(pinCleared.Message, LoginStatusKind.Error);
                Stage = ResolveNonPinUnlockStage();
                Pin = string.Empty;
                break;
            case AuthResult.LoginWithDeviceOutcomeUncertain:
            case AuthResult.Failure { Kind: AuthFailureKind.OutcomeUncertain }:
                CompleteLoginWithDeviceOutcomeUncertain(attempt);
                break;
            case AuthResult.Failure failure:
                HandleFailure(attempt, failure);
                break;
        }
    }

    private async Task HandleTrustedDeviceApprovalResultAsync(
        VaultSessionLease lease,
        AuthResult result)
    {
        if (result is AuthResult.Success success
            && success.Lease.SameSessionAs(lease))
        {
            _approvalLease = null;
            _approvalMethod = TrustedDeviceApprovalMethod.Device;
            ApprovalFingerprint = string.Empty;
            ClearStatus();
            await NotifySuccessfulSessionAsync(success.Lease);
            return;
        }

        var message = result is AuthResult.Failure failure
            ? failure.Kind == AuthFailureKind.SafeMessage
                ? global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE78AD519016A66B4)
                : FailureMessage(failure, wasTwoFactor: false)
            : global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA0EB75855CF7FFCB);
        ReturnFromTrustedDeviceApproval(message, LoginStatusKind.Error);
    }

    private void ReturnFromTrustedDeviceApproval(
        string message,
        LoginStatusKind kind)
    {
        _approvalLease = null;
        _approvalMethod = TrustedDeviceApprovalMethod.Device;
        ApprovalMethodOptions = new();
        SelectedApprovalMethodIndex = 0;
        ApprovalFingerprint = string.Empty;
        Stage = LoginStage.TrustedDeviceSso;
        SetStatus(message, kind);
        RequestFocus(LoginFocusTarget.PrimaryAction);
    }

    private void ClearTrustedDeviceApproval()
    {
        _approvalCancellation?.Cancel();
        if (_approvalLease is { } lease)
            _trustedDeviceApproval?.Cancel(lease);
        _approvalLease = null;
        _approvalMethod = TrustedDeviceApprovalMethod.Device;
        ApprovalMethodOptions = new();
        SelectedApprovalMethodIndex = 0;
        ApprovalFingerprint = string.Empty;
    }

    private void ClearTdeV2JitProvisioning()
    {
        _tdeV2JitLease = null;
        _tdeV2JitOrganizationIdentifier = null;
        CanChooseTrustCurrentDeviceAfterJit = true;
    }

    private void ClearRequiredPasswordChange()
    {
        _requiredPasswordChangeLease = null;
        _requiredPasswordPolicy = null;
        _requiredPasswordChangeReason = default;
        MasterPassword = string.Empty;
        NewMasterPassword = string.Empty;
        ConfirmNewMasterPassword = string.Empty;
        MasterPasswordHint = string.Empty;
        OnPropertyChanged(nameof(RequiredPasswordPolicySummary));
    }

    private string ResolveCurrentDeviceIdentifier()
    {
        if (_approvalLease is not { } lease
            || _tokenStore?.TryLoad(out var persisted) != true
            || !_usesTrustedDeviceSso
            || persisted.AccountId != lease.AccountId
            || persisted.Endpoints != lease.Endpoints
            || !Guid.TryParseExact(persisted.DeviceIdentifier, "D", out _))
        {
            throw new TrustedDeviceApprovalException(
                "The trusted-device session binding is unavailable.");
        }

        return persisted.DeviceIdentifier;
    }

    private AuthenticationAttemptLease BeginAuthenticationAttempt()
    {
        AbandonCurrentAuthenticationAttempt();
        var attempt = _auth.BeginAuthenticationAttempt();
        _authenticationAttempt = attempt;
        return attempt;
    }

    private void AbandonAuthenticationAttempt(AuthenticationAttemptLease attempt)
    {
        _auth.AbandonAuthenticationAttempt(attempt);
        if (ReferenceEquals(_authenticationAttempt, attempt))
            _authenticationAttempt = null;
    }

    private void AbandonCurrentAuthenticationAttempt()
    {
        _ssoCancellation?.Cancel();
        CancelLoginWithDeviceAvailabilityProbe();

        var deviceCancellation = _loginWithDeviceCancellation;
        var deviceAttempt = _loginWithDeviceAttempt;
        if (deviceCancellation is not null || deviceAttempt is not null)
        {
            Interlocked.Increment(ref _loginWithDeviceGeneration);
            _loginWithDeviceCancellation = null;
            _loginWithDeviceAttempt = null;
            _loginWithDeviceRequestId = null;
            deviceCancellation?.Cancel();
            if (deviceAttempt is not null)
            {
                _auth.CancelLoginWithDevice(deviceAttempt);
                if (ReferenceEquals(_authenticationAttempt, deviceAttempt))
                    _authenticationAttempt = null;
            }
            LoginWithDeviceFingerprint = string.Empty;
            LoginWithDeviceExpiresAtUtc = null;
            IsBusy = false;
        }

        if (_authenticationAttempt is { } attempt)
            AbandonAuthenticationAttempt(attempt);
    }

    private void HandleFailure(
        AuthenticationAttemptLease attempt,
        AuthResult.Failure failure)
    {
        if (failure.Kind == AuthFailureKind.AccountDeletionRecoveryRequired)
        {
            AbandonAuthenticationAttempt(attempt);
            MasterPassword = string.Empty;
            Pin = string.Empty;
            try
            {
                if (_tokenStore?.TryLoad(out var persisted) == true)
                    PrepareInitialLoginState(persisted);
                else
                    PrepareInitialLoginState(activeSession: null);
            }
            catch
            {
                PrepareInitialLoginState(activeSession: null);
            }
            SetStatus(
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA7F7E059F9FEC7B9),
                LoginStatusKind.Error);
            return;
        }

        var wasTwoFactor = IsTwoFactorStage;
        var wasDeviceVerification = IsDeviceVerificationStage;
        var message = FailureMessage(failure, wasTwoFactor || wasDeviceVerification);
        var canRetryChallenge = (wasTwoFactor || wasDeviceVerification)
            && failure.Kind is AuthFailureKind.Unspecified
                or AuthFailureKind.InvalidCredentials
                or AuthFailureKind.Network
                or AuthFailureKind.RateLimited
                or AuthFailureKind.Timeout;

        if (!canRetryChallenge)
        {
            AbandonAuthenticationAttempt(attempt);
            if (wasTwoFactor)
                ReturnFromTwoFactorStage();
            else if (wasDeviceVerification)
                ReturnFromDeviceVerificationStage();
        }

        if (IsPinUnlockStage)
            Pin = string.Empty;

        SetStatus(message, LoginStatusKind.Error,
            inlineErrorTarget: failure.Kind == AuthFailureKind.InvalidCredentials
                || failure.Kind == AuthFailureKind.SafeMessage && IsPinUnlockStage
                ? CurrentSecretFocusTarget()
                : null);
        RequestFocus(CurrentSecretFocusTarget());
    }

    private string FailureMessage(AuthResult.Failure failure, bool wasTwoFactor) =>
        failure.Kind switch
        {
            AuthFailureKind.RateLimited =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2DF909F4558C7A91),
            AuthFailureKind.InvalidCredentials when wasTwoFactor && IsRecoveryCodeMode =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text296FF92C0E47AA6B),
            AuthFailureKind.InvalidCredentials when wasTwoFactor =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA8DBC4C19E59E396),
            AuthFailureKind.InvalidCredentials when IsUnlockStage =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3AEFC83F9B16D36C),
            AuthFailureKind.InvalidCredentials =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9895C50F00CC2460),
            AuthFailureKind.Network =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9B0501625B589BC9),
            AuthFailureKind.Timeout =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9EB5964A7F40DE2F),
            AuthFailureKind.InvalidResponse =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA748BB4E8773680D),
            AuthFailureKind.SessionExpired =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextE02E26E76AA0E916),
            AuthFailureKind.NoSavedSession =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1839ECA5FAB94C5B),
            AuthFailureKind.UnsupportedSsoDecryption =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text7F59246C3A57F0F1),
            AuthFailureKind.SsoAuthorization =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text440C33B846B2A208),
            AuthFailureKind.AccountDeletionRecoveryRequired =>
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA7F7E059F9FEC7B9),
            AuthFailureKind.SafeMessage when !string.IsNullOrWhiteSpace(failure.Message) =>
                failure.Message,
            _ => LocalizeLegacyAuthMessage(failure.Message),
        };

    private string LocalizeLegacyAuthMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return GenericFailureMessage();

        if (message.Contains("Username or password is incorrect", StringComparison.OrdinalIgnoreCase))
            return global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text9895C50F00CC2460);

        return message switch
        {
            "No saved session." => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1839ECA5FAB94C5B),
            "Authentication failed." => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF10B545D5BBDCAF0),
            "Two-factor authentication is still required." => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD42AC189F2BBDAB5),
            "No two-factor challenge is pending." => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text54DB5CB2AEDE4EC4),
            "Token response did not include a protected user key." => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text4EBBDECB37A1B045),
            _ => GenericFailureMessage(),
        };
    }

    private string GenericFailureMessage() => Stage switch
    {
        LoginStage.LoginWithDevice => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text77428D9DB100EDB6),
        LoginStage.DeviceVerification => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text0740D38D50534179),
        LoginStage.PasswordChangeRequired => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD47424DECE11F933),
        LoginStage.TwoFactor => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD033C3B684D962B6),
        LoginStage.Unlock or LoginStage.PinUnlock or LoginStage.KeyConnectorUnlock
            or LoginStage.TrustedDeviceSso or LoginStage.TrustedDeviceApproval =>
            global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1F805A1904ADDD69),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text42369FD15761E2EC),
    };

    private void ReturnFromTwoFactorStage(
        bool refreshLoginWithDeviceAvailability = true)
    {
        CancelLoginWithDeviceTwoFactorExpiration();
        ClearTwoFactorChallengeState();
        var returnToSso = _isSsoTwoFactor;
        var returnFromLoginWithDevice = _isLoginWithDeviceTwoFactor;
        _isSsoTwoFactor = false;
        _isLoginWithDeviceTwoFactor = false;
        LoginWithDeviceExpiresAtUtc = null;
        Stage = returnToSso
            ? _usesTrustedDeviceSso
                ? LoginStage.TrustedDeviceSso
                : LoginStage.Account
            : LoginStage.Password;
        if (returnFromLoginWithDevice
            && refreshLoginWithDeviceAvailability
            && IsPasswordStage)
        {
            StartLoginWithDeviceAvailabilityProbe();
        }
    }

    private void ClearTwoFactorChallengeState()
    {
        _twoFactorProviders = Array.Empty<int>();
        _twoFactorProviderData = new Dictionary<int, string>();
        TwoFactorProviderOptions = new();
        TwoFactorCode = string.Empty;
        IsRecoveryCodeMode = false;
    }

    private void ReturnFromDeviceVerificationStage()
    {
        DeviceVerificationCode = string.Empty;
        Stage = LoginStage.Password;
    }

    private void SetFieldError(string message, LoginFocusTarget target)
    {
        SetStatus(message, LoginStatusKind.Error, inlineErrorTarget: target);
        RequestFocus(target);
    }

    private void SetStatus(
        string message,
        LoginStatusKind kind,
        LoginFocusTarget? inlineErrorTarget = null,
        bool isUnlockPrompt = false,
        bool containsPasswordHint = false)
    {
        InlineErrorTarget = inlineErrorTarget;
        IsUnlockPrompt = isUnlockPrompt;
        _statusContainsPasswordHint = containsPasswordHint;
        StatusKind = kind;
        Status = message;
    }

    private void ClearStatus()
    {
        InlineErrorTarget = null;
        IsUnlockPrompt = false;
        _statusContainsPasswordHint = false;
        StatusKind = LoginStatusKind.Info;
        Status = string.Empty;
    }

    private string ResolveServerUrl()
    {
        if (!IsAccountStage)
            return ServerUrl.Trim();

        return SelectedServerOptionIndex switch
        {
            0 => BitwardenUsUrl,
            1 => BitwardenEuUrl,
            _ => ServerUrl.Trim(),
        };
    }

    private static bool IsValidServerUrl(string serverUrl) =>
        TryParseServerEndpoint(serverUrl, out _);

    private static string FormatServerSummary(string serverUrl)
    {
        if (TryParseServerEndpoint(serverUrl, out var endpoint))
            return endpoint.Value;

        return serverUrl.Trim();
    }

    private static bool TryParseServerEndpoint(
        string serverUrl,
        out ServerEndpoint endpoint)
    {
        try
        {
            return ServerEndpoint.TryParse(serverUrl, out endpoint);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            endpoint = default;
            return false;
        }
    }

    partial void OnSelectedServerOptionIndexChanged(int value)
    {
        if (value == 0)
            ServerUrl = BitwardenUsUrl;
        else if (value == 1)
            ServerUrl = BitwardenEuUrl;
        else if (ServerUrl is BitwardenUsUrl or BitwardenEuUrl)
            ServerUrl = string.Empty;

        NotifyServerProperties();
        if (value == 2 && IsAccountStage)
            RequestFocus(LoginFocusTarget.ServerUrl);
    }

    partial void OnServerUrlChanged(string value)
    {
        if (_isPasswordHintRequestActive || _statusContainsPasswordHint)
            InvalidatePasswordHintRequest();
        NotifyServerProperties();
    }

    partial void OnEmailChanged(string value)
    {
        if (_isPasswordHintRequestActive || _statusContainsPasswordHint)
            InvalidatePasswordHintRequest();
        OnPropertyChanged(nameof(ShowAccountContext));
        OnPropertyChanged(nameof(AccountSummary));
        OnPropertyChanged(nameof(AccountInitial));
        OnPropertyChanged(nameof(FormSubtitle));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnIsLoginWithDeviceAvailableChanged(bool value)
    {
        LoginWithDeviceCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTwoFactorProviderIndexChanged(int value)
    {
        NotifyTwoFactorModeProperties();
        if (IsTwoFactorStage && !IsRecoveryCodeMode)
        {
            TwoFactorCode = string.Empty;
            RequestFocus(CurrentStageFocusTarget());
        }
    }

    partial void OnSelectedApprovalMethodIndexChanged(int value)
    {
        if (ApprovalMethodOptions.Count == 0)
            return;
        var index = Math.Clamp(value, 0, ApprovalMethodOptions.Count - 1);
        _approvalMethod = ApprovalMethodOptions[index].Method;
    }

    private static ObservableCollection<TrustedDeviceApprovalMethodOption> CreateApprovalMethodOptions(
        AuthResult.TrustedDeviceApprovalRequired approval)
    {
        var options = new ObservableCollection<TrustedDeviceApprovalMethodOption>();
        if (approval.HasLoginApprovingDevice)
        {
            options.Add(new TrustedDeviceApprovalMethodOption(
                TrustedDeviceApprovalMethod.Device,
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2EF14FB058078C61)));
        }
        if (approval.HasAdminApproval)
        {
            options.Add(new TrustedDeviceApprovalMethodOption(
                TrustedDeviceApprovalMethod.Administrator,
                global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextDA113F25899B2CE1)));
        }
        if (options.Count == 0)
            throw new InvalidOperationException("No trusted-device approval method is available.");
        return options;
    }

    partial void OnIsRecoveryCodeModeChanged(bool value)
    {
        NotifyTwoFactorModeProperties();
    }

    partial void OnAccountDeletionRecoveryNoticeChanged(
        AccountDeletionRecoveryNoticeKind value)
    {
        OnPropertyChanged(nameof(ShowClearPendingAccountDeletion));
        OnPropertyChanged(nameof(ShowRetryConfirmedAccountDeletionCleanup));
        OnPropertyChanged(nameof(CanRunAccountDeletionRecoveryAction));
        OnPropertyChanged(nameof(ShowFullLocalResetAction));
        OnPropertyChanged(nameof(CanRequestFullLocalReset));
        OnPropertyChanged(nameof(AccountDeletionRecoveryTitle));
        OnPropertyChanged(nameof(AccountDeletionRecoveryMessage));
        OnPropertyChanged(nameof(AccountDeletionRecoveryContinueText));
        OnPropertyChanged(nameof(FormTitle));
        RetryConfirmedAccountDeletionCleanupCommand.NotifyCanExecuteChanged();
        RequestFullLocalResetCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyStageProperties();
        LoginWithDeviceCommand.NotifyCanExecuteChanged();
        CancelLoginWithDeviceCommand.NotifyCanExecuteChanged();
        ClearLoginWithDeviceQuarantineCommand.NotifyCanExecuteChanged();
        ContinueFromAccountDeletionRecoveryCommand.NotifyCanExecuteChanged();
        RetryConfirmedAccountDeletionCleanupCommand.NotifyCanExecuteChanged();
        RequestFullLocalResetCommand.NotifyCanExecuteChanged();
    }

    partial void OnStageChanged(LoginStage value)
    {
        if (value != LoginStage.Password && (_isPasswordHintRequestActive || _statusContainsPasswordHint))
            InvalidatePasswordHintRequest();
        InlineErrorTarget = null;
        IsUnlockPrompt = false;
        if (value == LoginStage.Account)
        {
            ApiKeySecret = string.Empty;
            ApiKeyClientId = string.Empty;
            IsApiKeyMode = false;
        }
        NotifyStageProperties();
        RequestFocus(CurrentStageFocusTarget());
        if (value is LoginStage.Unlock or LoginStage.PinUnlock or LoginStage.KeyConnectorUnlock
            or LoginStage.TrustedDeviceSso)
            _ = RefreshWindowsHelloAvailabilityAsync();
    }

    // 进入解锁阶段(主密码/PIN)时探测本机 Hello 可用性;结果就绪后再通知一次 ShowHelloUnlock
    // (fake 场景下 IsAvailableAsync 已完成的 Task 会同步跑完,真实 WinRT 调用则是真异步)。
    private async Task RefreshWindowsHelloAvailabilityAsync()
    {
        IsWindowsHelloAvailable = _hello is not null && await _hello.IsAvailableAsync();
        OnPropertyChanged(nameof(ShowHelloUnlock));
    }

    private void NotifyServerProperties()
    {
        OnPropertyChanged(nameof(ServerSummary));
        OnPropertyChanged(nameof(ShowCustomServerUrl));
        OnPropertyChanged(nameof(FormSubtitle));
    }

    private void NotifyStageProperties()
    {
        OnPropertyChanged(nameof(CanUseApiKeySignIn));
        OpenApiKeySignInCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowAccountContext));
        OnPropertyChanged(nameof(IsAccountStage));
        OnPropertyChanged(nameof(IsPasswordStage));
        OnPropertyChanged(nameof(IsLoginWithDeviceStage));
        OnPropertyChanged(nameof(IsDeviceVerificationStage));
        OnPropertyChanged(nameof(IsTwoFactorStage));
        OnPropertyChanged(nameof(IsPasswordChangeRequiredStage));
        OnPropertyChanged(nameof(IsUnlockStage));
        OnPropertyChanged(nameof(IsPinUnlockStage));
        OnPropertyChanged(nameof(IsKeyConnectorUnlockStage));
        OnPropertyChanged(nameof(IsTrustedDeviceSsoStage));
        OnPropertyChanged(nameof(IsTrustedDeviceApprovalStage));
        OnPropertyChanged(nameof(IsTdeV2JitProvisioningStage));
        OnPropertyChanged(nameof(IsAccountDeletionRecoveryStage));
        OnPropertyChanged(nameof(CanContinueFromAccountDeletionRecovery));
        OnPropertyChanged(nameof(ShowClearPendingAccountDeletion));
        OnPropertyChanged(nameof(ShowRetryConfirmedAccountDeletionCleanup));
        OnPropertyChanged(nameof(CanRunAccountDeletionRecoveryAction));
        OnPropertyChanged(nameof(ShowFullLocalResetAction));
        OnPropertyChanged(nameof(CanRequestFullLocalReset));
        OnPropertyChanged(nameof(ShowLoginWithDeviceQuarantineNotice));
        OnPropertyChanged(nameof(ShowLoginWithDeviceQuarantineClearAction));
        OnPropertyChanged(nameof(CanClearLoginWithDeviceQuarantine));
        OnPropertyChanged(nameof(ShowLoginWithDeviceOption));
        OnPropertyChanged(nameof(CanStartLoginWithDevice));
        OnPropertyChanged(nameof(ShowCancelLoginWithDevice));
        OnPropertyChanged(nameof(ShowPrimaryAction));
        OnPropertyChanged(nameof(ShowLoginWithDeviceTwoFactorExpiry));
        OnPropertyChanged(nameof(ShowUnlockPanel));
        OnPropertyChanged(nameof(ShowCancelDeviceApproval));
        OnPropertyChanged(nameof(ShowApprovalMethodPicker));
        OnPropertyChanged(nameof(ShowSavedAccounts));
        OnPropertyChanged(nameof(ShowHelloUnlock));
        OnPropertyChanged(nameof(IsPinUnlockAvailable));
        OnPropertyChanged(nameof(CanUsePinUnlock));
        UsePinUnlockCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditServer));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanResendNewDeviceCode));
        OnPropertyChanged(nameof(CanUsePrimaryAction));
        OnPropertyChanged(nameof(CanUseDemoVault));
        OnPropertyChanged(nameof(CanUseSso));
        OnPropertyChanged(nameof(CanRequestPasswordHint));
        OnPropertyChanged(nameof(PasswordHintButtonText));
        RequestPasswordHintCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ShowCustomServerUrl));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(FormSubtitle));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(RequiredPasswordPolicySummary));
        OnPropertyChanged(nameof(ShowResendEmailCode));
        OnPropertyChanged(nameof(CanResendEmailCode));
        OnPropertyChanged(nameof(ShowRememberDevice));
        OnPropertyChanged(nameof(ShowRecoveryCodeWarning));
        OnPropertyChanged(nameof(ShowRecoveryCodeSwitch));
        OnPropertyChanged(nameof(RecoveryCodeActionText));
        OnPropertyChanged(nameof(TwoFactorInputHeader));
        OnPropertyChanged(nameof(TwoFactorInputPlaceholder));
        OnPropertyChanged(nameof(IsWebAuthnMode));
        OnPropertyChanged(nameof(IsDuoMode));
        OnPropertyChanged(nameof(ShowTwoFactorCodeInput));
        ResendEmailCodeCommand.NotifyCanExecuteChanged();
        ResendNewDeviceCodeCommand.NotifyCanExecuteChanged();
        UseDemoVaultCommand.NotifyCanExecuteChanged();
        LoginWithDeviceCommand.NotifyCanExecuteChanged();
        CancelLoginWithDeviceCommand.NotifyCanExecuteChanged();
        ClearLoginWithDeviceQuarantineCommand.NotifyCanExecuteChanged();
        ContinueFromAccountDeletionRecoveryCommand.NotifyCanExecuteChanged();
        RetryConfirmedAccountDeletionCleanupCommand.NotifyCanExecuteChanged();
        RequestFullLocalResetCommand.NotifyCanExecuteChanged();
    }

    private void RefreshSavedAccounts()
    {
        SavedAccounts = _savedAccountService is null
            ? new()
            : new ObservableCollection<SavedAccountOption>(_savedAccountService.GetAccounts());
    }

    private void NotifyTwoFactorModeProperties()
    {
        OnPropertyChanged(nameof(ShowResendEmailCode));
        OnPropertyChanged(nameof(CanResendEmailCode));
        OnPropertyChanged(nameof(ShowTwoFactorProviderPicker));
        OnPropertyChanged(nameof(ShowRememberDevice));
        OnPropertyChanged(nameof(ShowRecoveryCodeWarning));
        OnPropertyChanged(nameof(ShowRecoveryCodeSwitch));
        OnPropertyChanged(nameof(RecoveryCodeActionText));
        OnPropertyChanged(nameof(TwoFactorInputHeader));
        OnPropertyChanged(nameof(TwoFactorInputPlaceholder));
        OnPropertyChanged(nameof(IsWebAuthnMode));
        OnPropertyChanged(nameof(IsDuoMode));
        OnPropertyChanged(nameof(ShowTwoFactorCodeInput));
        OnPropertyChanged(nameof(PrimaryButtonText));
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(FormSubtitle));
        ResendEmailCodeCommand.NotifyCanExecuteChanged();
    }
}

public sealed record TwoFactorProviderOption(int Type, string DisplayName);

public sealed record TrustedDeviceApprovalMethodOption(
    TrustedDeviceApprovalMethod Method,
    string DisplayName);
