using App.ViewModels.Models;
using Core.Models;
using Core.Services;
using AppSendType = App.ViewModels.Models.SendType;
using CoreSendType = Core.Enums.SendType;

namespace App.Services;

// 真实 Send UI 服务:把 App 层 SendEditorDraft/SendListItem(SendType Text=0,File=1)
// 映射到 Core 领域(SendDraftModel + Core.Enums.SendType Text=1,File=2),并委派给
// ISendService / ISendWriteService / ISendAccessService。删除日期相对标签在此转绝对时间(<=31 天)。
public sealed class SendUiService : ISendUiService
{
    private readonly ISendService _read;
    private readonly ISendWriteService _write;
    private readonly ISendAccessService _access;
    private readonly string _serverUrl;

    public SendUiService(ISendService read, ISendWriteService write, ISendAccessService access, string serverUrl)
    {
        _read = read;
        _write = write;
        _access = access;
        _serverUrl = serverUrl;
    }

    public async Task<IReadOnlyList<SendListItem>> GetSendsAsync(CancellationToken ct = default)
    {
        var sends = await _read.GetSendsAsync(ct);
        return sends.Select(ToListItem).ToList();
    }

    public async Task<SendListItem> CreateSendAsync(SendEditorDraft draft, byte[]? fileBytes, CancellationToken ct = default)
    {
        var model = ToDraftModel(draft, id: null);
        if (model.Type == CoreSendType.File)
            await _write.SaveFileSendAsync(model, fileBytes ?? Array.Empty<byte>(), ct);
        else
            await _write.SaveTextSendAsync(model, ct);
        return await ReloadItemAsync(draft, ct);
    }

    public async Task<SendListItem> UpdateSendAsync(string id, SendEditorDraft draft, byte[]? fileBytes, CancellationToken ct = default)
    {
        var model = ToDraftModel(draft, id);
        if (model.Type == CoreSendType.File)
            await _write.SaveFileSendAsync(model, fileBytes ?? Array.Empty<byte>(), ct);
        else
            await _write.SaveTextSendAsync(model, ct);
        return await ReloadItemAsync(draft, ct, id);
    }

    public Task DeleteSendAsync(string id, CancellationToken ct = default) => _write.DeleteSendAsync(id, ct);

    public string? CopyShareLink(SendListItem item) => item.Link;

    public async Task<SendReceivedResult> OpenReceivedLinkAsync(string url, string? password, CancellationToken ct = default)
    {
        SendAccessResult accessed;
        try
        {
            accessed = await _access.AccessAsync(url, password, ct);
        }
        catch (UnauthorizedAccessException)
        {
            return SendReceivedResult.Wrong();
        }
        catch (Exception ex)
        {
            return SendReceivedResult.Failure(ex.Message);
        }

        return new SendReceivedResult(
            Ok: true,
            WrongPassword: false,
            Type: MapType(accessed.Type),
            Name: accessed.Name,
            TextContent: accessed.TextContent,
            FileName: accessed.FileName,
            Error: null,
            Accessed: accessed);
    }

    private async Task<SendListItem> ReloadItemAsync(SendEditorDraft draft, CancellationToken ct, string? preferId = null)
    {
        // 写后重新拉取以拿到服务端 accessId/share URL;按名称/id 命中刚写入的项。
        var items = await GetSendsAsync(ct);
        var name = draft.Type == AppSendType.File && !string.IsNullOrWhiteSpace(draft.FileName) ? draft.FileName : draft.Name;
        return (preferId is not null ? items.FirstOrDefault(i => i.Id == preferId) : null)
            ?? items.LastOrDefault(i => i.Name == name)
            ?? items.LastOrDefault()
            ?? new SendListItem(preferId ?? "", name, draft.Type, $"{draft.DeletionDateLabel} 后删除", null);
    }

    private SendListItem ToListItem(Send send)
    {
        var deletion = send.DeletionDate.LocalDateTime.ToString("yyyy/M/d HH:mm");
        var link = _serverUrl.TrimEnd('/') + "/#/send/" + send.AccessId;
        return new SendListItem(send.Id, send.Name, MapType(send.Type), $"{deletion} 截止", link);
    }

    private SendDraftModel ToDraftModel(SendEditorDraft draft, string? id) => new()
    {
        Id = id,
        Type = MapType(draft.Type),
        Name = draft.Name.Trim(),
        Notes = NullIfBlank(draft.Notes),
        TextContent = draft.Type == AppSendType.Text ? draft.Text : null,
        TextHidden = draft.HideTextByDefault,
        FileName = draft.Type == AppSendType.File ? NullIfBlank(draft.FileName) : null,
        MaxAccessCount = draft.MaxAccessCount,
        ExpirationDate = draft.ExpirationDate,
        DeletionDate = draft.ToDeletionDate(),
        Disabled = draft.Disabled,
        HideEmail = draft.HideEmail,
        Password = NullIfBlank(draft.Password),
    };

    private static AppSendType MapType(CoreSendType type) => type == CoreSendType.File ? AppSendType.File : AppSendType.Text;
    private static CoreSendType MapType(AppSendType type) => type == AppSendType.File ? CoreSendType.File : CoreSendType.Text;
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
