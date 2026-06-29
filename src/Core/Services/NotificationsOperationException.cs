namespace Core.Services;

public sealed class NotificationsOperationException : Exception
{
    public NotificationsOperationException(string message) : base(message) { }
}
