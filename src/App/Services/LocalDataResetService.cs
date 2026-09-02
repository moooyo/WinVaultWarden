using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security;
using System.Security.Cryptography;

namespace App.Services;

public enum LocalDataResetRequestStatus
{
    Requested,
    AlreadyPending,
    CommitOutcomeUncertain,
    Corrupt,
    Unavailable,
}

public readonly record struct LocalDataResetRequestResult(
    LocalDataResetRequestStatus Status,
    Guid? ResetId = null,
    DateTimeOffset? RequestedAt = null);

public interface ILocalDataResetService
{
    LocalDataResetRequestResult RequestReset();
}

public enum LocalDataResetStartupStatus
{
    Ready,
    Contended,
    Corrupt,
    Unavailable,
    Ambiguous,
    UnsafePath,
}

public readonly record struct LocalDataResetStartupResult(
    LocalDataResetStartupStatus Status,
    bool ResetCompleted,
    IDisposable? DataLease)
{
    public bool CanStart =>
        Status == LocalDataResetStartupStatus.Ready
        && DataLease is not null;
}

internal sealed class AppStorageLayout
{
    public AppStorageLayout(string localApplicationDataDirectory, string tempDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);
        LocalApplicationDataDirectory = Normalize(localApplicationDataDirectory);
        TempDirectory = Normalize(tempDirectory);
        RootPath = Path.Combine(LocalApplicationDataDirectory, "WinVaultWarden");
        ControlRoot = Path.Combine(LocalApplicationDataDirectory, "WinVaultWarden.Control");
        MarkerPath = Path.Combine(ControlRoot, "reset-request.bin");
        EmergencyMarkerPath = Path.Combine(ControlRoot, "emergency-reset.bin");
        MoveIntentPath = Path.Combine(ControlRoot, "reset-move-intent.bin");
        RequestLockPath = Path.Combine(ControlRoot, "reset-request.lock");
        CompletionPath = Path.Combine(ControlRoot, "reset-completed.bin");
        GenerationPath = Path.Combine(RootPath, ".local-reset-generation.bin");
        DataLeasePath = Path.Combine(ControlRoot, "data-root.lock");
    }

    public string LocalApplicationDataDirectory { get; }
    public string RootPath { get; }
    public string ControlRoot { get; }
    public string MarkerPath { get; }
    public string EmergencyMarkerPath { get; }
    public string MoveIntentPath { get; }
    public string RequestLockPath { get; }
    public string CompletionPath { get; }
    public string GenerationPath { get; }
    public string DataLeasePath { get; }
    public string TempDirectory { get; }

    public static AppStorageLayout CreateDefault()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
            throw new InvalidOperationException("The local application data directory is unavailable.");
        return new AppStorageLayout(localApplicationData, Path.GetTempPath());
    }

    public string GetTrashPath(Guid resetId)
    {
        if (resetId == Guid.Empty)
            throw new ArgumentException("The reset identifier cannot be empty.", nameof(resetId));
        return Path.Combine(
            LocalApplicationDataDirectory,
            $".WinVaultWarden.reset-{resetId:N}.trash");
    }

    public string GetEmergencyRequestQuarantinePath(Guid resetId)
    {
        if (resetId == Guid.Empty)
            throw new ArgumentException("The reset identifier cannot be empty.", nameof(resetId));
        return Path.Combine(
            ControlRoot,
            $".reset-request.{resetId:N}.quarantine");
    }

    public string GetReplacementStagingPath(Guid resetId)
    {
        if (resetId == Guid.Empty)
            throw new ArgumentException("The reset identifier cannot be empty.", nameof(resetId));
        return Path.Combine(
            LocalApplicationDataDirectory,
            $".WinVaultWarden.new-{resetId:N}.staging");
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}

internal enum LocalDataResetFaultPoint
{
    AfterInitialRequestReadBeforeExclusiveLease,
    AfterInitialMoveIntentReadBeforeExclusiveLease,
    AfterRootMoved,
    BeforeDeletePath,
    AfterTrashDeleted,
    BeforeExternalCleanup,
    AfterExternalCleanup,
    AfterRootCreatedBeforeGeneration,
    AfterGenerationCommitted,
    AfterCompletionCommitted,
    BeforeEmergencyMarkerMove,
    AfterEmergencyMarkerMoveBeforeVerification,
    AfterNormalRequestQuarantinedBeforeDelete,
    BeforeEmergencyMarkerCommit,
    AfterMoveIntentPrepared,
    AfterMoveIntentDestinationVerified,
    AfterMoveIntentSourceDeleted,
    AfterReplacementStagingCreatedBeforeIntent,
    AfterMoveIntentReplacementPrepared,
    BeforeMoveIntentClear,
    BeforeMarkerCommit,
}

internal enum LocalDataResetRequestFaultPoint
{
    BeforeMarkerMove,
    AfterMarkerMoveBeforeVerification,
}

internal readonly record struct LocalDataResetMarker(
    Guid ResetId,
    DateTimeOffset RequestedAt,
    Guid BaseResetId = default);

internal enum LocalDataResetMarkerReadStatus
{
    Absent,
    Valid,
    Corrupt,
    Unavailable,
}

internal readonly record struct LocalDataResetMarkerReadResult(
    LocalDataResetMarkerReadStatus Status,
    LocalDataResetMarker Marker = default);

internal enum LocalDataResetMoveKind
{
    Normal = 1,
    Emergency = 2,
}

internal enum LocalDataResetMovePhase
{
    MovePrepared = 1,
    DestinationVerified = 2,
    SourceDeleted = 3,
    ReplacementPrepared = 4,
}

internal readonly record struct LocalDataResetMoveIntent(
    LocalDataResetMoveKind Kind,
    LocalDataResetMovePhase Phase,
    Guid ResetId,
    uint SourceVolumeSerialNumber,
    ulong SourceFileIndex,
    uint ReplacementVolumeSerialNumber = 0,
    ulong ReplacementFileIndex = 0);

internal readonly record struct LocalDataResetMoveIntentReadResult(
    LocalDataResetMarkerReadStatus Status,
    LocalDataResetMoveIntent Intent = default);

internal enum LocalDataEmergencyResetRequestStatus
{
    Requested,
    AlreadyPending,
    CommitOutcomeUncertain,
    NotAllowed,
    Contended,
    Corrupt,
    UnsafePath,
    Unavailable,
}

internal readonly record struct LocalDataEmergencyResetRequestResult(
    LocalDataEmergencyResetRequestStatus Status,
    Guid? ResetId = null,
    DateTimeOffset? RequestedAt = null);

/// <summary>
/// Persists a non-secret reset request outside the data root that will be removed.
/// The caller must request a real process exit only after this method reports success.
/// </summary>
public sealed class LocalDataResetService : ILocalDataResetService
{
    private static readonly TimeSpan RequestLockTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RequestLockRetryDelay = TimeSpan.FromMilliseconds(10);
    private static readonly object RequestGatesSync = new();
    private static readonly Dictionary<string, RequestGateEntry> RequestGates = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly AppStorageLayout _layout;
    private readonly TimeProvider _timeProvider;
    private readonly Action<LocalDataResetRequestFaultPoint, string?>? _faultHook;

    public LocalDataResetService()
        : this(AppStorageLayout.CreateDefault(), TimeProvider.System, faultHook: null)
    {
    }

    internal LocalDataResetService(
        AppStorageLayout layout,
        TimeProvider? timeProvider = null,
        Action<LocalDataResetRequestFaultPoint, string?>? faultHook = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _faultHook = faultHook;
    }

    public LocalDataResetRequestResult RequestReset()
    {
        try
        {
            LocalDataResetFileSystem.EnsureControlDirectory(_layout);
            using var inProcessLease = AcquireInProcessRequestGate();
            using var requestLease = AcquireRequestSerializationLease();
            using var dataLease = AcquireSharedDataLease();
            var existing = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
            var existingResult = ToExistingRequestResult(existing);
            if (existingResult is not null)
                return existingResult.Value;

            var baseResetId = ReadRequestBaseline();

            var requestedAt = DateTimeOffset.FromUnixTimeMilliseconds(
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            var marker = new LocalDataResetMarker(
                Guid.NewGuid(),
                requestedAt,
                baseResetId);
            try
            {
                LocalDataResetAtomicFile.WriteMarker(
                    _layout.MarkerPath,
                    marker,
                    replaceExisting: false,
                    beforeMove: path => _faultHook?.Invoke(
                        LocalDataResetRequestFaultPoint.BeforeMarkerMove,
                        path),
                    afterMove: path => _faultHook?.Invoke(
                        LocalDataResetRequestFaultPoint.AfterMarkerMoveBeforeVerification,
                        path));
            }
            catch (LocalDataResetAtomicMoveException)
            {
                var raced = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
                if (raced.Status == LocalDataResetMarkerReadStatus.Valid)
                {
                    return new(
                        LocalDataResetRequestStatus.AlreadyPending,
                        raced.Marker.ResetId,
                        raced.Marker.RequestedAt);
                }
                return new(LocalDataResetRequestStatus.CommitOutcomeUncertain);
            }

            var committed = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
            if (committed.Status == LocalDataResetMarkerReadStatus.Valid
                && committed.Marker == marker)
            {
                return new(
                    LocalDataResetRequestStatus.Requested,
                    marker.ResetId,
                    marker.RequestedAt);
            }

            return new(LocalDataResetRequestStatus.CommitOutcomeUncertain);
        }
        catch (LocalDataResetUnsafePathException)
        {
            return new(LocalDataResetRequestStatus.Corrupt);
        }
        catch (LocalDataResetCorruptStateException)
        {
            return new(LocalDataResetRequestStatus.Corrupt);
        }
        catch (LocalDataResetAmbiguousStateException)
        {
            return new(LocalDataResetRequestStatus.Corrupt);
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            return new(LocalDataResetRequestStatus.Unavailable);
        }
    }

    private static LocalDataResetRequestResult? ToExistingRequestResult(
        LocalDataResetMarkerReadResult existing) => existing.Status switch
        {
            LocalDataResetMarkerReadStatus.Valid => new(
                LocalDataResetRequestStatus.AlreadyPending,
                existing.Marker.ResetId,
                existing.Marker.RequestedAt),
            LocalDataResetMarkerReadStatus.Corrupt => new(
                LocalDataResetRequestStatus.Corrupt),
            LocalDataResetMarkerReadStatus.Unavailable => new(
                LocalDataResetRequestStatus.Unavailable),
            _ => null,
        };

    private Guid ReadRequestBaseline()
    {
        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        if (completion.Status == LocalDataResetMarkerReadStatus.Corrupt)
        {
            throw new LocalDataResetCorruptStateException(
                "The completed reset marker is corrupt.");
        }
        if (completion.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The completed reset marker is unavailable.");

        var generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        if (generation.Status == LocalDataResetMarkerReadStatus.Corrupt)
        {
            throw new LocalDataResetCorruptStateException(
                "The local data generation marker is corrupt.");
        }
        if (generation.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The local data generation marker is unavailable.");

        if (completion.Status == LocalDataResetMarkerReadStatus.Absent
            && generation.Status == LocalDataResetMarkerReadStatus.Absent)
        {
            return Guid.Empty;
        }

        if (completion.Status == LocalDataResetMarkerReadStatus.Valid
            && generation.Status == LocalDataResetMarkerReadStatus.Valid
            && completion.Marker == generation.Marker)
        {
            return completion.Marker.ResetId;
        }

        throw new LocalDataResetAmbiguousStateException();
    }

    private LocalDataResetFileLease AcquireSharedDataLease()
    {
        try
        {
            return LocalDataResetFileSystem.OpenPinnedControlLock(
                _layout,
                _layout.DataLeasePath,
                FileShare.ReadWrite);
        }
        catch (IOException exception) when (
            LocalDataResetFileSystem.IsLockContention(exception))
        {
            throw new IOException("A local data reset is currently in progress.", exception);
        }
    }

    private LocalDataResetFileLease AcquireRequestSerializationLease()
    {
        var startedAt = _timeProvider.GetTimestamp();
        while (true)
        {
            try
            {
                return LocalDataResetFileSystem.OpenPinnedControlLock(
                    _layout,
                    _layout.RequestLockPath,
                    FileShare.None);
            }
            catch (IOException exception) when (
                LocalDataResetFileSystem.IsLockContention(exception))
            {
                if (_timeProvider.GetElapsedTime(startedAt) >= RequestLockTimeout)
                {
                    throw new IOException(
                        "Another local reset request is being committed.",
                        exception);
                }
                Thread.Sleep(RequestLockRetryDelay);
            }
        }
    }

    internal static bool IsInProcessRequestGateTracked(string markerPath)
    {
        var key = Path.GetFullPath(markerPath);
        lock (RequestGatesSync)
            return RequestGates.ContainsKey(key);
    }

    private InProcessRequestLease AcquireInProcessRequestGate()
    {
        var key = Path.GetFullPath(_layout.MarkerPath);
        RequestGateEntry entry;
        lock (RequestGatesSync)
        {
            if (!RequestGates.TryGetValue(key, out entry!))
            {
                entry = new RequestGateEntry();
                RequestGates.Add(key, entry);
            }
            entry.References++;
        }

        try
        {
            if (!entry.Semaphore.Wait(RequestLockTimeout))
            {
                throw new IOException(
                    "Another local reset request is being committed in this process.");
            }
            return new InProcessRequestLease(key, entry);
        }
        catch
        {
            ReleaseRequestGateReference(key, entry);
            throw;
        }
    }

    private static void ReleaseRequestGateReference(string key, RequestGateEntry entry)
    {
        lock (RequestGatesSync)
        {
            entry.References--;
            if (entry.References != 0)
                return;

            if (RequestGates.TryGetValue(key, out var current)
                && ReferenceEquals(current, entry))
            {
                RequestGates.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class RequestGateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(initialCount: 1, maxCount: 1);

        public int References { get; set; }
    }

    private sealed class InProcessRequestLease : IDisposable
    {
        private readonly string _key;
        private RequestGateEntry? _entry;

        public InProcessRequestLease(string key, RequestGateEntry entry)
        {
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is null)
                return;

            entry.Semaphore.Release();
            ReleaseRequestGateReference(_key, entry);
        }
    }
}

internal static class LocalDataResetMarkerCodec
{
    private const int MagicLength = 8;
    private const int VersionOffset = MagicLength;
    private const int ResetIdOffset = VersionOffset + sizeof(int);
    private const int RequestedAtOffset = ResetIdOffset + 16;
    private const int LegacyPayloadLength = RequestedAtOffset + sizeof(long);
    private const int BaseResetIdOffset = LegacyPayloadLength;
    private const int PayloadLength = BaseResetIdOffset + 16;
    private const int DigestLength = 32;
    internal const int SerializedLength = PayloadLength + DigestLength;
    internal const int LegacySerializedLength = LegacyPayloadLength + DigestLength;
    private const int LegacyVersion = 1;
    private const int CurrentVersion = 2;

    private static ReadOnlySpan<byte> Magic => "WVWRST01"u8;
    private static ReadOnlySpan<byte> LegacyHashDomain =>
        "WinVaultWarden.LocalDataResetRequest.v1\0"u8;
    private static ReadOnlySpan<byte> HashDomain =>
        "WinVaultWarden.LocalDataResetRequest.v2\0"u8;

    public static byte[] Serialize(LocalDataResetMarker marker) =>
        Serialize(marker, CurrentVersion);

    internal static byte[] SerializeLegacyV1(LocalDataResetMarker marker) =>
        Serialize(marker, LegacyVersion);

    private static byte[] Serialize(LocalDataResetMarker marker, int version)
    {
        if (marker.ResetId == Guid.Empty)
            throw new InvalidDataException("The reset request identifier is empty.");
        if (marker.BaseResetId != Guid.Empty
            && marker.BaseResetId == marker.ResetId)
        {
            throw new InvalidDataException(
                "The reset request cannot use itself as its completed baseline.");
        }
        if (version == LegacyVersion && marker.BaseResetId != Guid.Empty)
            throw new InvalidDataException("A legacy marker cannot encode a baseline.");

        var unixMilliseconds = marker.RequestedAt.ToUniversalTime().ToUnixTimeMilliseconds();
        if (unixMilliseconds < 0)
            throw new InvalidDataException("The reset request time is invalid.");

        var payloadLength = version == LegacyVersion
            ? LegacyPayloadLength
            : PayloadLength;
        var bytes = GC.AllocateUninitializedArray<byte>(payloadLength + DigestLength);
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(VersionOffset, sizeof(int)),
            version);
        WriteGuid(marker.ResetId, bytes.AsSpan(ResetIdOffset, 16));
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes.AsSpan(RequestedAtOffset, sizeof(long)),
            unixMilliseconds);
        if (version == CurrentVersion)
            WriteGuid(marker.BaseResetId, bytes.AsSpan(BaseResetIdOffset, 16));
        WriteDigest(
            bytes.AsSpan(0, payloadLength),
            bytes.AsSpan(payloadLength),
            version == LegacyVersion ? LegacyHashDomain : HashDomain);
        return bytes;
    }

    public static LocalDataResetMarkerReadResult Read(string path)
    {
        var probe = LocalDataResetFileSystem.Probe(path);
        if (probe.Kind == LocalDataResetPathKind.Absent)
            return new(LocalDataResetMarkerReadStatus.Absent);
        if (probe.Kind is LocalDataResetPathKind.Directory
            or LocalDataResetPathKind.ReparsePoint)
        {
            return new(LocalDataResetMarkerReadStatus.Corrupt);
        }
        if (probe.Kind == LocalDataResetPathKind.Unavailable)
            return new(LocalDataResetMarkerReadStatus.Unavailable);

        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: SerializedLength,
                FileOptions.SequentialScan);
            if (stream.Length is not SerializedLength and not LegacySerializedLength)
                return new(LocalDataResetMarkerReadStatus.Corrupt);
            bytes = GC.AllocateUninitializedArray<byte>((int)stream.Length);
            stream.ReadExactly(bytes);
            if (stream.Position != stream.Length)
                return new(LocalDataResetMarkerReadStatus.Corrupt);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            return new(LocalDataResetMarkerReadStatus.Absent);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or SecurityException)
        {
            return new(LocalDataResetMarkerReadStatus.Unavailable);
        }

        try
        {
            return TryParse(bytes, out var marker)
                ? new(LocalDataResetMarkerReadStatus.Valid, marker)
                : new(LocalDataResetMarkerReadStatus.Corrupt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryParse(ReadOnlySpan<byte> bytes, out LocalDataResetMarker marker)
    {
        marker = default;
        if (bytes.Length < LegacyPayloadLength
            || !bytes[..MagicLength].SequenceEqual(Magic))
        {
            return false;
        }

        var version = BinaryPrimitives.ReadInt32LittleEndian(
            bytes.Slice(VersionOffset, sizeof(int)));
        var payloadLength = version switch
        {
            LegacyVersion when bytes.Length == LegacySerializedLength =>
                LegacyPayloadLength,
            CurrentVersion when bytes.Length == SerializedLength => PayloadLength,
            _ => 0,
        };
        if (payloadLength == 0)
            return false;

        Span<byte> actualDigest = stackalloc byte[DigestLength];
        try
        {
            WriteDigest(
                bytes[..payloadLength],
                actualDigest,
                version == LegacyVersion ? LegacyHashDomain : HashDomain);
            if (!CryptographicOperations.FixedTimeEquals(
                    actualDigest,
                    bytes[payloadLength..]))
                return false;

            var resetId = new Guid(bytes.Slice(ResetIdOffset, 16), bigEndian: true);
            if (resetId == Guid.Empty)
                return false;

            var unixMilliseconds = BinaryPrimitives.ReadInt64LittleEndian(
                bytes.Slice(RequestedAtOffset, sizeof(long)));
            if (unixMilliseconds < 0)
                return false;

            DateTimeOffset requestedAt;
            try
            {
                requestedAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }

            var baseResetId = version == CurrentVersion
                ? new Guid(bytes.Slice(BaseResetIdOffset, 16), bigEndian: true)
                : Guid.Empty;
            if (baseResetId != Guid.Empty && baseResetId == resetId)
                return false;

            marker = new(resetId, requestedAt, baseResetId);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualDigest);
        }
    }

    private static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(
                destination,
                bigEndian: true,
                out var bytesWritten)
            || bytesWritten != 16)
        {
            throw new InvalidDataException("The reset identifier could not be encoded.");
        }
    }

    private static void WriteDigest(
        ReadOnlySpan<byte> payload,
        Span<byte> destination,
        ReadOnlySpan<byte> domain)
    {
        Span<byte> checksumInput = stackalloc byte[domain.Length + payload.Length];
        try
        {
            domain.CopyTo(checksumInput);
            payload.CopyTo(checksumInput[domain.Length..]);
            SHA256.HashData(checksumInput, destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(checksumInput);
        }
    }
}

internal static class LocalDataResetMoveIntentCodec
{
    private const int MagicLength = 8;
    private const int VersionOffset = MagicLength;
    private const int KindOffset = VersionOffset + sizeof(int);
    private const int PhaseOffset = KindOffset + sizeof(int);
    private const int ResetIdOffset = PhaseOffset + sizeof(int);
    private const int VolumeSerialOffset = ResetIdOffset + 16;
    private const int FileIndexOffset = VolumeSerialOffset + sizeof(uint);
    private const int ReplacementVolumeSerialOffset = FileIndexOffset + sizeof(ulong);
    private const int ReplacementFileIndexOffset =
        ReplacementVolumeSerialOffset + sizeof(uint);
    private const int PayloadLength = ReplacementFileIndexOffset + sizeof(ulong);
    private const int DigestLength = 32;
    private const int CurrentVersion = 1;
    internal const int SerializedLength = PayloadLength + DigestLength;

    private static ReadOnlySpan<byte> Magic => "WVWMOVE1"u8;
    private static ReadOnlySpan<byte> HashDomain =>
        "WinVaultWarden.LocalDataResetMoveIntent.v1\0"u8;

    public static byte[] Serialize(LocalDataResetMoveIntent intent)
    {
        if (!Enum.IsDefined(intent.Kind)
            || !Enum.IsDefined(intent.Phase)
            || intent.ResetId == Guid.Empty
            || !HasCanonicalIdentities(intent))
        {
            throw new InvalidDataException("The local reset move intent is invalid.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(SerializedLength);
        Magic.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(VersionOffset, sizeof(int)),
            CurrentVersion);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(KindOffset, sizeof(int)),
            (int)intent.Kind);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(PhaseOffset, sizeof(int)),
            (int)intent.Phase);
        if (!intent.ResetId.TryWriteBytes(
                bytes.AsSpan(ResetIdOffset, 16),
                bigEndian: true,
                out var bytesWritten)
            || bytesWritten != 16)
        {
            throw new InvalidDataException("The move intent reset identifier is invalid.");
        }
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(VolumeSerialOffset, sizeof(uint)),
            intent.SourceVolumeSerialNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(FileIndexOffset, sizeof(ulong)),
            intent.SourceFileIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(ReplacementVolumeSerialOffset, sizeof(uint)),
            intent.ReplacementVolumeSerialNumber);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(ReplacementFileIndexOffset, sizeof(ulong)),
            intent.ReplacementFileIndex);
        WriteDigest(bytes.AsSpan(0, PayloadLength), bytes.AsSpan(PayloadLength));
        return bytes;
    }

    public static LocalDataResetMoveIntentReadResult Read(string path)
    {
        var probe = LocalDataResetFileSystem.Probe(path);
        if (probe.Kind == LocalDataResetPathKind.Absent)
            return new(LocalDataResetMarkerReadStatus.Absent);
        if (probe.Kind is LocalDataResetPathKind.Directory
            or LocalDataResetPathKind.ReparsePoint)
        {
            return new(LocalDataResetMarkerReadStatus.Corrupt);
        }
        if (probe.Kind == LocalDataResetPathKind.Unavailable)
            return new(LocalDataResetMarkerReadStatus.Unavailable);

        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: SerializedLength,
                FileOptions.SequentialScan);
            if (stream.Length != SerializedLength)
                return new(LocalDataResetMarkerReadStatus.Corrupt);
            bytes = GC.AllocateUninitializedArray<byte>(SerializedLength);
            stream.ReadExactly(bytes);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            return new(LocalDataResetMarkerReadStatus.Absent);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or SecurityException)
        {
            return new(LocalDataResetMarkerReadStatus.Unavailable);
        }

        try
        {
            return TryParse(bytes, out var intent)
                ? new(LocalDataResetMarkerReadStatus.Valid, intent)
                : new(LocalDataResetMarkerReadStatus.Corrupt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool TryParse(
        ReadOnlySpan<byte> bytes,
        out LocalDataResetMoveIntent intent)
    {
        intent = default;
        if (bytes.Length != SerializedLength
            || !bytes[..MagicLength].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(
                bytes.Slice(VersionOffset, sizeof(int))) != CurrentVersion)
        {
            return false;
        }

        Span<byte> digest = stackalloc byte[DigestLength];
        try
        {
            WriteDigest(bytes[..PayloadLength], digest);
            if (!CryptographicOperations.FixedTimeEquals(
                    digest,
                    bytes[PayloadLength..]))
            {
                return false;
            }

            var kind = (LocalDataResetMoveKind)BinaryPrimitives.ReadInt32LittleEndian(
                bytes.Slice(KindOffset, sizeof(int)));
            var phase = (LocalDataResetMovePhase)BinaryPrimitives.ReadInt32LittleEndian(
                bytes.Slice(PhaseOffset, sizeof(int)));
            var resetId = new Guid(bytes.Slice(ResetIdOffset, 16), bigEndian: true);
            if (!Enum.IsDefined(kind)
                || !Enum.IsDefined(phase)
                || resetId == Guid.Empty)
            {
                return false;
            }

            var parsed = new LocalDataResetMoveIntent(
                kind,
                phase,
                resetId,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.Slice(VolumeSerialOffset, sizeof(uint))),
                BinaryPrimitives.ReadUInt64LittleEndian(
                    bytes.Slice(FileIndexOffset, sizeof(ulong))),
                BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.Slice(ReplacementVolumeSerialOffset, sizeof(uint))),
                BinaryPrimitives.ReadUInt64LittleEndian(
                    bytes.Slice(ReplacementFileIndexOffset, sizeof(ulong))));
            if (!HasCanonicalIdentities(parsed))
                return false;
            intent = parsed;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static void WriteDigest(ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        Span<byte> input = stackalloc byte[HashDomain.Length + PayloadLength];
        try
        {
            HashDomain.CopyTo(input);
            payload.CopyTo(input[HashDomain.Length..]);
            SHA256.HashData(input, destination);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private static bool HasCanonicalIdentities(LocalDataResetMoveIntent intent)
    {
        if (OperatingSystem.IsWindows()
            && intent.Phase is LocalDataResetMovePhase.MovePrepared
                or LocalDataResetMovePhase.DestinationVerified
            && intent.SourceVolumeSerialNumber == 0
            && intent.SourceFileIndex == 0)
        {
            return false;
        }
        if (intent.Phase != LocalDataResetMovePhase.ReplacementPrepared)
        {
            return intent.ReplacementVolumeSerialNumber == 0
                && intent.ReplacementFileIndex == 0;
        }

        if (!OperatingSystem.IsWindows())
            return true;
        if (intent.ReplacementVolumeSerialNumber == 0
            && intent.ReplacementFileIndex == 0)
        {
            return false;
        }
        return intent.SourceVolumeSerialNumber != intent.ReplacementVolumeSerialNumber
            || intent.SourceFileIndex != intent.ReplacementFileIndex;
    }
}

internal sealed class LocalDataResetAtomicMoveException : IOException
{
    public LocalDataResetAtomicMoveException(Exception innerException)
        : base("The local reset marker move could not be committed atomically.", innerException)
    {
    }
}

internal static class LocalDataResetAtomicFile
{
    public static void WriteMarker(
        string destinationPath,
        LocalDataResetMarker marker,
        bool replaceExisting,
        Action<string>? beforeMove = null,
        Action<string>? afterMove = null) => WriteBytes(
            destinationPath,
            LocalDataResetMarkerCodec.Serialize(marker),
            replaceExisting,
            beforeMove,
            afterMove);

    public static void WriteMoveIntent(
        string destinationPath,
        LocalDataResetMoveIntent intent,
        bool replaceExisting,
        Action<string>? afterMove = null) => WriteBytes(
            destinationPath,
            LocalDataResetMoveIntentCodec.Serialize(intent),
            replaceExisting,
            beforeMove: null,
            afterMove: afterMove);

    private static void WriteBytes(
        string destinationPath,
        byte[] bytes,
        bool replaceExisting,
        Action<string>? beforeMove,
        Action<string>? afterMove)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("The marker destination has no parent directory.");

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: bytes.Length,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            beforeMove?.Invoke(destinationPath);
            try
            {
                LocalDataResetFileSystem.MoveWriteThrough(
                    temporaryPath,
                    destinationPath,
                    replaceExisting);
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or SecurityException)
            {
                throw new LocalDataResetAtomicMoveException(exception);
            }

            temporaryPath = string.Empty;
            afterMove?.Invoke(destinationPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (temporaryPath.Length > 0)
                LocalDataResetFileSystem.TryDeleteFile(temporaryPath);
        }
    }
}

internal enum LocalDataResetPathKind
{
    Absent,
    File,
    Directory,
    ReparsePoint,
    Unavailable,
}

internal readonly record struct LocalDataResetPathProbe(
    LocalDataResetPathKind Kind,
    FileAttributes Attributes = default);

internal static class LocalDataResetFileSystem
{
    private const string StreamingTemporaryPrefix = "winvaultwarden-";
    private const string StreamingTemporarySuffix = ".ciphertext.tmp";
    private const string ReservedTrashPrefix = ".WinVaultWarden.reset-";
    private const string ReservedTrashSuffix = ".trash";
    private const string ReplacementStagingPrefix = ".WinVaultWarden.new-";
    private const string ReplacementStagingSuffix = ".staging";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [Flags]
    private enum MoveFileFlags : uint
    {
        ReplaceExisting = 0x1,
        WriteThrough = 0x8,
    }

    public static LocalDataResetPathProbe Probe(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return new(LocalDataResetPathKind.ReparsePoint, attributes);
            return (attributes & FileAttributes.Directory) != 0
                ? new(LocalDataResetPathKind.Directory, attributes)
                : new(LocalDataResetPathKind.File, attributes);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException or DirectoryNotFoundException)
        {
            return new(LocalDataResetPathKind.Absent);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or SecurityException)
        {
            return new(LocalDataResetPathKind.Unavailable);
        }
    }

    public static LocalDataResetDirectoryPin PinDirectory(
        string path,
        bool allowDeleteShare = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            var probe = Probe(path);
            if (probe.Kind != LocalDataResetPathKind.Directory)
            {
                if (probe.Kind == LocalDataResetPathKind.Unavailable)
                    throw new IOException($"The directory could not be inspected: {path}");
                throw new LocalDataResetUnsafePathException(path);
            }
            return LocalDataResetDirectoryPin.CreateFallback(path);
        }

        var share = FileShareRead | FileShareWrite;
        if (allowDeleteShare)
            share |= FileShareDelete;
        var handle = OpenNativeHandle(
            path,
            FileListDirectory | FileReadAttributes,
            share,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics);
        try
        {
            var information = GetVerifiedInformation(handle, path);
            var attributes = (FileAttributes)information.FileAttributes;
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new LocalDataResetUnsafePathException(path);
            }

            return new LocalDataResetDirectoryPin(
                path,
                handle,
                new LocalDataResetFileIdentity(
                    information.VolumeSerialNumber,
                    ((ulong)information.FileIndexHigh << 32)
                        | information.FileIndexLow,
                    IsStable: true));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static FileStream OpenVerifiedLockFile(string path, FileShare share)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                share,
                bufferSize: 1,
                FileOptions.None);
            try
            {
                EnsureRegularLockFile(path);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        var nativeShare = 0u;
        if ((share & FileShare.Read) != 0)
            nativeShare |= FileShareRead;
        if ((share & FileShare.Write) != 0)
            nativeShare |= FileShareWrite;
        if ((share & FileShare.Delete) != 0)
            nativeShare |= FileShareDelete;
        var handle = OpenNativeHandle(
            path,
            GenericRead | GenericWrite,
            nativeShare,
            OpenAlways,
            FileAttributeNormal | FileFlagOpenReparsePoint);
        try
        {
            var information = GetVerifiedInformation(handle, path);
            var attributes = (FileAttributes)information.FileAttributes;
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new LocalDataResetUnsafePathException(path);
            return new FileStream(handle, FileAccess.ReadWrite);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static LocalDataResetFileLease OpenPinnedControlLock(
        AppStorageLayout layout,
        string lockPath,
        FileShare share)
    {
        ArgumentNullException.ThrowIfNull(layout);
        EnsureControlDirectory(layout);
        var controlPin = PinDirectory(layout.ControlRoot);
        try
        {
            var lockStream = OpenVerifiedLockFile(lockPath, share);
            return new LocalDataResetFileLease(controlPin, lockStream);
        }
        catch
        {
            controlPin.Dispose();
            throw;
        }
    }

    public static void EnsureControlDirectory(AppStorageLayout layout)
    {
        var control = Probe(layout.ControlRoot);
        if (control.Kind == LocalDataResetPathKind.Absent)
        {
            Directory.CreateDirectory(layout.ControlRoot);
            control = Probe(layout.ControlRoot);
        }

        if (control.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The local reset control directory is unavailable.");
        if (control.Kind != LocalDataResetPathKind.Directory)
            throw new LocalDataResetUnsafePathException(layout.ControlRoot);
    }

    public static void EnsureLockFileCanBeOpened(AppStorageLayout layout)
    {
        EnsureControlDirectory(layout);
        var lockFile = Probe(layout.DataLeasePath);
        if (lockFile.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The local reset data lease is unavailable.");
        if (lockFile.Kind is LocalDataResetPathKind.Directory
            or LocalDataResetPathKind.ReparsePoint)
        {
            throw new LocalDataResetUnsafePathException(layout.DataLeasePath);
        }
    }

    public static void EnsureRegularLockFile(string path)
    {
        var lockFile = Probe(path);
        if (lockFile.Kind != LocalDataResetPathKind.File)
            throw new LocalDataResetUnsafePathException(path);
    }

    public static void DeleteReparsePoint(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) != 0)
            Directory.Delete(path, recursive: false);
        else
            File.Delete(path);
    }

    public static bool IsOwnedStreamingTemporaryFileName(string fileName)
    {
        var requiredLength = StreamingTemporaryPrefix.Length
            + 32
            + StreamingTemporarySuffix.Length;
        if (fileName.Length != requiredLength
            || !fileName.StartsWith(StreamingTemporaryPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(StreamingTemporarySuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var identifier = fileName.AsSpan(StreamingTemporaryPrefix.Length, 32);
        foreach (var character in identifier)
        {
            if (character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')
                and not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }
        return true;
    }

    public static bool IsReservedTrashName(string fileName)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var requiredLength = ReservedTrashPrefix.Length
            + 32
            + ReservedTrashSuffix.Length;
        if (fileName.Length != requiredLength
            || !fileName.StartsWith(ReservedTrashPrefix, comparison)
            || !fileName.EndsWith(ReservedTrashSuffix, comparison))
        {
            return false;
        }

        return Guid.TryParseExact(
                fileName.AsSpan(
                    ReservedTrashPrefix.Length,
                    32),
                "N",
                out var resetId)
            && resetId != Guid.Empty;
    }

    public static bool IsReplacementStagingName(string fileName)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var requiredLength = ReplacementStagingPrefix.Length
            + 32
            + ReplacementStagingSuffix.Length;
        if (fileName.Length != requiredLength
            || !fileName.StartsWith(ReplacementStagingPrefix, comparison)
            || !fileName.EndsWith(ReplacementStagingSuffix, comparison))
        {
            return false;
        }

        return Guid.TryParseExact(
                fileName.AsSpan(ReplacementStagingPrefix.Length, 32),
                "N",
                out var resetId)
            && resetId != Guid.Empty;
    }

    public static bool IsLockContention(IOException exception) =>
        (exception.HResult & 0xffff) is 11 or 32 or 33 or 35;

    public static void MoveWriteThrough(
        string sourcePath,
        string destinationPath,
        bool replaceExisting)
    {
        if (OperatingSystem.IsWindows())
        {
            var flags = MoveFileFlags.WriteThrough;
            if (replaceExisting)
                flags |= MoveFileFlags.ReplaceExisting;

            if (!MoveFileExW(sourcePath, destinationPath, flags))
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException(
                    "The local reset path move did not complete.",
                    new Win32Exception(error));
            }
            return;
        }

        if (Directory.Exists(sourcePath))
        {
            if (replaceExisting)
            {
                throw new IOException(
                    "Replacing a directory is not supported by the fallback move.");
            }
            Directory.Move(sourcePath, destinationPath);
            return;
        }

        File.Move(sourcePath, destinationPath, replaceExisting);
    }

    public static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    public static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is
            FileNotFoundException
            or DirectoryNotFoundException
            or IOException
            or UnauthorizedAccessException
            or SecurityException)
        {
        }
    }

    private static SafeFileHandle OpenNativeHandle(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint creationDisposition,
        uint flags)
    {
        var handle = CreateFileW(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            creationDisposition,
            flags,
            IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new LocalDataResetWin32IOException(
            error,
            $"The local reset path could not be opened without following reparse points: {path}");
    }

    private static NativeFileInformation GetVerifiedInformation(
        SafeFileHandle handle,
        string path)
    {
        if (GetFileInformationByHandle(handle, out var information))
            return information;
        throw new LocalDataResetWin32IOException(
            Marshal.GetLastPInvokeError(),
            $"The local reset path identity could not be read: {path}");
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "MoveFileExW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(
        string existingFileName,
        string newFileName,
        MoveFileFlags flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out NativeFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}

internal readonly record struct LocalDataResetFileIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex,
    bool IsStable)
{
    public bool Matches(LocalDataResetFileIdentity other) =>
        !IsStable || !other.IsStable
            || VolumeSerialNumber == other.VolumeSerialNumber
                && FileIndex == other.FileIndex;
}

internal sealed class LocalDataResetDirectoryPin : IDisposable
{
    private SafeFileHandle? _handle;

    internal LocalDataResetDirectoryPin(
        string path,
        SafeFileHandle handle,
        LocalDataResetFileIdentity identity)
    {
        Path = path;
        _handle = handle;
        Identity = identity;
    }

    public string Path { get; }
    public LocalDataResetFileIdentity Identity { get; }

    internal static LocalDataResetDirectoryPin CreateFallback(string path) => new(
        path,
        new SafeFileHandle(IntPtr.Zero, ownsHandle: false),
        new LocalDataResetFileIdentity(0, 0, IsStable: false));

    public void Dispose() => Interlocked.Exchange(ref _handle, null)?.Dispose();
}

internal sealed class LocalDataResetFileLease : IDisposable
{
    private readonly object _gate = new();
    private List<IDisposable>? _resources;

    public LocalDataResetFileLease(params IDisposable[] resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Length == 0 || resources.Any(resource => resource is null))
            throw new ArgumentException("A local reset lease requires owned resources.", nameof(resources));
        _resources = [.. resources];
    }

    public void Attach(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_resources is null, this);
            _resources.Add(resource);
        }
    }

    public void Dispose()
    {
        List<IDisposable>? resources;
        lock (_gate)
        {
            resources = _resources;
            _resources = null;
        }
        if (resources is null)
            return;
        for (var index = resources.Count - 1; index >= 0; index--)
            resources[index].Dispose();
    }
}

internal sealed class LocalDataResetWin32IOException : IOException
{
    public LocalDataResetWin32IOException(int error, string message)
        : base(message, new Win32Exception(error))
    {
        HResult = unchecked((int)(0x80070000u | (uint)error));
    }
}

internal sealed class LocalDataResetUnsafePathException : IOException
{
    public LocalDataResetUnsafePathException(string path)
        : base($"The local reset path is unsafe: {path}")
    {
    }
}

internal sealed class LocalDataResetAmbiguousStateException : IOException
{
    public LocalDataResetAmbiguousStateException()
        : base("The live and detached local data roots both exist.")
    {
    }
}

internal sealed class LocalDataResetCorruptStateException : IOException
{
    public LocalDataResetCorruptStateException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Resolves a pending reset before normal application services are constructed.
/// A successful caller must retain <see cref="LocalDataResetStartupResult.DataLease"/>
/// until process exit.
/// </summary>
public sealed class LocalDataResetStartupCoordinator
{
    private static readonly TimeSpan DefaultExclusiveLockTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultExclusiveRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly SemaphoreSlim EmergencyRequestGate = new(
        initialCount: 1,
        maxCount: 1);

    private readonly AppStorageLayout _layout;
    private readonly Action _externalCleanup;
    private readonly Action<LocalDataResetFaultPoint, string?>? _faultHook;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _exclusiveLockTimeout;
    private readonly TimeSpan _exclusiveRetryDelay;

    public LocalDataResetStartupCoordinator(Action externalCleanup)
        : this(
            AppStorageLayout.CreateDefault(),
            externalCleanup,
            faultHook: null,
            timeProvider: TimeProvider.System)
    {
    }

    internal LocalDataResetStartupCoordinator(
        AppStorageLayout layout,
        Action externalCleanup,
        Action<LocalDataResetFaultPoint, string?>? faultHook = null,
        TimeProvider? timeProvider = null,
        TimeSpan? exclusiveLockTimeout = null,
        TimeSpan? exclusiveRetryDelay = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _externalCleanup = externalCleanup ?? throw new ArgumentNullException(nameof(externalCleanup));
        _faultHook = faultHook;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _exclusiveLockTimeout = exclusiveLockTimeout ?? DefaultExclusiveLockTimeout;
        _exclusiveRetryDelay = exclusiveRetryDelay ?? DefaultExclusiveRetryDelay;

        if (_exclusiveLockTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(exclusiveLockTimeout));
        if (_exclusiveRetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(exclusiveRetryDelay));
    }

    internal LocalDataEmergencyResetRequestResult RequestEmergencyReset(
        LocalDataResetStartupStatus observedStatus)
    {
        if (observedStatus is not LocalDataResetStartupStatus.Corrupt
            and not LocalDataResetStartupStatus.Ambiguous)
        {
            return new(LocalDataEmergencyResetRequestStatus.NotAllowed);
        }

        if (!EmergencyRequestGate.Wait(_exclusiveLockTimeout))
            return new(LocalDataEmergencyResetRequestStatus.Contended);
        try
        {
            return RequestEmergencyResetCore(observedStatus);
        }
        finally
        {
            EmergencyRequestGate.Release();
        }
    }

    private LocalDataEmergencyResetRequestResult RequestEmergencyResetCore(
        LocalDataResetStartupStatus observedStatus)
    {
        try
        {
            LocalDataResetFileSystem.EnsureControlDirectory(_layout);
            using var exclusiveLease = TryAcquireExclusiveLease();
            if (exclusiveLease is null)
                return new(LocalDataEmergencyResetRequestStatus.Contended);

            using var localDataPin = LocalDataResetFileSystem.PinDirectory(
                _layout.LocalApplicationDataDirectory);
            var existing = ValidateEmergencyRequestPaths(localDataPin);
            if (existing.Status == LocalDataResetMarkerReadStatus.Valid)
            {
                return HandleExistingEmergencyRequest(
                    existing.Marker,
                    observedStatus);
            }
            if (existing.Status == LocalDataResetMarkerReadStatus.Unavailable)
                return new(LocalDataEmergencyResetRequestStatus.Unavailable);

            var currentStatus = ClassifyCurrentStateForEmergencyRequest();
            if (currentStatus == LocalDataResetStartupStatus.UnsafePath)
                return new(LocalDataEmergencyResetRequestStatus.UnsafePath);
            if (currentStatus == LocalDataResetStartupStatus.Unavailable)
                return new(LocalDataEmergencyResetRequestStatus.Unavailable);
            if (currentStatus != observedStatus)
                return new(LocalDataEmergencyResetRequestStatus.NotAllowed);

            var moveIntent = ReadMoveIntent();
            if (moveIntent.HasValue)
            {
                if (moveIntent.Value.Kind != LocalDataResetMoveKind.Normal)
                    return new(LocalDataEmergencyResetRequestStatus.Corrupt);
                EnsureMoveIntentCanConverge(moveIntent.Value);
            }

            return WriteEmergencyRequest(
                replaceExisting:
                    existing.Status == LocalDataResetMarkerReadStatus.Corrupt);
        }
        catch (LocalDataResetUnsafePathException)
        {
            return new(LocalDataEmergencyResetRequestStatus.UnsafePath);
        }
        catch (LocalDataResetCorruptStateException)
        {
            return new(LocalDataEmergencyResetRequestStatus.Corrupt);
        }
        catch (LocalDataResetAmbiguousStateException)
        {
            return new(LocalDataEmergencyResetRequestStatus.Corrupt);
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            return new(LocalDataEmergencyResetRequestStatus.Unavailable);
        }
    }

    private LocalDataEmergencyResetRequestResult HandleExistingEmergencyRequest(
        LocalDataResetMarker existing,
        LocalDataResetStartupStatus observedStatus)
    {
        var moveIntent = ReadMoveIntent();
        if (moveIntent.HasValue)
        {
            if (moveIntent.Value.Kind == LocalDataResetMoveKind.Emergency
                && moveIntent.Value.ResetId != existing.ResetId)
            {
                return new(LocalDataEmergencyResetRequestStatus.Corrupt);
            }
            EnsureMoveIntentCanConverge(moveIntent.Value);
            return new(
                LocalDataEmergencyResetRequestStatus.AlreadyPending,
                existing.ResetId,
                existing.RequestedAt);
        }

        var brokenStatus = GetBrokenEmergencyProofStatus(existing);
        if (!brokenStatus.HasValue)
        {
            return new(
                LocalDataEmergencyResetRequestStatus.AlreadyPending,
                existing.ResetId,
                existing.RequestedAt);
        }
        if (brokenStatus == LocalDataResetStartupStatus.UnsafePath)
            return new(LocalDataEmergencyResetRequestStatus.UnsafePath);
        if (brokenStatus == LocalDataResetStartupStatus.Unavailable)
            return new(LocalDataEmergencyResetRequestStatus.Unavailable);
        if (brokenStatus != observedStatus)
            return new(LocalDataEmergencyResetRequestStatus.NotAllowed);
        return WriteEmergencyRequest(replaceExisting: true);
    }

    private void EnsureMoveIntentCanConverge(LocalDataResetMoveIntent intent)
    {
        if (intent.Phase != LocalDataResetMovePhase.ReplacementPrepared)
            EnsureUncommittedStagingIsRecoverable(intent.ResetId);

        var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
        var trashPath = _layout.GetTrashPath(intent.ResetId);
        var trash = LocalDataResetFileSystem.Probe(trashPath);
        EnsureDirectoryProbeIsSafe(_layout.RootPath, root);
        EnsureDirectoryProbeIsSafe(trashPath, trash);

        switch (intent.Phase)
        {
            case LocalDataResetMovePhase.MovePrepared:
                if (root.Kind == LocalDataResetPathKind.Directory
                    && trash.Kind == LocalDataResetPathKind.Absent)
                {
                    using var rootPin = LocalDataResetFileSystem.PinDirectory(
                        _layout.RootPath);
                    if (!MoveIntentSourceMatches(intent, rootPin))
                        throw new LocalDataResetCorruptStateException(
                            "The prepared reset source identity changed.");
                    return;
                }
                if (root.Kind == LocalDataResetPathKind.Absent
                    && trash.Kind == LocalDataResetPathKind.Directory)
                {
                    using var trashPin = LocalDataResetFileSystem.PinDirectory(trashPath);
                    if (!MoveIntentSourceMatches(intent, trashPin))
                        throw new LocalDataResetCorruptStateException(
                            "The prepared reset destination identity changed.");
                    return;
                }
                break;
            case LocalDataResetMovePhase.DestinationVerified:
                if (root.Kind == LocalDataResetPathKind.Absent
                    && trash.Kind == LocalDataResetPathKind.Absent)
                {
                    return;
                }
                if (root.Kind == LocalDataResetPathKind.Absent
                    && trash.Kind == LocalDataResetPathKind.Directory)
                {
                    using var trashPin = LocalDataResetFileSystem.PinDirectory(trashPath);
                    if (!MoveIntentSourceMatches(intent, trashPin))
                        throw new LocalDataResetCorruptStateException(
                            "The verified reset destination identity changed.");
                    return;
                }
                break;
            case LocalDataResetMovePhase.SourceDeleted:
                if (root.Kind == LocalDataResetPathKind.Absent
                    && trash.Kind == LocalDataResetPathKind.Absent)
                {
                    return;
                }
                break;
            case LocalDataResetMovePhase.ReplacementPrepared:
                if (trash.Kind != LocalDataResetPathKind.Absent)
                    break;
                var stagingPath = _layout.GetReplacementStagingPath(intent.ResetId);
                var staging = LocalDataResetFileSystem.Probe(stagingPath);
                EnsureDirectoryProbeIsSafe(stagingPath, staging);
                if (staging.Kind == LocalDataResetPathKind.Directory
                    && root.Kind == LocalDataResetPathKind.Absent)
                {
                    using var stagingPin = LocalDataResetFileSystem.PinDirectory(stagingPath);
                    if (!MoveIntentReplacementMatches(intent, stagingPin))
                        throw new LocalDataResetCorruptStateException(
                            "The replacement staging identity changed.");
                    if (Directory.GetFileSystemEntries(stagingPath).Length != 0)
                        throw new LocalDataResetAmbiguousStateException();
                    return;
                }
                if (staging.Kind == LocalDataResetPathKind.Absent
                    && root.Kind == LocalDataResetPathKind.Directory)
                {
                    using var rootPin = LocalDataResetFileSystem.PinDirectory(_layout.RootPath);
                    if (!MoveIntentReplacementMatches(intent, rootPin))
                        throw new LocalDataResetCorruptStateException(
                            "The installed replacement identity changed.");
                    return;
                }
                break;
            default:
                throw new UnreachableException();
        }

        throw new LocalDataResetAmbiguousStateException();
    }

    private void EnsureUncommittedStagingIsRecoverable(Guid resetId)
    {
        var stagingPath = _layout.GetReplacementStagingPath(resetId);
        var staging = LocalDataResetFileSystem.Probe(stagingPath);
        if (staging.Kind == LocalDataResetPathKind.Absent)
            return;
        if (staging.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The uncommitted replacement staging is unavailable.");
        if (staging.Kind != LocalDataResetPathKind.Directory)
            throw new LocalDataResetUnsafePathException(stagingPath);
        using var stagingPin = LocalDataResetFileSystem.PinDirectory(stagingPath);
        if (Directory.GetFileSystemEntries(stagingPath).Length != 0)
            throw new LocalDataResetAmbiguousStateException();
    }

    private LocalDataResetStartupStatus? GetBrokenEmergencyProofStatus(
        LocalDataResetMarker marker)
    {
        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        if (completion.Status == LocalDataResetMarkerReadStatus.Unavailable)
            return LocalDataResetStartupStatus.Unavailable;
        if (completion.Status == LocalDataResetMarkerReadStatus.Valid
            && completion.Marker == marker)
        {
            return ClassifyEmergencyRootProof(marker);
        }

        var generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        if (generation.Status == LocalDataResetMarkerReadStatus.Unavailable)
            return LocalDataResetStartupStatus.Unavailable;
        if (generation.Status == LocalDataResetMarkerReadStatus.Valid
            && generation.Marker == marker)
        {
            var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
            return root.Kind switch
            {
                LocalDataResetPathKind.Directory => null,
                LocalDataResetPathKind.Absent => LocalDataResetStartupStatus.Ambiguous,
                LocalDataResetPathKind.Unavailable =>
                    LocalDataResetStartupStatus.Unavailable,
                _ => LocalDataResetStartupStatus.UnsafePath,
            };
        }

        return null;
    }

    private LocalDataResetStartupStatus? ClassifyEmergencyRootProof(
        LocalDataResetMarker marker)
    {
        var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
        if (root.Kind == LocalDataResetPathKind.Absent)
            return LocalDataResetStartupStatus.Ambiguous;
        if (root.Kind == LocalDataResetPathKind.Unavailable)
            return LocalDataResetStartupStatus.Unavailable;
        if (root.Kind != LocalDataResetPathKind.Directory)
            return LocalDataResetStartupStatus.UnsafePath;

        var generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        return generation.Status switch
        {
            LocalDataResetMarkerReadStatus.Valid when generation.Marker == marker => null,
            LocalDataResetMarkerReadStatus.Corrupt => LocalDataResetStartupStatus.Corrupt,
            LocalDataResetMarkerReadStatus.Unavailable =>
                LocalDataResetStartupStatus.Unavailable,
            _ => LocalDataResetStartupStatus.Ambiguous,
        };
    }

    private LocalDataEmergencyResetRequestResult WriteEmergencyRequest(
        bool replaceExisting)
    {
        var requestedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        var marker = new LocalDataResetMarker(Guid.NewGuid(), requestedAt);
        try
        {
            LocalDataResetAtomicFile.WriteMarker(
                _layout.EmergencyMarkerPath,
                marker,
                replaceExisting,
                beforeMove: path => _faultHook?.Invoke(
                    LocalDataResetFaultPoint.BeforeEmergencyMarkerMove,
                    path),
                afterMove: path => _faultHook?.Invoke(
                    LocalDataResetFaultPoint.AfterEmergencyMarkerMoveBeforeVerification,
                    path));
        }
        catch (LocalDataResetAtomicMoveException)
        {
            return new(LocalDataEmergencyResetRequestStatus.CommitOutcomeUncertain);
        }

        var committed = LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
        if (committed.Status == LocalDataResetMarkerReadStatus.Valid
            && committed.Marker == marker)
        {
            return new(
                LocalDataEmergencyResetRequestStatus.Requested,
                marker.ResetId,
                marker.RequestedAt);
        }

        return new(LocalDataEmergencyResetRequestStatus.CommitOutcomeUncertain);
    }

    private LocalDataResetStartupStatus ClassifyCurrentStateForEmergencyRequest()
    {
        try
        {
            var moveIntent = LocalDataResetMoveIntentCodec.Read(_layout.MoveIntentPath);
            if (moveIntent.Status == LocalDataResetMarkerReadStatus.Corrupt)
                return LocalDataResetStartupStatus.Corrupt;
            if (moveIntent.Status == LocalDataResetMarkerReadStatus.Unavailable)
                return LocalDataResetStartupStatus.Unavailable;
            if (moveIntent.Status == LocalDataResetMarkerReadStatus.Valid)
                return ClassifyMoveIntentForEmergencyRequest(moveIntent.Intent);

            var emergency = LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
            if (emergency.Status == LocalDataResetMarkerReadStatus.Corrupt)
                return LocalDataResetStartupStatus.Corrupt;
            if (emergency.Status == LocalDataResetMarkerReadStatus.Unavailable)
                return LocalDataResetStartupStatus.Unavailable;

            var request = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
            return request.Status switch
            {
                LocalDataResetMarkerReadStatus.Absent =>
                    ClassifyReadyStateWithoutRequest(),
                LocalDataResetMarkerReadStatus.Valid =>
                    ClassifyPendingNormalReset(request.Marker),
                LocalDataResetMarkerReadStatus.Corrupt =>
                    LocalDataResetStartupStatus.Corrupt,
                _ => LocalDataResetStartupStatus.Unavailable,
            };
        }
        catch (LocalDataResetAmbiguousStateException)
        {
            return LocalDataResetStartupStatus.Ambiguous;
        }
        catch (LocalDataResetCorruptStateException)
        {
            return LocalDataResetStartupStatus.Corrupt;
        }
        catch (LocalDataResetUnsafePathException)
        {
            return LocalDataResetStartupStatus.UnsafePath;
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            return LocalDataResetStartupStatus.Unavailable;
        }
    }

    private LocalDataResetStartupStatus ClassifyMoveIntentForEmergencyRequest(
        LocalDataResetMoveIntent intent)
    {
        var markerPath = intent.Kind == LocalDataResetMoveKind.Normal
            ? _layout.MarkerPath
            : _layout.EmergencyMarkerPath;
        var marker = LocalDataResetMarkerCodec.Read(markerPath);
        if (marker.Status == LocalDataResetMarkerReadStatus.Corrupt)
            return LocalDataResetStartupStatus.Corrupt;
        if (marker.Status == LocalDataResetMarkerReadStatus.Unavailable)
            return LocalDataResetStartupStatus.Unavailable;
        if (marker.Status == LocalDataResetMarkerReadStatus.Absent)
        {
            ValidateOrphanedMoveIntentCompletion(intent);
            EnsureNoReplacementStaging();
            return LocalDataResetStartupStatus.Ready;
        }
        if (marker.Marker.ResetId != intent.ResetId)
            throw new LocalDataResetAmbiguousStateException();

        EnsureMoveIntentCanConverge(intent);
        if (intent.Kind == LocalDataResetMoveKind.Normal)
        {
            EnsureEmergencyMarkerAbsent();
            if (intent.Phase != LocalDataResetMovePhase.ReplacementPrepared)
                EnsureRequestBaseline(marker.Marker);
            EnsureCompletionDestinationCanBeReplaced();
            EnsureNoReservedTrash(intent.ResetId);
            EnsureNoReplacementStaging(intent.ResetId);
        }
        return LocalDataResetStartupStatus.Ready;
    }

    private LocalDataResetStartupStatus ClassifyReadyStateWithoutRequest()
    {
        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        switch (completion.Status)
        {
            case LocalDataResetMarkerReadStatus.Absent:
                EnsureLegacyGenerationAbsent();
                EnsureNoReplacementStaging();
                var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
                EnsureDirectoryProbeIsSafe(_layout.RootPath, root);
                if (root.Kind == LocalDataResetPathKind.Directory)
                {
                    using var rootPin = LocalDataResetFileSystem.PinDirectory(
                        _layout.RootPath);
                    EnsurePinnedRootPath(rootPin);
                }
                EnsureNoReservedTrash();
                return LocalDataResetStartupStatus.Ready;
            case LocalDataResetMarkerReadStatus.Valid:
                EnsureNoReplacementStaging();
                using (var rootPin = PinCompletedRoot())
                    EnsureCompletedEpochReady(completion.Marker, rootPin);
                return LocalDataResetStartupStatus.Ready;
            case LocalDataResetMarkerReadStatus.Corrupt:
                return LocalDataResetStartupStatus.Corrupt;
            default:
                return LocalDataResetStartupStatus.Unavailable;
        }
    }

    private LocalDataResetStartupStatus ClassifyPendingNormalReset(
        LocalDataResetMarker marker)
    {
        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        if (completion.Status == LocalDataResetMarkerReadStatus.Corrupt)
            return LocalDataResetStartupStatus.Corrupt;
        if (completion.Status == LocalDataResetMarkerReadStatus.Unavailable)
            return LocalDataResetStartupStatus.Unavailable;
        if (completion.Status == LocalDataResetMarkerReadStatus.Valid
            && completion.Marker == marker)
        {
            using var rootPin = PinCompletedRoot();
            EnsureCompletedEpochReady(marker, rootPin);
            EnsureNoReplacementStaging();
            return LocalDataResetStartupStatus.Ready;
        }

        var generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        if (generation.Status == LocalDataResetMarkerReadStatus.Valid
            && generation.Marker == marker)
        {
            using var rootPin = PinCompletedRoot();
            EnsurePinnedRootPath(rootPin);
            EnsureNoReservedTrash();
            EnsureNoReplacementStaging();
            var baselineMatches = marker.BaseResetId == Guid.Empty
                ? completion.Status == LocalDataResetMarkerReadStatus.Absent
                : completion.Status == LocalDataResetMarkerReadStatus.Valid
                    && completion.Marker.ResetId == marker.BaseResetId;
            if (!baselineMatches)
                throw new LocalDataResetAmbiguousStateException();
            return LocalDataResetStartupStatus.Ready;
        }

        EnsureRequestBaseline(marker);
        EnsureRequestMarkerMatches(marker);
        EnsureCompletionDestinationCanBeReplaced();
        var trashPath = _layout.GetTrashPath(marker.ResetId);
        var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
        var trash = LocalDataResetFileSystem.Probe(trashPath);
        EnsureDirectoryProbeIsSafe(_layout.RootPath, root);
        EnsureDirectoryProbeIsSafe(trashPath, trash);
        EnsureNoReservedTrash(marker.ResetId);
        EnsureNoReplacementStaging();
        if (root.Kind == LocalDataResetPathKind.Directory)
        {
            using var rootPin = LocalDataResetFileSystem.PinDirectory(_layout.RootPath);
            EnsurePinnedRootPath(rootPin);
        }
        if (trash.Kind == LocalDataResetPathKind.Directory)
        {
            using var trashPin = LocalDataResetFileSystem.PinDirectory(trashPath);
        }
        if (root.Kind == LocalDataResetPathKind.Directory
            && trash.Kind == LocalDataResetPathKind.Directory)
        {
            throw new LocalDataResetAmbiguousStateException();
        }
        return LocalDataResetStartupStatus.Ready;
    }

    public LocalDataResetStartupResult PrepareForStartup()
    {
        try
        {
            LocalDataResetFileSystem.EnsureControlDirectory(_layout);
            var moveIntent = LocalDataResetMoveIntentCodec.Read(_layout.MoveIntentPath);
            if (moveIntent.Status != LocalDataResetMarkerReadStatus.Absent)
            {
                return moveIntent.Status switch
                {
                    LocalDataResetMarkerReadStatus.Valid =>
                        ResumeMoveIntent(moveIntent.Intent),
                    LocalDataResetMarkerReadStatus.Corrupt => Failure(
                        LocalDataResetStartupStatus.Corrupt),
                    _ => Failure(LocalDataResetStartupStatus.Unavailable),
                };
            }

            var emergency = LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
            if (emergency.Status != LocalDataResetMarkerReadStatus.Absent)
            {
                return emergency.Status switch
                {
                    LocalDataResetMarkerReadStatus.Valid =>
                        ResumeEmergencyReset(emergency.Marker),
                    LocalDataResetMarkerReadStatus.Corrupt => Failure(
                        LocalDataResetStartupStatus.Corrupt),
                    _ => Failure(LocalDataResetStartupStatus.Unavailable),
                };
            }

            var initial = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
            return initial.Status switch
            {
                LocalDataResetMarkerReadStatus.Absent => PrepareWithoutKnownMarker(),
                LocalDataResetMarkerReadStatus.Valid => ResumeReset(initial.Marker),
                LocalDataResetMarkerReadStatus.Corrupt => Failure(
                    LocalDataResetStartupStatus.Corrupt),
                _ => Failure(LocalDataResetStartupStatus.Unavailable),
            };
        }
        catch (LocalDataResetUnsafePathException)
        {
            return Failure(LocalDataResetStartupStatus.UnsafePath);
        }
        catch (LocalDataResetAmbiguousStateException)
        {
            return Failure(LocalDataResetStartupStatus.Ambiguous);
        }
        catch (LocalDataResetCorruptStateException)
        {
            return Failure(LocalDataResetStartupStatus.Corrupt);
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            return Failure(LocalDataResetStartupStatus.Unavailable);
        }
    }

    private LocalDataResetStartupResult PrepareWithoutKnownMarker()
    {
        var sharedLease = TryAcquireSharedLease();
        if (sharedLease is null)
            return Failure(LocalDataResetStartupStatus.Contended);

        var moveIntent = LocalDataResetMoveIntentCodec.Read(_layout.MoveIntentPath);
        if (moveIntent.Status != LocalDataResetMarkerReadStatus.Absent)
        {
            sharedLease.Dispose();
            return moveIntent.Status switch
            {
                LocalDataResetMarkerReadStatus.Valid =>
                    ResumeMoveIntent(moveIntent.Intent),
                LocalDataResetMarkerReadStatus.Corrupt => Failure(
                    LocalDataResetStartupStatus.Corrupt),
                _ => Failure(LocalDataResetStartupStatus.Unavailable),
            };
        }

        var emergency = LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
        if (emergency.Status != LocalDataResetMarkerReadStatus.Absent)
        {
            sharedLease.Dispose();
            return emergency.Status switch
            {
                LocalDataResetMarkerReadStatus.Valid =>
                    ResumeEmergencyReset(emergency.Marker),
                LocalDataResetMarkerReadStatus.Corrupt => Failure(
                    LocalDataResetStartupStatus.Corrupt),
                _ => Failure(LocalDataResetStartupStatus.Unavailable),
            };
        }

        var marker = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
        if (marker.Status == LocalDataResetMarkerReadStatus.Absent)
        {
            try
            {
                PrepareReadyLease(sharedLease);
            }
            catch
            {
                sharedLease.Dispose();
                throw;
            }
            return new(
                LocalDataResetStartupStatus.Ready,
                ResetCompleted: false,
                sharedLease);
        }

        sharedLease.Dispose();
        return marker.Status switch
        {
            LocalDataResetMarkerReadStatus.Valid => ResumeReset(marker.Marker),
            LocalDataResetMarkerReadStatus.Corrupt => Failure(
                LocalDataResetStartupStatus.Corrupt),
            _ => Failure(LocalDataResetStartupStatus.Unavailable),
        };
    }

    private LocalDataResetStartupResult ResumeReset(LocalDataResetMarker expectedMarker)
    {
        _faultHook?.Invoke(
            LocalDataResetFaultPoint.AfterInitialRequestReadBeforeExclusiveLease,
            _layout.MarkerPath);
        using var exclusiveLease = TryAcquireExclusiveLease();
        if (exclusiveLease is null)
            return Failure(LocalDataResetStartupStatus.Contended);

        var emergency = LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
        if (emergency.Status == LocalDataResetMarkerReadStatus.Valid)
            return ExecuteEmergencyAndRelease(emergency.Marker, exclusiveLease);
        if (emergency.Status == LocalDataResetMarkerReadStatus.Corrupt)
            return Failure(LocalDataResetStartupStatus.Corrupt);
        if (emergency.Status == LocalDataResetMarkerReadStatus.Unavailable)
            return Failure(LocalDataResetStartupStatus.Unavailable);

        var current = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
        if (current.Status == LocalDataResetMarkerReadStatus.Absent)
        {
            try
            {
                using var rootPin = PinCompletedRoot();
                EnsureCompletedEpochReady(expectedMarker, rootPin);
                EnsureRequestMarkerAbsent();
            }
            catch (LocalDataResetAmbiguousStateException)
            {
                return Failure(LocalDataResetStartupStatus.Ambiguous);
            }
            catch (LocalDataResetCorruptStateException)
            {
                return Failure(LocalDataResetStartupStatus.Corrupt);
            }
            catch (LocalDataResetUnsafePathException)
            {
                return Failure(LocalDataResetStartupStatus.UnsafePath);
            }
            catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
            {
                return Failure(LocalDataResetStartupStatus.Unavailable);
            }

            return ReleaseExclusiveAndAcquireReadyLease(exclusiveLease);
        }
        if (current.Status == LocalDataResetMarkerReadStatus.Corrupt)
            return Failure(LocalDataResetStartupStatus.Corrupt);
        if (current.Status != LocalDataResetMarkerReadStatus.Valid)
            return Failure(LocalDataResetStartupStatus.Unavailable);

        expectedMarker = current.Marker;

        try
        {
            var moveIntent = ReadMoveIntent();
            if (moveIntent.HasValue)
            {
                ExecuteReset(expectedMarker);
            }
            else if (!TryReplayCompletedReset(expectedMarker)
                && !TryCompleteGenerationCommit(expectedMarker))
            {
                ExecuteReset(expectedMarker);
            }
        }
        catch (LocalDataResetAmbiguousStateException)
        {
            return Failure(LocalDataResetStartupStatus.Ambiguous);
        }
        catch (LocalDataResetUnsafePathException)
        {
            return Failure(LocalDataResetStartupStatus.UnsafePath);
        }
        catch (LocalDataResetCorruptStateException)
        {
            return Failure(LocalDataResetStartupStatus.Corrupt);
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            return Failure(LocalDataResetStartupStatus.Unavailable);
        }

        return ReleaseExclusiveAndAcquireReadyLease(exclusiveLease);
    }

    private LocalDataResetStartupResult ResumeMoveIntent(
        LocalDataResetMoveIntent expectedIntent)
    {
        _faultHook?.Invoke(
            LocalDataResetFaultPoint.AfterInitialMoveIntentReadBeforeExclusiveLease,
            _layout.MoveIntentPath);
        using var exclusiveLease = TryAcquireExclusiveLease();
        if (exclusiveLease is null)
            return Failure(LocalDataResetStartupStatus.Contended);

        try
        {
            var read = LocalDataResetMoveIntentCodec.Read(_layout.MoveIntentPath);
            if (read.Status == LocalDataResetMarkerReadStatus.Absent)
            {
                ValidateOrphanedMoveIntentCompletion(expectedIntent);
                return ReleaseExclusiveAndAcquireReadyLease(exclusiveLease);
            }
            if (read.Status == LocalDataResetMarkerReadStatus.Corrupt)
                return Failure(LocalDataResetStartupStatus.Corrupt);
            if (read.Status != LocalDataResetMarkerReadStatus.Valid)
                return Failure(LocalDataResetStartupStatus.Unavailable);
            expectedIntent = read.Intent;

            var markerPath = expectedIntent.Kind == LocalDataResetMoveKind.Normal
                ? _layout.MarkerPath
                : _layout.EmergencyMarkerPath;
            var marker = LocalDataResetMarkerCodec.Read(markerPath);
            if (marker.Status == LocalDataResetMarkerReadStatus.Corrupt)
                return Failure(LocalDataResetStartupStatus.Corrupt);
            if (marker.Status == LocalDataResetMarkerReadStatus.Unavailable)
                return Failure(LocalDataResetStartupStatus.Unavailable);
            if (marker.Status == LocalDataResetMarkerReadStatus.Absent)
            {
                if (expectedIntent.Kind == LocalDataResetMoveKind.Normal)
                {
                    var emergency = LocalDataResetMarkerCodec.Read(
                        _layout.EmergencyMarkerPath);
                    if (emergency.Status == LocalDataResetMarkerReadStatus.Corrupt)
                        return Failure(LocalDataResetStartupStatus.Corrupt);
                    if (emergency.Status == LocalDataResetMarkerReadStatus.Unavailable)
                        return Failure(LocalDataResetStartupStatus.Unavailable);
                    if (emergency.Status == LocalDataResetMarkerReadStatus.Valid)
                    {
                        ValidateOrphanedMoveIntentCompletion(
                            expectedIntent,
                            emergency.Marker);
                        ClearMoveIntent(expectedIntent);
                        EnsureEmergencyMarkerMatches(emergency.Marker);
                        return ExecuteEmergencyAndRelease(
                            emergency.Marker,
                            exclusiveLease);
                    }
                }
                FinalizeOrphanedMoveIntent(expectedIntent);
                return ReleaseExclusiveAndAcquireReadyLease(exclusiveLease);
            }
            if (marker.Marker.ResetId != expectedIntent.ResetId)
                return Failure(LocalDataResetStartupStatus.Ambiguous);

            if (expectedIntent.Kind == LocalDataResetMoveKind.Normal)
            {
                var emergency = LocalDataResetMarkerCodec.Read(
                    _layout.EmergencyMarkerPath);
                if (emergency.Status == LocalDataResetMarkerReadStatus.Corrupt)
                    return Failure(LocalDataResetStartupStatus.Corrupt);
                if (emergency.Status == LocalDataResetMarkerReadStatus.Unavailable)
                    return Failure(LocalDataResetStartupStatus.Unavailable);
                if (emergency.Status == LocalDataResetMarkerReadStatus.Valid)
                {
                    ApplyEmergencyAuthorizationAroundNormalIntent(
                        expectedIntent,
                        emergency.Marker);
                }
                ExecuteReset(marker.Marker);
                return ReleaseExclusiveAndAcquireReadyLease(exclusiveLease);
            }
            return ExecuteEmergencyAndRelease(marker.Marker, exclusiveLease);
        }
        catch (LocalDataResetAmbiguousStateException)
        {
            return Failure(LocalDataResetStartupStatus.Ambiguous);
        }
        catch (LocalDataResetUnsafePathException)
        {
            return Failure(LocalDataResetStartupStatus.UnsafePath);
        }
        catch (LocalDataResetCorruptStateException)
        {
            return Failure(LocalDataResetStartupStatus.Corrupt);
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            return Failure(LocalDataResetStartupStatus.Unavailable);
        }
    }

    private void FinalizeOrphanedMoveIntent(LocalDataResetMoveIntent intent)
    {
        ValidateOrphanedMoveIntentCompletion(intent);
        ClearMoveIntent(intent);
    }

    private void ApplyEmergencyAuthorizationAroundNormalIntent(
        LocalDataResetMoveIntent intent,
        LocalDataResetMarker emergencyMarker)
    {
        EnsureMoveIntentCanConverge(intent);
        using var localDataPin = LocalDataResetFileSystem.PinDirectory(
            _layout.LocalApplicationDataDirectory);
        ValidateEmergencyExecutionPaths(localDataPin, emergencyMarker);
        DeleteAllReservedTrashDirectories(intent.ResetId);
        DeleteAllReplacementStagingDirectories(intent.ResetId);
        EnsureMoveIntentCanConverge(intent);
        EnsureMoveIntentEquals(intent);
        EnsureEmergencyMarkerMatches(emergencyMarker);
    }

    private void ValidateOrphanedMoveIntentCompletion(
        LocalDataResetMoveIntent intent,
        LocalDataResetMarker? retainedEmergency = null)
    {
        if (intent.Phase != LocalDataResetMovePhase.ReplacementPrepared)
            throw new LocalDataResetAmbiguousStateException();
        EnsureRequestMarkerAbsent();
        if (retainedEmergency.HasValue)
            EnsureEmergencyMarkerMatches(retainedEmergency.Value);
        else
            EnsureEmergencyMarkerAbsent();
        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        if (completion.Status == LocalDataResetMarkerReadStatus.Corrupt)
        {
            throw new LocalDataResetCorruptStateException(
                "The reset completion marker is corrupt.");
        }
        if (completion.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The reset completion marker is unavailable.");
        if (completion.Status != LocalDataResetMarkerReadStatus.Valid
            || completion.Marker.ResetId != intent.ResetId)
        {
            throw new LocalDataResetAmbiguousStateException();
        }

        using var rootPin = PinCompletedRoot();
        EnsureCompletedEpochReady(completion.Marker, rootPin);
        if (!MoveIntentReplacementMatches(intent, rootPin))
        {
            throw new LocalDataResetCorruptStateException(
                "The completed replacement identity does not match its move intent.");
        }
        EnsureCompletedEpochReady(completion.Marker, rootPin);
        EnsureRequestMarkerAbsent();
        if (retainedEmergency.HasValue)
            EnsureEmergencyMarkerMatches(retainedEmergency.Value);
    }

    private LocalDataResetStartupResult ResumeEmergencyReset(
        LocalDataResetMarker expectedMarker)
    {
        using var exclusiveLease = TryAcquireExclusiveLease();
        if (exclusiveLease is null)
            return Failure(LocalDataResetStartupStatus.Contended);

        var current = LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
        if (current.Status == LocalDataResetMarkerReadStatus.Absent)
        {
            try
            {
                using var rootPin = PinCompletedRoot();
                EnsureCompletedEpochReady(expectedMarker, rootPin);
                EnsureNormalRequestAbsent();
            }
            catch (LocalDataResetAmbiguousStateException)
            {
                return Failure(LocalDataResetStartupStatus.Ambiguous);
            }
            catch (LocalDataResetCorruptStateException)
            {
                return Failure(LocalDataResetStartupStatus.Corrupt);
            }
            catch (LocalDataResetUnsafePathException)
            {
                return Failure(LocalDataResetStartupStatus.UnsafePath);
            }
            catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
            {
                return Failure(LocalDataResetStartupStatus.Unavailable);
            }

            return ReleaseExclusiveAndAcquireReadyLease(exclusiveLease);
        }
        if (current.Status == LocalDataResetMarkerReadStatus.Corrupt)
            return Failure(LocalDataResetStartupStatus.Corrupt);
        if (current.Status != LocalDataResetMarkerReadStatus.Valid)
            return Failure(LocalDataResetStartupStatus.Unavailable);

        return ExecuteEmergencyAndRelease(current.Marker, exclusiveLease);
    }

    private LocalDataResetStartupResult ExecuteEmergencyAndRelease(
        LocalDataResetMarker marker,
        LocalDataResetFileLease exclusiveLease)
    {
        try
        {
            var moveIntent = ReadMoveIntent();
            if (moveIntent.HasValue)
            {
                ExecuteEmergencyReset(marker);
            }
            else if (!TryReplayCompletedEmergencyReset(marker)
                && !TryCompleteEmergencyGenerationCommit(marker))
            {
                ExecuteEmergencyReset(marker);
            }
        }
        catch (LocalDataResetAmbiguousStateException)
        {
            return Failure(LocalDataResetStartupStatus.Ambiguous);
        }
        catch (LocalDataResetUnsafePathException)
        {
            return Failure(LocalDataResetStartupStatus.UnsafePath);
        }
        catch (LocalDataResetCorruptStateException)
        {
            return Failure(LocalDataResetStartupStatus.Corrupt);
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            return Failure(LocalDataResetStartupStatus.Unavailable);
        }

        return ReleaseExclusiveAndAcquireReadyLease(exclusiveLease);
    }

    private bool TryReplayCompletedEmergencyReset(LocalDataResetMarker marker)
    {
        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        if (completion.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The emergency completion marker is unavailable.");
        if (completion.Status != LocalDataResetMarkerReadStatus.Valid
            || completion.Marker != marker)
        {
            return false;
        }

        using var localDataPin = LocalDataResetFileSystem.PinDirectory(
            _layout.LocalApplicationDataDirectory);
        ValidateEmergencyExecutionPaths(localDataPin, marker);
        DeleteAllReservedTrashDirectories();
        using var rootPin = PinCompletedRoot();
        EnsureCompletedEpochReady(marker, rootPin);
        FinishEmergencyReset(marker, rootPin);
        return true;
    }

    private bool TryCompleteEmergencyGenerationCommit(LocalDataResetMarker marker)
    {
        var generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        if (generation.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The emergency generation marker is unavailable.");
        if (generation.Status != LocalDataResetMarkerReadStatus.Valid
            || generation.Marker != marker)
        {
            return false;
        }

        using var localDataPin = LocalDataResetFileSystem.PinDirectory(
            _layout.LocalApplicationDataDirectory);
        ValidateEmergencyExecutionPaths(localDataPin, marker);
        DeleteAllReservedTrashDirectories();
        using var rootPin = PinCompletedRoot();
        EnsurePinnedRootPath(rootPin);
        EnsureEmergencyMarkerMatches(marker);
        generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        if (generation.Status != LocalDataResetMarkerReadStatus.Valid
            || generation.Marker != marker)
        {
            throw new LocalDataResetAmbiguousStateException();
        }
        EnsureNoReservedTrash();
        EnsureCompletionDestinationCanBeReplaced();
        LocalDataResetAtomicFile.WriteMarker(
            _layout.CompletionPath,
            marker,
            replaceExisting: true);
        _faultHook?.Invoke(
            LocalDataResetFaultPoint.AfterCompletionCommitted,
            _layout.CompletionPath);
        EnsureCompletedEpochReady(marker, rootPin);
        FinishEmergencyReset(marker, rootPin);
        return true;
    }

    private void ExecuteEmergencyReset(LocalDataResetMarker marker)
    {
        using var localDataPin = LocalDataResetFileSystem.PinDirectory(
            _layout.LocalApplicationDataDirectory);
        ValidateEmergencyExecutionPaths(localDataPin, marker);
        var intent = CompleteSourceDeletion(
            LocalDataResetMoveKind.Emergency,
            marker,
            emergency: true);
        intent = PrepareReplacement(
            intent,
            LocalDataResetMoveKind.Emergency,
            marker);
        using var rootPin = InstallReplacement(
            intent,
            LocalDataResetMoveKind.Emergency,
            marker);
        CommitReplacementEpoch(marker, rootPin);
        FinishEmergencyReset(marker, rootPin);
        ClearMoveIntent(intent);
    }

    private void FinishEmergencyReset(
        LocalDataResetMarker marker,
        LocalDataResetDirectoryPin rootPin)
    {
        EnsureEmergencyMarkerMatches(marker);
        EnsureCompletedEpochReady(marker, rootPin);
        QuarantineNormalRequest(marker);
        EnsureCompletedEpochReady(marker, rootPin);
        EnsureNormalRequestAbsent();
        DeleteEmergencyMarker(marker);
    }

    private void CommitReplacementEpoch(
        LocalDataResetMarker marker,
        LocalDataResetDirectoryPin rootPin)
    {
        EnsurePinnedRootPath(rootPin);
        var generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        switch (generation.Status)
        {
            case LocalDataResetMarkerReadStatus.Absent:
                LocalDataResetAtomicFile.WriteMarker(
                    _layout.GenerationPath,
                    marker,
                    replaceExisting: false);
                _faultHook?.Invoke(
                    LocalDataResetFaultPoint.AfterGenerationCommitted,
                    _layout.GenerationPath);
                EnsureMarkerWriteMatches(_layout.GenerationPath, marker);
                break;
            case LocalDataResetMarkerReadStatus.Valid when generation.Marker == marker:
                break;
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The replacement generation marker is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException("The replacement generation marker is unavailable.");
            default:
                throw new LocalDataResetAmbiguousStateException();
        }

        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        if (completion.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The reset completion marker is unavailable.");
        if (completion.Status != LocalDataResetMarkerReadStatus.Valid
            || completion.Marker != marker)
        {
            EnsureCompletionDestinationCanBeReplaced();
            LocalDataResetAtomicFile.WriteMarker(
                _layout.CompletionPath,
                marker,
                replaceExisting: true);
            _faultHook?.Invoke(
                LocalDataResetFaultPoint.AfterCompletionCommitted,
                _layout.CompletionPath);
        }
        EnsureCompletedEpochReady(marker, rootPin);
    }

    private LocalDataResetMoveIntent? ReadMoveIntent()
    {
        var read = LocalDataResetMoveIntentCodec.Read(_layout.MoveIntentPath);
        return read.Status switch
        {
            LocalDataResetMarkerReadStatus.Absent => null,
            LocalDataResetMarkerReadStatus.Valid => read.Intent,
            LocalDataResetMarkerReadStatus.Corrupt =>
                throw new LocalDataResetCorruptStateException(
                    "The local reset move intent is corrupt."),
            _ => throw new IOException("The local reset move intent is unavailable."),
        };
    }

    private LocalDataResetMoveIntent CreateMoveIntent(
        LocalDataResetMoveKind kind,
        LocalDataResetMovePhase phase,
        Guid resetId,
        LocalDataResetFileIdentity sourceIdentity = default,
        LocalDataResetFileIdentity replacementIdentity = default)
    {
        if (ReadMoveIntent().HasValue)
            throw new LocalDataResetAmbiguousStateException();
        EnsureStableIdentityWhenRequired(sourceIdentity, allowEmpty: true);
        EnsureStableIdentityWhenRequired(
            replacementIdentity,
            allowEmpty: phase != LocalDataResetMovePhase.ReplacementPrepared);
        var intent = new LocalDataResetMoveIntent(
            kind,
            phase,
            resetId,
            sourceIdentity.VolumeSerialNumber,
            sourceIdentity.FileIndex,
            replacementIdentity.VolumeSerialNumber,
            replacementIdentity.FileIndex);
        LocalDataResetAtomicFile.WriteMoveIntent(
            _layout.MoveIntentPath,
            intent,
            replaceExisting: false,
            afterMove: _ => InvokeMoveIntentFault(intent.Phase));
        EnsureMoveIntentEquals(intent);
        return intent;
    }

    private LocalDataResetMoveIntent AdvanceMoveIntent(
        LocalDataResetMoveIntent expected,
        LocalDataResetMovePhase nextPhase,
        LocalDataResetFileIdentity replacementIdentity = default)
    {
        EnsureMoveIntentEquals(expected);
        if ((int)nextPhase != (int)expected.Phase + 1)
            throw new InvalidOperationException("The move intent phase advance is invalid.");
        EnsureStableIdentityWhenRequired(
            replacementIdentity,
            allowEmpty: nextPhase != LocalDataResetMovePhase.ReplacementPrepared);
        var next = expected with
        {
            Phase = nextPhase,
            ReplacementVolumeSerialNumber = replacementIdentity.VolumeSerialNumber,
            ReplacementFileIndex = replacementIdentity.FileIndex,
        };
        LocalDataResetAtomicFile.WriteMoveIntent(
            _layout.MoveIntentPath,
            next,
            replaceExisting: true,
            afterMove: _ => InvokeMoveIntentFault(next.Phase));
        EnsureMoveIntentEquals(next);
        return next;
    }

    private void EnsureMoveIntentEquals(LocalDataResetMoveIntent expected)
    {
        var current = LocalDataResetMoveIntentCodec.Read(_layout.MoveIntentPath);
        if (current.Status == LocalDataResetMarkerReadStatus.Corrupt)
        {
            throw new LocalDataResetCorruptStateException(
                "The local reset move intent is corrupt.");
        }
        if (current.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The local reset move intent is unavailable.");
        if (current.Status != LocalDataResetMarkerReadStatus.Valid
            || current.Intent != expected)
        {
            throw new LocalDataResetAmbiguousStateException();
        }
    }

    private void ClearMoveIntent(LocalDataResetMoveIntent expected)
    {
        _faultHook?.Invoke(
            LocalDataResetFaultPoint.BeforeMoveIntentClear,
            _layout.MoveIntentPath);
        EnsureMoveIntentEquals(expected);
        File.Delete(_layout.MoveIntentPath);
        var cleared = LocalDataResetMoveIntentCodec.Read(_layout.MoveIntentPath);
        if (cleared.Status != LocalDataResetMarkerReadStatus.Absent)
            throw new IOException("The local reset move intent could not be cleared.");
    }

    private void InvokeMoveIntentFault(LocalDataResetMovePhase phase)
    {
        var point = phase switch
        {
            LocalDataResetMovePhase.MovePrepared =>
                LocalDataResetFaultPoint.AfterMoveIntentPrepared,
            LocalDataResetMovePhase.DestinationVerified =>
                LocalDataResetFaultPoint.AfterMoveIntentDestinationVerified,
            LocalDataResetMovePhase.SourceDeleted =>
                LocalDataResetFaultPoint.AfterMoveIntentSourceDeleted,
            LocalDataResetMovePhase.ReplacementPrepared =>
                LocalDataResetFaultPoint.AfterMoveIntentReplacementPrepared,
            _ => throw new UnreachableException(),
        };
        _faultHook?.Invoke(point, _layout.MoveIntentPath);
    }

    private static void EnsureStableIdentityWhenRequired(
        LocalDataResetFileIdentity identity,
        bool allowEmpty)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (allowEmpty
            && identity.VolumeSerialNumber == 0
            && identity.FileIndex == 0)
        {
            return;
        }
        if (!identity.IsStable)
        {
            throw new LocalDataResetCorruptStateException(
                "A stable directory identity is required for reset recovery.");
        }
    }

    private static bool MoveIntentSourceMatches(
        LocalDataResetMoveIntent intent,
        LocalDataResetDirectoryPin pin) =>
        !OperatingSystem.IsWindows()
        || pin.Identity.IsStable
            && pin.Identity.VolumeSerialNumber == intent.SourceVolumeSerialNumber
            && pin.Identity.FileIndex == intent.SourceFileIndex;

    private static bool MoveIntentReplacementMatches(
        LocalDataResetMoveIntent intent,
        LocalDataResetDirectoryPin pin) =>
        !OperatingSystem.IsWindows()
        || pin.Identity.IsStable
            && pin.Identity.VolumeSerialNumber == intent.ReplacementVolumeSerialNumber
            && pin.Identity.FileIndex == intent.ReplacementFileIndex;

    private LocalDataResetMoveIntent CompleteSourceDeletion(
        LocalDataResetMoveKind kind,
        LocalDataResetMarker marker,
        bool emergency)
    {
        var intent = ReadMoveIntent();
        if (intent.HasValue
            && (intent.Value.Kind != kind || intent.Value.ResetId != marker.ResetId))
        {
            throw new LocalDataResetAmbiguousStateException();
        }

        if (emergency)
        {
            DeleteAllReservedTrashDirectories(intent?.ResetId);
            DeleteAllReplacementStagingDirectories(intent?.ResetId);
        }
        else
        {
            EnsureNoReservedTrash(marker.ResetId);
            EnsureNoReplacementStaging(intent.HasValue ? marker.ResetId : null);
        }

        var trashPath = _layout.GetTrashPath(marker.ResetId);
        var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
        var trash = LocalDataResetFileSystem.Probe(trashPath);
        EnsureDirectoryProbeIsSafe(_layout.RootPath, root);
        EnsureDirectoryProbeIsSafe(trashPath, trash);

        if (!intent.HasValue)
        {
            if (trash.Kind != LocalDataResetPathKind.Absent)
                throw new LocalDataResetAmbiguousStateException();

            if (root.Kind == LocalDataResetPathKind.Directory)
            {
                using var rootPin = LocalDataResetFileSystem.PinDirectory(
                    _layout.RootPath,
                    allowDeleteShare: true);
                EnsurePinnedRootPath(rootPin);
                EnsureOperationMarkerMatches(kind, marker);
                intent = CreateMoveIntent(
                    kind,
                    LocalDataResetMovePhase.MovePrepared,
                    marker.ResetId,
                    sourceIdentity: rootPin.Identity);
            }
            else
            {
                intent = CreateMoveIntent(
                    kind,
                    LocalDataResetMovePhase.SourceDeleted,
                    marker.ResetId);
            }
        }

        var current = intent.Value;
        if (current.Phase == LocalDataResetMovePhase.MovePrepared)
        {
            root = LocalDataResetFileSystem.Probe(_layout.RootPath);
            trash = LocalDataResetFileSystem.Probe(trashPath);
            EnsureDirectoryProbeIsSafe(_layout.RootPath, root);
            EnsureDirectoryProbeIsSafe(trashPath, trash);
            LocalDataResetDirectoryPin? trashPin = null;
            try
            {
                if (root.Kind == LocalDataResetPathKind.Directory
                    && trash.Kind == LocalDataResetPathKind.Absent)
                {
                    using var rootPin = LocalDataResetFileSystem.PinDirectory(
                        _layout.RootPath,
                        allowDeleteShare: true);
                    if (!MoveIntentSourceMatches(current, rootPin))
                    {
                        throw new LocalDataResetCorruptStateException(
                            "The reset source identity does not match its move intent.");
                    }
                    EnsureOperationMarkerMatches(kind, marker);
                    EnsureMoveIntentEquals(current);
                    LocalDataResetFileSystem.MoveWriteThrough(
                        _layout.RootPath,
                        trashPath,
                        replaceExisting: false);
                    _faultHook?.Invoke(LocalDataResetFaultPoint.AfterRootMoved, trashPath);
                    trashPin = LocalDataResetFileSystem.PinDirectory(trashPath);
                }
                else if (root.Kind == LocalDataResetPathKind.Absent
                    && trash.Kind == LocalDataResetPathKind.Directory)
                {
                    trashPin = LocalDataResetFileSystem.PinDirectory(trashPath);
                }
                else
                {
                    throw new LocalDataResetAmbiguousStateException();
                }

                if (!MoveIntentSourceMatches(current, trashPin))
                {
                    throw new LocalDataResetCorruptStateException(
                        "The reset destination identity does not match its move intent.");
                }
                if (LocalDataResetFileSystem.Probe(_layout.RootPath).Kind
                    != LocalDataResetPathKind.Absent)
                {
                    throw new LocalDataResetAmbiguousStateException();
                }
                current = AdvanceMoveIntent(
                    current,
                    LocalDataResetMovePhase.DestinationVerified);
            }
            finally
            {
                trashPin?.Dispose();
            }
        }

        if (current.Phase == LocalDataResetMovePhase.DestinationVerified)
        {
            root = LocalDataResetFileSystem.Probe(_layout.RootPath);
            trash = LocalDataResetFileSystem.Probe(trashPath);
            if (root.Kind == LocalDataResetPathKind.Unavailable)
                throw new IOException("The reset root is unavailable after its move.");
            if (root.Kind is LocalDataResetPathKind.File
                or LocalDataResetPathKind.ReparsePoint)
            {
                throw new LocalDataResetUnsafePathException(_layout.RootPath);
            }
            if (root.Kind != LocalDataResetPathKind.Absent)
                throw new LocalDataResetAmbiguousStateException();
            if (trash.Kind == LocalDataResetPathKind.Directory)
            {
                using var trashPin = LocalDataResetFileSystem.PinDirectory(trashPath);
                if (!MoveIntentSourceMatches(current, trashPin))
                {
                    throw new LocalDataResetCorruptStateException(
                        "The verified reset destination identity changed.");
                }
                EnsureMoveIntentEquals(current);
                DeleteDirectoryWithoutFollowingReparsePoints(trashPath, trashPin);
                _faultHook?.Invoke(LocalDataResetFaultPoint.AfterTrashDeleted, trashPath);
            }
            else if (trash.Kind != LocalDataResetPathKind.Absent)
            {
                EnsureDirectoryProbeIsSafe(trashPath, trash);
                throw new LocalDataResetAmbiguousStateException();
            }

            EnsureDirectoryAbsent(_layout.RootPath);
            EnsureDirectoryAbsent(trashPath);
            current = AdvanceMoveIntent(
                current,
                LocalDataResetMovePhase.SourceDeleted);
        }

        if (current.Phase == LocalDataResetMovePhase.SourceDeleted)
        {
            EnsureDirectoryAbsent(_layout.RootPath);
            EnsureDirectoryAbsent(trashPath);
            RemoveUncommittedEmptyStaging(current, marker);
        }
        else if (current.Phase == LocalDataResetMovePhase.ReplacementPrepared)
        {
            EnsureDirectoryAbsent(trashPath);
        }
        else
        {
            throw new LocalDataResetAmbiguousStateException();
        }

        return current;
    }

    private LocalDataResetMoveIntent PrepareReplacement(
        LocalDataResetMoveIntent intent,
        LocalDataResetMoveKind kind,
        LocalDataResetMarker marker)
    {
        if (intent.Phase == LocalDataResetMovePhase.ReplacementPrepared)
            return intent;
        if (intent.Phase != LocalDataResetMovePhase.SourceDeleted)
            throw new LocalDataResetAmbiguousStateException();

        EnsureOperationMarkerMatches(kind, marker);
        EnsureDirectoryAbsent(_layout.RootPath);
        EnsureDirectoryAbsent(_layout.GetTrashPath(marker.ResetId));
        EnsurePathIsAbsent(_layout.GetReplacementStagingPath(marker.ResetId));
        DeleteOwnedStreamingTemporaryFiles();
        _faultHook?.Invoke(LocalDataResetFaultPoint.BeforeExternalCleanup, null);
        _externalCleanup();
        _faultHook?.Invoke(LocalDataResetFaultPoint.AfterExternalCleanup, null);
        EnsureDirectoryAbsent(_layout.RootPath);
        EnsureDirectoryAbsent(_layout.GetTrashPath(marker.ResetId));

        var stagingPath = _layout.GetReplacementStagingPath(marker.ResetId);
        Directory.CreateDirectory(stagingPath);
        using var stagingPin = LocalDataResetFileSystem.PinDirectory(stagingPath);
        if (Directory.GetFileSystemEntries(stagingPath).Length != 0)
            throw new LocalDataResetAmbiguousStateException();
        _faultHook?.Invoke(
            LocalDataResetFaultPoint.AfterReplacementStagingCreatedBeforeIntent,
            stagingPath);
        EnsureOperationMarkerMatches(kind, marker);
        return AdvanceMoveIntent(
            intent,
            LocalDataResetMovePhase.ReplacementPrepared,
            stagingPin.Identity);
    }

    private void RemoveUncommittedEmptyStaging(
        LocalDataResetMoveIntent intent,
        LocalDataResetMarker marker)
    {
        var stagingPath = _layout.GetReplacementStagingPath(marker.ResetId);
        var staging = LocalDataResetFileSystem.Probe(stagingPath);
        if (staging.Kind == LocalDataResetPathKind.Absent)
            return;
        if (staging.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The uncommitted replacement staging is unavailable.");
        if (staging.Kind != LocalDataResetPathKind.Directory)
            throw new LocalDataResetUnsafePathException(stagingPath);

        using var stagingPin = LocalDataResetFileSystem.PinDirectory(stagingPath);
        if (Directory.GetFileSystemEntries(stagingPath).Length != 0)
            throw new LocalDataResetAmbiguousStateException();
        EnsureMoveIntentEquals(intent);
        DeleteDirectoryWithoutFollowingReparsePoints(stagingPath, stagingPin);
        EnsureDirectoryAbsent(stagingPath);
    }

    private LocalDataResetDirectoryPin InstallReplacement(
        LocalDataResetMoveIntent intent,
        LocalDataResetMoveKind kind,
        LocalDataResetMarker marker)
    {
        if (intent.Phase != LocalDataResetMovePhase.ReplacementPrepared)
            throw new LocalDataResetAmbiguousStateException();
        var stagingPath = _layout.GetReplacementStagingPath(marker.ResetId);
        var staging = LocalDataResetFileSystem.Probe(stagingPath);
        var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
        EnsureDirectoryProbeIsSafe(stagingPath, staging);
        EnsureDirectoryProbeIsSafe(_layout.RootPath, root);

        if (staging.Kind == LocalDataResetPathKind.Directory
            && root.Kind == LocalDataResetPathKind.Absent)
        {
            using var stagingPin = LocalDataResetFileSystem.PinDirectory(
                stagingPath,
                allowDeleteShare: true);
            if (!MoveIntentReplacementMatches(intent, stagingPin))
            {
                throw new LocalDataResetCorruptStateException(
                    "The replacement staging identity changed.");
            }
            if (Directory.GetFileSystemEntries(stagingPath).Length != 0)
                throw new LocalDataResetAmbiguousStateException();
            EnsureOperationMarkerMatches(kind, marker);
            EnsureMoveIntentEquals(intent);
            LocalDataResetFileSystem.MoveWriteThrough(
                stagingPath,
                _layout.RootPath,
                replaceExisting: false);
            var installedPin = LocalDataResetFileSystem.PinDirectory(_layout.RootPath);
            try
            {
                if (!MoveIntentReplacementMatches(intent, installedPin))
                {
                    throw new LocalDataResetCorruptStateException(
                        "The installed replacement identity changed.");
                }
                _faultHook?.Invoke(
                    LocalDataResetFaultPoint.AfterRootCreatedBeforeGeneration,
                    _layout.RootPath);
                return installedPin;
            }
            catch
            {
                installedPin.Dispose();
                throw;
            }
        }

        if (staging.Kind == LocalDataResetPathKind.Absent
            && root.Kind == LocalDataResetPathKind.Directory)
        {
            var installedPin = LocalDataResetFileSystem.PinDirectory(_layout.RootPath);
            try
            {
                if (!MoveIntentReplacementMatches(intent, installedPin))
                {
                    throw new LocalDataResetCorruptStateException(
                        "The installed replacement identity does not match its intent.");
                }
                _faultHook?.Invoke(
                    LocalDataResetFaultPoint.AfterRootCreatedBeforeGeneration,
                    _layout.RootPath);
                return installedPin;
            }
            catch
            {
                installedPin.Dispose();
                throw;
            }
        }

        throw new LocalDataResetAmbiguousStateException();
    }

    private void EnsureOperationMarkerMatches(
        LocalDataResetMoveKind kind,
        LocalDataResetMarker marker)
    {
        if (kind == LocalDataResetMoveKind.Normal)
            EnsureRequestMarkerMatches(marker);
        else
            EnsureEmergencyMarkerMatches(marker);
    }

    private LocalDataResetMarkerReadResult ValidateEmergencyRequestPaths(
        LocalDataResetDirectoryPin localDataPin)
    {
        ArgumentNullException.ThrowIfNull(localDataPin);
        EnsurePinnedParentPath(localDataPin);
        EnsureControlRootPathIsDirectory();
        EnsureRegularFileOrAbsent(_layout.MarkerPath);
        EnsureRegularFileOrAbsent(_layout.RequestLockPath);
        EnsureRegularFileOrAbsent(_layout.MoveIntentPath);
        EnsureRegularFileOrAbsent(_layout.CompletionPath);
        EnsureRegularFileOrAbsent(_layout.EmergencyMarkerPath);
        ValidateRootAndGenerationShapes();
        ValidateTemporaryDirectoryShape();
        ValidateReservedTrashDirectoryShapes();
        ValidateReplacementStagingDirectoryShapes();
        return LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
    }

    private void ValidateEmergencyExecutionPaths(
        LocalDataResetDirectoryPin localDataPin,
        LocalDataResetMarker marker)
    {
        ValidateEmergencyRequestPaths(localDataPin);
        EnsureEmergencyMarkerMatches(marker);
    }

    private void EnsurePinnedParentPath(LocalDataResetDirectoryPin localDataPin)
    {
        var parent = LocalDataResetFileSystem.Probe(
            _layout.LocalApplicationDataDirectory);
        if (parent.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The local application data directory is unavailable.");
        if (parent.Kind != LocalDataResetPathKind.Directory)
        {
            throw new LocalDataResetUnsafePathException(
                _layout.LocalApplicationDataDirectory);
        }
    }

    private void EnsureControlRootPathIsDirectory()
    {
        var control = LocalDataResetFileSystem.Probe(_layout.ControlRoot);
        if (control.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The local reset control directory is unavailable.");
        if (control.Kind != LocalDataResetPathKind.Directory)
            throw new LocalDataResetUnsafePathException(_layout.ControlRoot);
    }

    private void ValidateRootAndGenerationShapes()
    {
        var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
        if (root.Kind == LocalDataResetPathKind.Absent)
            return;
        if (root.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The local data root is unavailable.");
        if (root.Kind != LocalDataResetPathKind.Directory)
            throw new LocalDataResetUnsafePathException(_layout.RootPath);

        using var rootPin = LocalDataResetFileSystem.PinDirectory(_layout.RootPath);
        EnsurePinnedRootPath(rootPin);
        EnsureRegularFileOrAbsent(_layout.GenerationPath);
    }

    private void ValidateTemporaryDirectoryShape()
    {
        var temporary = LocalDataResetFileSystem.Probe(_layout.TempDirectory);
        if (temporary.Kind == LocalDataResetPathKind.Absent)
            return;
        if (temporary.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The temporary directory is unavailable.");
        if (temporary.Kind != LocalDataResetPathKind.Directory)
            throw new LocalDataResetUnsafePathException(_layout.TempDirectory);
        using var temporaryPin = LocalDataResetFileSystem.PinDirectory(
            _layout.TempDirectory);
    }

    private static void EnsureRegularFileOrAbsent(string path)
    {
        var probe = LocalDataResetFileSystem.Probe(path);
        if (probe.Kind is LocalDataResetPathKind.Absent
            or LocalDataResetPathKind.File)
        {
            return;
        }
        if (probe.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException($"The local reset file is unavailable: {path}");
        throw new LocalDataResetUnsafePathException(path);
    }

    private void ValidateReservedTrashDirectoryShapes()
    {
        foreach (var path in GetReservedTrashPaths())
        {
            var probe = LocalDataResetFileSystem.Probe(path);
            if (probe.Kind == LocalDataResetPathKind.Unavailable)
            {
                throw new IOException(
                    $"The reserved reset directory is unavailable: {path}");
            }
            if (probe.Kind != LocalDataResetPathKind.Directory)
                throw new LocalDataResetUnsafePathException(path);
            using var trashPin = LocalDataResetFileSystem.PinDirectory(path);
        }
    }

    private void ValidateReplacementStagingDirectoryShapes()
    {
        foreach (var path in GetReplacementStagingPaths())
        {
            var probe = LocalDataResetFileSystem.Probe(path);
            if (probe.Kind == LocalDataResetPathKind.Unavailable)
            {
                throw new IOException(
                    $"The replacement staging directory is unavailable: {path}");
            }
            if (probe.Kind != LocalDataResetPathKind.Directory)
                throw new LocalDataResetUnsafePathException(path);
            using var stagingPin = LocalDataResetFileSystem.PinDirectory(path);
        }
    }

    private void DeleteAllReservedTrashDirectories(Guid? excludedResetId = null)
    {
        foreach (var path in GetReservedTrashPaths())
        {
            if (excludedResetId.HasValue
                && IsPathForResetId(
                    path,
                    _layout.GetTrashPath(excludedResetId.Value)))
            {
                continue;
            }
            var probe = LocalDataResetFileSystem.Probe(path);
            if (probe.Kind == LocalDataResetPathKind.Absent)
                continue;
            if (probe.Kind == LocalDataResetPathKind.Unavailable)
            {
                throw new IOException(
                    $"The reserved reset directory is unavailable: {path}");
            }
            if (probe.Kind != LocalDataResetPathKind.Directory)
                throw new LocalDataResetUnsafePathException(path);
            var trashPin = LocalDataResetFileSystem.PinDirectory(path);
            DeleteDirectoryWithoutFollowingReparsePoints(path, trashPin);
        }
    }

    private void DeleteAllReplacementStagingDirectories(Guid? excludedResetId = null)
    {
        foreach (var path in GetReplacementStagingPaths())
        {
            if (excludedResetId.HasValue
                && IsPathForResetId(
                    path,
                    _layout.GetReplacementStagingPath(excludedResetId.Value)))
            {
                continue;
            }
            var probe = LocalDataResetFileSystem.Probe(path);
            if (probe.Kind == LocalDataResetPathKind.Absent)
                continue;
            if (probe.Kind == LocalDataResetPathKind.Unavailable)
            {
                throw new IOException(
                    $"The replacement staging directory is unavailable: {path}");
            }
            if (probe.Kind != LocalDataResetPathKind.Directory)
                throw new LocalDataResetUnsafePathException(path);
            var stagingPin = LocalDataResetFileSystem.PinDirectory(path);
            DeleteDirectoryWithoutFollowingReparsePoints(path, stagingPin);
        }
    }

    private string[] GetReservedTrashPaths()
    {
        var candidates = Directory.GetFileSystemEntries(
            _layout.LocalApplicationDataDirectory,
            ".WinVaultWarden.reset-*.trash",
            SearchOption.TopDirectoryOnly);
        var paths = candidates
            .Where(path => LocalDataResetFileSystem.IsReservedTrashName(
                Path.GetFileName(path)))
            .ToArray();
        Array.Sort(paths, StringComparer.Ordinal);
        return paths;
    }

    private string[] GetReplacementStagingPaths()
    {
        var candidates = Directory.GetFileSystemEntries(
            _layout.LocalApplicationDataDirectory,
            ".WinVaultWarden.new-*.staging",
            SearchOption.TopDirectoryOnly);
        var paths = candidates
            .Where(path => LocalDataResetFileSystem.IsReplacementStagingName(
                Path.GetFileName(path)))
            .ToArray();
        Array.Sort(paths, StringComparer.Ordinal);
        return paths;
    }

    private void EnsureNoReplacementStaging(Guid? allowedResetId = null)
    {
        foreach (var path in GetReplacementStagingPaths())
        {
            if (allowedResetId.HasValue
                && IsPathForResetId(
                    path,
                    _layout.GetReplacementStagingPath(allowedResetId.Value)))
            {
                continue;
            }
            var probe = LocalDataResetFileSystem.Probe(path);
            if (probe.Kind == LocalDataResetPathKind.Directory)
                throw new LocalDataResetAmbiguousStateException();
            if (probe.Kind == LocalDataResetPathKind.Unavailable)
            {
                throw new IOException(
                    $"The replacement staging directory is unavailable: {path}");
            }
            throw new LocalDataResetUnsafePathException(path);
        }
    }

    private static bool IsPathForResetId(string path, string expectedPath) =>
        string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(expectedPath),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private void QuarantineNormalRequest(LocalDataResetMarker marker)
    {
        var quarantinePath = _layout.GetEmergencyRequestQuarantinePath(marker.ResetId);
        EnsureRegularFileOrAbsent(_layout.MarkerPath);
        EnsureRegularFileOrAbsent(quarantinePath);

        var request = LocalDataResetFileSystem.Probe(_layout.MarkerPath);
        var quarantine = LocalDataResetFileSystem.Probe(quarantinePath);
        if (request.Kind == LocalDataResetPathKind.Absent)
        {
            if (quarantine.Kind == LocalDataResetPathKind.File)
                LocalDataResetFileSystem.TryDeleteFile(quarantinePath);
            return;
        }

        try
        {
            LocalDataResetFileSystem.MoveWriteThrough(
                _layout.MarkerPath,
                quarantinePath,
                replaceExisting: true);
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            throw new IOException(
                "The reset request quarantine outcome is uncertain.",
                exception);
        }

        if (LocalDataResetFileSystem.Probe(_layout.MarkerPath).Kind
                != LocalDataResetPathKind.Absent
            || LocalDataResetFileSystem.Probe(quarantinePath).Kind
                != LocalDataResetPathKind.File)
        {
            throw new IOException("The reset request was not isolated durably.");
        }
        _faultHook?.Invoke(
            LocalDataResetFaultPoint.AfterNormalRequestQuarantinedBeforeDelete,
            quarantinePath);
        LocalDataResetFileSystem.TryDeleteFile(quarantinePath);
    }

    private void EnsureNormalRequestAbsent()
    {
        var request = LocalDataResetFileSystem.Probe(_layout.MarkerPath);
        if (request.Kind == LocalDataResetPathKind.Absent)
            return;
        if (request.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The normal reset request is unavailable.");
        if (request.Kind is LocalDataResetPathKind.Directory
            or LocalDataResetPathKind.ReparsePoint)
        {
            throw new LocalDataResetUnsafePathException(_layout.MarkerPath);
        }
        throw new LocalDataResetAmbiguousStateException();
    }

    private void EnsureEmergencyMarkerMatches(LocalDataResetMarker marker)
    {
        var emergency = LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
        if (emergency.Status == LocalDataResetMarkerReadStatus.Corrupt)
        {
            throw new LocalDataResetCorruptStateException(
                "The emergency reset marker is corrupt.");
        }
        if (emergency.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The emergency reset marker is unavailable.");
        if (emergency.Status != LocalDataResetMarkerReadStatus.Valid
            || emergency.Marker != marker)
        {
            throw new LocalDataResetAmbiguousStateException();
        }
    }

    private void DeleteEmergencyMarker(LocalDataResetMarker marker)
    {
        _faultHook?.Invoke(
            LocalDataResetFaultPoint.BeforeEmergencyMarkerCommit,
            _layout.EmergencyMarkerPath);
        EnsureEmergencyMarkerMatches(marker);
        EnsureNormalRequestAbsent();
        File.Delete(_layout.EmergencyMarkerPath);
        var deleted = LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
        if (deleted.Status != LocalDataResetMarkerReadStatus.Absent)
            throw new IOException("The completed emergency reset could not be cleared.");
    }

    private void ExecuteReset(LocalDataResetMarker marker)
    {
        using var localDataPin = LocalDataResetFileSystem.PinDirectory(
            _layout.LocalApplicationDataDirectory);
        var existingIntent = ReadMoveIntent();
        if (!existingIntent.HasValue
            || existingIntent.Value.Phase
                != LocalDataResetMovePhase.ReplacementPrepared)
        {
            EnsureRequestBaseline(marker);
        }
        EnsureRequestMarkerMatches(marker);
        EnsureCompletionDestinationCanBeReplaced();
        var intent = CompleteSourceDeletion(
            LocalDataResetMoveKind.Normal,
            marker,
            emergency: false);
        intent = PrepareReplacement(
            intent,
            LocalDataResetMoveKind.Normal,
            marker);
        using var rootPin = InstallReplacement(
            intent,
            LocalDataResetMoveKind.Normal,
            marker);
        CommitReplacementEpoch(marker, rootPin);
        DeleteRequestMarker(marker, rootPin);
        ClearMoveIntent(intent);
    }

    private void EnsureRequestBaseline(LocalDataResetMarker marker)
    {
        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        if (completion.Status == LocalDataResetMarkerReadStatus.Corrupt)
        {
            throw new LocalDataResetCorruptStateException(
                "The completed reset marker is corrupt.");
        }
        if (completion.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The completed reset marker is unavailable.");

        if (marker.BaseResetId == Guid.Empty)
        {
            if (completion.Status != LocalDataResetMarkerReadStatus.Absent)
                throw new LocalDataResetAmbiguousStateException();
        }
        else if (completion.Status != LocalDataResetMarkerReadStatus.Valid
            || completion.Marker.ResetId != marker.BaseResetId)
        {
            throw new LocalDataResetAmbiguousStateException();
        }

        var generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        switch (generation.Status)
        {
            case LocalDataResetMarkerReadStatus.Absent:
                return;
            case LocalDataResetMarkerReadStatus.Valid
                when marker.BaseResetId != Guid.Empty
                    && completion.Status == LocalDataResetMarkerReadStatus.Valid
                    && generation.Marker == completion.Marker:
                return;
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The local data generation marker is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException("The local data generation marker is unavailable.");
            default:
                throw new LocalDataResetAmbiguousStateException();
        }
    }

    private bool TryReplayCompletedReset(LocalDataResetMarker marker)
    {
        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        switch (completion.Status)
        {
            case LocalDataResetMarkerReadStatus.Absent:
                return false;
            case LocalDataResetMarkerReadStatus.Valid when completion.Marker != marker:
                return false;
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The completed reset marker is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException("The completed reset marker is unavailable.");
            case LocalDataResetMarkerReadStatus.Valid:
                break;
            default:
                throw new UnreachableException();
        }

        using var rootPin = PinCompletedRoot();
        EnsureCompletedEpochReady(marker, rootPin);
        DeleteRequestMarker(marker, rootPin);
        return true;
    }

    private bool TryCompleteGenerationCommit(LocalDataResetMarker marker)
    {
        var generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        if (generation.Status != LocalDataResetMarkerReadStatus.Valid
            || generation.Marker != marker)
        {
            return false;
        }

        using var rootPin = PinCompletedRoot();
        generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        switch (generation.Status)
        {
            case LocalDataResetMarkerReadStatus.Valid when generation.Marker == marker:
                break;
            case LocalDataResetMarkerReadStatus.Valid:
            case LocalDataResetMarkerReadStatus.Absent:
                throw new LocalDataResetAmbiguousStateException();
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The partially committed generation marker is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException(
                    "The partially committed generation marker is unavailable.");
            default:
                throw new UnreachableException();
        }
        EnsureNoReservedTrash();

        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        if (completion.Status == LocalDataResetMarkerReadStatus.Corrupt)
        {
            throw new LocalDataResetCorruptStateException(
                "The completed reset marker is corrupt.");
        }
        if (completion.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The completed reset marker is unavailable.");

        var baselineMatches = marker.BaseResetId == Guid.Empty
            ? completion.Status == LocalDataResetMarkerReadStatus.Absent
            : completion.Status == LocalDataResetMarkerReadStatus.Valid
                && completion.Marker.ResetId == marker.BaseResetId;
        if (!baselineMatches)
            throw new LocalDataResetAmbiguousStateException();

        EnsureCompletionDestinationCanBeReplaced();
        LocalDataResetAtomicFile.WriteMarker(
            _layout.CompletionPath,
            marker,
            replaceExisting: true);
        _faultHook?.Invoke(
            LocalDataResetFaultPoint.AfterCompletionCommitted,
            _layout.CompletionPath);
        EnsureCompletedEpochReady(marker, rootPin);
        DeleteRequestMarker(marker, rootPin);
        return true;
    }

    private void PrepareReadyLease(LocalDataResetFileLease sharedLease)
    {
        ArgumentNullException.ThrowIfNull(sharedLease);
        EnsureMoveIntentAbsent();
        EnsureNoReplacementStaging();
        EnsureEmergencyMarkerAbsent();
        EnsureRequestMarkerAbsent();
        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        LocalDataResetMarker? completedMarker;
        switch (completion.Status)
        {
            case LocalDataResetMarkerReadStatus.Absent:
                EnsureLegacyGenerationAbsent();
                EnsureNoReservedTrash();
                completedMarker = null;
                break;
            case LocalDataResetMarkerReadStatus.Valid:
                completedMarker = completion.Marker;
                break;
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The completed reset marker is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException("The completed reset marker is unavailable.");
            default:
                throw new UnreachableException();
        }

        LocalDataResetDirectoryPin? rootPin = null;
        try
        {
            rootPin = completedMarker.HasValue
                ? PinCompletedRoot()
                : CreateAndPinLegacyRoot();
            EnsureReadyStateAfterRootPin(completedMarker, rootPin);
            EnsureReadyStateAfterRootPin(completedMarker, rootPin);
            EnsureNoReplacementStaging();
            sharedLease.Attach(rootPin);
            rootPin = null;
        }
        finally
        {
            rootPin?.Dispose();
        }
    }

    private LocalDataResetDirectoryPin CreateAndPinLegacyRoot()
    {
        var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
        if (root.Kind == LocalDataResetPathKind.Absent)
        {
            Directory.CreateDirectory(_layout.RootPath);
            root = LocalDataResetFileSystem.Probe(_layout.RootPath);
        }

        if (root.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The legacy local data root is unavailable.");
        if (root.Kind != LocalDataResetPathKind.Directory)
            throw new LocalDataResetUnsafePathException(_layout.RootPath);
        return LocalDataResetFileSystem.PinDirectory(_layout.RootPath);
    }

    private LocalDataResetDirectoryPin PinCompletedRoot()
    {
        var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
        return root.Kind switch
        {
            LocalDataResetPathKind.Directory =>
                LocalDataResetFileSystem.PinDirectory(_layout.RootPath),
            LocalDataResetPathKind.Absent =>
                throw new LocalDataResetAmbiguousStateException(),
            LocalDataResetPathKind.Unavailable =>
                throw new IOException("The completed local data root is unavailable."),
            _ => throw new LocalDataResetUnsafePathException(_layout.RootPath),
        };
    }

    private void EnsureReadyStateAfterRootPin(
        LocalDataResetMarker? completedMarker,
        LocalDataResetDirectoryPin rootPin)
    {
        EnsureMoveIntentAbsent();
        EnsureNoReplacementStaging();
        EnsureEmergencyMarkerAbsent();
        EnsureRequestMarkerAbsent();
        if (completedMarker.HasValue)
        {
            EnsureCompletedEpochReady(completedMarker.Value, rootPin);
            return;
        }

        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        switch (completion.Status)
        {
            case LocalDataResetMarkerReadStatus.Absent:
                break;
            case LocalDataResetMarkerReadStatus.Valid:
                throw new LocalDataResetAmbiguousStateException();
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The completed reset marker is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException("The completed reset marker is unavailable.");
            default:
                throw new UnreachableException();
        }

        EnsurePinnedRootPath(rootPin);
        EnsureLegacyGenerationAbsent();
        EnsureNoReservedTrash();
    }

    private void EnsureLegacyGenerationAbsent()
    {
        var generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        switch (generation.Status)
        {
            case LocalDataResetMarkerReadStatus.Absent:
                return;
            case LocalDataResetMarkerReadStatus.Valid:
                throw new LocalDataResetAmbiguousStateException();
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "A root generation exists without a completed reset marker.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException(
                    "The root generation cannot be checked for legacy startup.");
            default:
                throw new UnreachableException();
        }
    }

    private void EnsureCompletedEpochReady(
        LocalDataResetMarker marker,
        LocalDataResetDirectoryPin rootPin)
    {
        ArgumentNullException.ThrowIfNull(rootPin);
        var completion = LocalDataResetMarkerCodec.Read(_layout.CompletionPath);
        switch (completion.Status)
        {
            case LocalDataResetMarkerReadStatus.Valid when completion.Marker == marker:
                break;
            case LocalDataResetMarkerReadStatus.Valid:
            case LocalDataResetMarkerReadStatus.Absent:
                throw new LocalDataResetAmbiguousStateException();
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The completed reset marker is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException("The completed reset marker is unavailable.");
            default:
                throw new UnreachableException();
        }

        EnsurePinnedRootPath(rootPin);

        var generation = LocalDataResetMarkerCodec.Read(_layout.GenerationPath);
        switch (generation.Status)
        {
            case LocalDataResetMarkerReadStatus.Valid when generation.Marker == marker:
                break;
            case LocalDataResetMarkerReadStatus.Valid:
            case LocalDataResetMarkerReadStatus.Absent:
                throw new LocalDataResetAmbiguousStateException();
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The local data generation marker is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException("The local data generation marker is unavailable.");
            default:
                throw new UnreachableException();
        }

        EnsureNoReservedTrash();
    }

    private void EnsurePinnedRootPath(LocalDataResetDirectoryPin rootPin)
    {
        ArgumentNullException.ThrowIfNull(rootPin);
        var root = LocalDataResetFileSystem.Probe(_layout.RootPath);
        switch (root.Kind)
        {
            case LocalDataResetPathKind.Directory:
                break;
            case LocalDataResetPathKind.Absent:
                throw new LocalDataResetAmbiguousStateException();
            case LocalDataResetPathKind.Unavailable:
                throw new IOException("The pinned local data root is unavailable.");
            default:
                throw new LocalDataResetUnsafePathException(_layout.RootPath);
        }
    }

    private void EnsureRequestMarkerAbsent()
    {
        var request = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
        switch (request.Status)
        {
            case LocalDataResetMarkerReadStatus.Absent:
                return;
            case LocalDataResetMarkerReadStatus.Valid:
                throw new LocalDataResetAmbiguousStateException();
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The reset request marker is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException("The reset request marker is unavailable.");
            default:
                throw new UnreachableException();
        }
    }

    private void EnsureEmergencyMarkerAbsent()
    {
        var emergency = LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
        switch (emergency.Status)
        {
            case LocalDataResetMarkerReadStatus.Absent:
                return;
            case LocalDataResetMarkerReadStatus.Valid:
                throw new LocalDataResetAmbiguousStateException();
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The emergency reset marker is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException("The emergency reset marker is unavailable.");
            default:
                throw new UnreachableException();
        }
    }

    private void EnsureMoveIntentAbsent()
    {
        var intent = LocalDataResetMoveIntentCodec.Read(_layout.MoveIntentPath);
        switch (intent.Status)
        {
            case LocalDataResetMarkerReadStatus.Absent:
                return;
            case LocalDataResetMarkerReadStatus.Valid:
                throw new LocalDataResetAmbiguousStateException();
            case LocalDataResetMarkerReadStatus.Corrupt:
                throw new LocalDataResetCorruptStateException(
                    "The local reset move intent is corrupt.");
            case LocalDataResetMarkerReadStatus.Unavailable:
                throw new IOException("The local reset move intent is unavailable.");
            default:
                throw new UnreachableException();
        }
    }

    private void EnsureRequestMarkerMatches(LocalDataResetMarker marker)
    {
        var request = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
        if (request.Status == LocalDataResetMarkerReadStatus.Corrupt)
        {
            throw new LocalDataResetCorruptStateException(
                "The reset request marker is corrupt.");
        }
        if (request.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The reset request marker is unavailable.");
        if (request.Status != LocalDataResetMarkerReadStatus.Valid
            || request.Marker != marker)
        {
            throw new InvalidDataException("The reset request changed before completion.");
        }
    }

    private void DeleteRequestMarker(
        LocalDataResetMarker marker,
        LocalDataResetDirectoryPin rootPin)
    {
        _faultHook?.Invoke(LocalDataResetFaultPoint.BeforeMarkerCommit, _layout.MarkerPath);
        EnsureCompletedEpochReady(marker, rootPin);

        EnsureRequestMarkerMatches(marker);

        File.Delete(_layout.MarkerPath);
        var deleted = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
        if (deleted.Status != LocalDataResetMarkerReadStatus.Absent)
            throw new IOException("The completed reset request could not be cleared.");
    }

    private void EnsureCompletionDestinationCanBeReplaced()
    {
        var completion = LocalDataResetFileSystem.Probe(_layout.CompletionPath);
        if (completion.Kind is LocalDataResetPathKind.Absent
            or LocalDataResetPathKind.File)
        {
            return;
        }
        if (completion.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The completed reset marker cannot be inspected.");
        throw new LocalDataResetUnsafePathException(_layout.CompletionPath);
    }

    private static void EnsurePathIsAbsent(string path)
    {
        var probe = LocalDataResetFileSystem.Probe(path);
        if (probe.Kind == LocalDataResetPathKind.Absent)
            return;
        if (probe.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException("The local reset marker destination is unavailable.");
        throw new LocalDataResetUnsafePathException(path);
    }

    private static void EnsureMarkerWriteMatches(
        string path,
        LocalDataResetMarker marker)
    {
        var written = LocalDataResetMarkerCodec.Read(path);
        if (written.Status == LocalDataResetMarkerReadStatus.Unavailable)
            throw new IOException("The local reset marker could not be verified.");
        if (written.Status != LocalDataResetMarkerReadStatus.Valid
            || written.Marker != marker)
        {
            throw new InvalidDataException("The local reset marker did not match its write.");
        }
    }

    private LocalDataResetStartupResult AcquireReadyLease(bool resetCompleted)
    {
        LocalDataResetFileLease? sharedLease;
        try
        {
            sharedLease = TryAcquireSharedLease();
        }
        catch (LocalDataResetUnsafePathException)
        {
            return new(LocalDataResetStartupStatus.UnsafePath, resetCompleted, null);
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            return new(LocalDataResetStartupStatus.Unavailable, resetCompleted, null);
        }

        if (sharedLease is null)
        {
            return new(
                LocalDataResetStartupStatus.Contended,
                resetCompleted,
                DataLease: null);
        }

        var moveIntent = LocalDataResetMoveIntentCodec.Read(_layout.MoveIntentPath);
        if (moveIntent.Status != LocalDataResetMarkerReadStatus.Absent)
        {
            sharedLease.Dispose();
            return moveIntent.Status switch
            {
                LocalDataResetMarkerReadStatus.Valid =>
                    ResumeMoveIntent(moveIntent.Intent),
                LocalDataResetMarkerReadStatus.Corrupt => new(
                    LocalDataResetStartupStatus.Corrupt,
                    resetCompleted,
                    null),
                _ => new(
                    LocalDataResetStartupStatus.Unavailable,
                    resetCompleted,
                    null),
            };
        }

        var emergency = LocalDataResetMarkerCodec.Read(_layout.EmergencyMarkerPath);
        if (emergency.Status != LocalDataResetMarkerReadStatus.Absent)
        {
            sharedLease.Dispose();
            return emergency.Status switch
            {
                LocalDataResetMarkerReadStatus.Valid =>
                    ResumeEmergencyReset(emergency.Marker),
                LocalDataResetMarkerReadStatus.Corrupt => new(
                    LocalDataResetStartupStatus.Corrupt,
                    resetCompleted,
                    null),
                _ => new(
                    LocalDataResetStartupStatus.Unavailable,
                    resetCompleted,
                    null),
            };
        }

        var marker = LocalDataResetMarkerCodec.Read(_layout.MarkerPath);
        if (marker.Status == LocalDataResetMarkerReadStatus.Absent)
        {
            try
            {
                PrepareReadyLease(sharedLease);
            }
            catch (LocalDataResetAmbiguousStateException)
            {
                sharedLease.Dispose();
                return new(LocalDataResetStartupStatus.Ambiguous, resetCompleted, null);
            }
            catch (LocalDataResetUnsafePathException)
            {
                sharedLease.Dispose();
                return new(LocalDataResetStartupStatus.UnsafePath, resetCompleted, null);
            }
            catch (LocalDataResetCorruptStateException)
            {
                sharedLease.Dispose();
                return new(LocalDataResetStartupStatus.Corrupt, resetCompleted, null);
            }
            catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
            {
                sharedLease.Dispose();
                return new(LocalDataResetStartupStatus.Unavailable, resetCompleted, null);
            }
            return new(
                LocalDataResetStartupStatus.Ready,
                resetCompleted,
                sharedLease);
        }

        sharedLease.Dispose();
        return marker.Status switch
        {
            LocalDataResetMarkerReadStatus.Corrupt => new(
                LocalDataResetStartupStatus.Corrupt,
                resetCompleted,
                null),
            LocalDataResetMarkerReadStatus.Unavailable => new(
                LocalDataResetStartupStatus.Unavailable,
                resetCompleted,
                null),
            _ => new(
                LocalDataResetStartupStatus.Contended,
                resetCompleted,
                null),
        };
    }

    private LocalDataResetStartupResult ReleaseExclusiveAndAcquireReadyLease(
        LocalDataResetFileLease exclusiveLease)
    {
        try
        {
            exclusiveLease.Dispose();
        }
        catch (Exception exception) when (!LocalDataResetFileSystem.IsFatal(exception))
        {
            return new(
                LocalDataResetStartupStatus.Unavailable,
                ResetCompleted: true,
                DataLease: null);
        }

        return AcquireReadyLease(resetCompleted: true);
    }

    private static void EnsureDirectoryProbeIsSafe(
        string path,
        LocalDataResetPathProbe probe)
    {
        if (probe.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException($"The reset directory could not be inspected: {path}");
        if (probe.Kind is LocalDataResetPathKind.File
            or LocalDataResetPathKind.ReparsePoint)
        {
            throw new LocalDataResetUnsafePathException(path);
        }
    }

    private static void EnsureDirectoryAbsent(string path)
    {
        var probe = LocalDataResetFileSystem.Probe(path);
        if (probe.Kind == LocalDataResetPathKind.Absent)
            return;
        if (probe.Kind == LocalDataResetPathKind.Unavailable)
            throw new IOException($"The reset directory could not be verified: {path}");
        if (probe.Kind is LocalDataResetPathKind.File
            or LocalDataResetPathKind.ReparsePoint)
        {
            throw new LocalDataResetUnsafePathException(path);
        }
        throw new IOException($"The reset directory still exists: {path}");
    }

    private static LocalDataResetStartupResult Failure(
        LocalDataResetStartupStatus status) => new(
            status,
            ResetCompleted: false,
            DataLease: null);

    private LocalDataResetFileLease? TryAcquireSharedLease()
    {
        try
        {
            return LocalDataResetFileSystem.OpenPinnedControlLock(
                _layout,
                _layout.DataLeasePath,
                FileShare.ReadWrite);
        }
        catch (IOException exception) when (LocalDataResetFileSystem.IsLockContention(exception))
        {
            return null;
        }
    }

    private LocalDataResetFileLease? TryAcquireExclusiveLease()
    {
        var startedAt = _timeProvider.GetTimestamp();
        while (true)
        {
            try
            {
                return LocalDataResetFileSystem.OpenPinnedControlLock(
                    _layout,
                    _layout.DataLeasePath,
                    FileShare.None);
            }
            catch (IOException exception) when (
                LocalDataResetFileSystem.IsLockContention(exception))
            {
                if (_exclusiveLockTimeout == TimeSpan.Zero
                    || _timeProvider.GetElapsedTime(startedAt) >= _exclusiveLockTimeout)
                {
                    return null;
                }

                if (_exclusiveRetryDelay > TimeSpan.Zero)
                    Thread.Sleep(_exclusiveRetryDelay);
            }
        }
    }

    private void DeleteDirectoryWithoutFollowingReparsePoints(
        string path,
        LocalDataResetDirectoryPin? existingPin = null)
    {
        LocalDataResetDirectoryPin? directoryPin = existingPin;
        var deleteDirectory = false;
        try
        {
            if (directoryPin is null)
            {
                var probe = LocalDataResetFileSystem.Probe(path);
                if (probe.Kind == LocalDataResetPathKind.Absent)
                    return;
                if (probe.Kind != LocalDataResetPathKind.Directory)
                    throw new LocalDataResetUnsafePathException(path);
                directoryPin = LocalDataResetFileSystem.PinDirectory(path);
            }

            var entries = Directory.GetFileSystemEntries(path);
            Array.Sort(entries, StringComparer.Ordinal);
            foreach (var entry in entries)
                DeleteEntryWithoutFollowingReparsePoints(entry);

            _faultHook?.Invoke(LocalDataResetFaultPoint.BeforeDeletePath, path);
            deleteDirectory = true;
        }
        finally
        {
            directoryPin?.Dispose();
        }

        if (deleteDirectory)
            Directory.Delete(path, recursive: false);
    }

    private void DeleteEntryWithoutFollowingReparsePoints(string path)
    {
        var probe = LocalDataResetFileSystem.Probe(path);
        switch (probe.Kind)
        {
            case LocalDataResetPathKind.Absent:
                return;
            case LocalDataResetPathKind.Unavailable:
                throw new IOException($"The reset entry could not be inspected: {path}");
            case LocalDataResetPathKind.ReparsePoint:
                _faultHook?.Invoke(LocalDataResetFaultPoint.BeforeDeletePath, path);
                LocalDataResetFileSystem.DeleteReparsePoint(path, probe.Attributes);
                return;
            case LocalDataResetPathKind.Directory:
                DeleteDirectoryWithoutFollowingReparsePoints(path);
                return;
            case LocalDataResetPathKind.File:
                _faultHook?.Invoke(LocalDataResetFaultPoint.BeforeDeletePath, path);
                File.Delete(path);
                return;
            default:
                throw new UnreachableException();
        }
    }

    private void DeleteOwnedStreamingTemporaryFiles()
    {
        var tempDirectory = LocalDataResetFileSystem.Probe(_layout.TempDirectory);
        if (tempDirectory.Kind == LocalDataResetPathKind.Absent)
            return;
        if (tempDirectory.Kind != LocalDataResetPathKind.Directory)
            throw new LocalDataResetUnsafePathException(_layout.TempDirectory);

        using var tempPin = LocalDataResetFileSystem.PinDirectory(_layout.TempDirectory);
        foreach (var path in Directory.GetFileSystemEntries(_layout.TempDirectory))
        {
            if (!LocalDataResetFileSystem.IsOwnedStreamingTemporaryFileName(
                    Path.GetFileName(path)))
            {
                continue;
            }

            var probe = LocalDataResetFileSystem.Probe(path);
            if (probe.Kind == LocalDataResetPathKind.File)
            {
                _faultHook?.Invoke(LocalDataResetFaultPoint.BeforeDeletePath, path);
                File.Delete(path);
            }
            else if (probe.Kind == LocalDataResetPathKind.ReparsePoint)
            {
                _faultHook?.Invoke(LocalDataResetFaultPoint.BeforeDeletePath, path);
                LocalDataResetFileSystem.DeleteReparsePoint(path, probe.Attributes);
            }
            else if (probe.Kind == LocalDataResetPathKind.Unavailable)
            {
                throw new IOException(
                    $"The reset temporary file could not be inspected: {path}");
            }
            else if (probe.Kind == LocalDataResetPathKind.Directory)
            {
                throw new LocalDataResetUnsafePathException(path);
            }
        }
    }

    private void EnsureNoReservedTrash(Guid? allowedResetId = null)
    {
        var parent = LocalDataResetFileSystem.Probe(
            _layout.LocalApplicationDataDirectory);
        if (parent.Kind == LocalDataResetPathKind.Absent)
            return;
        if (parent.Kind != LocalDataResetPathKind.Directory)
            throw new LocalDataResetUnsafePathException(
                _layout.LocalApplicationDataDirectory);

        using var parentPin = LocalDataResetFileSystem.PinDirectory(
            _layout.LocalApplicationDataDirectory);
        var candidates = Directory.GetFileSystemEntries(
            _layout.LocalApplicationDataDirectory,
            ".WinVaultWarden.reset-*.trash",
            SearchOption.TopDirectoryOnly);

        foreach (var path in candidates)
        {
            var fileName = Path.GetFileName(path);
            if (!LocalDataResetFileSystem.IsReservedTrashName(fileName))
                continue;

            if (allowedResetId.HasValue
                && string.Equals(
                    fileName,
                    Path.GetFileName(_layout.GetTrashPath(allowedResetId.Value)),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                continue;
            }

            var probe = LocalDataResetFileSystem.Probe(path);
            switch (probe.Kind)
            {
                case LocalDataResetPathKind.Absent:
                    continue;
                case LocalDataResetPathKind.Directory:
                    throw new LocalDataResetAmbiguousStateException();
                case LocalDataResetPathKind.Unavailable:
                    throw new IOException(
                        $"The reserved reset directory could not be inspected: {path}");
                default:
                    throw new LocalDataResetUnsafePathException(path);
            }
        }
    }
}
