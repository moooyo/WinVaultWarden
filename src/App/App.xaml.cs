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
        // 全局兜底:UI 层遍布的 async void 处理器一旦抛出未捕获异常(如 ContentDialog 重入 COMException、
        // 附件文件 I/O),会直接终止整个进程且无任何日志。这里先记一条脱敏诊断,再按白名单区分:
        // 显式可恢复/取消 → 标记已处理继续运行;未知异常 → 同步 fail-closed 清空敏感状态后保持未处理,
        // 交由系统终止进程,绝不带着已损坏的状态继续跑(见 OnUnhandledException)。
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        MainWindow = new MainWindow();
        ThemeManager.Apply(AppPreferences.Current.ThemeIndex);

        // 浏览器集成(passkey 桥接)受设置门控(S5):仅当"允许浏览器集成"开启时才启动桥接命名管道服务器。
        // 默认关闭——UI 显示"关"即真的不监听。开关运行时切换即时启停(桥接可重启)。
        if (AppPreferences.Current.AllowBrowserIntegration)
            Services.GetRequiredService<PasskeyBridgeServer>().Start();
        ViewModels.SettingsViewModel.AllowBrowserIntegrationSettingChanged += OnBrowserIntegrationChanged;

        MainWindow.Activate();
    }

    private static void OnBrowserIntegrationChanged(bool enabled)
    {
        var bridge = Services.GetRequiredService<PasskeyBridgeServer>();
        if (enabled)
            bridge.Start();
        else
            _ = bridge.StopAsync();
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // 只记脱敏诊断(类型链 + HResult),绝不落 Message/Inner/栈——它们可能夹带主密码/令牌/敏感路径。
        CrashLogger.Write(AppDataDir, CrashLogger.FormatRedacted("UI", e.Exception, DateTimeOffset.UtcNow));

        var canContinue = UiExceptionPolicy.CanContinue(e.Exception);
        if (canContinue)
        {
            // 显式可恢复/取消:安全地把此次杂散 UI 异常降级为"记一条日志继续运行"。
            e.Handled = canContinue;
            return;
        }

        // 未知异常:先同步 fail-closed(阻断敏感操作、失效会话、清空敏感状态/剪贴板/瞬态窗口),
        // 再保持未处理,让系统按原异常终止进程——绝不吞掉未知异常带着损坏状态继续。
        // 兜底清理本身绝不能再抛/盖住原始异常:Services 理论上此刻已构造,仍防御性判空并吞掉任何清理异常
        // (FailClosedSynchronously 内部已对每步 Safe 包裹,这里的 try 只防 bootstrap 极早期/Provider 异常)。
        try
        {
            Services?.GetService<IAppLifecycleCoordinator>()?.FailClosedSynchronously();
        }
        catch
        {
            // 清理失败不得中断兜底,也不得盖住原始未知异常。
        }
        e.Handled = false;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLogger.Write(AppDataDir, CrashLogger.FormatRedacted("Task", e.Exception, DateTimeOffset.UtcNow));
        e.SetObserved();
    }
}
