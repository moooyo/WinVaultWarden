using App.ViewModels.Models;

namespace App.Services;

public interface ISendUiService
{
    IReadOnlyList<SendListItem> GetSends();
    SendListItem CreateSend(SendEditorDraft draft);
    bool DeleteSend(string id);
    SendListItem? UpdateSend(string id, SendEditorDraft draft);
}

public sealed class MockSendUiService : ISendUiService
{
    private readonly List<SendListItem> _sends = new()
    {
        new("s1", "项目周报.pdf", SendType.File, "2026-07-01 截止", "https://vault.example/send/s1"),
        new("s2", "临时登录口令", SendType.Text, "2026-06-25 截止", "https://vault.example/send/s2"),
        new("s3", "设计稿打包.zip", SendType.File, "无截止日期", "https://vault.example/send/s3"),
    };

    public IReadOnlyList<SendListItem> GetSends() => _sends.ToList();

    public SendListItem CreateSend(SendEditorDraft draft)
    {
        var id = $"local-{_sends.Count + 1}";
        var name = draft.Type == SendType.File && !string.IsNullOrWhiteSpace(draft.FileName)
            ? draft.FileName
            : draft.Name;

        var item = new SendListItem(
            id,
            name,
            draft.Type,
            $"{draft.DeletionDateLabel} 后删除",
            $"https://vault.example/send/{id}");
        _sends.Add(item);
        return item;
    }

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
}
