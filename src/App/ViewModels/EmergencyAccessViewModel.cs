using System.Collections.ObjectModel;
using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Models;
using Core.Services;

namespace App.ViewModels;

public partial class EmergencyAccessViewModel : ObservableObject
{
    private readonly IEmergencyAccessUiService _service;

    // ── 两个显示集合 ──────────────────────────────────────────────────────────

    /// <summary>授予方视角：我授权出去的紧急联系人。</summary>
    public ObservableCollection<EmergencyContact> MyContacts { get; } = new();

    /// <summary>受托方视角：信任我的账户（我可以访问其密码库）。</summary>
    public ObservableCollection<GrantedAccess> TrustedByOthers { get; } = new();

    /// <summary>View 命令解密后的密码库内容。</summary>
    public ObservableCollection<RecoveredVault> RecoveredItems { get; } = new();

    // ── 忙碌 / 错误状态 ───────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? OperationError { get; set; }

    public bool HasError => !string.IsNullOrEmpty(OperationError);

    // ── 邀请输入 ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string InviteEmail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial EmergencyAccessType InviteType { get; set; } = EmergencyAccessType.View;

    [ObservableProperty]
    public partial int InviteWaitTimeDays { get; set; } = 7;

    // ── 当前选中项的参数（供各命令读取） ──────────────────────────────────────

    /// <summary>选中的紧急访问记录 Id（授予方视角命令使用）。</summary>
    [ObservableProperty]
    public partial string SelectedContactId { get; set; } = string.Empty;

    /// <summary>选中记录对应受托人的 UserId（ConfirmCommand 需要）。</summary>
    [ObservableProperty]
    public partial string SelectedGranteeId { get; set; } = string.Empty;

    /// <summary>选中的 GrantedAccess 记录 Id（受托方视角命令使用）。</summary>
    [ObservableProperty]
    public partial string SelectedGrantedId { get; set; } = string.Empty;

    /// <summary>选中 GrantedAccess 的授予方邮箱（ViewCommand / TakeoverCommand 需要）。</summary>
    [ObservableProperty]
    public partial string SelectedGrantorEmail { get; set; } = string.Empty;

    // ── Takeover 新密码 ───────────────────────────────────────────────────────

    [ObservableProperty]
    public partial string TakeoverNewPassword { get; set; } = string.Empty;

    // ── View 命令结果 ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoveredVault))]
    public partial RecoveredVault? RecoveredVault { get; set; }

    public bool HasRecoveredVault => RecoveredVault is not null;

    // ── 构造 ──────────────────────────────────────────────────────────────────

    public EmergencyAccessViewModel(IEmergencyAccessUiService service)
    {
        _service = service;
    }

    // ── 加载 ──────────────────────────────────────────────────────────────────

    /// <summary>加载两个集合（授予方联系人 + 受托方列表）。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = null;
        try
        {
            var trusted = await _service.GetTrustedAsync(ct);
            var granted = await _service.GetGrantedAsync(ct);

            MyContacts.Clear();
            foreach (var c in trusted) MyContacts.Add(c);

            TrustedByOthers.Clear();
            foreach (var g in granted) TrustedByOthers.Add(g);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，静默处理
        }
        catch (EmergencyAccessOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch (Exception)
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── 授予方命令 ────────────────────────────────────────────────────────────

    /// <summary>邀请新的紧急联系人，成功后重新加载列表。</summary>
    [RelayCommand]
    private async Task Invite()
    {
        await RunAsync(async ct =>
        {
            await _service.InviteAsync(InviteEmail, InviteType, InviteWaitTimeDays, ct);
            await ReloadAsync(ct);
        });
    }

    /// <summary>确认受托人（受托人已接受邀请，授予方执行 Confirm 完成握手）。</summary>
    [RelayCommand]
    private async Task Confirm()
    {
        await RunAsync(async ct =>
        {
            await _service.ConfirmAsync(SelectedContactId, SelectedGranteeId, ct);
            await ReloadAsync(ct);
        });
    }

    /// <summary>重新发送邀请邮件。</summary>
    [RelayCommand]
    private async Task Reinvite()
    {
        await RunAsync(async ct =>
        {
            await _service.ReinviteAsync(SelectedContactId, ct);
            await ReloadAsync(ct);
        });
    }

    /// <summary>移除紧急联系人授权。</summary>
    [RelayCommand]
    private async Task Remove()
    {
        await RunAsync(async ct =>
        {
            await _service.RemoveAsync(SelectedContactId, ct);
            await ReloadAsync(ct);
        });
    }

    /// <summary>更新紧急访问类型或等待天数。</summary>
    [RelayCommand]
    private async Task Update()
    {
        await RunAsync(async ct =>
        {
            await _service.UpdateAsync(SelectedContactId, InviteType, InviteWaitTimeDays, ct);
            await ReloadAsync(ct);
        });
    }

    /// <summary>批准受托人发起的紧急访问请求。</summary>
    [RelayCommand]
    private async Task Approve()
    {
        await RunAsync(async ct =>
        {
            await _service.ApproveAsync(SelectedContactId, ct);
            await ReloadAsync(ct);
        });
    }

    /// <summary>拒绝受托人发起的紧急访问请求。</summary>
    [RelayCommand]
    private async Task Reject()
    {
        await RunAsync(async ct =>
        {
            await _service.RejectAsync(SelectedContactId, ct);
            await ReloadAsync(ct);
        });
    }

    // ── 受托方命令 ────────────────────────────────────────────────────────────

    /// <summary>发起紧急访问（启动等待期倒计时）。</summary>
    [RelayCommand]
    private async Task Initiate()
    {
        await RunAsync(async ct =>
        {
            await _service.InitiateAsync(SelectedGrantedId, ct);
            await ReloadAsync(ct);
        });
    }

    /// <summary>查看授予方密码库（解密后存入 RecoveredVault / RecoveredItems）。</summary>
    [RelayCommand]
    private async Task View()
    {
        await RunAsync(async ct =>
        {
            var vault = await _service.ViewAsync(SelectedGrantedId, SelectedGrantorEmail, ct);
            RecoveredVault = vault;
            RecoveredItems.Clear();
            RecoveredItems.Add(vault);
        });
    }

    /// <summary>接管并重置授予方的主密码。</summary>
    [RelayCommand]
    private async Task Takeover()
    {
        await RunAsync(async ct =>
        {
            await _service.TakeoverAndResetPasswordAsync(SelectedGrantedId, SelectedGrantorEmail, TakeoverNewPassword, ct);
        });
    }

    // ── 辅助 ──────────────────────────────────────────────────────────────────

    /// <summary>统一错误处理包装：IsBusy 保护 + try/catch 写 OperationError。</summary>
    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        OperationError = null;
        try
        {
            await action(ct);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，静默处理
        }
        catch (EmergencyAccessOperationException ex)
        {
            OperationError = ex.Message;
        }
        catch (Exception)
        {
            OperationError = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        var trusted = await _service.GetTrustedAsync(ct);
        var granted = await _service.GetGrantedAsync(ct);

        MyContacts.Clear();
        foreach (var c in trusted) MyContacts.Add(c);

        TrustedByOthers.Clear();
        foreach (var g in granted) TrustedByOthers.Add(g);
    }
}
