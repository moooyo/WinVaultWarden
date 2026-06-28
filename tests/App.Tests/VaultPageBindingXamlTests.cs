using System.Xml.Linq;
using Xunit;

namespace App.Tests;

public class VaultPageBindingXamlTests
{
    private static string Xaml()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "App", "Views", "VaultPage.xaml");
            if (File.Exists(path))
                return File.ReadAllText(path);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not find VaultPage.xaml from the test output directory.");
    }

    [Fact]
    public void No_classic_ElementName_Root_bindings_remain()
    {
        // 全量 AOT 要求 0 处反射 {Binding};VaultPage 是最后的留存点。
        Assert.DoesNotContain("{Binding ElementName=Root", Xaml());
    }
}
