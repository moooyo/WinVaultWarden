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
        public bool IsHelloSet => Set;
        public void SetHello(byte[] signature, byte[] challenge) { Set = true; LastSig = signature; LastChallenge = challenge; }
        public void ClearHello() => Set = false;
        public byte[]? LoadChallenge() => Set ? new byte[32] : null;
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

        vm.DisableWindowsHello();

        Assert.False(vm.CanToggleWindowsHello);
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
