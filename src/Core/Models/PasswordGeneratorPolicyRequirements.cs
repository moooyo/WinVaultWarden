namespace Core.Models;

public enum PasswordGeneratorCredentialType
{
    Password,
    Passphrase,
}

public sealed record PasswordGeneratorPolicyRequirements(
    int ApplicablePolicyCount = 0,
    bool HasMalformedPolicyData = false,
    int MinLength = 0,
    bool UseUppercase = false,
    bool UseLowercase = false,
    bool UseNumbers = false,
    int MinNumbers = 0,
    bool UseSpecial = false,
    int MinSpecial = 0,
    int MinNumberWords = 0,
    bool Capitalize = false,
    bool IncludeNumber = false,
    PasswordGeneratorCredentialType? OverridePasswordType = null)
{
    public static PasswordGeneratorPolicyRequirements None { get; } = new();

    public int EffectiveMinNumbers => Math.Max(MinNumbers, UseNumbers ? 1 : 0);

    public int EffectiveMinSpecial => Math.Max(MinSpecial, UseSpecial ? 1 : 0);

    public int MinimumRequiredPasswordLength =>
        (UseUppercase ? 1 : 0)
        + (UseLowercase ? 1 : 0)
        + EffectiveMinNumbers
        + EffectiveMinSpecial;

    public bool PasswordRequirementsAreSatisfiable =>
        MinimumRequiredPasswordLength <= 128;

    public bool CanGenerateCompliantCredential =>
        !HasMalformedPolicyData && PasswordRequirementsAreSatisfiable;

    public bool InEffect =>
        MinLength > 0
        || UseUppercase
        || UseLowercase
        || UseNumbers
        || MinNumbers > 0
        || UseSpecial
        || MinSpecial > 0
        || MinNumberWords > 0
        || Capitalize
        || IncludeNumber
        || OverridePasswordType.HasValue;
}
