namespace Core.Services;

public sealed class AuthSessionPersistenceException : Exception
{
    public AuthSessionPersistenceException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
