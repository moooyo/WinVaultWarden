namespace Core.Passkeys;

// Identifies a single passkey ceremony slot: one relying party inside one browser frame.
// TabId/FrameId come from the trusted BrowserRequestContext and CanonicalRpId from the Task 1
// policy, so a page can never widen or forge its own slot.
public readonly record struct PasskeyRequestKey(int TabId, int FrameId, string CanonicalRpId);

// Handle returned by PasskeyRequestGate.TryEnter; disposing releases the single in-flight slot
// for its key. Release is idempotent so a caller may dispose defensively in a finally block.
public sealed class GateLease : IDisposable
{
    private Action? _release;

    internal GateLease(Action release) => _release = release;

    public void Dispose()
    {
        // Interlocked so a double dispose (finally + using) frees the slot exactly once.
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
/// Serialises and rate-limits browser passkey ceremonies per (tab, frame, canonical RP). At most
/// one ceremony may be in flight for a key at a time, and at most three may start inside any
/// rolling ten-second window. Both limits reuse the stable wire error codes so the browser gets a
/// deterministic reason. A <see cref="TimeProvider"/> drives the window so the behaviour is
/// unit-testable without wall-clock waits. Empty per-key time buckets are pruned on release and on
/// the next lookup so a caller cycling random relying-party ids cannot grow the dictionary without
/// bound.
/// </summary>
public sealed class PasskeyRequestGate
{
    private const int MaxRequestsPerWindow = 3;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly HashSet<PasskeyRequestKey> _inFlight = [];
    private readonly Dictionary<PasskeyRequestKey, Queue<DateTimeOffset>> _recent = new();

    public PasskeyRequestGate(TimeProvider timeProvider) =>
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>
    /// Attempts to reserve the ceremony slot for <paramref name="key"/>. Returns a lease that must
    /// be disposed to release the slot, or <see langword="null"/> with a stable error code when a
    /// ceremony is already in flight (<see cref="PasskeyErrorCodes.RequestInProgress"/>) or the
    /// rolling window is saturated (<see cref="PasskeyErrorCodes.RateLimited"/>).
    /// </summary>
    public GateLease? TryEnter(PasskeyRequestKey key, out string? errorCode)
    {
        lock (_gate)
        {
            if (_inFlight.Contains(key))
            {
                errorCode = PasskeyErrorCodes.RequestInProgress;
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            var recent = GetAndPrune(key, now);
            if (recent.Count >= MaxRequestsPerWindow)
            {
                errorCode = PasskeyErrorCodes.RateLimited;
                return null;
            }

            recent.Enqueue(now);
            _inFlight.Add(key);
            errorCode = null;
            return new GateLease(() => Release(key));
        }
    }

    private void Release(PasskeyRequestKey key)
    {
        lock (_gate)
        {
            _inFlight.Remove(key);
            // Drop the time bucket once it holds no live samples so idle keys do not accumulate.
            if (_recent.TryGetValue(key, out var bucket))
            {
                Prune(bucket, _timeProvider.GetUtcNow());
                if (bucket.Count == 0)
                    _recent.Remove(key);
            }
        }
    }

    private Queue<DateTimeOffset> GetAndPrune(PasskeyRequestKey key, DateTimeOffset now)
    {
        if (!_recent.TryGetValue(key, out var bucket))
        {
            bucket = new Queue<DateTimeOffset>();
            _recent[key] = bucket;
        }

        Prune(bucket, now);
        return bucket;
    }

    // Evicts every sample at or before the window start so a ceremony exactly Window old frees a slot.
    private static void Prune(Queue<DateTimeOffset> bucket, DateTimeOffset now)
    {
        var cutoff = now - Window;
        while (bucket.Count > 0 && bucket.Peek() <= cutoff)
            bucket.Dequeue();
    }
}
