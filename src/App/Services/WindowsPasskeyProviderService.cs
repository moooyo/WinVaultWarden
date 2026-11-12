using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Core.Services;
using Core.Session;
using Core.WindowsPasskeys;

namespace App.Services;

/// <summary>
/// NativeAOT-compatible out-of-process IPluginAuthenticator COM server. Only requests
/// signed by Windows for this registered CLSID reach the vault authenticator.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPasskeyProviderService(
    WindowsPasskeyAuthenticator authenticator,
    ISyncService? sync = null,
    Action<VaultSessionLease>? vaultChanged = null) : IWindowsPasskeyProvider, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _requests = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Lock _metadataGate = new();
    private CancellationTokenSource? _hostStop;
    private Task? _hostTask;
    private long _metadataEpoch;
    private bool _syncSubscribed;
    private volatile bool _accepting;
    private bool IsLocked => !_accepting || authenticator.IsLocked;
    public string? MetadataWarning { get; private set; }

    public WindowsPasskeyProviderStatus GetStatus()
    {
        var status = WindowsPasskeyPlatformApi.GetStatus();
        var warning = MetadataWarning;
        return string.IsNullOrEmpty(warning) ? status
            : status with { Description = status.Description + Environment.NewLine + warning };
    }

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_hostTask is not null)
            {
                if (_hostTask.IsFaulted) await _hostTask.ConfigureAwait(false);
                return;
            }
            if (!GetStatus().Supported) return;
            var stop = new CancellationTokenSource();
            _hostStop = stop;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (sync is ISyncCompletionSource source)
            {
                source.SyncCompleted += OnSyncCompleted;
                _syncSubscribed = true;
            }
            _hostTask = Task.Factory.StartNew(() => RunComServer(ready, stop.Token),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await ready.Task.ConfigureAwait(false);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _accepting = false;
            Interlocked.Increment(ref _metadataEpoch);
            if (_syncSubscribed && sync is ISyncCompletionSource source)
                source.SyncCompleted -= OnSyncCompleted;
            _syncSubscribed = false;
            _hostStop?.Cancel();
            CancelActiveRequests();
            try
            {
                if (_hostTask is not null) await _hostTask.ConfigureAwait(false);
            }
            finally
            {
                _hostTask = null;
                _hostStop?.Dispose();
                _hostStop = null;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    public void Register()
    {
        if (!_accepting) throw new InvalidOperationException("The Windows passkey COM server is not running.");
        WindowsPasskeyPlatformApi.Register(Environment.ProcessPath
            ?? throw new InvalidOperationException("The application path is unavailable."), AboutInfo.IsPackaged);
        TrySynchronizeMetadata();
    }

    public void Unregister()
    {
        CancelActiveRequests();
        WindowsPasskeyPlatformApi.Unregister(AboutInfo.IsPackaged);
    }

    /// <summary>Publishes public credential metadata only for the current unlocked account.</summary>
    public void SynchronizeMetadata()
        => SynchronizeMetadataCore(Interlocked.Increment(ref _metadataEpoch), expectedLease: null);

    private void SynchronizeMetadataCore(long epoch, VaultSessionLease? expectedLease)
    {
        lock (_metadataGate)
        {
            if (epoch != Volatile.Read(ref _metadataEpoch)
                || (expectedLease is not null && !authenticator.IsCurrent(expectedLease))) return;
            var status = GetStatus();
            if (!status.Supported || !status.Registered) return;
            if (authenticator.IsLocked)
            {
                WindowsPasskeyPlatformApi.ReplaceCredentialMetadata([]);
                return;
            }
            var lease = authenticator.CaptureLease();
            var metadata = authenticator.GetCredentialMetadata();
            if (!authenticator.IsCurrent(lease)) return;
            WindowsPasskeyPlatformApi.ReplaceCredentialMetadata(metadata);
            if (!authenticator.IsCurrent(lease))
                WindowsPasskeyPlatformApi.ReplaceCredentialMetadata([]);
            MetadataWarning = null;
        }
    }

    /// <summary>Cancels approvals and removes public metadata when locking or changing accounts.</summary>
    public Task OnSessionLockedAsync()
    {
        CancelActiveRequests();
        var epoch = Interlocked.Increment(ref _metadataEpoch);
        return Task.Run(() =>
        {
            lock (_metadataGate)
            {
                if (epoch != Volatile.Read(ref _metadataEpoch)) return;
                var status = GetStatus();
                if (status.Supported && status.Registered)
                    WindowsPasskeyPlatformApi.ReplaceCredentialMetadata([]);
            }
        });
    }

    private void OnSyncCompleted(VaultSessionLease lease)
    {
        if (!_accepting || !authenticator.IsCurrent(lease)) return;
        _ = Task.Run(() =>
        {
            // Never wait for a Windows API or the metadata gate in the sync observer.
            // The queued job captures fresh current-account data, not the event's snapshot.
            if (!_accepting || !authenticator.IsCurrent(lease)) return;
            try { SynchronizeMetadata(); }
            catch (Exception exception) when (exception is WindowsPasskeyException or InvalidOperationException or OperationCanceledException)
            {
                MetadataWarning = "Windows autofill metadata could not be refreshed. Unlock and synchronize again.";
            }
        });
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void RunComServer(TaskCompletionSource ready, CancellationToken ct)
    {
        uint cookie = 0;
        NativeComObject? factory = null;
        var initialized = false;
        try
        {
            WindowsPasskeyPlatformApi.ThrowIfFailed(CoInitializeEx(0, 0));
            initialized = true;
            if (authenticator.IsLocked)
            {
                try { SynchronizeMetadata(); }
                catch (WindowsPasskeyException)
                {
                    MetadataWarning = "Windows autofill metadata could not be cleared. Unlock and synchronize again.";
                }
            }
            factory = new NativeComObject(this, factory: true);
            var classId = WindowsPasskeyIdentity.ClassId;
            WindowsPasskeyPlatformApi.ThrowIfFailed(CoRegisterClassObject(in classId, factory.Pointer,
                4, 1, out cookie));
            _accepting = true;
            ready.TrySetResult();
            ct.WaitHandle.WaitOne();
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            throw;
        }
        finally
        {
            _accepting = false;
            if (cookie != 0) CoRevokeClassObject(cookie);
            factory?.Release();
            if (initialized) CoUninitialize();
        }
    }

    private unsafe int HandleOperation(nint requestPointer, nint responsePointer, WindowsPasskeyOperation kind)
    {
        if (responsePointer == 0 || requestPointer == 0) return WindowsPasskeyException.InvalidRequest;
        *(WindowsPasskeyPlatformApi.PluginOperationResponse*)responsePointer = default;
        if (!_accepting) return WindowsPasskeyException.UserCancelled;
        CancellationTokenSource? cancellation = null;
        Guid transaction = default;
        try
        {
            if (!GetStatus().Enabled) return WindowsPasskeyException.UserCancelled;
            var operation = Marshal.PtrToStructure<WindowsPasskeyPlatformApi.PluginOperationRequest>(requestPointer);
            if (operation.RequestType != 1 || operation.TransactionId == Guid.Empty)
                return WindowsPasskeyException.InvalidRequest;
            var data = WindowsPasskeyPlatformApi.ReadBytes(operation.Request, operation.RequestLength,
                1, WindowsPasskeyIdentity.MaximumRequestBytes);
            var signature = WindowsPasskeyPlatformApi.ReadBytes(operation.Signature, operation.SignatureLength, 1, 16384);
            if (!WindowsPasskeyPlatformApi.VerifyRequest(data, signature))
                return unchecked((int)0x80070005);
            cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            transaction = operation.TransactionId;
            if (!_requests.TryAdd(transaction, cancellation)) return WindowsPasskeyException.Busy;
            if (!_accepting) cancellation.Cancel();
            var decoded = WindowsPasskeyPlatformApi.Decode(operation, kind, data);
            var completed = authenticator.ExecuteAsync(decoded, cancellation.Token).GetAwaiter().GetResult();
            var response = completed.EncodedResponse;
            cancellation.Token.ThrowIfCancellationRequested();
            if (!_accepting || !GetStatus().Enabled || authenticator.IsLocked || !authenticator.IsCurrent(completed.Lease))
                return WindowsPasskeyException.UserCancelled;
            if (kind == WindowsPasskeyOperation.MakeCredential)
            {
                TrySynchronizeMetadata();
                try { vaultChanged?.Invoke(completed.Lease); }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A UI observer cannot turn an already committed credential into a failure.
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!authenticator.IsCurrent(completed.Lease))
                return WindowsPasskeyException.UserCancelled;
            var output = Marshal.AllocCoTaskMem(response.Length);
            try
            {
                Marshal.Copy(response, 0, output, response.Length);
                *(WindowsPasskeyPlatformApi.PluginOperationResponse*)responsePointer =
                    new WindowsPasskeyPlatformApi.PluginOperationResponse { Length = (uint)response.Length, Data = output };
                output = 0;
            }
            finally { if (output != 0) Marshal.FreeCoTaskMem(output); }
            return 0;
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
        {
            return WindowsPasskeyException.UserCancelled;
        }
        catch (Exception exception)
        {
            // A COM callback must contain all managed exceptions. Unowned cancellation
            // and network deadlines are failures, not evidence that the user cancelled.
            return WindowsPasskeyFailurePolicy.ToHResult(exception, cancellation?.Token ?? CancellationToken.None);
        }
        finally
        {
            if (cancellation is not null)
            {
                // A duplicate transaction never owns the dictionary entry.
                if (_requests.TryGetValue(transaction, out var current) && ReferenceEquals(current, cancellation))
                    _requests.TryRemove(transaction, out _);
                cancellation.Dispose();
            }
        }
    }

    private unsafe int CancelOperation(nint requestPointer)
    {
        if (requestPointer == 0) return WindowsPasskeyException.InvalidRequest;
        var transaction = *(Guid*)requestPointer;
        // The cancellation ABI begins with the unpredictable in-flight transaction ID.
        // Cancelling never performs a credential operation or changes persisted state.
        if (!_requests.TryGetValue(transaction, out var operation)) return WindowsPasskeyException.NotFound;
        operation.Cancel();
        return 0;
    }

    private void CancelActiveRequests()
    {
        foreach (var request in _requests.Values)
        {
            try { request.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void TrySynchronizeMetadata()
    {
        try { SynchronizeMetadata(); }
        catch (Exception exception) when (exception is WindowsPasskeyException or InvalidOperationException or OperationCanceledException)
        {
            // Metadata caching is not part of the credential's remote commit. Preserve the
            // successful WebAuthn operation and offer an honest, non-sensitive warning.
            MetadataWarning = "Windows autofill metadata could not be refreshed. Unlock and synchronize again.";
        }
    }

    private sealed unsafe class NativeComObject
    {
        private static readonly Guid UnknownId = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid FactoryId = new("00000001-0000-0000-c000-000000000046");
        private static readonly Guid AuthenticatorId = new("d26bcf6f-b54c-43ff-9f06-d5bf148625f7");
        private static readonly nint FactoryVtable = CreateVtable(factory: true);
        private static readonly nint AuthenticatorVtable = CreateVtable(factory: false);
        private readonly WindowsPasskeyProviderService _owner;
        private readonly bool _factory;
        private GCHandle _handle;
        private int _references = 1;
        internal nint Pointer { get; }

        internal NativeComObject(WindowsPasskeyProviderService owner, bool factory)
        {
            _owner = owner; _factory = factory;
            _handle = GCHandle.Alloc(this);
            Pointer = (nint)NativeMemory.AllocZeroed((nuint)(2 * nint.Size));
            *(nint*)Pointer = factory ? FactoryVtable : AuthenticatorVtable;
            ((nint*)Pointer)[1] = GCHandle.ToIntPtr(_handle);
        }

        internal uint Release()
        {
            var references = Interlocked.Decrement(ref _references);
            if (references == 0)
            {
                _handle.Free();
                NativeMemory.Free((void*)Pointer);
            }
            return checked((uint)references);
        }

        private int QueryInterface(Guid interfaceId, nint* result)
        {
            if (result == null) return WindowsPasskeyException.InvalidRequest;
            *result = 0;
            if (interfaceId != UnknownId && interfaceId != (_factory ? FactoryId : AuthenticatorId))
                return unchecked((int)0x80004002);
            Interlocked.Increment(ref _references);
            *result = Pointer;
            return 0;
        }

        private static NativeComObject Instance(nint pointer) =>
            (NativeComObject)GCHandle.FromIntPtr(((nint*)pointer)[1]).Target!;

        private static nint CreateVtable(bool factory)
        {
            var table = (nint*)NativeMemory.AllocZeroed((nuint)((factory ? 5 : 7) * nint.Size));
            table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterfaceEntry;
            table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRefEntry;
            table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseEntry;
            if (factory)
            {
                table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)&CreateInstanceEntry;
                table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, int, int>)&LockServerEntry;
            }
            else
            {
                table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&MakeCredentialEntry;
                table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, nint, int>)&GetAssertionEntry;
                table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&CancelEntry;
                table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, int*, int>)&GetLockStatusEntry;
            }
            return (nint)table;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterfaceEntry(nint self, Guid* iid, nint* result)
        {
            try { return iid == null ? WindowsPasskeyException.InvalidRequest : Instance(self).QueryInterface(*iid, result); }
            catch { return unchecked((int)0x80004005); }
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRefEntry(nint self) => (uint)Interlocked.Increment(ref Instance(self)._references);
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ReleaseEntry(nint self) => Instance(self).Release();
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CreateInstanceEntry(nint self, nint outer, Guid* iid, nint* result)
        {
            if (result == null || iid == null) return WindowsPasskeyException.InvalidRequest;
            *result = 0;
            if (outer != 0) return unchecked((int)0x80040110);
            try
            {
                var instance = new NativeComObject(Instance(self)._owner, factory: false);
                try { return instance.QueryInterface(*iid, result); }
                finally { instance.Release(); }
            }
            catch { return unchecked((int)0x80004005); }
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int LockServerEntry(nint self, int value) => 0;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int MakeCredentialEntry(nint self, nint request, nint response)
        {
            try { return Instance(self)._owner.HandleOperation(request, response, WindowsPasskeyOperation.MakeCredential); }
            catch { return unchecked((int)0x80004005); }
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetAssertionEntry(nint self, nint request, nint response)
        {
            try { return Instance(self)._owner.HandleOperation(request, response, WindowsPasskeyOperation.GetAssertion); }
            catch { return unchecked((int)0x80004005); }
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CancelEntry(nint self, nint request)
        {
            try { return Instance(self)._owner.CancelOperation(request); }
            catch { return unchecked((int)0x80004005); }
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetLockStatusEntry(nint self, int* status)
        {
            if (status == null) return WindowsPasskeyException.InvalidRequest;
            try { *status = Instance(self)._owner.IsLocked ? 0 : 1; return 0; }
            catch { *status = 0; return unchecked((int)0x80004005); }
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CoRegisterClassObject(in Guid classId, nint factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CoRevokeClassObject(uint cookie);
}
