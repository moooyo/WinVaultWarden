using App.Services;
using Xunit;

namespace App.Tests;

public sealed class LocalDataResetServiceTests
{
    private static readonly DateTimeOffset FixedRequestTime =
        new(2026, 8, 31, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void PrepareForStartup_NoMarkerCreatesAndPinsDataRootForProcessLifetime()
    {
        using var scope = new ResetTestScope();
        LocalDataResetStartupResult first = default;
        LocalDataResetStartupResult second = default;
        try
        {
            first = scope.CreateCoordinator().PrepareForStartup();
            second = scope.CreateCoordinator().PrepareForStartup();
            Assert.Equal(LocalDataResetStartupStatus.Ready, first.Status);
            Assert.False(first.ResetCompleted);
            Assert.True(first.CanStart);
            Assert.NotNull(first.DataLease);
            Assert.Equal(LocalDataResetStartupStatus.Ready, second.Status);
            Assert.NotNull(second.DataLease);
            Assert.True(Directory.Exists(scope.Layout.RootPath));
            Assert.Throws<IOException>(() => OpenExclusive(scope.Layout.DataLeasePath));
        }
        finally
        {
            try
            {
                second.DataLease?.Dispose();
            }
            finally
            {
                first.DataLease?.Dispose();
            }
        }
    }

    [WindowsFact]
    public void PrepareForStartup_ReadyLeasePreventsRootAndControlRootRenameUntilDisposed()
    {
        using var scope = new ResetTestScope();
        LocalDataResetStartupResult result = default;
        var movedRoot = scope.Layout.RootPath + ".moved";
        var movedControlRoot = scope.Layout.ControlRoot + ".moved";

        try
        {
            result = scope.CreateCoordinator().PrepareForStartup();
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.NotNull(result.DataLease);
            Assert.Throws<IOException>(() =>
                Directory.Move(scope.Layout.RootPath, movedRoot));
            Assert.Throws<IOException>(() =>
                Directory.Move(scope.Layout.ControlRoot, movedControlRoot));
        }
        finally
        {
            result.DataLease?.Dispose();
        }

        Directory.Move(scope.Layout.RootPath, movedRoot);
        Directory.Move(movedRoot, scope.Layout.RootPath);
        Directory.Move(scope.Layout.ControlRoot, movedControlRoot);
        Directory.Move(movedControlRoot, scope.Layout.ControlRoot);
    }

    [Fact]
    public void RequestReset_WritesFixedIntegrityCheckedMarkerAndIsIdempotent()
    {
        using var scope = new ResetTestScope();
        var service = scope.CreateRequestService();

        var first = service.RequestReset();
        var second = service.RequestReset();

        Assert.Equal(LocalDataResetRequestStatus.Requested, first.Status);
        Assert.NotNull(first.ResetId);
        Assert.NotEqual(Guid.Empty, first.ResetId);
        Assert.Equal(FixedRequestTime, first.RequestedAt);
        Assert.Equal(LocalDataResetRequestStatus.AlreadyPending, second.Status);
        Assert.Equal(first.ResetId, second.ResetId);
        Assert.Equal(first.RequestedAt, second.RequestedAt);
        Assert.Equal(
            LocalDataResetMarkerCodec.SerializedLength,
            new FileInfo(scope.Layout.MarkerPath).Length);
        Assert.Empty(Directory.GetFiles(scope.Layout.ControlRoot, "*.tmp"));

        var marker = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, marker.Status);
        Assert.Equal(first.ResetId, marker.Marker.ResetId);
        Assert.Equal(FixedRequestTime, marker.Marker.RequestedAt);
    }

    [Fact]
    public void RequestReset_NormalizesSubMillisecondClockBeforeRoundTripVerification()
    {
        using var scope = new ResetTestScope();
        var providerTime = FixedRequestTime.AddTicks(1_234);
        var service = new LocalDataResetService(
            scope.Layout,
            new FixedTimeProvider(providerTime));

        var result = service.RequestReset();

        Assert.Equal(LocalDataResetRequestStatus.Requested, result.Status);
        Assert.Equal(FixedRequestTime, result.RequestedAt);
        var marker = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, marker.Status);
        Assert.Equal(FixedRequestTime, marker.Marker.RequestedAt);
    }

    [Fact]
    public void RequestReset_ChecksumMismatchIsCorruptAndIsNotOverwritten()
    {
        using var scope = new ResetTestScope();
        var service = scope.CreateRequestService();
        Assert.Equal(LocalDataResetRequestStatus.Requested, service.RequestReset().Status);
        var bytes = File.ReadAllBytes(scope.Layout.MarkerPath);
        bytes[^1] ^= 0x80;
        File.WriteAllBytes(scope.Layout.MarkerPath, bytes);

        var result = service.RequestReset();
        var startup = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetRequestStatus.Corrupt, result.Status);
        Assert.Equal(LocalDataResetStartupStatus.Corrupt, startup.Status);
        Assert.False(startup.CanStart);
        Assert.Null(startup.DataLease);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
    }

    [Fact]
    public void RequestReset_LockedMarkerIsUnavailableAndIsNotReplaced()
    {
        using var scope = new ResetTestScope();
        var service = scope.CreateRequestService();
        Assert.Equal(LocalDataResetRequestStatus.Requested, service.RequestReset().Status);
        using var markerLock = new FileStream(
            scope.Layout.MarkerPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var result = service.RequestReset();

        Assert.Equal(LocalDataResetRequestStatus.Unavailable, result.Status);
        Assert.Equal(LocalDataResetMarkerCodec.SerializedLength, markerLock.Length);
    }

    [Fact]
    public void RequestReset_MovedMarkerLockedBeforeVerificationIsCommitOutcomeUncertain()
    {
        using var scope = new ResetTestScope();
        FileStream? markerLock = null;
        var service = scope.CreateRequestService((point, path) =>
        {
            if (point == LocalDataResetRequestFaultPoint.AfterMarkerMoveBeforeVerification)
                markerLock = OpenExclusive(Assert.IsType<string>(path));
        });

        LocalDataResetRequestResult result;
        try
        {
            result = service.RequestReset();

            Assert.Equal(LocalDataResetRequestStatus.CommitOutcomeUncertain, result.Status);
            Assert.NotNull(markerLock);
            Assert.True(File.Exists(scope.Layout.MarkerPath));
            Assert.Empty(Directory.GetFiles(scope.Layout.ControlRoot, "*.tmp"));
        }
        finally
        {
            markerLock?.Dispose();
        }

        var marker = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, marker.Status);
        var retry = scope.CreateRequestService().RequestReset();
        Assert.Equal(LocalDataResetRequestStatus.AlreadyPending, retry.Status);
        Assert.Equal(marker.Marker.ResetId, retry.ResetId);
    }

    [Fact]
    public void RequestReset_RacedTargetLockedAfterMoveFailureIsCommitOutcomeUncertain()
    {
        using var scope = new ResetTestScope();
        FileStream? markerLock = null;
        var service = scope.CreateRequestService((point, path) =>
        {
            if (point != LocalDataResetRequestFaultPoint.BeforeMarkerMove)
                return;

            var markerPath = Assert.IsType<string>(path);
            File.WriteAllBytes(markerPath, [0x42]);
            markerLock = OpenExclusive(markerPath);
        });

        try
        {
            var result = service.RequestReset();

            Assert.Equal(LocalDataResetRequestStatus.CommitOutcomeUncertain, result.Status);
            Assert.NotNull(markerLock);
            Assert.True(File.Exists(scope.Layout.MarkerPath));
            Assert.Empty(Directory.GetFiles(scope.Layout.ControlRoot, "*.tmp"));
        }
        finally
        {
            markerLock?.Dispose();
        }

        Assert.Equal(
            LocalDataResetMarkerReadStatus.Corrupt,
            LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath).Status);
    }

    [Fact]
    public void RequestReset_ExistingValidMarkerReturnsAlreadyPendingWithoutFaultHook()
    {
        using var scope = new ResetTestScope();
        var initial = scope.CreateRequestService().RequestReset();
        var faultHookCalled = false;
        var service = scope.CreateRequestService((_, _) => faultHookCalled = true);

        var result = service.RequestReset();

        Assert.Equal(LocalDataResetRequestStatus.AlreadyPending, result.Status);
        Assert.Equal(initial.ResetId, result.ResetId);
        Assert.Equal(initial.RequestedAt, result.RequestedAt);
        Assert.False(faultHookCalled);
    }

    [Fact]
    public async Task RequestReset_ConcurrentWritersCreateOneBaselineBoundRequest()
    {
        using var scope = new ResetTestScope();
        var initialRequest = scope.CreateRequestService().RequestReset();
        var initialReset = scope.CreateCoordinator().PrepareForStartup();
        initialReset.DataLease?.Dispose();
        var completedEpoch = AssertCompletedEpoch(scope, initialRequest);
        for (var iteration = 0; iteration < 32; iteration++)
        {
            using var startGate = new ManualResetEventSlim(initialState: false);
            var first = scope.CreateRequestService();
            var second = scope.CreateRequestService();
            var firstTask = Task.Run(() =>
            {
                startGate.Wait();
                return first.RequestReset();
            });
            var secondTask = Task.Run(() =>
            {
                startGate.Wait();
                return second.RequestReset();
            });
            startGate.Set();

            var results = await Task.WhenAll(firstTask, secondTask)
                .WaitAsync(TimeSpan.FromSeconds(15));

            var requested = Assert.Single(
                results,
                result => result.Status == LocalDataResetRequestStatus.Requested);
            Assert.Single(
                results,
                result => result.Status == LocalDataResetRequestStatus.AlreadyPending);
            Assert.Equal(results[0].ResetId, results[1].ResetId);
            var marker = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath);
            Assert.Equal(LocalDataResetMarkerReadStatus.Valid, marker.Status);
            Assert.Equal(completedEpoch.ResetId, marker.Marker.BaseResetId);
            Assert.False(LocalDataResetService.IsInProcessRequestGateTracked(
                scope.Layout.MarkerPath));

            var reset = scope.CreateCoordinator().PrepareForStartup();
            reset.DataLease?.Dispose();
            Assert.Equal(LocalDataResetStartupStatus.Ready, reset.Status);
            completedEpoch = AssertCompletedEpoch(scope, requested);
        }
    }

    [Theory]
    [InlineData(LocalDataResetStartupStatus.Ready)]
    [InlineData(LocalDataResetStartupStatus.UnsafePath)]
    [InlineData(LocalDataResetStartupStatus.Unavailable)]
    [InlineData(LocalDataResetStartupStatus.Contended)]
    public void RequestEmergencyReset_RejectsUnauthorizedObservedStatus(
        LocalDataResetStartupStatus observedStatus)
    {
        using var scope = new ResetTestScope();

        var result = scope.CreateCoordinator().RequestEmergencyReset(observedStatus);

        Assert.Equal(LocalDataEmergencyResetRequestStatus.NotAllowed, result.Status);
        Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
    }

    [Fact]
    public async Task RequestEmergencyReset_ConcurrentRequestsAreIdempotent()
    {
        using var scope = new ResetTestScope();
        WriteCorruptNormalRequest(scope);
        using var startGate = new ManualResetEventSlim(initialState: false);
        var first = scope.CreateCoordinator();
        var second = scope.CreateCoordinator();
        var firstTask = Task.Run(() =>
        {
            startGate.Wait();
            return first.RequestEmergencyReset(LocalDataResetStartupStatus.Corrupt);
        });
        var secondTask = Task.Run(() =>
        {
            startGate.Wait();
            return second.RequestEmergencyReset(LocalDataResetStartupStatus.Corrupt);
        });
        startGate.Set();

        var results = await Task.WhenAll(firstTask, secondTask)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(
            results,
            result => result.Status == LocalDataEmergencyResetRequestStatus.Requested);
        Assert.Single(
            results,
            result => result.Status == LocalDataEmergencyResetRequestStatus.AlreadyPending);
        Assert.Equal(results[0].ResetId, results[1].ResetId);
        Assert.Equal(
            LocalDataResetMarkerReadStatus.Valid,
            LocalDataResetMarkerCodec.Read(scope.Layout.EmergencyMarkerPath).Status);
    }

    [Fact]
    public void RequestEmergencyReset_PostMoveVerificationFailureIsCommitOutcomeUncertain()
    {
        using var scope = new ResetTestScope();
        WriteCorruptNormalRequest(scope);
        FileStream? markerLock = null;
        var coordinator = scope.CreateCoordinator(faultHook: (point, path) =>
        {
            if (point
                == LocalDataResetFaultPoint.AfterEmergencyMarkerMoveBeforeVerification)
            {
                markerLock = OpenExclusive(Assert.IsType<string>(path));
            }
        });

        LocalDataEmergencyResetRequestResult result;
        try
        {
            result = coordinator.RequestEmergencyReset(
                LocalDataResetStartupStatus.Corrupt);
            Assert.Equal(
                LocalDataEmergencyResetRequestStatus.CommitOutcomeUncertain,
                result.Status);
            Assert.NotNull(markerLock);
            Assert.True(File.Exists(scope.Layout.EmergencyMarkerPath));
        }
        finally
        {
            markerLock?.Dispose();
        }

        Assert.Equal(
            LocalDataResetMarkerReadStatus.Valid,
            LocalDataResetMarkerCodec.Read(scope.Layout.EmergencyMarkerPath).Status);
    }

    [Fact]
    public void RequestEmergencyReset_UnsafeNormalMarkerPathIsRejected()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        var target = Path.Combine(scope.BaseDirectory, "normal-marker-target.bin");
        File.WriteAllText(target, "retain");
        File.CreateSymbolicLink(scope.Layout.MarkerPath, target);

        var result = scope.CreateCoordinator().RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);

        Assert.Equal(LocalDataEmergencyResetRequestStatus.UnsafePath, result.Status);
        Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
        Assert.Equal("retain", File.ReadAllText(target));
    }

    [Fact]
    public void RequestEmergencyReset_ExplicitAuthorizationReplacesCorruptEmergencyMarker()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        File.WriteAllBytes(scope.Layout.EmergencyMarkerPath, [0x42]);

        var startup = scope.CreateCoordinator().PrepareForStartup();
        var request = scope.CreateCoordinator().RequestEmergencyReset(startup.Status);

        Assert.Equal(LocalDataResetStartupStatus.Corrupt, startup.Status);
        Assert.Equal(LocalDataEmergencyResetRequestStatus.Requested, request.Status);
        Assert.Equal(
            LocalDataResetMarkerReadStatus.Valid,
            LocalDataResetMarkerCodec.Read(scope.Layout.EmergencyMarkerPath).Status);
    }

    [Fact]
    public void RequestEmergencyReset_ObservedCorruptButCurrentReadyIsNotAllowed()
    {
        using var scope = new ResetTestScope();

        var result = scope.CreateCoordinator().RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);

        Assert.Equal(LocalDataEmergencyResetRequestStatus.NotAllowed, result.Status);
        Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
    }

    [Theory]
    [InlineData(LocalDataResetStartupStatus.Corrupt)]
    [InlineData(LocalDataResetStartupStatus.Ambiguous)]
    public void RequestEmergencyReset_ObservedEligibleStatusMustStillMatchCurrentState(
        LocalDataResetStartupStatus observedStatus)
    {
        using var scope = new ResetTestScope();
        if (observedStatus == LocalDataResetStartupStatus.Corrupt)
        {
            Directory.CreateDirectory(scope.Layout.LocalApplicationDataDirectory);
            Directory.CreateDirectory(scope.Layout.GetTrashPath(Guid.NewGuid()));
        }
        else
        {
            WriteCorruptNormalRequest(scope);
        }

        var result = scope.CreateCoordinator().RequestEmergencyReset(observedStatus);

        Assert.Equal(LocalDataEmergencyResetRequestStatus.NotAllowed, result.Status);
        Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
    }

    [Fact]
    public void RequestEmergencyReset_CurrentUnavailableStateIsNotOverwritten()
    {
        using var scope = new ResetTestScope();
        WriteCorruptNormalRequest(scope);
        using var requestLock = OpenExclusive(scope.Layout.MarkerPath);

        var result = scope.CreateCoordinator().RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);

        Assert.Equal(LocalDataEmergencyResetRequestStatus.Unavailable, result.Status);
        Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
    }

    [Fact]
    public void RequestEmergencyReset_ClassifierMatchesCorruptPartialNormalCommit()
    {
        using var scope = new ResetTestScope();
        var normalRequest = scope.CreateRequestService().RequestReset();
        var requestMarker = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, requestMarker.Status);
        Directory.CreateDirectory(scope.Layout.RootPath);
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.GenerationPath,
            requestMarker.Marker,
            replaceExisting: false);
        File.WriteAllBytes(scope.Layout.CompletionPath, [0x42]);
        var coordinator = scope.CreateCoordinator();
        var observed = coordinator.PrepareForStartup();

        var result = coordinator.RequestEmergencyReset(observed.Status);

        Assert.Equal(LocalDataResetRequestStatus.Requested, normalRequest.Status);
        Assert.Equal(LocalDataResetStartupStatus.Corrupt, observed.Status);
        Assert.Equal(LocalDataEmergencyResetRequestStatus.Requested, result.Status);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.True(File.Exists(scope.Layout.EmergencyMarkerPath));
    }

    [Fact]
    public void RequestEmergencyReset_PreflightsTemporaryDirectoryAndRequestLock()
    {
        using var scope = new ResetTestScope();
        WriteCorruptNormalRequest(scope);
        Directory.CreateDirectory(scope.Layout.TempDirectory);
        var target = Path.Combine(scope.BaseDirectory, "temp-target");
        Directory.CreateDirectory(target);
        Directory.Delete(scope.Layout.TempDirectory);
        Directory.CreateSymbolicLink(scope.Layout.TempDirectory, target);

        var temporaryResult = scope.CreateCoordinator().RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);

        Assert.Equal(
            LocalDataEmergencyResetRequestStatus.UnsafePath,
            temporaryResult.Status);
        Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));

        Directory.Delete(scope.Layout.TempDirectory);
        Directory.CreateDirectory(scope.Layout.RequestLockPath);
        var requestLockResult = scope.CreateCoordinator().RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);

        Assert.Equal(
            LocalDataEmergencyResetRequestStatus.UnsafePath,
            requestLockResult.Status);
        Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
    }

    [Fact]
    public void RequestEmergencyReset_RejectsReparseReservedTrashWithoutFollowingTarget()
    {
        using var scope = new ResetTestScope();
        WriteCorruptNormalRequest(scope);
        var target = Path.Combine(scope.BaseDirectory, "reserved-trash-target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "retain.bin");
        File.WriteAllText(sentinel, "retain");
        Directory.CreateSymbolicLink(
            scope.Layout.GetTrashPath(Guid.NewGuid()),
            target);

        var result = scope.CreateCoordinator().RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);

        Assert.Equal(LocalDataEmergencyResetRequestStatus.UnsafePath, result.Status);
        Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void PrepareForStartup_LockedEmergencyMarkerFailsBeforeNormalState()
    {
        using var scope = new ResetTestScope();
        WriteCorruptNormalRequest(scope);
        var coordinator = scope.CreateCoordinator();
        Assert.Equal(
            LocalDataEmergencyResetRequestStatus.Requested,
            coordinator.RequestEmergencyReset(LocalDataResetStartupStatus.Corrupt).Status);
        using var emergencyLock = OpenExclusive(scope.Layout.EmergencyMarkerPath);

        var result = coordinator.PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Unavailable, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
    }

    [Fact]
    public void PrepareForStartup_EmergencyResetRecoversCorruptNormalMarker()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        var removedPath = Path.Combine(scope.Layout.RootPath, "secret.bin");
        File.WriteAllText(removedPath, "remove");
        WriteCorruptNormalRequest(scope);
        var coordinator = scope.CreateCoordinator();
        var observed = coordinator.PrepareForStartup();
        var request = coordinator.RequestEmergencyReset(observed.Status);

        var result = coordinator.PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Corrupt, observed.Status);
            Assert.Equal(LocalDataEmergencyResetRequestStatus.Requested, request.Status);
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.True(result.ResetCompleted);
            Assert.True(result.CanStart);
            Assert.False(File.Exists(removedPath));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
            AssertEmergencyCompletedEpoch(scope, request);
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_EmergencyResetCleansAllStrictTrashAndPreservesNearMatch()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        File.WriteAllText(Path.Combine(scope.Layout.RootPath, "root.bin"), "remove");
        var firstTrash = scope.Layout.GetTrashPath(Guid.NewGuid());
        var secondTrash = scope.Layout.GetTrashPath(Guid.NewGuid());
        Directory.CreateDirectory(firstTrash);
        Directory.CreateDirectory(secondTrash);
        File.WriteAllText(Path.Combine(firstTrash, "first.bin"), "remove");
        File.WriteAllText(Path.Combine(secondTrash, "second.bin"), "remove");
        var nearMatch = Path.Combine(
            scope.Layout.LocalApplicationDataDirectory,
            $".WinVaultWarden.reset-{Guid.NewGuid():N}.trash.keep");
        Directory.CreateDirectory(nearMatch);
        var nearSentinel = Path.Combine(nearMatch, "retain.bin");
        File.WriteAllText(nearSentinel, "retain");
        var coordinator = scope.CreateCoordinator();
        var observed = coordinator.PrepareForStartup();
        var request = coordinator.RequestEmergencyReset(observed.Status);

        var result = coordinator.PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ambiguous, observed.Status);
            Assert.Equal(LocalDataEmergencyResetRequestStatus.Requested, request.Status);
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.False(Directory.Exists(firstTrash));
            Assert.False(Directory.Exists(secondTrash));
            Assert.True(File.Exists(nearSentinel));
            AssertEmergencyCompletedEpoch(scope, request);
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_EmergencyResetDoesNotFollowRootChildReparsePoint()
    {
        using var scope = new ResetTestScope();
        var target = Path.Combine(scope.BaseDirectory, "outside-root-target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "retain.bin");
        File.WriteAllText(sentinel, "retain");
        Directory.CreateDirectory(scope.Layout.RootPath);
        Directory.CreateSymbolicLink(
            Path.Combine(scope.Layout.RootPath, "linked-directory"),
            target);
        WriteCorruptNormalRequest(scope);
        var coordinator = scope.CreateCoordinator();
        var request = coordinator.RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);

        var result = coordinator.PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataEmergencyResetRequestStatus.Requested, request.Status);
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.True(File.Exists(sentinel));
            AssertEmergencyCompletedEpoch(scope, request);
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_EmergencyResetRetriesExternalCleanupFailure()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        WriteCorruptNormalRequest(scope);
        var cleanupCalls = 0;
        void Cleanup()
        {
            cleanupCalls++;
            if (cleanupCalls == 1)
                throw new IOException("Injected emergency external cleanup failure.");
        }
        var coordinator = scope.CreateCoordinator(Cleanup);
        var request = coordinator.RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);

        var failed = coordinator.PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.True(File.Exists(scope.Layout.EmergencyMarkerPath));

        var resumed = coordinator.PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, resumed.Status);
            Assert.Equal(2, cleanupCalls);
            AssertEmergencyCompletedEpoch(scope, request);
        }
        finally
        {
            resumed.DataLease?.Dispose();
        }
    }

    [Theory]
    [InlineData("AfterGenerationCommitted")]
    [InlineData("AfterCompletionCommitted")]
    public void PrepareForStartup_EmergencyCommitResumeOnlyFinishesPendingStages(
        string faultPointName)
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        WriteCorruptNormalRequest(scope);
        var cleanupCalls = 0;
        var faultPoint = Enum.Parse<LocalDataResetFaultPoint>(faultPointName);
        var coordinator = scope.CreateCoordinator(
            externalCleanup: () => cleanupCalls++,
            faultHook: (point, _) =>
            {
                if (point == faultPoint)
                    throw new IOException("Injected emergency commit failure.");
            });
        var request = coordinator.RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);

        var failed = coordinator.PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.True(File.Exists(scope.Layout.EmergencyMarkerPath));
        var retainedPath = Path.Combine(scope.Layout.RootPath, "post-reset.bin");
        File.WriteAllText(retainedPath, "retain");
        var replayTrash = scope.Layout.GetTrashPath(Guid.NewGuid());
        Directory.CreateDirectory(replayTrash);
        File.WriteAllText(Path.Combine(replayTrash, "cleanup.bin"), "remove");

        var resumed = scope.CreateCoordinator(() => cleanupCalls++).PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, resumed.Status);
            Assert.True(File.Exists(retainedPath));
            Assert.False(Directory.Exists(replayTrash));
            Assert.Equal(1, cleanupCalls);
            AssertEmergencyCompletedEpoch(scope, request);
        }
        finally
        {
            resumed.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_CompletedEmergencyReplayPreservesNewRoot()
    {
        using var scope = new ResetTestScope();
        WriteCorruptNormalRequest(scope);
        var cleanupCalls = 0;
        var coordinator = scope.CreateCoordinator(
            externalCleanup: () => cleanupCalls++,
            faultHook: (point, _) =>
            {
                if (point == LocalDataResetFaultPoint.BeforeEmergencyMarkerCommit)
                    throw new IOException("Injected emergency marker cleanup failure.");
            });
        var request = coordinator.RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);

        var failed = coordinator.PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.True(File.Exists(scope.Layout.EmergencyMarkerPath));
        Assert.False(File.Exists(scope.Layout.MarkerPath));
        var retainedPath = Path.Combine(scope.Layout.RootPath, "post-reset.bin");
        File.WriteAllText(retainedPath, "retain");

        var replay = scope.CreateCoordinator(() => cleanupCalls++).PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, replay.Status);
            Assert.True(File.Exists(retainedPath));
            Assert.Equal(1, cleanupCalls);
            Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
            AssertEmergencyCompletedEpoch(scope, request);
        }
        finally
        {
            replay.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_EmergencyDurablyQuarantinesNormalRequestBeforeCommit()
    {
        using var scope = new ResetTestScope();
        WriteCorruptNormalRequest(scope);
        var coordinator = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point
                == LocalDataResetFaultPoint.AfterNormalRequestQuarantinedBeforeDelete)
            {
                throw new IOException("Injected quarantine cleanup failure.");
            }
        });
        var request = coordinator.RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);
        var quarantinePath = scope.Layout.GetEmergencyRequestQuarantinePath(
            request.ResetId!.Value);
        File.WriteAllText(quarantinePath, "stale quarantine");

        var failed = coordinator.PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.False(File.Exists(scope.Layout.MarkerPath));
        Assert.True(File.Exists(quarantinePath));
        Assert.Equal(1, new FileInfo(quarantinePath).Length);
        Assert.True(File.Exists(scope.Layout.EmergencyMarkerPath));

        var replay = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, replay.Status);
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
            Assert.False(File.Exists(quarantinePath));
            AssertEmergencyCompletedEpoch(scope, request);
        }
        finally
        {
            replay.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_SharedContenderPreservesPendingReset()
    {
        using var scope = new ResetTestScope();
        Assert.Equal(
            LocalDataResetRequestStatus.Requested,
            scope.CreateRequestService().RequestReset().Status);
        using var sharedContender = new FileStream(
            scope.Layout.DataLeasePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite);

        var result = scope.CreateCoordinator(
            exclusiveLockTimeout: TimeSpan.Zero).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Contended, result.Status);
        Assert.False(result.ResetCompleted);
        Assert.Null(result.DataLease);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
    }

    [Fact]
    public void PrepareForStartup_RequestDisappearsWithoutCompletionFailsClosed()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "legacy-root.bin");
        File.WriteAllText(retainedPath, "retain");
        Assert.Equal(
            LocalDataResetRequestStatus.Requested,
            scope.CreateRequestService().RequestReset().Status);

        var result = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.AfterInitialRequestReadBeforeExclusiveLease)
                File.Delete(scope.Layout.MarkerPath);
        }).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(retainedPath));
        Assert.False(File.Exists(scope.Layout.CompletionPath));
        Assert.False(File.Exists(scope.Layout.GenerationPath));
    }

    [Fact]
    public void PrepareForStartup_RequestDisappearsAfterSameEpochCompletionIsProven()
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var completed = scope.CreateCoordinator().PrepareForStartup();
        completed.DataLease?.Dispose();
        var epoch = AssertCompletedEpoch(scope, request);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "post-reset.bin");
        File.WriteAllText(retainedPath, "retain");
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.MarkerPath,
            epoch,
            replaceExisting: false);
        var cleanupCalls = 0;

        var result = scope.CreateCoordinator(
            externalCleanup: () => cleanupCalls++,
            faultHook: (point, _) =>
            {
                if (point
                    == LocalDataResetFaultPoint.AfterInitialRequestReadBeforeExclusiveLease)
                {
                    File.Delete(scope.Layout.MarkerPath);
                }
            }).PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.True(result.ResetCompleted);
            Assert.True(result.CanStart);
            Assert.True(File.Exists(retainedPath));
            Assert.Equal(0, cleanupCalls);
            AssertCompletedEpoch(scope, request);
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_SuccessfulResetReturnsSharedLeaseThatBlocksExclusiveOpen()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        var secretPath = Path.Combine(scope.Layout.RootPath, "secret.bin");
        File.WriteAllText(secretPath, "secret");
        var request = scope.CreateRequestService().RequestReset();
        Assert.Equal(LocalDataResetRequestStatus.Requested, request.Status);

        var result = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.True(result.ResetCompleted);
            Assert.True(result.CanStart);
            Assert.NotNull(result.DataLease);
            Assert.True(Directory.Exists(scope.Layout.RootPath));
            Assert.False(File.Exists(secretPath));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            AssertCompletedEpoch(scope, request);
            Assert.Throws<IOException>(() => OpenExclusive(scope.Layout.DataLeasePath));
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_NormalRestartPreservesCompletedRootData()
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var completed = scope.CreateCoordinator().PrepareForStartup();
        completed.DataLease?.Dispose();
        Assert.Equal(LocalDataResetStartupStatus.Ready, completed.Status);
        AssertCompletedEpoch(scope, request);

        var retainedPath = Path.Combine(scope.Layout.RootPath, "post-reset.bin");
        File.WriteAllText(retainedPath, "retain");

        var restarted = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, restarted.Status);
            Assert.False(restarted.ResetCompleted);
            Assert.True(restarted.CanStart);
            Assert.True(File.Exists(retainedPath));
            AssertCompletedEpoch(scope, request);
        }
        finally
        {
            restarted.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_CompletedRequestReplayOnlyClearsRequestMarker()
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var completed = scope.CreateCoordinator().PrepareForStartup();
        completed.DataLease?.Dispose();
        var epoch = AssertCompletedEpoch(scope, request);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "post-reset.bin");
        File.WriteAllText(retainedPath, "retain");
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.MarkerPath,
            epoch,
            replaceExisting: false);
        var externalCleanupCalls = 0;

        var replayed = scope.CreateCoordinator(() => externalCleanupCalls++).PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, replayed.Status);
            Assert.True(replayed.ResetCompleted);
            Assert.True(File.Exists(retainedPath));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            Assert.Equal(0, externalCleanupCalls);
            AssertCompletedEpoch(scope, request);
        }
        finally
        {
            replayed.DataLease?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepareForStartup_CompletedRequestWithInvalidGenerationFailsClosed(
        bool writeDifferentGeneration)
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var completed = scope.CreateCoordinator().PrepareForStartup();
        completed.DataLease?.Dispose();
        var epoch = AssertCompletedEpoch(scope, request);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "post-reset.bin");
        File.WriteAllText(retainedPath, "retain");
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.MarkerPath,
            epoch,
            replaceExisting: false);
        File.Delete(scope.Layout.GenerationPath);
        if (writeDifferentGeneration)
        {
            var different = new LocalDataResetMarker(
                Guid.NewGuid(),
                FixedRequestTime.AddMinutes(1));
            LocalDataResetAtomicFile.WriteMarker(
                scope.Layout.GenerationPath,
                different,
                replaceExisting: false);
        }

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(retainedPath));
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.False(result.ResetCompleted);
    }

    [Fact]
    public void PrepareForStartup_NewRequestReplacesEpochAndClearsPreviousRoot()
    {
        using var scope = new ResetTestScope();
        var firstRequest = scope.CreateRequestService().RequestReset();
        var firstReset = scope.CreateCoordinator().PrepareForStartup();
        firstReset.DataLease?.Dispose();
        var firstEpoch = AssertCompletedEpoch(scope, firstRequest);
        var oldRootData = Path.Combine(scope.Layout.RootPath, "old-root.bin");
        File.WriteAllText(oldRootData, "remove");

        var nextRequest = scope.CreateRequestService().RequestReset();
        Assert.Equal(LocalDataResetRequestStatus.Requested, nextRequest.Status);
        Assert.NotEqual(firstEpoch.ResetId, nextRequest.ResetId);
        var pending = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, pending.Status);
        Assert.Equal(firstEpoch.ResetId, pending.Marker.BaseResetId);
        var secondReset = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, secondReset.Status);
            Assert.True(secondReset.ResetCompleted);
            Assert.False(File.Exists(oldRootData));
            var secondEpoch = AssertCompletedEpoch(scope, nextRequest);
            Assert.NotEqual(firstEpoch, secondEpoch);
        }
        finally
        {
            secondReset.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_OldRequestMarkerCannotDeleteNewerCompletedRoot()
    {
        using var scope = new ResetTestScope();
        var firstRequest = scope.CreateRequestService().RequestReset();
        var firstReset = scope.CreateCoordinator().PrepareForStartup();
        firstReset.DataLease?.Dispose();
        var oldEpoch = AssertCompletedEpoch(scope, firstRequest);

        var secondRequest = scope.CreateRequestService().RequestReset();
        var secondReset = scope.CreateCoordinator().PrepareForStartup();
        secondReset.DataLease?.Dispose();
        AssertCompletedEpoch(scope, secondRequest);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "new-root.bin");
        File.WriteAllText(retainedPath, "retain");
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.MarkerPath,
            oldEpoch,
            replaceExisting: false);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(retainedPath));
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        AssertCompletedEpoch(scope, secondRequest);
    }

    [Fact]
    public void PrepareForStartup_LegacyV1RequestWithoutEpochCompletesMigration()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        Directory.CreateDirectory(scope.Layout.RootPath);
        var removedPath = Path.Combine(scope.Layout.RootPath, "legacy.bin");
        File.WriteAllText(removedPath, "remove");
        var legacyRequest = new LocalDataResetMarker(Guid.NewGuid(), FixedRequestTime);
        WriteLegacyV1Marker(scope.Layout.MarkerPath, legacyRequest);

        var result = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.True(result.ResetCompleted);
            Assert.False(File.Exists(removedPath));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            Assert.Equal(
                LocalDataResetMarkerCodec.SerializedLength,
                new FileInfo(scope.Layout.CompletionPath).Length);
            Assert.Equal(
                LocalDataResetMarkerCodec.SerializedLength,
                new FileInfo(scope.Layout.GenerationPath).Length);
            AssertCompletedEpoch(
                scope,
                new LocalDataResetRequestResult(
                    LocalDataResetRequestStatus.Requested,
                    legacyRequest.ResetId,
                    legacyRequest.RequestedAt));
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_LegacyV1CompletedEpochIsAcceptedAndBindsNextRequest()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        Directory.CreateDirectory(scope.Layout.RootPath);
        var legacyEpoch = new LocalDataResetMarker(Guid.NewGuid(), FixedRequestTime);
        WriteLegacyV1Marker(scope.Layout.CompletionPath, legacyEpoch);
        WriteLegacyV1Marker(scope.Layout.GenerationPath, legacyEpoch);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "legacy-root.bin");
        File.WriteAllText(retainedPath, "retain");

        var startup = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, startup.Status);
            Assert.False(startup.ResetCompleted);
            Assert.True(File.Exists(retainedPath));
        }
        finally
        {
            startup.DataLease?.Dispose();
        }

        var request = scope.CreateRequestService().RequestReset();
        var pending = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath);
        Assert.Equal(LocalDataResetRequestStatus.Requested, request.Status);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, pending.Status);
        Assert.Equal(legacyEpoch.ResetId, pending.Marker.BaseResetId);
        Assert.Equal(
            LocalDataResetMarkerCodec.SerializedLength,
            new FileInfo(scope.Layout.MarkerPath).Length);
    }

    [Fact]
    public void PrepareForStartup_LegacyV1RequestCannotCrossExistingEpoch()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        Directory.CreateDirectory(scope.Layout.RootPath);
        var currentEpoch = new LocalDataResetMarker(Guid.NewGuid(), FixedRequestTime);
        WriteLegacyV1Marker(scope.Layout.CompletionPath, currentEpoch);
        WriteLegacyV1Marker(scope.Layout.GenerationPath, currentEpoch);
        var oldRequest = new LocalDataResetMarker(
            Guid.NewGuid(),
            FixedRequestTime.AddMinutes(-1));
        WriteLegacyV1Marker(scope.Layout.MarkerPath, oldRequest);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "current-root.bin");
        File.WriteAllText(retainedPath, "retain");

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(retainedPath));
        Assert.True(File.Exists(scope.Layout.MarkerPath));
    }

    [Fact]
    public void PrepareForStartup_FailureAfterMoveResumesFromDetachedTrash()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        File.WriteAllText(Path.Combine(scope.Layout.RootPath, "session.bin"), "secret");
        var request = scope.CreateRequestService().RequestReset();
        var trashPath = scope.Layout.GetTrashPath(request.ResetId!.Value);

        var failed = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.AfterRootMoved)
                throw new IOException("Injected post-move failure.");
        }).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.False(Directory.Exists(scope.Layout.RootPath));
        Assert.True(Directory.Exists(trashPath));

        var resumed = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, resumed.Status);
            Assert.True(resumed.ResetCompleted);
            Assert.False(Directory.Exists(trashPath));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
        }
        finally
        {
            resumed.DataLease?.Dispose();
        }
    }

    [WindowsFact]
    public void PrepareForStartup_TrashIdentityMismatchStopsBeforeRecursiveDeletion()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        var secretName = "session.bin";
        File.WriteAllText(Path.Combine(scope.Layout.RootPath, secretName), "secret");
        var request = scope.CreateRequestService().RequestReset();
        var trashPath = scope.Layout.GetTrashPath(request.ResetId!.Value);
        var preservedOriginal = trashPath + ".preserved";
        var replacementSentinel = Path.Combine(trashPath, "replacement.bin");

        var result = scope.CreateCoordinator(faultHook: (point, path) =>
        {
            if (point != LocalDataResetFaultPoint.AfterRootMoved)
                return;

            Assert.Equal(trashPath, path);
            Directory.Move(trashPath, preservedOriginal);
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(replacementSentinel, "keep");
        }).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Corrupt, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.Equal(
            LocalDataResetMovePhase.MovePrepared,
            LocalDataResetMoveIntentCodec.Read(scope.Layout.MoveIntentPath).Intent.Phase);
        Assert.True(File.Exists(Path.Combine(preservedOriginal, secretName)));
        Assert.True(File.Exists(replacementSentinel));

        var second = scope.CreateCoordinator().PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Corrupt, second.Status);
        Assert.True(File.Exists(Path.Combine(preservedOriginal, secretName)));
        Assert.True(File.Exists(replacementSentinel));
        Assert.True(File.Exists(scope.Layout.MoveIntentPath));
    }

    [WindowsFact]
    public void PrepareForStartup_EmergencyTrashIdentityMismatchPersistsAcrossRestart()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        File.WriteAllText(Path.Combine(scope.Layout.RootPath, "secret.bin"), "secret");
        WriteCorruptNormalRequest(scope);
        var coordinator = scope.CreateCoordinator();
        var request = coordinator.RequestEmergencyReset(
            LocalDataResetStartupStatus.Corrupt);
        var trashPath = scope.Layout.GetTrashPath(request.ResetId!.Value);
        var preservedOriginal = trashPath + ".preserved";
        var replacementSentinel = Path.Combine(trashPath, "replacement.bin");

        var first = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point != LocalDataResetFaultPoint.AfterRootMoved)
                return;
            Directory.Move(trashPath, preservedOriginal);
            Directory.CreateDirectory(trashPath);
            File.WriteAllText(replacementSentinel, "retain");
        }).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Corrupt, first.Status);
        var second = coordinator.PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Corrupt, second.Status);
        Assert.True(File.Exists(Path.Combine(preservedOriginal, "secret.bin")));
        Assert.True(File.Exists(replacementSentinel));
        Assert.True(File.Exists(scope.Layout.EmergencyMarkerPath));
        Assert.True(File.Exists(scope.Layout.MoveIntentPath));
    }

    [WindowsFact]
    public void PrepareForStartup_ReplacementIdentityMismatchPreservesBothDirectories()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        scope.CreateRequestService().RequestReset();
        var marker = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath).Marker;
        var stagingPath = scope.Layout.GetReplacementStagingPath(marker.ResetId);
        var preservedStaging = stagingPath + ".preserved";
        var first = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.AfterMoveIntentReplacementPrepared)
                throw new IOException("Injected replacement intent failure.");
        }).PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, first.Status);

        Directory.Move(stagingPath, preservedStaging);
        var originalSentinel = Path.Combine(preservedStaging, "original.bin");
        File.WriteAllText(originalSentinel, "retain original");
        Directory.CreateDirectory(stagingPath);
        var replacementSentinel = Path.Combine(stagingPath, "replacement.bin");
        File.WriteAllText(replacementSentinel, "retain replacement");

        var second = scope.CreateCoordinator().PrepareForStartup();
        var third = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Corrupt, second.Status);
        Assert.Equal(LocalDataResetStartupStatus.Corrupt, third.Status);
        Assert.True(File.Exists(originalSentinel));
        Assert.True(File.Exists(replacementSentinel));
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.True(File.Exists(scope.Layout.MoveIntentPath));
    }

    [Theory]
    [InlineData("AfterRootMoved")]
    [InlineData("AfterTrashDeleted")]
    [InlineData("AfterExternalCleanup")]
    [InlineData("AfterRootCreatedBeforeGeneration")]
    [InlineData("AfterGenerationCommitted")]
    [InlineData("AfterCompletionCommitted")]
    public void PrepareForStartup_SecondResetResumesAcrossDurablePhases(
        string faultPointName)
    {
        using var scope = new ResetTestScope();
        var firstRequest = scope.CreateRequestService().RequestReset();
        var firstReset = scope.CreateCoordinator().PrepareForStartup();
        firstReset.DataLease?.Dispose();
        var firstEpoch = AssertCompletedEpoch(scope, firstRequest);
        var removedPath = Path.Combine(scope.Layout.RootPath, "second-reset.bin");
        File.WriteAllText(removedPath, "remove");
        var secondRequest = scope.CreateRequestService().RequestReset();
        var pending = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath);
        Assert.Equal(firstEpoch.ResetId, pending.Marker.BaseResetId);
        var faultPoint = Enum.Parse<LocalDataResetFaultPoint>(faultPointName);
        var cleanupCalls = 0;

        var failed = scope.CreateCoordinator(
            externalCleanup: () => cleanupCalls++,
            faultHook: (point, _) =>
            {
                if (point == faultPoint)
                    throw new IOException("Injected second-reset phase failure.");
            }).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.True(File.Exists(scope.Layout.MarkerPath));

        var resumed = scope.CreateCoordinator(() => cleanupCalls++).PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, resumed.Status);
            Assert.True(resumed.ResetCompleted);
            Assert.True(resumed.CanStart);
            Assert.False(File.Exists(removedPath));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            Assert.True(cleanupCalls >= 1);
            AssertCompletedEpoch(scope, secondRequest);
        }
        finally
        {
            resumed.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_SecondResetResumesAfterPartialTrashDeletion()
    {
        using var scope = new ResetTestScope();
        var firstRequest = scope.CreateRequestService().RequestReset();
        var firstReset = scope.CreateCoordinator().PrepareForStartup();
        firstReset.DataLease?.Dispose();
        AssertCompletedEpoch(scope, firstRequest);
        var nested = Path.Combine(scope.Layout.RootPath, "nested");
        Directory.CreateDirectory(nested);
        var blockedPath = Path.Combine(nested, "blocked.bin");
        File.WriteAllText(blockedPath, "remove");
        var secondRequest = scope.CreateRequestService().RequestReset();
        var injected = false;

        var failed = scope.CreateCoordinator(faultHook: (point, path) =>
        {
            if (!injected
                && point == LocalDataResetFaultPoint.BeforeDeletePath
                && string.Equals(Path.GetFileName(path), "blocked.bin", StringComparison.Ordinal))
            {
                injected = true;
                throw new IOException("Injected partial trash deletion failure.");
            }
        }).PrepareForStartup();

        Assert.True(injected);
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.True(File.Exists(scope.Layout.MarkerPath));

        var resumed = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, resumed.Status);
            Assert.False(File.Exists(blockedPath));
            AssertCompletedEpoch(scope, secondRequest);
        }
        finally
        {
            resumed.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_SecondResetRetriesExternalCleanupFailure()
    {
        using var scope = new ResetTestScope();
        var firstRequest = scope.CreateRequestService().RequestReset();
        var firstReset = scope.CreateCoordinator().PrepareForStartup();
        firstReset.DataLease?.Dispose();
        AssertCompletedEpoch(scope, firstRequest);
        File.WriteAllText(Path.Combine(scope.Layout.RootPath, "remove.bin"), "remove");
        var secondRequest = scope.CreateRequestService().RequestReset();
        var cleanupCalls = 0;
        void Cleanup()
        {
            cleanupCalls++;
            if (cleanupCalls == 1)
                throw new IOException("Injected external cleanup failure.");
        }

        var failed = scope.CreateCoordinator(Cleanup).PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.True(File.Exists(scope.Layout.MarkerPath));

        var resumed = scope.CreateCoordinator(Cleanup).PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, resumed.Status);
            Assert.Equal(2, cleanupCalls);
            AssertCompletedEpoch(scope, secondRequest);
        }
        finally
        {
            resumed.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_DeletionFailurePreservesMarkerAndResumesPartialTrash()
    {
        using var scope = new ResetTestScope();
        var nested = Path.Combine(scope.Layout.RootPath, "nested");
        Directory.CreateDirectory(nested);
        var blockedPath = Path.Combine(nested, "blocked.bin");
        File.WriteAllText(blockedPath, "secret");
        var request = scope.CreateRequestService().RequestReset();
        var trashPath = scope.Layout.GetTrashPath(request.ResetId!.Value);
        var injected = false;

        var failed = scope.CreateCoordinator(faultHook: (point, path) =>
        {
            if (!injected
                && point == LocalDataResetFaultPoint.BeforeDeletePath
                && string.Equals(Path.GetFileName(path), "blocked.bin", StringComparison.Ordinal))
            {
                injected = true;
                throw new IOException("Injected deletion failure.");
            }
        }).PrepareForStartup();

        Assert.True(injected);
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.True(Directory.Exists(trashPath));

        var resumed = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, resumed.Status);
            Assert.True(resumed.ResetCompleted);
            Assert.False(Directory.Exists(trashPath));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
        }
        finally
        {
            resumed.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_ExternalCleanupFailureIsRetriedBeforeMarkerCommit()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        Assert.Equal(
            LocalDataResetRequestStatus.Requested,
            scope.CreateRequestService().RequestReset().Status);
        var attempts = 0;
        void Cleanup()
        {
            attempts++;
            if (attempts == 1)
                throw new InvalidOperationException("Injected external cleanup failure.");
        }

        var failed = scope.CreateCoordinator(Cleanup).PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.Equal(1, attempts);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.False(Directory.Exists(scope.Layout.RootPath));

        var resumed = scope.CreateCoordinator(Cleanup).PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, resumed.Status);
            Assert.True(resumed.ResetCompleted);
            Assert.Equal(2, attempts);
            Assert.False(File.Exists(scope.Layout.MarkerPath));
        }
        finally
        {
            resumed.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_LiveRootAndTrashIsAmbiguous()
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        Directory.CreateDirectory(scope.Layout.RootPath);
        Directory.CreateDirectory(scope.Layout.GetTrashPath(request.ResetId!.Value));

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.False(result.ResetCompleted);
        Assert.Null(result.DataLease);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.True(Directory.Exists(scope.Layout.RootPath));
        Assert.True(Directory.Exists(scope.Layout.GetTrashPath(request.ResetId.Value)));
    }

    [Fact]
    public void PrepareForStartup_ReparseDataRootIsRejectedWithoutTouchingTarget()
    {
        using var scope = new ResetTestScope();
        var target = Path.Combine(scope.BaseDirectory, "outside-target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        Assert.Equal(
            LocalDataResetRequestStatus.Requested,
            scope.CreateRequestService().RequestReset().Status);
        Directory.CreateSymbolicLink(scope.Layout.RootPath, target);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.UnsafePath, result.Status);
        Assert.False(result.ResetCompleted);
        Assert.Null(result.DataLease);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public void PrepareForStartup_ChildReparsePointIsDeletedWithoutFollowingTarget()
    {
        using var scope = new ResetTestScope();
        var target = Path.Combine(scope.BaseDirectory, "outside-child-target");
        Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        Directory.CreateDirectory(scope.Layout.RootPath);
        Directory.CreateSymbolicLink(
            Path.Combine(scope.Layout.RootPath, "linked-directory"),
            target);
        Assert.Equal(
            LocalDataResetRequestStatus.Requested,
            scope.CreateRequestService().RequestReset().Status);

        var result = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.True(result.ResetCompleted);
            Assert.True(File.Exists(sentinel));
            Assert.True(Directory.Exists(scope.Layout.RootPath));
            Assert.True(File.Exists(scope.Layout.GenerationPath));
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_DeletesOnlyStrictStreamingTemporaryFileNames()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.TempDirectory);
        var lower = Path.Combine(
            scope.Layout.TempDirectory,
            "winvaultwarden-0123456789abcdef0123456789abcdef.ciphertext.tmp");
        var upper = Path.Combine(
            scope.Layout.TempDirectory,
            "winvaultwarden-ABCDEF0123456789ABCDEF0123456789.ciphertext.tmp");
        var nearMisses = new[]
        {
            "winvaultwarden-0123456789abcdef0123456789abcde.ciphertext.tmp",
            "winvaultwarden-0123456789abcdef0123456789abcdef0.ciphertext.tmp",
            "winvaultwarden-0123456789abcdef0123456789abcdeg.ciphertext.tmp",
            ".winvaultwarden-0123456789abcdef0123456789abcdef.ciphertext.tmp",
            "winvaultwarden-0123456789abcdef0123456789abcdef.ciphertext.tmp.other",
        };
        File.WriteAllText(lower, "delete");
        File.WriteAllText(upper, "delete");
        foreach (var name in nearMisses)
            File.WriteAllText(Path.Combine(scope.Layout.TempDirectory, name), "keep");
        Assert.Equal(
            LocalDataResetRequestStatus.Requested,
            scope.CreateRequestService().RequestReset().Status);

        var result = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.False(File.Exists(lower));
            Assert.False(File.Exists(upper));
            foreach (var name in nearMisses)
                Assert.True(File.Exists(Path.Combine(scope.Layout.TempDirectory, name)));
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_NoRequestPreservesOwnedStreamingTemporaryFiles()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.TempDirectory);
        var owned = Path.Combine(
            scope.Layout.TempDirectory,
            "winvaultwarden-0123456789abcdef0123456789abcdef.ciphertext.tmp");
        var unrelated = Path.Combine(
            scope.Layout.TempDirectory,
            "winvaultwarden-0123456789abcdef0123456789abcdef.ciphertext.keep");
        File.WriteAllText(owned, "preserve");
        File.WriteAllText(unrelated, "keep");

        var result = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.True(File.Exists(owned));
            Assert.True(File.Exists(unrelated));
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_NoRequestPreservesOwnedTemporaryDirectory()
    {
        using var scope = new ResetTestScope();
        var owned = Path.Combine(
            scope.Layout.TempDirectory,
            "winvaultwarden-0123456789abcdef0123456789abcdef.ciphertext.tmp");
        Directory.CreateDirectory(owned);

        var result = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.True(result.CanStart);
            Assert.True(Directory.Exists(owned));
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_StrictStreamingTemporaryDirectoryFailsClosed()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.TempDirectory);
        var unexpectedDirectory = Path.Combine(
            scope.Layout.TempDirectory,
            "winvaultwarden-0123456789abcdef0123456789abcdef.ciphertext.tmp");
        Directory.CreateDirectory(unexpectedDirectory);
        Assert.Equal(
            LocalDataResetRequestStatus.Requested,
            scope.CreateRequestService().RequestReset().Status);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.UnsafePath, result.Status);
        Assert.False(result.CanStart);
        Assert.True(Directory.Exists(unexpectedDirectory));
        Assert.True(File.Exists(scope.Layout.MarkerPath));
    }

    [Fact]
    public void PrepareForStartup_NoMarkerButReservedTrashFailsClosed()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.LocalApplicationDataDirectory);
        var orphanedTrash = scope.Layout.GetTrashPath(Guid.NewGuid());
        Directory.CreateDirectory(orphanedTrash);
        File.WriteAllText(Path.Combine(orphanedTrash, "session.bin"), "secret");

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.False(result.CanStart);
        Assert.Null(result.DataLease);
        Assert.True(Directory.Exists(orphanedTrash));
    }

    [Fact]
    public void PrepareForStartup_NoCompletionButGenerationExistsFailsClosed()
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var completed = scope.CreateCoordinator().PrepareForStartup();
        completed.DataLease?.Dispose();
        AssertCompletedEpoch(scope, request);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "post-reset.bin");
        File.WriteAllText(retainedPath, "retain");
        File.Delete(scope.Layout.CompletionPath);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(scope.Layout.GenerationPath));
        Assert.True(File.Exists(retainedPath));
    }

    [Fact]
    public void PrepareForStartup_CompletionWithoutRootFailsClosed()
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var completed = scope.CreateCoordinator().PrepareForStartup();
        completed.DataLease?.Dispose();
        AssertCompletedEpoch(scope, request);
        Directory.Delete(scope.Layout.RootPath, recursive: true);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(scope.Layout.CompletionPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrepareForStartup_CompletionWithInvalidGenerationFailsClosed(
        bool writeDifferentGeneration)
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var completed = scope.CreateCoordinator().PrepareForStartup();
        completed.DataLease?.Dispose();
        AssertCompletedEpoch(scope, request);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "post-reset.bin");
        File.WriteAllText(retainedPath, "retain");
        File.Delete(scope.Layout.GenerationPath);
        if (writeDifferentGeneration)
        {
            LocalDataResetAtomicFile.WriteMarker(
                scope.Layout.GenerationPath,
                new LocalDataResetMarker(Guid.NewGuid(), FixedRequestTime.AddMinutes(1)),
                replaceExisting: false);
        }

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(retainedPath));
    }

    [Fact]
    public void PrepareForStartup_CorruptCompletionFailsClosed()
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var completed = scope.CreateCoordinator().PrepareForStartup();
        completed.DataLease?.Dispose();
        AssertCompletedEpoch(scope, request);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "post-reset.bin");
        File.WriteAllText(retainedPath, "retain");
        var bytes = File.ReadAllBytes(scope.Layout.CompletionPath);
        bytes[^1] ^= 0x80;
        File.WriteAllBytes(scope.Layout.CompletionPath, bytes);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Corrupt, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(retainedPath));
    }

    [Fact]
    public void PrepareForStartup_LockedCompletionFailsClosed()
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var completed = scope.CreateCoordinator().PrepareForStartup();
        completed.DataLease?.Dispose();
        AssertCompletedEpoch(scope, request);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "post-reset.bin");
        File.WriteAllText(retainedPath, "retain");
        using var completionLock = OpenExclusive(scope.Layout.CompletionPath);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Unavailable, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(retainedPath));
    }

    [Fact]
    public void PrepareForStartup_CommitsMarkerOnlyAfterAllCleanupStages()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        File.WriteAllText(Path.Combine(scope.Layout.RootPath, "vault-cache.bin"), "secret");
        Directory.CreateDirectory(scope.Layout.TempDirectory);
        var temporaryPath = Path.Combine(
            scope.Layout.TempDirectory,
            "winvaultwarden-0123456789abcdef0123456789abcdef.ciphertext.tmp");
        File.WriteAllText(temporaryPath, "ciphertext");
        var request = scope.CreateRequestService().RequestReset();
        var trashPath = scope.Layout.GetTrashPath(request.ResetId!.Value);
        var externalCleanupCalled = false;

        var result = scope.CreateCoordinator(() =>
        {
            Assert.True(File.Exists(scope.Layout.MarkerPath));
            Assert.False(Directory.Exists(scope.Layout.RootPath));
            Assert.False(Directory.Exists(trashPath));
            Assert.False(File.Exists(temporaryPath));
            externalCleanupCalled = true;
        }).PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.True(result.ResetCompleted);
            Assert.True(externalCleanupCalled);
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            AssertCompletedEpoch(scope, request);
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_FailureImmediatelyBeforeCommitKeepsMarker()
    {
        using var scope = new ResetTestScope();
        Assert.Equal(
            LocalDataResetRequestStatus.Requested,
            scope.CreateRequestService().RequestReset().Status);

        var result = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.BeforeMarkerCommit)
                throw new IOException("Injected commit failure.");
        }).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Unavailable, result.Status);
        Assert.False(result.ResetCompleted);
        Assert.Null(result.DataLease);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
    }

    [Theory]
    [InlineData("AfterGenerationCommitted")]
    [InlineData("AfterCompletionCommitted")]
    public void PrepareForStartup_EpochCommitFaultKeepsRequestMarker(
        string faultPointName)
    {
        using var scope = new ResetTestScope();
        var faultPoint = Enum.Parse<LocalDataResetFaultPoint>(faultPointName);
        Directory.CreateDirectory(scope.Layout.RootPath);
        File.WriteAllText(Path.Combine(scope.Layout.RootPath, "secret.bin"), "secret");
        var request = scope.CreateRequestService().RequestReset();
        var cleanupCalls = 0;

        var result = scope.CreateCoordinator(
            externalCleanup: () => cleanupCalls++,
            faultHook: (point, _) =>
            {
                if (point == faultPoint)
                    throw new IOException("Injected epoch commit failure.");
            }).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Unavailable, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.Equal(
            LocalDataResetMarkerReadStatus.Valid,
            LocalDataResetMarkerCodec.Read(scope.Layout.GenerationPath).Status);
        Assert.Equal(
            faultPoint == LocalDataResetFaultPoint.AfterCompletionCommitted
                ? LocalDataResetMarkerReadStatus.Valid
                : LocalDataResetMarkerReadStatus.Absent,
            LocalDataResetMarkerCodec.Read(scope.Layout.CompletionPath).Status);

        var retainedRequest = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath);
        Assert.Equal(request.ResetId, retainedRequest.Marker.ResetId);

        var resumed = scope.CreateCoordinator(() => cleanupCalls++).PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, resumed.Status);
            Assert.True(resumed.ResetCompleted);
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            Assert.Equal(1, cleanupCalls);
            AssertCompletedEpoch(scope, request);
        }
        finally
        {
            resumed.DataLease?.Dispose();
        }
    }

    [Theory]
    [InlineData("ReparseRoot", LocalDataResetStartupStatus.UnsafePath)]
    [InlineData("ExtraTrash", LocalDataResetStartupStatus.Ambiguous)]
    public void PrepareForStartup_PartialGenerationCommitRejectsUnsafeState(
        string mutation,
        LocalDataResetStartupStatus expectedStatus)
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var failed = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.AfterGenerationCommitted)
                throw new IOException("Injected generation commit failure.");
        }).PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);

        string? reparseTarget = null;
        if (mutation == "ReparseRoot")
        {
            reparseTarget = Path.Combine(scope.BaseDirectory, "partial-root-target");
            Directory.Move(scope.Layout.RootPath, reparseTarget);
            Directory.CreateSymbolicLink(scope.Layout.RootPath, reparseTarget);
        }
        else
        {
            var extraTrash = scope.Layout.GetTrashPath(Guid.NewGuid());
            Directory.CreateDirectory(extraTrash);
        }

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(expectedStatus, result.Status);
        Assert.False(result.CanStart);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.False(File.Exists(scope.Layout.CompletionPath));
        if (reparseTarget is not null)
            Assert.True(File.Exists(Path.Combine(reparseTarget, ".local-reset-generation.bin")));
        Assert.Equal(request.ResetId, LocalDataResetMarkerCodec.Read(
            scope.Layout.GenerationPath).Marker.ResetId);
    }

    [Fact]
    public void MoveIntentCodec_RoundTripsAndRejectsCorruption()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        var intent = new LocalDataResetMoveIntent(
            LocalDataResetMoveKind.Emergency,
            LocalDataResetMovePhase.ReplacementPrepared,
            Guid.NewGuid(),
            SourceVolumeSerialNumber: 11,
            SourceFileIndex: 12,
            ReplacementVolumeSerialNumber: 21,
            ReplacementFileIndex: 22);
        var bytes = LocalDataResetMoveIntentCodec.Serialize(intent);
        File.WriteAllBytes(scope.Layout.MoveIntentPath, bytes);

        var read = LocalDataResetMoveIntentCodec.Read(scope.Layout.MoveIntentPath);

        Assert.Equal(LocalDataResetMoveIntentCodec.SerializedLength, bytes.Length);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, read.Status);
        Assert.Equal(intent, read.Intent);

        bytes[^1] ^= 0x80;
        File.WriteAllBytes(scope.Layout.MoveIntentPath, bytes);
        Assert.Equal(
            LocalDataResetMarkerReadStatus.Corrupt,
            LocalDataResetMoveIntentCodec.Read(scope.Layout.MoveIntentPath).Status);
        Assert.Throws<InvalidDataException>(() =>
            LocalDataResetMoveIntentCodec.Serialize(intent with
            {
                Phase = LocalDataResetMovePhase.SourceDeleted,
            }));
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<InvalidDataException>(() =>
                LocalDataResetMoveIntentCodec.Serialize(new(
                    LocalDataResetMoveKind.Normal,
                    LocalDataResetMovePhase.MovePrepared,
                    Guid.NewGuid(),
                    SourceVolumeSerialNumber: 0,
                    SourceFileIndex: 0)));
        }
    }

    [Fact]
    public void PrepareForStartup_CorruptMoveIntentHasPriorityAndCannotBeOverwritten()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        Directory.CreateDirectory(scope.Layout.RootPath);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "retain.bin");
        File.WriteAllText(retainedPath, "retain");
        File.WriteAllBytes(scope.Layout.MoveIntentPath, [0x42]);

        var startup = scope.CreateCoordinator().PrepareForStartup();
        var emergency = scope.CreateCoordinator().RequestEmergencyReset(startup.Status);

        Assert.Equal(LocalDataResetStartupStatus.Corrupt, startup.Status);
        Assert.Equal(LocalDataEmergencyResetRequestStatus.Corrupt, emergency.Status);
        Assert.True(File.Exists(retainedPath));
        Assert.Equal(1, new FileInfo(scope.Layout.MoveIntentPath).Length);
        Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
    }

    [Fact]
    public void RequestEmergencyReset_OrphanStagingIsReclassifiedAndExplicitlyRecovered()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.LocalApplicationDataDirectory);
        var stagingPath = scope.Layout.GetReplacementStagingPath(Guid.NewGuid());
        Directory.CreateDirectory(stagingPath);
        File.WriteAllText(Path.Combine(stagingPath, "orphan.bin"), "remove");
        var coordinator = scope.CreateCoordinator();
        var observed = coordinator.PrepareForStartup();

        var request = coordinator.RequestEmergencyReset(observed.Status);
        var recovered = coordinator.PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ambiguous, observed.Status);
            Assert.Equal(LocalDataEmergencyResetRequestStatus.Requested, request.Status);
            Assert.Equal(LocalDataResetStartupStatus.Ready, recovered.Status);
            Assert.False(Directory.Exists(stagingPath));
            AssertEmergencyCompletedEpoch(scope, request);
        }
        finally
        {
            recovered.DataLease?.Dispose();
        }
    }

    [Fact]
    public void RequestEmergencyReset_PendingNormalMarkerWithStagingIsExplicitlyRecovered()
    {
        using var scope = new ResetTestScope();
        var normalRequest = scope.CreateRequestService().RequestReset();
        var stagingPath = scope.Layout.GetReplacementStagingPath(Guid.NewGuid());
        Directory.CreateDirectory(stagingPath);
        File.WriteAllText(Path.Combine(stagingPath, "orphan.bin"), "remove");
        var coordinator = scope.CreateCoordinator();
        var observed = coordinator.PrepareForStartup();

        var emergency = coordinator.RequestEmergencyReset(observed.Status);
        var recovered = coordinator.PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetRequestStatus.Requested, normalRequest.Status);
            Assert.Equal(LocalDataResetStartupStatus.Ambiguous, observed.Status);
            Assert.Equal(
                LocalDataEmergencyResetRequestStatus.Requested,
                emergency.Status);
            Assert.Equal(LocalDataResetStartupStatus.Ready, recovered.Status);
            Assert.False(Directory.Exists(stagingPath));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
            Assert.False(File.Exists(scope.Layout.MoveIntentPath));
            AssertEmergencyCompletedEpoch(scope, emergency);
        }
        finally
        {
            recovered.DataLease?.Dispose();
        }
    }

    [Fact]
    public void RequestEmergencyReset_NormalIntentWithExtraTrashIsExplicitlyRecovered()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        File.WriteAllText(Path.Combine(scope.Layout.RootPath, "secret.bin"), "remove");
        scope.CreateRequestService().RequestReset();
        var failed = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.AfterMoveIntentPrepared)
                throw new IOException("Injected move intent failure.");
        }).PrepareForStartup();
        var activeIntent = LocalDataResetMoveIntentCodec.Read(
            scope.Layout.MoveIntentPath).Intent;
        var extraTrash = scope.Layout.GetTrashPath(Guid.NewGuid());
        Directory.CreateDirectory(extraTrash);
        File.WriteAllText(Path.Combine(extraTrash, "orphan.bin"), "remove");
        var coordinator = scope.CreateCoordinator();
        var observed = coordinator.PrepareForStartup();

        var emergency = coordinator.RequestEmergencyReset(observed.Status);

        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, observed.Status);
        Assert.Equal(
            LocalDataEmergencyResetRequestStatus.Requested,
            emergency.Status);
        Assert.Equal(
            activeIntent,
            LocalDataResetMoveIntentCodec.Read(scope.Layout.MoveIntentPath).Intent);

        var recovered = coordinator.PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, recovered.Status);
            Assert.False(Directory.Exists(extraTrash));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
            Assert.False(File.Exists(scope.Layout.MoveIntentPath));
            AssertEmergencyCompletedEpoch(scope, emergency);
        }
        finally
        {
            recovered.DataLease?.Dispose();
        }
    }

    [Fact]
    public void RequestEmergencyReset_NormalIntentWithExtraStagingIsExplicitlyRecovered()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        File.WriteAllText(Path.Combine(scope.Layout.RootPath, "secret.bin"), "remove");
        scope.CreateRequestService().RequestReset();
        var failed = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.AfterMoveIntentPrepared)
                throw new IOException("Injected move intent failure.");
        }).PrepareForStartup();
        var activeIntent = LocalDataResetMoveIntentCodec.Read(
            scope.Layout.MoveIntentPath).Intent;
        var extraStaging = scope.Layout.GetReplacementStagingPath(Guid.NewGuid());
        Directory.CreateDirectory(extraStaging);
        File.WriteAllText(Path.Combine(extraStaging, "orphan.bin"), "remove");
        var coordinator = scope.CreateCoordinator();
        var observed = coordinator.PrepareForStartup();

        var emergency = coordinator.RequestEmergencyReset(observed.Status);

        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, observed.Status);
        Assert.Equal(
            LocalDataEmergencyResetRequestStatus.Requested,
            emergency.Status);
        Assert.Equal(
            activeIntent,
            LocalDataResetMoveIntentCodec.Read(scope.Layout.MoveIntentPath).Intent);

        var recovered = coordinator.PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, recovered.Status);
            Assert.False(Directory.Exists(extraStaging));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
            Assert.False(File.Exists(scope.Layout.MoveIntentPath));
            AssertEmergencyCompletedEpoch(scope, emergency);
        }
        finally
        {
            recovered.DataLease?.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestEmergencyReset_PendingCommitReplayRejectsAnyStaging(
        bool completionCommitted)
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var marker = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath).Marker;
        Directory.CreateDirectory(scope.Layout.RootPath);
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.GenerationPath,
            marker,
            replaceExisting: false);
        if (completionCommitted)
        {
            LocalDataResetAtomicFile.WriteMarker(
                scope.Layout.CompletionPath,
                marker,
                replaceExisting: false);
        }
        var stagingPath = scope.Layout.GetReplacementStagingPath(Guid.NewGuid());
        Directory.CreateDirectory(stagingPath);

        var emergency = scope.CreateCoordinator().RequestEmergencyReset(
            LocalDataResetStartupStatus.Ambiguous);

        Assert.Equal(LocalDataResetRequestStatus.Requested, request.Status);
        Assert.Equal(
            LocalDataEmergencyResetRequestStatus.Requested,
            emergency.Status);
        Assert.True(Directory.Exists(stagingPath));
    }

    [Theory]
    [InlineData("MovePrepared")]
    [InlineData("DestinationVerified")]
    public void RequestEmergencyReset_EarlyNormalIntentRequiresEmptyActiveStaging(
        string phaseName)
    {
        using var scope = new ResetTestScope();
        var phase = Enum.Parse<LocalDataResetMovePhase>(phaseName);
        var request = scope.CreateRequestService().RequestReset();
        var resetId = request.ResetId!.Value;
        var sourcePath = phase == LocalDataResetMovePhase.MovePrepared
            ? scope.Layout.RootPath
            : scope.Layout.GetTrashPath(resetId);
        Directory.CreateDirectory(sourcePath);
        using var sourcePin = LocalDataResetFileSystem.PinDirectory(sourcePath);
        var intent = new LocalDataResetMoveIntent(
            LocalDataResetMoveKind.Normal,
            phase,
            resetId,
            sourcePin.Identity.VolumeSerialNumber,
            sourcePin.Identity.FileIndex);
        LocalDataResetAtomicFile.WriteMoveIntent(
            scope.Layout.MoveIntentPath,
            intent,
            replaceExisting: false);
        var stagingPath = scope.Layout.GetReplacementStagingPath(resetId);
        Directory.CreateDirectory(stagingPath);
        File.WriteAllText(Path.Combine(stagingPath, "unexpected.bin"), "retain");

        var emergency = scope.CreateCoordinator().RequestEmergencyReset(
            LocalDataResetStartupStatus.Ambiguous);

        Assert.Equal(LocalDataEmergencyResetRequestStatus.Corrupt, emergency.Status);
        Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
        Assert.Equal(
            intent,
            LocalDataResetMoveIntentCodec.Read(scope.Layout.MoveIntentPath).Intent);
        Assert.True(File.Exists(Path.Combine(stagingPath, "unexpected.bin")));
    }

    [Fact]
    public void RequestEmergencyReset_UnresolvableValidIntentReturnsCorruptWithoutRotation()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        Directory.CreateDirectory(scope.Layout.RootPath);
        var emergency = new LocalDataResetMarker(Guid.NewGuid(), FixedRequestTime);
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.EmergencyMarkerPath,
            emergency,
            replaceExisting: false);
        var trashPath = scope.Layout.GetTrashPath(emergency.ResetId);
        Directory.CreateDirectory(trashPath);
        var rootSentinel = Path.Combine(scope.Layout.RootPath, "root.bin");
        var trashSentinel = Path.Combine(trashPath, "trash.bin");
        File.WriteAllText(rootSentinel, "retain root");
        File.WriteAllText(trashSentinel, "retain trash");
        using var rootPin = LocalDataResetFileSystem.PinDirectory(scope.Layout.RootPath);
        var intent = new LocalDataResetMoveIntent(
            LocalDataResetMoveKind.Emergency,
            LocalDataResetMovePhase.MovePrepared,
            emergency.ResetId,
            rootPin.Identity.VolumeSerialNumber,
            rootPin.Identity.FileIndex);
        LocalDataResetAtomicFile.WriteMoveIntent(
            scope.Layout.MoveIntentPath,
            intent,
            replaceExisting: false);
        var coordinator = scope.CreateCoordinator();
        var observed = coordinator.PrepareForStartup();

        var request = coordinator.RequestEmergencyReset(observed.Status);
        var repeated = coordinator.PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, observed.Status);
        Assert.Equal(LocalDataEmergencyResetRequestStatus.Corrupt, request.Status);
        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, repeated.Status);
        Assert.Equal(
            emergency.ResetId,
            LocalDataResetMarkerCodec.Read(scope.Layout.EmergencyMarkerPath).Marker.ResetId);
        Assert.Equal(intent, LocalDataResetMoveIntentCodec.Read(
            scope.Layout.MoveIntentPath).Intent);
        Assert.True(File.Exists(rootSentinel));
        Assert.True(File.Exists(trashSentinel));
    }

    [Fact]
    public void PrepareForStartup_CompletedNormalIntentContinuesPendingEmergencyReset()
    {
        using var scope = new ResetTestScope();
        var (normalRequest, emergencyRequest) =
            CreateNormalIntentWithEmergencyAuthorization(scope);
        var failed = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.BeforeMoveIntentClear)
                throw new IOException("Injected normal move intent cleanup failure.");
        }).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        Assert.False(File.Exists(scope.Layout.MarkerPath));
        Assert.True(File.Exists(scope.Layout.EmergencyMarkerPath));
        var retainedIntent = LocalDataResetMoveIntentCodec.Read(
            scope.Layout.MoveIntentPath);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, retainedIntent.Status);
        Assert.Equal(LocalDataResetMoveKind.Normal, retainedIntent.Intent.Kind);
        Assert.Equal(
            LocalDataResetMovePhase.ReplacementPrepared,
            retainedIntent.Intent.Phase);
        Assert.Equal(normalRequest.ResetId, retainedIntent.Intent.ResetId);
        AssertCompletedEpoch(scope, normalRequest);
        var removedPath = Path.Combine(scope.Layout.RootPath, "normal-root.bin");
        File.WriteAllText(removedPath, "remove during emergency recovery");

        var recovered = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, recovered.Status);
            Assert.True(recovered.ResetCompleted);
            Assert.False(File.Exists(removedPath));
            Assert.False(File.Exists(scope.Layout.MarkerPath));
            Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
            Assert.False(File.Exists(scope.Layout.MoveIntentPath));
            AssertEmergencyCompletedEpoch(scope, emergencyRequest);
        }
        finally
        {
            recovered.DataLease?.Dispose();
        }
    }

    [WindowsFact]
    public void PrepareForStartup_CompletedNormalIntentWithWrongRootIdentityFailsClosed()
    {
        using var scope = new ResetTestScope();
        var (normalRequest, _) =
            CreateNormalIntentWithEmergencyAuthorization(scope);
        var failed = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.BeforeMoveIntentClear)
                throw new IOException("Injected normal move intent cleanup failure.");
        }).PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        AssertCompletedEpoch(scope, normalRequest);

        var displacedRoot = scope.Layout.RootPath + ".displaced";
        Directory.Move(scope.Layout.RootPath, displacedRoot);
        Directory.CreateDirectory(scope.Layout.RootPath);
        File.Copy(
            Path.Combine(
                displacedRoot,
                Path.GetFileName(scope.Layout.GenerationPath)),
            scope.Layout.GenerationPath);
        var displacedSentinel = Path.Combine(displacedRoot, "displaced.bin");
        var replacementSentinel = Path.Combine(scope.Layout.RootPath, "replacement.bin");
        File.WriteAllText(displacedSentinel, "retain displaced root");
        File.WriteAllText(replacementSentinel, "retain replacement root");

        var resumed = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Corrupt, resumed.Status);
        Assert.False(resumed.CanStart);
        Assert.True(File.Exists(displacedSentinel));
        Assert.True(File.Exists(replacementSentinel));
        Assert.True(File.Exists(scope.Layout.EmergencyMarkerPath));
        Assert.True(File.Exists(scope.Layout.MoveIntentPath));
        Assert.False(File.Exists(scope.Layout.MarkerPath));
    }

    [Fact]
    public void PrepareForStartup_MarkerlessSourceDeletedIntentCannotUseCompletedEpoch()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        Directory.CreateDirectory(scope.Layout.RootPath);
        var marker = new LocalDataResetMarker(Guid.NewGuid(), FixedRequestTime);
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.CompletionPath,
            marker,
            replaceExisting: false);
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.GenerationPath,
            marker,
            replaceExisting: false);
        var intent = new LocalDataResetMoveIntent(
            LocalDataResetMoveKind.Normal,
            LocalDataResetMovePhase.SourceDeleted,
            marker.ResetId,
            SourceVolumeSerialNumber: 0,
            SourceFileIndex: 0);
        LocalDataResetAtomicFile.WriteMoveIntent(
            scope.Layout.MoveIntentPath,
            intent,
            replaceExisting: false);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.True(File.Exists(scope.Layout.MoveIntentPath));
        Assert.True(Directory.Exists(scope.Layout.RootPath));
    }

    [Fact]
    public void PrepareForStartup_MarkerlessReplacementPreparedOnlyClearsProvenIntent()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        Directory.CreateDirectory(scope.Layout.RootPath);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "retain.bin");
        File.WriteAllText(retainedPath, "retain");
        var marker = new LocalDataResetMarker(Guid.NewGuid(), FixedRequestTime);
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.CompletionPath,
            marker,
            replaceExisting: false);
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.GenerationPath,
            marker,
            replaceExisting: false);
        using var rootPin = LocalDataResetFileSystem.PinDirectory(scope.Layout.RootPath);
        var intent = new LocalDataResetMoveIntent(
            LocalDataResetMoveKind.Emergency,
            LocalDataResetMovePhase.ReplacementPrepared,
            marker.ResetId,
            SourceVolumeSerialNumber: 0,
            SourceFileIndex: 0,
            ReplacementVolumeSerialNumber: rootPin.Identity.VolumeSerialNumber,
            ReplacementFileIndex: rootPin.Identity.FileIndex);
        LocalDataResetAtomicFile.WriteMoveIntent(
            scope.Layout.MoveIntentPath,
            intent,
            replaceExisting: false);
        var cleanupCalls = 0;

        var result = scope.CreateCoordinator(() => cleanupCalls++).PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, result.Status);
            Assert.True(result.ResetCompleted);
            Assert.True(File.Exists(retainedPath));
            Assert.False(File.Exists(scope.Layout.MoveIntentPath));
            Assert.Equal(0, cleanupCalls);
        }
        finally
        {
            result.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_SourceDeletedIntentRejectsUnknownRoot()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "retain.bin");
        File.WriteAllText(retainedPath, "retain");
        var request = scope.CreateRequestService().RequestReset();
        var intent = new LocalDataResetMoveIntent(
            LocalDataResetMoveKind.Normal,
            LocalDataResetMovePhase.SourceDeleted,
            request.ResetId!.Value,
            SourceVolumeSerialNumber: 0,
            SourceFileIndex: 0);
        LocalDataResetAtomicFile.WriteMoveIntent(
            scope.Layout.MoveIntentPath,
            intent,
            replaceExisting: false);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.NotEqual(LocalDataResetStartupStatus.Ready, result.Status);
        Assert.True(File.Exists(retainedPath));
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.True(File.Exists(scope.Layout.MoveIntentPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrepareForStartup_ReplacementPreparedRequiresExactlyOneIdentityPath(
        bool createBoth)
    {
        using var scope = new ResetTestScope();
        var request = scope.CreateRequestService().RequestReset();
        var marker = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath).Marker;
        if (createBoth)
        {
            Directory.CreateDirectory(scope.Layout.RootPath);
            Directory.CreateDirectory(scope.Layout.GetReplacementStagingPath(marker.ResetId));
        }
        var intent = new LocalDataResetMoveIntent(
            LocalDataResetMoveKind.Normal,
            LocalDataResetMovePhase.ReplacementPrepared,
            marker.ResetId,
            SourceVolumeSerialNumber: 0,
            SourceFileIndex: 0,
            ReplacementVolumeSerialNumber: 1,
            ReplacementFileIndex: 2);
        LocalDataResetAtomicFile.WriteMoveIntent(
            scope.Layout.MoveIntentPath,
            intent,
            replaceExisting: false);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetRequestStatus.Requested, request.Status);
        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.True(File.Exists(scope.Layout.MoveIntentPath));
    }

    [Theory]
    [InlineData("AfterMoveIntentPrepared", "MovePrepared")]
    [InlineData("AfterRootMoved", "MovePrepared")]
    [InlineData("AfterMoveIntentDestinationVerified", "DestinationVerified")]
    [InlineData("AfterTrashDeleted", "DestinationVerified")]
    [InlineData("AfterMoveIntentSourceDeleted", "SourceDeleted")]
    [InlineData("AfterReplacementStagingCreatedBeforeIntent", "SourceDeleted")]
    [InlineData("AfterMoveIntentReplacementPrepared", "ReplacementPrepared")]
    [InlineData("AfterRootCreatedBeforeGeneration", "ReplacementPrepared")]
    [InlineData("AfterGenerationCommitted", "ReplacementPrepared")]
    [InlineData("AfterCompletionCommitted", "ReplacementPrepared")]
    [InlineData("BeforeMarkerCommit", "ReplacementPrepared")]
    [InlineData("BeforeMoveIntentClear", "ReplacementPrepared")]
    public void PrepareForStartup_NormalMoveIntentResumesEveryDurablePhase(
        string faultPointName,
        string expectedPhaseName)
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        var removedPath = Path.Combine(scope.Layout.RootPath, "remove.bin");
        File.WriteAllText(removedPath, "remove");
        var request = scope.CreateRequestService().RequestReset();
        var faultPoint = Enum.Parse<LocalDataResetFaultPoint>(faultPointName);
        var expectedPhase = Enum.Parse<LocalDataResetMovePhase>(expectedPhaseName);
        var failed = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == faultPoint)
                throw new IOException("Injected move-intent phase failure.");
        }).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        var intent = LocalDataResetMoveIntentCodec.Read(scope.Layout.MoveIntentPath);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, intent.Status);
        Assert.Equal(LocalDataResetMoveKind.Normal, intent.Intent.Kind);
        Assert.Equal(request.ResetId, intent.Intent.ResetId);
        Assert.Equal(expectedPhase, intent.Intent.Phase);

        var resumed = scope.CreateCoordinator().PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, resumed.Status);
            Assert.True(resumed.ResetCompleted);
            Assert.False(File.Exists(removedPath));
            Assert.False(File.Exists(scope.Layout.MoveIntentPath));
            AssertCompletedEpoch(scope, request);
        }
        finally
        {
            resumed.DataLease?.Dispose();
        }
    }

    [Fact]
    public void PrepareForStartup_MoveIntentDisappearsWithoutCompletionFailsClosed()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.RootPath);
        var retainedPath = Path.Combine(scope.Layout.RootPath, "retain.bin");
        File.WriteAllText(retainedPath, "retain");
        scope.CreateRequestService().RequestReset();
        var prepared = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.AfterMoveIntentPrepared)
                throw new IOException("Injected prepared intent failure.");
        }).PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, prepared.Status);

        var result = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point
                == LocalDataResetFaultPoint.AfterInitialMoveIntentReadBeforeExclusiveLease)
            {
                File.Delete(scope.Layout.MoveIntentPath);
            }
        }).PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, result.Status);
        Assert.True(File.Exists(retainedPath));
        Assert.True(File.Exists(scope.Layout.MarkerPath));
    }

    [Theory]
    [InlineData("NonEmpty", LocalDataResetStartupStatus.Ambiguous)]
    [InlineData("Reparse", LocalDataResetStartupStatus.UnsafePath)]
    public void PrepareForStartup_UncommittedStagingMustRemainEmptyAndNoFollow(
        string mutation,
        LocalDataResetStartupStatus expectedStatus)
    {
        using var scope = new ResetTestScope();
        scope.CreateRequestService().RequestReset();
        var failed = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point
                == LocalDataResetFaultPoint.AfterReplacementStagingCreatedBeforeIntent)
            {
                throw new IOException("Injected staging commit failure.");
            }
        }).PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, failed.Status);
        var request = LocalDataResetMarkerCodec.Read(scope.Layout.MarkerPath).Marker;
        var stagingPath = scope.Layout.GetReplacementStagingPath(request.ResetId);

        string? targetSentinel = null;
        if (mutation == "NonEmpty")
        {
            File.WriteAllText(Path.Combine(stagingPath, "unexpected.bin"), "retain");
        }
        else
        {
            Directory.Delete(stagingPath);
            var target = Path.Combine(scope.BaseDirectory, "staging-target");
            Directory.CreateDirectory(target);
            targetSentinel = Path.Combine(target, "retain.bin");
            File.WriteAllText(targetSentinel, "retain");
            Directory.CreateSymbolicLink(stagingPath, target);
        }

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(expectedStatus, result.Status);
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.True(File.Exists(scope.Layout.MoveIntentPath));
        if (targetSentinel is not null)
            Assert.True(File.Exists(targetSentinel));
    }

    [Theory]
    [InlineData("MissingRoot", LocalDataResetStartupStatus.Ambiguous)]
    [InlineData("CorruptGeneration", LocalDataResetStartupStatus.Corrupt)]
    public void RequestEmergencyReset_RotatesBrokenCompletedEmergencyProof(
        string mutation,
        LocalDataResetStartupStatus expectedObservedStatus)
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        Directory.CreateDirectory(scope.Layout.RootPath);
        var oldEmergency = new LocalDataResetMarker(Guid.NewGuid(), FixedRequestTime);
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.EmergencyMarkerPath,
            oldEmergency,
            replaceExisting: false);
        LocalDataResetAtomicFile.WriteMarker(
            scope.Layout.CompletionPath,
            oldEmergency,
            replaceExisting: false);
        if (mutation == "MissingRoot")
        {
            Directory.Delete(scope.Layout.RootPath);
        }
        else
        {
            File.WriteAllBytes(scope.Layout.GenerationPath, [0x42]);
        }
        var coordinator = scope.CreateCoordinator();
        var observed = coordinator.PrepareForStartup();

        var rotated = coordinator.RequestEmergencyReset(observed.Status);

        Assert.Equal(expectedObservedStatus, observed.Status);
        Assert.Equal(LocalDataEmergencyResetRequestStatus.Requested, rotated.Status);
        Assert.NotEqual(oldEmergency.ResetId, rotated.ResetId);

        var recovered = coordinator.PrepareForStartup();
        try
        {
            Assert.Equal(LocalDataResetStartupStatus.Ready, recovered.Status);
            AssertEmergencyCompletedEpoch(scope, rotated);
            Assert.False(File.Exists(scope.Layout.EmergencyMarkerPath));
            Assert.False(File.Exists(scope.Layout.MoveIntentPath));
        }
        finally
        {
            recovered.DataLease?.Dispose();
        }
    }

    [Fact]
    public void AtomicResetMovesUseWindowsWriteThroughContract()
    {
        var sourcePath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "App",
            "Services",
            "LocalDataResetService.cs");
        var source = File.ReadAllText(sourcePath);

        Assert.Contains("MoveFileExW", source, StringComparison.Ordinal);
        Assert.Contains("MoveFileFlags.WriteThrough", source, StringComparison.Ordinal);
        Assert.Contains("MoveFileFlags.ReplaceExisting", source, StringComparison.Ordinal);
        Assert.Contains(
            "LocalDataResetFileSystem.MoveWriteThrough(",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Directory.Move(_layout.RootPath",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReservedTrashNameMatchingUsesWindowsPathCaseSemantics()
    {
        var identifier = Guid.NewGuid().ToString("N");
        var canonical = $".WinVaultWarden.reset-{identifier}.trash";
        var caseVariant = $".WINVAULTWARDEN.RESET-{identifier}.TRASH";

        Assert.True(LocalDataResetFileSystem.IsReservedTrashName(canonical));
        Assert.Equal(
            OperatingSystem.IsWindows(),
            LocalDataResetFileSystem.IsReservedTrashName(caseVariant));
    }

    [Fact]
    public void PrepareForStartup_NonDirectoryDataRootIsRejected()
    {
        using var scope = new ResetTestScope();
        Directory.CreateDirectory(scope.Layout.LocalApplicationDataDirectory);
        File.WriteAllText(scope.Layout.RootPath, "not-a-directory");
        Assert.Equal(
            LocalDataResetRequestStatus.Requested,
            scope.CreateRequestService().RequestReset().Status);

        var result = scope.CreateCoordinator().PrepareForStartup();

        Assert.Equal(LocalDataResetStartupStatus.UnsafePath, result.Status);
        Assert.True(File.Exists(scope.Layout.RootPath));
        Assert.True(File.Exists(scope.Layout.MarkerPath));
        Assert.Null(result.DataLease);
    }

    private static FileStream OpenExclusive(string path) => new(
        path,
        FileMode.OpenOrCreate,
        FileAccess.ReadWrite,
        FileShare.None);

    private static (
        LocalDataResetRequestResult NormalRequest,
        LocalDataEmergencyResetRequestResult EmergencyRequest)
        CreateNormalIntentWithEmergencyAuthorization(ResetTestScope scope)
    {
        Directory.CreateDirectory(scope.Layout.RootPath);
        File.WriteAllText(Path.Combine(scope.Layout.RootPath, "secret.bin"), "remove");
        var normalRequest = scope.CreateRequestService().RequestReset();
        var prepared = scope.CreateCoordinator(faultHook: (point, _) =>
        {
            if (point == LocalDataResetFaultPoint.AfterMoveIntentPrepared)
                throw new IOException("Injected move intent preparation failure.");
        }).PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Unavailable, prepared.Status);

        var extraTrash = scope.Layout.GetTrashPath(Guid.NewGuid());
        Directory.CreateDirectory(extraTrash);
        File.WriteAllText(Path.Combine(extraTrash, "orphan.bin"), "remove");
        var coordinator = scope.CreateCoordinator();
        var observed = coordinator.PrepareForStartup();
        Assert.Equal(LocalDataResetStartupStatus.Ambiguous, observed.Status);
        var emergencyRequest = coordinator.RequestEmergencyReset(observed.Status);
        Assert.Equal(
            LocalDataEmergencyResetRequestStatus.Requested,
            emergencyRequest.Status);
        return (normalRequest, emergencyRequest);
    }

    private static LocalDataResetMarker AssertCompletedEpoch(
        ResetTestScope scope,
        LocalDataResetRequestResult request)
    {
        var completion = LocalDataResetMarkerCodec.Read(scope.Layout.CompletionPath);
        var generation = LocalDataResetMarkerCodec.Read(scope.Layout.GenerationPath);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, completion.Status);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, generation.Status);
        Assert.Equal(completion.Marker, generation.Marker);
        Assert.Equal(request.ResetId, completion.Marker.ResetId);
        Assert.Equal(request.RequestedAt, completion.Marker.RequestedAt);
        return completion.Marker;
    }

    private static LocalDataResetMarker AssertEmergencyCompletedEpoch(
        ResetTestScope scope,
        LocalDataEmergencyResetRequestResult request)
    {
        var completion = LocalDataResetMarkerCodec.Read(scope.Layout.CompletionPath);
        var generation = LocalDataResetMarkerCodec.Read(scope.Layout.GenerationPath);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, completion.Status);
        Assert.Equal(LocalDataResetMarkerReadStatus.Valid, generation.Status);
        Assert.Equal(completion.Marker, generation.Marker);
        Assert.Equal(request.ResetId, completion.Marker.ResetId);
        Assert.Equal(request.RequestedAt, completion.Marker.RequestedAt);
        return completion.Marker;
    }

    private static void WriteCorruptNormalRequest(ResetTestScope scope)
    {
        Directory.CreateDirectory(scope.Layout.ControlRoot);
        File.WriteAllBytes(scope.Layout.MarkerPath, [0x42]);
    }

    private static void WriteLegacyV1Marker(
        string path,
        LocalDataResetMarker marker)
    {
        var bytes = LocalDataResetMarkerCodec.SerializeLegacyV1(marker);
        File.WriteAllBytes(path, bytes);
        Assert.Equal(LocalDataResetMarkerCodec.LegacySerializedLength, bytes.Length);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinVaultWarden.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class ResetTestScope : IDisposable
    {
        public ResetTestScope()
        {
            BaseDirectory = Path.Combine(
                Path.GetTempPath(),
                "WinVaultWarden.LocalReset.Tests",
                Guid.NewGuid().ToString("N"));
            Layout = new AppStorageLayout(
                Path.Combine(BaseDirectory, "local-app-data"),
                Path.Combine(BaseDirectory, "temp"));
        }

        public string BaseDirectory { get; }

        public AppStorageLayout Layout { get; }

        public LocalDataResetService CreateRequestService(
            Action<LocalDataResetRequestFaultPoint, string?>? faultHook = null) => new(
            Layout,
            new FixedTimeProvider(FixedRequestTime),
            faultHook);

        public LocalDataResetStartupCoordinator CreateCoordinator(
            Action? externalCleanup = null,
            Action<LocalDataResetFaultPoint, string?>? faultHook = null,
            TimeSpan? exclusiveLockTimeout = null) => new(
                Layout,
                externalCleanup ?? (static () => { }),
                faultHook,
                TimeProvider.System,
                exclusiveLockTimeout ?? TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(10));

        public void Dispose()
        {
            if (!Directory.Exists(BaseDirectory))
                return;

            DeleteReparseChildren(BaseDirectory);
            Directory.Delete(BaseDirectory, recursive: true);
        }

        private static void DeleteReparseChildren(string directory)
        {
            foreach (var path in Directory.GetFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                        Directory.Delete(path, recursive: false);
                    else
                        File.Delete(path);
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    DeleteReparseChildren(path);
                }
            }
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
