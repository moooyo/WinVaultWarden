using App.Services;
using Core.Models;
using Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.ViewModels;

/// <summary>
/// 设置页 ViewModel 的纯 C# 部分（无 WinUI 依赖）。
/// WinUI 相关属性（主题切换回调、AboutInfo、ThemeManager）在 SettingsViewModel.WinUI.cs 中定义。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAccountUiService? _accountUi;
    private readonly ITwoFactorUiService? _twoFactorUi;

    [ObservableProperty]
    public partial int SelectedSessionTimeoutIndex { get; set; } = AppPreferences.Current.SessionTimeoutIndex;

    [ObservableProperty]
    public partial int SelectedTimeoutActionIndex { get; set; } = AppPreferences.Current.TimeoutActionIndex;

    [ObservableProperty]
    public partial bool UsePinUnlock { get; set; }

    [ObservableProperty]
    public partial int SelectedClearClipboardIndex { get; set; }

    [ObservableProperty]
    public partial bool MinimizeOnCopy { get; set; }

    [ObservableProperty]
    public partial bool ShowIconsAndPasswordUrl { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowTrayIcon { get; set; } = true;

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; } = true;

    [ObservableProperty]
    public partial bool StartOnLogin { get; set; }

    [ObservableProperty]
    public partial bool AllowBrowserIntegration { get; set; }

    [ObservableProperty]
    public partial bool UseHardwareAcceleration { get; set; } = true;

    [ObservableProperty]
    public partial bool EnableSshAgent { get; set; } = true;

    [ObservableProperty]
    public partial int SelectedSshAuthorizationPromptIndex { get; set; }

    [ObservableProperty]
    public partial bool AllowScreenshots { get; set; } = true;

    [ObservableProperty]
    public partial int SelectedLanguageIndex { get; set; }

    // ── 账户操作状态 ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOperationError))]
    public partial string OperationError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool HasOperationError => !string.IsNullOrEmpty(OperationError);

    partial void OnSelectedSessionTimeoutIndexChanged(int value)
    {
        AppPreferences.Current.SessionTimeoutIndex = value;
        AppPreferences.Save();
    }

    partial void OnSelectedTimeoutActionIndexChanged(int value)
    {
        AppPreferences.Current.TimeoutActionIndex = value;
        AppPreferences.Save();
    }

    // ─────────────────────────────────────────────────────────────────────────

    // 无参构造器——供 XAML 设计时和 App.Tests 无注入场景使用
    public SettingsViewModel()
    {
    }

    public SettingsViewModel(IAccountUiService accountUi) => _accountUi = accountUi;

    public SettingsViewModel(ITwoFactorUiService twoFactorUi) => _twoFactorUi = twoFactorUi;

    public SettingsViewModel(IAccountUiService accountUi, ITwoFactorUiService twoFactorUi)
    {
        _accountUi = accountUi;
        _twoFactorUi = twoFactorUi;
    }

    public string AccountEmail => _accountUi?.GetAccount().Email ?? string.Empty;
    public string AccountServer => _accountUi?.GetAccount().ServerUrl ?? string.Empty;
    public string AccountInitial => _accountUi?.GetAccount().Initial ?? string.Empty;
    public string KdfSummary => _accountUi?.GetAccount().KdfSummary ?? string.Empty;

    // ── 账户操作 ─────────────────────────────────────────────────────────────

    // 直接可 await 的方法（供 code-behind 调用）；RelayCommand 包装供 XAML 绑定。

    public async Task ChangePasswordAsync(string current, string next, string confirm, string? hint, CancellationToken ct = default)
    {
        if (_accountUi is null)
        {
            OperationError = "账户服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _accountUi.ChangePasswordAsync(current, next, confirm, hint, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (AccountOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RenameAsync(string name, CancellationToken ct = default)
    {
        if (_accountUi is null)
        {
            OperationError = "账户服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _accountUi.RenameAsync(name, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (AccountOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ChangeIterationsAsync(string current, int iterations, CancellationToken ct = default)
    {
        if (_accountUi is null)
        {
            OperationError = "账户服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _accountUi.ChangeIterationsAsync(current, iterations, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (AccountOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // RenameCommand 供 XAML 绑定（string 参数可正常传递）
    // ChangePassword/ChangeIterations 的 tuple 版本不能被 XAML CommandParameter 绑定，
    // 由 code-behind 直接 await 公开的 async 方法即可。
    [RelayCommand]
    private async Task Rename(string name) =>
        await RenameAsync(name);

    // ── 两步验证操作 ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TwoFactorProvider>> ListProvidersAsync(CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return [];
        }
        if (IsBusy)
            return [];

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            return await _twoFactorUi.ListProvidersAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return [];
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return [];
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<(string secret, string otpauth)> BeginTotpSetupAsync(string pw, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return (string.Empty, string.Empty);
        }
        if (IsBusy)
            return (string.Empty, string.Empty);

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            return await _twoFactorUi.BeginTotpSetupAsync(pw, ct);
        }
        catch (OperationCanceledException)
        {
            return (string.Empty, string.Empty);
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return (string.Empty, string.Empty);
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return (string.Empty, string.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<string> EnableTotpAsync(string pw, string secret, string code, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return string.Empty;
        }
        if (IsBusy)
            return string.Empty;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            return await _twoFactorUi.EnableTotpAsync(pw, secret, code, ct);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
            return string.Empty;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
            return string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SendEmailAsync(string pw, string email, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _twoFactorUi.SendEmailAsync(pw, email, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task EnableEmailAsync(string pw, string email, string token, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _twoFactorUi.EnableEmailAsync(pw, email, token, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DisableTwoFactorAsync(string pw, int type, CancellationToken ct = default)
    {
        if (_twoFactorUi is null)
        {
            OperationError = "两步验证服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = string.Empty;
        try
        {
            await _twoFactorUi.DisableAsync(pw, type, ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，不设错误
        }
        catch (TwoFactorOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
