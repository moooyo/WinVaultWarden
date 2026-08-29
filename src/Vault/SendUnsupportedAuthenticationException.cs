using Core.Enums;

namespace Vault;

public sealed class SendUnsupportedAuthenticationException : InvalidOperationException
{
    public SendUnsupportedAuthenticationException(SendAuthType authType, int rawAuthType)
        : base("This Send authentication mode cannot be preserved during an update.")
    {
        AuthType = authType;
        RawAuthType = rawAuthType;
    }

    public SendAuthType AuthType { get; }
    public int RawAuthType { get; }
}
