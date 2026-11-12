using App.ViewModels.Models;

namespace App.Services;

// 内存替身:设计期使用。不触网。Task 12 重写后测试改用 FakeSendUiService。
public sealed class MockSendUiService : ISendUiService
{
    private readonly List<SendListItem> _sends = new()
    {
        new("s1", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB0C92A9C02A31F7A), SendType.File, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB9A975A69DF7CDDB), "https://vault.example/#/send/s1/seed1"),
        new("s2", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text1D2B98AE82F48A60), SendType.Text, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextB04320E9DE11FF28), "https://vault.example/#/send/s2/seed2"),
        new("s3", global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.TextF14B8A26CA33D02D), SendType.File, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text6A5E9A88D86579D0), "https://vault.example/#/send/s3/seed3"),
    };

    public IReadOnlyList<SendListItem> GetSends() => _sends.ToList();

    public bool DeleteSend(string id)
    {
        var index = _sends.FindIndex(s => s.Id == id);
        if (index < 0)
            return false;
        _sends.RemoveAt(index);
        return true;
    }

    public SendListItem? UpdateSend(string id, SendEditorDraft draft)
    {
        var index = _sends.FindIndex(s => s.Id == id);
        if (index < 0)
            return null;

        var name = draft.Type == SendType.File && !string.IsNullOrWhiteSpace(draft.FileName)
            ? draft.FileName
            : draft.Name;

        var updated = _sends[index] with
        {
            Name = name,
            Type = draft.Type,
            DeleteDate = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text96C980AD3CAD40A7,draft.DeletionDateLabel),
        };
        _sends[index] = updated;
        return updated;
    }

    public SendListItem CreateSend(SendEditorDraft draft)
    {
        var id = $"local-{_sends.Count + 1}";
        var name = draft.Type == SendType.File && !string.IsNullOrWhiteSpace(draft.FileName) ? draft.FileName : draft.Name;
        var item = new SendListItem(id, name, draft.Type, global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text96C980AD3CAD40A7,draft.DeletionDateLabel), $"https://vault.example/#/send/{id}/seed");
        _sends.Add(item);
        return item;
    }

    // ISendUiService async 实现
    public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SendListItem>>(_sends.ToList());

    public Task<SendListItem> CreateSendAsync(SendEditorDraft draft, Core.Models.SendFileSource? file, CancellationToken ct = default)
    {
        var item = CreateSend(draft);
        return Task.FromResult(item);
    }

    public Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, Core.Models.SendFileSource? file, CancellationToken ct = default)
    {
        var name = draft.Type == SendType.File && !string.IsNullOrWhiteSpace(draft.FileName) ? draft.FileName : draft.Name;
        var index = _sends.FindIndex(s => s.Id == id);
        if (index < 0)
        {
            var added = new SendListItem(id, name, draft.Type, global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text96C980AD3CAD40A7,draft.DeletionDateLabel), $"https://vault.example/#/send/{id}/seed");
            _sends.Add(added);
            return Task.FromResult(added);
        }
        var updated = _sends[index] with { Name = name, Type = draft.Type, DeleteDate = global::App.Localization.UiText.Format(global::App.Localization.UiStringKey.Text96C980AD3CAD40A7,draft.DeletionDateLabel) };
        _sends[index] = updated;
        return Task.FromResult(updated);
    }

    public Task DeleteSendAsync(
        string id,
        Core.Session.VaultSessionLease originatingSession,
        CancellationToken ct = default)
    {
        DeleteSend(id);
        return Task.CompletedTask;
    }

    // 编辑打开路径:内存替身按 id 找条目并水合成默认 draft;找不到返回 null。
    public Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default)
    {
        var send = _sends.FirstOrDefault(s => s.Id == id);
        if (send is null)
            return Task.FromResult<SendEditorDraft?>(null);
        var draft = SendEditorDraft.CreateDefault(send.Type);
        draft.Name = send.Name;
        return Task.FromResult<SendEditorDraft?>(draft);
    }

    public string? CopyShareLink(SendListItem item) => item.Link;

    public Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default) =>
        Task.FromResult(new SendReceivedResult(true, false, SendType.Text, global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text070EA3B3A9511416), global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.Text2AA6312EABA971C7), null, null, null));
}
