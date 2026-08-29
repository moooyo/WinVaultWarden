using Core.Models;

namespace Vault;

/// <summary>
/// The replacement file was created successfully, but deletion of the previous Send has an
/// unknown outcome. Callers must refresh before taking another destructive action.
/// </summary>
public sealed class SendReplacementUncertainException : Exception
{
    public SendReplacementUncertainException(
        string existingSendId,
        SendMutationReceipt replacement,
        Exception deleteError)
        : base(
            "The replacement was uploaded, but deletion of the previous Send could not be confirmed.",
            deleteError)
    {
        ExistingSendId = existingSendId;
        Replacement = replacement;
    }

    public string ExistingSendId { get; }
    public SendMutationReceipt Replacement { get; }
}
