namespace Core.Models;

public static class MasterPasswordSaltPolicy
{
    public const int MaxLength = 256;

    public static string NormalizeEmail(string email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return email.Trim().ToLowerInvariant();
    }

    public static string Resolve(string email, string? explicitSalt)
    {
        var salt = explicitSalt is null
            ? NormalizeEmail(email)
            : explicitSalt;
        Validate(salt);
        return salt;
    }

    public static bool IsValid(string? salt) =>
        !string.IsNullOrWhiteSpace(salt)
        && salt.Length <= MaxLength;

    public static void Validate(string? salt)
    {
        if (!IsValid(salt))
        {
            throw new ArgumentException(
                $"Master-password salt must contain 1 to {MaxLength} non-whitespace characters.",
                nameof(salt));
        }
    }
}
