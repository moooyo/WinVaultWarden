using Xunit;

namespace App.Tests;

public sealed class TrackedTransientUiSourceTests
{
    [Fact]
    public void ContentDialogs_AreOnlyShownByDialogHelper()
    {
        var offenders = Sources()
            .Where(file => !file.EndsWith("DialogHelper.cs", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains(".ShowAsync(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Pickers_AreOnlyAwaitedByFilePickerHelper()
    {
        var offenders = Sources()
            .Where(file => !file.EndsWith("FilePickerHelper.cs", StringComparison.Ordinal))
            .Where(file =>
            {
                var source = File.ReadAllText(file);
                return source.Contains(".PickSingleFileAsync(", StringComparison.Ordinal)
                    || source.Contains(".PickSaveFileAsync(", StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void GeneratorHistory_ClosesBeforeHostConfirmation()
    {
        var history = Read("Views", "GeneratorHistoryDialog.xaml.cs");
        var window = Read("MainWindow.xaml.cs");
        Assert.Contains("ClearRequested = true", history);
        Assert.DoesNotContain("DialogHelper.ConfirmAsync", history);
        Assert.Contains("history.ClearRequested", window);
        Assert.Contains("DialogHelper.ConfirmAsync", window);
    }

    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(AppDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase));

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([AppDirectory(), .. parts]));

    private static string AppDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "App");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("src\\App");
    }
}
