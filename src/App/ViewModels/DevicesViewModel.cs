using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.Services;

namespace App.ViewModels;

public partial class DevicesViewModel : ObservableObject
{
    private readonly IAuthRequestUiService? _authRequests;

    public ObservableCollection<DeviceItem> Devices { get; } = new();
    public ObservableCollection<AuthRequestItem> PendingRequests { get; } = new();

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasNoDevices => Devices.Count == 0;

    public DevicesViewModel(IDeviceUiService service, IAuthRequestUiService? authRequests = null)
    {
        _authRequests = authRequests;
        foreach (var d in service.GetDevices()) Devices.Add(d);
    }

    // ── 登录授权请求 ──────────────────────────────────────────────────────────

    /// <summary>刷新待审批的设备登录请求列表。</summary>
    public async Task RefreshRequestsAsync(CancellationToken ct = default)
    {
        if (_authRequests is null)
        {
            Error = "设备登录授权服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        Error = null;
        try
        {
            var items = await _authRequests.ListPendingAsync(ct);
            PendingRequests.Clear();
            foreach (var item in items)
                PendingRequests.Add(item);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，静默处理
        }
        catch (AuthRequestOperationException ex)
        {
            Error = ex.Message;
        }
        catch
        {
            Error = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>批准指定的设备登录请求，成功后从列表移除。</summary>
    public async Task ApproveAsync(AuthRequestItem item, CancellationToken ct = default)
    {
        if (_authRequests is null)
        {
            Error = "设备登录授权服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        Error = null;
        try
        {
            await _authRequests.ApproveAsync(item.Id, item.PublicKey, ct);
            PendingRequests.Remove(item);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，静默处理
        }
        catch (AuthRequestOperationException ex)
        {
            Error = ex.Message;
        }
        catch
        {
            Error = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>拒绝指定的设备登录请求，成功后从列表移除。</summary>
    public async Task DenyAsync(AuthRequestItem item, CancellationToken ct = default)
    {
        if (_authRequests is null)
        {
            Error = "设备登录授权服务不可用";
            return;
        }
        if (IsBusy)
            return;

        IsBusy = true;
        Error = null;
        try
        {
            await _authRequests.DenyAsync(item.Id, ct);
            PendingRequests.Remove(item);
        }
        catch (OperationCanceledException)
        {
            // 用户取消，静默处理
        }
        catch (AuthRequestOperationException ex)
        {
            Error = ex.Message;
        }
        catch
        {
            Error = "操作失败，请稍后重试";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
