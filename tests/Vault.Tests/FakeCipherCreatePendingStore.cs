using Core.Abstractions;
using Core.Models;

namespace Vault.Tests;

internal sealed class FakeCipherCreatePendingStore : ICipherCreatePendingStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Scope, CipherCreatePendingRecord> _entries = [];
    private readonly HashSet<Scope> _activeReservations = [];

    public Exception? CreateFailure { get; set; }
    public Exception? UpdateFailure { get; set; }
    public Exception? RemoveFailure { get; set; }
    public CipherCreatePendingReadStatus? ReadStatusOverride { get; set; }
    public int CreateCalls { get; private set; }
    public int UpdateCalls { get; private set; }
    public int ConfirmedRemovalCalls { get; private set; }
    public int AbandonCalls { get; private set; }

    public CipherCreatePendingReadResult Read(string accountId, ServerEndpoints endpoints)
    {
        lock (_gate)
        {
            if (ReadStatusOverride is { } status)
            {
                return status switch
                {
                    CipherCreatePendingReadStatus.Absent => CipherCreatePendingReadResult.Absent,
                    CipherCreatePendingReadStatus.Corrupt => CipherCreatePendingReadResult.Corrupt,
                    CipherCreatePendingReadStatus.Unavailable => CipherCreatePendingReadResult.Unavailable,
                    _ => throw new InvalidOperationException("A Found override requires an entry."),
                };
            }

            var scope = new Scope(accountId, endpoints);
            if (!_entries.TryGetValue(scope, out var record))
                return CipherCreatePendingReadResult.Absent;
            if (record.IsInFlight && !_activeReservations.Contains(scope))
            {
                record = new CipherCreatePendingRecord(
                    record.ReservationId,
                    checked(record.Revision + 1),
                    false,
                    record.Pending);
                _entries[scope] = record;
            }
            return CipherCreatePendingReadResult.Found(record);
        }
    }

    public ICipherCreatePendingReservation? TryCreate(CipherCreatePending pending)
    {
        lock (_gate)
        {
            CreateCalls++;
            if (CreateFailure is not null)
                throw CreateFailure;
            var scope = Scope.From(pending);
            if (_entries.ContainsKey(scope))
                return null;
            var record = new CipherCreatePendingRecord(
                Guid.NewGuid(),
                1,
                true,
                pending);
            _entries.Add(scope, record);
            _activeReservations.Add(scope);
            return new Reservation(this, scope, record);
        }
    }

    public CipherCreatePendingRecord? TryUpdate(
        CipherCreatePendingRecord expected,
        CipherCreatePending pending)
    {
        lock (_gate)
        {
            UpdateCalls++;
            if (UpdateFailure is not null)
                throw UpdateFailure;
            var scope = Scope.From(expected.Pending);
            if (!_entries.TryGetValue(scope, out var existing)
                || !Matches(existing, expected)
                || !WitnessesMatch(existing.Pending.Witness, pending.Witness))
                return null;

            var merged = Merge(existing.Pending, pending);
            if (StateMatches(existing.Pending, merged))
                return existing;
            var updated = new CipherCreatePendingRecord(
                existing.ReservationId,
                checked(existing.Revision + 1),
                existing.IsInFlight,
                merged);
            _entries[scope] = updated;
            return updated;
        }
    }

    public CipherCreatePendingRecord? Release(
        CipherCreatePendingRecord expected,
        CipherCreatePending pending)
    {
        lock (_gate)
        {
            UpdateCalls++;
            if (UpdateFailure is not null)
                throw UpdateFailure;
            var scope = Scope.From(expected.Pending);
            if (!_entries.TryGetValue(scope, out var existing)
                || !Matches(existing, expected)
                || !existing.IsInFlight
                || !WitnessesMatch(existing.Pending.Witness, pending.Witness))
            {
                return null;
            }

            var released = new CipherCreatePendingRecord(
                existing.ReservationId,
                checked(existing.Revision + 1),
                false,
                Merge(existing.Pending, pending));
            _entries[scope] = released;
            return released;
        }
    }

    public bool RemoveConfirmed(CipherCreatePendingRecord expected)
    {
        lock (_gate)
        {
            ConfirmedRemovalCalls++;
            if (RemoveFailure is not null)
                throw RemoveFailure;
            var scope = Scope.From(expected.Pending);
            return expected.Pending.CommitConfirmed
                && !string.IsNullOrWhiteSpace(expected.Pending.ExpectedCipherId)
                && _entries.TryGetValue(scope, out var existing)
                && Matches(existing, expected)
                && _entries.Remove(scope);
        }
    }

    public bool Abandon(CipherCreatePendingRecord expected)
    {
        lock (_gate)
        {
            AbandonCalls++;
            if (RemoveFailure is not null)
                throw RemoveFailure;
            var scope = Scope.From(expected.Pending);
            return !expected.IsInFlight
                && !_activeReservations.Contains(scope)
                && _entries.TryGetValue(scope, out var existing)
                && Matches(existing, expected)
                && _entries.Remove(scope);
        }
    }

    public void Seed(CipherCreatePending pending)
    {
        lock (_gate)
            _entries[Scope.From(pending)] = new CipherCreatePendingRecord(
                Guid.NewGuid(),
                1,
                false,
                pending);
    }

    private void ReleaseReservation(Scope scope)
    {
        lock (_gate)
            _activeReservations.Remove(scope);
    }

    private static CipherCreatePending Merge(
        CipherCreatePending existing,
        CipherCreatePending incoming)
    {
        if (existing.ExpectedCipherId is not null
            && incoming.ExpectedCipherId is not null
            && !string.Equals(
                existing.ExpectedCipherId,
                incoming.ExpectedCipherId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The expected cipher identifier cannot change.");
        }

        return existing with
        {
            CommitConfirmed = existing.CommitConfirmed || incoming.CommitConfirmed,
            ExpectedCipherId = existing.ExpectedCipherId ?? incoming.ExpectedCipherId,
        };
    }

    private static bool Matches(
        CipherCreatePendingRecord existing,
        CipherCreatePendingRecord expected) =>
        existing.ReservationId == expected.ReservationId
        && existing.Revision == expected.Revision
        && existing.IsInFlight == expected.IsInFlight
        && WitnessesMatch(existing.Pending.Witness, expected.Pending.Witness)
        && StateMatches(existing.Pending, expected.Pending);

    private static bool WitnessesMatch(
        CipherCreateWitness left,
        CipherCreateWitness right) =>
        string.Equals(left.AccountId, right.AccountId, StringComparison.Ordinal)
        && left.Endpoints == right.Endpoints
        && left.CipherType == right.CipherType
        && string.Equals(left.OrganizationId, right.OrganizationId, StringComparison.Ordinal)
        && left.CollectionIds.SequenceEqual(right.CollectionIds, StringComparer.Ordinal)
        && left.BaselineCipherIds.SequenceEqual(right.BaselineCipherIds, StringComparer.Ordinal)
        && string.Equals(left.KeyFingerprint, right.KeyFingerprint, StringComparison.Ordinal);

    private static bool StateMatches(CipherCreatePending left, CipherCreatePending right) =>
        left.CommitConfirmed == right.CommitConfirmed
        && string.Equals(left.ExpectedCipherId, right.ExpectedCipherId, StringComparison.Ordinal);

    private sealed record Scope(string AccountId, ServerEndpoints Endpoints)
    {
        public static Scope From(CipherCreatePending pending) => new(
            pending.Witness.AccountId,
            pending.Witness.Endpoints);
    }

    private sealed class Reservation(
        FakeCipherCreatePendingStore owner,
        Scope scope,
        CipherCreatePendingRecord record) : ICipherCreatePendingReservation
    {
        private FakeCipherCreatePendingStore? _owner = owner;

        public CipherCreatePendingRecord Record { get; } = record;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.ReleaseReservation(scope);
        }
    }
}
