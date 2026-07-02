using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace App;

/// <summary>
/// 自定义入口（替代 XAML 生成的 Main，见 csproj DISABLE_XAML_GENERATED_MAIN）：
/// 加单实例——已运行时再次启动则把激活重定向给现有实例并恢复其窗口，本进程退出。
/// 其余初始化完整复刻生成 Main（ComWrappers + DispatcherQueueSynchronizationContext）。
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "WinVaultWarden-main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (DecideRedirection())
            return 0;   // 已重定向给现有实例，本进程退出

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new global::App.App();
        });
        return 0;
    }

    private static bool DecideRedirection()
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnActivated;
            return false;
        }

        RedirectActivationTo(activationArgs, keyInstance);
        return true;
    }

    private static void OnActivated(object? sender, AppActivationArguments e)
    {
        var window = global::App.App.MainWindow;
        window?.DispatcherQueue.TryEnqueue(() => window.RestoreFromTray());
    }

    // 官方推荐的同步重定向：后台线程执行异步重定向，主 STA 线程用 CoWaitForMultipleObjects 泵消息避免死锁。
    private static void RedirectActivationTo(AppActivationArguments activationArgs, AppInstance keyInstance)
    {
        var redirectEvent = CreateEvent(IntPtr.Zero, true, false, null);
        System.Threading.Tasks.Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait();
            SetEvent(redirectEvent);
        });

        const uint CWMO_DEFAULT = 0;
        const uint INFINITE = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1, [redirectEvent], out _);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateEvent(IntPtr lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr hEvent);

    [LibraryImport("ole32.dll")]
    private static partial uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles, IntPtr[] pHandles, out uint lpdwIndex);
}
