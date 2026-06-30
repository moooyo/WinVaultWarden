namespace Core.Services;

public sealed class EmergencyAccessOperationException : Exception
{
    public EmergencyAccessOperationException(string message, Exception? inner = null)
        : base(message, inner) { }
}
