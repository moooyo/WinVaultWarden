using Core.Session;

namespace Vault;

public sealed class SessionTerminationSignal
    : ISessionTerminationSource, ISessionTerminationSink
{
    public event Action<SessionTerminationNotice>? TerminationRequested;

    public void Request(SessionTerminationNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        try
        {
            TerminationRequested?.Invoke(notice);
        }
        catch
        {
            // Core invalidation has already completed; a listener cannot undo it.
        }
    }
}
