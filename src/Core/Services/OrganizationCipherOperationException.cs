namespace Core.Services;

public sealed class OrganizationCipherOperationException : InvalidOperationException
{
    public OrganizationCipherOperationException()
        : base("The organization cipher operation is not permitted by the current key or permission state.")
    {
    }

    public OrganizationCipherOperationException(string message)
        : base(message)
    {
    }

    public OrganizationCipherOperationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
