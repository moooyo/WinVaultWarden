using System.Linq;
using Xunit;

namespace App.Tests;

/// <summary>
/// 字符串断言测试：确认 EmergencyAccessPage.xaml 包含正确的绑定和状态矩阵，且无反射 {Binding}（AOT 要求）。
/// 同时断言 code-behind 文件中实际调用了各 item-based 方法，且接管密码不落在 ViewModel 状态上。
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
    public void Page_BindsTwoSections()
    {
        var xaml = Xaml();
        Assert.Contains("ViewModel.MyContacts", xaml);
        Assert.Contains("ViewModel.TrustedByOthers", xaml);
    }

    [Fact]
    public void Page_NoReflectionBinding()
    {
        // AOT: 禁止任何反射 {Binding ...}（允许 {x:Bind ...}）
        var xaml = Xaml();
        Assert.DoesNotContain("{Binding ", xaml);
    }

    [Fact]
    public void Page_HasFourStateContainers()
    {
        var xaml = Xaml();
        Assert.Contains("ViewModel.ShowLoading", xaml);
        Assert.Contains("ViewModel.ShowError", xaml);
        Assert.Contains("ViewModel.ShowEmpty", xaml);
        Assert.Contains("ViewModel.ShowContent", xaml);
    }

    [Fact]
    public void Page_EachSection_HasOwnEmptyStateBoundToHasNoXxx()
    {
        var xaml = Xaml();
        Assert.Contains("ViewModel.HasNoContacts", xaml);
        Assert.Contains("ViewModel.HasNoGranted", xaml);
    }

    // ── 状态矩阵：每个行级按钮必须绑定到 Show* 静态函数,不存在无条件显示的命令 ────

    [Theory]
    [InlineData("ShowReinvite")]
    [InlineData("ShowConfirm")]
    [InlineData("ShowUpdate")]
    [InlineData("ShowApprove")]
    [InlineData("ShowReject")]
    [InlineData("ShowRemove")]
    [InlineData("ShowInitiate")]
    [InlineData("ShowView")]
    [InlineData("ShowTakeover")]
    public void Page_ButtonVisibility_BoundToStateMatrixFunction(string functionName)
    {
        var xaml = Xaml();
        Assert.Contains($"local:EmergencyAccessPage.{functionName}(", xaml);
    }

    [Fact]
    public void Page_InviteAndListsAreDisabled_WhenBusy()
    {
        var xaml = Xaml();
        Assert.Contains("ViewModel.CanOperate", xaml);
    }

    [Fact]
    public void CodeBehind_HasStateMatrixStaticFunctions()
    {
        var cs = CodeBehind();
        Assert.Contains("public static Visibility ShowReinvite(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowConfirm(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowUpdate(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowApprove(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowReject(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowRemove(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowInitiate(EmergencyAccessStatus status)", cs);
        Assert.Contains("public static Visibility ShowView(", cs);
        Assert.Contains("public static Visibility ShowTakeover(", cs);
    }

    [Fact]
    public void CodeBehind_InvokesItemBasedMethods_NotSelectedIdProperties()
    {
        var cs = CodeBehind();
        Assert.Contains("ViewModel.InviteAsync(", cs);
        Assert.Contains("ViewModel.ConfirmAsync(item", cs);
        Assert.Contains("ViewModel.ReinviteAsync(item", cs);
        Assert.Contains("ViewModel.ApproveAsync(item", cs);
        Assert.Contains("ViewModel.RejectAsync(", cs);
        Assert.Contains("ViewModel.RemoveAsync(", cs);
        Assert.Contains("ViewModel.UpdateAsync(item", cs);
        Assert.Contains("ViewModel.InitiateAsync(item", cs);
        Assert.Contains("ViewModel.ViewAsync(item", cs);
        Assert.Contains("ViewModel.TakeoverAsync(item", cs);

        // 不再依赖可变 SelectedId
        Assert.DoesNotContain("SelectedContactId", cs);
        Assert.DoesNotContain("SelectedGranteeId", cs);
        Assert.DoesNotContain("SelectedGrantedId", cs);
        Assert.DoesNotContain("SelectedGrantorEmail", cs);
    }

    [Fact]
    public void CodeBehind_ViewClick_ConsumesThisInvocationValue_NotStaleViewModelProperty()
    {
        var cs = CodeBehind();
        Assert.Contains("result.Value is { } recovered", cs);
        Assert.DoesNotContain("HasRecoveredVault", cs);
        Assert.DoesNotContain("ViewModel.RecoveredVault", cs);
    }

    [Fact]
    public void RecoveredVaultDialog_UsesSafePresentationItems_InsteadOfRawCiphers()
    {
        var xaml = Xaml();
        var cs = CodeBehind();

        Assert.Contains("x:DataType=\"viewModels:RecoveredCipherListItem\"", xaml);
        Assert.Contains("Text=\"{x:Bind Name}\"", xaml);
        Assert.Contains("Text=\"{x:Bind Subtitle}\"", xaml);
        Assert.Contains("Select(RecoveredCipherListItem.FromCipher)", cs);
        Assert.Contains("ItemTemplate = (DataTemplate)Resources[\"RecoveredCipherItemTemplate\"]", cs);
        Assert.DoesNotContain("ItemsSource = recovered.Ciphers", cs);
    }

    [Fact]
    public void CodeBehind_RemoveAndReject_RequireConfirmationDialog()
    {
        var cs = CodeBehind();
        Assert.Contains("DialogHelper.ConfirmAsync(", cs);
    }

    // ── 接管密码不得残留：不写入 ViewModel,且始终在 finally 清空局部密码框 ──────

    [Fact]
    public void CodeBehind_TakeoverDoesNotStorePasswordOnViewModel()
    {
        var cs = CodeBehind();
        Assert.DoesNotContain("ViewModel.TakeoverNewPassword", cs);
    }

    [Fact]
    public void CodeBehind_TakeoverClearsPasswordBoxesInFinally()
    {
        var cs = CodeBehind();
        var startIdx = cs.IndexOf("ShowTakeoverDialogAsync", StringComparison.Ordinal);
        Assert.True(startIdx >= 0, "ShowTakeoverDialogAsync not found");
        var finallyIdx = cs.IndexOf("finally", startIdx, StringComparison.Ordinal);
        Assert.True(finallyIdx > startIdx, "finally block not found after ShowTakeoverDialogAsync");
        var tail = cs.Substring(finallyIdx, Math.Min(300, cs.Length - finallyIdx));
        Assert.Contains("pwBox.Password = string.Empty", tail);
        Assert.Contains("confirmBox.Password = string.Empty", tail);
    }

    [Fact]
    public void ViewModel_NoLongerExposesTakeoverPasswordOrSelectedIds()
    {
        var vmPath = FindFromOutput("src", "App", "ViewModels", "EmergencyAccessViewModel.cs");
        var cs = File.ReadAllText(vmPath);
        Assert.DoesNotContain("TakeoverNewPassword", cs);
        Assert.DoesNotContain("SelectedContactId", cs);
        Assert.DoesNotContain("SelectedGranteeId", cs);
        Assert.DoesNotContain("SelectedGrantedId", cs);
        Assert.DoesNotContain("SelectedGrantorEmail", cs);
        Assert.DoesNotContain("HasRecoveredVault", cs);
    }

    private static string FindFromOutput(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(path))
                return path;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find " + Path.Combine(segments));
    }
}
