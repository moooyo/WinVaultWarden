namespace Core.Services;

public sealed class OrganizationPolicyException : InvalidOperationException
{
    public OrganizationPolicyException(string message)
        : base(message)
    {
    }
}
