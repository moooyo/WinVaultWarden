namespace Crypto.PasswordStrength;

internal static class PasswordStrengthBudget
{
    // This includes every valid protocol master password (at most 128 UTF-16
    // code units) while bounding work for passwords obtained from vault data.
    internal const int MaximumLength = 256;

    internal static void ValidateMatcherInput(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length > MaximumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(password),
                $"Password matching is limited to {MaximumLength} UTF-16 code units.");
        }
    }
}
