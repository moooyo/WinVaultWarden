using Core.Enums;
using Core.Models;

namespace Core.Services;

public static class CipherWritePolicy
{
    public static bool CanWrite(CipherType type, VaultCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return type is
            CipherType.Login
            or CipherType.SecureNote
            or CipherType.Card
            or CipherType.Identity
            or CipherType.SshKey
            || capabilities.CanWriteNewCipherTypes && type is
                CipherType.BankAccount
                or CipherType.DriversLicense
                or CipherType.Passport;
    }

    // Types 1-5 remain the compatibility baseline for callers without negotiated capabilities.
    public static bool CanWriteWithoutFeatureNegotiation(CipherType type) =>
        CanWrite(type, VaultCapabilities.None);

    public static void EnsureCanWrite(CipherType type, VaultCapabilities capabilities)
    {
        if (!CanWrite(type, capabilities))
            throw new UnsupportedCipherTypeWriteException(type);
    }

    public static void EnsureCanWriteWithoutFeatureNegotiation(CipherType type)
        => EnsureCanWrite(type, VaultCapabilities.None);

    public static void EnsureCanWrite(Cipher cipher, VaultCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(capabilities);
        EnsureCanWrite(cipher.Type, capabilities);

        var typedChildCount =
            (cipher.Login is null ? 0 : 1)
            + (cipher.SecureNote is null ? 0 : 1)
            + (cipher.Card is null ? 0 : 1)
            + (cipher.Identity is null ? 0 : 1)
            + (cipher.Ssh is null ? 0 : 1)
            + (cipher.BankAccount is null ? 0 : 1)
            + (cipher.DriversLicense is null ? 0 : 1)
            + (cipher.Passport is null ? 0 : 1);
        var hasMatchingChild = cipher.Type switch
        {
            CipherType.Login => cipher.Login is not null,
            CipherType.SecureNote => cipher.SecureNote is not null,
            CipherType.Card => cipher.Card is not null,
            CipherType.Identity => cipher.Identity is not null,
            CipherType.SshKey => cipher.Ssh is not null,
            CipherType.BankAccount => cipher.BankAccount is not null,
            CipherType.DriversLicense => cipher.DriversLicense is not null,
            CipherType.Passport => cipher.Passport is not null,
            _ => false,
        };

        if (!hasMatchingChild || typedChildCount != 1)
            throw new InvalidCipherWriteShapeException(cipher.Type);
    }

    public static void EnsureCanWriteWithoutFeatureNegotiation(Cipher cipher) =>
        EnsureCanWrite(cipher, VaultCapabilities.None);
}
