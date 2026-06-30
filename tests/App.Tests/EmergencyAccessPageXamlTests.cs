using Xunit;

namespace App.Tests;

/// <summary>
/// 字符串断言测试：确认 EmergencyAccessPage.xaml 包含正确的绑定和命令，且无反射 {Binding}（AOT 要求）。
/// 同时断言 code-behind 文件中实际调用了各命令。
/// </summary>
public class EmergencyAccessPageXamlTests
{
    private static string Xaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "EmergencyAccessPage.xaml");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find EmergencyAccessPage.xaml from the test output directory.");
    }

    private static string CodeBehind()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "EmergencyAccessPage.xaml.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find EmergencyAccessPage.xaml.cs from the test output directory.");
    }

    [Fact]
    public void Page_BindsTwoSectionsAndInvite()
    {
        var xaml = Xaml();
        Assert.Contains("MyContacts", xaml);
        Assert.Contains("TrustedByOthers", xaml);
        Assert.Contains("InviteCommand", xaml);
    }

    [Fact]
    public void Page_HasTakeoverCommand()
    {
        var xaml = Xaml();
        Assert.Contains("TakeoverCommand", xaml);
    }

    [Fact]
    public void Page_NoReflectionBinding()
    {
        // AOT: 禁止任何反射 {Binding ...}（允许 {x:Bind ...}）
        var xaml = Xaml();
        Assert.DoesNotContain("{Binding ", xaml);
    }

    [Fact]
    public void CodeBehind_InvokesInviteCommandExecuteAsync()
    {
        var cs = CodeBehind();
        Assert.Contains("InviteCommand.ExecuteAsync", cs);
    }

    [Fact]
    public void CodeBehind_InvokesTakeoverCommandExecuteAsync()
    {
        var cs = CodeBehind();
        Assert.Contains("TakeoverCommand.ExecuteAsync", cs);
    }

    [Fact]
    public void CodeBehind_InvokesUpdateCommandExecuteAsync()
    {
        var cs = CodeBehind();
        Assert.Contains("UpdateCommand.ExecuteAsync", cs);
    }
}
