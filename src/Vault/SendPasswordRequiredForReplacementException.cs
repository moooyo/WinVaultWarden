namespace Vault;

/// <summary>
/// A password-protected file Send cannot be recreated without plaintext password input.
/// </summary>
public sealed class SendPasswordRequiredForReplacementException : InvalidOperationException
{
    public SendPasswordRequiredForReplacementException()
        : base("Replacing a password-protected file Send requires a password.")
    {
    }
}
