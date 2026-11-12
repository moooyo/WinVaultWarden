namespace Core.Services;

/// <summary>
/// The import request was dispatched but no complete result was observed. The server may
/// have created some or all folders and items. Repeating the operation can duplicate data.
/// This is distinct from a confirmed write followed by a failed local refresh.
/// </summary>
public sealed class VaultImportOutcomeUncertainException : Exception
{
    public VaultImportOutcomeUncertainException(int attemptedCipherCount, Exception innerException)
        : base("The import result is uncertain. Synchronize and inspect the vault before submitting the source again.", innerException)
    {
        AttemptedCipherCount = attemptedCipherCount;
    }

    public int AttemptedCipherCount { get; }
}
