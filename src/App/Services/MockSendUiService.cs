using App.ViewModels.Models;

namespace App.Services;

// 内存替身:设计期使用。不触网。Task 12 重写后测试改用 FakeSendUiService。
public sealed class MockSendUiService : ISendUiService
{
    private readonly List<SendListItem> _sends = new()
    {
        new("s1", "项目周报.pdf", SendType.File, "2026-07-01 截止", "https://vault.example/#/send/s1/seed1"),
        new("s2", "临时登录口令", SendType.Text, "2026-06-25 截止", "https://vault.example/#/send/s2/seed2"),
        new("s3", "设计稿打包.zip", SendType.File, "无截止日期", "https://vault.example/#/send/s3/seed3"),
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
            DeleteDate = $"{draft.DeletionDateLabel} 后删除",
        };
        _sends[index] = updated;
        return updated;
    }

    public SendListItem CreateSend(SendEditorDraft draft)
    {
        var id = $"local-{_sends.Count + 1}";
        var name = draft.Type == SendType.File && !string.IsNullOrWhiteSpace(draft.FileName) ? draft.FileName : draft.Name;
        var item = new SendListItem(id, name, draft.Type, $"{draft.DeletionDateLabel} 后删除", $"https://vault.example/#/send/{id}/seed");
        _sends.Add(item);
        return item;
    }

    // ISendUiService async 实现
    public Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SendListItem>>(_sends.ToList());

    public Task<SendListItem> CreateSendAsync(SendEditorDraft draft, byte[]? fileBytes, CancellationToken ct = default)
    {
        var item = CreateSend(draft);
        return Task.FromResult(item);
    }

    public Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, byte[]? fileBytes, CancellationToken ct = default)
    {
        var name = draft.Type == SendType.File && !string.IsNullOrWhiteSpace(draft.FileName) ? draft.FileName : draft.Name;
        var index = _sends.FindIndex(s => s.Id == id);
        if (index < 0)
        {
            var added = new SendListItem(id, name, draft.Type, $"{draft.DeletionDateLabel} 后删除", $"https://vault.example/#/send/{id}/seed");
            _sends.Add(added);
            return Task.FromResult(added);
        }
        var updated = _sends[index] with { Name = name, Type = draft.Type, DeleteDate = $"{draft.DeletionDateLabel} 后删除" };
        _sends[index] = updated;
        return Task.FromResult(updated);
    }

    public Task DeleteSendAsync(string id, CancellationToken ct = default)
    {
        DeleteSend(id);
        return Task.CompletedTask;
    }

    public string? CopyShareLink(SendListItem item) => item.Link;

    public Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default) =>
        Task.FromResult(new SendReceivedResult(true, false, SendType.Text, "示例 Send", "示例文本内容", null, null, null));
}
