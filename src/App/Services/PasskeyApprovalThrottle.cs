namespace App.Services;

/// <summary>
/// WinUI-free per-origin cooldown for passkey approval prompts. Prevents a
/// malicious page from spamming approval dialogs (prompt-fatigue / clickjacking).
/// Time source is injectable so it can be unit-tested without a real clock.
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
    /// Returns true and records the timestamp if a prompt for <paramref name="origin"/>
    /// is allowed now; returns false if the previous prompt was within the cooldown.
    /// </summary>
    public bool TryBegin(string origin)
    {
        var key = origin ?? string.Empty;
        var now = _clock();

        lock (_gate)
        {
            if (_lastPrompt.TryGetValue(key, out var last) && now - last < _cooldown)
                return false;

            _lastPrompt[key] = now;
            return true;
        }
    }
}
