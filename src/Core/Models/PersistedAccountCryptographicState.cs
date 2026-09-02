using System.Text.Json.Serialization;

namespace Core.Models;

public sealed record PersistedAccountCryptographicState(
    int SecurityVersion,
    string WrappedPrivateKey,
    string? PublicKey = null,
    string? SignedPublicKey = null,
    string? WrappedSigningKey = null,
    string? VerifyingKey = null,
    string? SignedSecurityState = null)
{
    public const int MaximumEncodedValueLength = 1024 * 1024;

    [JsonIgnore]
    public bool HasValidShape => SecurityVersion switch
    {
        1 => HasValue(WrappedPrivateKey)
            && PublicKey is null
            && SignedPublicKey is null
            && WrappedSigningKey is null
            && VerifyingKey is null
            && SignedSecurityState is null,
        // The SDK derives public/verifying keys from the wrapped private keys and temporarily
        // permits signedPublicKey to be absent for transitional accounts.
        2 => HasValue(WrappedPrivateKey)
            && HasValue(WrappedSigningKey)
            && HasValue(SignedSecurityState)
            && IsAbsentOrHasValue(PublicKey)
            && IsAbsentOrHasValue(SignedPublicKey)
            && IsAbsentOrHasValue(VerifyingKey),
        _ => false,
    };

    private static bool IsAbsentOrHasValue(string? value) =>
        value is null || HasValue(value);

    private static bool HasValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumEncodedValueLength;
}
