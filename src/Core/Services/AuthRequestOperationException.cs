namespace Core.Services;

public sealed class AuthRequestOperationException : Exception
{
    public AuthRequestOperationException(string message) : base(message) { }
}
