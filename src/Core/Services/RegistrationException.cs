namespace Core.Services;

public sealed class RegistrationException : Exception
{
    public RegistrationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
