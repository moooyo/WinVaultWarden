using Xunit;

namespace App.Tests;

public sealed class ClipboardSecuritySourceTests
{
    [Fact]
    public void ClipboardService_AllVaultWritesUseTheProtectedWriter()
    {
        var source = ReadAppSource("Services", "ClipboardService.cs");

        Assert.Contains(
            "public void SetText(string text) => SetVaultText(text, autoClearSeconds: null);",
            source);
        Assert.Contains("SetVaultText(text, autoClearSeconds);", source);
        Assert.Contains("IsAllowedInHistory = false", source);
        Assert.Contains("IsRoamable = false", source);
        Assert.Contains("AppPreferences.Current.ClearClipboardIndex", source);
    }

    [Fact]
    public void ClipboardService_ClearsOnlyTheLatestApplicationOwnedWrite()
    {
        var source = ReadAppSource("Services", "ClipboardService.cs");

        Assert.Contains("package.SetData(OwnershipFormat", source);
        Assert.Contains("ReferenceEquals(_clearTimer, timer)", source);
        Assert.Contains("ReferenceEquals(_latestWrite, write)", source);
        Assert.Contains("current.Contains(OwnershipFormat)", source);
        Assert.Contains("ClearSecretsNow() => ClearLatestOwned", source);

        var comparison = source.IndexOf("current.Contains(OwnershipFormat)", StringComparison.Ordinal);
        var clear = source.IndexOf("Clipboard.Clear()", comparison, StringComparison.Ordinal);
        Assert.True(comparison >= 0 && clear > comparison);
    }

    [Fact]
    public void ClipboardService_ProcessRestartStillClearsOwnedClipboardContent()
    {
        var source = ReadAppSource("Services", "ClipboardService.cs");
        var methodStart = source.IndexOf(
            "private void ClearLatestOwned",
            StringComparison.Ordinal);
        var nextMethod = source.IndexOf(
            "private void StopTimerNoLock",
            methodStart,
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && nextMethod > methodStart);
        var method = source[methodStart..nextMethod];
        Assert.Contains("TryClearIfOwnedNoLock(operation)", method);
        Assert.DoesNotContain("_latestWrite is not null", method);
        Assert.DoesNotContain("write is not null", method);
    }

    [Fact]
    public void Startup_ClearsOwnedClipboardBeforeAccountDeletionRecovery()
    {
        var source = ReadAppSource("App.xaml.cs");
        var clear = source.IndexOf(
            "GetRequiredService<IClipboardService>().ClearSecretsNow()",
            StringComparison.Ordinal);
        var recovery = source.IndexOf(
            "GetRequiredService<AccountDeletionRecoveryHost>()",
            StringComparison.Ordinal);
        var window = source.IndexOf("new MainWindow()", StringComparison.Ordinal);

        Assert.True(clear >= 0 && recovery > clear && window > recovery);
        Assert.Contains("ClipboardStartupClear", source[clear..recovery]);
        Assert.Contains("catch", source[clear..recovery]);
    }

    [Theory]
    [InlineData("Controls", "FieldRow.xaml.cs")]
    [InlineData("Controls", "TotpField.xaml.cs")]
    public void ClipboardFallback_DisablesHistoryAndRoaming(string folder, string file)
    {
        var source = ReadAppSource(folder, file);

        Assert.Contains("Clipboard.SetContentWithOptions", source);
        Assert.Contains("IsAllowedInHistory = false", source);
        Assert.Contains("IsRoamable = false", source);
        Assert.DoesNotContain("Clipboard.SetContent(package)", source);
    }

    [Fact]
    public void FieldRow_ProtectsEveryVaultFieldRegardlessOfRevealState()
    {
        var source = ReadAppSource("Controls", "FieldRow.xaml.cs");
        var copyStart = source.IndexOf("private void OnCopy", StringComparison.Ordinal);
        var openStart = source.IndexOf("private async void OnOpen", copyStart, StringComparison.Ordinal);
        Assert.True(copyStart >= 0 && openStart > copyStart);

        var copyBlock = source[copyStart..openStart];
        Assert.Contains("clipboard.SetText(value)", copyBlock);
        Assert.DoesNotContain("if (IsSecret)", copyBlock);
    }

    [Fact]
    public void DiagnosticsCopy_RemainsOutsideTheVaultClipboardContract()
    {
        var source = ReadAppSource("Views", "SettingsPage.xaml.cs");

        Assert.Contains("AboutInfo.ToDiagnosticsText()", source);
        Assert.Contains("Clipboard.SetContent(package)", source);
        Assert.DoesNotContain("IClipboardService", source);
    }

    private static string ReadAppSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(
                [directory.FullName, "src", "App", .. parts]);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
