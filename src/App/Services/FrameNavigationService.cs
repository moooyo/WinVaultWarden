using App.Views;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Core.Services;
using Core.Session;

namespace App.Services;

/// <summary>
/// <see cref="INavigationService"/> 的 WinUI 实现:持有内容区 Frame 与登录流 Frame,
/// 把领域导航方法逐字翻译为 <c>frame.Navigate(typeof(XxxPage), ...)</c>。
/// 两个 Frame 归 <see cref="MainWindow"/> 所有,故由 MainWindow 在 Frame 就绪后经
/// <see cref="Initialize"/> 注入;注入前调用导航方法为安全 no-op。
/// </summary>
public sealed class FrameNavigationService : INavigationService
{
    private Frame? _contentFrame;
    private Frame? _loginFrame;

    /// <summary>由 MainWindow 在 <c>InitializeComponent</c> 之后、首次导航之前注入两个 Frame。</summary>
    public void Initialize(Frame contentFrame, Frame loginFrame)
    {
        _contentFrame = contentFrame;
        _loginFrame = loginFrame;
    }

    // ── 内容区导航(ContentFrame)────────────────────────────────────────────
    public void GoToVault(string? tag = null) => NavigateContent(typeof(VaultPage), tag);
    public void GoToSend(string? tag = null) => NavigateContent(typeof(SendPage), tag);
    public void GoToGenerator() => NavigateContent(typeof(GeneratorPage));
    public void GoToSettings() => NavigateContent(typeof(SettingsPage));
    public void GoToDevices() => NavigateContent(typeof(DevicesPage));
    public void GoToSecurityReport() => NavigateContent(typeof(SecurityReportPage));
    public void GoToImportExport() => NavigateContent(typeof(ImportExportPage));
    public void GoToEmergencyAccess() => NavigateContent(typeof(EmergencyAccessPage));

    private void NavigateContent(Type pageType, string? tag = null)
    {
        // A delayed callback must never restore an unlocked page after a lifecycle transition.
        if (_contentFrame is null
            || global::App.App.Services.GetRequiredService<IVaultService>().Snapshot.State != VaultState.Unlocked
            || global::App.App.Services.GetRequiredService<IAppLifecycleCoordinator>().IsTransitioning)
            return;
        if (_contentFrame.Content is VaultPage vaultPage)
        {
            vaultPage.RememberWorkspaceContext();
            if (pageType == typeof(VaultPage))
            {
                vaultPage.RestoreWorkspaceContext(tag);
                return;
            }
        }
        // Only explicitly cacheable pages survive navigation, and only within this unlock session.
        _contentFrame.CacheSize = 2;
        _contentFrame.Navigate(pageType, tag);
        _contentFrame.BackStack.Clear();
        _contentFrame.ForwardStack.Clear();
    }

    // ── 登录流导航(LoginFrame)──────────────────────────────────────────────
    public void GoToRegister() => _loginFrame?.Navigate(typeof(RegisterPage));
    public void GoToLoginPage() => _loginFrame?.Navigate(typeof(LoginPage));

    /// <summary>
    /// 锁定/登出时清空两个 Frame 的内容与前后导航栈,防止锁屏后仍能经后退栈回到明文页面。
    /// </summary>
    public void ResetForLock()
    {
        Reset(_contentFrame);
        Reset(_loginFrame);
    }

    private static void Reset(Frame? frame)
    {
        if (frame is null)
            return;
        // Enabled caches are evicted before content and journal references are released.
        frame.CacheSize = 0;
        frame.Content = null;
        frame.BackStack.Clear();
        frame.ForwardStack.Clear();
    }
}
