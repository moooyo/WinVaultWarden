using Xunit;

namespace App.Tests;

public class VaultPageRecycleBinXamlTests
{
    private static string LoadText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("VaultPage.xaml not found.");
    }

    [Fact]
    public void TrashView_Has_InfoBar_Header_EmptyButton_EmptyState()
    {
        var xaml = LoadText();
        Assert.Contains("回收站中的项目不计入密码库", xaml);
        Assert.Contains("ViewModel.TrashHeader", xaml);
        Assert.Contains("ViewModel.CanEmptyRecycleBin", xaml);
        Assert.Contains("OnEmptyRecycleBinClick", xaml);
        Assert.Contains("ViewModel.IsTrashEmpty", xaml);
        Assert.Contains("回收站为空", xaml);
    }
}
