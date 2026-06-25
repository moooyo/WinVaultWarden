using System.IO.Pipes;
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
            var pipe = new NamedPipeServerStream(
                BrowserPasskeyBridge.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

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
