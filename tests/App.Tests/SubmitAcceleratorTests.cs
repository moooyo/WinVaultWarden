using Xunit;

namespace App.Tests;

public sealed class SubmitAcceleratorTests
{
    [Fact]
    public void LoginSubmitAccelerator_IsAsyncVoid_AndAwaitsLoginCommand()
    {
        var src = ReadSource("src", "App", "Views", "LoginPage.xaml.cs");

        Assert.Contains("private async void OnSubmitAccelerator", src);
        Assert.Contains("args.Handled = true;", src);
        Assert.Contains("await ViewModel.LoginCommand.ExecuteAsync(null);", src);
        Assert.DoesNotContain("_ = ViewModel.LoginCommand.ExecuteAsync(null);", src);
    }

    [Fact]
    public void RegisterSubmitAccelerator_IsAsyncVoid_AndAwaitsSubmitAsync()
    {
        var src = ReadSource("src", "App", "Views", "RegisterPage.xaml.cs");

        Assert.Contains("private async void OnSubmitAccelerator", src);
        Assert.Contains("args.Handled = true;", src);
        Assert.Contains("await SubmitAsync();", src);
        Assert.DoesNotContain("_ = SubmitAsync();", src);
    }

    private static string ReadSource(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(new[] { dir.FullName }.Concat(relativeParts).ToArray());
            if (File.Exists(path))
                return File.ReadAllText(path);
            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join('/', relativeParts));
    }
}
