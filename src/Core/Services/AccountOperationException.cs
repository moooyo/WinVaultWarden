namespace Core.Services;

public sealed class AccountOperationException : Exception
{
    public AccountOperationException(string message) : base(message) { }
}
