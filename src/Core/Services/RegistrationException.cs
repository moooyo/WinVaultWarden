namespace Core.Services;

public sealed class RegistrationException : Exception
{
    public RegistrationException(string message) : base(message) { }
}
