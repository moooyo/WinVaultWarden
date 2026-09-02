using Core.Abstractions;

namespace App.Services;

/// <summary>
/// Coordinates account deletion with an async-flow-reentrant process gate and an exclusive
/// cross-process file handle. The lock file is intentionally retained after release.
/// </summary>
public sealed class FileAccountDeletionCoordinationGate
    : IAccountDeletionCoordinationGate
{
    private const int RetryDelayMilliseconds = 25;

    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly AsyncLocal<OwnershipScope?> _ambientScope = new();
    private readonly string _path;
    private readonly string _directoryPath;

    public FileAccountDeletionCoordinationGate()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinVaultWarden",
            "account-deletion.lock"))
    {
    }

    public FileAccountDeletionCoordinationGate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _directoryPath = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException(
                "The account-deletion lock path must have a parent directory.",
                nameof(path));
    }

    public IAccountDeletionCoordinationLease? TryAcquire()
    {
        if (_ambientScope.Value is { } current)
        {
            var acquisition = current.TryAddReference();
            if (acquisition == ScopeAcquisition.Reentrant)
                return new Lease(this, current);
            if (acquisition == ScopeAcquisition.Pending)
                return null;
        }

        if (!_processGate.Wait(0))
            return null;

        FileStream? stream = null;
        var ownershipTransferred = false;
        try
        {
            stream = TryOpenFileLock();
            if (stream is null)
                return null;

            var scope = new OwnershipScope();
            scope.Activate(stream);
            var lease = new Lease(this, scope);
            _ambientScope.Value = scope;
            stream = null;
            ownershipTransferred = true;
            return lease;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                stream?.Dispose();
                _processGate.Release();
            }
        }
    }

    public ValueTask<IAccountDeletionCoordinationLease> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_ambientScope.Value is { } current)
        {
            var acquisition = current.TryAddReference();
            if (acquisition == ScopeAcquisition.Reentrant)
            {
                return new ValueTask<IAccountDeletionCoordinationLease>(
                    new Lease(this, current));
            }
            if (acquisition == ScopeAcquisition.Pending)
            {
                throw new InvalidOperationException(
                    "The current async flow started a concurrent account-deletion gate acquisition.");
            }
        }

        var scope = new OwnershipScope();
        _ambientScope.Value = scope;
        return AcquireOuterAsync(scope, cancellationToken);
    }

    private async ValueTask<IAccountDeletionCoordinationLease> AcquireOuterAsync(
        OwnershipScope scope,
        CancellationToken cancellationToken)
    {
        var processGateHeld = false;
        FileStream? stream = null;
        try
        {
            await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            processGateHeld = true;
            stream = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            scope.Activate(stream);
            stream = null;
            return new Lease(this, scope);
        }
        catch
        {
            scope.Fail();
            stream?.Dispose();
            if (processGateHeld)
                _processGate.Release();
            if (ReferenceEquals(_ambientScope.Value, scope))
                _ambientScope.Value = null;
            throw;
        }
    }

    private async ValueTask<FileStream> AcquireFileLockAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directoryPath);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                await Task.Delay(RetryDelayMilliseconds, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private FileStream? TryOpenFileLock()
    {
        Directory.CreateDirectory(_directoryPath);
        try
        {
            return new FileStream(
                _path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception) when (IsLockContention(exception))
        {
            return null;
        }
    }

    private void Release(OwnershipScope scope)
    {
        var stream = scope.ReleaseReference();
        if (stream is null)
            return;

        if (ReferenceEquals(_ambientScope.Value, scope))
            _ambientScope.Value = null;
        try
        {
            stream.Dispose();
        }
        finally
        {
            _processGate.Release();
        }
    }

    private static bool IsLockContention(IOException exception) =>
        (exception.HResult & 0xffff) is 11 or 32 or 33 or 35;

    private enum ScopeState
    {
        Pending,
        Active,
        Released,
    }

    private enum ScopeAcquisition
    {
        Unavailable,
        Pending,
        Reentrant,
    }

    private sealed class OwnershipScope
    {
        private readonly object _gate = new();
        private ScopeState _state = ScopeState.Pending;
        private FileStream? _stream;
        private int _references = 1;

        public ScopeAcquisition TryAddReference()
        {
            lock (_gate)
            {
                if (_state == ScopeState.Pending)
                    return ScopeAcquisition.Pending;
                if (_state != ScopeState.Active)
                    return ScopeAcquisition.Unavailable;
                _references = checked(_references + 1);
                return ScopeAcquisition.Reentrant;
            }
        }

        public void Activate(FileStream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            lock (_gate)
            {
                if (_state != ScopeState.Pending || _stream is not null)
                    throw new InvalidOperationException("The deletion gate scope is not pending.");
                _stream = stream;
                _state = ScopeState.Active;
            }
        }

        public void Fail()
        {
            lock (_gate)
            {
                _stream = null;
                _references = 0;
                _state = ScopeState.Released;
            }
        }

        public FileStream? ReleaseReference()
        {
            lock (_gate)
            {
                if (_state != ScopeState.Active || _references <= 0)
                    return null;
                _references--;
                if (_references != 0)
                    return null;

                var stream = _stream
                    ?? throw new InvalidOperationException("The deletion gate has no lock handle.");
                _stream = null;
                _state = ScopeState.Released;
                return stream;
            }
        }
    }

    private sealed class Lease : IAccountDeletionCoordinationLease
    {
        private FileAccountDeletionCoordinationGate? _owner;
        private readonly OwnershipScope _scope;

        public Lease(
            FileAccountDeletionCoordinationGate owner,
            OwnershipScope scope)
        {
            _owner = owner;
            _scope = scope;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(_scope);
    }
}
