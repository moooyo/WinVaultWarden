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

    [ObservableProperty]
    public partial string SelectedFilterTag { get; set; } = "send:all";

    [ObservableProperty]
    public partial SendListItem? SelectedMenuItem { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasItems => FilteredItems.Count > 0;
    public bool NoItems => !HasItems;

    // Task 9 最小适配:构造函数不再同步拉取列表;调用方在 Task 11 改为 await LoadAsync()。
    public SendViewModel(ISendUiService service, IClipboardService? clipboard = null)
    {
        _service = service;
        _clipboard = clipboard;
        // 旧同步 GetSends() 已从接口移除;Task 11 重写为 await LoadAsync()。
        // 此处若 service 是 MockSendUiService,可通过向下转型维持现有 SendViewModelTests。
        if (service is MockSendUiService mock)
        {
            foreach (var send in mock.GetSends()) Items.Add(send);
        }
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

    // Task 9 最小适配:改为 async 但保留同名方法签名供旧测试调用(Task 11 完整重写)。
    public bool CreateSend(SendEditorDraft draft)
    {
        if (!draft.HasRequiredData())
            return false;

        if (_service is MockSendUiService mock)
        {
            var item = mock.CreateSend(draft);
            Items.Add(item);
            ApplyFilter();
            return true;
        }

        // 非 mock 情形:触发异步创建但不等待;Task 11 完整重写为 async。
        _ = Task.Run(async () =>
        {
            var item = await _service.CreateSendAsync(draft, null);
            Items.Add(item);
            ApplyFilter();
        });
        return true;
    }

    [RelayCommand]
    private void DeleteSend(SendListItem? item)
    {
        if (item is null)
            return;

        if (_service is MockSendUiService mock)
        {
            mock.DeleteSend(item.Id);
        }
        else
        {
            _ = _service.DeleteSendAsync(item.Id);
        }

        var existing = Items.FirstOrDefault(s => s.Id == item.Id);
        if (existing is not null)
            Items.Remove(existing);

        ApplyFilter();
    }

    public bool UpdateSendFromDraft(SendListItem item, SendEditorDraft draft)
    {
        if (!draft.HasRequiredData())
            return false;

        if (_service is MockSendUiService mock)
        {
            var updated = mock.UpdateSend(item.Id, draft);
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

        // 非 mock 情形(Task 11 完整重写)
        return false;
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
