using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Core.Passkeys;

namespace App.Services;

public sealed class PasskeyBridgeServer : IAsyncDisposable
{
    private readonly BrowserPasskeyRequestHandler _handler;
    private readonly IPeerAuthorizer _peerAuthorizer;
    private readonly object _gate = new();
    private CancellationTokenSource? _shutdown;
    private Task? _listenTask;

    public PasskeyBridgeServer(BrowserPasskeyRequestHandler handler, IPeerAuthorizer peerAuthorizer)
    {
        _handler = handler;
        _peerAuthorizer = peerAuthorizer;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _listenTask is not null; }
    }

    // 幂等启动。可重启:每次运行用独立 CTS,以便 StopAsync 后能再次 Start(浏览器集成开关运行时切换,S5)。
    public void Start()
    {
        lock (_gate)
        {
            if (_listenTask is not null)
                return;
            _shutdown = new CancellationTokenSource();
            var token = _shutdown.Token;
            _listenTask = Task.Run(() => ListenAsync(token));
        }
    }

    // 停止监听并等待排空,复位为可再次 Start 的状态。幂等。
    public async Task StopAsync()
    {
        Task? listen;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_listenTask is null)
                return;
            listen = _listenTask;
            cts = _shutdown;
            _listenTask = null;
            _shutdown = null;
        }

        cts!.Cancel();
        try
        {
            await listen!;
        }
        catch (OperationCanceledException)
        {
        }
        cts.Dispose();
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = CreateSecuredPipe();

            try
            {
                await pipe.WaitForConnectionAsync(ct);

                // Peer 鉴别(M1):连接进程须是期望的 BrowserNativeHost;否则记录并断开,不处理请求。
                if (!_peerAuthorizer.IsAuthorized(pipe))
                {
                    await pipe.DisposeAsync();
                    continue;
                }

                _ = Task.Run(() => HandleConnectionAsync(pipe, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (IOException)
            {
                await pipe.DisposeAsync();
            }
        }
    }

    private static NamedPipeServerStream CreateSecuredPipe()
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SID.");

        var security = new PipeSecurity();
        // Grant the interactive owner full duplex access to the bridge.
        security.AddAccessRule(new PipeAccessRule(
            currentUser,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        // Explicitly deny anything arriving over the network (defence in depth;
        // named pipes are local but a remote SID must never reach this bridge).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(
            BrowserPasskeyBridge.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        // 本方法为 fire-and-forget(ListenAsync 里 _ = Task.Run(...)),必须**永不外抛**,
        // 否则异常成为未观察的 Task 异常被静默(R12)。所有异常在此就地处理。
        await using (pipe)
        {
            BrowserPasskeyRequest? request = null;

            try
            {
                request = await BrowserPasskeyMessageProtocol.ReadAsync(pipe, PasskeyJsonContext.Default.BrowserPasskeyRequest, ct);
                if (request is null)
                    return;

                var response = await _handler.HandleAsync(request, ct);
                await BrowserPasskeyMessageProtocol.WriteAsync(pipe, response, PasskeyJsonContext.Default.BrowserPasskeyResponse, ct);
            }
            catch (OperationCanceledException)
            {
                // 关机/取消:正常路径,忽略。
            }
            catch (Exception ex)
            {
                // 尽力回一个错误响应(浏览器请求中途断开时 WriteAsync 可能再抛 IOException)——
                // 整个方法不得外抛,故内层再包一层 catch 吞掉写回失败。
                try
                {
                    if (request is not null && pipe.IsConnected)
                    {
                        var response = new BrowserPasskeyResponse(
                            request.Id,
                            "error",
                            false,
                            Error: new BrowserPasskeyError("bridge_error", ex.Message));
                        await BrowserPasskeyMessageProtocol.WriteAsync(pipe, response, PasskeyJsonContext.Default.BrowserPasskeyResponse, CancellationToken.None);
                    }
                }
                catch
                {
                    // 回错误响应也失败(管道已断开等):吞掉,不外抛。
                }
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
