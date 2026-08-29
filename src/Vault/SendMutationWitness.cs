using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Api.Dtos;

namespace Vault;

internal static class SendMutationWitness
{
    public static string Compute(SendResponseDto dto)
    {
        var canonical = new StringBuilder();
        Append(canonical, dto.Id);
        Append(canonical, dto.Type.ToString(CultureInfo.InvariantCulture));
        Append(canonical, dto.Name);
        Append(canonical, dto.Notes);
        Append(canonical, dto.Text?.Text);
        Append(canonical, dto.Text?.Hidden.ToString(CultureInfo.InvariantCulture));
        Append(canonical, dto.File?.Id);
        Append(canonical, dto.File?.FileName);
        Append(canonical, dto.File?.Size?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, dto.File?.SizeName);
        Append(canonical, dto.Key);
        Append(canonical, dto.MaxAccessCount?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, dto.Password);
        Append(canonical, dto.Emails);
        Append(canonical, dto.AuthType?.ToString(CultureInfo.InvariantCulture));
        Append(canonical, dto.Disabled.ToString(CultureInfo.InvariantCulture));
        Append(canonical, dto.HideEmail.ToString(CultureInfo.InvariantCulture));
        Append(canonical, dto.ExpirationDate?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Append(canonical, dto.DeletionDate.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

        var bytes = Encoding.UTF8.GetBytes(canonical.ToString());
        try
        {
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void Append(StringBuilder target, string? value)
    {
        if (value is null)
        {
            target.Append("-1:");
            return;
        }

        target.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }
}
