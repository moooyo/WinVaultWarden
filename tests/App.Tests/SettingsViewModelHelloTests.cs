using Core.Services;
using App.ViewModels;
using Xunit;

namespace App.Tests;

// ============================================================================
// Windows Hello 解锁开关 SettingsViewModel 测试（Task H6 TDD RED 先写）
// ============================================================================

[Collection(TestCollectionNames.AppPreferences)]
public class SettingsViewModelHelloTests : AppPreferencesTestBase
{
    private sealed class FakeHello : Core.Abstractions.IWindowsHelloAuthenticator
    {
        public bool Available { get; set; } = true;
        public byte[]? CreateResult { get; set; }
        public byte[]? GetResult { get; set; }
        public int CreateCalls { get; private set; }
        public Task<bool> IsAvailableAsync() => Task.FromResult(Available);
        public Task<byte[]?> CreateProtectorAsync(byte[] challenge, System.Threading.CancellationToken ct = default) { CreateCalls++; return Task.FromResult(CreateResult); }
        public Task<byte[]?> GetProtectorAsync(byte[] challenge, System.Threading.CancellationToken ct = default) => Task.FromResult(GetResult);
    }

    private sealed class FakeHelloUnlock : Core.Services.IHelloUnlockService
    {
        public bool Set; public byte[]? LastSig; public byte[]? LastChallenge;
        public bool ThrowOnSet { get; set; }
        public bool IsHelloSet => Set;
        public void SetHello(byte[] signature, byte[] challenge)
        {
            if (ThrowOnSet)
                throw new InvalidOperationException("The account key format is not supported.");
            Set = true; LastSig = signature; LastChallenge = challenge;
        }
        public void ClearHello() => Set = false;
        public byte[]? LoadChallenge() => Set ? new byte[32] : null;
    }

    [Fact]
    public async Task V2Account_DisablesHelloBeforeRequestingAnOperatingSystemGesture()
    {
        var hello = new FakeHello { Available = true, CreateResult = new byte[256] };
        var unlock = new FakeHelloUnlock();
        var vm = new SettingsViewModel(hello, unlock, supportsLocalUnlock: () => false);
        await vm.RefreshWindowsHelloAvailabilityAsync();

        Assert.False(vm.CanToggleWindowsHello);
        Assert.Contains("V2", vm.WindowsHelloDescription, StringComparison.Ordinal);
        Assert.False(await vm.EnableWindowsHelloAsync());
        Assert.Equal(0, hello.CreateCalls);
        Assert.False(unlock.Set);
    }

    [Fact]
    public async Task EnableWindowsHello_WhenGestureSucceeds_SetsHello()
    {
        var hello = new FakeHello { CreateResult = Enumerable.Repeat((byte)7, 256).ToArray() };
        var unlock = new FakeHelloUnlock();
        var vm = new SettingsViewModel(hello, unlock);

        var ok = await vm.EnableWindowsHelloAsync();

        Assert.True(ok);
        Assert.True(unlock.Set);
        Assert.Equal(1, hello.CreateCalls);
        Assert.NotNull(unlock.LastChallenge);
        Assert.Equal(32, unlock.LastChallenge!.Length);
    }

    [Fact]
    public async Task EnableWindowsHello_WhenProtectedCommitRejects_DoesNotBindAndClearsSignature()
    {
        var signature = Enumerable.Repeat((byte)7, 256).ToArray();
        var hello = new FakeHello { CreateResult = signature };
        var unlock = new FakeHelloUnlock();
        var vm = new SettingsViewModel(hello, unlock);
        var attemptedCommit = false;

        var enabled = await vm.EnableWindowsHelloAsync(_ =>
        {
            attemptedCommit = true;
            return false;
        });

        Assert.True(attemptedCommit);
        Assert.False(enabled);
        Assert.False(unlock.Set);
        Assert.All(signature, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task EnableWindowsHello_WhenSessionCancels_DoesNotCommit()
    {
        var hello = new FakeHello { CreateResult = Enumerable.Repeat((byte)9, 256).ToArray() };
        var unlock = new FakeHelloUnlock();
        var vm = new SettingsViewModel(hello, unlock);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.False(await vm.EnableWindowsHelloAsync(action =>
        {
            action();
            return true;
        }, cancellation.Token));
        Assert.False(unlock.Set);
    }

    [Fact]
    public async Task EnableWindowsHello_WhenProtectorCannotStore_ReturnsSafeFailure()
    {
        var signature = Enumerable.Repeat((byte)5, 256).ToArray();
        var unlock = new FakeHelloUnlock { ThrowOnSet = true };
        var vm = new SettingsViewModel(new FakeHello { CreateResult = signature }, unlock);

        Assert.False(await vm.EnableWindowsHelloAsync(action =>
        {
            action();
            return true;
        }));
        Assert.False(unlock.Set);
        Assert.All(signature, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task EnableWindowsHello_WhenGestureCancelled_DoesNotSet()
    {
        var hello = new FakeHello { CreateResult = null }; // 取消/失败
        var unlock = new FakeHelloUnlock();
        var vm = new SettingsViewModel(hello, unlock);

        Assert.False(await vm.EnableWindowsHelloAsync());
        Assert.False(unlock.Set);
    }

    [Fact]
    public void DisableWindowsHello_ClearsHello()
    {
        var unlock = new FakeHelloUnlock { Set = true };
        var vm = new SettingsViewModel(new FakeHello(), unlock);

        vm.DisableWindowsHello();

        Assert.False(unlock.Set);
    }

    [Fact]
    public void CanToggleWindowsHello_WhenUnavailableButConfigured_AllowsDisable()
    {
        var unlock = new FakeHelloUnlock { Set = true };
        var vm = new SettingsViewModel(
            new FakeHello { Available = false },
            unlock);

        Assert.True(vm.CanToggleWindowsHello);
        Assert.Contains("仍可关闭", vm.WindowsHelloDescription, StringComparison.Ordinal);

        vm.DisableWindowsHello();

        Assert.False(vm.CanToggleWindowsHello);
        Assert.Contains("Windows 设置", vm.WindowsHelloDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanToggleWindowsHello_WhenAvailabilityRefreshes_TracksAvailability()
    {
        var hello = new FakeHello { Available = true };
        var vm = new SettingsViewModel(hello, new FakeHelloUnlock());

        Assert.False(vm.CanToggleWindowsHello);

        await vm.RefreshWindowsHelloAvailabilityAsync();

        Assert.True(vm.CanToggleWindowsHello);
    }
}
