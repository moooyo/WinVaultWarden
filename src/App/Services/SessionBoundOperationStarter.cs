using Core.Session;
using Vault;

namespace App.Services;

public sealed class SessionBoundOperation<T> : IDisposable
    where T : class
{
    private IDisposable? _registration;

    internal SessionBoundOperation(T operation, IDisposable registration)
    {
        Operation = operation;
        _registration = registration;
    }

    public T Operation { get; }

    public void Dispose() => Interlocked.Exchange(ref _registration, null)?.Dispose();
}

public static class SessionBoundOperationStarter
{
    /// <summary>
    /// Registers and starts an operation while lifecycle and expected-session gates are both held.
    /// The operation itself must be awaited only after this method returns.
    /// </summary>
    public static SessionBoundOperation<T>? TryStart<T>(
        IAppLifecycleCoordinator lifecycle,
        VaultSession session,
        VaultSessionLease expectedSession,
        Func<IDisposable> register,
        Func<T> start)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(expectedSession);
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(start);

        SessionBoundOperation<T>? started = null;
        var accepted = lifecycle.TryRunSensitive(
            () => session.TryRunIfUnlocked(
                expectedSession,
                () =>
                {
                    var registration = register()
                        ?? throw new InvalidOperationException("Transient registration returned null.");
                    try
                    {
                        var operation = start()
                            ?? throw new InvalidOperationException("Sensitive operation start returned null.");
                        started = new SessionBoundOperation<T>(operation, registration);
                    }
                    catch
                    {
                        registration.Dispose();
                        throw;
                    }
                }));

        if (accepted && started is not null)
            return started;

        started?.Dispose();
        return null;
    }
}
