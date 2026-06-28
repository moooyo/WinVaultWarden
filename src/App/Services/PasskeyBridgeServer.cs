using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Core.Passkeys;

namespace App.Services;

public sealed class PasskeyBridgeServer : IAsyncDisposable
{
    private readonly BrowserPasskeyRequestHandler _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _listenTask;

    public PasskeyBridgeServer(BrowserPasskeyRequestHandler handler) => _handler = handler;

    public void Start()
    {
        if (_listenTask is not null)
            return;

        _listenTask = Task.Run(() => ListenAsync(_shutdown.Token));
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = CreateSecuredPipe();

            try
            {
                await pipe.WaitForConnectionAsync(ct);
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
        await using (pipe)
        {
            BrowserPasskeyRequest? request = null;

            try
            {
                request = await BrowserPasskeyMessageProtocol.ReadAsync<BrowserPasskeyRequest>(pipe, ct);
                if (request is null)
                    return;

                var response = await _handler.HandleAsync(request, ct);
                await BrowserPasskeyMessageProtocol.WriteAsync(pipe, response, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (request is not null && pipe.IsConnected)
                {
                    var response = new BrowserPasskeyResponse(
                        request.Id,
                        "error",
                        false,
                        Error: new BrowserPasskeyError("bridge_error", ex.Message));
                    await BrowserPasskeyMessageProtocol.WriteAsync(pipe, response, CancellationToken.None);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdown.Dispose();
    }
}
