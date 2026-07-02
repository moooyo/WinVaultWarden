namespace App.Services;

/// <summary>
/// 集中导航所有权:所有页面切换经此服务下沉,不再散落 <c>Frame.Navigate</c>。
/// 方法用领域语义命名,参数保持 WinUI-free(仅出现字符串 tag),
/// 以便被 ViewModel 或被 <c>tests/App.Tests</c> 链接的纯 C# 文件安全引用。
/// </summary>
public interface INavigationService
{
    /// <summary>导航到密码库页(内容区)。<paramref name="tag"/> 为既有的字符串过滤参数。</summary>
    void GoToVault(string? tag = null);

    /// <summary>导航到 Send 页(内容区)。<paramref name="tag"/> 为既有的字符串过滤参数。</summary>
    void GoToSend(string? tag = null);

    /// <summary>导航到设置页(内容区)。</summary>
    void GoToSettings();

    /// <summary>导航到设备页(内容区)。</summary>
    void GoToDevices();

    /// <summary>导航到安全报告页(内容区)。</summary>
    void GoToSecurityReport();

    /// <summary>导航到导入 / 导出页(内容区)。</summary>
    void GoToImportExport();

    /// <summary>导航到紧急访问页(内容区)。</summary>
    void GoToEmergencyAccess();

    /// <summary>导航到注册页(登录流)。</summary>
    void GoToRegister();

    /// <summary>导航到登录页(登录流)。</summary>
    void GoToLoginPage();
}
