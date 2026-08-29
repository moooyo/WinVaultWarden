using Core.Enums;
using Core.Models;

namespace App.ViewModels.Models;

/// <summary>
/// A deliberately narrow presentation model for a recovered vault entry.
/// Only allowlisted metadata is copied so the UI cannot accidentally stringify
/// the full decrypted cipher and expose passwords or other secret fields.
/// </summary>
public sealed class RecoveredCipherListItem
{
    private RecoveredCipherListItem(string name, string typeLabel, string accountSummary)
    {
        Name = name;
        TypeLabel = typeLabel;
        AccountSummary = accountSummary;
    }

    public string Name { get; }
    public string TypeLabel { get; }
    public string AccountSummary { get; }

    public string Subtitle => string.IsNullOrEmpty(AccountSummary)
        ? TypeLabel
        : $"{TypeLabel} · {AccountSummary}";

    public static RecoveredCipherListItem FromCipher(Cipher cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);

        return new RecoveredCipherListItem(
            Normalize(cipher.Name, "未命名条目"),
            TypeLabelFor(cipher.Type),
            AccountSummaryFor(cipher));
    }

    private static string TypeLabelFor(CipherType type) => type switch
    {
        CipherType.Login => "登录",
        CipherType.SecureNote => "安全笔记",
        CipherType.Card => "卡片",
        CipherType.Identity => "身份",
        CipherType.SshKey => "SSH 密钥",
        _ => "其他条目",
    };

    private static string AccountSummaryFor(Cipher cipher) => cipher.Type switch
    {
        CipherType.Login => Normalize(cipher.Login?.Username),
        CipherType.Card => Normalize(cipher.Card?.Brand),
        CipherType.SshKey => Normalize(cipher.Ssh?.Fingerprint),
        _ => string.Empty,
    };

    private static string Normalize(string? value, string fallback = "") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
