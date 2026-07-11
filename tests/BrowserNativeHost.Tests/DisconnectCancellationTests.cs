using Core.Passkeys;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BrowserNativeHost.Tests;

// Task 7: a native-messaging disconnect (stdin EOF) is the browser walking away. The persistent
// session must cancel every in-flight App request AND stay silent — no late deadline/response frame
// may be written after the peer is gone. (The extension side maps the vanished port to
// operation_aborted; the host itself must not emit a spurious terminal frame.)
public class DisconnectCancellationTests
{
    private const string Id23 = "00000000000000000000000000000023";

    [Fact]
    public async Task NativeInputEof_CancelsActiveAppRequest()
    {
        var bridge = new CancellationObservingBridge();
        await using var fixture = await SessionFixture.StartAsync(bridge);
        await fixture.SendAsync(NativeMessagingSessionTests.Request(Id23, TimeSpan.FromSeconds(60)));
        await bridge.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await fixture.DisconnectInputAsync();

        await bridge.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Empty(fixture.ResponsesFor(Id23));
    }

    // Blocks in SendAsync until the request token is cancelled, then surfaces the cancellation. It
    // records that its token was cancelled so the test can prove the disconnect propagated upstream.
    private sealed class CancellationObservingBridge : IAppPasskeyBridgeClient
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<BrowserPasskeyResponse> SendAsync(
            BrowserPasskeyRequest request, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = ct.Register(() =>
            {
                Cancelled.TrySetResult();
                blocked.TrySetCanceled(ct);
            });
            await blocked.Task.ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }

        public Task CancelAsync(string requestId, CancellationToken ct = default)
        {
            Cancelled.TrySetResult();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Drives a real NativeMessagingSession over the deterministic in-memory transport shared with
    // NativeMessagingSessionTests, but for an arbitrary IAppPasskeyBridgeClient.
    private sealed class SessionFixture : IAsyncDisposable
    {
        private readonly NativeMessagingSessionTests.HandoffStream _input = new();
        private readonly NativeMessagingSessionTests.FrameSink _output = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly NativeMessagingSession _session;
        private readonly Task _run;

        private SessionFixture(IAppPasskeyBridgeClient bridge)
        {
            _session = new NativeMessagingSession(
                _input, _output, bridge, new FakeTimeProvider(NativeMessagingSessionTests.Epoch));
            _run = _session.RunAsync(_cts.Token);
        }

        public static Task<SessionFixture> StartAsync(IAppPasskeyBridgeClient bridge) =>
            Task.FromResult(new SessionFixture(bridge));

        public Task SendAsync(NativeRequest request) => _input.WriteFrameAsync(Serialize(request));

        public Task DisconnectInputAsync()
        {
            _input.CompleteWriting();
            return Task.CompletedTask;
        }

        public IReadOnlyList<NativeResponse> ResponsesFor(string id) =>
            _output.Snapshot().Where(r => r.Id == id).ToArray();

        private static byte[] Serialize(NativeRequest request)
        {
            using var buffer = new MemoryStream();
            NativeMessageProtocol
                .WriteAsync(buffer, request, NativeMessageJsonContext.Default.NativeRequest)
                .GetAwaiter()
                .GetResult();
            return buffer.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await _session.DisposeAsync().ConfigureAwait(false);
            _cts.Dispose();
        }
    }
}
