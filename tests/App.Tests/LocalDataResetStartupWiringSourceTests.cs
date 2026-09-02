using Xunit;

namespace App.Tests;

public sealed class LocalDataResetStartupWiringSourceTests
{
    [Fact]
    public void Program_PreparesResetAfterRedirectionAndBeforeApplicationStart()
    {
        var source = Read("src", "App", "Program.cs");
        var winRt = source.IndexOf(
            "WinRT.ComWrappersSupport.InitializeComWrappers()",
            StringComparison.Ordinal);
        var redirection = source.IndexOf("if (DecideRedirection())", StringComparison.Ordinal);
        var prepare = source.IndexOf(".PrepareForStartup()", StringComparison.Ordinal);
        var dataLease = source.IndexOf(
            "using var dataLease = startup.DataLease!;",
            StringComparison.Ordinal);
        var applicationStart = source.IndexOf("Application.Start(", StringComparison.Ordinal);
        var appConstruction = source.IndexOf("new global::App.App()", StringComparison.Ordinal);

        Assert.True(winRt >= 0);
        Assert.True(redirection > winRt);
        Assert.True(prepare > redirection);
        Assert.True(dataLease > prepare);
        Assert.True(applicationStart > dataLease);
        Assert.True(appConstruction > applicationStart);
        Assert.Equal(1, Count(source, "new global::App.App()"));
    }

    [Fact]
    public void Program_FailsClosedWithoutConstructingApplicationServices()
    {
        var source = Read("src", "App", "Program.cs");
        var failureBranch = source.IndexOf("if (!startup.CanStart)", StringComparison.Ordinal);
        var failureReturn = source.IndexOf(
            "return StartupFailureExitCode;",
            failureBranch,
            StringComparison.Ordinal);
        var applicationStart = source.IndexOf("Application.Start(", StringComparison.Ordinal);
        var appConstruction = source.IndexOf("new global::App.App()", StringComparison.Ordinal);

        Assert.True(failureBranch >= 0);
        Assert.True(failureReturn > failureBranch && failureReturn < applicationStart);
        Assert.True(appConstruction > applicationStart);
        Assert.Contains("MessageBoxW(", source, StringComparison.Ordinal);
        Assert.Contains("StartupFailureExitCode = 2", source, StringComparison.Ordinal);
        Assert.Contains("LocalDataResetStartupStatus.Contended", source, StringComparison.Ordinal);
        Assert.Contains("LocalDataResetStartupStatus.Corrupt", source, StringComparison.Ordinal);
        Assert.Contains("LocalDataResetStartupStatus.Ambiguous", source, StringComparison.Ordinal);
        Assert.Contains("LocalDataResetStartupStatus.UnsafePath", source, StringComparison.Ordinal);
        Assert.Contains("StartupUnavailableMessage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("startup.Status.ToString", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_OffersEmergencyRecoveryOnlyForPortableCorruptOrAmbiguousStartup()
    {
        var source = Read("src", "App", "Program.cs");
        var main = Section(
            source,
            "private static int Main(string[] args)",
            "private static void ShowStartupFailure");
        var eligible = Section(
            source,
            "private static bool IsEmergencyRecoveryEligible",
            "private static bool TryRecoverPortableLocalData");

        Assert.Contains("LocalDataResetStartupStatus.Corrupt", eligible, StringComparison.Ordinal);
        Assert.Contains("LocalDataResetStartupStatus.Ambiguous", eligible, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalDataResetStartupStatus.Contended", eligible, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalDataResetStartupStatus.Unavailable", eligible, StringComparison.Ordinal);
        Assert.DoesNotContain("LocalDataResetStartupStatus.UnsafePath", eligible, StringComparison.Ordinal);

        var firstPrepare = main.IndexOf("resetCoordinator.PrepareForStartup()", StringComparison.Ordinal);
        var eligibility = main.IndexOf("IsEmergencyRecoveryEligible(startup.Status)", StringComparison.Ordinal);
        var packaged = main.IndexOf("if (!isExplicitlyPortable)", StringComparison.Ordinal);
        var recovery = main.IndexOf("TryRecoverPortableLocalData(", StringComparison.Ordinal);
        var lease = main.IndexOf("using var dataLease = startup.DataLease!;", StringComparison.Ordinal);
        var app = main.IndexOf("Application.Start(", StringComparison.Ordinal);
        Assert.True(firstPrepare >= 0 && eligibility > firstPrepare);
        Assert.True(packaged > eligibility && recovery > packaged);
        Assert.True(lease > recovery && app > lease);
        Assert.Contains("PackagedRecoveryMessage", main[packaged..recovery], StringComparison.Ordinal);
        Assert.Contains("return StartupFailureExitCode;", main[packaged..recovery], StringComparison.Ordinal);
        Assert.DoesNotContain("AboutInfo.IsPackaged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_RequiresTwoDefaultNoConfirmationsBeforeEmergencyRequest()
    {
        var source = Read("src", "App", "Program.cs");
        var recover = Section(
            source,
            "private static bool TryRecoverPortableLocalData",
            "private static bool ConfirmEmergencyRecovery");
        var confirm = Section(
            source,
            "private static bool ConfirmEmergencyRecovery",
            "private static void ShowEmergencyRequestFailure");

        Assert.True(
            recover.IndexOf("if (!ConfirmEmergencyRecovery())", StringComparison.Ordinal)
            < recover.IndexOf("coordinator.RequestEmergencyReset(observedStatus)", StringComparison.Ordinal));
        Assert.Equal(2, Count(confirm, "MessageBoxW("));
        Assert.Contains("MessageBoxYesNo", confirm, StringComparison.Ordinal);
        Assert.Contains("MessageBoxDefaultButton2", confirm, StringComparison.Ordinal);
        Assert.Contains("MessageBoxTaskModal", confirm, StringComparison.Ordinal);
        Assert.Contains("MessageBoxSetForeground", confirm, StringComparison.Ordinal);
        Assert.Contains("flags | MessageBoxIconWarning", confirm, StringComparison.Ordinal);
        Assert.Contains("flags | MessageBoxIconError", confirm, StringComparison.Ordinal);
        Assert.Contains("if (first != MessageBoxResultYes)", confirm, StringComparison.Ordinal);
        Assert.Contains("return second == MessageBoxResultYes;", confirm, StringComparison.Ordinal);
        Assert.Contains("不会修改任何数据", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_RetriesStartupOnlyForVerifiedEmergencyRequests()
    {
        var source = Read("src", "App", "Program.cs");
        var recover = Section(
            source,
            "private static bool TryRecoverPortableLocalData",
            "private static bool ConfirmEmergencyRecovery");
        var verifiedStart = recover.IndexOf(
            "case LocalDataEmergencyResetRequestStatus.Requested:",
            StringComparison.Ordinal);
        var uncertainStart = recover.IndexOf(
            "case LocalDataEmergencyResetRequestStatus.CommitOutcomeUncertain:",
            StringComparison.Ordinal);
        var defaultStart = recover.IndexOf("default:", uncertainStart, StringComparison.Ordinal);

        Assert.True(verifiedStart >= 0 && uncertainStart > verifiedStart);
        var verified = recover[verifiedStart..uncertainStart];
        var uncertain = recover[uncertainStart..defaultStart];
        Assert.Contains(
            "case LocalDataEmergencyResetRequestStatus.AlreadyPending:",
            verified,
            StringComparison.Ordinal);
        Assert.Equal(1, Count(verified, "coordinator.PrepareForStartup()"));
        Assert.DoesNotContain("coordinator.PrepareForStartup()", uncertain, StringComparison.Ordinal);
        Assert.Contains("RecoveryCommitUncertainMessage", uncertain, StringComparison.Ordinal);
        Assert.Equal(1, Count(recover, "RequestEmergencyReset("));
    }

    [Fact]
    public void Program_RecoveryMessagesDeclareExactDestructiveScope()
    {
        var source = Read("src", "App", "Program.cs");

        Assert.Contains("保存账户", source, StringComparison.Ordinal);
        Assert.Contains("离线密码库", source, StringComparison.Ordinal);
        Assert.Contains("设备密钥", source, StringComparison.Ordinal);
        Assert.Contains("应用偏好", source, StringComparison.Ordinal);
        Assert.Contains("永久删除隔离记录", source, StringComparison.Ordinal);
        Assert.Contains("Windows Hello 凭据", source, StringComparison.Ordinal);
        Assert.Contains("便携版浏览器注册", source, StringComparison.Ordinal);
        Assert.Contains("服务器账户和服务器密码库", source, StringComparison.Ordinal);
        Assert.Contains("程序目录及用户导出文件不受影响", source, StringComparison.Ordinal);
        Assert.Contains("Windows 设置", source, StringComparison.Ordinal);
        Assert.Contains("下次启动会继续安全检查", source, StringComparison.Ordinal);
        Assert.Contains("UnknownPackageIdentityMessage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_EmergencyFailuresUseSanitizedStatusMessages()
    {
        var source = Read("src", "App", "Program.cs");
        var failure = Section(
            source,
            "private static void ShowEmergencyRequestFailure",
            "private static void ShowMessage");

        Assert.Contains("LocalDataEmergencyResetRequestStatus.NotAllowed", failure, StringComparison.Ordinal);
        Assert.Contains("LocalDataEmergencyResetRequestStatus.Contended", failure, StringComparison.Ordinal);
        Assert.Contains("LocalDataEmergencyResetRequestStatus.Corrupt", failure, StringComparison.Ordinal);
        Assert.Contains("LocalDataEmergencyResetRequestStatus.UnsafePath", failure, StringComparison.Ordinal);
        Assert.Contains("LocalDataEmergencyResetRequestStatus.Unavailable", failure, StringComparison.Ordinal);
        Assert.Contains("无法安全创建本地恢复请求", failure, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%", failure, StringComparison.Ordinal);
        Assert.Contains("人工检查", failure, StringComparison.Ordinal);
        Assert.Contains("本地恢复状态无法确认", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("exception.Message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("request.Status.ToString", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_PermitsRecoveryOnlyForExplicitlyUnpackagedIdentity()
    {
        var source = Read("src", "App", "Program.cs");
        var identity = Section(
            source,
            "private static PackageIdentityStatus GetPackageIdentityStatus",
            "private static bool DecideRedirection");

        Assert.Contains("GetCurrentPackageFullName(", identity, StringComparison.Ordinal);
        Assert.Contains("ErrorInsufficientBuffer", identity, StringComparison.Ordinal);
        Assert.Contains("AppModelErrorNoPackage", identity, StringComparison.Ordinal);
        Assert.Contains("AppModelErrorNoPackage = 15700", source, StringComparison.Ordinal);
        Assert.Contains("PackageIdentityStatus.Packaged", identity, StringComparison.Ordinal);
        Assert.Contains("PackageIdentityStatus.Unpackaged", identity, StringComparison.Ordinal);
        Assert.Contains("PackageIdentityStatus.Unknown", identity, StringComparison.Ordinal);
        Assert.Contains("return PackageIdentityStatus.Unknown;", identity, StringComparison.Ordinal);
        Assert.Contains(
            "[LibraryImport(\"kernel32.dll\", EntryPoint = \"GetCurrentPackageFullName\")]",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Program_ActivationRedirectionAlwaysSignalsAndPropagatesWorkerFailure()
    {
        var source = Read("src", "App", "Program.cs");
        var start = source.IndexOf(
            "private static void RedirectActivationTo(",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "private static partial uint CoWaitForMultipleObjects",
            start,
            StringComparison.Ordinal);
        var redirect = source[start..end];

        Assert.Contains("using var redirectEvent = new EventWaitHandle(", redirect, StringComparison.Ordinal);
        Assert.Contains("finally", redirect, StringComparison.Ordinal);
        Assert.Contains("redirectEvent.Set();", redirect, StringComparison.Ordinal);
        Assert.Contains("if (waitResult != 0)", redirect, StringComparison.Ordinal);
        Assert.Contains("redirectTask.GetAwaiter().GetResult();", redirect, StringComparison.Ordinal);
        Assert.DoesNotContain(".AsTask().Wait()", redirect, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsExternalCleaner_DeletesHelloAndExactPortableHostRegistrations()
    {
        var program = Read("src", "App", "Program.cs");
        var hello = Read("src", "App", "Services", "WindowsHelloAuthenticator.cs");
        var cleaner = Read(
            "src",
            "App",
            "Services",
            "WindowsLocalDataResetExternalCleaner.cs");

        Assert.Contains(
            "internal const string CredentialName = \"WinVaultWarden.HelloUnlock\"",
            hello,
            StringComparison.Ordinal);
        Assert.Contains(
            "KeyCredentialManager.DeleteAsync(WindowsHelloAuthenticator.CredentialName)",
            cleaner,
            StringComparison.Ordinal);
        Assert.Contains(".GetAwaiter()", cleaner, StringComparison.Ordinal);
        Assert.DoesNotContain("IsSupportedAsync", cleaner, StringComparison.Ordinal);
        Assert.Contains(
            @"Software\Google\Chrome\NativeMessagingHosts\com.winvaultwarden.browser",
            cleaner,
            StringComparison.Ordinal);
        Assert.Contains(
            @"Software\Microsoft\Edge\NativeMessagingHosts\com.winvaultwarden.browser",
            cleaner,
            StringComparison.Ordinal);
        Assert.Contains("Registry.CurrentUser.DeleteSubKey(", cleaner, StringComparison.Ordinal);
        Assert.Contains("throwOnMissingSubKey: false", cleaner, StringComparison.Ordinal);
        Assert.Equal(3, Count(cleaner, "DeletePortableNativeMessagingRegistration("));
        Assert.Equal(2, Count(cleaner, "ChromeNativeMessagingHostRegistryPath"));
        Assert.Equal(2, Count(cleaner, "EdgeNativeMessagingHostRegistryPath"));
        Assert.DoesNotContain("DeleteSubKeyTree", cleaner, StringComparison.Ordinal);
        Assert.DoesNotContain("reg.exe", cleaner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Process.Start", cleaner, StringComparison.Ordinal);
        Assert.DoesNotContain("catch", cleaner, StringComparison.Ordinal);
        Assert.Contains("new WindowsLocalDataResetExternalCleaner()", program, StringComparison.Ordinal);
        Assert.Contains("externalCleaner.Clean", program, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionExitHookRejectsMissingMainWindowInsteadOfSilentlyReturning()
    {
        var services = Read("src", "App", "Services", "ServiceConfiguration.cs");

        Assert.Contains(
            "var window = global::App.App.MainWindow",
            services,
            StringComparison.Ordinal);
        Assert.Contains(
            "?? throw new InvalidOperationException(",
            services,
            StringComparison.Ordinal);
        Assert.Contains("window.ExitAfterLifecycle();", services, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MainWindow?.ExitAfterLifecycle()",
            services,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowExitUsesHardProcessTerminationFallback()
    {
        var source = Read("src", "App", "MainWindow.xaml.cs");
        var start = source.IndexOf(
            "public void ExitAfterLifecycle()",
            StringComparison.Ordinal);
        var end = source.IndexOf(
            "public bool ShowVault(",
            start,
            StringComparison.Ordinal);
        var exit = source[start..end];

        Assert.Contains("Application.Current.Exit();", exit, StringComparison.Ordinal);
        Assert.Contains("finally", exit, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(0);", exit, StringComparison.Ordinal);
        Assert.True(
            exit.IndexOf("Environment.Exit(0);", StringComparison.Ordinal)
            > exit.IndexOf("Application.Current.Exit();", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalResetPinsCriticalDirectoriesAndVerifiesNoFollowHandles()
    {
        var source = Read("src", "App", "Services", "LocalDataResetService.cs")
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains("CreateFileW(", source, StringComparison.Ordinal);
        Assert.Contains("GetFileInformationByHandle(", source, StringComparison.Ordinal);
        Assert.Contains("FileFlagOpenReparsePoint", source, StringComparison.Ordinal);
        Assert.Contains("FileFlagBackupSemantics", source, StringComparison.Ordinal);
        Assert.Contains(
            "FileListDirectory | FileReadAttributes",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FileAttributeNormal | FileFlagOpenReparsePoint",
            source,
            StringComparison.Ordinal);
        Assert.Contains("FileShareDelete", source, StringComparison.Ordinal);
        Assert.Contains(
            "var share = FileShareRead | FileShareWrite;\n"
                + "        if (allowDeleteShare)\n"
                + "            share |= FileShareDelete;",
            source,
            StringComparison.Ordinal);
        Assert.Contains("OpenVerifiedLockFile(", source, StringComparison.Ordinal);
        Assert.Contains("OpenPinnedControlLock(", source, StringComparison.Ordinal);
        Assert.Contains("sharedLease.Attach(rootPin);", source, StringComparison.Ordinal);
        Assert.Contains(
            "using var localDataPin = LocalDataResetFileSystem.PinDirectory(\n"
                + "            _layout.LocalApplicationDataDirectory);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("allowDeleteShare: true", source, StringComparison.Ordinal);
        Assert.Contains("sourceIdentity: rootPin.Identity", source, StringComparison.Ordinal);
        var trashDeletion = source.IndexOf(
            "DeleteDirectoryWithoutFollowingReparsePoints(trashPath, trashPin);",
            StringComparison.Ordinal);
        Assert.True(trashDeletion >= 0);
        var identityCheck = source.LastIndexOf(
            "if (!MoveIntentSourceMatches(current, trashPin))",
            trashDeletion,
            StringComparison.Ordinal);
        var durableIntentCheck = source.LastIndexOf(
            "EnsureMoveIntentEquals(current);",
            trashDeletion,
            StringComparison.Ordinal);
        Assert.True(
            identityCheck >= 0
                && durableIntentCheck > identityCheck
                && trashDeletion > durableIntentCheck);
        Assert.Contains(
            "using var tempPin = LocalDataResetFileSystem.PinDirectory(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "using var parentPin = LocalDataResetFileSystem.PinDirectory(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(path, recursive: false);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete(path, recursive: true);", source, StringComparison.Ordinal);
    }

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;

    private static string Section(string source, string startToken, string endToken)
    {
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        var end = source.IndexOf(endToken, start + startToken.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    private static string Read(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }
}
