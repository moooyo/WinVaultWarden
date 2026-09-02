using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using App.Services;
using Core.Services;
using Core.Session;
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

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 全局兜底:UI 层遍布的 async void 处理器一旦抛出未捕获异常(如 ContentDialog 重入 COMException、
        // 附件文件 I/O),会直接终止整个进程且无任何日志。这里先记一条脱敏诊断,再按白名单区分:
        // 显式可恢复/取消 → 标记已处理继续运行;未知异常 → 同步 fail-closed 清空敏感状态后保持未处理,
        // 交由系统终止进程,绝不带着已损坏的状态继续跑(见 OnUnhandledException)。
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            Services.GetRequiredService<IClipboardService>().ClearSecretsNow();
        }
        catch (Exception exception)
        {
            CrashLogger.WriteRedacted("ClipboardStartupClear", exception);
        }

        try
        {
            await Services.GetRequiredService<AccountDeletionRecoveryHost>()
                .ResumeConfirmedCleanupAsync();
        }
        catch
        {
            // Startup recovery is best effort. The login guard remains authoritative on failure.
        }

        MainWindow = new MainWindow();
        ThemeManager.Apply(AppPreferences.Current.ThemeIndex);

        // 浏览器集成(passkey 桥接)不再在启动时仅凭偏好启动:锁定的登录页绝不监听。bridge 只在 vault
        // 解锁就绪后由 MainWindow.ShowVault 按设置启动,并随生命周期(Lock/Logout/Exit)停止。此处只订阅
        // 设置的运行时变更。MSIX packaged 时 Host 不随包携带,变更处理器再以 IsPackaged 兜底 fail-closed。
        ViewModels.SettingsViewModel.AllowBrowserIntegrationSettingChanged += OnBrowserIntegrationChanged;

        MainWindow.Activate();
    }

    private static void OnBrowserIntegrationChanged(bool enabled)
    {
        // packaged 时 Host 不随包携带,防御层强制关闭(SettingsViewModel 已拦截 true 不发事件,此处兜底)。
        // 用 fault-only continuation 观察任务,绝不留下未观察异常。
        _ = ApplyBrowserIntegrationSettingAsync(enabled && !AboutInfo.IsPackaged)
            .ContinueWith(
                task => CrashLogger.Write(
                    AppDataDir,
                    CrashLogger.FormatRedacted("BrowserBridge", task.Exception!, DateTimeOffset.UtcNow)),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }

    // 运行时切换浏览器集成设置:仅当 vault 已解锁时才启动 bridge,否则停止。锁定登录页不监听,重新解锁
    // 由 ShowVault 恢复。任何失败在此脱敏记录并吞掉,绝不上抛(调用方再以 fault-only continuation 兜底)。
    private static async Task ApplyBrowserIntegrationSettingAsync(bool enabled)
    {
        var bridge = Services.GetRequiredService<PasskeyBridgeServer>();
        var vault = Services.GetRequiredService<IVaultService>();
        try
        {
            if (enabled && vault.Snapshot.State == VaultState.Unlocked)
                bridge.Start();
            else
                await bridge.StopAsync();
        }
        catch (Exception exception)
        {
            CrashLogger.WriteRedacted("BrowserBridge", exception);
        }
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
