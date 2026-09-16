namespace App.ViewModels.Models;

public sealed record GeneratorHistoryItem(string Value, DateTimeOffset CreatedAt, GeneratorMode Mode = GeneratorMode.Password)
{
    public string CreatedAtText => $"{ModeLabel} · {CreatedAt.ToLocalTime():M月d日 HH:mm:ss}";
    public string ModeLabel => Mode switch
    {
        GeneratorMode.Passphrase => "密码短语",
        GeneratorMode.Username => "用户名",
        _ => "密码",
    };
}
