using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices.WindowsRuntime;
using Core.Session;
using App.Services;

namespace App.Views;

// 全 App 唯一允许调用 ContentDialog.ShowAsync 的入口。所有对话框必须经 ShowTrackedAsync
// 注册到 Task1 的瞬态操作登记表,使锁屏/登出/退出时生命周期协调器能强制取消并关闭它们,
// 且能在强制关闭路径上执行调用方提供的敏感值清理(clearSensitive)。
public static class DialogHelper
{
    public static async Task<ContentDialogResult> ShowTrackedAsync(
        ContentDialog dialog,
        Action? clearSensitive = null,
        CancellationToken ct = default)
    {
        var registry = global::App.App.Services
            .GetRequiredService<global::App.Services.ITransientOperationRegistry>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var tracked = registry.Register(
            linked.Cancel,
            () => dialog.DispatcherQueue.TryEnqueue(dialog.Hide),
            clearSensitive ?? (() => { }));

        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var result = await dialog.ShowAsync().AsTask(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return ContentDialogResult.None;
        }
    }

    public static async Task<ContentDialogResult> ShowTrackedAsync(
        ContentDialog dialog,
        VaultSessionLease expectedSession,
        Action? clearSensitive = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        ArgumentNullException.ThrowIfNull(expectedSession);
        var services = global::App.App.Services;
        var registry = services.GetRequiredService<ITransientOperationRegistry>();
        var lifecycle = services.GetRequiredService<IAppLifecycleCoordinator>();
        var session = services.GetRequiredService<Vault.VaultSession>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var started = SessionBoundOperationStarter.TryStart(
            lifecycle,
            session,
            expectedSession,
            () => registry.Register(
                linked.Cancel,
                () => dialog.DispatcherQueue.TryEnqueue(dialog.Hide),
                clearSensitive ?? (() => { })),
            () =>
            {
                linked.Token.ThrowIfCancellationRequested();
                return dialog.ShowAsync();
            });
        if (started is null)
            return ContentDialogResult.None;

        try
        {
            var result = await started.Operation.AsTask(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return ContentDialogResult.None;
        }
    }

    // 破坏性操作的统一确认弹窗。各页/壳复用,保证交互与文案约定一致。
    public static async Task<bool> ConfirmAsync(
        XamlRoot root,
        string title,
        string message,
        string primaryText,
        string closeText = "取消",
        CancellationToken ct = default)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        return await ShowTrackedAsync(dialog, ct: ct) == ContentDialogResult.Primary;
    }

    public static async Task<bool> ConfirmAsync(
        XamlRoot root,
        VaultSessionLease expectedSession,
        string title,
        string message,
        string primaryText,
        string closeText = "取消",
        CancellationToken ct = default)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = closeText,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        return await ShowTrackedAsync(dialog, expectedSession, ct: ct)
            == ContentDialogResult.Primary;
    }
}
