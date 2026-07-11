using App.Services;
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

public partial class RegisterViewModel : ObservableObject, ISensitiveState
{
    private const string BitwardenUsUrl = "https://vault.bitwarden.com";
    private const string BitwardenEuUrl = "https://vault.bitwarden.eu";

    private readonly IRegisterUiService? _registerUi;

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

    // 页面据 FocusRequestId 单调递增触发一次编程式焦点。
    [ObservableProperty]
    public partial RegisterFocusTarget FocusTarget { get; set; }

    [ObservableProperty]
    public partial long FocusRequestId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationError))]
    public partial string OperationError { get; set; } = string.Empty;

    // ── 成功后由 VM 暴露给页面读取 ────────────────────────────────────────

    [ObservableProperty]
    public partial bool Registered { get; set; }

    [ObservableProperty]
    public partial string RegisteredEmail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RegisteredServerUrl { get; set; } = string.Empty;

    // ── 计算属性 ──────────────────────────────────────────────────────────

    public bool HasOperationError => !string.IsNullOrEmpty(OperationError);

    // 注册进行中禁用整个表单(含返回登录):由 OnIsBusyChanged 通知刷新。
    public bool CanEditForm => !IsBusy;

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

    // 锁屏/登出时清空注册页机密输入:主密码、确认密码与提示。幂等。
    public void ClearSensitive()
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

        if (IsBusy)
            return;

        if (!ValidateInput())
            return;

        IsBusy = true;
        OperationError = string.Empty;

        try
        {
            var serverUrl = ResolveServerUrl();
            var email = Email.Trim();

            await _registerUi.RegisterAsync(serverUrl, email, Name, MasterPassword, ConfirmPassword, MasterPasswordHint, ct);

            RegisteredServerUrl = serverUrl;
            RegisteredEmail = email;
            Registered = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户取消,不设错误
            OperationError = string.Empty;
        }
        catch (RegistrationException ex)
        {
            OperationError = ex.Message;
            RequestFocus(RegisterFocusTarget.Email);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // 调用服务前完成字段校验,并把焦点请求给第一个无效字段(文案沿用现有中文)。
    private bool ValidateInput()
    {
        var serverUrl = ResolveServerUrl();
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            OperationError = "请输入有效的服务器地址";
            RequestFocus(RegisterFocusTarget.ServerUrl);
            return false;
        }

        if (string.IsNullOrWhiteSpace(Email) || !Email.Contains('@'))
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
