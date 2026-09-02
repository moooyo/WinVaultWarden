using System.Globalization;
using Core.Models;

namespace Crypto.PasswordStrength;

public sealed class MasterPasswordPolicyEvaluator
{
    private const string AllowedSpecialCharacters = "!@#$%^&*";

    private readonly PasswordStrengthEvaluator _strengthEvaluator;

    public MasterPasswordPolicyEvaluator(PasswordStrengthEvaluator strengthEvaluator)
    {
        ArgumentNullException.ThrowIfNull(strengthEvaluator);
        _strengthEvaluator = strengthEvaluator;
    }

    public MasterPasswordEvidence CaptureEvidence(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        return new MasterPasswordEvidence(
            _strengthEvaluator.Evaluate(password).Score,
            password.Length,
            !string.Equals(password.ToLower(CultureInfo.CurrentCulture), password, StringComparison.Ordinal),
            !string.Equals(password.ToUpper(CultureInfo.CurrentCulture), password, StringComparison.Ordinal),
            password.Any(static character => character is >= '0' and <= '9'),
            password.Any(static character => AllowedSpecialCharacters.Contains(character)));
    }

    public bool Evaluate(
        MasterPasswordPolicyRequirements? requirements,
        MasterPasswordEvidence evidence)
    {
        if (requirements is null)
            return true;

        if (evidence is null
            || !requirements.HasValidProtocolValues
            || !evidence.HasValidValues)
        {
            return false;
        }

        if (requirements.MinComplexity is > 0
            && evidence.StrengthScore < requirements.MinComplexity.Value)
        {
            return false;
        }

        if (requirements.MinLength is > 0
            && evidence.Utf16Length < requirements.MinLength.Value)
        {
            return false;
        }

        if (requirements.RequireUpper && !evidence.HasUpper)
            return false;
        if (requirements.RequireLower && !evidence.HasLower)
            return false;
        if (requirements.RequireNumbers && !evidence.HasNumber)
            return false;
        if (requirements.RequireSpecial && !evidence.HasSpecial)
            return false;

        return true;
    }
}
