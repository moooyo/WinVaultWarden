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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);
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

    [RelayCommand]
    private void DeleteSend(SendListItem? item)
    {
        if (item is null)
            return;

        _service.DeleteSend(item.Id);

        var existing = Items.FirstOrDefault(s => s.Id == item.Id);
        if (existing is not null)
            Items.Remove(existing);

        ApplyFilter();
    }

    public bool UpdateSendFromDraft(SendListItem item, SendEditorDraft draft)
    {
        if (!draft.HasRequiredData())
            return false;

        var updated = _service.UpdateSend(item.Id, draft);
        if (updated is null)
            return false;

        var index = Items.IndexOf(item);
        if (index < 0)
        {
            var existing = Items.FirstOrDefault(s => s.Id == item.Id);
            index = existing is null ? -1 : Items.IndexOf(existing);
        }

        if (index < 0)
            return false;

        Items[index] = updated;
        ApplyFilter();
        return true;
    }

    public void MarkMoreMenuOpened(SendListItem? item) => SelectedMenuItem = item;

    [RelayCommand]
    private void CopyLink(SendListItem? item)
    {
        if (!string.IsNullOrWhiteSpace(item?.Link))
            _clipboard?.SetSecretText(item.Link);
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
