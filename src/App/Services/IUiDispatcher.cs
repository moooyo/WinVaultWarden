namespace App.Services;

/// <summary>
/// UI 线程派发抽象:把“到 UI 线程执行回调”的动作从静态 <c>App.MainWindow.DispatcherQueue</c>
/// 解耦出来,供组合根 / 设置页等以依赖注入方式派发。
/// 刻意保持 WinUI-free(不出现 DispatcherQueue / Window 类型),以便可被链接进 App.Tests 的
/// 纯 C# 文件与 ViewModel 引用,并在测试中提供假实现。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>当前线程是否即为 UI 线程;未初始化时为 <c>false</c>。</summary>
    bool HasThreadAccess { get; }

    /// <summary>
    /// Attempts to enqueue a callback on the UI thread. Returns <c>false</c> when the dispatcher
    /// is unavailable or shutting down so security-sensitive callers can fail closed.
    /// </summary>
    bool Enqueue(Action action);

    /// <summary>把异步回调排入 UI 线程执行,并返回其完成的 <see cref="Task"/>;未初始化时为安全 no-op。</summary>
    Task EnqueueAsync(Func<Task> action);
}
