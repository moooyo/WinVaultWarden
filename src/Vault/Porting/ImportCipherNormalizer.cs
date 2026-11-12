using Core.Models;

namespace Vault.Porting;

/// <summary>
/// Converts exported entries into independent personal-vault creations. Exported identifiers,
/// permissions and key wrappers belong to the source vault and cannot be reused by an import.
/// Folder relationships are carried separately by the import request.
/// </summary>
internal static class ImportCipherNormalizer
{
    public static Cipher AsNewPersonalCipher(Cipher source) => new()
    {
        Type = source.Type,
        Favorite = source.Favorite,
        Reprompt = source.Reprompt,
        ArchivedDate = source.ArchivedDate,
        Name = source.Name,
        Notes = source.Notes,
        Login = source.Login,
        Card = source.Card,
        Identity = source.Identity,
        SecureNote = source.SecureNote,
        Ssh = source.Ssh,
        BankAccount = source.BankAccount,
        DriversLicense = source.DriversLicense,
        Passport = source.Passport,
        Fields = source.Fields,
        PasswordHistory = source.PasswordHistory,
    };
}
