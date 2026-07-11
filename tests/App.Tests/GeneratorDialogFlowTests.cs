using Xunit;

namespace App.Tests;

/// <summary>
/// R3/R14 源码结构守卫。对话框顺序与全局异常兜底属 WinUI 运行时行为、无法单测,
/// 故扫描源文件锁定关键结构不被回退(与 SettingsPageXamlTests 的做法一致)。
/// </summary>
public class GeneratorDialogFlowTests
{
    private static string ReadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join('/', relativeParts));
    }

    // ── R3: 生成器历史不得嵌套 ContentDialog ─────────────────────────────────

    [Fact]
    public void GeneratorDialog_HistoryHandler_DoesNotNestContentDialog()
    {
        var src = ReadSource("src", "App", "Views", "GeneratorDialog.xaml.cs");

        // 历史处理器只置标志 + Hide,不在本对话框仍打开时构造/显示第二个 ContentDialog。
        Assert.DoesNotContain("new GeneratorHistoryDialog", src);
        Assert.Contains("Hide();", src);
        Assert.Contains("ConsumeHistoryRequest", src);
    }

    [Fact]
    public void MainWindow_ShowsHistoryFromHostAfterGeneratorCloses()
    {
        var src = ReadSource("src", "App", "MainWindow.xaml.cs");

        // 历史对话框改由宿主在生成器关闭后打开,并据标志循环重开生成器。
        Assert.Contains("ConsumeHistoryRequest", src);
        Assert.Contains("new GeneratorHistoryDialog", src);
    }

    // ── R14: 全局未处理异常兜底已接线 ────────────────────────────────────────

    [Fact]
    public void App_RegistersGlobalUnhandledExceptionHandlers()
    {
        var src = ReadSource("src", "App", "App.xaml.cs");

        Assert.Contains("UnhandledException +=", src);
        Assert.Contains("TaskScheduler.UnobservedTaskException +=", src);
        Assert.Contains("CrashLogger", src);
        Assert.Contains("FailClosedSynchronously", src);
        Assert.Contains("e.Handled = false", src);
        Assert.DoesNotContain("e.Handled = true;", src);
        var logIndex = src.IndexOf("FormatRedacted", StringComparison.Ordinal);
        var clearIndex = src.IndexOf("FailClosedSynchronously", StringComparison.Ordinal);
        var terminateIndex = src.IndexOf("e.Handled = false", StringComparison.Ordinal);
        Assert.True(logIndex >= 0 && logIndex < clearIndex);
        Assert.True(clearIndex < terminateIndex);
    }
}
