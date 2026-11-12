using App.Services;
using Xunit;

namespace App.Tests;

public sealed class StartupRegistrationServiceTests
{
    [Theory]
    [InlineData(@"C:\Apps\WinVaultWarden\App.exe", "\"C:\\Apps\\WinVaultWarden\\App.exe\" --startup")]
    [InlineData(@"D:\Apps With Spaces\App.EXE", "\"D:\\Apps With Spaces\\App.EXE\" --startup")]
    public void PortableCommand_QuotesOneExecutable(string path, string expected)
    {
        Assert.True(StartupRegistrationService.TryCreatePortableCommand(path, out var command));
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("App.exe")]
    [InlineData("C:App.exe")]
    [InlineData(@"\\server\share\App.exe")]
    [InlineData("C:\\Apps\\App.exe\" --extra \"")]
    [InlineData("C:\\Apps\\App.exe\n")]
    [InlineData(@"C:\Apps\script.cmd")]
    public void PortableCommand_RejectsAmbiguousOrInjectedCommands(string? path)
    {
        Assert.False(StartupRegistrationService.TryCreatePortableCommand(path, out var command));
        Assert.Equal(string.Empty, command);
    }

    [Fact]
    public void PortableCommand_EnforcesDocumentedRunValueLength()
    {
        var path = "C:\\" + new string('a', 250) + "\\App.exe";
        Assert.False(StartupRegistrationService.TryCreatePortableCommand(path, out _));
    }

    [Theory]
    [InlineData(StartupRegistrationState.Enabled, true, true)]
    [InlineData(StartupRegistrationState.Registered, true, true)]
    [InlineData(StartupRegistrationState.EnabledByPolicy, true, false)]
    [InlineData(StartupRegistrationState.Disabled, false, true)]
    [InlineData(StartupRegistrationState.DisabledByUser, false, true)]
    [InlineData(StartupRegistrationState.DisabledByPolicy, false, false)]
    [InlineData(StartupRegistrationState.Unavailable, false, false)]
    public void Result_ReportsOperatingSystemAuthority(
        StartupRegistrationState state, bool requested, bool canChange)
    {
        var result = new StartupRegistrationResult(state, "state");
        Assert.Equal(requested, result.IsRequestedEnabled);
        Assert.Equal(canChange, result.CanChange);
    }

    [Fact]
    public async Task SetEnabled_ReturnsDenialWithoutInventingSuccess()
    {
        var backend = new RecordingBackend
        {
            Next = new(StartupRegistrationState.DisabledByUser, "Windows denied the request."),
        };
        var service = new StartupRegistrationService(backend);
        var result = await service.SetEnabledAsync(true);
        Assert.Equal(StartupRegistrationState.DisabledByUser, result.State);
        Assert.False(result.IsRequestedEnabled);
        Assert.Equal(new[] { true }, backend.Changes);
    }

    [Fact]
    public async Task CancelledRequest_DoesNotMutateTheBackend()
    {
        var backend = new RecordingBackend();
        var service = new StartupRegistrationService(backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SetEnabledAsync(true, cancellation.Token));
        Assert.Empty(backend.Changes);
    }

    [Fact]
    public async Task ConcurrentChanges_AreSerialized()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new RecordingBackend
        {
            BeforeChange = async enabled =>
            {
                if (!enabled)
                    return;
                entered.SetResult();
                await gate.Task;
            },
        };
        var service = new StartupRegistrationService(backend);
        var first = service.SetEnabledAsync(true);
        await entered.Task;
        var second = service.SetEnabledAsync(false);
        Assert.Equal(new[] { true }, backend.Changes);
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(new[] { true, false }, backend.Changes);
    }

    private sealed class RecordingBackend : IStartupRegistrationBackend
    {
        public List<bool> Changes { get; } = [];
        public Func<bool, Task>? BeforeChange { get; init; }
        public StartupRegistrationResult Next { get; init; } =
            new(StartupRegistrationState.Disabled, "Disabled.");

        public Task<StartupRegistrationResult> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Next);

        public async Task<StartupRegistrationResult> SetEnabledAsync(
            bool enabled, CancellationToken cancellationToken)
        {
            Changes.Add(enabled);
            if (BeforeChange is not null)
                await BeforeChange(enabled);
            return Next;
        }
    }
}
