namespace Core.Services;

public sealed class VaultMutationReconciliationException : Exception
{
    public VaultMutationReconciliationException(string message, IReadOnlyCollection<string> unresolvedCipherIds)
        : base(message)
    {
        UnresolvedCipherIds = Array.AsReadOnly(unresolvedCipherIds.ToArray());
    }

    public IReadOnlyList<string> UnresolvedCipherIds { get; }
}
