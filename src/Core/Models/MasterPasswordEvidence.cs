namespace Core.Models;

public sealed record MasterPasswordEvidence(
    int StrengthScore,
    int Utf16Length,
    bool HasUpper,
    bool HasLower,
    bool HasNumber,
    bool HasSpecial)
{
    public bool HasValidValues =>
        StrengthScore is >= MasterPasswordPolicyRequirements.MinimumComplexity
            and <= MasterPasswordPolicyRequirements.MaximumComplexity
        && Utf16Length >= 0;
}
