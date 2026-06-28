using CommunityToolkit.Mvvm.ComponentModel;

namespace App.ViewModels.Models;

public sealed partial class SendEditorDraft : ObservableObject
{
    [ObservableProperty]
    public partial SendType Type { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeletionDateLabel { get; set; } = "7 天";

    [ObservableProperty]
    public partial string ViewerLabel { get; set; } = "拥有链接的任何人";

    [ObservableProperty]
    public partial int? MaxAccessCount { get; set; }

    [ObservableProperty]
    public partial bool HideTextByDefault { get; set; }

    [ObservableProperty]
    public partial bool HideEmail { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Disabled { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? ExpirationDate { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? DeletionDate { get; set; }

    public bool IsText => Type == SendType.Text;
    public bool IsFile => Type == SendType.File;

    // 选中文件的明文字节(由文件选择器读入)。非绑定属性,故用普通自动属性,避免进入 x:Bind。
    public byte[]? FileBytes { get; set; }

    // 相对删除标签 → 绝对删除时间(服务端要求 <=31 天)。"自定义" 用 DeletionDate(为空则回退 7 天)。
    public DateTimeOffset ToDeletionDate()
    {
        var now = DateTimeOffset.UtcNow;
        var days = DeletionDateLabel switch
        {
            "1 天" => 1,
            "7 天" => 7,
            "30 天" => 30,
            _ => -1,
        };
        if (days >= 0)
            return now.AddDays(days);

        var picked = DeletionDate ?? now.AddDays(7);
        var max = now.AddDays(31);
        return picked > max ? max : picked;
    }

    public static SendEditorDraft CreateDefault(SendType type) => new() { Type = type };

    public static SendEditorDraft FromExisting(SendListItem item) => new()
    {
        Type = item.Type,
        Name = item.Name,
        FileName = item.Type == SendType.File ? item.Name : string.Empty,
    };

    partial void OnTypeChanged(SendType value)
    {
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsFile));
    }

    public bool HasRequiredData()
    {
        if (string.IsNullOrWhiteSpace(Name))
            return false;

        return Type switch
        {
            SendType.Text => !string.IsNullOrWhiteSpace(Text),
            SendType.File => !string.IsNullOrWhiteSpace(FileName),
            _ => false,
        };
    }
}
