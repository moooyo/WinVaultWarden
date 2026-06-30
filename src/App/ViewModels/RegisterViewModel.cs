using App.Services;
using Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace App.ViewModels;

public partial class RegisterViewModel : ObservableObject
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationError))]
    public partial string OperationError { get; set; } = string.Empty;

    // ── 成功后由 VM 暴露给页面读取 ────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationError))]
    public partial bool Registered { get; set; }

    [ObservableProperty]
    public partial string RegisteredEmail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RegisteredServerUrl { get; set; } = string.Empty;

    // ── 计算属性 ──────────────────────────────────────────────────────────

    public bool HasOperationError => !string.IsNullOrEmpty(OperationError);

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
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (RegistrationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "注册失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
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
}
