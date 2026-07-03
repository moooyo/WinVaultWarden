using System.IO;

namespace App.Services;

/// <summary>
/// 命名管道 peer 鉴别的**纯判定**逻辑（安全整改 M1）。与运行时取 PID/镜像路径分离,便于单测。
///
/// 价值边界(见 spec):同用户恶意软件已能读解锁进程内存/注入已签名 host,故 M1 是**防御纵深**——
/// 抬高"只想直连管道的机会型恶意软件"的门槛,非硬边界。浏览器集成默认关闭(S5),审批对话框仍是主要边界。
///
/// ⚠️ 运行时部分(ClientPeerAuthorizer)与真实 host 路径/签名的判定**需 app-run 验证**,尚未在此环境验证。
/// </summary>
public static class PeerAuthorization
{
    /// <summary>
    /// 连接进程镜像路径是否被授权。imagePath 须等于 expectedPath(规范化、大小写不敏感);
    /// requireSignature 为真时(Release)还需 signatureValid。
    /// </summary>
    public static bool IsAuthorized(string? imagePath, string? expectedPath, bool signatureValid, bool requireSignature)
    {
        if (string.IsNullOrEmpty(imagePath) || string.IsNullOrEmpty(expectedPath))
            return false;

        if (!PathsEqual(imagePath, expectedPath))
            return false;

        return !requireSignature || signatureValid;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 路径非法(非常规字符等)→ 视为不匹配,fail-closed。
            return false;
        }
    }
}
