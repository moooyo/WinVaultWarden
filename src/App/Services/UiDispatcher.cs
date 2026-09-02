using Microsoft.UI.Dispatching;

namespace App.Services;

/// <summary>
/// <see cref="IUiDispatcher"/> 的 WinUI 实现:包裹主窗口的 <see cref="DispatcherQueue"/>。
/// 由 <see cref="MainWindow"/> 在窗口 DispatcherQueue 就绪后经 <see cref="Initialize"/> 注入
/// (与 <see cref="FrameNavigationService.Initialize"/> 同模式);注入前调用为安全 no-op。
/// </summary>
public sealed class UiDispatcher : IUiDispatcher
{
    private DispatcherQueue? _queue;

    /// <summary>由 MainWindow 在窗口 <see cref="DispatcherQueue"/> 就绪后注入(首次派发之前)。</summary>
    public void Initialize(DispatcherQueue queue) => _queue = queue;

    public bool HasThreadAccess => _queue?.HasThreadAccess ?? false;

    public bool Enqueue(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var queue = _queue;
        return queue is not null && queue.TryEnqueue(() => action());
    }

    public Task EnqueueAsync(Func<Task> action)
    {
        var queue = _queue;
        if (queue is null)
            return Task.CompletedTask;

        // 与 PasskeyApprovalDialogService 相同的 TCS + TryEnqueue 封装:把回调排到 UI 线程,
        // 用 TaskCompletionSource 把其完成/异常回传给调用方等待。
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
            {
                try
                {
                    await action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetResult();
        }

        return tcs.Task;
    }
}
