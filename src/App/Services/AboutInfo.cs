using System.Runtime.InteropServices;

namespace App.Services;

/// <summary>
/// 只读诊断信息，用于设置页"关于"区。所有取值均为运行时真实数据，
/// 取不到时给出明确占位，不臆造（例如不声称"已是最新版本"——本应用没有更新检查）。
/// </summary>
public static class AboutInfo
{
    // 与 App.csproj <Version> 保持一致;非打包运行时的兜底显示值。
    private const string FallbackVersion = "0.1.0";

    public static string AppVersion
    {
        get
        {
            // 打包运行时优先用包版本；非打包/取不到时回退到编译期常量。
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch (InvalidOperationException)
            {
                return FallbackVersion;
            }
        }
    }

    public static string WindowsVersion => $"Windows {Environment.OSVersion.Version}";

    public static string DotNetVersion => RuntimeInformation.FrameworkDescription;

    public static string Architecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

    /// <summary>组合成可复制的纯文本诊断块。</summary>
    public static string ToDiagnosticsText() =>
        $"应用版本: {AppVersion}\n{WindowsVersion}\n.NET: {DotNetVersion}\n架构: {Architecture}";
}
