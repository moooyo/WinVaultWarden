using System.Reflection;
using System.Runtime.InteropServices;

namespace App.Services;

/// <summary>
/// 只读诊断信息，用于设置页“关于”区。所有取值均为运行时真实数据，
/// 取不到时给出明确占位，不臆造（例如不声称“已是最新版本”——本应用没有更新检查）。
/// </summary>
public static class AboutInfo
{
    public static string AppVersion
    {
        get
        {
            // 打包运行时优先用包版本；非打包/取不到时回退到程序集版本。
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch (InvalidOperationException)
            {
                var asm = Assembly.GetExecutingAssembly().GetName().Version;
                return asm?.ToString() ?? "—";
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
