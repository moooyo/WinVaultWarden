using App.Services;
using Xunit;

namespace App.Tests;

public sealed class PreparedAppInstallerRetentionTests
{
    [Theory]
    [InlineData(-8, true)]
    [InlineData(-7, true)]
    [InlineData(-6, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void SuccessfullyLaunchedMetadata_IsRetainedForSevenDays(int age, bool expected)
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, PreparedAppInstallerRetention.CanRemove(
            "WinVaultWarden-00000000000000000000000000000001.appinstaller", now.AddDays(age), now));
    }

    [Theory]
    [InlineData("OtherApp-00000000000000000000000000000001.appinstaller")]
    [InlineData("WinVaultWarden-x64.appinstaller")]
    [InlineData("WinVaultWarden-00000000000000000000000000000001.exe")]
    [InlineData("WinVaultWarden-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.appinstaller")]
    [InlineData("../WinVaultWarden-00000000000000000000000000000001.appinstaller")]
    public void UnrelatedNames_AreNeverEligibleForCleanup(string name)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(PreparedAppInstallerRetention.CanRemove(name, now.AddYears(-1), now));
    }
}
