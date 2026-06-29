namespace Core.Services;

public sealed class TwoFactorOperationException : Exception
{
    public TwoFactorOperationException(string message) : base(message) { }
}
