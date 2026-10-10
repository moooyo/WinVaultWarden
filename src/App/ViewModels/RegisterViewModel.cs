using Api;
using App.Services;
using Core.Models;
using Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace App.ViewModels;

public enum RegisterFocusTarget
{
    ServerUrl,
    Email,
    Name,
    MasterPassword,
    ConfirmPassword,
}

public enum RegisterState
{
    Editing,
    AwaitingEmailVerification,
    OutcomeUncertain,
    Registered,
}

public partial class RegisterViewModel : ObservableObject, ISensitiveState
{
    private const string BitwardenUsUrl = "https://vault.bitwarden.com";
    private const string BitwardenEuUrl = "https://vault.bitwarden.eu";

    private readonly IRegisterUiService? _registerUi;
    private Func<string, CancellationToken, Task<int>>? _passwordBreachCheck;
    private readonly object _registrationStateLock = new();
    private CancellationTokenSource? _activeRegistrationCancellation;
    private long _registrationGeneration;

    // ── Observable 属性 ────────────────────────────────────────────────────

    [ObservableProperty]
    public partial int SelectedServerOptionIndex { get; set; }

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = BitwardenUsUrl;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Name { get; set; }

    [ObservableProperty]
    public partial string MasterPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? MasterPasswordHint { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool CheckPasswordBreaches { get; set; }

    // 页面据 FocusRequestId 单调递增触发一次编程式焦点。
    [ObservableProperty]
    public partial RegisterFocusTarget FocusTarget { get; set; }

    [ObservableProperty]
    public partial long FocusRequestId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationError))]
    public partial string OperationError { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Registered))]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(IsAwaitingEmailVerification))]
    [NotifyPropertyChangedFor(nameof(IsOutcomeUncertain))]
    [NotifyPropertyChangedFor(nameof(CanEditForm))]
    public partial RegisterState State { get; set; } = RegisterState.Editing;

    [ObservableProperty]
    public partial string RegisteredEmail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RegisteredServerUrl { get; set; } = string.Empty;

    // ── 计算属性 ──────────────────────────────────────────────────────────

    public bool HasOperationError => !string.IsNullOrEmpty(OperationError);

    public bool Registered => State == RegisterState.Registered;

    public bool IsEditing => State == RegisterState.Editing;

    public bool IsAwaitingEmailVerification =>
        State == RegisterState.AwaitingEmailVerification;

    public bool IsOutcomeUncertain => State == RegisterState.OutcomeUncertain;

    // 注册进行中禁用整个表单(含返回登录):由 OnIsBusyChanged 通知刷新。
    public bool CanEditForm => IsEditing && !IsBusy;

    public bool ShowCustomServerUrl => SelectedServerOptionIndex == 2;

    // ── 构造器 ────────────────────────────────────────────────────────────

    // 无参构造器：供 XAML 设计时及无注入场景使用
    public RegisterViewModel()
    {
    }

    public RegisterViewModel(IRegisterUiService registerUi)
    {
        _registerUi = registerUi;
    }

    public void ConfigurePasswordBreachCheck(Func<string, CancellationToken, Task<int>> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        lock (_registrationStateLock)
        {
            if (IsBusy)
                throw new InvalidOperationException("Cannot change password checks during registration.");
            _passwordBreachCheck = check;
            CheckPasswordBreaches = true;
        }
    }

    // 锁屏/登出时清空注册页机密输入:主密码、确认密码与提示。幂等。
    public void ClearSensitive()
    {
        CancellationTokenSource? active;
        lock (_registrationStateLock)
        {
            active = DetachActiveRegistration();
            ClearSecretInputs();
        }
        CancelDetached(active);
    }

    public void CancelPendingRegistration()
    {
        CancellationTokenSource? active;
        lock (_registrationStateLock)
            active = DetachActiveRegistration();
        CancelDetached(active);
    }

    public void ResumeEditing()
    {
        CancellationTokenSource? active;
        lock (_registrationStateLock)
        {
            active = DetachActiveRegistration();
            ClearSecretInputs();
            RegisteredEmail = string.Empty;
            RegisteredServerUrl = string.Empty;
            OperationError = string.Empty;
            State = RegisterState.Editing;
            RequestFocus(RegisterFocusTarget.Email);
        }
        CancelDetached(active);
    }

    private CancellationTokenSource? DetachActiveRegistration()
    {
        _registrationGeneration++;
        var active = _activeRegistrationCancellation;
        _activeRegistrationCancellation = null;
        IsBusy = false;
        return active;
    }

    private static void CancelDetached(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owning operation reached its terminal finally block first.
        }
    }

    private void ClearSecretInputs()
    {
        MasterPassword = string.Empty;
        ConfirmPassword = string.Empty;
        MasterPasswordHint = null;
    }

    // ── 注册操作 ──────────────────────────────────────────────────────────

    public async Task RegisterAsync(CancellationToken ct = default)
    {
        if (_registerUi is null)
        {
            OperationError = "注册服务不可用";
            return;
        }

        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? previousCancellation;
        long operationGeneration;
        string serverUrl;
        string email;
        string? name;
        string password;
        string confirmPassword;
        string? passwordHint;
        bool checkPasswordBreaches;
        Func<string, CancellationToken, Task<int>>? passwordBreachCheck;
        lock (_registrationStateLock)
        {
            if (IsBusy || !IsEditing)
            {
                operationCancellation.Dispose();
                return;
            }

            if (!ValidateInput())
            {
                operationCancellation.Dispose();
                return;
            }

            operationGeneration = ++_registrationGeneration;
            previousCancellation = _activeRegistrationCancellation;
            _activeRegistrationCancellation = operationCancellation;
            IsBusy = true;
            OperationError = string.Empty;
            serverUrl = ResolveServerUrl();
            email = Email.Trim();
            name = Name;
            password = MasterPassword;
            confirmPassword = ConfirmPassword;
            passwordHint = MasterPasswordHint;
            checkPasswordBreaches = CheckPasswordBreaches;
            passwordBreachCheck = _passwordBreachCheck;
        }
        CancelDetached(previousCancellation);

        try
        {
            if (checkPasswordBreaches)
            {
                try
                {
                    if (passwordBreachCheck is null)
                        throw new InvalidOperationException("The password breach checker is unavailable.");

                    var breaches = await passwordBreachCheck(password, operationCancellation.Token);
                    if (breaches < 0)
                        throw new InvalidDataException("The password breach checker returned an invalid count.");
                    if (breaches > 0)
                    {
                        TryUpdateCurrentOperation(operationGeneration, operationCancellation, allowCancellation: false, () =>
                        {
                            OperationError = "这个密码出现在已知数据泄露中，换一个吧";
                            RequestFocus(RegisterFocusTarget.MasterPassword);
                        });
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or InvalidOperationException)
                {
                    TryUpdateCurrentOperation(operationGeneration, operationCancellation, allowCancellation: false, () =>
                    {
                        OperationError = "无法完成密码泄露检查。请重试，或取消勾选后创建账户。";
                        RequestFocus(RegisterFocusTarget.MasterPassword);
                    });
                    return;
                }
            }

            lock (_registrationStateLock)
            {
                if (!IsCurrentOperation(operationGeneration, operationCancellation, allowCancellation: false))
                    return;
            }
            var outcome = await _registerUi.RegisterAsync(
                serverUrl,
                email,
                name,
                password,
                confirmPassword,
                passwordHint,
                operationCancellation.Token);

            var state = outcome switch
            {
                RegistrationOutcome.Registered => RegisterState.Registered,
                RegistrationOutcome.EmailVerificationRequired =>
                    RegisterState.AwaitingEmailVerification,
                RegistrationOutcome.OutcomeUncertain =>
                    RegisterState.OutcomeUncertain,
                _ => throw new InvalidOperationException(
                    "The registration service returned an unknown outcome."),
            };
            if (!TryCommitOutcome(
                    operationGeneration,
                    operationCancellation,
                    serverUrl,
                    email,
                    state))
            {
                return;
            }
        }
        catch (OperationCanceledException ex)
            when (UiExceptionPolicy.IsGenuineCancellation(
                ex,
                operationCancellation.Token))
        {
            TryUpdateCurrentOperation(
                operationGeneration,
                operationCancellation,
                allowCancellation: true,
                () => OperationError = string.Empty);
        }
        catch (Exception ex) when (RequestTimeoutClassifier.IsTimeout(ex))
        {
            TryUpdateCurrentOperation(
                operationGeneration,
                operationCancellation,
                allowCancellation: false,
                () =>
                {
                    OperationError = UiExceptionPolicy.RequestTimeoutMessage;
                    RequestFocus(RegisterFocusTarget.Email);
                });
        }
        catch (RegistrationException ex)
        {
            TryUpdateCurrentOperation(
                operationGeneration,
                operationCancellation,
                allowCancellation: false,
                () =>
                {
                    OperationError = ex.Message;
                    RequestFocus(RegisterFocusTarget.Email);
                });
        }
        finally
        {
            lock (_registrationStateLock)
            {
                if (ReferenceEquals(
                        _activeRegistrationCancellation,
                        operationCancellation))
                {
                    _activeRegistrationCancellation = null;
                    IsBusy = false;
                }
            }
            operationCancellation.Dispose();
        }
    }

    private bool TryCommitOutcome(
        long generation,
        CancellationTokenSource cancellation,
        string serverUrl,
        string email,
        RegisterState state)
    {
        lock (_registrationStateLock)
        {
            if (!IsCurrentOperation(generation, cancellation, allowCancellation: false))
                return false;

            _activeRegistrationCancellation = null;
            RegisteredServerUrl = serverUrl;
            RegisteredEmail = email;
            ClearSecretInputs();
            State = state;
            IsBusy = false;
            return true;
        }
    }

    private void TryUpdateCurrentOperation(
        long generation,
        CancellationTokenSource cancellation,
        bool allowCancellation,
        Action update)
    {
        lock (_registrationStateLock)
        {
            if (IsCurrentOperation(generation, cancellation, allowCancellation))
                update();
        }
    }

    private bool IsCurrentOperation(
        long generation,
        CancellationTokenSource cancellation,
        bool allowCancellation) =>
        generation == _registrationGeneration
        && ReferenceEquals(_activeRegistrationCancellation, cancellation)
        && (allowCancellation || !cancellation.IsCancellationRequested);

    // 调用服务前完成字段校验,并把焦点请求给第一个无效字段(文案沿用现有中文)。
    private bool ValidateInput()
    {
        var serverUrl = ResolveServerUrl();
        if (!TryParseServerEndpoint(serverUrl))
        {
            OperationError = "请输入有效的服务器地址";
            RequestFocus(RegisterFocusTarget.ServerUrl);
            return false;
        }

        if (string.IsNullOrWhiteSpace(Email)
            || Email.Trim().Length > MasterPasswordSaltPolicy.MaxLength
            || !Email.Contains('@'))
        {
            OperationError = "请输入有效的邮箱地址";
            RequestFocus(RegisterFocusTarget.Email);
            return false;
        }

        if (string.IsNullOrEmpty(MasterPassword))
        {
            OperationError = "密码不能为空";
            RequestFocus(RegisterFocusTarget.MasterPassword);
            return false;
        }

        if (MasterPassword != ConfirmPassword)
        {
            OperationError = "两次输入的密码不一致";
            RequestFocus(RegisterFocusTarget.ConfirmPassword);
            return false;
        }

        if (Name is not null && Name.Trim().Length > 50)
        {
            OperationError = "昵称不能超过 50 个字符";
            RequestFocus(RegisterFocusTarget.Name);
            return false;
        }

        return true;
    }

    private static bool TryParseServerEndpoint(string value)
    {
        try
        {
            return ServerEndpoint.TryParse(value, out _);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return false;
        }
    }

    private void RequestFocus(RegisterFocusTarget target)
    {
        FocusTarget = target;
        checked { FocusRequestId++; }
    }

    // ── 服务器 URL 解析 ───────────────────────────────────────────────────

    private string ResolveServerUrl() => SelectedServerOptionIndex switch
    {
        0 => BitwardenUsUrl,
        1 => BitwardenEuUrl,
        _ => ServerUrl.Trim(),
    };

    // ── 属性变更通知 ─────────────────────────────────────────────────────

    partial void OnSelectedServerOptionIndexChanged(int value)
    {
        if (value == 0)
            ServerUrl = BitwardenUsUrl;
        else if (value == 1)
            ServerUrl = BitwardenEuUrl;
        else if (ServerUrl is BitwardenUsUrl or BitwardenEuUrl)
            ServerUrl = "https://vault.example.com";

        OnPropertyChanged(nameof(ShowCustomServerUrl));
    }

    partial void OnServerUrlChanged(string value) => OnPropertyChanged(nameof(ShowCustomServerUrl));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanEditForm));
}
