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

    [ObservableProperty]
    public partial string ReceivedLinkUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReceivedLinkPassword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReceivedText))]
    public partial string? ReceivedText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReceivedFile))]
    public partial string? ReceivedFileName { get; set; }

    [ObservableProperty]
    public partial bool ReceivedWrongPassword { get; set; }

    [ObservableProperty]
    public partial SendReceivedResult? LastReceived { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool HasItems => FilteredItems.Count > 0;
    public bool NoItems => !HasItems;
    public bool HasReceivedText => !string.IsNullOrEmpty(ReceivedText);
    public bool HasReceivedFile => !string.IsNullOrEmpty(ReceivedFileName);

    public SendViewModel(ISendUiService service, IClipboardService? clipboard = null)
    {
        _service = service;
        _clipboard = clipboard;
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        Error = null;
        try
        {
            var sends = await _service.GetSendsAsync(ct);
            Items.Clear();
            foreach (var send in sends) Items.Add(send);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void SelectFilterByTag(string? tag)
    {
        SelectedFilterTag = tag is "send:text" or "send:file" or "send:all" ? tag : "send:all";
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

    public async Task<bool> CreateSendAsync(SendEditorDraft draft, byte[]? fileBytes, CancellationToken ct = default)
    {
        if (!draft.HasRequiredData())
            return false;
        IsBusy = true;
        Error = null;
        try
        {
            var item = await _service.CreateSendAsync(draft, fileBytes, ct);
            Items.Add(item);
            ApplyFilter();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> UpdateSendFromDraftAsync(SendListItem item, SendEditorDraft draft, byte[]? fileBytes, CancellationToken ct = default)
    {
        if (!draft.HasRequiredData())
            return false;
        IsBusy = true;
        Error = null;
        try
        {
            var updated = await _service.UpdateSendAsync(item.Id, draft, fileBytes, ct);
            var existing = Items.FirstOrDefault(s => s.Id == item.Id);
            var index = existing is null ? -1 : Items.IndexOf(existing);
            if (index < 0)
                return false;
            Items[index] = updated;
            ApplyFilter();
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteSendAsync(SendListItem? item)
    {
        if (item is null)
            return;
        IsBusy = true;
        Error = null;
        try
        {
            await _service.DeleteSendAsync(item.Id);
            var existing = Items.FirstOrDefault(s => s.Id == item.Id);
            if (existing is not null) Items.Remove(existing);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void MarkMoreMenuOpened(SendListItem? item) => SelectedMenuItem = item;

    [RelayCommand]
    private void CopyLink(SendListItem? item)
    {
        if (item is null)
            return;
        var link = _service.CopyShareLink(item);
        if (!string.IsNullOrWhiteSpace(link))
            _clipboard?.SetSecretText(link);
    }

    [RelayCommand]
    private async Task OpenReceivedLinkAsync(CancellationToken ct = default)
    {
        ReceivedText = null;
        ReceivedFileName = null;
        ReceivedWrongPassword = false;
        LastReceived = null;
        Error = null;

        if (string.IsNullOrWhiteSpace(ReceivedLinkUrl))
            return;

        IsBusy = true;
        try
        {
            var password = string.IsNullOrEmpty(ReceivedLinkPassword) ? null : ReceivedLinkPassword;
            var result = await _service.OpenReceivedLinkAsync(ReceivedLinkUrl.Trim(), password, ct);
            LastReceived = result;
            if (result.WrongPassword)
            {
                ReceivedWrongPassword = true;
                return;
            }
            if (!result.Ok)
            {
                Error = result.Error ?? "无法打开该 Send 链接。";
                return;
            }
            if (result.Type == SendType.File)
                ReceivedFileName = result.FileName;
            else
                ReceivedText = result.TextContent;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Add()
    {
    }

    [RelayCommand]
    private void More(SendListItem? item) => MarkMoreMenuOpened(item);
}
