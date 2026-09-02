namespace Vault;

public sealed class SendStateStaleException : InvalidOperationException
{
    public SendStateStaleException()
        : base("Send changes require a successful vault sync after offline unlock.")
    {
    }
}
