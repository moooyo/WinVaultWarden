namespace Core.Models;

public enum TwoFactorUserVerificationKind
{
    MasterPassword,
    Otp,
    AuthRequestAccessCode,
}

/// <summary>
/// One mutually exclusive proof for a server-side two-factor management action.
/// The secret is intentionally unavailable outside trusted service assemblies and is never
/// included in the object's string representation.
/// </summary>
public sealed class TwoFactorUserVerification
{
    private TwoFactorUserVerification(TwoFactorUserVerificationKind kind, string secret)
    {
        Kind = kind;
        Secret = secret;
    }

    public TwoFactorUserVerificationKind Kind { get; }

    internal string Secret { get; }

    public static TwoFactorUserVerification FromMasterPassword(string masterPassword)
    {
        if (string.IsNullOrEmpty(masterPassword))
            throw new ArgumentException("A master password is required.", nameof(masterPassword));
        return new TwoFactorUserVerification(
            TwoFactorUserVerificationKind.MasterPassword,
            masterPassword);
    }

    public static TwoFactorUserVerification FromOtp(string otp) =>
        FromTrimmedSecret(TwoFactorUserVerificationKind.Otp, otp, nameof(otp));

    public static TwoFactorUserVerification FromAuthRequestAccessCode(string accessCode) =>
        FromTrimmedSecret(
            TwoFactorUserVerificationKind.AuthRequestAccessCode,
            accessCode,
            nameof(accessCode));

    public override string ToString() => Kind.ToString();

    private static TwoFactorUserVerification FromTrimmedSecret(
        TwoFactorUserVerificationKind kind,
        string value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A verification value is required.", parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 4096)
            throw new ArgumentException("The verification value is too long.", parameterName);
        return new TwoFactorUserVerification(kind, normalized);
    }
}
