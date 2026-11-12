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
            Normalize(cipher.Name, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD03438B702B564F3)),
            TypeLabelFor(cipher.Type),
            AccountSummaryFor(cipher));
    }

    private static string TypeLabelFor(CipherType type) => type switch
    {
        CipherType.Login => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1E2DF9C3075AE9E4),
        CipherType.SecureNote => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text3B0221743F79E8A4),
        CipherType.Card => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFB5640F8E12E3337),
        CipherType.Identity => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2A9C9E9976421A9D),
        CipherType.SshKey => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextBF18658907FB9C66),
        CipherType.BankAccount => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextC4C25EEB5DAF09EA),
        CipherType.DriversLicense => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text61B7153092BCDCCB),
        CipherType.Passport => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextD24F1B9E50C3D3C8),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextFCC0D5729CD499A1),
    };

    private static string AccountSummaryFor(Cipher cipher) => cipher.Type switch
    {
        CipherType.Login => Normalize(cipher.Login?.Username),
        CipherType.Card => Normalize(cipher.Card?.Brand),
        CipherType.SshKey => Normalize(cipher.Ssh?.Fingerprint),
        CipherType.BankAccount => Normalize(cipher.BankAccount?.BankName),
        CipherType.DriversLicense => Normalize(cipher.DriversLicense?.IssuingCountry),
        CipherType.Passport => Normalize(cipher.Passport?.IssuingCountry),
        _ => string.Empty,
    };

    private static string Normalize(string? value, string fallback = "") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
