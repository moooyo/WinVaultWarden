using CommunityToolkit.Mvvm.ComponentModel;

namespace App.ViewModels.Models;

public sealed partial class SendEditorDraft : ObservableObject
{
    [ObservableProperty] private SendType _type;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _deletionDateLabel = "7 天";
    [ObservableProperty] private string _viewerLabel = "拥有链接的任何人";
    [ObservableProperty] private int? _maxAccessCount;
    [ObservableProperty] private bool _hideTextByDefault;
    [ObservableProperty] private bool _hideEmail;
    [ObservableProperty] private string _notes = string.Empty;

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
