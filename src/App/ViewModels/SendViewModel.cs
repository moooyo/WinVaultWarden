using System.Collections.ObjectModel;
using App.Services;
using App.ViewModels.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace App.ViewModels;

public partial class SendViewModel : ObservableObject
{
    private readonly IClipboardService? _clipboard;

    public ObservableCollection<SendListItem> Items { get; } = new();
    public ObservableCollection<SendListItem> FilteredItems { get; } = new();

    [ObservableProperty] private string _selectedFilterTag = "send:all";

    public bool HasItems => FilteredItems.Count > 0;
    public bool NoItems => !HasItems;

    public SendViewModel(ISendUiService service, IClipboardService? clipboard = null)
    {
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
    }
}
