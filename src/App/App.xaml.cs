using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow? MainWindow { get; private set; }

    // 崩溃日志目录:与 DpapiTokenStore / AppPreferences 共用 LocalApplicationData/WinVaultWarden。
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinVaultWarden");

    public App()
    {
        InitializeComponent();
        Services = ServiceConfiguration.Build();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 全局兜底:此前 UI 层遍布的 async void 处理器一旦抛出未捕获异常(如 ContentDialog
        // 重入 COMException、附件文件 I/O),会直接终止整个进程且无任何日志。这里记录后标记
        // 已处理,把一次杂散的 UI 异常降级为"记一条日志继续运行"而非硬崩溃。
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        MainWindow = new MainWindow();
        ThemeManager.Apply(AppPreferences.Current.ThemeIndex);
        Services.GetRequiredService<PasskeyBridgeServer>().Start();
        MainWindow.Activate();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLogger.Write(AppDataDir, CrashLogger.Format("UI", e.Exception, DateTimeOffset.UtcNow));
        // 标记已处理:密码管理器里这类异常绝大多数是可恢复的 UI/IO 抖动,继续运行优于整进程崩溃。
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLogger.Write(AppDataDir, CrashLogger.Format("Task", e.Exception, DateTimeOffset.UtcNow));
        e.SetObserved();
    }
}
