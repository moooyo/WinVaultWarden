using App.Services;
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

    [ObservableProperty]
    public partial int SelectedSessionTimeoutIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedTimeoutActionIndex { get; set; }

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

    // ─────────────────────────────────────────────────────────────────────────

    // 无参构造器——供 XAML 设计时和 App.Tests 无注入场景使用
    public SettingsViewModel()
    {
    }

    public SettingsViewModel(IAccountUiService accountUi) => _accountUi = accountUi;

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
}
