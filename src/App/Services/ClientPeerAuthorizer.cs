using System.IO;
using System.IO.Pipes;
using App.Interop;
using Core.BrowserIntegration;

namespace App.Services;

/// <summary>连接进程鉴别(M1)。桥接在处理请求前调用,拒绝非期望 host 的连接。</summary>
public interface IPeerAuthorizer
{
    bool IsAuthorized(NamedPipeServerStream pipe);
}

/// <summary>
/// 运行时实现：取管道客户端 PID → 镜像路径,与期望的 BrowserNativeHost.exe 比对(纯判定见 PeerAuthorization)。
///
/// ⚠️ **需 app-run 验证**：期望路径(策略 A：相对本应用安装位置)与真实 host 是否一致、真实浏览器扩展能连通、
/// 伪造进程被拒——均未在本环境验证。Release 的 Authenticode 验签尚未实现(当前 path-only),见 spec 待办。
/// 拒绝时把镜像路径写入崩溃日志目录,供 app-run 时确认期望路径。
/// </summary>
public sealed class ClientPeerAuthorizer : IPeerAuthorizer
{
    // 策略 A：host 与本应用同目录(MSIX 下同包)。定位规则与安装脚本/发布布局共用同一 locator。
    private static readonly string ExpectedHostPath = BrowserNativeHostLocator.ResolveCurrent();

    // Release Authenticode 验签尚未实现 → 暂 path-only(见 spec)。实现后按 #if !DEBUG 置真。
    private const bool RequireSignature = false;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinVaultWarden");

    public bool IsAuthorized(NamedPipeServerStream pipe)
    {
        var imagePath = ResolveClientImagePath(pipe);
        var ok = PeerAuthorization.IsAuthorized(imagePath, ExpectedHostPath, signatureValid: true, RequireSignature);
        if (!ok)
            CrashLogger.Write(LogDir, $"[peer-reject] image='{imagePath}' expected='{ExpectedHostPath}'\n");
        return ok;
    }

    private static unsafe string? ResolveClientImagePath(NamedPipeServerStream pipe)
    {
        try
        {
            if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
                return null;

            var proc = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == IntPtr.Zero)
                return null;
            try
            {
                var buffer = new char[1024];
                uint size = (uint)buffer.Length;
                fixed (char* p = buffer)
                {
                    if (!NativeMethods.QueryFullProcessImageName(proc, 0, p, ref size))
                        return null;
                }
                return new string(buffer, 0, (int)size);
            }
            finally
            {
                NativeMethods.CloseHandle(proc);
            }
        }
        catch
        {
            return null; // 解析失败 → fail-closed(返回 null → 未授权)
        }
    }
}
