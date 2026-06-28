namespace App.ViewModels.Models;

public enum SendType { Text, File }

public sealed record SendListItem(
    string Id,
    string Name,
    SendType Type,
    string DeleteDate,
    string? Link)
{
    public string TypeLabel => Type == SendType.File ? "文件" : "文本";

    public string DeleteDateAccessibleLabel => $"删除日期 {DeleteDate}";

    public string Glyph => Type switch
    {
        SendType.Text => "\uE8D2",
        SendType.File => "\uE8A5",
        _ => "\uE724",
    };
}
