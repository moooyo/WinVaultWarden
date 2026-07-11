using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Core.BrowserIntegration;
using Core.Passkeys;

namespace App.Services;

public sealed class PasskeyBridgeServer : IAsyncDisposable
{
    private readonly BrowserPasskeyRequestHandler _handler;
    private readonly IPipePeerAuthorizer _peerAuthorizer;
    private readonly object _gate = new();
    private CancellationTokenSource? _shutdown;
    private Task? _listenTask;
    // Per-session challenge: minted on Start(), cleared on StopAsync(); every restart rotates it.
    private string? _sessionNonce;

    public PasskeyBridgeServer(BrowserPasskeyRequestHandler handler, IPipePeerAuthorizer peerAuthorizer)
    {
        _handler = handler;
        _peerAuthorizer = peerAuthorizer;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _listenTask is not null; }
    }

    // 幂等启动。可重启:每次运行用独立 CTS + 新 nonce,以便 StopAsync 后能再次 Start(浏览器集成开关运行时切换,S5)。
    public void Start()
    {
        lock (_gate)
        {
            if (_listenTask is not null)
                return;
            _sessionNonce = PasskeyBridgeHandshake.CreateServerNonce();
            _shutdown = new CancellationTokenSource();
            var token = _shutdown.Token;
            var nonce = _sessionNonce;
            _listenTask = Task.Run(() => ListenAsync(nonce, token));
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
        // 连接取消排空后再清 nonce:任何仍在途的 handshake 已随 CTS 结束,重启必生成新值。
        lock (_gate)
        {
            _sessionNonce = null;
        }
    }

    private async Task ListenAsync(string sessionNonce, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = CreateSecuredPipe();

            try
            {
                await pipe.WaitForConnectionAsync(ct);
                // 先双向鉴别再交换 nonce/PID,全部在 HandleConnectionAsync 内完成(身份先于任何 hello 字节)。
                _ = Task.Run(() => HandleConnectionAsync(pipe, sessionNonce, ct), CancellationToken.None);
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

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, string sessionNonce, CancellationToken ct)
    {
        // 本方法为 fire-and-forget(ListenAsync 里 _ = Task.Run(...)),必须**永不外抛**,
        // 否则异常成为未观察的 Task 异常被静默(R12)。所有异常在此就地处理。
        await using (pipe)
        {
            PasskeyBridgeSession? session = null;
            BrowserPasskeyRequest? request = null;

            try
            {
                // 身份先验证再 hello:CompleteServerAsync 内先 await 授权,再写 server.hello、读 client.hello。
                session = await PasskeyBridgeHandshake.CompleteServerAsync(
                    pipe,
                    sessionNonce,
                    (uint)Environment.ProcessId,
                    innerCt => _peerAuthorizer.AuthorizeClientAsync(
                        pipe, BrowserNativeHostLocator.ResolveCurrent(), innerCt),
                    ct);

                request = await BrowserPasskeyMessageProtocol.ReadAsync(
                    pipe, PasskeyJsonContext.Default.BrowserPasskeyRequest, ct);
                if (request is null)
                    return;
                // 后续消息绑定 nonce:错/重放(含重启后旧 nonce)直接丢弃,不处理请求。
                if (!PasskeyBridgeHandshake.NonceMatches(request.Nonce, session.Nonce))
                    return;

                var response = await _handler.HandleAsync(request, ct);
                await BrowserPasskeyMessageProtocol.WriteAsync(
                    pipe,
                    response with { Nonce = session.Nonce },
                    PasskeyJsonContext.Default.BrowserPasskeyResponse,
                    ct);
            }
            catch (OperationCanceledException)
            {
                // 关机/取消:正常路径,忽略。
            }
            catch (UnauthorizedAccessException)
            {
                // 身份/握手/nonce 被拒:静默断开,不回任何内容(fail-closed)。
            }
            catch (Exception ex)
            {
                // 握手已完成后处理请求失败:尽力回一个绑定 nonce 的错误响应(写回再抛 IOException 时吞掉)——
                // 整个方法不得外抛,故内层再包一层 catch。握手前的失败没有 session,不回内容。
                try
                {
                    if (session is not null && request is not null && pipe.IsConnected)
                    {
                        var response = new BrowserPasskeyResponse(
                            request.Id,
                            "error",
                            false,
                            Error: new BrowserPasskeyError("bridge_error", ex.Message),
                            Nonce: session.Nonce);
                        await BrowserPasskeyMessageProtocol.WriteAsync(
                            pipe, response, PasskeyJsonContext.Default.BrowserPasskeyResponse, CancellationToken.None);
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
