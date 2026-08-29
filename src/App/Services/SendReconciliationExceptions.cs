namespace App.Services;

public sealed class SendSnapshotSupersededException : OperationCanceledException
{
    public SendSnapshotSupersededException()
        : base("The Send snapshot was superseded by a concurrent mutation.")
    {
    }
}

public sealed class SendMutationInProgressException : InvalidOperationException
{
    public SendMutationInProgressException(string sendId)
        : base("Another mutation is already in progress for this Send.") =>
        SendId = sendId;

    public string SendId { get; }
}
