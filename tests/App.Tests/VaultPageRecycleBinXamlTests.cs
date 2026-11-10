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
    public void TrashView_Has_WorkspaceHeader_EmptyButton_EmptyState()
    {
        var xaml = LoadText();
        Assert.Contains("删除的条目会显示在这里，可恢复或永久删除。", xaml);
        Assert.Contains("ViewModel.WorkspaceTitle", xaml);
        Assert.Contains("ViewModel.FilteredItems.Count", xaml);
        Assert.Contains("ViewModel.CanEmptyRecycleBin", xaml);
        Assert.Contains("OnEmptyRecycleBinClick", xaml);
        Assert.Contains("ViewModel.IsTrashEmpty", xaml);
        Assert.Contains("回收站是空的", xaml);
    }
}
