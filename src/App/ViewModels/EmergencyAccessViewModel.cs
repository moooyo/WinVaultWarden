using System.Collections.ObjectModel;
using App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Models;
using Core.Services;
using Vault;

namespace App.ViewModels;

public partial class EmergencyAccessViewModel : ObservableObject, ISensitiveState
{
    private readonly IEmergencyAccessUiService _service;
    private readonly VaultSession? _session;

    // 单持有者原子闸门:加载与所有写操作共享同一把闸门,确保任意两个操作(含 confirm 弹窗等待期间)
    // 都不会并发重入——第二个调用立即拿到 Busy,而不是被静默丢弃或读到半途状态。
    private readonly AsyncOperationGate _operations = new();

    // 网络层真实失败为 VaultApiException/VaultWriteException(均 : HttpRequestException)。文案固定脱敏,
    // 绝不回显 ex.Message(服务端错误可能夹带被授权账户/资源标识)。
    private const string NetworkLoadError = "无法加载紧急访问信息，请检查网络后重试。";
    private const string NetworkOperationError = "网络请求失败，请检查连接后重试。";

    // ── 两个显示集合 ──────────────────────────────────────────────────────────

    /// <summary>授予方视角：我授权出去的紧急联系人。</summary>
    public ObservableCollection<EmergencyContact> MyContacts { get; } = new();

    /// <summary>受托方视角：信任我的账户（我可以访问其密码库）。</summary>
    public ObservableCollection<GrantedAccess> TrustedByOthers { get; } = new();

    /// <summary>View 命令解密后的密码库内容。每次任意操作开始时先清空,只保留本次成功 View 的结果。</summary>
    public ObservableCollection<RecoveredVault> RecoveredItems { get; } = new();

    // ── 忙碌 / 错误状态 ───────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOperate))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? OperationError { get; set; }

    public bool HasError => !string.IsNullOrEmpty(OperationError);

    // ── 页面四态（Loading/Error/Empty/Content，由 LoadAsync / ReloadAsync 驱动）──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoading))]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyPropertyChangedFor(nameof(ShowContent))]
    public partial DataPageState PageState { get; set; } = DataPageState.Loading;

    public bool ShowLoading => PageState == DataPageState.Loading;
    public bool ShowError => PageState == DataPageState.Error;
    public bool ShowEmpty => PageState == DataPageState.Empty;
    public bool ShowContent => PageState == DataPageState.Content;
    public bool CanOperate => !IsBusy;
    public bool HasNoContacts => MyContacts.Count == 0;
    public bool HasNoGranted => TrustedByOthers.Count == 0;
    public bool HasRecoveredItems => RecoveredItems.Count > 0;

    // ── 邀请输入（仅 InviteEmail 供敏感清理/回填使用；type/waitDays 由调用方显式传入）──

    [ObservableProperty]
    public partial string InviteEmail { get; set; } = string.Empty;

    // ── 构造 ──────────────────────────────────────────────────────────────────

    public EmergencyAccessViewModel(IEmergencyAccessUiService service, VaultSession? session = null)
    {
        _service = service;
        _session = session;
        MyContacts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoContacts));
        TrustedByOthers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoGranted));
        RecoveredItems.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecoveredItems));
    }

    // ── 加载 ──────────────────────────────────────────────────────────────────

    /// <summary>加载两个集合（授予方联系人 + 受托方列表），并驱动页面四态。</summary>
    public async Task<UiOperationResult> LoadAsync(CancellationToken ct = default)
    {
        // 演示/离线会话没有服务端:短路为空的两个集合(展示干净的空状态),不触网、不报错。
        if (_session?.IsDemo == true)
        {
            MyContacts.Clear();
            TrustedByOthers.Clear();
            RecoveredItems.Clear();
            OperationError = null;
            PageState = DataPageState.Empty;
            return UiOperationResult.Success();
        }

        if (!_operations.TryEnter(out var operation))
            return UiOperationResult.Busy();

        using (operation)
        {
            IsBusy = true;
            PageState = DataPageState.Loading;
            OperationError = null;
            RecoveredItems.Clear();
            try
            {
                await ReloadAsync(ct);
                return UiOperationResult.Success();
            }
            catch (OperationCanceledException)
            {
                return UiOperationResult.Cancelled();
            }
            catch (HttpRequestException)
            {
                OperationError = NetworkLoadError;
                PageState = DataPageState.Error;
                return UiOperationResult.Failure(NetworkLoadError);
            }
            catch (EmergencyAccessOperationException ex)
            {
                OperationError = ex.Message;
                PageState = DataPageState.Error;
                return UiOperationResult.Failure(ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    // ── 授予方命令（均接收具体 EmergencyContact item，不依赖可变 SelectedId）──────

    /// <summary>邀请新的紧急联系人，成功后重新加载列表。</summary>
    public Task<UiOperationResult> InviteAsync(
        string email,
        EmergencyAccessType type,
        int waitTimeDays,
        CancellationToken ct = default) =>
        RunMutationAsync(
            token => _service.InviteAsync(email.Trim(), type, waitTimeDays, token),
            reload: true,
            ct);

    /// <summary>确认受托人（受托人已接受邀请，授予方执行 Confirm 完成握手）。</summary>
    public Task<UiOperationResult> ConfirmAsync(
        EmergencyContact item,
        CancellationToken ct = default) =>
        RunMutationAsync(
            token => _service.ConfirmAsync(item.Id, item.GranteeId ?? string.Empty, token),
            reload: true,
            ct);

    /// <summary>重新发送邀请邮件。</summary>
    public Task<UiOperationResult> ReinviteAsync(
        EmergencyContact item,
        CancellationToken ct = default) =>
        RunMutationAsync(
            token => _service.ReinviteAsync(item.Id, token),
            reload: true,
            ct);

    /// <summary>移除紧急联系人授权。破坏性操作，先经 <paramref name="confirm"/> 用户确认。</summary>
    public async Task<UiOperationResult> RemoveAsync(
        EmergencyContact item,
        Func<EmergencyContact, CancellationToken, Task<bool>> confirm,
        CancellationToken ct = default)
    {
        var result = await RunAsync(async token =>
        {
            if (!await confirm(item, token))
                return false;
            await _service.RemoveAsync(item.Id, token);
            await ReloadAsync(token);
            return true;
        }, ct);

        if (!result.IsSuccess)
            return new UiOperationResult(result.Status, result.Error);
        return result.Value
            ? UiOperationResult.Success()
            : UiOperationResult.Cancelled();
    }

    /// <summary>更新紧急访问类型或等待天数。</summary>
    public Task<UiOperationResult> UpdateAsync(
        EmergencyContact item,
        EmergencyAccessType type,
        int waitTimeDays,
        CancellationToken ct = default) =>
        RunMutationAsync(
            token => _service.UpdateAsync(item.Id, type, waitTimeDays, token),
            reload: true,
            ct);

    /// <summary>批准受托人发起的紧急访问请求。</summary>
    public Task<UiOperationResult> ApproveAsync(
        EmergencyContact item,
        CancellationToken ct = default) =>
        RunMutationAsync(
            token => _service.ApproveAsync(item.Id, token),
            reload: true,
            ct);

    /// <summary>拒绝受托人发起的紧急访问请求。破坏性操作，先经 <paramref name="confirm"/> 用户确认。</summary>
    public async Task<UiOperationResult> RejectAsync(
        EmergencyContact item,
        Func<EmergencyContact, CancellationToken, Task<bool>> confirm,
        CancellationToken ct = default)
    {
        var result = await RunAsync(async token =>
        {
            if (!await confirm(item, token))
                return false;
            await _service.RejectAsync(item.Id, token);
            await ReloadAsync(token);
            return true;
        }, ct);

        if (!result.IsSuccess)
            return new UiOperationResult(result.Status, result.Error);
        return result.Value
            ? UiOperationResult.Success()
            : UiOperationResult.Cancelled();
    }

    // ── 受托方命令（均接收具体 GrantedAccess item）───────────────────────────────

    /// <summary>发起紧急访问（启动等待期倒计时）。</summary>
    public Task<UiOperationResult> InitiateAsync(
        GrantedAccess item,
        CancellationToken ct = default) =>
        RunMutationAsync(
            token => _service.InitiateAsync(item.Id, token),
            reload: true,
            ct);

    /// <summary>查看授予方密码库（解密后存入 RecoveredItems）。只消费本次调用的返回值。</summary>
    public async Task<UiOperationResult<RecoveredVault>> ViewAsync(
        GrantedAccess item,
        CancellationToken ct = default)
    {
        var result = await RunAsync(
            token => _service.ViewAsync(item.Id, item.Email ?? string.Empty, token),
            ct);
        if (result.IsSuccess && result.Value is { } recovered)
            RecoveredItems.Add(recovered);
        return result;
    }

    /// <summary>接管并重置授予方的主密码。新密码只作为参数流转，绝不写入 VM 可观察状态。</summary>
    public async Task<UiOperationResult> TakeoverAsync(
        GrantedAccess item,
        string newPassword,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
            return UiOperationResult.Failure("新主密码不能为空");
        var result = await RunAsync(
            async token =>
            {
                await _service.TakeoverAndResetPasswordAsync(
                    item.Id,
                    item.Email ?? string.Empty,
                    newPassword,
                    token);
                return true;
            },
            ct);
        return new UiOperationResult(result.Status, result.Error);
    }

    // ── 辅助 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 单一原子 gate 包装:Busy/Cancelled/Failed/Succeeded 四态明确返回,不静默丢弃第二次调用。
    /// 每次进入都先清空上一次 View 结果,避免用户在无关操作后仍看到旧密码库。
    /// 未知异常不在此捕获,交由上层 fail-closed 边界处理。
    /// </summary>
    private async Task<UiOperationResult<T>> RunAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        if (!_operations.TryEnter(out var operation))
            return UiOperationResult<T>.Busy();

        using (operation)
        {
            IsBusy = true;
            OperationError = null;
            ClearRecoveredResult();
            try
            {
                return UiOperationResult<T>.Success(await action(ct));
            }
            catch (OperationCanceledException)
            {
                return UiOperationResult<T>.Cancelled();
            }
            catch (HttpRequestException)
            {
                OperationError = NetworkOperationError;
                return UiOperationResult<T>.Failure(NetworkOperationError);
            }
            catch (EmergencyAccessOperationException ex)
            {
                OperationError = ex.Message;
                return UiOperationResult<T>.Failure(ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    private void ClearRecoveredResult() => RecoveredItems.Clear();

    private async Task<UiOperationResult> RunMutationAsync(
        Func<CancellationToken, Task> mutation,
        bool reload,
        CancellationToken ct)
    {
        var result = await RunAsync(
            async token =>
            {
                await mutation(token);
                if (reload)
                    await ReloadAsync(token);
                return true;
            },
            ct);
        return new UiOperationResult(result.Status, result.Error);
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        var trusted = await _service.GetTrustedAsync(ct);
        var granted = await _service.GetGrantedAsync(ct);

        MyContacts.Clear();
        foreach (var c in trusted) MyContacts.Add(c);

        TrustedByOthers.Clear();
        foreach (var g in granted) TrustedByOthers.Add(g);

        PageState = MyContacts.Count == 0 && TrustedByOthers.Count == 0
            ? DataPageState.Empty
            : DataPageState.Content;
    }

    // 锁屏/登出时清空紧急访问的机密足迹:恢复出的授予方密码库、联系人/授权列表、操作错误。幂等。
    // 接管新密码从不落在 VM 状态上(直接作为参数传递给 TakeoverAsync),因此无需在此清理。
    public void ClearSensitive()
    {
        RecoveredItems.Clear();
        InviteEmail = string.Empty;
        MyContacts.Clear();
        TrustedByOthers.Clear();
        OperationError = null;
    }
}
