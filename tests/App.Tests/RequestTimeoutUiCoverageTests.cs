using System.Text.RegularExpressions;
using Xunit;

namespace App.Tests;

public sealed class RequestTimeoutUiCoverageTests
{
    private static readonly Regex UnfilteredOperationCancellation = new(
        @"catch\s*\(\s*OperationCanceledException(?:\s+\w+)?\s*\)\s*\{",
        RegexOptions.CultureInvariant);

    [Fact]
    public void ViewModels_DoNotClassifyOperationCanceledExceptionWithoutTokenGuard()
    {
        var directory = RepoDirectory("src", "App", "ViewModels");

        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var source = File.ReadAllText(file);
            Assert.DoesNotMatch(UnfilteredOperationCancellation, source);
        }
    }

    [Fact]
    public void AppCode_LeavesUnfilteredCancellationOnlyInBackgroundPipeLoop()
    {
        var directory = RepoDirectory("src", "App");
        var offenders = Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => UnfilteredOperationCancellation.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetFileName(file)!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["PasskeyBridgeServer.cs"], offenders);
    }

    [Fact]
    public void NetworkFacingUiSurfaces_KeepTimeoutClassificationExplicit()
    {
        foreach (var file in new[]
        {
            RepoFile("src", "App", "ViewModels", "DevicesViewModel.cs"),
            RepoFile("src", "App", "ViewModels", "EmergencyAccessViewModel.cs"),
            RepoFile("src", "App", "ViewModels", "SettingsViewModel.cs"),
            RepoFile("src", "App", "ViewModels", "SendViewModel.cs"),
            RepoFile("src", "App", "ViewModels", "ImportExportViewModel.cs"),
            RepoFile("src", "App", "Services", "ItemRepromptGate.cs"),
        })
        {
            var source = File.ReadAllText(file);
            Assert.Contains("RequestTimeoutClassifier.IsTimeout", source, StringComparison.Ordinal);
            Assert.Contains("UiExceptionPolicy.RequestTimeoutMessage", source, StringComparison.Ordinal);
        }
    }

    private static string RepoFile(params string[] parts) =>
        Path.Combine([RepoRoot(), .. parts]);

    private static string RepoDirectory(params string[] parts) =>
        Path.Combine([RepoRoot(), .. parts]);

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinVaultWarden.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
