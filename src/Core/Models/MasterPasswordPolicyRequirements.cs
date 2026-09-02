namespace Core.Models;

public sealed record MasterPasswordPolicyRequirements(
    int? MinComplexity = null,
    int? MinLength = null,
    bool RequireUpper = false,
    bool RequireLower = false,
    bool RequireNumbers = false,
    bool RequireSpecial = false,
    bool EnforceOnLogin = false)
{
    public const int MinimumComplexity = 0;
    public const int MaximumComplexity = 4;
    public const int MinimumLength = 12;
    public const int MaximumLength = 128;

    public bool HasValidProtocolValues =>
        (!MinComplexity.HasValue
            || MinComplexity.Value is >= MinimumComplexity and <= MaximumComplexity)
        && (!MinLength.HasValue
            || MinLength.Value == 0
            || MinLength.Value is >= MinimumLength and <= MaximumLength);
}
