using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace App.Views;

// 全 App 唯一允许 await FileOpenPicker/FileSavePicker 结果、以及围绕其后续文件读写的入口。
// 所有 picker/文件 IO 都注册到 Task1 的瞬态操作登记表,使锁屏/登出/退出时能取消等待并丢弃迟到结果。
// WinUI 的 IAsyncOperation<StorageFile> 一旦系统选择器 UI 弹出就无法真正中途取消(Cancel() 仅在
// 尚未返回时尝试请求取消,系统对话框本身不受应用控制)——这里的"取消"语义是:取消等待并丢弃结果,
// 不是强制关闭系统选择器窗口。
public static class FilePickerHelper
{
    public static Task<StorageFile?> PickSingleAsync(
        FileOpenPicker picker,
        CancellationToken ct = default) =>
        AwaitPickerAsync(picker.PickSingleFileAsync(), ct);

    public static Task<StorageFile?> PickSaveAsync(
        FileSavePicker picker,
        CancellationToken ct = default) =>
        AwaitPickerAsync(picker.PickSaveFileAsync(), ct);

    public static async Task<T?> RunTrackedAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Action? clearSensitive = null,
        CancellationToken ct = default)
    {
        var registry = global::App.App.Services
            .GetRequiredService<global::App.Services.ITransientOperationRegistry>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var tracked = registry.Register(
            linked.Cancel,
            () => { },
            clearSensitive ?? (() => { }));
        try
        {
            var result = await operation(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return default;
        }
        finally
        {
            clearSensitive?.Invoke();
        }
    }

    /// <summary>
    /// Starts an uncancellable storage transaction only while the expected vault lease remains
    /// unlocked and no lifecycle transition is pending. Starting the OS operation is the
    /// linearization point; the operation is awaited outside both coordinator and session locks.
    /// </summary>
    public static async Task CommitSensitiveAsync(
        StorageStreamTransaction transaction,
        Core.Session.VaultSessionLease expectedLease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(expectedLease);
        ct.ThrowIfCancellationRequested();

        var lifecycle = global::App.App.Services
            .GetRequiredService<global::App.Services.IAppLifecycleCoordinator>();
        var session = global::App.App.Services
            .GetRequiredService<Vault.VaultSession>();
        IAsyncAction? commitOperation = null;
        var started = lifecycle.TryRunSensitive(
            () => session.TryRunIfUnlocked(
                expectedLease,
                () =>
                {
                    ct.ThrowIfCancellationRequested();
                    commitOperation = transaction.CommitAsync();
                }));
        if (!started || commitOperation is null)
        {
            throw new OperationCanceledException(
                "A lock or logout prevented the file commit.",
                ct.IsCancellationRequested ? ct : new CancellationToken(canceled: true));
        }

        await commitOperation.AsTask().ConfigureAwait(false);
    }

    private static async Task<StorageFile?> AwaitPickerAsync(
        IAsyncOperation<StorageFile> operation,
        CancellationToken ct)
    {
        var registry = global::App.App.Services
            .GetRequiredService<global::App.Services.ITransientOperationRegistry>();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cancellationRequested = 0;
        void CancelPickerWait()
        {
            if (Interlocked.Exchange(ref cancellationRequested, 1) != 0)
                return;

            TryCancel(linked);
            TryCancel(operation);
        }
        using var tracked = registry.Register(
            CancelPickerWait,
            () => { },
            () => { });
        try
        {
            var result = await operation.AsTask(linked.Token);
            if (Volatile.Read(ref cancellationRequested) != 0
                || linked.IsCancellationRequested)
                return null;
            return result;
        }
        catch (OperationCanceledException)
            when (linked.IsCancellationRequested || operation.Status == AsyncStatus.Canceled)
        {
            return null;
        }
    }

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (AggregateException)
        {
            // A cancellation callback cannot keep a picker wait alive during lifecycle cleanup.
        }
    }

    private static void TryCancel(IAsyncInfo operation)
    {
        try
        {
            operation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // The picker completed while lifecycle cleanup was claiming its registration.
        }
    }
}
