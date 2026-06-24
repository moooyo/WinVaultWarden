using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.ViewModels;

public partial class SendViewModel : ObservableObject
{
    private readonly ISendUiService _service;
    private readonly IClipboardService? _clipboard;

    public ObservableCollection<SendListItem> Items { get; } = new();
    public ObservableCollection<SendListItem> FilteredItems { get; } = new();

    [ObservableProperty] private string _selectedFilterTag = "send:all";
    [ObservableProperty] private SendListItem? _selectedMenuItem;

    public bool HasItems => FilteredItems.Count > 0;
    public bool NoItems => !HasItems;

    public SendViewModel(ISendUiService service, IClipboardService? clipboard = null)
    {
        _service = service;
        _clipboard = clipboard;
        foreach (var send in service.GetSends()) Items.Add(send);
        ApplyFilter();
    }

    public void SelectFilterByTag(string? tag)
    {
        SelectedFilterTag = tag is "send:text" or "send:file" or "send:all"
            ? tag
            : "send:all";
    }

    partial void OnSelectedFilterTagChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        FilteredItems.Clear();

        IEnumerable<SendListItem> source = SelectedFilterTag switch
        {
            "send:text" => Items.Where(i => i.Type == SendType.Text),
            "send:file" => Items.Where(i => i.Type == SendType.File),
            _ => Items,
        };

        foreach (var item in source) FilteredItems.Add(item);

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(NoItems));
    }

    public bool CreateSend(SendEditorDraft draft)
    {
        if (!draft.HasRequiredData())
            return false;

        var item = _service.CreateSend(draft);
        Items.Add(item);
        ApplyFilter();
        return true;
    }

    public void MarkMoreMenuOpened(SendListItem? item) => SelectedMenuItem = item;

    [RelayCommand]
    private void CopyLink(SendListItem? item)
    {
        if (!string.IsNullOrWhiteSpace(item?.Link))
            _clipboard?.SetText(item.Link);
    }

    [RelayCommand]
    private void Add()
    {
    }

    [RelayCommand]
    private void More(SendListItem? item)
    {
        MarkMoreMenuOpened(item);
    }
}
