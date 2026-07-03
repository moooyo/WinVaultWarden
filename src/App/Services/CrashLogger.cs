using System.Globalization;
using System.Text;

namespace App.Services;

/// <summary>
/// 全局未处理异常的最后一道网:把异常格式化后追加到 appdata 下的滚动崩溃日志,
/// 供事后定位(此前 async void 处理器抛出的异常会静默终止进程、无任何记录)。
/// 纯逻辑、不依赖 WinUI,便于单测。只记录异常类型/消息/栈,不含任何密钥材料
/// (与 Crypto 层异常卫生一致:异常消息仅含长度/encType 等非机密信息)。
/// 日志本身的任何失败都静默吞掉——兜底器绝不能再抛。
/// </summary>
public static class CrashLogger
{
    /// <summary>日志文件超过此大小则清空重来,避免无限增长。</summary>
    public const long MaxLogBytes = 256 * 1024;

    public const string LogFileName = "crash.log";

    /// <summary>把异常(含 inner 链)格式化为一条日志文本。now 由调用方传入以便测试确定性。</summary>
    public static string Format(string source, Exception ex, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append('[')
          .Append(now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))
          .Append("] ")
          .Append(source)
          .Append(": ")
          .Append(ex.GetType().FullName)
          .Append(": ")
          .Append(ex.Message);

        if (!string.IsNullOrEmpty(ex.StackTrace))
            sb.Append('\n').Append(ex.StackTrace);

        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            sb.Append("\n --> ")
              .Append(inner.GetType().FullName)
              .Append(": ")
              .Append(inner.Message);
        }

        sb.Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// 把一条日志追加到 <paramref name="baseDir"/>/logs/crash.log(超过上限先清空)。
    /// 返回写入的文件路径,失败返回 null(静默,不抛)。
    /// </summary>
    public static string? Write(string baseDir, string entry)
    {
        try
        {
            var dir = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, LogFileName);

            if (File.Exists(path) && new FileInfo(path).Length > MaxLogBytes)
                File.WriteAllText(path, string.Empty);

            File.AppendAllText(path, entry);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
