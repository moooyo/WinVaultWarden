using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Core.SshAgent;
using Microsoft.Win32.SafeHandles;

namespace Vault;

public enum SshAgentStartResult
{
    Started,
    AlreadyRunning,
    VaultLocked,
    PipeInUse,
    Unavailable,
}

/// <summary>
/// Opt-in, current-user Windows OpenSSH agent. The pipe is exclusively claimed;
/// an existing system agent is never stopped, replaced, or impersonated.
/// </summary>
public sealed class SshAgentServer(VaultSession session, ISshAgentRequestHandler handler) : IAsyncDisposable
{
    public const string PipeName = "openssh-ssh-agent";
    private readonly object _gate = new();
    private CancellationTokenSource? _shutdown;
    private NamedPipeServerStream? _pipe;
    private Task? _serverTask;
    private Task _stopTask = Task.CompletedTask;
    private bool _disposed;

    public bool IsRunning
    {
        get { lock (_gate) return _serverTask is { IsCompleted: false } && _shutdown is { IsCancellationRequested: false }; }
    }

    public SshAgentStartResult Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!OperatingSystem.IsWindows())
                return SshAgentStartResult.Unavailable;
            if (_serverTask is { IsCompleted: false })
                return _shutdown?.IsCancellationRequested == false
                    ? SshAgentStartResult.AlreadyRunning : SshAgentStartResult.Unavailable;
            if (!_stopTask.IsCompleted)
                return SshAgentStartResult.Unavailable;
            if (_serverTask?.IsFaulted == true)
                _ = _serverTask.Exception;
            _shutdown?.Dispose();
            _shutdown = null;
            Core.Session.VaultSessionLease lease;
            try { lease = session.CaptureLease(); }
            catch (InvalidOperationException) { return SshAgentStartResult.VaultLocked; }
            if (!session.IsUnlocked(lease))
                return SshAgentStartResult.VaultLocked;
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
            {
                return SshAgentStartResult.PipeInUse;
            }
            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(lease.CancellationToken);
            _pipe = pipe;
            var token = _shutdown.Token;
            _serverTask = Task.Run(() => ServeAsync(pipe, token), CancellationToken.None);
            return SshAgentStartResult.Started;
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            var server = _serverTask;
            var shutdown = _shutdown;
            var pipe = _pipe;
            if (server is null)
                return _stopTask;
            _serverTask = null;
            _shutdown = null;
            _pipe = null;
            _stopTask = DrainAsync(server, shutdown!, pipe!);
            return _stopTask;
        }
    }

    private static async Task DrainAsync(Task server, CancellationTokenSource shutdown, NamedPipeServerStream pipe)
    {
        try
        {
            try { await shutdown.CancelAsync().ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
            // Closing the handle also interrupts a blocked read or an approval whose peer vanished.
            pipe.Dispose();
            try { await server.ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        finally
        {
            pipe.Dispose();
            shutdown.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreatePipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
            ?? throw new UnauthorizedAccessException("A current Windows user is required.");
        // This is the explicit CurrentUserOnly ACL. Creating the native handle also lets
        // the host reject remote pipe clients, which PipeOptions cannot express.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                $"O:{sid}D:P(A;;GA;;;{sid})", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false,
            };
            const uint duplex = 3;
            const uint firstInstance = 0x00080000;
            const uint asynchronous = 0x40000000;
            const uint rejectRemoteClients = 8;
            var handle = CreateNamedPipe(
                @"\\.\pipe\" + PipeName,
                duplex | firstInstance | asynchronous,
                rejectRemoteClients,
                1, 16_384, 16_384, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var closeOnCancellation = ct.Register(static state => ((NamedPipeServerStream)state!).Dispose(), pipe);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                try
                {
                    using var client = WindowsPipeClientIdentity.TryOpen(pipe);
                    if (client is null)
                        continue;
                    // Bound idle and approval time even when a client keeps the connection open.
                    using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connection.CancelAfter(TimeSpan.FromMinutes(3));
                    var monitor = MonitorClientAsync(client, connection);
                    try
                    {
                        await SshAgentProtocol.ServeAsync(pipe, handler, client, connection.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        await connection.CancelAsync().ConfigureAwait(false);
                        await monitor.ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or InvalidDataException or UnauthorizedAccessException or OperationCanceledException
                    or ObjectDisposedException or InvalidOperationException)
                {
                    // A client failure reveals no exception details and affects only that connection.
                }
                finally
                {
                    if (!ct.IsCancellationRequested && pipe.IsConnected)
                        pipe.Disconnect();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task MonitorClientAsync(ISshAgentClientIdentity client, CancellationTokenSource connection)
    {
        try
        {
            while (!connection.IsCancellationRequested)
            {
                if (!client.IsCurrent)
                {
                    await connection.CancelAsync().ConfigureAwait(false);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), connection.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private sealed class WindowsPipeClientIdentity : ISshAgentClientIdentity
    {
        private readonly Process _process;
        private readonly NamedPipeServerStream _pipe;
        private bool _disposed;

        private WindowsPipeClientIdentity(Process process, NamedPipeServerStream pipe, SshAgentClientInfo info)
        {
            _process = process;
            _pipe = pipe;
            Info = info;
        }

        public SshAgentClientInfo Info { get; }

        public bool IsCurrent
        {
            get
            {
                if (_disposed || !OperatingSystem.IsWindows())
                    return false;
                try
                {
                    return !_process.HasExited && _pipe.IsConnected
                        && PeekNamedPipe(_pipe.SafePipeHandle, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
                        && GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var current)
                        && current == Info.ProcessId;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or Win32Exception or ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        public static WindowsPipeClientIdentity? TryOpen(NamedPipeServerStream pipe)
        {
            if (!OperatingSystem.IsWindows()
                || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId)
                || processId == 0 || processId > int.MaxValue)
                return null;
            Process? process = null;
            try
            {
                process = Process.GetProcessById((int)processId);
                // Force acquisition now and retain the handle to prevent a PID-reuse identity race.
                var handle = process.SafeHandle;
                var path = new StringBuilder(32_768);
                uint length = (uint)path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref length) || length == 0)
                    return null;
                var info = new SshAgentClientInfo(processId, path.ToString(), process.StartTime.ToUniversalTime());
                var identity = new WindowsPipeClientIdentity(process, pipe, info);
                if (!identity.IsCurrent)
                    return null;
                process = null;
                return identity;
            }
            catch (Exception exception) when (exception is Win32Exception
                or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                return null;
            }
            finally
            {
                process?.Dispose();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _process.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekNamedPipe(SafePipeHandle pipe, IntPtr buffer, uint bufferSize,
            IntPtr bytesRead, IntPtr totalBytesAvailable, IntPtr bytesLeftThisMessage);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string descriptor, uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outputSize, uint inputSize, uint defaultTimeout, ref SecurityAttributes attributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
