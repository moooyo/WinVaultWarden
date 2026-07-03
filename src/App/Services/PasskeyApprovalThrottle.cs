namespace App.Services;

/// <summary>
/// WinUI-free per-rpId cooldown for passkey approval prompts. Keying by rpId (not
/// full origin) stops a malicious page from spamming approval dialogs — and stops
/// rotating subdomain origins of one rpId from bypassing the cooldown
/// (prompt-fatigue / clickjacking). Time source is injectable so it can be
/// unit-tested without a real clock.
/// </summary>
public sealed class PasskeyApprovalThrottle
{
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, DateTimeOffset> _lastPrompt = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public PasskeyApprovalThrottle(TimeSpan cooldown, Func<DateTimeOffset>? clock = null)
    {
        _cooldown = cooldown;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Number of keys (rpIds) currently tracked. Test seam to verify stale-entry pruning;
    /// kept internal because App.Tests links this file as source.
    /// </summary>
    internal int TrackedKeyCount
    {
        get { lock (_gate) return _lastPrompt.Count; }
    }

    /// <summary>
    /// Returns true and records the timestamp if a prompt for <paramref name="key"/>
    /// (the request's rpId) is allowed now; returns false if the previous prompt was
    /// within the cooldown.
    /// </summary>
    public bool TryBegin(string key)
    {
        var mapKey = key ?? string.Empty;
        var now = _clock();

        lock (_gate)
        {
            // Prune stale entries so a flood of synthetic keys can't grow the map
            // forever. A stale entry would be allowed again anyway, so removing it is
            // behaviorally invisible. Snapshot keys first to avoid mutating during enum.
            var stale = new List<string>();
            foreach (var entry in _lastPrompt)
            {
                if (now - entry.Value >= _cooldown)
                    stale.Add(entry.Key);
            }
            foreach (var k in stale)
                _lastPrompt.Remove(k);

            if (_lastPrompt.TryGetValue(mapKey, out var last) && now - last < _cooldown)
                return false;

            _lastPrompt[mapKey] = now;
            return true;
        }
    }
}
