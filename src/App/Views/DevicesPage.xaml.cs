using App.ViewModels;
using App.ViewModels.Models;
using App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace App.Views;

public sealed partial class DevicesPage : Page
{
    private readonly DeviceApprovalWindowService _requestWindows;
    private readonly DispatcherTimer _expiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loaded;
    private CancellationTokenSource? _pageCancellation;
    private bool _refreshing;
    private bool _refreshAgain;
    public DevicesViewModel ViewModel { get; }

    public DevicesPage()
    {
        ViewModel = global::App.App.Services.GetRequiredService<DevicesViewModel>();
        _requestWindows = global::App.App.Services.GetRequiredService<DeviceApprovalWindowService>();
        InitializeComponent();
        Unloaded += OnPageUnloaded;
        _expiryTimer.Tick += (_, _) => RefreshExpiryLabels();
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        _pageCancellation?.Dispose();
        _pageCancellation = new CancellationTokenSource();
        _requestWindows.PendingRequestsChanged -= RefreshRequestsList;
        _requestWindows.PendingRequestsChanged += RefreshRequestsList;
        _expiryTimer.Start();
        RefreshRequestsList();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        _pageCancellation?.Cancel();
        _expiryTimer.Stop();
        _requestWindows.PendingRequestsChanged -= RefreshRequestsList;
    }

    private void OnRefreshRequestsClick(object sender, RoutedEventArgs e)
    {
        RefreshRequestsList();
    }

    /// <summary>Refreshes requests after a notification on the UI thread.</summary>
    public void RefreshRequestsList() => _ = RefreshPendingRequestsAsync();

    private async Task RefreshPendingRequestsAsync()
    {
        if (!_loaded)
            return;
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            var ct = _pageCancellation?.Token ?? CancellationToken.None;
            await Task.WhenAll(ViewModel.RefreshRequestsAsync(ct), ViewModel.RefreshDevicesAsync(ct));
            if (_loaded)
                RefreshExpiryLabels();
        }
        finally
        {
            _refreshing = false;
            if (_refreshAgain && _loaded)
            {
                _refreshAgain = false;
                await RefreshPendingRequestsAsync();
            }
        }
    }

    private void RefreshExpiryLabels()
    {
        foreach (var item in ViewModel.PendingRequests.ToArray())
        {
            if (item.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                ViewModel.PendingRequests.Remove(item);
            else
                item.RefreshExpiration();
        }
        if (!ViewModel.IsBusy && ViewModel.PendingRequests.Count == 0
            && ViewModel.RequestsState == DataPageState.Content)
            ViewModel.RequestsState = DataPageState.Empty;
    }

    private void OnApproveRequestClick(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as AuthRequestItem
                ?? (sender as FrameworkElement)?.DataContext as AuthRequestItem;
        if (item is not null)
            _requestWindows.OpenRequest(item);
    }

    private async void OnDenyRequestClick(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.Tag as AuthRequestItem
                ?? (sender as FrameworkElement)?.DataContext as AuthRequestItem;
        if (item is null)
            return;

        await ViewModel.DenyAsync(
            item,
            (request, ct) => DialogHelper.ConfirmAsync(
                XamlRoot,
                "拒绝登录请求",
                $"确定拒绝来自 {request.DeviceTypeName} 的登录请求吗？",
                "拒绝",
                ct: ct),
            CancellationToken.None);
    }
}
