using App.ViewModels.Models;

namespace App.Services;

public interface ISendUiService
{
    Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default);
    Task<SendListItem> CreateSendAsync(SendEditorDraft draft, byte[]? fileBytes, CancellationToken ct = default);
    Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, byte[]? fileBytes, CancellationToken ct = default);
    Task DeleteSendAsync(string id, CancellationToken ct = default);
    string? CopyShareLink(SendListItem item);
    Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default);
}

public sealed record SendReceivedResult(
    bool Ok,
    bool WrongPassword,
    SendType Type,
    string Name,
    string? TextContent,
    string? FileName,
    string? Error,
    object? Accessed)
{
    public static SendReceivedResult Failure(string error) =>
        new(false, false, SendType.Text, "", null, null, error, null);
    public static SendReceivedResult Wrong() =>
        new(false, true, SendType.Text, "", null, null, null, null);
}
