using App.Services;
using Xunit;

namespace App.Tests;

public class CrashLoggerTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 7, 3, 8, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Format_IncludesTimestampSourceTypeAndMessage()
    {
        var entry = CrashLogger.Format("UI", new InvalidOperationException("boom"), FixedNow);

        Assert.Contains("2026-07-03T08:30:00.0000000+00:00", entry);
        Assert.Contains("UI:", entry);
        Assert.Contains("System.InvalidOperationException", entry);
        Assert.Contains("boom", entry);
        Assert.EndsWith("\n", entry);
    }

    [Fact]
    public void Format_IncludesInnerExceptionChain()
    {
        var ex = new Exception("outer", new InvalidOperationException("inner"));

        var entry = CrashLogger.Format("Task", ex, FixedNow);

        Assert.Contains("outer", entry);
        Assert.Contains(" --> System.InvalidOperationException: inner", entry);
    }

    [Fact]
    public void Format_DoesNotThrowWhenStackTraceNull()
    {
        // 未经 throw 的异常 StackTrace 为 null——不得因此报错。
        var entry = CrashLogger.Format("UI", new Exception("no-stack"), FixedNow);
        Assert.Contains("no-stack", entry);
    }

    [Fact]
    public void FormatRedacted_ContainsTypesButNoExceptionMessages()
    {
        var exception = new InvalidOperationException(
            "top-secret",
            new FormatException("inner-secret"));

        var entry = CrashLogger.FormatRedacted("Lifecycle:stop", exception, FixedNow);

        Assert.Contains("Lifecycle:stop", entry);
        Assert.Contains("System.InvalidOperationException", entry);
        Assert.Contains("System.FormatException", entry);
        Assert.DoesNotContain("top-secret", entry);
        Assert.DoesNotContain("inner-secret", entry);
    }

    [Fact]
    public void Write_CreatesLogsDirAndAppends()
    {
        var baseDir = NewTempDir();
        try
        {
            var p1 = CrashLogger.Write(baseDir, "line-1\n");
            var p2 = CrashLogger.Write(baseDir, "line-2\n");

            Assert.NotNull(p1);
            Assert.Equal(p1, p2); // 同一文件
            Assert.Equal(Path.Combine(baseDir, "logs", CrashLogger.LogFileName), p1);
            var text = File.ReadAllText(p1!);
            Assert.Equal("line-1\nline-2\n", text); // 追加而非覆盖
        }
        finally { Cleanup(baseDir); }
    }

    [Fact]
    public void Write_TruncatesWhenOverMaxSize()
    {
        var baseDir = NewTempDir();
        try
        {
            var dir = Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, CrashLogger.LogFileName);
            File.WriteAllText(path, new string('x', (int)CrashLogger.MaxLogBytes + 10));

            CrashLogger.Write(baseDir, "fresh\n");

            // 超限后清空重来:只剩最新一条。
            Assert.Equal("fresh\n", File.ReadAllText(path));
        }
        finally { Cleanup(baseDir); }
    }

    [Fact]
    public void Write_ReturnsNullOnFailureInsteadOfThrowing()
    {
        // baseDir 指向一个已存在的文件 → 无法在其下建 logs 目录,但必须静默返回 null。
        var file = Path.Combine(Path.GetTempPath(), "wvw-crashlog-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "x");
        try
        {
            var result = CrashLogger.Write(file, "entry\n");
            Assert.Null(result);
        }
        finally { File.Delete(file); }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wvw-crashlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
