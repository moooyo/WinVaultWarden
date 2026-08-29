using App.ViewModels.Models;

namespace App.Services;

public interface ISendUiService
{
    Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default);
    Task<SendListItem> CreateSendAsync(SendEditorDraft draft, Core.Models.SendFileSource? file, CancellationToken ct = default);
    Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, Core.Models.SendFileSource? file, CancellationToken ct = default);
    Task DeleteSendAsync(
        string id,
        Core.Session.VaultSessionLease originatingSession,
        CancellationToken ct = default);
    string? CopyShareLink(SendListItem item);
    Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default);

    // 编辑打开路径:按 id 拉取完整解密 Send 并水合成可编辑 draft(F1)。找不到返回 null。
    Task<SendEditorDraft?> GetEditDraftAsync(string id, CancellationToken ct = default);
}

public sealed record SendReceivedResult(
    bool Ok,
    bool WrongPassword,
    SendType Type,
    string Name,
    string? TextContent,
    string? FileName,
    string? Error,
    Core.Models.SendAccessResult? Accessed) : IDisposable
{
    private int _disposed;

    public static SendReceivedResult Failure(string error) =>
        new(false, false, SendType.Text, "", null, null, error, null);
    public static SendReceivedResult Wrong() =>
        new(false, true, SendType.Text, "", null, null, null, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Accessed?.Dispose();
    }
}
