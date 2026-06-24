namespace App.ViewModels.Models;

public sealed record GeneratorHistoryItem(string Value, DateTimeOffset CreatedAt)
{
    public string CreatedAtText => CreatedAt.ToLocalTime().ToString("yyyy年M月d日 HH:mm:ss");
}
