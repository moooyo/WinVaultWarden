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

    public bool IsText => Type == SendType.Text;
    public bool IsFile => Type == SendType.File;

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
