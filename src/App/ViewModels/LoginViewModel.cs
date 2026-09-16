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
        IAppLifecycleCoordinator? lifecycle = null)
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
        AccountDeletionRecoveryNoticeKind.Prepared => "账户删除结果不确定",
        AccountDeletionRecoveryNoticeKind.ServerConfirmed => "账户已在服务器删除",
        AccountDeletionRecoveryNoticeKind.LocalCleanupComplete => "检测到已删除账户的本地残留",
        AccountDeletionRecoveryNoticeKind.UncertainLocalCleanupComplete =>
            "检测到删除结果不确定账户的本地残留",
        _ => "无法安全恢复保存账户",
    };

    public string AccountDeletionRecoveryMessage => AccountDeletionRecoveryNotice switch
    {
        AccountDeletionRecoveryNoticeKind.Prepared =>
            "此前的账户删除请求可能已到达服务器，并且仍可能稍后完成。旧令牌、离线、PIN 和 Windows Hello 解锁已禁用。你可以仅清除此设备上的全部账户数据，或改用其他账户。",
        AccountDeletionRecoveryNoticeKind.ServerConfirmed =>
            "服务器已确认删除账户，但此设备仍有数据尚未清理。请重试本地清理；旧令牌与离线解锁保持禁用。",
        AccountDeletionRecoveryNoticeKind.LocalCleanupComplete =>
            "此账户已完成本地清理，并由永久安全记录阻止再次使用，但检测到旧进程重新写入的保存会话。请再次清理本机残留数据，或使用其他账户。",
        AccountDeletionRecoveryNoticeKind.UncertainLocalCleanupComplete =>
            "此账户此前的删除结果不确定，本地数据已清理并保持安全隔离，但检测到旧进程重新写入的保存会话。请再次清理本机残留数据，或使用其他账户。",
        _ =>
            "账户删除恢复记录无法读取，因此所有账户登录和旧令牌解锁均已禁用。请先关闭其他应用实例并重新检查。若记录永久损坏，可以清除本应用的全部本地数据并退出；服务器数据不会受影响。若应用内重置不可用，请使用 Windows 应用重置。",
    };

    public string AccountDeletionRecoveryContinueText =>
        AccountDeletionRecoveryNotice == AccountDeletionRecoveryNoticeKind.LedgerUnavailable
            ? "重新检查安全存储"
            : "使用其他账户";

    public bool ShowLoginWithDeviceOption =>
        IsPasswordStage
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
            LoginWithDeviceQuarantineInfo.Found => "设备登录已隔离",
            LoginWithDeviceQuarantineInfo.Corrupt => "设备登录安全状态已损坏",
            _ => "设备登录安全状态不可用",
        };

    public string LoginWithDeviceQuarantineMessage =>
        _loginWithDeviceQuarantineInfo switch
        {
            LoginWithDeviceQuarantineInfo.Found { IsClearEligible: true } =>
                "一次设备批准的登录结果无法确认。请先检查并撤销其他设备中的可疑会话，再清除此隔离。",
            LoginWithDeviceQuarantineInfo.Found =>
                "一次设备批准的登录结果无法确认。隔离期结束前，请使用主密码登录，或返回并使用企业 SSO 完成身份确认。",
            LoginWithDeviceQuarantineInfo.Corrupt =>
                "无法验证本机保存的设备登录安全状态。为保护账户，设备登录已停用；请修复本机安全存储后重试。",
            _ =>
                "暂时无法验证本机设备登录安全状态。设备登录将在检查恢复前保持停用；你仍可使用主密码或企业 SSO。",
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

    public string AlternativeUnlockText => _usesTrustedDeviceSso
        ? "用企业 SSO 解锁"
        : _usesKeyConnector
            ? "用 Key Connector 解锁"
            : "用主密码解锁";

    public bool ShowResendEmailCode => IsTwoFactorStage
        && !IsRecoveryCodeMode
        && ResolveSelectedTwoFactorProvider() == EmailTwoFactorProvider;

    public bool CanResendEmailCode => ShowResendEmailCode && !IsBusy;

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

    public string RecoveryCodeActionText => IsRecoveryCodeMode ? "使用其他验证方式" : "使用恢复代码";

    public string TwoFactorInputHeader => ResolveSelectedTwoFactorProvider() switch
    {
        (int)TwoFactorProviderType.YubiKey => "YubiKey OTP",
        RecoveryCodeProvider => "恢复代码",
        _ => "验证码",
    };

    public string TwoFactorInputPlaceholder => ResolveSelectedTwoFactorProvider() switch
    {
        (int)TwoFactorProviderType.YubiKey => "轻触 YubiKey 后输入 OTP",
        RecoveryCodeProvider => "输入恢复代码",
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
        0 => "身份验证器 App",
        1 => "电子邮件",
        2 => "Duo",
        3 => "YubiKey OTP",
        6 => "组织 Duo",
        7 => "安全密钥 / Windows Hello",
        8 => "恢复代码",
        _ => $"其他方式 ({provider})",
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
                $"至少 {Math.Max(MasterPasswordPolicyRequirements.MinimumLength, policy?.MinLength ?? 0)} 个字符",
            };
            if (policy?.MinComplexity is > 0)
                requirements.Add($"强度等级至少 {policy.MinComplexity}/4");
            if (policy?.RequireUpper == true)
                requirements.Add("包含大写字母");
            if (policy?.RequireLower == true)
                requirements.Add("包含小写字母");
            if (policy?.RequireNumbers == true)
                requirements.Add("包含数字");
            if (policy?.RequireSpecial == true)
                requirements.Add("包含 !@#$%^&* 中的字符");
            return string.Join("；", requirements);
        }
    }

    public bool CanUsePrimaryAction => !IsBusy && ShowPrimaryAction;

    public bool CanUseSso => _ssoAuthorization is not null && IsAccountStage && !IsBusy;

    public bool ShowCancelSso => IsSsoPending;

    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);

    public bool ShowCustomServerUrl => IsAccountStage && SelectedServerOptionIndex == 2;

    public string PrimaryButtonText => IsBusy
        ? Stage switch
        {
            LoginStage.Password => "正在登录",
            LoginStage.LoginWithDevice => "正在等待设备批准",
            LoginStage.DeviceVerification => "正在验证设备",
            LoginStage.PasswordChangeRequired => "正在更新主密码",
            LoginStage.TwoFactor => IsRecoveryCodeMode
                ? "正在恢复"
                : IsWebAuthnMode
                    ? "正在等待安全密钥"
                    : IsDuoMode ? "正在等待 Duo" : "正在验证",
            LoginStage.Unlock => "正在解锁",
            LoginStage.PinUnlock => "正在解锁",
            LoginStage.KeyConnectorUnlock => "正在连接 Key Connector",
            LoginStage.TrustedDeviceSso => "正在完成企业 SSO",
            LoginStage.TrustedDeviceApproval => "正在等待设备批准",
            LoginStage.TdeV2JitProvisioning => "正在初始化账户加密",
            LoginStage.AccountDeletionRecovery => "正在清理本地账户数据",
            _ => "请稍候",
        }
        : Stage switch
        {
            LoginStage.Account => "继续",
            LoginStage.LoginWithDevice => "等待设备批准",
            LoginStage.DeviceVerification => "验证设备",
            LoginStage.PasswordChangeRequired => "更新主密码",
            LoginStage.TwoFactor => IsRecoveryCodeMode
                ? "恢复并登录"
                : IsWebAuthnMode
                    ? "使用安全密钥"
                    : IsDuoMode ? "打开 Duo" : "验证",
            LoginStage.Unlock => "解锁",
            LoginStage.PinUnlock => "解锁",
            LoginStage.KeyConnectorUnlock => "通过 Key Connector 解锁",
            LoginStage.TrustedDeviceSso => "使用企业 SSO 解锁",
            LoginStage.TrustedDeviceApproval => "请求设备批准",
            LoginStage.TdeV2JitProvisioning => "完成账户初始化",
            LoginStage.AccountDeletionRecovery => "账户删除恢复",
            _ => "登录",
        };

    public string FormTitle => Stage switch
    {
        LoginStage.Password => "输入主密码",
        LoginStage.LoginWithDevice => "使用设备登录",
        LoginStage.DeviceVerification => "验证新设备",
        LoginStage.PasswordChangeRequired => "必须更新主密码",
        LoginStage.TwoFactor => IsRecoveryCodeMode
            ? "使用恢复代码"
            : IsWebAuthnMode
                ? "安全密钥验证"
                : IsDuoMode ? "Duo 两步验证" : "两步验证",
        LoginStage.Unlock => "解锁保险库",
        LoginStage.PinUnlock => "用 PIN 解锁",
        LoginStage.KeyConnectorUnlock => "通过 Key Connector 解锁",
        LoginStage.TrustedDeviceSso => "可信设备登录",
        LoginStage.TrustedDeviceApproval => "批准此设备",
        LoginStage.TdeV2JitProvisioning => "设置账户加密",
        LoginStage.AccountDeletionRecovery => AccountDeletionRecoveryTitle,
        _ => "登录",
    };

    public string FormSubtitle => Stage switch
    {
        LoginStage.Password => "输入此账户的主密码以继续。",
        LoginStage.LoginWithDevice => "在已登录的设备上核对指纹并批准请求。",
        LoginStage.DeviceVerification => "验证此设备后继续登录。",
        LoginStage.PasswordChangeRequired => "满足账户要求后继续使用保险库。",
        LoginStage.TwoFactor => IsRecoveryCodeMode
            ? "使用账户的恢复代码完成身份验证。"
            : "完成此账户要求的附加验证。",
        LoginStage.Unlock => "解锁此设备上已保存的保险库。",
        LoginStage.PinUnlock => "输入此设备的 PIN。",
        LoginStage.KeyConnectorUnlock => "连接组织的 Key Connector 以解锁。",
        LoginStage.TrustedDeviceSso => "重新完成企业身份验证以解锁。",
        LoginStage.TrustedDeviceApproval => "核对指纹短语后批准此设备。",
        LoginStage.TdeV2JitProvisioning => "完成企业账户的首次加密设置。",
        LoginStage.AccountDeletionRecovery => "先确认删除状态并完成本地恢复。",
        _ => "连接到你的 Bitwarden / Vaultwarden 保险库",
    };

    public string StepText => Stage switch
    {
        LoginStage.Password => "主密码",
        LoginStage.LoginWithDevice => "在另一台设备上批准",
        LoginStage.DeviceVerification => "安全检查",
        LoginStage.PasswordChangeRequired => "账户恢复",
        LoginStage.TwoFactor => "附加验证",
        LoginStage.Unlock => "已锁定",
        LoginStage.PinUnlock => "已锁定",
        LoginStage.KeyConnectorUnlock => "已锁定",
        LoginStage.TrustedDeviceSso => "需要重新认证",
        LoginStage.TrustedDeviceApproval => "需要另一台设备",
        LoginStage.TdeV2JitProvisioning => "首次企业登录",
        LoginStage.AccountDeletionRecovery => "安全恢复",
        _ => "账户信息",
    };

    public bool ShowAccountContext => !IsAccountStage && !string.IsNullOrWhiteSpace(Email);

    public string AccountSummary => string.IsNullOrWhiteSpace(Email) ? "邮箱地址" : Email.Trim();

    public string ServerSummary => FormatServerSummary(ResolveServerUrl());

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
                    ? "安全存储仍不可用，所有账户登录保持禁用。"
                    : IsAccountDeletionRecoveryStage
                        ? "安全存储检查已恢复，请继续处理账户删除记录。"
                        : "安全存储检查已恢复。",
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
            "请输入其他账户的邮箱地址。待恢复账户仍不可登录。",
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
                    "重置请求结果无法确认，应用将安全退出，下次启动会重新检查。",
                    LoginStatusKind.Error);
                await ExitAfterLocalDataResetRequestAsync(alwaysFailClosed: true);
                return;
            }

            if (result.Status is LocalDataResetRequestStatus.Requested
                or LocalDataResetRequestStatus.AlreadyPending)
            {
                SetStatus("本地数据重置请求已记录，正在安全退出。", LoginStatusKind.Success);
            }
            else
            {
                SetStatus(
                    "重置请求结果无法确认，应用将安全退出，下次启动会重新检查。",
                    LoginStatusKind.Info);
            }
            await ExitAfterLocalDataResetRequestAsync(alwaysFailClosed: false);
        }
        catch (OperationCanceledException) when (!resetAttempted && ct.IsCancellationRequested)
        {
            SetStatus("本地数据重置已取消。", LoginStatusKind.Info);
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
                "重置请求结果无法确认，自动退出失败。请立即关闭应用；下次启动会重新检查。",
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
                    "本机数据已清理，该账户在此安装中保持安全隔离。你可以使用其他账户。",
                    LoginStatusKind.Success);
                return;
            }
            if (result.Status == AccountDeletionLocalResolutionStatus.NotFound)
            {
                FinishAccountDeletionRecoveryAction(
                    "账户删除恢复记录已变化，未执行本地清理。请重新检查后再选择账户。",
                    LoginStatusKind.Info,
                    "账户删除恢复记录已变化，请处理当前恢复记录。");
                return;
            }

            RetainAccountDeletionRecovery(result.Pending);
            SetStatus(
                result.Status == AccountDeletionLocalResolutionStatus.CancelledBeforeCleanup
                    ? "本地清理已取消。"
                    : "未能完整清除此设备上的账户数据，请重试。",
                result.Status == AccountDeletionLocalResolutionStatus.CancelledBeforeCleanup
                    ? LoginStatusKind.Info
                    : LoginStatusKind.Error);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            if (IsCurrentAccountDeletionRecoveryOperation(generation, expected))
                SetStatus("本地清理已取消。", LoginStatusKind.Info);
        }
        catch
        {
            if (IsCurrentAccountDeletionRecoveryOperation(generation, expected))
            {
                SetStatus(
                    "未能完整清除此设备上的账户数据，请重试。",
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
                    "本机数据已清理，该账户在此安装中保持安全隔离。你可以使用其他账户。",
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
                "本地清理仍未完成，请检查本机安全存储后重试。",
                LoginStatusKind.Error);
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            if (IsCurrentAccountDeletionRecoveryOperation(generation, expected))
                SetStatus("本地清理已取消。", LoginStatusKind.Info);
        }
        catch
        {
            if (IsCurrentAccountDeletionRecoveryOperation(generation, expected))
            {
                SetStatus(
                    "本地清理仍未完成，请检查本机安全存储后重试。",
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
        string remainingRecoveryMessage = "已完成一项本地恢复，请继续处理其余账户记录。")
    {
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
        Stage = (_pin?.IsPinSet ?? false)
            ? LoginStage.PinUnlock
            : ResolveNonPinUnlockStage();
        SetStatus(
            IsPinUnlockStage
                ? "请输入 PIN 解锁。"
                : IsKeyConnectorUnlockStage
                    ? "连接组织的 Key Connector 以解锁保险库。"
                    : IsTrustedDeviceSsoStage
                        ? "请重新完成企业 SSO，以确认当前设备的信任状态。"
                    : "请输入主密码解锁。",
            LoginStatusKind.Info);
    }

    public void PrepareAccount(string serverUrl, string email)
    {
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
            SetStatus("认证会话已失效，请重试。", LoginStatusKind.Error);
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

                    SetStatus("请按系统提示使用安全密钥或 Windows Hello。", LoginStatusKind.Info);
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

                    SetStatus("请在 Duo 窗口中完成验证。", LoginStatusKind.Info);
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
                        : await _auth.LoginAsync(attempt, ResolveServerUrl(), Email.Trim(), MasterPassword, ct);

            await HandleResultAsync(attempt, result);
        }
        catch (WebAuthnTwoFactorCancelledException)
        {
            SetStatus("安全密钥验证已取消，可重新尝试或选择其他方式。", LoginStatusKind.Info);
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
            SetStatus("Duo 验证已取消，可重新尝试或选择其他方式。", LoginStatusKind.Info);
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
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoginWithSsoAsync(CancellationToken ct)
    {
        if (IsBusy || _ssoAuthorization is null)
            return;

        ClearStatus();
        var serverUrl = ResolveServerUrl();
        if (!IsValidServerUrl(serverUrl))
        {
            SetStatus("请输入有效的服务器地址。", LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.ServerUrl);
            return;
        }
        if (string.IsNullOrWhiteSpace(Email))
        {
            SetStatus("请输入邮箱地址。", LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.Email);
            return;
        }

        ServerUrl = serverUrl;
        Email = Email.Trim();
        var attempt = BeginAuthenticationAttempt();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ssoCancellation = cancellation;
        try
        {
            IsBusy = true;
            IsSsoPending = true;
            SetStatus("请在浏览器中完成企业 SSO 登录。", LoginStatusKind.Info);

            var endpoints = ServerEndpoints.FromWebVault(serverUrl);
            var authorization = await _ssoAuthorization.AuthorizeAsync(
                endpoints,
                Email,
                cancellation.Token);
            SetStatus("正在完成 SSO 登录…", LoginStatusKind.Info);
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
            IsBusy = false;
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
        SetStatus("正在创建设备登录请求。", LoginStatusKind.Info);

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
                "请在另一台已登录设备上核对指纹短语并批准此请求。",
                LoginStatusKind.Info);

            while (IsCurrentLoginWithDeviceOperation(generation, cancellation, attempt))
            {
                if (LoginWithDeviceExpiresAtUtc is not { } expiresAt
                    || expiresAt <= DateTimeOffset.UtcNow)
                {
                    CompleteLoginWithDeviceTerminal(
                        LoginWithDeviceTerminalKind.Expired,
                        "设备登录请求已过期。请重新发起请求或使用主密码登录。",
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
                            "另一台设备拒绝了登录请求。请重新发起请求或使用主密码登录。",
                            LoginStatusKind.Error,
                            cancelService: false);
                        return;
                    }

                    if (pending.Status == LoginWithDeviceUiStatus.Expired)
                    {
                        CompleteLoginWithDeviceTerminal(
                            LoginWithDeviceTerminalKind.Expired,
                            "设备登录请求已过期。请重新发起请求或使用主密码登录。",
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
                        "请在另一台已登录设备上核对指纹短语并批准此请求。",
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
                        "暂时无法检查设备批准状态，正在重试。",
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
                    "设备登录已取消。你仍可使用主密码登录。",
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
        "设备登录已取消。你仍可使用主密码登录。",
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
                "设备登录请求已过期。请重新发起请求或使用主密码登录。",
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
                ? "设备登录请求可能已经发送，但未收到服务器响应。请稍后重新发起，并只批准新页面显示的指纹短语。"
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
            "设备批准后的登录结果无法确认。请使用主密码重新登录以确认账户状态，不要重试设备登录。",
            LoginStatusKind.Error,
            cancelService: false);
    }

    private string LoginWithDeviceFailureMessage(AuthResult.Failure failure) =>
        failure.Kind switch
        {
            AuthFailureKind.InvalidCredentials =>
                "设备登录授权无效或已过期。请重新发起请求。",
            AuthFailureKind.SafeMessage =>
                "当前设备无法使用设备登录。请使用主密码登录。",
            AuthFailureKind.Unspecified =>
                "设备登录失败，请稍后重试。",
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
                    message = "设备批准可能与取消同时发生，登录结果无法确认。请使用主密码重新登录以确认账户状态，不要重试设备登录。";
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
                    "设备登录隔离已清除，正在重新检查此设备。",
                LoginWithDeviceQuarantineClearStatus.AlreadyAbsent =>
                    "设备登录隔离已不存在，正在重新检查此设备。",
                LoginWithDeviceQuarantineClearStatus.NotEligible =>
                    "隔离期尚未结束。请使用主密码或企业 SSO 完成身份确认。",
                LoginWithDeviceQuarantineClearStatus.Changed =>
                    "设备登录隔离状态已变化，已重新读取最新状态。",
                LoginWithDeviceQuarantineClearStatus.Corrupt =>
                    "本机设备登录安全状态已损坏，无法清除隔离。",
                _ => "暂时无法清除设备登录隔离。设备登录仍保持停用。",
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
                "设备批准服务不可用，请重新完成企业 SSO。",
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
                    ? "请等待组织管理员核对指纹并批准此请求。"
                    : "请在另一台已解锁设备上核对指纹并批准此请求。",
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
                        "另一台设备拒绝了此请求。",
                        LoginStatusKind.Error);
                    return;
                }
                if (poll.Status == TrustedDeviceApprovalStatus.Expired)
                {
                    ReturnFromTrustedDeviceApproval(
                        "设备批准请求已过期，请重新完成企业 SSO。",
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
                "设备批准已取消。",
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
                "无法连接服务器，请检查网络后重试。",
                LoginStatusKind.Error);
        }
        catch (TrustedDeviceApprovalException)
        {
            service.Cancel(lease);
            ReturnFromTrustedDeviceApproval(
                "设备批准响应无效，请重新完成企业 SSO。",
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
            SetStatus("账户初始化会话已失效，请重新完成企业 SSO。", LoginStatusKind.Error);
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
                    ? "账户密钥可能已部分提交。请重新登录核对状态，不要直接重复初始化。"
                    : FailureMessage(failure, wasTwoFactor: false)
                : "账户初始化会话已失效，请重新完成企业 SSO。";
            ClearTdeV2JitProvisioning();
            Stage = LoginStage.TrustedDeviceSso;
            SetStatus(message, LoginStatusKind.Error);
        }
        catch (OperationCanceledException ex) when (
            UiExceptionPolicy.IsGenuineCancellation(ex, ct)
            || lease.CancellationToken.IsCancellationRequested)
        {
            SetStatus("账户初始化已取消。", LoginStatusKind.Info);
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
                SetStatus("Windows Hello 未完成。", LoginStatusKind.Info);
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
            SetStatus("强制改密会话已失效，请重新登录。", LoginStatusKind.Error);
            return;
        }

        if (NewMasterPassword.Length is < MasterPasswordPolicyRequirements.MinimumLength
                or > MasterPasswordPolicyRequirements.MaximumLength)
        {
            SetStatus(
                $"新主密码长度必须为 {MasterPasswordPolicyRequirements.MinimumLength} 到 "
                + $"{MasterPasswordPolicyRequirements.MaximumLength} 个字符。",
                LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.NewMasterPassword);
            return;
        }
        if (!string.Equals(NewMasterPassword, ConfirmNewMasterPassword, StringComparison.Ordinal))
        {
            SetStatus("两次输入的新主密码不一致。", LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.NewMasterPassword);
            return;
        }
        if (MasterPasswordHint.Length > 50
            || string.Equals(MasterPasswordHint.Trim(), NewMasterPassword, StringComparison.Ordinal))
        {
            SetStatus("密码提示不能等于主密码，且不能超过 50 个字符。", LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.NewMasterPassword);
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
                    SetStatus("主密码已更新，请使用新主密码登录。", LoginStatusKind.Success);
                    RequestFocus(LoginFocusTarget.MasterPassword);
                    break;
                case AuthResult.PasswordChangeUncertain:
                    ClearRequiredPasswordChange();
                    RefreshSavedAccounts();
                    Stage = LoginStage.Password;
                    SetStatus(
                        "服务器可能已经完成改密。请先使用新主密码登录确认。",
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
                    SetStatus("强制改密会话已失效，请重新登录。", LoginStatusKind.Error);
                    RequestFocus(LoginFocusTarget.MasterPassword);
                    break;
            }
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(ex, ct))
        {
            SetStatus("改密已取消，可重新提交。", LoginStatusKind.Info);
        }
        catch (HttpRequestException)
        {
            ClearRequiredPasswordChange();
            Stage = LoginStage.Password;
            SetStatus(
                "改密结果无法确认，请使用新主密码重新登录。",
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
            SetStatus("设备批准已取消。", LoginStatusKind.Info);
        }
    }

    [RelayCommand(CanExecute = nameof(CanResendEmailCode))]
    private async Task ResendEmailCodeAsync()
    {
        var attempt = _authenticationAttempt;
        var sent = attempt is not null
            && await _auth.ResendTwoFactorEmailAsync(attempt);
        SetStatus(
            sent ? "验证码已重新发送，请查收邮箱。" : "验证码发送失败，请稍后重试。",
            sent ? LoginStatusKind.Success : LoginStatusKind.Error);
    }

    [RelayCommand(CanExecute = nameof(CanResendNewDeviceCode))]
    private async Task ResendNewDeviceCodeAsync()
    {
        var attempt = _authenticationAttempt;
        var sent = attempt is not null
            && await _auth.ResendNewDeviceVerificationAsync(attempt);
        SetStatus(
            sent ? "新的设备验证码已发送，请查收邮箱。" : "设备验证码发送失败，请稍后重试。",
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
                ? "恢复代码验证成功后，账户的所有两步验证方式都会被停用。"
                : IsWebAuthnMode
                    ? "请使用安全密钥或 Windows Hello 完成验证。"
                    : IsDuoMode ? "请打开 Duo 窗口完成验证。" : "请输入两步验证码。",
            LoginStatusKind.Info);
        RequestFocus(CurrentStageFocusTarget());
        NotifyTwoFactorModeProperties();
    }

    [RelayCommand]
    private void UseMasterPassword()
    {
        Stage = ResolveNonPinUnlockStage();
        Pin = string.Empty;
        SetStatus(
            IsKeyConnectorUnlockStage
                ? "连接组织的 Key Connector 以解锁保险库。"
                : IsTrustedDeviceSsoStage
                    ? "请重新完成企业 SSO，以确认当前设备的信任状态。"
                : "请输入主密码解锁。",
            LoginStatusKind.Info);
    }

    [RelayCommand]
    private async Task SwitchAccountAsync()
    {
        if (IsBusy)
            return;

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
                SetStatus("无法安全移除当前企业登录会话，请重试。", LoginStatusKind.Error);
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
        if (account is null || _savedAccountService?.TryActivate(account) != true)
        {
            SetStatus("无法读取所选账户，请重新登录。", LoginStatusKind.Error);
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
            SetStatus("无法安全读取所选账户，请重新登录。", LoginStatusKind.Error);
            return;
        }

        RefreshSavedAccounts();
        PreparePersistedSession(activated);
    }

    // 锁屏/登出时清空登录页所有机密输入:主密码、PIN、2FA 验证码与其可选项。幂等。
    public void ClearSensitive()
    {
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

        var serverUrl = ResolveServerUrl();
        if (!IsValidServerUrl(serverUrl))
        {
            SetStatus("请输入有效的服务器地址。", LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.ServerUrl);
            return;
        }

        if (string.IsNullOrWhiteSpace(Email))
        {
            SetStatus("请输入邮箱地址。", LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.Email);
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

        if ((IsPasswordStage || IsUnlockStage) && string.IsNullOrWhiteSpace(MasterPassword))
        {
            SetStatus("请输入主密码。", LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.MasterPassword);
            return false;
        }

        if (IsPinUnlockStage && string.IsNullOrWhiteSpace(Pin))
        {
            SetStatus("请输入 PIN。", LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.Pin);
            return false;
        }

        if (IsDeviceVerificationStage && string.IsNullOrWhiteSpace(DeviceVerificationCode))
        {
            SetStatus("请输入设备验证码。", LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.DeviceVerificationCode);
            return false;
        }

        if (ShowTwoFactorCodeInput && string.IsNullOrWhiteSpace(TwoFactorCode))
        {
            SetStatus("请输入两步验证码。", LoginStatusKind.Error);
            RequestFocus(LoginFocusTarget.TwoFactorCode);
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
        LoginStage.Password => LoginFocusTarget.MasterPassword,
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
            "无法打开保险库。会话已重新锁定，请再次解锁。",
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
                SetStatus("SSO 登录成功，请输入主密码解锁保险库。", LoginStatusKind.Success);
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
                        ? "请选择由其他已登录设备或组织管理员批准。"
                        : _approvalMethod == TrustedDeviceApprovalMethod.Administrator
                            ? "将向组织管理员发起设备批准请求。"
                            : "请由另一台已解锁设备批准此设备。",
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
                        ? "确认后将为此企业账户创建加密密钥并注册账户恢复。"
                        : "检测到未完成的账户初始化，将安全继续原有流程。",
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
                        ? "组织管理员设置了临时密码。继续使用前必须创建新的主密码。"
                        : "当前主密码不符合组织策略。继续使用前必须更新主密码。",
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
                            ? "服务器启用了已淘汰 Duo iframe，请管理员关闭 DUO_USE_IFRAME 或使用恢复码。"
                            : "当前两步验证方式暂不支持，可使用恢复代码继续。",
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
                        ? "请使用安全密钥或 Windows Hello 完成验证。"
                        : IsDuoMode ? "请打开 Duo 窗口完成验证。" : "请输入两步验证码。",
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
                    "这是新的登录设备。请输入发送到邮箱的一次性验证码。",
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
                ? "此设备已获批准，但未能安全建立设备信任。"
                : FailureMessage(failure, wasTwoFactor: false)
            : "设备批准会话已失效，请重新完成企业 SSO。";
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
                "检测到账户删除恢复记录，旧令牌和离线解锁已禁用。请先完成恢复。",
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

        SetStatus(message, LoginStatusKind.Error);
        RequestFocus(CurrentSecretFocusTarget());
    }

    private string FailureMessage(AuthResult.Failure failure, bool wasTwoFactor) =>
        failure.Kind switch
        {
            AuthFailureKind.RateLimited =>
                "登录请求过于频繁，请稍后重试。",
            AuthFailureKind.InvalidCredentials when wasTwoFactor && IsRecoveryCodeMode =>
                "恢复代码无效，请重试。",
            AuthFailureKind.InvalidCredentials when wasTwoFactor =>
                "验证码无效或已过期，请重试。",
            AuthFailureKind.InvalidCredentials when IsUnlockStage =>
                "主密码错误，请重试。",
            AuthFailureKind.InvalidCredentials =>
                "用户名或主密码错误，请重试。",
            AuthFailureKind.Network =>
                "无法连接服务器，请检查网络后重试。",
            AuthFailureKind.Timeout =>
                "连接服务器超时，请稍后重试。",
            AuthFailureKind.InvalidResponse =>
                "服务器响应无效，无法完成认证。",
            AuthFailureKind.SessionExpired =>
                "认证会话已失效，请重试。",
            AuthFailureKind.NoSavedSession =>
                "没有已保存的会话，请重新登录。",
            AuthFailureKind.UnsupportedSsoDecryption =>
                "服务器要求当前客户端尚未支持的 SSO 解密格式或审批方式。",
            AuthFailureKind.SsoAuthorization =>
                "无法启动 SSO 浏览器登录，请检查系统浏览器与本机端口后重试。",
            AuthFailureKind.AccountDeletionRecoveryRequired =>
                "检测到账户删除恢复记录，旧令牌和离线解锁已禁用。请先完成恢复。",
            AuthFailureKind.SafeMessage when !string.IsNullOrWhiteSpace(failure.Message) =>
                failure.Message,
            _ => LocalizeLegacyAuthMessage(failure.Message),
        };

    private string LocalizeLegacyAuthMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return GenericFailureMessage();

        if (message.Contains("Username or password is incorrect", StringComparison.OrdinalIgnoreCase))
            return "用户名或主密码错误，请重试。";

        return message switch
        {
            "No saved session." => "没有已保存的会话，请重新登录。",
            "Authentication failed." => "认证失败，请检查凭据。",
            "Two-factor authentication is still required." => "仍需完成两步验证。",
            "No two-factor challenge is pending." => "没有待处理的两步验证请求。",
            "Token response did not include a protected user key." => "服务器响应缺少用户密钥，登录失败。",
            _ => GenericFailureMessage(),
        };
    }

    private string GenericFailureMessage() => Stage switch
    {
        LoginStage.LoginWithDevice => "设备登录失败，请稍后重试。",
        LoginStage.DeviceVerification => "设备验证失败，请稍后重试。",
        LoginStage.PasswordChangeRequired => "主密码更新失败，请稍后重试。",
        LoginStage.TwoFactor => "验证失败，请稍后重试。",
        LoginStage.Unlock or LoginStage.PinUnlock or LoginStage.KeyConnectorUnlock
            or LoginStage.TrustedDeviceSso or LoginStage.TrustedDeviceApproval =>
            "解锁失败，请稍后重试。",
        _ => "登录失败，请稍后重试。",
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

    private void SetStatus(string message, LoginStatusKind kind)
    {
        StatusKind = kind;
        Status = message;
    }

    private void ClearStatus()
    {
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

    partial void OnServerUrlChanged(string value) => NotifyServerProperties();

    partial void OnEmailChanged(string value)
    {
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
                "其他已登录设备"));
        }
        if (approval.HasAdminApproval)
        {
            options.Add(new TrustedDeviceApprovalMethodOption(
                TrustedDeviceApprovalMethod.Administrator,
                "组织管理员"));
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
        OnPropertyChanged(nameof(CanEditServer));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanResendNewDeviceCode));
        OnPropertyChanged(nameof(CanUsePrimaryAction));
        OnPropertyChanged(nameof(CanUseDemoVault));
        OnPropertyChanged(nameof(CanUseSso));
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
        ResendEmailCodeCommand.NotifyCanExecuteChanged();
    }
}

public sealed record TwoFactorProviderOption(int Type, string DisplayName);

public sealed record TrustedDeviceApprovalMethodOption(
    TrustedDeviceApprovalMethod Method,
    string DisplayName);
