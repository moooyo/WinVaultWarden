using System.Runtime.InteropServices;
using App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace App;

/// <summary>
/// Custom entry point replacing the generated XAML Main method. It resolves single-instance
/// activation and local-data reset recovery before application services are constructed.
/// </summary>
public static partial class Program
{
    private enum PackageIdentityStatus
    {
        Packaged,
        Unpackaged,
        Unknown,
    }

    private const string InstanceKey = "WinVaultWarden-main";
    private const int StartupFailureExitCode = 2;
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;
    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxYesNo = 0x00000004;
    private const uint MessageBoxIconError = 0x00000010;
    private const uint MessageBoxIconWarning = 0x00000030;
    private const uint MessageBoxDefaultButton2 = 0x00000100;
    private const uint MessageBoxTaskModal = 0x00002000;
    private const uint MessageBoxSetForeground = 0x00010000;
    private const int MessageBoxResultYes = 6;
    private const string StartupFailureTitle = "WinVaultWarden 启动失败";
    private const string RecoveryTitle = "WinVaultWarden 本地数据恢复";
    private const string StartupContendedMessage =
        "本地数据正在被另一个 WinVaultWarden 进程使用。请关闭其他实例后重试。";
    private const string StartupCorruptMessage =
        "检测到本地数据重置状态损坏。为保护数据，WinVaultWarden 未启动。";
    private const string StartupUnavailableMessage =
        "无法安全访问本地数据。请检查磁盘状态和访问权限后重试。";
    private const string StartupAmbiguousMessage =
        "检测到相互冲突的本地数据重置状态。为保护数据，WinVaultWarden 未启动。";
    private const string StartupUnsafePathMessage =
        "检测到不安全的本地数据路径。为保护数据，WinVaultWarden 未启动。";
    private const string PackagedRecoveryMessage =
        "检测到本地数据重置状态损坏或冲突。请在 Windows 设置的“已安装的应用”中打开 WinVaultWarden 高级选项并选择“重置”。服务器账户和密码库不会受到影响。";
    private const string UnknownPackageIdentityMessage =
        "无法确认 WinVaultWarden 的安装类型，因此未提供破坏性本地恢复。应用将退出；若通过 Windows 安装，请使用 Windows 设置中的应用重置，便携版请检查系统状态后重试。";
    private const string RecoveryEntryMessage =
        "检测到本地数据重置状态损坏或冲突，WinVaultWarden 无法安全启动。是否查看并进入“清除全部本机数据”恢复选项？选择“否”或关闭窗口不会修改任何数据，应用将退出。";
    private const string RecoveryFinalMessage =
        "最终确认：此操作会删除 WinVaultWarden 的保存账户、离线密码库、设备密钥、应用偏好、永久删除隔离记录、Windows Hello 凭据和便携版浏览器注册。服务器账户和服务器密码库、程序目录及用户导出文件不受影响。选择“是”后会先记录恢复请求；只有恢复请求安全提交后，才会在应用服务和界面启动前清理本机数据。是否继续？";
    private const string RecoveryCommitUncertainMessage =
        "本地恢复请求的提交结果无法确认。WinVaultWarden 将退出；下次启动会继续安全检查，请勿手动删除或移动应用数据。";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        try
        {
            if (DecideRedirection())
                return 0;
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            ShowStartupFailure(LocalDataResetStartupStatus.Unavailable);
            return StartupFailureExitCode;
        }

        LocalDataResetStartupCoordinator resetCoordinator;
        LocalDataResetStartupResult startup;
        try
        {
            var externalCleaner = new WindowsLocalDataResetExternalCleaner();
            resetCoordinator = new LocalDataResetStartupCoordinator(externalCleaner.Clean);
            startup = resetCoordinator.PrepareForStartup();
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            ShowStartupFailure(LocalDataResetStartupStatus.Unavailable);
            return StartupFailureExitCode;
        }

        if (!startup.CanStart)
        {
            if (!IsEmergencyRecoveryEligible(startup.Status))
            {
                ShowStartupFailure(startup.Status);
                return StartupFailureExitCode;
            }

            var packageIdentity = GetPackageIdentityStatus();
            var isExplicitlyPortable = packageIdentity == PackageIdentityStatus.Unpackaged;
            if (!isExplicitlyPortable)
            {
                ShowMessage(
                    packageIdentity == PackageIdentityStatus.Packaged
                        ? PackagedRecoveryMessage
                        : UnknownPackageIdentityMessage,
                    StartupFailureTitle,
                    MessageBoxIconError);
                return StartupFailureExitCode;
            }

            if (!TryRecoverPortableLocalData(resetCoordinator, startup.Status, out startup))
                return StartupFailureExitCode;
        }

        using var dataLease = startup.DataLease!;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new global::App.App();
        });
        return 0;
    }

    private static void ShowStartupFailure(LocalDataResetStartupStatus status)
    {
        _ = MessageBoxW(
            IntPtr.Zero,
            GetStartupFailureMessage(status),
            StartupFailureTitle,
            MessageBoxOk | MessageBoxIconError | MessageBoxTaskModal);
    }

    private static bool IsEmergencyRecoveryEligible(LocalDataResetStartupStatus status) =>
        status is LocalDataResetStartupStatus.Corrupt
            or LocalDataResetStartupStatus.Ambiguous;

    private static bool TryRecoverPortableLocalData(
        LocalDataResetStartupCoordinator coordinator,
        LocalDataResetStartupStatus observedStatus,
        out LocalDataResetStartupResult startup)
    {
        startup = default;
        if (!ConfirmEmergencyRecovery())
            return false;

        LocalDataEmergencyResetRequestResult request;
        try
        {
            request = coordinator.RequestEmergencyReset(observedStatus);
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            ShowEmergencyRequestFailure(LocalDataEmergencyResetRequestStatus.Unavailable);
            return false;
        }

        switch (request.Status)
        {
            case LocalDataEmergencyResetRequestStatus.Requested:
            case LocalDataEmergencyResetRequestStatus.AlreadyPending:
                startup = coordinator.PrepareForStartup();
                if (startup.CanStart)
                    return true;
                ShowStartupFailure(startup.Status);
                return false;
            case LocalDataEmergencyResetRequestStatus.CommitOutcomeUncertain:
                ShowMessage(
                    RecoveryCommitUncertainMessage,
                    RecoveryTitle,
                    MessageBoxIconWarning);
                return false;
            default:
                ShowEmergencyRequestFailure(request.Status);
                return false;
        }
    }

    private static bool ConfirmEmergencyRecovery()
    {
        var flags = MessageBoxYesNo
            | MessageBoxDefaultButton2
            | MessageBoxTaskModal
            | MessageBoxSetForeground;
        var first = MessageBoxW(
            IntPtr.Zero,
            RecoveryEntryMessage,
            RecoveryTitle,
            flags | MessageBoxIconWarning);
        if (first != MessageBoxResultYes)
            return false;

        var second = MessageBoxW(
            IntPtr.Zero,
            RecoveryFinalMessage,
            RecoveryTitle,
            flags | MessageBoxIconError);
        return second == MessageBoxResultYes;
    }

    private static void ShowEmergencyRequestFailure(
        LocalDataEmergencyResetRequestStatus status)
    {
        var message = status switch
        {
            LocalDataEmergencyResetRequestStatus.NotAllowed =>
                "当前启动状态不允许执行本地恢复。未修改任何数据，请重新启动应用。",
            LocalDataEmergencyResetRequestStatus.Contended => StartupContendedMessage,
            LocalDataEmergencyResetRequestStatus.Corrupt =>
                "无法安全创建本地恢复请求，因为现有恢复记录无法可靠归属本机目录。内置恢复未修改任何数据。请完全退出应用，先备份并人工检查 %LOCALAPPDATA% 下与 WinVaultWarden 相关的目录，确认清理后再启动；不要反复尝试内置恢复。",
            LocalDataEmergencyResetRequestStatus.UnsafePath => StartupUnsafePathMessage,
            LocalDataEmergencyResetRequestStatus.Unavailable =>
                "无法安全创建本地恢复请求。未修改任何数据，请检查磁盘状态和访问权限后重试。",
            _ =>
                "本地恢复状态无法确认，WinVaultWarden 将退出；下次启动会继续安全检查。",
        };
        ShowMessage(message, StartupFailureTitle, MessageBoxIconError);
    }

    private static void ShowMessage(string message, string title, uint icon)
    {
        _ = MessageBoxW(
            IntPtr.Zero,
            message,
            title,
            MessageBoxOk | icon | MessageBoxTaskModal | MessageBoxSetForeground);
    }

    private static string GetStartupFailureMessage(LocalDataResetStartupStatus status) =>
        status switch
        {
            LocalDataResetStartupStatus.Contended => StartupContendedMessage,
            LocalDataResetStartupStatus.Corrupt => StartupCorruptMessage,
            LocalDataResetStartupStatus.Ambiguous => StartupAmbiguousMessage,
            LocalDataResetStartupStatus.UnsafePath => StartupUnsafePathMessage,
            _ => StartupUnavailableMessage,
        };

    private static PackageIdentityStatus GetPackageIdentityStatus()
    {
        try
        {
            uint packageFullNameLength = 0;
            var result = GetCurrentPackageFullName(
                ref packageFullNameLength,
                IntPtr.Zero);
            return result switch
            {
                ErrorSuccess or ErrorInsufficientBuffer => PackageIdentityStatus.Packaged,
                AppModelErrorNoPackage => PackageIdentityStatus.Unpackaged,
                _ => PackageIdentityStatus.Unknown,
            };
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            return PackageIdentityStatus.Unknown;
        }
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

    // Pump COM messages on the main STA while a worker redirects activation asynchronously.
    private static void RedirectActivationTo(AppActivationArguments activationArgs, AppInstance keyInstance)
    {
        using var redirectEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset);
        var redirectTask = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await keyInstance.RedirectActivationToAsync(activationArgs)
                    .AsTask()
                    .ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    redirectEvent.Set();
                }
                catch (ObjectDisposedException)
                {
                    // The COM wait failed and the process is already leaving this path.
                }
            }
        });

        const uint CWMO_DEFAULT = 0;
        const uint INFINITE = 0xFFFFFFFF;
        var waitResult = CoWaitForMultipleObjects(
            CWMO_DEFAULT,
            INFINITE,
            1,
            [redirectEvent.SafeWaitHandle.DangerousGetHandle()],
            out _);
        if (waitResult != 0)
        {
            throw new COMException(
                "The activation redirection wait failed.",
                unchecked((int)waitResult));
        }

        redirectTask.GetAwaiter().GetResult();
    }

    [LibraryImport("ole32.dll")]
    private static partial uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles, IntPtr[] pHandles, out uint lpdwIndex);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static partial int GetCurrentPackageFullName(
        ref uint packageFullNameLength,
        IntPtr packageFullName);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "MessageBoxW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(
        IntPtr window,
        string text,
        string caption,
        uint type);
}
