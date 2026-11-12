namespace App.ViewModels.Models;

public sealed record GeneratorHistoryItem(string Value, DateTimeOffset CreatedAt, GeneratorMode Mode = GeneratorMode.Password)
{
    public string CreatedAtText => global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text22C375A2BBFAAA0E,ModeLabel,CreatedAt.ToLocalTime());
    public string ModeLabel => Mode switch
    {
        GeneratorMode.Passphrase => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextCB1B19977701FF9B),
        GeneratorMode.Username => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1A3F0617D6DE8E52),
        _ => global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextA621AB606DB2A11F),
    };
}
