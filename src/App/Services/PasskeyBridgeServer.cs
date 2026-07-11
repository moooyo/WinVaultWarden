using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
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

    // 停止监听并等待排空,复位为可再次 Start 的状态。幂等。取消 listener 和所有连接会话,
    // 并等待每个连接的 handler tasks 排空后才返回(ListenAsync 的 finally 负责 join)。
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
        // 追踪每个已接受连接的处理任务,在关机时 join 全部(连接会话内部再 join 自己的并发 handlers)。
        var connections = new HashSet<Task>();
        var connectionsGate = new object();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = CreateSecuredPipe();

                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync();
                    break;
                }
                catch (IOException)
                {
                    await pipe.DisposeAsync();
                    continue;
                }

                // 先双向鉴别再交换 nonce/PID,全部在 HandleConnectionAsync 内完成(身份先于任何 hello 字节)。
                var connection = HandleConnectionAsync(pipe, sessionNonce, ct);
                lock (connectionsGate)
                    connections.Add(connection);
                _ = connection.ContinueWith(
                    completed =>
                    {
                        lock (connectionsGate)
                            connections.Remove(completed);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            Task[] pending;
            lock (connectionsGate)
                pending = [.. connections];
            await Task.WhenAll(pending).ConfigureAwait(false);
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
        // 本方法为 fire-and-forget(ListenAsync 里追踪并 join),必须**永不外抛**,否则 join 时成为
        // 未观察/上抛异常。握手前的一切失败静默 fail-closed;握手后交给可并发的连接会话循环。
        await using (pipe)
        {
            PasskeyBridgeSession session;
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
            }
            catch (OperationCanceledException)
            {
                return; // 关机/取消:正常路径。
            }
            catch (UnauthorizedAccessException)
            {
                return; // 身份/握手被拒:静默断开(fail-closed)。
            }
            catch
            {
                return; // 握手前的任何失败都没有 session,不回内容。
            }

            await RunConnectionAsync(pipe, session.Nonce, ct);
        }
    }

    // 单个已认证连接内的并发分发:一个读循环解析 request/cancel,不等待业务任务;每个在途 id
    // 拥有独立 CTS,响应共用一把写锁;重复 id 拒绝;EOF/nonce 错误取消该连接全部 active 并排空。
    private async Task RunConnectionAsync(NamedPipeServerStream pipe, string nonce, CancellationToken ct)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = connectionCts.Token;
        var active = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var writeGate = new SemaphoreSlim(1, 1);
        var handlers = new HashSet<Task>();
        var handlersGate = new object();

        try
        {
            while (!token.IsCancellationRequested)
            {
                BrowserPasskeyRequest? envelope;
                try
                {
                    envelope = await BrowserPasskeyMessageProtocol.ReadAsync(
                        pipe, PasskeyJsonContext.Default.BrowserPasskeyRequest, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (
                    ex is IOException or InvalidDataException or EndOfStreamException or JsonException)
                {
                    break; // 管道断开/坏帧(含 JSON 解析失败):结束该连接,finally 取消并排空全部 active。
                }

                if (envelope is null)
                    break; // EOF

                // 每条消息绑定 nonce:错/重放(含重启后旧 nonce)直接 fail-closed 断开整个连接。
                if (!PasskeyBridgeHandshake.NonceMatches(envelope.Nonce, nonce))
                    break;

                if (envelope.Type == "cancel")
                {
                    if (envelope.Id is not null && active.TryGetValue(envelope.Id, out var activeCts))
                    {
                        try { activeCts.Cancel(); }
                        catch (ObjectDisposedException) { }
                    }
                    continue;
                }

                if (envelope.Id is null)
                    continue; // 无 id 的业务请求无法路由/取消,忽略。

                var requestCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (!active.TryAdd(envelope.Id, requestCts))
                {
                    requestCts.Dispose();
                    await WriteResponseAsync(
                        pipe,
                        writeGate,
                        BrowserPasskeyResponse.Failure(envelope.Id, PasskeyErrorCodes.RequestInProgress) with { Nonce = nonce });
                    continue;
                }

                var handler = HandleRequestAsync(pipe, writeGate, active, envelope, requestCts, nonce);
                lock (handlersGate)
                    handlers.Add(handler);
                _ = handler.ContinueWith(
                    completed =>
                    {
                        lock (handlersGate)
                            handlers.Remove(completed);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        finally
        {
            // 断线/停止:取消每个在途请求让其 handler 观察到取消,然后等全部排空再释放写锁。
            foreach (var requestCts in active.Values)
            {
                try { requestCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            Task[] pending;
            lock (handlersGate)
                pending = [.. handlers];
            await Task.WhenAll(pending).ConfigureAwait(false);
            writeGate.Dispose();
        }
    }

    private async Task HandleRequestAsync(
        NamedPipeServerStream pipe,
        SemaphoreSlim writeGate,
        ConcurrentDictionary<string, CancellationTokenSource> active,
        BrowserPasskeyRequest envelope,
        CancellationTokenSource requestCts,
        string nonce)
    {
        try
        {
            var response = await _handler.HandleAsync(envelope, requestCts.Token).ConfigureAwait(false);
            // 只有请求未被取消才回写:显式 cancel/连接停止都不写迟到响应。
            if (!requestCts.IsCancellationRequested)
                await WriteResponseAsync(pipe, writeGate, response with { Nonce = nonce }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消(客户端 cancel 或连接停止):不回任何内容。
        }
        catch
        {
            // 握手已完成后处理请求失败:尽力回一个绑定 nonce 的 code-only 错误(不外泄内部消息)。
            try
            {
                if (!requestCts.IsCancellationRequested)
                    await WriteResponseAsync(
                        pipe,
                        writeGate,
                        BrowserPasskeyResponse.Failure(envelope.Id!, PasskeyErrorCodes.InternalError) with { Nonce = nonce })
                        .ConfigureAwait(false);
            }
            catch
            {
                // 回错误也失败(管道已断开等):吞掉,绝不外抛。
            }
        }
        finally
        {
            if (envelope.Id is not null && active.TryRemove(envelope.Id, out var removed))
                removed.Dispose();
        }
    }

    // 单连接内所有响应共用一把写锁,避免并发帧交错;帧本身用 None 写以保证不会写半帧撕裂协议。
    private static async Task WriteResponseAsync(
        NamedPipeServerStream pipe,
        SemaphoreSlim writeGate,
        BrowserPasskeyResponse response)
    {
        await writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (pipe.IsConnected)
                await BrowserPasskeyMessageProtocol.WriteAsync(
                    pipe, response, PasskeyJsonContext.Default.BrowserPasskeyResponse, CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 对端已断开:该连接无法再投递,忽略。
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
