using System.Text;
using Core.Enums;
using Core.Models;

namespace Vault;

// 密码库搜索索引策略(域层)。BuildHaystack 决定条目的哪些字段进入可搜索文本——
// 这是安全相关规则:刻意排除密码/隐藏字段值/CVV,卡号仅取后四位。
// 语义须与历史行为逐字一致(自 App/Services/VaultUiService 迁入,行为不变)。
public static class VaultSearch
{
    public static string BuildHaystack(Cipher cipher)
    {
        var sb = new StringBuilder();
        var includeProtectedMetadata = cipher.CanViewPassword;
        void Add(string? s) { if (!string.IsNullOrWhiteSpace(s)) { sb.Append(s); sb.Append('\n'); } }

        Add(cipher.Name);
        if (includeProtectedMetadata)
            Add(cipher.Notes);

        if (cipher.Login is { } login)
        {
            Add(login.Username);
            foreach (var u in login.Uris) Add(u.Uri);
        }

        foreach (var f in cipher.Fields)
        {
            Add(f.Name);
            if (f.Type is CipherFieldType.Text or CipherFieldType.Boolean)
                Add(f.Value);
        }

        if (includeProtectedMetadata && cipher.Card?.Number is { Length: >= 4 } num) Add(num[^4..]);  // 仅后四位,不含 CVV

        if (cipher.Identity is { } id)
        {
            Add(id.FirstName); Add(id.MiddleName); Add(id.LastName);
            Add(id.Username); Add(id.Company);
            Add(id.Email); Add(id.Phone);
            Add(id.City);
        }

        if (cipher.BankAccount is { } bank)
        {
            Add(bank.BankName);
            Add(bank.NameOnAccount);
            Add(bank.AccountType);
            Add(bank.BankContactPhone);
            if (includeProtectedMetadata)
            {
                AddLastFour(bank.AccountNumber);
                AddLastFour(bank.Iban);
            }
        }

        if (cipher.DriversLicense is { } license)
        {
            Add(license.FirstName);
            Add(license.MiddleName);
            Add(license.LastName);
            Add(license.IssuingCountry);
            Add(license.IssuingState);
            Add(license.IssuingAuthority);
            Add(license.LicenseClass);
            if (includeProtectedMetadata)
                AddLastFour(license.LicenseNumber);
        }

        if (cipher.Passport is { } passport)
        {
            Add(passport.GivenName);
            Add(passport.Surname);
            Add(passport.Nationality);
            Add(passport.IssuingCountry);
            Add(passport.IssuingAuthority);
            Add(passport.PassportType);
            if (includeProtectedMetadata)
                AddLastFour(passport.PassportNumber);
        }

        return sb.ToString().ToLowerInvariant();

        void AddLastFour(string? value)
        {
            if (value is { Length: >= 4 })
                Add(value[^4..]);
        }
    }
}
