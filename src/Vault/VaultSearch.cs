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
        void Add(string? s) { if (!string.IsNullOrWhiteSpace(s)) { sb.Append(s); sb.Append('\n'); } }

        Add(cipher.Name);
        Add(cipher.Notes);

        if (cipher.Login is { } login)
        {
            Add(login.Username);
            foreach (var u in login.Uris) Add(u.Uri);
        }

        foreach (var f in cipher.Fields)
        {
            Add(f.Name);                                  // 字段名可搜(含隐藏字段的名)
            if (f.Type != CipherFieldType.Hidden) Add(f.Value);  // 仅非隐藏值
        }

        if (cipher.Card?.Number is { Length: >= 4 } num) Add(num[^4..]);  // 仅后四位,不含 CVV

        if (cipher.Identity is { } id)
        {
            Add(id.FirstName); Add(id.MiddleName); Add(id.LastName);
            Add(id.Username); Add(id.Company);
            Add(id.Email); Add(id.Phone);
            Add(id.City);
        }

        return sb.ToString().ToLowerInvariant();
    }
}
